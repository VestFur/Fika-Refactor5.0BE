using System.Collections.Generic;
using Comfort.Common;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Backend;

public class LoadingScreenUI : MonoBehaviour
{
    public LoadingScreenUI(System.IntPtr pointer) : base(pointer) { }


    public LoadingScreenUI() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<LoadingScreenUI>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public static LoadingScreenUI Instance { get; internal set; }

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> LoadingPlayerPrefab;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Transform> Stack;

    private Dictionary<int, LoadingScreenPlayer> _loadingPlayers;

    /// <summary>
    /// 每个 net id 对应的昵称，从本界面创建过的每一行中学到。故意不
    /// 被 <see cref="ClearData"/> 清空：之后必须重新创建的行（一次重新武装，或某个
    /// 重新自我通告的对等端）必须带回玩家真实的昵称，而不是占位符。
    /// </summary>
    private readonly Dictionary<int, string> _knownNicknames = [];

    // ------------------------------------------------------------------ 对等端状态心跳
    //
    // 原因（analysis/loading-screen-peer-state，round10 2026-10-11）：ReInitAfterTransit 会丢掉*每一*行，
    // 包括仍然连接着的对等端的行，而一台机器只会通告自己的行恰好
    // 一次——在它自己重新武装时（ReInitAfterTransit -> players packet）以及连接时。一次中转的
    // 两台机器会在不同时刻重新武装（round10：客户端在 20:16:06，主机在 20:17:30），所以
    // 较晚的重新武装会抹掉较早那台已经通告过的行。从那一刻起，那个对等端的每个
    // LoadingScreenPacket 都到达了一个没有对应行的界面，并在 SetProgress 内部被丢弃，
    // 毫无痕迹：主机重新武装之后，它的日志里完全没有 "progress netId=2" 这一行，而
    // 客户端自己的行却一路爬到 100 %，客户端也一直显示着主机的行。
    //
    // 心跳在不新增通道的前提下关闭了这个窗口：该界面存在期间，它会重新发布
    // 本地行（players packet）并在既有数据包上重新发布本地进度，于是一个较晚重新武装的
    // 对等端会在一两秒内重新创建该行，之后便能再次收到当前值。它是有
    // 边界的（每秒一条进度行、每两秒一次行通告、最长 600 秒），并会在界面被隐藏、战局
    // 正在运行或网络管理器消失时自行停止——也就是恰好在
    // 本机的加载阶段结束时。

    /// <summary>界面存在期间，心跳重新发布本地进度的间隔。</summary>
    private const float HeartbeatProgressInterval = 1f;
    /// <summary>心跳重新发布本地行（players packet）的间隔。</summary>
    private const float HeartbeatRowInterval = 2f;
    /// <summary>心跳的硬边界：自它被武装之后，运行时间绝不会超过这个值。</summary>
    private const float HeartbeatMaxSeconds = 600f;

    private bool _heartbeatArmed;
    private float _heartbeatArmedAt;
    private float _lastHeartbeatProgressAt;
    private float _lastHeartbeatRowAt;
    private float _lastHeartbeatValue = -1f;
    private string _heartbeatSite = "none";

    protected void Awake()
    {
        _loadingPlayers = [];
    }

    public void ReInitAfterTransit()
    {
        FikaGlobals.LogInfo("Restarting LoadingScreenUI");
        var netId = FikaSingleton<IFikaNetworkManager>.Instance.NetId;
        var pmcName = FikaBackendUtils.IsHeadless ? "Headless" : FikaBackendUtils.PMCName;

        // （analysis/transit-reload-oom）本界面会在中转后继续存在，所以它的行也一样：刚被
        // 替换掉的那场战局的行被保留了下来（连同它们过期的进度）。它们会在
        // 这里被丢弃，并在对等端于切换后重新自我通告时被重新创建——这正是本方法末尾
        // 那行 [LoadSync] 所描述的内容。
        var replacedRows = _loadingPlayers.Count;
        ClearData();

        AddPlayer(netId, pmcName);

        var loadingPacket = new LoadingScreenPlayersPacket
        {
            NetIds = [netId],
            Nicknames = [pmcName]
        };

        FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref loadingPacket, DeliveryMethod.ReliableUnordered, true);

        // 判定行（analysis/loading-screen-sync）：一次中转之后，界面只会带着本地
        // 玩家重新武装，而对等端预期会重新自我通告。没有这一行，
        // 读者就无法判断对等端的行是熬过了世界切换，还是从未被重新创建。

        // （analysis/loading-screen-peer-state）另一台机器的重新武装在它自己的时刻发生，并
        // 会带走每一行，因此本机必须持续通告，直到加载阶段结束。
        ArmPeerStateHeartbeat("LoadingScreenUI.ReInitAfterTransit");
    }

    public LoadingScreenPlayersPacket GetPlayersPacket()
    {
        var count = _loadingPlayers.Count;
        var netIds = new int[count];
        var nicknames = new string[count];

        var index = 0;
        foreach ((var netId, var loadingPlayer) in _loadingPlayers)
        {
            netIds[index] = netId;
            nicknames[index] = loadingPlayer.Nickname.Value.text;
            index++;
        }

        return new LoadingScreenPlayersPacket
        {
            NetIds = netIds,
            Nicknames = nicknames
        };
    }

    public LoadingScreenPacket[] GetLatestStates()
    {
        var packets = new LoadingScreenPacket[_loadingPlayers.Count];

        var index = 0;
        foreach ((var netId, var loadingPlayer) in _loadingPlayers)
        {
            packets[index] = new LoadingScreenPacket
            {
                NetId = netId,
                Progress = loadingPlayer.Progress
            };
            index++;
        }

        return packets;
    }

    public void UpdateAndBroadcast(float progress)
    {
        var netId = FikaSingleton<IFikaNetworkManager>.Instance.NetId;
        var loadingPacket = new LoadingScreenPacket
        {
            NetId = netId,
            Progress = progress
        };
        FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref loadingPacket, DeliveryMethod.Unreliable, true);
        SetProgress(netId, progress);

        // 本地值就是心跳会持续重新发布的那一个，所以在这里记住它：心跳
        // 不得发送比引擎进度已经报告过的更旧的值。
        _lastHeartbeatValue = progress;
    }

    public void SetProgress(int netId, float progress)
    {
        SetProgress(netId, progress, "local");
    }

    /// <summary>
    /// 把一个进度值应用到某一行。
    /// </summary>
    /// <param name="netId">该行的 net id。</param>
    /// <param name="progress">以百分比表示的值（0-100），即进度包所携带的形式。</param>
    /// <param name="source">该值的来源（"local"、"peer#0"、"host"），用于诊断。</param>
    public void SetProgress(int netId, float progress, string source)
    {
        if (_loadingPlayers.TryGetValue(netId, out var player))
        {
            player.SetProgress(progress);
            ReportProgress(netId, progress, source);
            return;
        }

        // 绝不静默丢弃一个值（round10 的缺口）：能跑到这一行说明界面丢掉了一个它本应
        // 持有的行，而读者必须看清是哪一行、哪个值、来自谁。
        Diag("progress for unknown netId=" + netId + " from " + source + " (" + Fmt(progress)
            + "%) was dropped, this screen holds " + DescribeRows());
    }

    /// <summary>
    /// 应用另一台机器报告的进度；当本界面没有对应行时就创建它。
    ///
    /// 原因（analysis/loading-screen-peer-state）：一次中转之后，两个界面会在不同时刻重新
    /// 武装，因此对等端的进度包会在它的行已经消失时合法地到达。从数据包
    /// 重建该行，正是让对等端的加载状态重新可见的关键，而不是让每一个值都
    /// 丢在 <see cref="SetProgress"/> 的行查找里。
    /// </summary>
    /// <param name="netId">对等端的 net id，即数据包所携带的形式。</param>
    /// <param name="progress">所报告的百分比值。</param>
    /// <param name="source">谁发送的，用于诊断（"peer#0"、"host"）。</param>
    /// <returns>当必须为该数据包创建行时为 <see langword="true"/>。</returns>
    public bool ApplyPeerProgress(int netId, float progress, string source)
    {
        var created = EnsureRow(netId);
        if (created)
        {
            Diag("progress from " + source + " for netId=" + netId + ": this screen had no row for it yet, "
                + "the row was created again (" + DescribeRows() + ") => the peer was loading while this "
                + "machine's re-arm had already dropped its row");
        }

        SetProgress(netId, progress, source);
        return created;
    }

    /// <summary>
    /// 创建本界面尚不认识的一个 net id 的行，并为该 id 复用更早那一行的
    /// 昵称。此处创建了行时返回 true。
    /// </summary>
    /// <param name="nickname">
    /// 调用方为该 net id 所知道的昵称（对等端自己的通告）。当它为空或 null
    /// 时，会复用该 id 更早那一行的昵称。
    /// </param>
    public bool EnsureRow(int netId, string nickname = null)
    {
        if (_loadingPlayers.ContainsKey(netId))
        {
            return false;
        }

        if (string.IsNullOrEmpty(nickname) &&
            (!_knownNicknames.TryGetValue(netId, out nickname) || string.IsNullOrEmpty(nickname)))
        {
            nickname = "player " + netId;
        }

        AddPlayer(netId, nickname);
        return true;
    }

    // ------------------------------------------------------------------ 心跳实现

    /// <summary>
    /// 启动对本机行与进度的有界重新通告。在两种角色上都会于一次中转重新武装之后
    /// 调用；第二次调用只会把时间窗口往后推。
    /// </summary>
    public void ArmPeerStateHeartbeat(string site)
    {
        _heartbeatArmed = true;
        _heartbeatArmedAt = UnityEngine.Time.realtimeSinceStartup;
        _lastHeartbeatProgressAt = 0f;
        _lastHeartbeatRowAt = 0f;
        _heartbeatSite = site;
        Diag("heartbeat armed (site=" + site + "): this machine re-announces its row every "
            + HeartbeatRowInterval + "s and its progress every " + HeartbeatProgressInterval
            + "s while the loading screen is up (bound " + HeartbeatMaxSeconds + "s) => a peer that re-armed "
            + "later re-creates the row instead of losing every progress value");
    }

    private void StopHeartbeat(string reason)
    {
        _heartbeatArmed = false;
        Diag("heartbeat stopped (" + reason + ", site=" + _heartbeatSite + ", " + DescribeRows() + ")");
    }

    /// <summary>
    /// 心跳的逐帧泵。由网络管理器的 Update 驱动
    /// （FikaServer.Update / FikaClient.Update），因为引擎只会进入这个注入组件的
    /// Awake/OnDestroy——不能保证本类型上的 MonoBehaviour.Update 会被执行。
    /// </summary>
    public static void TickPeerStateHeartbeat()
    {
        var screen = Instance;
        if (screen == null)
        {
            return;
        }

        screen.TickHeartbeat();
    }

    private void TickHeartbeat()
    {
        if (!_heartbeatArmed)
        {
            return;
        }

        bool screenIsUp;
        try
        {
            screenIsUp = gameObject != null && gameObject.activeInHierarchy;
        }
        catch (System.Exception)
        {
            screenIsUp = false;
        }

        if (!screenIsUp)
        {
            StopHeartbeat("the loading screen is hidden, the loading phase of this machine is over");
            return;
        }

        var now = UnityEngine.Time.realtimeSinceStartup;
        if (now - _heartbeatArmedAt >= HeartbeatMaxSeconds)
        {
            StopHeartbeat("the bounded window of " + HeartbeatMaxSeconds + "s is over");
            return;
        }

        IFikaNetworkManager networkManager;
        try
        {
            networkManager = FikaSingleton<IFikaNetworkManager>.Instance;
        }
        catch (System.Exception)
        {
            networkManager = null;
        }

        if (networkManager == null)
        {
            StopHeartbeat("no network manager on this machine");
            return;
        }

        var netId = networkManager.NetId;
        var pmcName = FikaBackendUtils.IsHeadless ? "Headless" : FikaBackendUtils.PMCName;

        try
        {
            if (_lastHeartbeatRowAt <= 0f || now - _lastHeartbeatRowAt >= HeartbeatRowInterval)
            {
                _lastHeartbeatRowAt = now;
                AddPlayer(netId, pmcName);

                var rows = new LoadingScreenPlayersPacket
                {
                    NetIds = [netId],
                    Nicknames = [pmcName]
                };
                networkManager.SendData(ref rows, DeliveryMethod.ReliableUnordered, true);
                Diag("heartbeat row announced: netId=" + netId + " nickname='" + pmcName + "' ("
                    + DescribeRows() + ")");
            }

            if (_lastHeartbeatProgressAt > 0f && now - _lastHeartbeatProgressAt < HeartbeatProgressInterval)
            {
                return;
            }

            _lastHeartbeatProgressAt = now;

            var progress = 0f;
            if (_loadingPlayers.TryGetValue(netId, out var me) && me != null)
            {
                progress = me.Progress;
            }

            var state = new LoadingScreenPacket
            {
                NetId = netId,
                Progress = progress
            };
            networkManager.SendData(ref state, DeliveryMethod.Unreliable, true);

            if (System.Math.Abs(progress - _lastHeartbeatValue) >= 1f)
            {
                _lastHeartbeatValue = progress;
                Diag("heartbeat progress sent: netId=" + netId + " " + Fmt(progress) + "% ("
                    + DescribeRows() + ")");
            }
        }
        catch (System.Exception exception)
        {
            StopHeartbeat("sending failed (" + exception.GetType().Name + ": " + exception.Message + ")");
        }
    }

    /// <summary>
    /// 每行最后写入日志的进度值，使逐值追踪保持为每行每 10 个百分点
    /// 一行，而不是每个数据包一行。
    /// </summary>
    private readonly Dictionary<int, float> _reportedProgress = [];

    /// <summary>
    /// 当某行的进度跨过一个 10 个百分点台阶时，把该进度写入诊断。本地行
    /// （UpdateAndBroadcast）与对等端的行（SetProgress）都会用到它，正是它让
    /// "进度条不同步"在下次运行中成为一条可读的论断：这行会点明是哪一行、哪个
    /// 值，以及它是被本地应用还是从网络收到的。
    /// </summary>
    private void ReportProgress(int netId, float progress, string source)
    {
        if (_reportedProgress.TryGetValue(netId, out var previous) && (int)(previous / 10f) == (int)(progress / 10f))
        {
            return;
        }

        _reportedProgress[netId] = progress;
        var line = "progress netId=" + netId + " " + Fmt(progress)
            + "% (" + DescribeRows() + ") source=" + source;

        // （analysis/loading-screen-peer-state）游戏日志中的同一行，每 10 个百分点一步、每行
        // 一条，这样无需打开诊断文件就能比较两台机器。
        Diag(line);
    }

    /// <summary>
    /// 写入一行能穿过安静诊断模式的诊断行（本作用域在
    /// "LoadingScreen" 前缀允许列表内），并且在两份日志中都可被 grep 到。
    /// </summary>
    public static void Diag(string message)
    {
        try
        {
            FikaGlobals.LogInfo("[LoadScreenDiag] " + message, "LoadScreenDiag");
            
        }
        catch (System.Exception)
        {
        }
    }

    /// <summary>每个节流键的最后写入时间（见 <see cref="DiagThrottled"/>）。</summary>
    private static readonly Dictionary<string, float> _diagThrottle = [];

    /// <summary>
    /// 对一个键每个 <paramref name="seconds"/> 至多写入一行诊断。仅用于高
    /// 频路径：一个被拒绝或无法识别的数据包每秒可能到达多次，但
    /// 仍必须留下一条可读的行，同时不淹没游戏日志。
    /// </summary>
    public static void DiagThrottled(string key, float seconds, string message)
    {
        try
        {
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (_diagThrottle.TryGetValue(key, out var last) && now - last < seconds)
            {
                return;
            }

            _diagThrottle[key] = now;
        }
        catch (System.Exception)
        {
        }

        Diag(message);
    }

    /// <summary>出现在日志中的百分比值（"42.5"，而不是依赖区域设置的形式）。</summary>
    private static string Fmt(float value)
    {
        return value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 本界面所持有的行，汇成一行："1 row(s) [2(harbin)=92.5]"。每一行
    /// [LoadSync] 判定行都会用到它，这样测试者能看出界面认识哪些玩家、各自
    /// 进展到何处——本地驱动的行与从主机收到的行出现在同一个列表里。
    /// 从不抛出：没有执行 Awake 的界面（或已被销毁的界面）会被如实报告。
    /// </summary>
    /// <summary>
    /// 本机为自己发布的进度，形如 "local=92.5"（当界面没有为本地 net id
    /// 持有任何行时为 "local=absent"）。客户端侧的每条 [LoadSync] 行都携带它，因此
    /// 读者可以从一行中把本地进度与对等端的进度作比较。
    /// </summary>
    public string DescribeLocalRow()
    {
        var players = _loadingPlayers;
        if (players == null || players.Count == 0)
        {
            return "local=absent (no rows)";
        }

        int localNetId;
        try
        {
            localNetId = FikaSingleton<IFikaNetworkManager>.Instance.NetId;
        }
        catch (System.Exception)
        {
            return "local=absent (no net manager)";
        }

        if (!players.TryGetValue(localNetId, out var me) || me == null)
        {
            return "local=absent (no row for netId " + localNetId + ")";
        }

        return "local=" + Fmt(me.Progress);
    }

    public string DescribeRows()
    {
        var players = _loadingPlayers;
        if (players == null)
        {
            return "no rows yet (Awake has not run)";
        }

        if (players.Count == 0)
        {
            return "0 row(s)";
        }

        var builder = new System.Text.StringBuilder(players.Count * 24);
        builder.Append(players.Count).Append(" row(s) [");
        var first = true;
        foreach ((var netId, var player) in players)
        {
            if (!first)
            {
                builder.Append(", ");
            }

            first = false;
            builder.Append(netId).Append('(');
            try
            {
                builder.Append(player == null ? "?" : player.Nickname.Value.text);
            }
            catch (System.Exception)
            {
                builder.Append('?');
            }

            builder.Append(")=");
            builder.Append(player == null ? "?" : Fmt(player.Progress));
        }

        builder.Append(']');
        return builder.ToString();
    }

    public void AddPlayer(int netId, string nickname)
    {
        if (_loadingPlayers.ContainsKey(netId))
        {
            return;
        }

        // 在行存在之前就记住它，这样之后必须重新创建的行不会丢掉
        // 昵称（ClearData 不会触碰这个映射）。
        _knownNicknames[netId] = nickname;

        var go = GameObject.Instantiate(LoadingPlayerPrefab.Value, Stack.Value);
        var lsp = go.GetComponent<LoadingScreenPlayer>();
        lsp.SetNickname(nickname);
        _loadingPlayers.Add(netId, lsp);
        go.SetActive(true);

        // 每行一行日志：这是预置体的 LoadingScreenPlayer 行确实为该玩家
        // 创建过的唯一证据（缺失的行意味着某个对等端的进度永远无法显示）。
    }

    public void DeletePlayer(int netId)
    {
        if (_loadingPlayers.TryGetValue(netId, out var lsp))
        {
            _loadingPlayers.Remove(netId);
            GameObject.Destroy(lsp.gameObject);
        }
    }

    protected void OnDestroy()
    {
        _heartbeatArmed = false;
        ClearData();
    }

    public void ClearData()
    {
        foreach (var item in _loadingPlayers)
        {
            GameObject.Destroy(item.Value.gameObject);
        }

        _loadingPlayers.Clear();
    }
}
