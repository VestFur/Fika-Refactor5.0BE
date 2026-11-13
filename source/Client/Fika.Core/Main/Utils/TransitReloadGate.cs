using System;
using System.Threading.Tasks;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.Networking.Packets.Communication;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 一次 transit 重载的“两台机器同时开始加载”规则
/// （analysis/transit-reload-sync/REPORT.md）。
///
/// 实测到的问题（第 8 轮，主机 Fika-diagnostics.log 400404 ms 主机自身玩家撤离 ->
/// 412284 ms 世界消失并重新进入加载界面 -> 464548 ms LocalGameMatching ->
/// 609161 ms LocalGameCreate；客户端 Fika-diagnostics.log 528635 ms 重新进入 -> 543132 ms LocalGameMatching
/// -> 611823 ms LocalGameCreate）：一次 transit 之后，客户端在主机撤离后约一秒就结束了自己的战局，
/// 并在 14.5 秒后就开始下一场战局的流程，而主机需要 60.1 秒才走到
/// 那一步（其中 4 秒是 FikaHostTransitController.Transit 里刻意的停止延迟，7.9 秒在
/// CoopGame.Stop 内部，52.3 秒是直到下一次 LocalGameMatching 的原生应用层工作）。因此客户端在
/// 两台机器都还没加载一个字节时就已经领先约 45 秒——又因为主机自己的地图加载
/// 也更长（TarkovStreets 为 144.6 秒，客户端为 68.7 秒），客户端就坐在
/// WaitForServerToReceiveLocation 里约 130 秒，而它自己那一行加载界面早已到 100 %。
///
/// 本闸门对此的处理：客户端仍然在主机落地一到就结束自己的战局
/// （这样战局收尾记账、存档和撤离流程都留在原处），但它
/// 把自己的关卡创建挂起，直到主机宣布它即将开始自己的加载。那个
/// 宣布由 FikaHostGameWorld.InitLevelManaged 发出，也就是在
/// TarkovApplication.LocalGameCreate 之前运行的那一步原生流程，于是两台机器的原生 InitLevel 彼此相差
/// 不超过一次往返，而不是相差约 45 秒（“同一秒”）。谁到自己加载的路更短，
/// 谁就等——这里就是客户端——而主机从不等待对等端，所以慢的或不在的客户端无法
/// 拖延主机。
///
/// 每一次释放都是有界且被上报的，因此机器绝不会被一个已经消失的对等端挂住：
///   * 重载开始信号（正常路径，reason=host-reload-start），
///   * 到主机的传输不再处于 Connected（reason=transport-gone），
///   * 一份新的主机响应，表明主机已经创建了自己的战局，也就是主机在
///     本机还挂起时就完成了自己的加载——信号丢失，或者主机运行的构建
///     没有这个信号（reason=host-finished-load），
///   * 作为最后手段的 <see cref="ClientHoldSeconds"/>（reason=hold-ceiling）。
/// 默认值取自实测（analysis/transit-reload-sync2/REPORT.md）：上限
/// 从本机到达其关卡创建那一刻开始计算，而主机从
/// 客户端落地走到它自己开始加载，在迄今实测最差的一轮里花了 84.1 秒挂钟时间
/// （2026-10-11 第 10 轮：客户端 LogOutput.log:2610 park -> 客户端 LogOutput.log:3193 signal，主机侧
/// 主机 LogOutput.log:2693），也就是按客户端自己的时钟从布防点算起 102.4 秒（主机时钟上 103.4 秒；
/// 两个挂钟相差约 1.1 秒，由信号的发送与接收测得）。240 秒覆盖了它的 2.8 倍，这一点很重要，因为主机的原生部分在没有更换 mod 的情况下
/// 从 52.3 秒（第 8 轮）增长到 92.2 秒（第 10 轮）。从不发送该信号的主机
/// 仍会通过 host-finished-load 或 transport-gone 结束挂起，而挂起期间它不加载任何东西，
/// 所以在正常路径上这个宽松的上限不花任何代价。
///
/// 该信号作为无负载的 <see cref="TransitEventPacket.ETransitEventType.ReloadGo"/> 数据包传输。
/// 不认识该类型的接收方会读取那一个类型字节并忽略它（序列化器与
/// 读取方的 default 分支都能处理无负载的未知类型），因此构建版本混搭的
/// 一对机器也不会解析错自己的数据包流；没有该信号的主机则只通过上面的
/// 兜底来结束客户端的挂起。
/// </summary>
public static class TransitReloadGate
{
    /// <summary>
    /// 客户端侧：在主机信号缺席的情况下，一次 transit 重载的挂起最多可以持续多久，之后本机就自行加载
    /// 下一张地图。有配置项绑定到这个字段（与
    /// <see cref="LoadSyncGate"/> 相同的模式，FikaConfig 在启动时以及每次 SettingChanged 时都会绑定它），因此这个
    /// 数字在现场仍然可调（"Coop / Transit: Client Reload Hold"）。默认 240 秒。
    ///
    /// 上限从本机到达其关卡创建那一刻开始计算。第 10 轮里主机需要
    /// 84.1 秒挂钟时间才到达自己的加载开始（客户端 LogOutput.log:2610 park
    /// 20:16:06.297 -> 客户端 LogOutput.log:3193 signal 20:17:29.336；从布防点算起 102.4 秒），
    /// 而两轮之间增长的是它的原生部分（第 8 轮 52.3 秒 -> 第 10 轮 92.2 秒），
    /// 所以上限必须明显高于最差的实测值。原先的 60 秒在本机被释放时，
    /// 主机的信号还有 22.8 秒才到（客户端 LogOutput.log:2625 对 :3193）——这正是该
    /// 上限存在所为防止的错位。
    /// </summary>
    public static int ClientHoldSeconds = 240;

    /// <summary>
    /// 迄今实测到的、从本机布防点到主机自己开始重载的最差距离，单位
    /// 为秒。它会以 <c>hostWorstLead=</c> 打印在判定行里，这样读者无需打开报告就能判断
    /// 所配置的上限是否覆盖了眼下的情形——上限必须保持
    /// 高于它。第 10 轮（2026-10-11）：客户端自己的时钟上 102.4 秒（布防 uptime 479.6 秒 -> 信号
    /// uptime 582.0 秒；主机时钟上 103.4 秒——两个挂钟相差约 1.1 秒）；第 8 轮：约 64 秒。“信号到得太晚”那一行会打印它
    /// 实际看到的距离，因此在更新这个数字之前，新的最差值就已经可见。
    /// </summary>
    public static int HostLeadWorstSeconds = 105;

    /// <summary>
    /// 挂起的轮询间隔，单位为毫秒。该轮询与位置等待使用的是同一个 InformationPacket 往返：
    /// 它让本机的存活状态对主机可见，也让挂起能够读回
    /// 主机自己的进度。
    /// </summary>
    public static int ClientHoldPollMilliseconds = 500;

    /// <summary>本机已结束一次 transit 战局，且尚未开始下一张地图。</summary>
    private static bool _armed;

    /// <summary>是什么布防了挂起，用于判定行（“point 1 的一次 transit 落地”）。</summary>
    private static string _armedSource = "none";

    /// <summary>布防时刻的 realtimeSinceStartup，这样每一行挂起都能说出该状态的年龄。</summary>
    private static float _armedAt;

    /// <summary>主机宣布它自己的重载已经开始（以及何时）。</summary>
    private static bool _goReceived;
    private static float _goAt;

    /// <summary>
    /// 上一次挂起的结束。在它之后到达的信号就是一次 *迟到* 的信号——主机广播了，
    /// 但本机已不再等待——而这必须与“主机压根没有
    /// 广播”保持可区分，因为两者需要不同的修复（提高上限 vs. 升级主机）。
    /// </summary>
    private static bool _holdEnded;
    private static float _holdEndedAt;
    private static string _holdEndedReason = "none";
    private static float _holdEndedSinceArm;

    /// <summary>每种释放原因各被取用多少次，这样一行就能承载一次运行的全部历史。</summary>
    private static int _signalsReceived;
    private static int _parks;
    private static int _releasedBySignal;
    private static int _releasedByHostLoaded;
    private static int _releasedByCeiling;
    private static int _releasedByTransport;

    /// <summary>在本机挂起已经结束之后才到达的信号（主机广播得太晚）。</summary>
    private static int _lateSignals;

    /// <summary>到达时本机压根没有 transit 重载的信号（无布防、无挂起）。</summary>
    private static int _signalsWithoutContext;

    /// <summary>本进程实际见过的最差“布防点 到 信号”距离（0 = 尚未出现）。</summary>
    private static float _worstSinceArm;

    /// <summary>关于该闸门的一句紧凑描述，用于 [LoadSync] 头或诊断行。</summary>
    internal static string Describe()
    {
        return "transitReloadGate hold=" + ClientHoldSeconds + "s armed=" + _armed + " (source=" + _armedSource
            + ") hostSignal=" + _goReceived + " parks=" + _parks + " released=signal:" + _releasedBySignal
            + "/hostLoaded:" + _releasedByHostLoaded + "/ceiling:" + _releasedByCeiling
            + "/transport:" + _releasedByTransport + ", signalsReceived=" + _signalsReceived
            + " late:" + _lateSignals + " noContext:" + _signalsWithoutContext
            + ", sinceArmWorst=" + (_worstSinceArm > 0f ? _worstSinceArm.ToString("0.0") + "s" : "none")
            + ", hostWorstLead=" + HostLeadWorstSeconds + "s";
    }

    /// <summary>
    /// 布防挂起：由客户端在为自身玩家应用一次 transit 落地时调用，也就是当
    /// 本机知道自己处在一次 transit 重载中时。按 transit 幂等；同一 transit 的第二次
    /// 落地只会刷新来源文本。
    /// </summary>
    public static void ArmForTransitReload(string source)
    {
        _armed = true;
        _armedSource = source ?? "unspecified";
        _armedAt = Time.realtimeSinceStartup;
        // 一次新的 transit 拥有一个全新的信号：上一次重载的宣布不得在主机开始
        // 这一次之前就释放本次挂起。
        _goReceived = false;
        _goAt = 0f;
        // 一次新的 transit 也拥有一个全新的判定：上一次重载的挂起绝不能用来归类这一次的
        // 信号，也不能吞掉这一次的迟到信号行。
        _holdEnded = false;
        _holdEndedAt = 0f;
        _holdEndedReason = "none";
        _holdEndedSinceArm = 0f;
    }

    /// <summary>
    /// 丢弃挂起而不挂住：在战局启动失败时调用（<see cref="NetManagerUtils.AbortFailedJoin"/>），
    /// 也在每次挂起结束时调用，于是一个再也无法被释放的状态永远不会挂住之后的战局
    /// 启动。
    /// </summary>
    public static void Disarm(string reason)
    {
        if (!_armed && !_goReceived)
        {
            return;
        }

        _armed = false;
        _armedSource = "none";
        _goReceived = false;
    }

    /// <summary>
    /// 该信号的客户端一侧。在 <c>FikaClient</c> 的数据包回调中、transit
    /// 控制器查找之前处理：在挂起期间本机没有世界，因而压根没有 transit
    /// 控制器，而那正是该信号存在的窗口。
    /// </summary>
    /// <returns>当数据包就是这个信号时返回 true（调用方不得将其排队或转发）。</returns>
    public static bool HandlePacket(TransitEventPacket packet)
    {
        if (packet == null || packet.EventType != TransitEventPacket.ETransitEventType.ReloadGo)
        {
            return false;
        }

        var first = !_goReceived;
        _goReceived = true;
        _goAt = Time.realtimeSinceStartup;
        _signalsReceived++;
        if (first)
        {
            var sinceArm = _armedAt > 0f ? _goAt - _armedAt : -1f;

            if (_armed)
            {
                // 正常路径：该信号覆盖的是一次仍在运行的挂起。
                _worstSinceArm = Math.Max(_worstSinceArm, sinceArm);
            }
            else if (_holdEnded)
            {
                // 主机确实广播了——只是比本机挂起所覆盖的时间更晚。这与
                // “主机从未广播”是不同的缺陷，并且它指明了自己的修复方式。
                _lateSignals++;
                _worstSinceArm = Math.Max(_worstSinceArm, sinceArm);
                LoadSync.Warn("TransitReloadGate.HostReloadStart",
                    "transit reload signal arrived too late: the host started its own reload at " + WallClock() + " (uptime="
                    + Uptime() + "), but this machine was no longer waiting - its hold ended "
                    + (Time.realtimeSinceStartup - _holdEndedAt).ToString("0.0") + "s earlier (reason=" + _holdEndedReason
                    + ", the hold had covered sinceArm=" + _holdEndedSinceArm.ToString("0.0") + "s of the " + ClientHoldSeconds + "s hold when it ended; this"
                    + " signal arrived at sinceArm=" + sinceArm.ToString("0.0") + "s) => the two machines did not start loading together; raise"
                    + " 'Coop / Transit: Client Reload Hold' to at least " + SuggestedHoldSeconds(sinceArm) + "s (hostWorstLead=" + HostLeadWorstSeconds + "s, worst sinceArm seen in this run=" + _worstSinceArm.ToString("0.0") + "s)");
            }
            else
            {
                // 本机上压根没有 transit 重载：这个信号不由本机处置。
                _signalsWithoutContext++;
            }
        }

        return true;
    }

    /// <summary>
    /// 该信号的主机一侧：由主机下一场战局的原生关卡创建步骤调用
    /// （FikaHostGameWorld.InitLevelManaged），也就是从该主机真正开始加载地图的那一刻起。
    /// </summary>
    public static void AnnounceHostReloadStart(FikaServer server, string location)
    {
        var sent = false;
        var playerAmount = -1;

        try
        {
            if (server != null)
            {
                playerAmount = server.PlayerAmount;
                TransitEventPacket packet = new()
                {
                    EventType = TransitEventPacket.ETransitEventType.ReloadGo
                };
                server.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
                sent = true;
            }
        }
        catch (Exception exception)
        {
                    }

    }

    /// <summary>
    /// 挂起的客户端一侧：从 FikaClientGameWorld.InitLevel 中、紧接原生
    /// 关卡加载之前等待，因此这段等待只延迟加载下一张地图的那一步，别无其它。当本机不在
    /// transit 重载中时（每一次正常的战局启动）立即返回，且从不抛出异常。
    /// </summary>
    public static async Task WaitForHostReloadStartAsync(string site)
    {
        if (!_armed)
        {
            return;
        }

        var client = FikaSingleton<FikaClient>.Instance;
        if (client == null)
        {
            Disarm("no Fika client on this machine, the host cannot be heard (" + site + ")");
            LoadSync.Warn("TransitReloadGate.ClientHold",
                "transit reload hold skipped at " + site + ": this machine has no Fika client, so it cannot know "
                + "when the host starts its own reload; the next map is loaded without alignment");
            return;
        }

        var ceiling = Math.Max(5, ClientHoldSeconds);
        var pollMilliseconds = Math.Max(100, ClientHoldPollMilliseconds);
        var startedAt = Time.realtimeSinceStartup;
        var responsesAtPark = client.HostResponseCount;
        var stateAge = startedAt - _armedAt;
        _parks++;


        var reason = "hold-ceiling";
        var reportedAt = 0;

        while (true)
        {
            if (_goReceived)
            {
                reason = "host-reload-start";
                _releasedBySignal++;
                break;
            }

            if (client.ServerConnection == null || client.ServerConnection.ConnectionState != ConnectionState.Connected)
            {
                reason = "transport-gone";
                _releasedByTransport++;
                break;
            }

            // 只有 *新鲜的* 主机响应才算数：在新的重载窗口内本机轮询过
            // 主机之前，HostReceivedLocation 仍然保留着上一场战局的确认。
            if (client.HostResponseCount != responsesAtPark && client.HostReceivedLocation)
            {
                reason = "host-finished-load";
                _releasedByHostLoaded++;
                break;
            }

            var elapsed = Time.realtimeSinceStartup - startedAt;
            if (elapsed >= ceiling)
            {
                reason = "hold-ceiling";
                _releasedByCeiling++;
                
                break;
            }

            if (elapsed >= reportedAt)
            {
                reportedAt = (int)elapsed + 5;
                var sinceArmNow = stateAge + elapsed;
            }

            try
            {
                var information = new InformationPacket();
                client.SendData(ref information, DeliveryMethod.ReliableUnordered);
            }
            catch (Exception exception)
            {
                // 轮询只是一种便利（存活状态与主机自己的标志）；一次失败的轮询绝不能
                // 结束挂起，仍由上限和传输检查来决定。
            }

            await Task.Delay(pollMilliseconds);
        }

        var held = Time.realtimeSinceStartup - startedAt;
        var peerNotified = _goReceived;
        var sinceArm = _armedAt > 0f ? Time.realtimeSinceStartup - _armedAt : held;
        _armed = false;
        _armedSource = "none";
        _goReceived = false;
        // 用于归类迟到信号的记账（HandlePacket）。
        _holdEnded = true;
        _holdEndedAt = Time.realtimeSinceStartup;
        _holdEndedReason = reason;
        _holdEndedSinceArm = sinceArm;

        var line = "transit reload starting on this machine at " + WallClock() + " (uptime=" + Uptime()
            + ") role=client reason=" + reason + " site=" + site + " held=" + held.ToString("0.0") + "s sinceArm="
            + sinceArm.ToString("0.0") + "s peerNotified=" + peerNotified + " signals=" + _signalsReceived
            + " hostWorstLead=" + HostLeadWorstSeconds + "s"
            + " => the native level load of " + site + " starts here now";

        if (reason == "host-reload-start")
        {
            return;
        }

        LoadSync.Warn("TransitReloadGate.ClientHold", line
            + " (degraded: this machine starts its load without the host's reload-start signal. If the host broadcasts late, a"
            + " 'transit reload signal arrived too late' line follows this one - no such line in this run means the host did not"
            + " broadcast at all. See TransitReloadGate.Describe: " + Describe() + ")");
    }

    /// <summary>
    /// 迟到信号所要求的那个上限：本机实际等待过的距离，加上安全
    /// 余量，再向上取整到下一个半分钟。它会被打印出来，好让修复成为一个数字，而不是一次调查。
    /// </summary>
    private static int SuggestedHoldSeconds(float sinceArm)
    {
        var wanted = Math.Max(30f, sinceArm) + 15f;
        return (int)(Math.Ceiling(wanted / 30f) * 30f);
    }

    /// <summary>本地挂钟时间，这样两台机器的行可以直接跨两份日志相减。</summary>
    private static string WallClock()
    {
        return System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
    }

    /// <summary>进程运行秒数，即 Fika 诊断文件所用的那个时间戳。</summary>
    private static string Uptime()
    {
        return Time.realtimeSinceStartup.ToString("0.0") + "s";
    }
}
