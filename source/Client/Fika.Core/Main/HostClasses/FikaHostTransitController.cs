using Fika.Core.Main.BaseClasses;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using CommonAssets.Scripts.Game;
using JsonType;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.GlobalEvents;
using EFT.Interactive;
using EFT.UI;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Communication;

namespace Fika.Core.Main.HostClasses;

/// <summary>
/// 用户要求的开始规则，由可游玩主机（<see cref="FikaHostTransitController"/>）
/// 与独立主机（Fika.Headless.Classes.FikaHeadlessTransitController）共用：转场倒计时
/// 只有在<b>每一位</b>仍可转场的队友都完成了该
/// 点的交互<b>且</b>站在点内之后才开始。在此之前，被接受的交互仅被记录。
/// <para>
/// 这两个开关是 public 的，因此配置项可以直接绑定到它们，无需再改动此处：
/// <see cref="RequireAllTeammatesInteracted"/> 默认为 true（即所要求的行为，与该点
/// 自身的 <c>waitForAllParty</c> 思路一致），而 <see cref="CountdownFallbackSeconds"/> 仅用于
/// 自身 <c>TransitParameters.time</c> 为零的点。
/// </para>
/// <para>
/// 本文件所依赖的原生事实（证据：analysis/transit-countdown-transfer/REPORT.md 第 1 节）：
/// <c>TransitController.Timers(int, Dictionary&lt;int, ushort&gt;)</c> 与 <c>Sizes(int, Dictionary&lt;int, byte&gt;)</c>
/// 是原生控制器所发布两个全局事件的抽象那一半，两个字典都以
/// <b>raid id</b> 为键，而 <c>TransitParameters.time</c> 是该点的倒计时长度（秒）。
/// </para>
/// </summary>
public static class TransitStartGate
{
    /// <summary>严格门控（默认 true）：所有人都已交互，且所有人都在点内。</summary>
    public static bool RequireAllTeammatesInteracted = true;

    /// <summary>当某点自身的 <c>TransitParameters.time</c> 为零时使用的倒计时长度。</summary>
    public static int CountdownFallbackSeconds = 30;

    /// <summary>
    /// 供门控使用的精简“谁还未就绪”文本，倒计时可以开始时为 null。
    /// <paramref name="ready"/> 统计同时满足规则两部分的队友数量。
    /// </summary>
    public static string DescribeNotReady(List<Player> members, Dictionary<string, int> zoneByProfile,
        Dictionary<string, int> interactedByProfile, int pointId, out int ready)
    {
        ready = 0;
        if (members == null || members.Count == 0)
        {
            return "no eligible teammate";
        }

        var missingInteraction = new System.Text.StringBuilder();
        var missingZone = new System.Text.StringBuilder();
        foreach (var member in members)
        {
            if (member == null) continue;
            var profileId = member.ProfileId;
            var inZone = zoneByProfile != null && zoneByProfile.TryGetValue(profileId, out var zone) && zone == pointId;
            var interacted = interactedByProfile != null && interactedByProfile.TryGetValue(profileId, out var point) && point == pointId;
            if (inZone && interacted)
            {
                ready++;
                continue;
            }

            if (!interacted)
            {
                if (missingInteraction.Length > 0) missingInteraction.Append(',');
                missingInteraction.Append(profileId);
            }

            if (!inZone)
            {
                if (missingZone.Length > 0) missingZone.Append(',');
                missingZone.Append(profileId);
            }
        }

        if (missingInteraction.Length == 0 && missingZone.Length == 0) return null;
        return "notInteracted=[" + missingInteraction + "] notInZone=[" + missingZone + "]";
    }

    /// <summary>某点的倒计时长度：其自身时间，或在它未携带时使用回退值。</summary>
    public static int CountdownSeconds(TransitPoint point)
    {
        try
        {
            var seconds = point?.parameters?.time ?? 0;
            return seconds > 0 ? seconds : CountdownFallbackSeconds;
        }
        catch (System.Exception)
        {
            return CountdownFallbackSeconds;
        }
    }

    /// <summary>
    /// 构建某个点的计时器字典：原生条目被保留，而原生
    /// 字典未携带的每一位队友也会获得一个条目，以其 raid id 为键。
    /// <para>
    /// 为何存在：两个计时面板都由以 raid id 为键的字典驱动，而一次本地 raid 的
    /// 原生记账只描述本地组。因此在 Fika 主机上，客户端
    /// 在主机转发的字典中从未找到自己的 raid id —— 客户端显示了组内
    /// 人数（该字典以同样方式重建，见 <c>BuildZoneSizes</c>），但从未显示倒计时。
    /// </para>
    /// <paramref name="fikaSeconds"/> 是运行中的 Fika 倒计时，或 0 表示保留原生值（此时
    /// 对原生字典不认识的玩家使用最小的原生条目）。
    /// </summary>
    public static Il2CppSystem.Collections.Generic.Dictionary<int, ushort> BuildTimers(List<Player> members,
        int fikaSeconds, Il2CppSystem.Collections.Generic.Dictionary<int, ushort> nativeTimers)
    {
        var result = new Il2CppSystem.Collections.Generic.Dictionary<int, ushort>();
        var nativeCount = 0;
        if (nativeTimers != null)
        {
            foreach (var entry in nativeTimers)
            {
                if (result.ContainsKey(entry.Key)) continue;
                result.Add(entry.Key, entry.Value);
                nativeCount++;
            }
        }

        if (members == null || members.Count == 0) return result;

        var fallback = fikaSeconds;
        if (fallback <= 0 && nativeCount > 0)
        {
            fallback = ushort.MaxValue;
            foreach (var entry in result)
            {
                if (entry.Value < fallback) fallback = entry.Value;
            }
        }

        if (fallback < 0) fallback = 0;
        var value = (ushort)System.Math.Clamp(fallback, 0, ushort.MaxValue);
        foreach (var member in members)
        {
            if (member == null || result.ContainsKey(member.RaidId)) continue;
            result.Add(member.RaidId, value);
        }

        return result;
    }

    /// <summary>供诊断使用的 raid-id 到秒字典的精简视图。</summary>
    public static string DescribeTimers(Il2CppSystem.Collections.Generic.Dictionary<int, ushort> timers)
    {
        if (timers == null) return "null";
        var builder = new System.Text.StringBuilder();
        foreach (var entry in timers)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(entry.Key).Append('=').Append(entry.Value);
        }
        return builder.Length == 0 ? "empty" : builder.ToString();
    }

    /// <summary>
    /// 倒计时的通告值可以与正在运行的计时器界面显示的值相差多少秒，而
    /// 在重新启动该界面之前仍可接受。通告值是整秒
    /// （<c>Math.Ceiling</c>），而面板实时计数，因此最多一秒的差异是
    /// 有意为之的；该容差只是让确实落后（丢包）的界面能被重新通告。
    /// </summary>
    public const float PanelToleranceSeconds = 2f;

    /// <summary>
    /// 以 <paramref name="surfaceSeconds"/> 启动的计时器界面在
    /// <paramref name="startedAt"/> 时刻针对 <paramref name="pointId"/> 仍剩余的秒数；该点没有界面
    /// 运行时为 -1。<c>LocationTransitTimerPanel.Show(seconds)</c> 启动的倒计时会自行
    /// 逐秒递减（它启动的协程的 4.0 IL：每帧 <c>time -= Time.deltaTime</c>，
    /// 标签由它格式化，面板在归零时自行关闭），因此界面启动时的值就是它向下走的起点——
    /// 而再次启动会重放它。
    /// </summary>
    public static float SurfacedSecondsLeft(int pointId, int surfacePointId, float surfaceSeconds, float startedAt)
    {
        if (surfacePointId != pointId || surfaceSeconds < 0f) return -1f;
        return surfaceSeconds - (Time.unscaledTime - startedAt);
    }

    /// <summary>
    /// 两个计时器字典是否描述同一份通告。比较以 raid id 为键，
    /// 因此两个字典的枚举顺序可以自由不同：同一个值的两个来源
    /// 以不同顺序构建它——<see cref="BuildTimers"/> 在收到原生字典时采用原生顺序
    /// （原生 tick），否则采用 <c>CollectZonePlayers</c> 的成员顺序
    /// （Fika 自己的启动线路）——而第 8 轮在同一帧中正好展示了这一点：
    /// <c>LogOutput.log:2637 timers=1=20 2=20</c> 紧邻 <c>2639 timers=2=20 1=20</c>，即来自两种
    /// 顺序的同一个倒计时。若比较打印文本或枚举顺序，就会把这两者判为
    /// 不同，从而把同一个倒计时应用两次——这正是本轮消除的闪烁。
    /// </summary>
    public static bool SameTimers(Il2CppSystem.Collections.Generic.Dictionary<int, ushort> left,
        Il2CppSystem.Collections.Generic.Dictionary<int, ushort> right)
    {
        if (left == null || right == null) return false;
        if (left.Count != right.Count) return false;
        foreach (var entry in left)
        {
            if (!right.TryGetValue(entry.Key, out var value) || value != entry.Value) return false;
        }
        return true;
    }

    /// <summary>
    /// 对运行中游戏的真实 il2cpp 字典做一次性的 <see cref="SameTimers"/> 自检，
    /// 因为任何离线测试进程都无法构建这些类型。这四个用例正是倒计时
    /// 所需的：同一值的两种键顺序必须相等，而改变值、改变键以及不同的
    /// 大小则必须不相等。客户端在收到第一个组计时器数据包时调用一次；找到的细节
    /// 与结论一同记录，此处不得抛出异常。
    /// </summary>
    public static bool SelfCheckSameTimers(out string detail)
    {
        try
        {
            var ascending = new Il2CppSystem.Collections.Generic.Dictionary<int, ushort>();
            ascending[1] = 20;
            ascending[2] = 20;
            var descending = new Il2CppSystem.Collections.Generic.Dictionary<int, ushort>();
            descending[2] = 20;
            descending[1] = 20;
            var changedValue = new Il2CppSystem.Collections.Generic.Dictionary<int, ushort>();
            changedValue[1] = 20;
            changedValue[2] = 19;
            var changedKey = new Il2CppSystem.Collections.Generic.Dictionary<int, ushort>();
            changedKey[1] = 20;
            changedKey[3] = 20;

            var orderIndependent = SameTimers(ascending, descending);
            var valueDetected = !SameTimers(ascending, changedValue);
            var keyDetected = !SameTimers(ascending, changedKey);
            detail = $"keyOrder(free)={orderIndependent} valueChange(detected)={valueDetected} " +
                $"keyChange(detected)={keyDetected}";
            return orderIndependent && valueDetected && keyDetected;
        }
        catch (System.Exception exception)
        {
            detail = "the self-check itself failed (" + exception.GetType().Name + ": " + exception.Message + ")";
            return false;
        }
    }
}

/// <summary>
/// 运行中倒计时的某个已注册参与者得到的判定，见
/// <see cref="TransitCountdownGuard.Classify"/>。
/// </summary>
public enum ETransitMemberState
{
    /// <summary>已注册、存活且在点内：倒计时继续。</summary>
    Inside = 0,
    /// <summary>存活且在点内，但本机的区域报告没有这么说（报告丢失）。</summary>
    Missing = 1,
    /// <summary>存活、仍在此 raid 中，但在点外：这会取消转场。</summary>
    Left = 2,
    /// <summary>已死亡：从参与者列表移除，或在死亡开关开启时作为取消原因。</summary>
    Dead = 3,
    /// <summary>不再属于此 raid：已断线、已撤离或已在离开途中。</summary>
    Gone = 4
}

/// <summary>
/// 用户所要求转场规则的持续判定部分，由可游玩主机
/// （<see cref="FikaHostTransitController"/>）与独立主机
/// (Fika.Headless.Classes.FikaHeadlessTransitController).
/// <para>
/// 第 12 轮只评估规则一次：<c>CheckForPlayers</c> 加严格门控在按下的
/// 那一刻作出决定，整队被注册，随后倒计时运行且再也不看该点
/// （analysis/transit-countdown-transfer/REPORT.md 第 4 节）。用户的第一次报告——“如果客户端
/// 离开该点，两台机器都继续倒计时，结果谁也没被转场，所以必须重新进行交互”
/// ——正是缺失的那次重新检查。第三次报告——“一次断线会让该点
/// 永远返回 NonAllTeammates”——正是缺失的“谁还在这里”过滤器，见
/// <see cref="TransitDropouts"/>。
/// </para>
/// <para>
/// 五种判定中只有两种能终止转场，且两者都可开关控制：
/// <list type="number">
/// <item><b>仍在 raid 中但在点外</b>的已注册队友会取消整个
/// 转场（<see cref="AbortWhenSomeoneLeaves"/>，默认 true）。这也正是原生规则：
/// 该点自身的 <c>OnTriggerExit</c> 会移除玩家的组成员身份，并随之移除
/// 该组的倒计时；</item>
/// <item><b>已死亡</b>的已注册队友会从参与者列表移除，存活者
/// 继续（<see cref="AbortWhenSomeoneDies"/>，默认 false）——即原生机制所具有的形态，
/// 其中 <c>OnPlayerDead</c> 把他从组中移除，<c>SeparateGroupPlayers</c> 承载
/// 其余的人；</item>
/// <item><b>已离开此 raid</b>的队友（断线、撤离、已转场）会从
/// 参与者集合与两个门控中移除，绝不再阻塞任何人。这部分不可开关：它只是
/// 移除一个永远无法自行回来的阻塞者，而阻塞性的幽灵从不是预期的
/// 行为。</item>
/// </list>
/// </para>
/// <para>
/// 一个开关、一份回退，即 <see cref="TransitStartGate.RequireAllTeammatesInteracted"/> 的模式：
/// <see cref="AbortWhenSomeoneLeaves"/> 为 false（配置项 <c>Coop / Transit: Abort When Someone
/// Leaves</c>）会恢复第 12 轮的行为——倒计时运行到结束，而离开的玩家
/// 仍保持注册——而 <see cref="AbortWhenSomeoneDies"/> 为 true 则是“一次死亡终结
/// 所有人的转场”的选择加入变体。
/// </para>
/// </summary>
public static class TransitCountdownGuard
{
    /// <summary>
    /// 当已注册队友离开该点时取消正在运行的倒计时（默认 true，即
    /// 所要求的行为）。为 false 会重现第 12 轮的行为：倒计时继续运行
    /// 并最终不携带任何人。
    /// </summary>
    public static bool AbortWhenSomeoneLeaves = true;

    /// <summary>
    /// （analysis/transit-terminal-and-countdown）本轮用户规则：在倒计时运行时离开该点的人，
    /// 会隐藏<b>每一台</b>机器的倒计时并冻结<b>所有人</b>的转场，
    /// 而一旦每位已注册参与者重新站到
    /// 点内，倒计时便从冻结的那一秒继续。它为 true 时优先于 <see cref="AbortWhenSomeoneLeaves"/>：暂停的
    /// 倒计时可以恢复，被取消的则不能，因此该开关开启时取消开关不可达
    /// （这正是其用意）。
    /// <para>
    /// 这正是既有配置键 <c>Coop | Transit: Abort When Someone Leaves</c> 所绑定的开关
    /// （收敛轮次的选项 1：该键保留其名称与默认值，其<b>动作</b>变为
    /// 用户所要求的暂停/恢复）。绑定方式，供 FikaConfig.cs 的负责人：
    /// 在 SetupSetting 与 SettingChanged 中写 <c>TransitCountdownGuard.PauseWhenSomeoneLeaves = TransitAbortWhenSomeoneLeaves.Value;</c>，
    /// 并在其旁边写
    /// <c>TransitCountdownGuard.AbortWhenSomeoneLeaves = false;</c> —— 那样旧的取消就只存在于
    /// 代码中且只差一次赋值，这正是“该键不得再取消”所要求的。该键为 False
    /// 时恢复第 12 轮的行为：无人暂停，倒计时继续运行并最终不携带
    /// 走开的玩家。
    /// </para>
    /// </summary>
    public static bool PauseWhenSomeoneLeaves = true;

    /// <summary>
    /// 当已注册队友死亡时取消正在运行的倒计时（默认 false）：死亡不等于走开，
    /// 而且原生机制会把死者从组中移除并转场存活者。
    /// </summary>
    public static bool AbortWhenSomeoneDies = false;

    /// <summary>
    /// 某个已注册参与者的判定。<paramref name="inRaid"/> 与 <paramref name="gone"/> 描述
    /// 本机是否仍为他保留一个参与者；<paramref name="insidePoint"/> 来自
    /// 该点自身的碰撞体，是权威的那一半，因为它是缺失的区域
    /// 报告无法欺骗的那项检查；<paramref name="zoneEntryMatches"/> 是两个门控都读取的区域映射。
    /// </summary>
    public static ETransitMemberState Classify(bool inRaid, bool alive, bool gone, bool insidePoint, bool zoneEntryMatches)
    {
        if (!inRaid || gone) return ETransitMemberState.Gone;
        if (!alive) return ETransitMemberState.Dead;
        if (insidePoint) return zoneEntryMatches ? ETransitMemberState.Inside : ETransitMemberState.Missing;
        // 在该点的所有碰撞体之外。那些边界是触发器体积的超集——正是
        // 主机的区域退出确认所使用的检查——因此该判定是安全的方向。
        return ETransitMemberState.Left;
    }

    /// <summary>
    /// 下一轮 grep 的判据行：运行中倒计时每发生一次判定变化就有一行。其中的桶
    /// 是 profile id（会取消的还带位置细节），而判定是
    /// <c>continue</c> 或 <c>cancel(reason)</c>。
    /// </summary>
    public static string DescribeRecheck(int pointId, int members, List<string> inside, List<string> missing,
        List<string> left, List<string> dead, List<string> dropped, List<string> gone, string verdict)
    {
        return $"[TransitDiag] gate recheck point={pointId} members={members} inside={inside.Count} " +
            $"missing=[{Join(missing)}] left=[{Join(left)}] dead=[{Join(dead)}] " +
            $"dropped(disconnected)=[{Join(dropped)}] gone=[{Join(gone)}] -> {verdict}";
    }

    /// <summary>两个开关合成一个片段，使每一行都表明是哪条规则产生了它。</summary>
    public static string DescribeSwitches() =>
        $"abortWhenSomeoneLeaves={AbortWhenSomeoneLeaves} abortWhenSomeoneDies={AbortWhenSomeoneDies} " +
        $"pauseWhenSomeoneLeaves={PauseWhenSomeoneLeaves}";

    /// <summary>判据行以逗号连接的桶；空桶完全不打印任何内容。</summary>
    public static string Join(List<string> bucket)
    {
        if (bucket == null || bucket.Count == 0) return string.Empty;
        return string.Join(",", bucket);
    }
}

/// <summary>
/// 此 raid 中<b>对等端</b>（peer）已断线的玩家。
/// <para>
/// 为何存在：<c>FikaServer.OnPeerDisconnected</c> 清理会话记录（就绪标记、
/// 已分配的 profile、玩家数量）并发出 <c>PeerDisconnectedEvent</c>，但它不触碰
/// <c>CoopHandler.HumanPlayers</c>，也没有其他东西移除该角色——断线的玩家
/// 会一直留在该 raid 的玩家列表里，直到他的对象被销毁。两个转场门控都统计那个
/// 列表，这就是为何一次断线会让该点永远返回 <c>NonAllTeammates</c>：门控在等待
/// 一个已不在那里的玩家（用户需求 3）。
/// </para>
/// <para>
/// 该注册表由唯一一个点名对等端的事件喂入，以 profile 为键，并记住
/// 被丢弃的玩家对象的 net id，因此重连——Fika 会为同一个
/// profile 生成新的玩家对象——不会被误认为被丢弃的那个。来自某个 profile 的实时区域报告也会清除其条目，
/// 因为这样的数据包证明对等端已经回来了。
/// </para>
/// </summary>
public static class TransitDropouts
{
    /// <summary>Profile id -> 对等端已断线的玩家对象的 net id（无法读取时为 -1）。</summary>
    private static readonly Dictionary<string, int> Dropped = [];

    /// <summary>本进程的断线上报一旦就绪即为 true。</summary>
    private static bool _armed;

    /// <summary>
    /// 就绪化为此注册表提供输入的订阅。幂等，且为静态处理函数：因此一个
    /// 未经销毁就被替换的控制器，无法把第二个订阅泄漏到下一个 raid 中。
    /// </summary>
    public static void Arm()
    {
        if (_armed) return;
        _armed = true;
        try
        {
            Fika.Core.Modding.FikaEventDispatcher.SubscribeEvent<Fika.Core.Modding.Events.PeerDisconnectedEvent>(OnPeerDisconnected);
            
        }
        catch (System.Exception exception)
        {
                    }
    }

    private static void OnPeerDisconnected(Fika.Core.Modding.Events.PeerDisconnectedEvent @event)
    {
        try
        {
            Mark(@event?.Peer?.Player, "peer disconnected");
        }
        catch (System.Exception exception)
        {
                    }
    }

    /// <summary>记录已断线对等端的玩家，前提是该对等端有一位玩家。</summary>
    public static void Mark(Player player, string source)
    {
        if (player == null) return;
        var profileId = player.ProfileId;
        if (string.IsNullOrEmpty(profileId)) return;
        int netId;
        try { netId = player.TryCast<FikaPlayer>() is { } fikaPlayer ? fikaPlayer.NetId : -1; }
        catch (System.Exception) { netId = -1; }

        Dropped[profileId] = netId;
        
    }

    /// <summary>当此玩家对象属于一个已消失的对等端时为 true。</summary>
    public static bool IsDropped(Player player)
    {
        if (player == null) return false;
        string profileId;
        try { profileId = player.ProfileId; }
        catch (System.Exception) { return false; }
        if (string.IsNullOrEmpty(profileId) || !Dropped.TryGetValue(profileId, out var droppedNetId)) return false;
        try
        {
            // 同一 profile 的另一个对象意味着重连或重生成：他又回到此 raid 中了。
            if (droppedNetId < 0) return true;
            return player.TryCast<FikaPlayer>() is not { } fikaPlayer || droppedNetId == fikaPlayer.NetId;
        }
        catch (System.Exception)
        {
            return true;
        }
    }

    /// <summary>丢弃刚刚证明可再次到达的 profile 的条目（来自其对等端的数据包）。</summary>
    public static bool Clear(string profileId, string source)
    {
        if (string.IsNullOrEmpty(profileId)) return false;
        if (!Dropped.Remove(profileId)) return false;
        
        return true;
    }

    /// <summary>此 raid 当前丢弃的 profile 数量，供诊断行使用。</summary>
    public static int Count => Dropped.Count;

    /// <summary>供诊断使用的精简视图。</summary>
    public static string Describe()
    {
        if (Dropped.Count == 0) return "none";
        var builder = new System.Text.StringBuilder();
        foreach (var entry in Dropped)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(entry.Key).Append('/').Append(entry.Value);
        }

        return builder.ToString();
    }

    /// <summary>本 raid 结束；下一个 raid 以空列表开始。</summary>
    public static void Forget() => Dropped.Clear();

    /// <summary>
    /// 丢弃刚刚结束的世界的掉线记录，并返回它曾持有的 profile 数量。
    /// 这些条目是 profile id 与 net id，而非原生对象，因此此处无需触碰任何点——但
    /// 过去 raid 的 net id 不得排除下一个 raid 的 profile。幂等；事件订阅
    /// （<see cref="Arm"/>）保持就绪，因为它属于进程，而非属于某个世界。
    /// </summary>
    public static int ForceResetForWorldTeardown(string reason)
    {
        var held = Dropped.Count;
        Dropped.Clear();
        
        return held;
    }
}

/// <summary>
/// 用户针对转场点与撤离点位于同一位置时所要求的规则：一旦
/// 玩家交互激活了转场点，同一位置上的撤离点
/// 就被移出交互，直到转场完成、转场被取消或玩家
/// 离开该点。
/// <para>
/// 为何存在：同位的撤离点与转场点会在同一
/// 位置争夺同一次交互，而一个进入了玩家的撤离点会丢弃本主机所等待的转场注册
/// （analysis/transit-trigger-factory/REPORT.md 第 2 节）。此处没有任何东西绑定到地图、点名
/// 或数组索引：配对来自由运行中 raid 的游戏数据——把转场点的碰撞体
/// 体积与 raid 加载的每一个撤离点相比对
/// （<c>ExfiltrationController.ExfiltrationPoints</c>）——因此把两者放在同一位置的地图
/// 无需修改即可覆盖，而不把两者放在一起的地图绝不会被触碰。
/// </para>
/// <para>
/// 一个开关、一份回退：<see cref="SuppressCoLocatedExits"/> 为 false（配置项
/// <c>Coop / Transit: Suppress Co-Located Exfiltration</c>，默认 true）会把整条规则变成
/// 仅记录的空操作。它所做的每一次更改都是它所记住的点的原生
/// <c>ExfiltrationPoint.DisableInteraction()</c> / <c>EnableInteraction()</c> 成对调用，
/// 因此不写其他任何东西，且释放时绝不会启用它未曾禁用的点。
/// </para>
/// </summary>
public static class TransitCoLocatedExits
{
    /// <summary>
    /// 该规则的配置开关，默认 true（即所要求的行为）。它是 public 的，因此配置
    /// 项 <c>Coop / Transit: Suppress Co-Located Exfiltration</c> 可以直接绑定到它，无需再
    /// 改动此处，完全如同 <see cref="TransitStartGate.RequireAllTeammatesInteracted"/>。
    /// </summary>
    public static bool SuppressCoLocatedExits = true;

    /// <summary>
    /// 当两个触发器体积不重叠时，仍算作“同一位置”的距离（米）。
    /// 此构建的游戏数据把同位的两点完全叠在一起（0 米），因此
    /// 默认值是对并肩而立的一对点的容差，而不是搜索半径。
    /// </summary>
    public static float CoLocatedRadiusMeters = 6f;

    /// <summary>本规则禁用的点，连同它们是为哪个点而禁用，作为释放清单。</summary>
    private static readonly List<ExfiltrationPoint> Suppressed = [];

    /// <summary>
    /// 关于转场点自身位置上撤离点的一行文本，供已接受
    /// 交互的日志使用。只读，绝不抛出异常。
    /// </summary>
    public static string Describe(TransitPoint point)
    {
        try
        {
            var candidates = CollectCoLocated(point);
            if (candidates.Count == 0) return "none";
            var builder = new System.Text.StringBuilder();
            foreach (var candidate in candidates)
            {
                if (builder.Length > 0) builder.Append(", ");
                builder.Append(Name(candidate)).Append(" distance=")
                    .Append(UnityEngine.Vector3.Distance(point.transform.position, candidate.transform.position).ToString("F1"))
                    .Append("m suppressed=").Append(IsSuppressed(candidate));
            }
            return builder.ToString();
        }
        catch (System.Exception exception)
        {
            return "error:" + exception.GetType().Name;
        }
    }

    /// <summary>此 raid 当前移出交互的点的精简视图。</summary>
    public static string DescribeSuppressed()
    {
        try
        {
            if (Suppressed.Count == 0) return "none";
            var builder = new System.Text.StringBuilder();
            foreach (var point in Suppressed)
            {
                if (builder.Length > 0) builder.Append(", ");
                builder.Append(Name(point));
            }
            return builder.ToString();
        }
        catch (System.Exception exception)
        {
            return "error:" + exception.GetType().Name;
        }
    }

    /// <summary>
    /// 把转场点位置上的每一个撤离点移出交互。每次
    /// 激活调用一次，且在该点的玩家被注册之后；对本次 raid
    /// 已持有的点幂等。每种结果都是一行 <c>[TransitDiag] activate:</c>。
    /// </summary>
    public static string Suppress(TransitPoint point, string reason)
    {
        var pointId = PointId(point);
        if (point == null)
        {
            
            return "point-unavailable";
        }

        if (!SuppressCoLocatedExits)
        {
            var off = "the switch Coop / Transit: Suppress Co-Located Exfiltration is off; coLocatedExfil=" + Describe(point);
            
            return off;
        }

        try
        {
            var candidates = CollectCoLocated(point);
            var applied = 0;
            var already = 0;
            var failed = new System.Text.StringBuilder();
            foreach (var candidate in candidates)
            {
                if (candidate == null || IsSuppressed(candidate))
                {
                    already++;
                    continue;
                }

                try
                {
                    candidate.DisableInteraction();
                    Suppressed.Add(candidate);
                    applied++;
                }
                catch (System.Exception exception)
                {
                    if (failed.Length > 0) failed.Append(',');
                    failed.Append(Name(candidate)).Append(':').Append(exception.GetType().Name);
                }
            }

            var detail = $"coLocatedExfil=[{DescribeList(candidates)}] suppressed={applied} alreadySuppressed={already} " +
                $"failed=[{failed}] reason={reason}";
            
            return detail;
        }
        catch (System.Exception exception)
        {
            var detail = $"the rule itself failed ({exception.GetType().Name}: {exception.Message})";
            
            return detail;
        }
    }

    /// <summary>
    /// 把交互还给本规则禁用的每一个点：转场已完成、转场
    /// 已被取消，或玩家未完成转场就离开了该点。从解除转场禁令的同一批位置调用，
    /// 因此一次 raid 绝不会留下一个不再响应的撤离点。幂等，且
    /// 对未抑制任何点的 raid 也是安全的。
    /// </summary>
    public static string Release(string reason)
    {
        if (Suppressed.Count == 0) return "nothing suppressed";

        var released = 0;
        var destroyed = 0;
        var failed = new System.Text.StringBuilder();
        var names = new System.Text.StringBuilder();
        // 释放失败的点留在账本中，因此本次 raid 的下一次释放会再次尝试，
        // 而不是留下一个不再响应的撤离点。
        var retry = new List<ExfiltrationPoint>();
        foreach (var point in Suppressed.ToArray())
        {
            try
            {
                if (names.Length > 0) names.Append(", ");
                names.Append(Name(point));
                if (point == null)
                {
                    // 该 raid 的场景已随点一同消失；再没有什么需要启用的了。
                    destroyed++;
                }
                else
                {
                    point.EnableInteraction();
                    released++;
                }
            }
            catch (System.Exception exception)
            {
                retry.Add(point);
                if (failed.Length > 0) failed.Append(',');
                failed.Append(Name(point)).Append(':').Append(exception.GetType().Name);
            }
        }

        Suppressed.Clear();
        foreach (var point in retry) Suppressed.Add(point);

        var detail = $"released={released} destroyed={destroyed} failed=[{failed}] points=[{names}] reason={reason}";
        
        return detail;
    }

    /// <summary>
    /// 在不触碰场景的情况下关闭账本，供点已消失的 raid 的销毁路径使用。
    /// 下一个 raid 以空列表开始。
    /// </summary>
    public static void Forget() => Suppressed.Clear();

    /// <summary>
    /// 丢弃本账本记住的每一个撤离点，<b>且不触碰其中任何一个</b>，并返回它
    /// 曾持有的数量。这是 <see cref="Release"/> 的跨世界那一半。
    /// <para>
    /// 为何存在（第 13 轮；该发现来自本轮的世界拆除工作）：该列表持有它们被禁用时所在世界的
    /// 原生 <see cref="ExfiltrationPoint"/> 对象，而那个世界的拆除是
    /// 一个幂等序列（<c>WorldLifecycle.MarkWorldTornDown</c>，接在权威的
    /// <c>DestroyNetManager</c> 以及作为回退的 <c>CoopGame.Dispose/CleanUp</c> 上），它
    /// 会清理每一个跨 raid 持有原生对象的地方。本账本不在其中，因此一个在未单独结束 raid 的情况下被
    /// 替换掉的世界可能在其中留下死掉的撤离点——而读取或对其中某个执行
    /// <c>EnableInteraction()</c>，就是触碰一个已不存在的原生对象，
    /// 正是本轮两次 0xc0000005 崩溃所属的那类访问。
    /// </para>
    /// 这也是本方法<b>不进行任何</b>原生调用的原因：<see cref="Release"/> 在 raid 存活时
    /// 把交互还给撤离点，是正确的调用（取消、中止、最后一次禁令、
    /// 销毁），而正在关闭的世界再没有什么需要重新启用——下一个世界会构建
    /// 自己的点。幂等且不抛异常，账本为空时返回 0。两个主机控制器在
    /// 各自的 raid 开始时也会调用它，因此从未到达本类的拆除同样无法泄漏。
    /// </summary>
    public static int ForceResetForWorldTeardown(string reason)
    {
        var held = Suppressed.Count;
        Suppressed.Clear();
        
        return held;
    }

    /// <summary>
    /// 本 raid 中位于转场点自身位置上的每一个撤离点。判据：
    /// 两个触发器体积重叠且相距不超过两倍半径，或两个锚点相距在
    /// 半径之内。两个数值都由 <see cref="Describe"/> 记录，因此误判可以在
    /// 下一份日志中看到，而不必靠猜。
    /// </summary>
    private static List<ExfiltrationPoint> CollectCoLocated(TransitPoint point)
    {
        var result = new List<ExfiltrationPoint>();
        if (point == null) return result;

        var controller = ExfiltrationController.Instance;
        if (controller == null) return result;

        var transitOverlap = TryGetVolume(point.gameObject, out var transitBounds);
        var anchor = point.transform.position;
        Collect(controller.ExfiltrationPoints, point, anchor, transitOverlap, transitBounds, result);
        Collect(controller.ScavExfiltrationPoints, point, anchor, transitOverlap, transitBounds, result);
        return result;
    }

    /// <summary>
    /// 把该 raid 的两个撤离数组中某一个里、位于转场点
    /// 位置上的每个点都加入。这两个数组承载不同的具体类型（ExfiltrationPoint、ScavExfiltrationPoint），因此
    /// 一个泛型循环即可覆盖两者。
    /// </summary>
    private static void Collect<T>(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<T> points,
        TransitPoint point, UnityEngine.Vector3 anchor, bool transitOverlap, UnityEngine.Bounds transitBounds,
        List<ExfiltrationPoint> result) where T : ExfiltrationPoint
    {
        if (points == null) return;
        for (var i = 0; i < points.Count; i++)
        {
            var candidate = points[i];
            if (candidate == null) continue;
            if (IsCoLocated(point, candidate, anchor, transitOverlap, transitBounds)) result.Add(candidate);
        }
    }

    private static bool IsCoLocated(TransitPoint point, ExfiltrationPoint candidate, UnityEngine.Vector3 anchor,
        bool transitOverlap, UnityEngine.Bounds transitBounds)
    {
        try
        {
            var distance = UnityEngine.Vector3.Distance(anchor, candidate.transform.position);
            if (distance <= CoLocatedRadiusMeters) return true;
            if (!transitOverlap || distance > CoLocatedRadiusMeters * 2f) return false;
            return TryGetVolume(candidate.gameObject, out var exfilBounds) && transitBounds.Intersects(exfilBounds);
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 某个点的触发器体积：其下每个碰撞体的并集（包括未激活的），因此
    /// 触发器位于子物体上（或已被禁用）的点仍会报告自己的体积。当该对象
    /// 完全没有任何碰撞体时为 false，此时只有锚点距离起决定作用。
    /// </summary>
    private static bool TryGetVolume(UnityEngine.GameObject gameObject, out UnityEngine.Bounds bounds)
    {
        bounds = default;
        try
        {
            if (gameObject == null) return false;
            var colliders = gameObject.GetComponentsInChildren<UnityEngine.Collider>(true);
            if (colliders == null || colliders.Count == 0) return false;
            bounds = colliders[0].bounds;
            for (var i = 1; i < colliders.Count; i++)
            {
                if (colliders[i] != null) bounds.Encapsulate(colliders[i].bounds);
            }
            return true;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 本 raid 是否已把该点移出交互。按原生指针比较
    /// （<c>Il2CppObjectBase.Pointer</c>），即撤离管理器为其绑定所用的同一身份：
    /// 同一个原生点的两个包装器不一定是同一个托管实例。
    /// </summary>
    private static bool IsSuppressed(ExfiltrationPoint point)
    {
        if (point == null) return false;
        try
        {
            var pointer = point.Pointer;
            foreach (var entry in Suppressed)
            {
                if (entry != null && entry.Pointer == pointer) return true;
            }
        }
        catch (System.Exception)
        {
            return false;
        }

        return false;
    }

    private static string DescribeList(List<ExfiltrationPoint> points)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var point in points)
        {
            if (builder.Length > 0) builder.Append(", ");
            builder.Append(Name(point));
        }
        return builder.Length == 0 ? "none" : builder.ToString();
    }

    private static string Name(ExfiltrationPoint point)
    {
        try
        {
            if (point == null) return "<destroyed>";
            var name = point.Settings?.Name;
            return string.IsNullOrEmpty(name) ? "<unnamed>" : "'" + name + "'";
        }
        catch
        {
            return "<unreadable>";
        }
    }

    private static string PointId(TransitPoint point)
    {
        try
        {
            return point?.parameters == null ? "?" : point.parameters.id.ToString();
        }
        catch (System.Exception)
        {
            return "?";
        }
    }
}

/// <summary>
/// 转场点的目标地点，仅由该点自身的数据推导得出。
/// <para>
/// 此构建的每张地图都携带相同的结构——已对照
/// <c>SPT_Data/database/locations/*/base.json</c>: <c>factory4_night</c> (12 Woods, 13 bigmap,
/// 14 laboratory), <c>woods</c>, <c>bigmap</c>, <c>interchange</c>, <c>sandbox</c>,
/// <c>lighthouse</c>、<c>shoreline</c>、<c>rezervbase</c> 与 <c>tarkovstreets</c> 都把
/// <c>parameters.location</c> 填为加载界面所需的目标地点 id，把
/// <c>parameters.target</c> 填为访问条件所用的目标 MongoId。此处没有任何东西
/// 以地图名、点名或数组索引为键，因此后来新增的地图由其自身数据覆盖。
/// </para>
/// <para>
/// 该回退用于把 id 留空的数据集：此时它点名目标，而日志
/// 说明使用了哪个来源，因此下一轮可以区分地图数据问题与代码问题。
/// </para>
/// </summary>
public static class TransitDestination
{
    /// <summary>某点的目标地点 id，该点未携带时为空字符串。</summary>
    public static string Resolve(TransitPoint point, out string source)
    {
        source = "none";
        try
        {
            var location = point?.parameters == null ? null : point.parameters.location;
            if (!string.IsNullOrWhiteSpace(location))
            {
                source = "parameters.location";
                return location;
            }

            var target = point?.parameters == null ? null : point.parameters.target;
            if (!string.IsNullOrWhiteSpace(target))
            {
                source = "parameters.target (parameters.location was empty for this point)";
                return target;
            }
        }
        catch (System.Exception exception)
        {
            source = "error:" + exception.GetType().Name;
        }

        return string.Empty;
    }
}

public class FikaHostTransitController : FikaWorldController<LocalTransitController>
{
    public FikaHostTransitController(TransitGlobalSettings settings, Il2CppReferenceArray<LocationSettings.Location.TransitParameters> parameters, Profile profile, LocalRaidSettings localRaidSettings)
        : base(new LocalTransitController(settings, parameters, profile, localRaidSettings))
    {
        IsEvent = localRaidSettings.transitionType.HasFlagNoBox(ELocationTransition.Event);
        _localRaidSettings = localRaidSettings;
        var array = FikaTransitHistory.AppendLocation(localRaidSettings);
        Native.summonedTransits[profile.Id] = new(localRaidSettings.transition.transitionRaidId, localRaidSettings.transition.transitionCount,
            array, IsEvent);
        Native.TransferItemsController.InitItemControllerServer(FikaGlobals.TransitTraderId, FikaGlobals.TransitTraderName);
        _server = FikaSingleton<FikaServer>.Instance;
        _playersInTransitZone = [];
        _transittedPlayers = [];
    }

    /// <summary>
    /// 本 raid 的本地 raid 设置，保留它以便给远程玩家提供构造函数只会为主机自己
    /// 的 profile 创建的转场历史（见 <see cref="EnsureSummonedTransit"/>）。
    /// </summary>
    private readonly LocalRaidSettings _localRaidSettings;

    private readonly FikaServer _server;
    private readonly Dictionary<string, int> _playersInTransitZone;
    private readonly HashSet<int> _transittedPlayers;
    /// <summary>
    /// 已针对正在交互的那位玩家上报过“人还没到齐”答复的
    /// 点位 id，因此重试不会重复同一行。
    /// </summary>
    private readonly HashSet<string> _zoneCheckLogged = [];
    /// <summary>
    /// 已按玩家与点各解释过一次的区域退出报告。客户端几何回退重复发出的虚假“离开”
    /// 因此只有一行，而不是每次报告一行。
    /// </summary>
    private readonly HashSet<string> _zoneExitLogged = [];
    /// <summary>在本 raid 第一次 CheckForPlayers 调用（它会重置共享诊断集合）之前为 false。</summary>
    private bool _zoneCheckFirstCall;
    /// <summary>
    /// 本主机已接受的每位玩家转场交互的 profile id -> 点位 id。严格
    /// 开始门控读取它（见 <see cref="EveryoneInteracted"/>），它也是某位玩家在组尚未完整时
    /// 按下交互的唯一记录：在整组到达该点之前，他既不会被注册，也
    /// 不会为他运行倒计时。
    /// </summary>
    private readonly Dictionary<string, int> _interactedPlayers = [];
    /// <summary>每位玩家与每个点一行已接受交互的日志，因此重试不会重复它。</summary>
    private readonly HashSet<string> _interactionLogged = [];
    /// <summary>
    /// （2026-10-21）每个（玩家、点、答复、原因）一行拒绝日志。客户端不断重试的 InactivePoint，
    /// 正是该集合防止其刷爆日志的状态，而*不同*的门控仍会得到一行。
    /// </summary>
    private readonly HashSet<string> _answerLogged = [];
    /// <summary>本主机已推送过转场的玩家；防止两个驱动重复发送。</summary>
    private readonly HashSet<int> _transferIssued = [];
    /// <summary>
    /// 上一轮转场中已注册的玩家为何没得到他的转场，每位玩家一个条目。
    /// 倒计时结束报告会读取它，因此一行就能回答谁被落下以及为何。
    /// </summary>
    private readonly List<string> _transferFailures = [];
    /// <summary>Fika 倒计时当前运行所在的点，没有运行时为 -1。</summary>
    private int _countdownPointId = -1;
    /// <summary>运行中倒计时归零时的非缩放时间。</summary>
    private float _countdownEndsAt;
    /// <summary>运行中倒计时的长度（秒，即该点自身的时间）。</summary>
    private int _countdownSeconds;
    /// <summary>最后广播出去的那一秒，因此每秒一个数据包、一行日志。</summary>
    private int _lastBroadcastSecond = -1;
    /// <summary>
    /// 本 raid 最后一次原生 <see cref="Timers"/> 调用的点与计时器值，因此原生
    /// 组计时器 tick 按每个值转发一次，而不是每个事件一次。客户端的原生
    /// 应用器最终调用面板的 Display() 与 Show(seconds)，而 Show 会重启面板自己的
    /// 协程（<c>BattleUITimerPanel.StopActiveCoroutines</c> / <c>ShowCountdownPanel</c>）：因此每一次
    /// 冗余转发都会在客户端重放那段动画。
    /// </summary>
    private int _lastNativeTimerPointId = -1;
    private Il2CppSystem.Collections.Generic.Dictionary<int, ushort> _lastNativeTimers;
    /// <summary>
    /// 本主机自己的计时器界面：它为何点而启动、以多少秒启动以及
    /// 何时启动。<c>LocationTransitTimerPanel.Show(seconds)</c> 并不是一个值显示——它启动一个
    /// 自行逐秒递减的倒计时（<c>Co_Countdown</c>：每帧 <c>time -= Time.deltaTime</c>），
    /// 并且会再次启动面板的淡入（<c>Co_ShowInfoPanel</c> 先把画布 alpha 设为 0）。
    /// 第 9 轮已在客户端上证明了这一点（LogOutput.log:2922-2942：每个值一次启动，所报告的
    /// 每秒闪烁一次），而本主机自己的面板是同一对象、同样的协程，因此该
    /// 界面每个倒计时只启动一次，且只有当倒计时真正重启或
    /// 面板落后于通告值时才重新启动。
    /// </summary>
    private int _panelSurfacePointId = -1;
    private float _panelSurfaceSeconds = -1f;
    private float _panelSurfaceStartedAt;
    /// <summary>并非某个倒计时第一次启动的界面启动，每个各上报一次。</summary>
    private int _panelSurfaceRestarts;
    /// <summary>消失的界面最后一次被重新启动的时刻，因此最多每秒发生一次。</summary>
    private float _panelSurfaceReshowAt = -1f;
    /// <summary>未转发的冗余原生组计时器值，以及上报了多少次。</summary>
    private int _nativeTimerRepeatsSkipped;
    private int _nativeTimerRepeatsLogged;
    /// <summary>运行中倒计时的最后一行门控重检，因此每次判定变化只出现一行。</summary>
    private string _recheckLoggedSignature;
    /// <summary>运行中倒计时的门控重检次数，供取消那一行使用。</summary>
    private int _recheckCount;
    /// <summary>本 raid 失败的门控重检次数，最多上报几次。</summary>
    private int _recheckFailures;
    /// <summary>等待本主机自己玩家原生转场的宽限窗口到期的非缩放时间，或 -1。</summary>
    private float _transferGraceEndsAt = -1f;
    /// <summary>倒计时已结束、其客户端正在等待本主机自己玩家原生转场的点，或 -1。</summary>
    private int _pendingTransferPointId = -1;
    /// <summary>
    /// （analysis/transit-terminal-and-countdown）运行中倒计时的暂停状态：它为 true 时，
    /// 时钟不再前进，所有注册都被保留，两台机器都隐藏各自的界面。剩余
    /// 秒数冻结在 <c>_countdownPausedRemaining</c> 中，由恢复操作放回时钟。
    /// </summary>
    private bool _countdownPaused;
    private int _countdownPausedRemaining;
    private float _countdownPausedAt;
    private int _countdownPauses;
    /// <summary>上一次恢复尝试时仍在点外的参与者，由恢复操作点名。</summary>
    private string _countdownPausedOutside;
    /// <summary>队友最后一次被原生 CheckAccess 答复推促的时刻与次数，以玩家和点为键。</summary>
    private readonly Dictionary<string, float> _pressNudgedAt = [];
    private readonly Dictionary<string, int> _pressNudges = [];
    /// <summary>最后一次原生 Transit 调用的 hash、键与玩家数量，在本主机自行发起转场时复用。</summary>
    private string _lastTransitHash;
    private Il2CppSystem.Collections.Generic.Dictionary<string, ProfileKey> _lastTransitKeys;
    private int _lastTransitPlayersCount;

    public bool IsEvent { get; }

    public int AliveTransitPlayers
    {
        get
        {
            return _transittedPlayers.Count;
        }
    }

    internal void OnHostPlayerEnter(TransitPoint point, Player player)
    {
        
        if (!Native.TryGetAccessToLocation(player, point.parameters.id, out var _))
        {
            // 进入路径的提前退出：没有访问权限时，该玩家永远到不了
            // 启动倒计时的门控，因此它对每位玩家点名，而不只是对本地玩家点名。
            
            if (player.IsYourPlayer)
            {
                Native.AccessNotGrantedNotification();
            }
            return;
        }


        // 主机自己的玩家走原生触发器，因此这里是数据包路径为远程玩家所应用的
        // 同一份记账的本地那一半。
        TransitZonePresence.RecordInZone(_playersInTransitZone, player.ProfileId, point.parameters.id, "host player entered the zone");
        // （analysis/transit-terminal-20261013）原生控制器恰好在这一刻创建每玩家的
        // 转场容器（LocalGameTransitControllerClass.method_20 = OnPlayerEnter ->
        // TransferItemsController.InitPlayerStash）。在 Fika 主机上，该回调只会为
        // 本地玩家运行，因此这里为每一位到达该点的玩家应用同样的那一半。
        EnsureTransferStashFor(player, point, "entered the point (native trigger)");

        if (!Native.transitPlayers.ContainsKey(player.ProfileId))
        {
            if (player.TryCast<FikaPlayer>() is { } fikaPlayer)
            {
                fikaPlayer.UpdateBtrTraderServiceData()
                    .HandleExceptions();
            }

            if (player.IsYourPlayer)
            {
                Native.ShowInteraction(point.parameters.id, player, Native.GetSelectedTime());
                return;
            }

            TransitEventPacket packet = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Interaction,
                TransitEvent = new TransitInteractionEvent()
                {
                    PlayerRaidId = player.RaidId,
                    PointId = point.parameters.id,
                    Type = TransitInteractionEvent.EType.Show
                }
            };

            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            return;
        }
        Native.pointsById[point.parameters.id].GroupEnter(player);
    }

    /// <summary>
    /// 应用来自客户端的区域报告。远程玩家永远到不了
    /// <see cref="OnHostPlayerEnter"/>：原生触发器只驱动本地转场
    /// 控制器自己的回调，因此没有这个方法，主机对每位客户端的区域映射都会保持为空，
    /// 而 <see cref="CheckForPlayers"/> 会返回 NonAllTeammates。
    /// </summary>
    /// <param name="profileId">该数据包在套接字线程上被校验所依据的 profile。</param>
    /// <param name="pointId">客户端所上报的转场点。</param>
    /// <param name="kind">进入、退出，或交互请求的回退。</param>
    internal void OnRemotePlayerZonePresence(string profileId, int pointId,
        TransitZonePresencePacket.ETransitZonePresenceKind kind)
    {
        if (string.IsNullOrEmpty(profileId))
        {
            
            return;
        }
        if (kind == TransitZonePresencePacket.ETransitZonePresenceKind.Left)
        {
            // 本机的原生 PlayerExit 也会为已注册玩家调用
            // point.GroupExit，但远程玩家永远到不了该回调，因此这一
            // 侧只维护区域检查所读取的映射。转场注册
            // 本身保持不动：踏出去的玩家仍然拥有他的位置。
            // 该数据包不携带原因，因此删除条目之前会先向本机
            // 确认——见 OnRemotePlayerLeftZone。
            OnRemotePlayerLeftZone(profileId, pointId);
            return;
        }

        if (!Native.pointsById.TryGetValue(pointId, out _))
        {
            
            return;
        }

        var reason = kind == TransitZonePresencePacket.ETransitZonePresenceKind.In
            ? "client reported entering the zone" : "client interaction fallback";
        // 来自该对等端的实时数据包证明：一个已记录掉线者的 profile 又回到了此 raid
        // （重连），因此在其可能排除一位回来的玩家之前就清除该条目。
        TransitDropouts.Clear(profileId, "a zone report of this peer arrived");
        // 仅当该 profile 此刻尚不为人知时 RecordInZone 才返回 true，因此下面的
        // 提示每次进入只发生一次，重复的报告无法重发它。
        if (!TransitZonePresence.RecordInZone(_playersInTransitZone, profileId, pointId, reason)) return;
        SendInteractionShow(profileId, pointId, reason);
        // 本主机得知某位远程玩家站在某点的那一刻，正是原生
        // 控制器本会为他运行每玩家转场暂存那一半的时刻。内部已加保护：可用的
        // 容器绝不会被重建，因此这里不会清空玩家已经填好的容器。
        EnsureTransferStashFor(FindHumanPlayer(profileId), FindPoint(pointId), reason);
    }

    /// <summary>
    /// 向刚刚在某点注册的远程玩家提供转场提示。
    /// <para>
    /// 为何从这里发送：该提示是权威的——唯一的原生入口是
    /// <c>ClientTransitController.ShowInteraction</c>，而远程玩家永远到不了本机的
    /// 原生 <c>PlayerEnter</c>，因此主机必须推送 <c>TransitInteractionEvent{Show}</c>。
    /// <see cref="OnHostPlayerEnter"/> 的远程分支正是这样做的，但它只能通过
    /// 主机自己的原生 <c>PlayerEnter</c> 到达，因此对客户端的玩家它从未运行过：客户端看不到
    /// 提示，因此也从未发出交互请求。在场报告正是本机
    /// 得知某位远程玩家站在某点的时刻，因此从那里发送同一个数据包。
    /// 该数据包的构造与既有远程分支完全一致；其他都不变。
    /// </para>
    /// </summary>
    private void SendInteractionShow(string profileId, int pointId, string reason)
    {
        var player = FindHumanPlayer(profileId);
        if (player == null)
        {
            // 该数据包需要一个 raid id，而只有已解析的玩家才携带它。玩家对象尚未进入此 raid 的
            // 对等端会被大声上报，而不是被静默跳过。
            
            return;
        }
        if (player.IsYourPlayer) return;

        // 与既有远程分支相同的门控：已为某次转场注册的玩家
        // 保持他现有的交互状态。
        if (Native.transitPlayers.ContainsKey(profileId)) return;

        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.Interaction,
            TransitEvent = new TransitInteractionEvent()
            {
                PlayerRaidId = player.RaidId,
                PointId = pointId,
                Type = TransitInteractionEvent.EType.Show
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        
    }

    /// <summary>
    /// 某客户端报告其玩家离开了转场区域。该数据包不携带原因，而
    /// 客户端的各个发送者并不等价：真正踏出触发器、死亡、撤离以及
    /// 客户端 0.25 秒的几何回退，都以同一个“离开”到达。该回退在客户端上评估
    /// 触发器体积，因此当玩家仍站在点内时，一次错误的评估
    /// 曾会丢弃 CheckForPlayers 刚刚统计过的注册。因此这一侧
    /// 不会照单全收该报告：玩家必须以本机能够确认的原因
    /// 离开——已死亡、已撤离、已转场，或在该点自身触发器体积之外。
    /// 已确认的移除保持既有的措辞与日志格式。
    /// </summary>
    private void OnRemotePlayerLeftZone(string profileId, int reportedPointId)
    {
        if (!_playersInTransitZone.TryGetValue(profileId, out var registeredPoint))
        {
            
            return;
        }

        var player = FindHumanPlayer(profileId);
        var alive = player != null && player.HealthController?.IsAlive == true;
        var transited = player != null && _transittedPlayers.Contains(player.RaidId);
        var extracted = player != null && player.TryCast<FikaPlayer>() is { } fikaPlayer &&
            FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler.ExtractedPlayers.Contains(fikaPlayer.NetId);
        var inside = alive && !transited && !extracted && IsInsidePointBounds(registeredPoint, player);

        var verdict = player == null ? "player is no longer in this raid"
            : !alive ? "player is not alive"
            : transited ? "player already transferred"
            : extracted ? "player already extracted"
            : inside ? "still inside the point (spurious report)"
            : "outside the point";

        var detail = player == null ? "player=none" : DescribePlayerPlacement(registeredPoint, player);

        if (inside)
        {
            // 只上报第一次；客户端的回退可能重复同一判定。
            if (_zoneExitLogged.Add(profileId + "@" + registeredPoint))
            {
                
            }
            return;
        }

        
        TransitZonePresence.RemoveFromZone(_playersInTransitZone, profileId, "client left the zone");
    }

    private TransitPoint FindPoint(int pointId) =>
        Native.pointsById != null && Native.pointsById.TryGetValue(pointId, out var point) ? point : null;

    /// <summary>
    /// 该玩家是否仍站在点上，由该点拥有的每个碰撞体判定。若只测试
    /// 找到的第一个碰撞体，就会重现本检查正是要捕获的那次错误
    /// 评估：触发器体积可能位于该点的子物体上，而一个更小的同级碰撞体会
    /// 对一位正站在触发器里的玩家回答“在外面”。命中任何碰撞体都算数，
    /// 因此该测试是触发器体积的超集——这是否决的安全方向，因为它只可能
    /// 保留本主机已经统计过的注册。
    /// </summary>
    private bool IsInsidePointBounds(int pointId, Player player)
    {
        if (player == null) return false;
        var point = FindPoint(pointId);
        if (point == null) return false;

        var position = player.Position;
        var colliders = point.GetComponentsInChildren<UnityEngine.Collider>(true);
        if (colliders != null)
        {
            for (var i = 0; i < colliders.Count; i++)
            {
                var collider = colliders[i];
                if (collider != null && collider.bounds.Contains(position)) return true;
            }
        }

        return false;
    }

    /// <summary>玩家位置与点锚点，因此几何判定可以从日志重新推导。</summary>
    private string DescribePlayerPlacement(int pointId, Player player)
    {
        var position = player.Position;
        var builder = new System.Text.StringBuilder();
        builder.Append("pos=(").Append(position.x.ToString("F1")).Append(',')
            .Append(position.y.ToString("F1")).Append(',').Append(position.z.ToString("F1")).Append(')');

        var point = FindPoint(pointId);
        if (point == null)
        {
            builder.Append(" point=unavailable");
            return builder.ToString();
        }

        var anchor = point.transform.position;
        builder.Append(" point=(").Append(anchor.x.ToString("F1")).Append(',')
            .Append(anchor.y.ToString("F1")).Append(',').Append(anchor.z.ToString("F1")).Append(')')
            .Append(" distance=").Append(UnityEngine.Vector3.Distance(position, anchor).ToString("F1"))
            .Append(" colliders=").Append(point.GetComponentsInChildren<UnityEngine.Collider>(true)?.Count ?? 0);
        return builder.ToString();
    }

    /// <summary>主机自己控制器的原生转场死亡路径；已死亡的玩家不再被计入。</summary>
    internal void OnHostPlayerDead(Player player)
    {
        if (player == null) return;
        TransitZonePresence.RemoveFromZone(_playersInTransitZone, player.ProfileId, "host player died");
        DropDeadParticipant(player, "the host player died");
    }

    internal void OnHostPlayerExit(TransitPoint point, Player player)
    {
        // 这次离开是否终止整组的转场要到下面才决定，但该答案必须在
        // 此处移除注册之前读取。
        var registeredForThisPoint = false;
        try
        {
            // （2026-10-27 T2-B）注册表值的两种形态都被接受（见 IssueTransferForClients）：
            // 若只针对点位 id 读取，对本类写入的每一个注册它都为 false，因此
            // 下面“主机在其倒计时运行时离开了该点”这条规则永远无法触发。
            registeredForThisPoint = Native.transitPlayers != null &&
                Native.transitPlayers.TryGetValue(player.ProfileId, out var registeredPoint) &&
                (registeredPoint == point.parameters.id || registeredPoint == player.RaidId);
        }
        catch (System.Exception)
        {
        }

        if (_playersInTransitZone.TryGetValue(player.ProfileId, out var value))
        {
            if (value == point.parameters.id)
            {
                _playersInTransitZone.Remove(player.ProfileId);
            }
        }

        if (registeredForThisPoint && _countdownPointId == point.parameters.id &&
            TransitCountdownGuard.AbortWhenSomeoneLeaves)
        {
            // 主机玩家在其倒计时运行时离开了该点（用户需求 1）。这次离开的原生那一半——
            // 该玩家的 point.GroupExit、被取消的交互以及被解除的禁令——由取消操作
            // 自身应用，因此该路径在此结束。
            CancelCountdown("the host player left the point",
                " player=" + player.ProfileId + " " + DescribePlayerPlacement(point.parameters.id, player));
            return;
        }

        if (Native.transitPlayers.ContainsKey(player.ProfileId))
        {
            point.GroupExit(player);
            // 这次退出的原生侧：丢弃原生组会停止那个会把玩家带出本 raid 的
            // 倒计时。因此一个让触发器翻转的同位撤离会出现在
            // 这里，紧邻仍在运行的 Fika 倒计时。
            
        }
        if (player.IsYourPlayer)
        {
            Native.Cancel(player);
            // 原生交互刚刚被取消。当转场本身已经把玩家传送出触发器时，
            // 该路径也会运行，此时禁令必须保留：正是它
            // 阻止一个待处理的转场被变成普通撤离。只有未启动
            // 转场的离开才会撤销已接受的交互——否则该玩家在本 raid 中再也无法
            // 撤离。
            if (_transittedPlayers.Contains(player.RaidId))
            {
                
            }
            else
            {
                var wasRegistered = Native.transitPlayers.Remove(player.ProfileId);
                ReleaseTransitBanFor(player.RaidId, player.ProfileId,
                    $"host player left the point, interaction cancelled (transitRegistrationRemoved={wasRegistered})");
            }

            return;
        }

        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.Interaction,
            TransitEvent = new TransitInteractionEvent()
            {
                PlayerRaidId = player.RaidId,
                PointId = point.parameters.id,
                Type = TransitInteractionEvent.EType.Hide
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    public void Sizes(int pointId, Il2CppSystem.Collections.Generic.Dictionary<int, byte> sizes)
    {
        Native._currentTransitPointId = pointId;
#if DEBUG
        foreach (var item in sizes)
        {
            FikaGlobals.LogWarning($"int: {item.Key}, byte: {item.Value}");
        }
#endif

        // 原生字典是该点自己的记账（按该点的玩家组统计组大小）。
        // 在 Fika 主机上，远程玩家只能通过
        // InteractWithTransit 进入该记账，因此只要客户端自己的交互请求尚未被接受，
        // 面板就只显示主机一人，而 CheckForPlayers——它读取
        // _playersInTransitZone——已经把两人都算进去了。这些数字由同一张映射重建，
        // 使面板与门控描述同一个事实：当前谁站在该点内。此处不写入任何原生
        // 记账。
        var shown = BuildZoneSizes(pointId, sizes);
        

        foreach (var size in shown)
        {
            if (!FikaBackendUtils.IsHeadless && GamePlayerOwner.MyPlayer?.RaidId == size.Key && MonoBehaviourSingleton<GameUI>.Instance != null)
            {
                var groupSizePanel = MonoBehaviourSingleton<GameUI>.Instance.LocationTransitGroupSize;
                // Display() 让面板可见；Show() 填充它并启动其协程，而
                // 对象未激活时 Unity 会拒绝这样做。
                groupSizePanel.Display();
                if (FikaUiPanels.CanDrive(groupSizePanel, "FikaHostTransitController.GroupSize.Show"))
                {
                    groupSizePanel.Show(size.Value, Native.GetCurrentPointMaxGroupSize());
                }
            }
        }

        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.GroupSize,
            TransitEvent = new TransitGroupSizeEvent()
            {
                PointId = pointId,
                Sizes = shown
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// 本 raid 的原生计时器通知：原生控制器计算了该点的
    /// 倒计时，本机把它转发给各客户端。
    /// <para>
    /// 转发出去的字典中添加了两样东西。其一，原生字典未携带的每位玩家
    /// 都会获得一个以其 raid id 为键的条目，因为两个计时面板都按 raid id 查找本地玩家，
    /// 而一次本地 raid 的原生记账只描述本地组。没有这一点，
    /// 客户端就对自己没有条目，这正是第 7 轮在客户端显示组人数却
    /// 从不显示倒计时的原因（见 TransitStartGate.BuildTimers）。其二，当本主机运行自己的
    /// 倒计时时（严格门控已通过，见 <see cref="BeginCountdown"/>），该倒计时才是
    /// 权威，而这个原生值只会被记录。
    /// </para>
    /// </summary>
    public void Timers(int pointId, Il2CppSystem.Collections.Generic.Dictionary<int, ushort> timers)
    {
        try
        {
            Native._currentTransitPointId = pointId;
            if (_countdownPointId == pointId)
            {
                
                HostCountdownTick();
                return;
            }

            // 原生 tick 按每个值转发一次，而不是每个事件一次。被转发的值最终会
            // 进入客户端面板的 Show()，它会重启面板动画，因此重复转发
            // 就是客户端所报告的闪烁。重复的情形有其原因：本主机
            // 会把起始值通告两次，一次在自己的启动线路（BeginCountdown）上，一次在
            // 原生 tick 报告同一秒时（第 8 轮：LogOutput.log:2637 与 2639）。
            var unchanged = _lastNativeTimers != null && _lastNativeTimerPointId == pointId &&
                TransitStartGate.SameTimers(_lastNativeTimers, timers);
            if (unchanged)
            {
                _nativeTimerRepeatsSkipped++;
                if (_nativeTimerRepeatsLogged < 3)
                {
                    _nativeTimerRepeatsLogged++;
                    
                }

                HostCountdownTick();
                return;
            }

            _lastNativeTimerPointId = pointId;
            _lastNativeTimers = timers;
            // （analysis/transit-terminal-and-countdown）转发出去的字典由已注册
            // 参与者构建，而不是由区域映射构建：倒计时携带的每位玩家都需要在其中拥有自己的条目，
            // 否则他的机器根本不显示倒计时（见 CountdownParticipants）。
            var shown = TransitStartGate.BuildTimers(CountdownParticipants(pointId), 0, timers);
            

            foreach (var timer in shown)
            {
                if (!FikaBackendUtils.IsHeadless && GamePlayerOwner.MyPlayer?.RaidId == timer.Key && MonoBehaviourSingleton<GameUI>.Instance != null)
                {
                    var panel = DriveLocalTimerPanel(pointId, timer.Value);
                    
                }
            }

            TransitEventPacket packet = new()
            {
                EventType = TransitEventPacket.ETransitEventType.GroupTimer,
                TransitEvent = new TransitGroupTimerEvent()
                {
                    PointId = pointId,
                    Timers = shown
                }
            };

            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }
        catch (System.Exception exception)
        {
                    }

        // 原生 tick 是本主机倒计时两个时钟之一；轮询是另一个。
        HostCountdownTick();
    }

    public void InactivePointNotification(int playerId, int pointId)
    {
        if (!FikaBackendUtils.IsHeadless && GamePlayerOwner.MyPlayer?.RaidId == playerId)
        {
            NotificationManager.DisplayWarningNotification("Transit/InactivePoint".Localized(null), ENotificationDurationType.Default);
            Native.ShowPanel(pointId);
            return;
        }

        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.Interaction,
            TransitEvent = new TransitInteractionEvent()
            {
                PlayerRaidId = playerId,
                PointId = pointId,
                Type = TransitInteractionEvent.EType.InactivePoint
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>倒计时结束后，本主机自己玩家的原生转场仍可花费的秒数。</summary>
    private const float TransferGraceSeconds = 8f;

    /// <summary>
    /// 本机仍为门控与两个面板计入的队友：存活、尚未
    /// 转场、未撤离，且不是独立服务器上主机自己的玩家（该玩家没有
    /// 自己的转场条目）。正是 <see cref="CheckForPlayers"/> 两个循环所应用的谓词，
    /// 也是 <see cref="CollectZonePlayers"/> 所使用的那个，只读取一次，使门控、面板与日志一致。
    /// </summary>
    private List<Player> EligibleMembers()
    {
        var result = new List<Player>();
        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        var players = coopHandler?.HumanPlayers;
        if (players == null) return result;

        foreach (var member in players)
        {
            if (member == null || member.HealthController?.IsAlive != true) continue;
            if (TransitDropouts.IsDropped(member)) continue;
            if (_transittedPlayers.Contains(member.RaidId)) continue;
            if (coopHandler.ExtractedPlayers.Contains(member.NetId)) continue;
            if (member.IsYourPlayer && FikaBackendUtils.IsHeadless) continue;
            result.Add(member);
        }

        return result;
    }

    /// <summary>
    /// 记录一次已接受的交互。这正是严格门控得以实现的原因：组尚未完整的
    /// 玩家的按下会被记住而不是注册他，因此在整个组到达该点之前，
    /// 不会有任何原生倒计时启动。
    /// </summary>
    private void RecordInteraction(Player player, int pointId)
    {
        _interactedPlayers[player.ProfileId] = pointId;
        if (!_interactionLogged.Add(player.ProfileId + "@" + pointId)) return;
        
    }

    /// <summary>
    /// 严格开始门控：当每位合格队友都完成了该点的交互
    /// 且站在点内时为 true。为 false 的答复只会通知等待中的玩家，绝不注册任何人。
    /// </summary>
    private bool EveryoneInteracted(Player player, int pointId)
    {
        if (!TransitStartGate.RequireAllTeammatesInteracted) return true;

        // (2026-10-27 W37) 尊重点位自己的 waitForAllParty。证据：
        //  · 海岸线 transits[25]（终点站/码头那条）是 waitForAllParty=false，即原生语义"一个人按就能走"；
        //    同图的 24/43/51 都是 true。本类此前一律要求"每个队友都按过"，等于把 25 号点也按全队门处理，
        //    于是客机先按、主机没按时客机被一直拒绝（用户报的"客机先按就转移不了"）。
        //  · 官方 Fika 2.4.3（mods/fika-47242/.../FikaHostClasses/FikaHostTransitController.cs:228）里没有
        //    "都要按过"这道门，只有 CheckForPlayers（所有活着的队友都在点内）；"都要按过"是后来加的。
        // 所以：点位自己说不用等全队时，就按原生语义放行；说 true 的点位保持现有的严格门。
        var pointForGate = FindPoint(pointId);
        if (pointForGate?.parameters != null && !pointForGate.parameters.waitForAllParty)
        {
            FikaGlobals.LogInfo($"[TransitDiag] start-gate-skip point={pointId} pressedBy={player.ProfileId} " +
                "reason=waitForAllParty-false (the point itself allows a single press)", "FikaHostTransitController");
            return true;
        }

        var members = EligibleMembers();
        var missing = TransitStartGate.DescribeNotReady(members, _playersInTransitZone, _interactedPlayers, pointId, out var ready);
        if (missing == null)
        {
            return true;
        }

        NotifyTeammatesNotReady(player);
        // （2026-10-26 W29）就绪/缺失的细节被算出后又被丢弃，因此一次拒绝什么也没说，
        // 没说明谁还缺失——而这正是“客户端先按”报告所需的那唯一信息。每次拒绝一行。
        FikaGlobals.LogInfo($"[TransitDiag] start-gate point={pointId} pressedBy={player.ProfileId} ready={ready} missing={missing}",
            "FikaHostTransitController");
        // （analysis/transit-terminal-and-countdown）本轮修复的等待那一半：一位站在
        // 点内但按下从未到达本主机的队友，会得到那个让他自己的机器
        // 产生该请求的原生答复（TransitInteractionEvent.CheckAccess），有界且会上报。
        return false;
    }

    /// <summary>
    /// （analysis/transit-terminal-20261013）raid 内装备转场的每玩家那一半，应用时机与
    /// 原生本地控制器应用它的时刻相同。
    /// <para>
    /// 原生事实（2026-10-13 核对）：一次转场的转场容器是
    /// <c>TransitController.TransferItemsController</c>（interop dump 238759/238814），其中每玩家的网格
    /// 由 <c>TransferItemsController.InitPlayerStash(Player)</c> 创建
    /// （eft40-decompiled/TransferItemsControllerAbstractClass.cs:160-171），其大小来自
    /// <c>GetGridSize(profile)</c> -&gt; <c>GameWorld.GetAlivePlayerByProfileID(profileId)</c>，当该 profile 没有玩家对象时它会答复
    /// <b>0x0 外加一条 Debug.LogError("Cant find player with id ...")</b>；
    /// （同一文件 :199-209），而原生调用方是
    /// <c>LocalGameTransitControllerClass.method_20</c> = <c>OnPlayerEnter</c>（同一 dump :186-205），即
    /// 本机本地的触发器。
    /// </para>
    /// <para>
    /// 这就是为何在 Fika 上装备转场并不到达每位玩家：原生的每玩家调用只
    /// 会被本地玩家到达，而主机的替代品（<c>BaseGameController.CreateStashes</c>，
    /// 由 <c>HostGameController.CountdownScreen</c> 启动）在 raid 开始时只运行一次——角色对象
    /// 尚未进入世界的玩家在那里只能得到一个 0x0 容器，而客户端根本不会为自己运行该调用。
    /// 本方法以原生方式补上这个缺口：<b>仅当该玩家没有可用的
    /// 容器时</b>（缺失，或是本来就装不下任何东西的 0x0 网格），绝不在它已携带
    /// 物品时进行，因此玩家已经填好的容器绝不会被清空（InitPlayerStash 会清空它）。主机随后
    /// 用 <c>FikaServer.SendStashes()</c> 重新发布这些容器，即
    /// raid 开始时所用的同一调用。
    /// </para>
    /// <para>每种结果都是一行不受门控的 <c>[TransitDiag] dest=... gear-transfer applied=yes|no</c> 日志。</para>
    /// </summary>
    internal void EnsureTransferStashFor(Player member, TransitPoint point, string source)
    {
        if (member == null) return;
        var profileId = member.ProfileId;
        if (string.IsNullOrEmpty(profileId)) return;

        var destination = point == null ? string.Empty : TransitDestination.Resolve(point, out _);
        try
        {
            var controller = Native.TransferItemsController;
            if (controller == null)
            {
                

                return;
            }

            // 只读查看本 raid 的容器。元素类型故意保持未命名，因此
            // 本文件无需为一项诊断额外 using 物品栏命名空间。
            var containers = -1;
            var found = false;
            var width = 0;
            var height = 0;
            try
            {
                var list = controller._transferContainers;
                if (list != null)
                {
                    containers = list.Count;
                    for (var i = 0; i < list.Count; i++)
                    {
                        var candidate = list[i];
                        if (candidate == null || candidate.Id != profileId) continue;
                        found = true;
                        var grid = candidate.Grid;
                        if (grid != null)
                        {
                            width = grid.GridWidth;
                            height = grid.GridHeight;
                        }

                        break;
                    }
                }
            }
            catch (System.Exception)
            {
            }

            if (found && width > 0 && height > 0) return;

            // 网格大小从本世界的玩家对象读取，因此没有玩家对象的 profile 会
            // 再次产生原生的 0x0 容器。改为请求存活玩家对象，而不是传入的
            // 引用：客户端的玩家在本机上也是以同样方式按 profile 查找的。
            var resolved = Singleton<GameWorld>.Instance?.GetAlivePlayerByProfileID(profileId);
            if (resolved == null)
            {
                

                return;
            }

            controller.InitPlayerStash(resolved);
            var newWidth = 0;
            var newHeight = 0;
            try
            {
                var list = controller._transferContainers;
                if (list != null)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        var candidate = list[i];
                        if (candidate == null || candidate.Id != profileId) continue;
                        var grid = candidate.Grid;
                        if (grid != null)
                        {
                            newWidth = grid.GridWidth;
                            newHeight = grid.GridHeight;
                        }

                        break;
                    }
                }
            }
            catch (System.Exception)
            {
            }
            var sent = false;
            try
            {
                FikaSingleton<FikaServer>.Instance.SendStashes();
                sent = true;
            }
            catch (System.Exception exception)
            {
                
            }

            
        }
        catch (System.Exception exception)
        {
            
        }
    }

    /// <summary>
    /// 告知正在等待的玩家倒计时尚未开始。与原生区域
    /// 检查所用的消息相同，因此玩家为两种原因看到同一种措辞。
    /// </summary>
    private void NotifyTeammatesNotReady(Player player)
    {
        try
        {
            if (player.IsYourPlayer)
            {
                NotificationManager.DisplayWarningNotification(TransitMessagesEvent.EType.NonAllTeammates.ToString(), ENotificationDurationType.Default);
                return;
            }

            Il2CppSystem.Collections.Generic.Dictionary<int, TransitMessagesEvent.EType> messages = new();
            messages.Add(player.RaidId, TransitMessagesEvent.EType.NonAllTeammates);
            TransitEventPacket messagePacket = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Messages,
                TransitEvent = new TransitMessagesEvent()
                {
                    Messages = messages
                }
            };

            _server.SendData(ref messagePacket, DeliveryMethod.ReliableOrdered);
        }
        catch (System.Exception exception)
        {
                    }
    }

    /// <summary>某点的倒计时与面板所代表的队友，以 profile/raid-id 对表示。</summary>
    private string DescribeParticipants(int pointId)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var member in CollectZonePlayers(pointId))
        {
            if (member == null) continue;
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(member.ProfileId).Append('/').Append(member.RaidId);
        }
        return builder.Length == 0 ? "none" : builder.ToString();
    }

    /// <summary>本主机的原生转场注册表：哪个 profile 注册在哪个点上。</summary>
    private string DescribeNativeRegistry()
    {
        try
        {
            var builder = new System.Text.StringBuilder();
            foreach (var entry in Native.transitPlayers)
            {
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(entry.Key).Append('=').Append(entry.Value);
            }
            return builder.Length == 0 ? "empty" : builder.ToString();
        }
        catch (System.Exception exception)
        {
            return "error:" + exception.GetType().Name;
        }
    }

    /// <summary>
    /// 把站在该点内的每位队友注册到转场中，并用其控制器所等待的 Confirm 答复每一台远程
    /// 机器。仅在两个门控都通过后才运行，因此
    /// 本 raid 的原生倒计时会在同一刻为整组启动。
    /// </summary>
    private bool RegisterTransitParticipants(TransitPoint point, Player interacting, string keyId, EDateTime time)
    {
        var pointId = point.parameters.id;
        var members = EligibleMembers();
        var eligible = 0;
        var registered = 0;
        foreach (var member in members)
        {
            if (member == null || member.IsYourPlayer && FikaBackendUtils.IsHeadless) continue;
            if (!_playersInTransitZone.TryGetValue(member.ProfileId, out var zone) || zone != pointId) continue;
            eligible++;
            var preferredKey = member.ProfileId == interacting.ProfileId ? keyId : null;
            if (RegisterTransitParticipant(member, point, preferredKey, time)) registered++;
        }

        if (registered == 0)
        {
            
            return false;
        }

        

        // 该点由这次注册激活：位于其自身位置上的撤离点被移出
        // 交互，直到转场完成，因此两者无法争夺同一次按下。
        TransitCoLocatedExits.Suppress(point, $"the point registered {registered} player(s)");
        return true;
    }

    /// <summary>
    /// 一次已接受交互为某位玩家所执行注册的主机那一半：与单人路径过去所应用的
    /// 原生记账相同，外加远程控制器所需的 Confirm，
    /// 并加以保护，使一位失败的队友无法打断他人的转场，也无法把异常传播出去。
    /// </summary>
    private bool RegisterTransitParticipant(Player member, TransitPoint point, string preferredKeyId, EDateTime time)
    {
        var pointId = point.parameters.id;
        var profileId = member.ProfileId;
        if (Native.transitPlayers.ContainsKey(profileId))
        {
            
            return false;
        }

        var keyId = preferredKeyId;
        try
        {
            if (string.IsNullOrEmpty(keyId)) Native.TryGetAccessToLocation(member, pointId, out keyId);
        }
        catch (System.Exception exception)
        {
                        keyId = null;
        }

        try
        {
            Native.profileTime[profileId] = time;
            Native.transitPlayers[profileId] = member.RaidId;
            Native.profileKeys[profileId] = keyId;
            point.GroupEnter(member);
            ExfiltrationController.Instance.BannedPlayers.Add(member.RaidId);
            ExfiltrationController.Instance.CancelExtractionForPlayer(member);
            if (member.IsYourPlayer)
            {
                // 此处不得运行 Native.Cancel(player)：那个交互状态拥有倒计时 UI
                // （见先前单人路径的类注释）。
                Native.HideTransitInteractionNotification();
                ExfiltrationController.Instance.DisableExitsInteraction();
            }
            else
            {
                SendTransitInteraction(member.RaidId, pointId, TransitInteractionEvent.EType.Confirm);
            }
        }
        catch (System.Exception exception)
        {
                        ReleaseTransitBanFor(member.RaidId, profileId, "the registration threw; this player does not take part");
            return false;
        }

        
        return true;
    }

    /// <summary>本主机发往所有客户端的一个原生交互事件（Show/Confirm/Hide 答复）。</summary>
    private void SendTransitInteraction(int playerRaidId, int pointId, TransitInteractionEvent.EType type)
    {
        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.Interaction,
            TransitEvent = new TransitInteractionEvent()
            {
                PlayerRaidId = playerRaidId,
                PointId = pointId,
                Type = type
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// 启动刚刚通过门控的某点的倒计时。长度与时钟由 Fika 掌握：一次本地 raid 的
    /// 原生 tick 并不保证会为已转场的组运行，而两个驱动不可能
    /// 不一致，因为该值由同一个时钟计算得出。
    /// </summary>
    private void BeginCountdown(TransitPoint point)
    {
        var seconds = TransitStartGate.CountdownSeconds(point);
        FikaGlobals.LogWarning($"[TransitDiag] host countdown start point={point.parameters.id} seconds={seconds}");
        _countdownPointId = point.parameters.id;
        _countdownSeconds = seconds;
        _countdownEndsAt = Time.unscaledTime + seconds;
        // （analysis/transit-terminal-and-countdown）新的倒计时绝不继承上一个
        // 倒计时的冻结秒数。
        _countdownPaused = false;
        _countdownPausedRemaining = 0;
        _lastBroadcastSecond = -1;
        _recheckLoggedSignature = null;
        _recheckCount = 0;
        // 该点的新倒计时会重新启动面板：更早倒计时的界面
        // 已关闭或已过期，不得被误认为本次的界面。
        _panelSurfacePointId = -1;
        _panelSurfaceSeconds = -1f;
        _transferGraceEndsAt = -1f;
        
        BroadcastCountdown(true);
    }

    /// <summary>
    /// Fika 倒计时的一个 tick。由 <c>TransitPresenceReporter.Poll</c>（每秒四次，见 GameWorld.Update 补丁）
    /// 以及每一次原生 <see cref="Timers"/> 调用驱动，因此即使本 raid 的
    /// 原生 tick 不运行，倒计时仍会运行。
    /// </summary>
    internal void HostCountdownTick()
    {
        try
        {
            if (_countdownPointId >= 0)
            {
                // 规则的持续那一半：参与者列表在每个 tick 都被重新检查，而不只是在
                // 倒计时开始时。取消会在此返回，本次 tick 的其余部分被跳过。

                // （analysis/transit-terminal-and-countdown）规则的暂停那一半：当倒计时被
                // 冻结时其时钟不前进，因此本 tick 只寻找每位已注册参与者再次站到
                // 点内的时刻（暂停期间不广播任何东西）。
                if (_countdownPaused)
                {
                    return;
                }

                var remaining = System.Math.Max(0, (int)System.Math.Ceiling(_countdownEndsAt - Time.unscaledTime));
                if (remaining != _lastBroadcastSecond)
                {
                    _lastBroadcastSecond = remaining;
                    BroadcastCountdown(false);
                }

                if (remaining <= 0) FinishCountdown();
                return;
            }

            CheckTransferGrace();
        }
        catch (System.Exception exception)
        {
                        AbortCountdown("the countdown tick threw");
        }
    }

    /// <summary>
    /// （analysis/transit-terminal-and-countdown）运行中倒计时所携带的玩家：本主机
    /// 为该点注册的那些人，以其 raid id 为键——即原生点保存在自己计时
    /// 记录中的同一组。
    /// <para>
    /// 此处故意不使用区域映射。注册才是倒计时启动所依据的事实，
    /// 而区域映射只描述哪些报告到达了；因此区域报告丢失的客户端在主机的
    /// 字典中没有条目，而该客户端的原生应用器与 Fika 界面都按 raid id 查找
    /// 本地玩家——倒计时留在了主机上，客户端从未看到它
    /// （用户本轮的反馈）。每位已注册参与者总是会得到一个条目。
    /// </para>
    /// </summary>
    private List<Player> CountdownParticipants(int pointId)
    {
        var members = new List<Player>();
        try
        {
            foreach (var entry in Native.transitPlayers)
            {
                // 原生注册表是 profileId -> raid id（即 RegisterTransitParticipant 所写入、
                // 也是每个原生消费方读回的内容），而不是 profileId -> 点位 id。因此把
                // 该值与点位 id 相比较会丢弃每一个参与者：组计时器以一个条目或没有条目
                // 被通告，客户端在其中从未找到自己的 raid id，这就是
                // 客户端根本不显示倒计时的原因。此处接受两种形态，因此由原生路径
                // 填充的注册表仍然有效。
                var member = FindHumanPlayer(entry.Key);
                if (member == null || (entry.Value != member.RaidId && entry.Value != pointId)) continue;
                members.Add(member);
            }
        }
        catch (System.Exception exception)
        {
                    }

        return members;
    }

    /// <summary>
    /// （analysis/transit-terminal-and-countdown）因某位已注册参与者离开该点而冻结整组的
    /// 倒计时。与 <see cref="CancelCountdown"/> 不同，这里什么都不丢弃：剩余的
    /// 秒数、注册、原生组成员身份以及转场禁令全都保留，主机面板被
    /// 关闭，并告知每个客户端关闭自己的界面（一条不含该机器条目的
    /// 组计时器通告）。<see cref="TryResumeCountdown"/> 从冻结的那一秒继续同一个倒计时。
    /// </summary>

    /// <summary>
    /// <see cref="PauseCountdown"/> 的恢复那一半：每位已注册参与者又站到了
    /// 点内，因此冻结的秒数被放回时钟，同一个倒计时为整组
    /// 继续。在暂停状态的每个 tick 上运行，每次恢复写一行，且绝不抛出异常。
    /// </summary>

    /// <summary>
    /// （analysis/transit-terminal-and-countdown）倒计时的暂停通告：一个组计时器事件，其
    /// 字典<b>完全不</b>携带任何条目。机器的两半都把它读作“该倒计时不携带
    /// 我”——原生应用器什么都不显示（它只查找本地玩家），而客户端的 Fika 界面
    /// 会关闭它仍在屏幕上的面板（见 true）。它是
    /// 启动倒计时那条通告的反向形式，因此暂停不新增任何协议字段。
    /// </summary>
    private void BroadcastCountdownPaused(int pointId, int remaining)
    {
        try
        {
            TransitEventPacket packet = new()
            {
                EventType = TransitEventPacket.ETransitEventType.GroupTimer,
                TransitEvent = new TransitGroupTimerEvent
                {
                    PointId = pointId,
                    Timers = new Il2CppSystem.Collections.Generic.Dictionary<int, ushort>()
                }
            };

            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            
        }
        catch (System.Exception exception)
        {
                    }
    }

    /// <summary>
    /// （analysis/transit-terminal-and-countdown）本主机发给某个客户端的一个原生答复，正是原生权威在相同情形下
    /// 所发出的事件：点不可用时为 <c>InactivePoint</c>，
    /// 而当客户端需要自行解析访问权限并自己发出请求时为 <c>CheckAccess</c>。客户端
    /// 用自己的原生处理函数应用它，因此这里的答复没有任何臆造成分。
    /// </summary>
    private void SendAnswerEvent(Player player, int pointId, TransitInteractionEvent.EType type, string reason)
    {
        if (player == null || player.IsYourPlayer) return;

        // （analysis/transit-terminal-and-countdown）CheckAccess 会让客户端在同一刻发出请求，
        // 因此若本主机拒绝客户端认为有效的状态，否则就会永远这样答复下去。
        // 故该答复遵守与推促相同的上限，所有原因共享同一个桶。
        if (type == TransitInteractionEvent.EType.CheckAccess)
        {
            var bucket = "answer:" + player.ProfileId + "@" + pointId;
            var answered = _pressNudges.TryGetValue(bucket, out var count) ? count : 0;
            if (answered >= 4)
            {
                

                return;
            }

            var lastAnswer = _pressNudgedAt.TryGetValue(bucket, out var at) ? at : float.MinValue;
            if (Time.unscaledTime - lastAnswer < 5f) return;
            _pressNudgedAt[bucket] = Time.unscaledTime;
            _pressNudges[bucket] = answered + 1;
        }

        var sent = "no";
        try
        {
            SendTransitInteraction(player.RaidId, pointId, type);
            sent = "yes";
        }
        catch (System.Exception exception)
        {
            
        }

        // （2026-10-21）点名拒绝：客户端只会记录“主机答复了 InactivePoint”，因此做出
        // 决定的那台机器，正是本次调查每一轮都缺失的那一项证据。
        // 每个（玩家、点、答复、原因）一行：对同一被拒状态的重复按下不会重复
        // 该行，而不同的门控会。
        try
        {
            var key = player.ProfileId + "@" + pointId + "#" + type + ":" + reason;
            if (_answerLogged.Add(key))
            {
                FikaGlobals.LogInfo($"[TransitDiag] host answer={type} point={pointId} profile={player.ProfileId} " +
                    $"raidId={player.RaidId} reason={reason} sent={sent} " +
                    $"pointState={SafePointEnabled(pointId)} zone={SafeZoneOf(player.ProfileId)} " +
                    $"registered={SafeRegistered(player.ProfileId)}", "FikaHostTransitController");
            }
        }
        catch (System.Exception)
        {
        }
    }

    /// <summary>
    /// （2026-10-26 W25）只读：某转场点的进入条件，针对该玩家逐条判定。
    /// RE 证据（analysis/_re-transit-req.txt）：TransitPoint.Requirements 是一个 List&lt;TransitRequirement&gt;；
    /// 每个成员都暴露 Met(Player)、GetLocalizedTip() 与 get_IsEntryStage()；TransitParameters 携带 time、
    /// hideIfNoKey、waitForAllParty、maxGroupSize、completedQuestId、referenceConditions。
    /// 调用原生门控所调用的同一个谓词，可把“某个条件未满足”变成“就是这一条失败”。
    /// </summary>
    private static string DescribeEntryRequirements(TransitPoint point, Player player)
    {
        try
        {
            if (point == null) return "reqs=no-point";
            var text = new System.Text.StringBuilder("reqs=");
            var requirements = point.Requirements;
            var count = requirements == null ? 0 : requirements.Count;
            text.Append(count);
            for (var i = 0; i < count && i < 8; i++)
            {
                var requirement = requirements[i];
                if (requirement == null)
                {
                    text.Append(",[null]");
                    continue;
                }

                var type = "?";
                try
                {
                    var il2CppType = requirement.GetIl2CppType();
                    if (il2CppType != null && !string.IsNullOrEmpty(il2CppType.Name)) type = il2CppType.Name;
                }
                catch (System.Exception) { }

                var met = "?";
                try { met = requirement.Met(player) ? "y" : "n"; }
                catch (System.Exception) { }

                var tip = string.Empty;
                try
                {
                    var localized = requirement.GetLocalizedTip();
                    if (!string.IsNullOrEmpty(localized))
                    {
                        tip = localized.Length > 40 ? localized.Substring(0, 40) : localized;
                    }
                }
                catch (System.Exception) { }

                text.Append(",[").Append(type).Append(" met=").Append(met);
                if (tip.Length > 0) text.Append(" tip=").Append(tip);
                text.Append(']');
            }

            if (count > 8) text.Append(",+").Append(count - 8);

            try
            {
                var parameters = point.parameters;
                if (parameters != null)
                {
                    text.Append(" time=").Append(parameters.time);
                    text.Append(" hideIfNoKey=").Append(parameters.hideIfNoKey ? "y" : "n");
                    text.Append(" waitAll=").Append(parameters.waitForAllParty ? "y" : "n");
                    text.Append(" maxGroup=").Append(parameters.maxGroupSize);
                }
            }
            catch (System.Exception) { }

            return text.ToString();
        }
        catch (System.Exception exception)
        {
            return "reqs=describe-failed:" + exception.GetType().Name;
        }
    }

    /// <summary>
    /// 只读：本主机所持该点副本的三个原生状态位，供拒绝那一行使用。
    /// RE 证据（analysis/_re-transit-sig.txt，由 ildump 生成的 interop Assembly-CSharp.dll）：<c>TransitPoint</c> 同时携带
    /// <c>_Enabled_k__BackingField</c>（地图触发器状态，由
    /// <c>EFT.GameTriggers.HandlerTransitPointState.OnTrigger</c> 依据其 <c>_targetState</c> 写入）与
    /// <c>_IsActive_k__BackingField</c>（由私有的 <c>UpdateActivation</c>/<c>Update</c> 重新计算），此外还有
    /// 地点设置中的静态 <c>parameters.active</c>。只点名 <c>Enabled</c> 的拒绝无法区分
    /// “地图触发器关闭了该点”与“该点自身的激活重算为 false”，因此三者
    /// 都会打印：enabled=yes active=no 属于激活那一半，enabled=no active=yes 属于触发器那一半。
    /// </summary>
    private string SafePointEnabled(int pointId)
    {
        try
        {
            if (!Native.pointsById.TryGetValue(pointId, out var nativePoint) || nativePoint == null) return "missing";
            var enabled = nativePoint.Enabled ? "yes" : "no";
            var active = nativePoint.IsActive ? "yes" : "no";
            string configured;
            try { configured = nativePoint.parameters.active ? "yes" : "no"; }
            catch (System.Exception) { configured = "?"; }
            return $"enabled={enabled},isActive={active},configured={configured}";
        }
        catch (System.Exception)
        {
            return "?";
        }
    }

    /// <summary>只读：本主机已把该玩家注册在其中的点位 id，供拒绝那一行使用。</summary>
    private string SafeZoneOf(string profileId)
    {
        try
        {
            return _playersInTransitZone.TryGetValue(profileId, out var zone) ? zone.ToString() : "-";
        }
        catch (System.Exception)
        {
            return "?";
        }
    }

    /// <summary>只读：本主机的原生转场表是否已持有该玩家。</summary>
    private string SafeRegistered(string profileId)
    {
        try
        {
            return Native.transitPlayers.ContainsKey(profileId) ? "yes" : "no";
        }
        catch (System.Exception)
        {
            return "?";
        }
    }

    /// <summary>
    /// （analysis/transit-terminal-and-countdown）某位队友站在点内，而本主机没有他的任何按下记录，
    /// 因此严格门控无法通过，整组在等待一次可能永远不会到来的按下（客户端的
    /// 原生按下步骤可能静默失效——2026-10-13 的几轮在
    /// Shoreline -&gt; Terminal 点恰好测到过两次）。
    /// <para>
    /// 原生协议对这种状态有答复：<c>TransitInteractionEvent.CheckAccess</c>。该客户端
    /// 自己的原生处理函数随后解析该点的访问键并调用 <c>Player.TransitInteraction</c>——即
    /// 其按下链末端所发出的同一调用——因此请求由游戏自己的代码产生，而当该点
    /// 对该玩家不开放时，客户端会显示 'Transit/AccessNotGranted' 而不是默默等待。
    /// 此处不授予任何东西：该请求仍要通过本主机的每一个门控。
    /// </para>
    /// <para>有界：每位玩家、每个点、每个间隔最多推促一次，一个 raid 内每位玩家与每个点最多 <see cref="4"/>
    /// 次，每次推促一行。</para>
    /// </summary>

    /// <summary>
    /// 通告运行中的倒计时：为每个客户端一个组计时器数据包，其中包含每位参与者
    /// 以其 raid id 为键的值，以及本主机自己的计时面板，完全按原生主机
    /// 路径驱动它的方式。
    /// </summary>
    private void BroadcastCountdown(bool force)
    {
        var pointId = _countdownPointId;
        if (pointId < 0) return;

        var remaining = System.Math.Max(0, (int)System.Math.Ceiling(_countdownEndsAt - Time.unscaledTime));
        if (force) _lastBroadcastSecond = remaining;
        try
        {
            var timers = TransitStartGate.BuildTimers(CountdownParticipants(pointId), remaining, null);
            var panel = DriveLocalTimerPanel(pointId, remaining);
            TransitEventPacket packet = new()
            {
                EventType = TransitEventPacket.ETransitEventType.GroupTimer,
                TransitEvent = new TransitGroupTimerEvent()
                {
                    PointId = pointId,
                    Timers = timers
                }
            };

            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            
        }
        catch (System.Exception exception)
        {
                    }
    }

    /// <summary>
    /// 本主机自己的计时面板：先显示，再启动倒计时——即原生本地
    /// 转场控制器所使用的顺序，也是上面原生 <see cref="Timers"/> 处理函数所使用的顺序。
    /// <para>
    /// 这一对调用<b>每个倒计时只发出一次</b>，而不是每个值一次。<c>Show(seconds)</c> 并不是一个
    /// 值显示：它启动一个自行逐秒递减的倒计时，并且会再次
    /// 启动面板的淡入（<c>Co_ShowInfoPanel</c> 把画布 alpha 设为 0 再淡入回来），因此
    /// 每个通告值一次调用就是每个值一次闪烁——第 9 轮在客户端上正好展示了这点
    /// （LogOutput.log:2922-2942）。因此只有当通告值真正
    /// 重启了倒计时（值变大了）、或面板与该值相差超过
    /// <see cref="TransitStartGate.PanelToleranceSeconds"/>、或它根本不在屏幕上时，
    /// 界面才会重新启动——关闭的 HUD 最多每秒重开一次，绝不循环。
    /// </para>
    /// </summary>
    private string DriveLocalTimerPanel(int pointId, int seconds)
    {
        try
        {
            if (FikaBackendUtils.IsHeadless) return "headless";
            if (GamePlayerOwner.MyPlayer == null || MonoBehaviourSingleton<GameUI>.Instance == null) return "no GameUI";
            var transitTimerPanel = MonoBehaviourSingleton<GameUI>.Instance.LocationTransitTimerPanel;
            if (transitTimerPanel == null) return "no timer panel";

            var onScreen = transitTimerPanel.gameObject != null && transitTimerPanel.gameObject.activeInHierarchy;
            var secondsLeft = TransitStartGate.SurfacedSecondsLeft(pointId, _panelSurfacePointId,
                _panelSurfaceSeconds, _panelSurfaceStartedAt);

            // 运行中的界面自行逐秒递减，因此运行中倒计时的通告值就是它
            // 已经显示的那个值，不再重新启动。这个 return 就是
            // 下一轮的判据——倒计时运行期间它必须是正常结果。
            if (secondsLeft >= 0f && onScreen &&
                seconds <= _panelSurfaceSeconds + 0.5f &&
                System.Math.Abs(seconds - secondsLeft) <= TransitStartGate.PanelToleranceSeconds)
            {
                return $"self-walking (at {secondsLeft:0.0}s, not started again)";
            }

            if (secondsLeft >= 0f)
            {
                _panelSurfaceRestarts++;
                
            }

            if (!onScreen && Time.unscaledTime - _panelSurfaceReshowAt >= 1f)
            {
                // ShowPanel 是控制器自己的通告；最多每秒请求一次，
                // 因此未激活的层级由下面的守卫点名，而不是在这里反复猛敲。
                _panelSurfaceReshowAt = Time.unscaledTime;
                Native.ShowPanel(pointId);
                onScreen = transitTimerPanel.gameObject != null && transitTimerPanel.gameObject.activeInHierarchy;
            }

            transitTimerPanel.Display();
            if (!FikaUiPanels.CanDrive(transitTimerPanel, "FikaHostTransitController.CountdownTick.Show"))
            {
                return onScreen ? "on screen but not drivable" : "the panel is off and was not drivable";
            }

            transitTimerPanel.Show((float)seconds);
            _panelSurfacePointId = pointId;
            _panelSurfaceSeconds = seconds;
            _panelSurfaceStartedAt = Time.unscaledTime;
            _panelSurfaceReshowAt = Time.unscaledTime;
            return $"started once with {seconds}s (the panel walks it down itself)";
        }
        catch (System.Exception exception)
        {
                        return "error:" + exception.GetType().Name;
        }
    }

    /// <summary>
    /// 倒计时已归零。所有已注册客户端都从这里被转场，因为可游玩主机的原生
    /// tick 只驱动主机自己的玩家；该玩家会获得一个短的宽限
    /// 窗口等待原生转场，而若它没有到来，他的禁令会被再次解除。
    /// </summary>
    private void FinishCountdown()
    {
        var pointId = _countdownPointId;
        _countdownPointId = -1;
        _countdownPaused = false;
        _countdownPausedRemaining = 0;
        _lastBroadcastSecond = -1;
        _panelSurfacePointId = -1;
        _panelSurfaceSeconds = -1f;
        _pendingTransferPointId = pointId;
        _transferGraceEndsAt = Time.unscaledTime + TransferGraceSeconds;
        // 客户端与本主机自己的玩家一同被转场：该玩家的原生转场会发起
        // 它们的转场（见 Transit），因此在正常情况下两边在同一刻离开。
        // 只有未在宽限窗口内到达的原生转场，才会被 CheckTransferGrace 发起的
        // 转场替代——此时客户端在没有主机的情况下被带走。
        _transferFailures.Clear();
        
    }

    /// <summary>
    /// 停止一个失败的运行中倒计时（tick 抛出异常），并把它所注册的玩家的撤离
    /// 交互还给他们。它使用与取消相同的释放流程，因此中止同样无法留下注册、
    /// 原生组成员身份或转场禁令。
    /// </summary>
    private void AbortCountdown(string reason)
    {
        var pointId = _countdownPointId;
        _countdownPointId = -1;
        _countdownPaused = false;
        _countdownPausedRemaining = 0;
        _lastBroadcastSecond = -1;
        _panelSurfacePointId = -1;
        _panelSurfaceSeconds = -1f;
        _transferGraceEndsAt = -1f;
        _pendingTransferPointId = -1;
        _recheckLoggedSignature = null;
        

        var released = ReleaseParticipantsOfCancelledCountdown(pointId, "countdown aborted: " + reason);
        var panel = CloseLocalTimerPanel("the countdown of point " + pointId + " was aborted: " + reason);
        var suppressed = TransitCoLocatedExits.Release("the countdown of point " + pointId + " was aborted: " + reason);
        var banned = ExfiltrationController.Instance == null ? -1 : ExfiltrationController.Instance.BannedPlayers.Count;
        
    }

    /// <summary>
    /// 本机为某个点注册的 profile——运行中倒计时的参与者列表，
    /// 从原生注册表读取（即 <see cref="IssueTransferForClients"/> 所要求的同一个注册表，因此
    /// 两边谈论的是同一个集合）。
    /// </summary>
    private List<(string ProfileId, int RaidId)> RegisteredParticipants(int pointId)
    {
        var result = new List<(string ProfileId, int RaidId)>();
        try
        {
            if (Native.transitPlayers == null) return result;
            foreach (var entry in Native.transitPlayers)
            {
                // （2026-10-27 T2-B）与 CountdownParticipants 相同的两种形态。把点位 id 读回来
                // 等于拿点位 id 与 raid id 相比，因此对本类写入的每一个注册该列表都是空的：
                // 于是被取消的倒计时没有释放任何人——没有 GroupExit、没有向客户端发送 Hide、
                // 没有解除转场禁令、没有丢弃注册表条目。此处读取元组的 raid id。
                // 从玩家读取（在原生形态下该值本身就是点位 id）。
                var member = FindHumanPlayer(entry.Key);
                // 已离开 raid 的玩家无法仅凭该值区分（两种形态都是普通的
                // int）；他是有意保留在列表中的，因为这个列表也是在该点的倒计时被取消时
                // 丢弃他的注册并解除他禁令的依据。
                if (entry.Value != pointId && member != null && entry.Value != member.RaidId) continue;
                result.Add((entry.Key, member == null ? -1 : member.RaidId));
            }
        }
        catch (System.Exception exception)
        {
                    }

        return result;
    }

    /// <summary>当该玩家已从本 raid 撤离时为 true。</summary>
    private bool IsExtractedFromThisRaid(Player member)
    {
        try
        {
            var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
            return member != null && coopHandler != null && member.TryCast<FikaPlayer>() is { } fikaPlayer &&
                coopHandler.ExtractedPlayers.Contains(fikaPlayer.NetId);
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 开始规则的持续那一半（用户需求 1）：当某点的倒计时运行时，其
    /// 参与者列表在时钟的每个 tick 都被重新检查，而不是在启动时只检查一次。
    /// 判定来自 <see cref="TransitCountdownGuard.Classify"/>，每次判定变化都会写一行
    /// 判据行。
    /// <para>
    /// 倒计时被取消时返回 true，即调用者不得继续本次 tick。它
    /// 绝不抛出异常：失败的重检会上报自己并让倒计时继续运行，因为一个坏掉的检查
    /// 不得破坏它所观察的转场。
    /// </para>
    /// </summary>

    /// <summary>
    /// 在不取消倒计时的情况下从运行中的倒计时移除一位参与者：注册、该点的
    /// 原生组成员身份，以及——对尚未在离开途中的玩家——他的转场禁令。
    /// 用于已死亡的队友（在死亡开关关闭时）以及每一位离开此 raid 的队友，
    /// 这正是用户需求 3 的缺陷修复。
    /// </summary>
    private bool DropCountdownParticipant(int pointId, string profileId, bool releaseBan, string reason)
    {
        var point = FindPoint(pointId);
        var player = FindHumanPlayer(profileId);
        var raidId = player == null ? -1 : player.RaidId;
        var removed = false;
        try
        {
            if (Native.transitPlayers != null)
            {
                if (Native.transitPlayers.TryGetValue(profileId, out var registeredRaidId)) raidId = registeredRaidId;
                removed = Native.transitPlayers.Remove(profileId);
            }
        }
        catch (System.Exception exception)
        {
                    }

        _interactedPlayers.Remove(profileId);
        var groupExited = false;
        if (player != null && point != null)
        {
            try
            {
                point.GroupExit(player);
                point._confirmedPlayers.Remove(profileId);
                groupExited = true;
            }
            catch (System.Exception exception)
            {
                            }
        }

        if (releaseBan && raidId >= 0 && !_transittedPlayers.Contains(raidId))
        {
            ReleaseTransitBanFor(raidId, profileId, reason);
        }

        
        return removed || groupExited;
    }

    /// <summary>
    /// 某位已注册参与者死亡。在 <see cref="TransitCountdownGuard.AbortWhenSomeoneDies"/> 开启时
    /// 运行中的倒计时被取消——与玩家离开时相同的规则——否则他会被从
    /// 参与者列表移除，而存活者的倒计时继续，这正是原生机制所具有的
    /// 形态（<c>OnPlayerDead</c> 把他从组中移除，<c>SeparateGroupPlayers</c>
    /// 承载其余的人）。
    /// </summary>
    private void DropDeadParticipant(Player player, string reason)
    {
        var pointId = _countdownPointId;
        if (pointId < 0 || player == null) return;

        var registered = false;
        try
        {
            // （2026-10-27 T2-B）同样的两种形态：该检查对每一个 Fika 注册都返回 false，因此
            // 已注册的队友死亡后从未被移除，上面的死亡规则也从未生效。
            registered = Native.transitPlayers != null &&
                Native.transitPlayers.TryGetValue(player.ProfileId, out var registeredPoint) &&
                (registeredPoint == pointId || registeredPoint == player.RaidId);
        }
        catch (System.Exception)
        {
        }

        if (!registered) return;
        if (TransitCountdownGuard.AbortWhenSomeoneDies)
        {
            CancelCountdown("a registered teammate died",
                " player=" + player.ProfileId + " (" + reason + ") " + DescribePlayerPlacement(pointId, player));
            return;
        }

        DropCountdownParticipant(pointId, player.ProfileId, true, reason + "; the countdown continues for the others");
    }

    /// <summary>
    /// 结束某点的倒计时以及它正在准备的转场（用户需求 1）。
    /// <para>
    /// 激活所做的一切都按原生路径撤销它的顺序被撤销：每位已注册
    /// 玩家离开该点的原生组（<c>GroupExit</c>）并失去其 confirmed 标记，原生
    /// 注册表与交互账本丢弃他，他的转场禁令被解除（即“封印”：
    /// <c>ExfiltrationController.BannedPlayers</c> 中的条目加上被禁用的撤离交互），而先前在
    /// 该点自身位置上被移出交互的撤离点也被重新释出。每一位远程
    /// 参与者都会收到通知——客户端丢弃自己的注册、重新启用其撤离点并关闭
    /// 自己的倒计时面板——而本机也会关闭自己的面板。
    /// </para>
    /// 该点的原生计时记录不在此处写入：各组通过原生
    /// <c>GroupExit</c> 对离开，即原生离开路径所使用的调用，而它留下的状态
    /// （<c>pointGroups=.. timers[..] confirmed[..]</c>）属于下面的确认行——因此下一
    /// 轮可以读到原生倒计时是否真的停止了，而不必靠猜。
    /// </summary>
    private void CancelCountdown(string reason, string detail)
    {
        var pointId = _countdownPointId;
        if (pointId < 0) return;

        _countdownPointId = -1;
        _countdownPaused = false;
        _countdownPausedRemaining = 0;
        _lastBroadcastSecond = -1;
        _panelSurfacePointId = -1;
        _panelSurfaceSeconds = -1f;
        _transferGraceEndsAt = -1f;
        _pendingTransferPointId = -1;
        _recheckLoggedSignature = null;

        

        var released = ReleaseParticipantsOfCancelledCountdown(pointId, "countdown cancelled: " + reason);
        var panel = CloseLocalTimerPanel("the countdown of point " + pointId + " was cancelled: " + reason);
        var suppressed = TransitCoLocatedExits.Release("the countdown of point " + pointId + " was cancelled: " + reason);
        var banned = ExfiltrationController.Instance == null ? -1 : ExfiltrationController.Instance.BannedPlayers.Count;
        
    }

    /// <summary>
    /// 释放被取消的倒计时所注册的每一位玩家：原生组成员身份（通过与原生离开路径相同的
    /// <c>point.GroupExit</c>）、confirmed 标记、原生注册表条目、他的
    /// 转场禁令，以及——对远程玩家——告知其机器在本地做同样事情的消息。
    /// 返回确认行的细节片段。
    /// </summary>
    private string ReleaseParticipantsOfCancelledCountdown(int pointId, string reason)
    {
        var point = FindPoint(pointId);
        var released = 0;
        var groupsExited = 0;
        var clientsTold = 0;
        var selfCancelled = 0;
        var failed = new System.Text.StringBuilder();

        foreach (var entry in RegisteredParticipants(pointId))
        {
            var profileId = entry.ProfileId;
            var raidId = entry.RaidId;
            var player = FindHumanPlayer(profileId);
            if (player != null && point != null)
            {
                try
                {
                    point.GroupExit(player);
                    point._confirmedPlayers.Remove(profileId);
                    groupsExited++;
                }
                catch (System.Exception exception)
                {
                    if (failed.Length > 0) failed.Append(';');
                    failed.Append(profileId).Append(":GroupExit=").Append(exception.GetType().Name);
                }
            }

            try
            {
                Native.transitPlayers.Remove(profileId);
            }
            catch (System.Exception)
            {
            }

            _interactedPlayers.Remove(profileId);
            if (player != null && player.IsYourPlayer)
            {
                try
                {
                    // 本地那一半：该玩家的交互被原生取消（原生离开路径所发出的
                    // 那个调用），该点的提示也随之消失。
                    Native.Cancel(player);
                    Native.HideTransitInteractionNotification();
                    selfCancelled++;
                }
                catch (System.Exception exception)
                {
                    if (failed.Length > 0) failed.Append(';');
                    failed.Append(profileId).Append(":Cancel=").Append(exception.GetType().Name);
                }
            }
            else if (player != null)
            {
                // 远程那一半：与原生离开路径所发送的“你的转场交互已结束”相同的
                // 事件，它使客户端丢弃注册、重新启用其撤离点并关闭其面板。
                SendTransitInteraction(player.RaidId, pointId, TransitInteractionEvent.EType.Hide);
                NotifyTransitCancelled(player);
                clientsTold++;
            }

            if (!_transittedPlayers.Contains(raidId))
            {
                ReleaseTransitBanFor(raidId, profileId, reason);
            }

            released++;
        }

        return $"participants={released} nativeGroupsExited={groupsExited} remoteClientsTold={clientsTold} " +
            $"localInteractionsCancelled={selfCancelled} failed=[{failed}]";
    }

    /// <summary>
    /// 告知某位远程队友他所在点的转场已被取消，使用原生区域检查在相同事实上所用的
    /// 消息（“并非所有队友都在该点”）：他所看到的倒计时
    /// 已结束，必须重新进行交互。
    /// </summary>
    private void NotifyTransitCancelled(Player player)
    {
        try
        {
            Il2CppSystem.Collections.Generic.Dictionary<int, TransitMessagesEvent.EType> messages = new();
            messages.Add(player.RaidId, TransitMessagesEvent.EType.NonAllTeammates);
            TransitEventPacket messagePacket = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Messages,
                TransitEvent = new TransitMessagesEvent()
                {
                    Messages = messages
                }
            };

            _server.SendData(ref messagePacket, DeliveryMethod.ReliableOrdered);
        }
        catch (System.Exception exception)
        {
                    }
    }

    /// <summary>
    /// 用游戏自身关闭该界面的方法关闭本主机自己的计时界面
    /// （<c>BattleUITimerPanel.Close</c>，即面板自己的倒计时协程结束时调用的那个），因此被取消的
    /// 倒计时无法留下一个朝着永不会发生的转场递减的数字。
    /// </summary>
    private string CloseLocalTimerPanel(string reason)
    {
        try
        {
            if (FikaBackendUtils.IsHeadless) return "headless";
            var gameUi = MonoBehaviourSingleton<GameUI>.Instance;
            var panel = gameUi == null ? null : gameUi.LocationTransitTimerPanel;
            if (panel == null) return "no timer panel";
            if (panel.gameObject == null || !panel.gameObject.activeInHierarchy) return "already off";
            panel.Close();
            return "closed (" + reason + ")";
        }
        catch (System.Exception exception)
        {
                        return "error:" + exception.GetType().Name;
        }
    }

    /// <summary>
    /// 倒计时结束后，检查本主机自己玩家的原生转场是否开始。
    /// 若未开始，则解除转场禁令：该玩家必须能再次撤离，而不是
    /// 被锁在一个不会带他走的 raid 里。
    /// </summary>
    private void CheckTransferGrace()
    {
        if (_transferGraceEndsAt < 0f || Time.unscaledTime < _transferGraceEndsAt) return;
        _transferGraceEndsAt = -1f;
        var pointId = _pendingTransferPointId;
        _pendingTransferPointId = -1;
        var player = GamePlayerOwner.MyPlayer;
        if (player != null && _transittedPlayers.Contains(player.RaidId))
        {
            
            _transferFailures.Clear();
            return;
        }

        var issued = pointId >= 0 ? IssueTransferForClients(pointId, "no native transfer after the countdown ended") : 0;
        FikaGlobals.LogWarning($"[TransitDiag] host grace point={pointId} issued={issued} keys={(_lastTransitKeys == null ? "null" : _lastTransitKeys.Count.ToString())} hashEmpty={string.IsNullOrEmpty(_lastTransitHash)}");
        var failures = _transferFailures.Count == 0 ? "none" : string.Join(";", _transferFailures);
        if (player == null)
        {
            
            return;
        }

        
        Native.transitPlayers.Remove(player.ProfileId);
        ReleaseTransitBanFor(player.RaidId, player.ProfileId, "no native transfer after the countdown ended");
    }

    /// <summary>
    /// 把转场推送给为本点注册的每一位远程队友。这正是移植时
    /// 缺失的一步：在可游玩主机上，本地 raid 的原生 tick 只驱动主机自己的玩家，
    /// 因此没有这个调用，客户端会在主机离开后留在旧 raid 中。
    /// </summary>
    private int IssueTransferForClients(int pointId, string source)
    {
        var point = FindPoint(pointId);
        if (point == null)
        {
            
            return 0;
        }

        _transferFailures.Clear();
        var issued = 0;
        var eligible = 0;
        foreach (var member in EligibleMembers())
        {
            if (member == null || member.IsYourPlayer) continue;
            eligible++;
            // （2026-10-27 T2-B）注册表的值有两种形态。RegisterTransitParticipant 写入的是
            // 玩家的 RAID id（Native.transitPlayers[profileId] = member.RaidId——即每个原生
            // 消费方读回的形态），而原生路径写入的是点位 id。该检查只把它与
            // 点位 id 比较，因此一个已注册在正是本点上的客户端被读作“未
            // 注册”；下面的重新注册分支也帮不上忙（它拒绝表中已持有的
            // profile），于是转场跳过了他，他留在旧 raid 中而主机离开了。此处
            // 接受两种形态，完全如同 CountdownParticipants 已出于同样原因所做的那样。
            if (!Native.transitPlayers.TryGetValue(member.ProfileId, out var registeredPoint) ||
                (registeredPoint != pointId && registeredPoint != member.RaidId))
            {
                // （2026-10-26 W29）一个按下已有记录但原生注册已消失的玩家（即
                // “客户端先于主机按下”的情形：其自己的原生控制器在等待期间清掉了条目）
                // 过去会在此被跳过，因此他从未被带走，在其他人离开时留在旧 raid 中。
                // 按下就是同意，因此按正常路径的同样方式重新注册并把他带走。
                var pressedHere = _interactedPlayers.TryGetValue(member.ProfileId, out var pressedPoint) && pressedPoint == pointId;
                if (pressedHere && RegisterTransitParticipant(member, point, null, default))
                {
                    FikaGlobals.LogInfo($"[TransitDiag] grace re-registered profile={member.ProfileId} point={pointId} (press on record, native entry was gone)",
                        "FikaHostTransitController");
                }
                else
                {
                    _transferFailures.Add($"{member.ProfileId}=not registered for point {pointId} (registry={DescribeNativeRegistry()})");
                    continue;
                }
            }

            if (IssueClientTransfer(member, point, source)) issued++;
            else _transferFailures.Add($"{member.ProfileId}=not sent, see the line above");
        }

        // （2026-10-27 T2-B）该循环的失败列表一直只由宽限路径打印，因此
        // 本主机未带走的队友在正常（原生 `Transit`）路径中完全不会留下任何日志行。
        if (issued < eligible)
        {
            var reasons = _transferFailures.Count == 0 ? "none" : string.Join(";", _transferFailures);
            FikaGlobals.LogWarning($"[TransitDiag] transfer-skip point={pointId} issued={issued} eligible={eligible} reasons={reasons}", "FikaHostTransitController");
        }

        

        return issued;
    }

    /// <summary>
    /// 向某客户端发送他的转场：把目标地点记入本主机自己的注册表（客户端
    /// 据此校验该数据包，下一个 raid 也会读取它），并用原生路径所记录的同一行
    /// 上报该转场。幂等且有保护。
    /// </summary>
    private bool IssueClientTransfer(Player member, TransitPoint point, string source)
    {
        if (!_transferIssued.Add(member.RaidId))
        {
            
            return false;
        }

        try
        {
            if (!EnsureSummonedTransit(member.ProfileId))
            {
                _transferFailures.Add($"{member.ProfileId}=no transit history for this profile");
                
                _transferIssued.Remove(member.RaidId);
                return false;
            }

            var destination = BuildDestination(member, point, _lastTransitHash, _lastTransitKeys,
                _lastTransitPlayersCount > 0 ? _lastTransitPlayersCount : CountRegisteredParticipants(point.parameters.id));
            Native.alreadyTransits[member.ProfileId] = destination;
            _transittedPlayers.Add(member.RaidId);
            TransitEventPacket packet = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Extract,
                PlayerId = member.RaidId,
                TransitId = point.parameters.id,
                Destination = destination
            };

            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            
            return true;
        }
        catch (System.Exception exception)
        {
            _transferIssued.Remove(member.RaidId);
            _transferFailures.Add($"{member.ProfileId}={exception.GetType().Name}: {exception.Message}");
                        return false;
        }
    }

    /// <summary>本主机当前计为已注册在某点上的玩家数，作为转场的玩家数量。</summary>
    private int CountRegisteredParticipants(int pointId)
    {
        var count = 0;
        try
        {
            foreach (var entry in Native.transitPlayers)
            {
                // （2026-10-27 T2-B）两种形态：本类写入的值是玩家的 raid id，因此
                // 点位 id 从未匹配，该计数回退为 1——一个被转场组的玩家数量
                // 被通告成了一。
                var member = FindHumanPlayer(entry.Key);
                if (entry.Value == pointId || (member != null && entry.Value == member.RaidId)) count++;
            }
        }
        catch (System.Exception)
        {
            return 1;
        }

        return count > 0 ? count : 1;
    }

    /// <summary>
    /// 某个 profile 的转场历史。构造函数只为主机自己的 profile 创建一个条目，
    /// 因此过去每一次远程转场都会停在 <see cref="Transit"/> 的“没有转场历史”分支里，
    /// 只释放了禁令——这是客户端被落下的原因之一。
    /// </summary>
    private bool EnsureSummonedTransit(string profileId)
    {
        try
        {
            if (Native.summonedTransits.ContainsKey(profileId)) return true;
            if (_localRaidSettings == null) return false;
            Native.summonedTransits[profileId] = new Transit(_localRaidSettings.transition.transitionRaidId,
                _localRaidSettings.transition.transitionCount, FikaTransitHistory.AppendLocation(_localRaidSettings), IsEvent);
            
            return true;
        }
        catch (System.Exception exception)
        {
                        return false;
        }
    }

    /// <summary>
    /// 某位玩家转场的目标地点，按原生路径构建它的方式构建。客户端
    /// 会用其自己的 profile 校验该数据包，因此键集合被复制，并在原生调用未携带该玩家之处
    /// 被补全。
    /// </summary>
    private LocationTransit BuildDestination(Player player, TransitPoint point, string hash,
        Il2CppSystem.Collections.Generic.Dictionary<string, ProfileKey> keys, int playersCount)
    {
        var profiles = new Il2CppSystem.Collections.Generic.Dictionary<string, ProfileKey>();
        if (keys != null)
        {
            foreach (var entry in keys)
            {
                if (!profiles.ContainsKey(entry.Key)) profiles.Add(entry.Key, entry.Value);
            }
        }

        if (!profiles.ContainsKey(player.ProfileId))
        {
            
        }

        var location = TransitDestination.Resolve(point, out var destinationSource);
        if (destinationSource != "parameters.location")
        {
            
        }

        return new LocationTransit
        {
            hash = hash ?? string.Empty,
            playersCount = playersCount,
            ip = string.Empty,
            location = location,
            profiles = profiles,
            transitionRaidId = Native.summonedTransits.TryGetValue(player.ProfileId, out var history) ? history.raidId : _localRaidSettings?.transition.transitionRaidId ?? string.Empty,
            raidMode = ERaidMode.Local,
            side = player.Side == EPlayerSide.Savage ? ESideType.Savage : ESideType.Pmc,
            dayTime = Native.profileTime.TryGetValue(player.ProfileId, out var time) ? time : Native._localRaidSettings.timeVariant
        };
    }

    public void InteractWithTransit(Player player, InteractWithTransitPacket packet)
    {
        if (player == null || packet == null || !packet.hasInteraction || player.HealthController?.IsAlive != true)
        {
            
            return;
        }
        if (!Native.pointsById.TryGetValue(packet.pointId, out var point))
        {
            
            return;
        }
        // (2026-10-27 W48 / TARGETS N1) 用户复现：客机在转移点按下交互后立刻弹「无法使用转移功能」，
        // 但转移随后其实成功了。原因是这里只看原生的 point.Enabled —— 它由地图触发器驱动，
        // 合作局里按下那一刻常常还是 false，于是主机答 InactivePoint，客户端就把这句话显示出来，
        // 而倒计时/迁移随后照样走通。只要点位在配置里是 active（不是被配置关掉），就按"会话驱动"放行，
        // 真正的不可用仍由 access / entry-requirements / 组队门拒绝。
        if (!point.Enabled)
        {
            var configuredActive = point.parameters != null && point.parameters.active;
            if (!configuredActive)
            {
                SendAnswerEvent(player, packet.pointId, TransitInteractionEvent.EType.InactivePoint,
                    "point-disabled-by-config");
                return;
            }

            FikaGlobals.LogInfo($"[TransitDiag] point-disabled-but-session-driven point={packet.pointId} " +
                $"profile={player.ProfileId} nativeEnabled=false configured=true action=accept",
                "FikaHostTransitController");
        }
        // 自愈：一次被接受的交互证明该玩家正站在
        // 该点上，因此它也会注册他。这覆盖了丢失的区域数据包以及每一条
        // 根本不报告区域状态的路径。下面的其余检查（访问权限、进入
        // 条件、组规模、任务条件）保持不变，因此仅有在场永远不会
        // 让玩家从别处转场。
        if (!_playersInTransitZone.TryGetValue(player.ProfileId, out var currentPoint) || currentPoint != packet.pointId)
        {
            TransitZonePresence.RecordInZone(_playersInTransitZone, player.ProfileId, packet.pointId,
                "interact request at the point");
        }
        if (Native.transitPlayers.ContainsKey(player.ProfileId))
        {
            // （2026-10-19）进入该点就已经在本主机上注册了玩家（区域报告会运行
            // 原生 PlayerEnter），因此客户端的第一次按下会落到这里。把它当作“已注册
            // 因而不可用”正是过去拒绝每一次按下的原因；无论如何这次按下都是就绪信号，因此它
            // 走与正常路径相同的门控，且只有主机可以启动倒计时。
            RecordInteraction(player, packet.pointId);
            if (!CheckForPlayers(player, packet.pointId)) return;
            if (!EveryoneInteracted(player, packet.pointId)) return;
            if (!player.IsYourPlayer && !FikaBackendUtils.IsHeadless) return;
            BeginCountdown(point);
            return;
        }
        if (!Native.TryGetAccessToLocation(player, packet.pointId, out var keyId))
        {
            

            // （analysis/transit-terminal-and-countdown）“该点对你未开放”的原生答复：客户端会
            // 重新检查自己的访问权限，并自行发出请求或显示 Transit/AccessNotGranted。
            // 没有它，玩家只会得到一个静默的拒绝（2026-10-13 的 Terminal 几轮）。
            SendAnswerEvent(player, packet.pointId, TransitInteractionEvent.EType.CheckAccess, "access-not-granted");
            return;
        }
        if (!EFT.ClientTransitController.HasMetEntryRequirements(point, player))
        {
            

            // （analysis/transit-terminal-and-countdown）与上面相同的原生答复：客户端解析自己的
            // 状态，并用其自己的请求或 'Transit/AccessNotGranted' 作答。Terminal 点是
            // 游戏中断点条件并非走过场的那一个点（它的条件字符串是一个时间
            // 窗口 21:00-6:00，此外还有 hideIfNoKey 与它自己的引用条件），因此这正是用户反馈所
            // 关于的情形。
            // （2026-10-17）该答复过去是 CheckAccess，它会让客户端运行其按下步骤
            // 并再次发出请求：当拒绝者正是主机自己的进入条件评估时，
            // 两台机器会来回乒乓直到答复上限用尽，然后归于静默。
            // “该点对你不可用”的原生答复是 InactivePoint：客户端
            // 清除其交互，游戏显示原因。没有放松任何东西——门控仍然拒绝。
            // (2026-10-26 W25) "entry-requirements-not-met" 只说"有要求没过"，说不出是哪一条。把点位的
            // ExfiltrationRequirement 清单（类型 + Requirement 枚举值）和 TransitParameters 的时间/组队参数
            // 打出来，一次实机就能把这条闸门落到具体条件上。
            try
            {
                FikaGlobals.LogInfo($"[TransitDiag] entry-requirements point={packet.pointId} " +
                    $"profile={player.ProfileId} raidId={player.RaidId} {DescribeEntryRequirements(point, player)}",
                    "FikaHostTransitController");
            }
            catch (System.Exception)
            {
            }

            SendAnswerEvent(player, packet.pointId, TransitInteractionEvent.EType.InactivePoint, "entry-requirements-not-met");
            return;
        }
        // 任务条件已经由显示交互提示的客户端为每位玩家校验过了，
        // 也由上面的
        // EFT.ClientTransitController.HasMetEntryRequirements 校验过，而这个额外的原生查询
        // 对每个 profile 都返回 false 并阻塞了每一次转场。因此它只被记录而不被强制执行。
        if (!Native.HasMetCompletedQuestRequirement(point, player))
        {
            
        }
        

        // （analysis/client-transit-terminal）请求的到达是唯一能把“按下从未离开其机器的客户端”与
        // “主机已答复的客户端”区分开的事实，而 2026-10-13 那一轮
        // 无法区分两者（该分支的每一行都在 'Coop / Diagnostics' 之后）。每位
        // 玩家与每个点一行：按下已到达本主机并通过了原生检查；接下来由下面两个开始门控
        // 决定倒计时。
        
        

        if (_countdownPointId == packet.pointId)
        {
            // 组已在该点且倒计时正在运行；重复按下是一位已注册玩家的又一次请求，
            // 绝不能启动第二个倒计时。
            
            return;
        }

        // 先记住这次按下：下面的严格门控需要它作为该玩家已尽到自己那一份的记录，
        // 在整个组都完成之前不会注册任何东西。
        RecordInteraction(player, packet.pointId);
        if (!CheckForPlayers(player, packet.pointId)) return;
        if (!EveryoneInteracted(player, packet.pointId)) return;

        if (!RegisterTransitParticipants(point, player, keyId, packet.time))
        {
            // 什么都没注册，因此不得启动倒计时；该交互保持记录，
            // 之后任何队友的按下都会重新评估门控。
            return;
        }

        // （2026-10-19）客户端的按下只让该客户端就绪。倒计时由主机自己在
        // 每位队友就绪后的按下启动，因此客户端永远看不到拒绝，启动权留在
        // 主机手中。无头主机没有本地玩家，因此它的第一个通过的门控会启动它。
        if (!player.IsYourPlayer && !FikaBackendUtils.IsHeadless)
        {
            return;
        }

        BeginCountdown(point);
    }

    private bool CheckForPlayers(Player player, int pointId)
    {
        if (!_zoneCheckFirstCall)
        {
            _zoneCheckFirstCall = true;
            _zoneCheckLogged.Clear();
            _answerLogged.Clear();
            TransitZonePresence.ResetLog();
        }

        // 已死亡、已撤离或已转场的玩家被下面两个循环排除，因此
        // 他们的区域条目只会让映射变脏；在这里丢弃它们。仅仅是
        // 在地图别处的玩家保持注册，完全如同原生主机中那样——
        // 在那里注册只会被 PlayerExit 触发器移除。
        PruneStaleZoneRegistrations();

        var humanPlayers = 0;
        foreach (var fikaPlayer in FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler.HumanPlayers)
        {
            // 已经完成本次过渡的玩家会在
            // 下一帧从活动 raid 中移除。在这小段
            // 窗口期内，不要因为一名已撤离的成员而让
            // 其余组等待他进入该点。
            if (fikaPlayer.HealthController.IsAlive &&
                !TransitDropouts.IsDropped(fikaPlayer) &&
                !_transittedPlayers.Contains(fikaPlayer.RaidId) &&
                !FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler.ExtractedPlayers.Contains(fikaPlayer.NetId))
            {
                if (fikaPlayer.IsYourPlayer && FikaBackendUtils.IsHeadless)
                {
                    continue;
                }

                humanPlayers++;
            }
        }

        var playersInPoint = 0;
        foreach (var member in FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler.HumanPlayers)
        {
            if (member.HealthController?.IsAlive == true &&
                !TransitDropouts.IsDropped(member) &&
                !_transittedPlayers.Contains(member.RaidId) &&
                !FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler.ExtractedPlayers.Contains(member.NetId) &&
                !(member.IsYourPlayer && FikaBackendUtils.IsHeadless) &&
                _playersInTransitZone.TryGetValue(member.ProfileId, out var zone) && zone == pointId)
                playersInPoint++;
        }
        // （analysis/transit-terminal-20261013）原生 'NonAllTeammates' 答复的第二个产生者
        // 在每种模式下都自我点名，无论通过还是中止：区域报告从未到达的客户端就落
        // 在这里，否则这条链上的两个门控在日志中无法区分。
        if (playersInPoint < humanPlayers)
        {
            if (_zoneCheckLogged.Add(pointId + ":" + player.ProfileId))
            {
                
            }

            if (player.IsYourPlayer)
            {
                NotificationManager.DisplayWarningNotification(TransitMessagesEvent.EType.NonAllTeammates.ToString(), ENotificationDurationType.Default);
                return false;
            }

            Il2CppSystem.Collections.Generic.Dictionary<int, TransitMessagesEvent.EType> messages = new();
            messages.Add(player.RaidId, TransitMessagesEvent.EType.NonAllTeammates);

            TransitEventPacket messagePacket = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Messages,
                TransitEvent = new TransitMessagesEvent()
                {
                    Messages = messages
                }
            };

            _server.SendData(ref messagePacket, DeliveryMethod.ReliableOrdered);
            return false;
        }

        
        return true;
    }

    /// <summary>
    /// 主机当前计为站在某点内的玩家：与
    /// CheckForPlayers 所应用的同一个谓词（存活、尚未转场、未撤离、不是无头主机上主机自己的
    /// 玩家），从同一张映射读取——因此面板不可能与那个决定转场是否可以开始的
    /// 门控不一致。
    /// </summary>
    private List<Player> CollectZonePlayers(int pointId)
    {
        var result = new List<Player>();
        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        var players = coopHandler?.HumanPlayers;
        if (players == null) return result;

        for (var i = 0; i < players.Count; i++)
        {
            var member = players[i];
            if (member == null || member.HealthController?.IsAlive != true) continue;
            if (TransitDropouts.IsDropped(member)) continue;
            if (_transittedPlayers.Contains(member.RaidId)) continue;
            if (coopHandler.ExtractedPlayers.Contains(member.NetId)) continue;
            if (member.IsYourPlayer && FikaBackendUtils.IsHeadless) continue;
            if (!_playersInTransitZone.TryGetValue(member.ProfileId, out var zone) || zone != pointId) continue;
            result.Add(member);
        }

        return result;
    }

    /// <summary>
    /// 把原生组人数替换为观测到的区域内数量，每个位于该点内的玩家一次，
    /// 因此每台机器都能在字典中找到自己的 raid id，并且它们全都显示
    /// 同一个数字。当什么都没观测到时，原生字典原样透传，
    /// 而它所定义的槽位被保留，因此这张映射解释不了的显示绝不会消失。
    /// </summary>
    private Il2CppSystem.Collections.Generic.Dictionary<int, byte> BuildZoneSizes(int pointId,
        Il2CppSystem.Collections.Generic.Dictionary<int, byte> nativeSizes)
    {
        var inZone = CollectZonePlayers(pointId);
        if (inZone.Count == 0) return nativeSizes;

        var value = (byte)System.Math.Clamp(inZone.Count, 0, byte.MaxValue);
        var result = new Il2CppSystem.Collections.Generic.Dictionary<int, byte>();
        foreach (var member in inZone)
        {
            if (!result.ContainsKey(member.RaidId)) result.Add(member.RaidId, value);
        }

        if (nativeSizes != null)
        {
            foreach (var entry in nativeSizes)
            {
                if (!result.ContainsKey(entry.Key)) result.Add(entry.Key, value);
            }
        }

        return result;
    }

    /// <summary>供诊断使用的 raid-id 到计数字典的精简视图。</summary>

    /// <summary>
    /// 原生面板数字过去所依据的原生点记账：哪个 profile
    /// 属于哪个组，以及每个组的大小。它是判断远程玩家是否
    /// 曾在本主机上到达该点自己的组的证据。
    /// </summary>
    private string DescribePointGroups(int pointId)
    {
        if (Native.pointsById == null || !Native.pointsById.TryGetValue(pointId, out var point) || point == null)
        {
            return "point-unknown";
        }

        var builder = new System.Text.StringBuilder();
        foreach (var entry in point._playerGroups)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(entry.Key).Append("->").Append(entry.Value);
        }

        builder.Append(" size[");
        if (point._groupSize != null)
        {
            foreach (var entry in point._groupSize)
            {
                builder.Append(entry.Key).Append('=').Append(entry.Value).Append(';');
            }
        }

        // 该点的原生倒计时（组 -> 剩余秒数）就是承载主机自己玩家的时钟，
        // 而 confirmed 集合是原生组记账所等待的东西。同位的
        // 撤离点或从未注册该组的地图会破坏两者之一，而两者在此都可见。
        builder.Append("] timers[");
        if (point._groupTimers != null)
        {
            foreach (var entry in point._groupTimers)
            {
                builder.Append(entry.Key).Append('=').Append(entry.Value).Append(';');
            }
        }

        builder.Append("] confirmed[");
        if (point._confirmedPlayers != null)
        {
            foreach (var profile in point._confirmedPlayers)
            {
                builder.Append(profile).Append(';');
            }
        }

        builder.Append("] enabled=").Append(point.Enabled).Append(" active=").Append(point.IsActive);
        return builder.ToString();
    }

    /// <summary>丢弃永远无法再转场的玩家的注册。</summary>
    private void PruneStaleZoneRegistrations()
    {
        PruneInteractionLedger();
        if (_playersInTransitZone.Count == 0) return;
        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        if (coopHandler == null) return;

        List<string> stale = null;
        foreach (var entry in _playersInTransitZone)
        {
            var member = FindHumanPlayer(entry.Key);
            // 已不在 HumanPlayers 中的 profile 离开了该 raid；它也不再被
            // CheckForPlayers 计入，因此其条目可以保留到 raid 结束。
            if (member == null) continue;
            if (member.HealthController?.IsAlive != true ||
                _transittedPlayers.Contains(member.RaidId) ||
                (member.TryCast<FikaPlayer>() is { } fikaPlayer && coopHandler.ExtractedPlayers.Contains(fikaPlayer.NetId)))
            {
                stale ??= [];
                stale.Add(entry.Key);
            }
        }

        if (stale == null) return;
        foreach (var profileId in stale)
        {
            TransitZonePresence.RemoveFromZone(_playersInTransitZone, profileId, "dead, extracted or already transferred");
        }
    }

    /// <summary>
    /// 丢弃永远无法再参与的玩家的已记录交互，使已死亡或
    /// 已撤离的队友无法拖住存活者的倒计时。
    /// </summary>
    private void PruneInteractionLedger()
    {
        if (_interactedPlayers.Count == 0) return;
        List<string> stale = null;
        foreach (var entry in _interactedPlayers)
        {
            var member = FindHumanPlayer(entry.Key);
            // 已不在 HumanPlayers 中：该 profile 离开了该 raid，两个门控都不再计入它。
            if (member == null) continue;
            var transited = _transittedPlayers.Contains(member.RaidId);
            var extracted = member.TryCast<FikaPlayer>() is { } fikaPlayer &&
                FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler.ExtractedPlayers.Contains(fikaPlayer.NetId);
            if (member.HealthController?.IsAlive != true || transited || extracted)
            {
                stale ??= [];
                stale.Add(entry.Key);
            }
        }

        if (stale == null) return;
        foreach (var profileId in stale)
        {
            _interactedPlayers.Remove(profileId);
        }

        
    }

    private static Player FindHumanPlayer(string profileId)
    {
        var players = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler?.HumanPlayers;
        if (players == null) return null;
        for (var i = 0; i < players.Count; i++)
        {
            if (players[i] != null && string.Equals(players[i].ProfileId, profileId, System.StringComparison.Ordinal)) return players[i];
        }
        return null;
    }

    /// <summary>
    /// 当转场最终不会发生时，解除一次已接受交互所施加的转场禁令
    /// （见 <see cref="InteractWithTransit"/>）。
    /// <para>
    /// 为何需要它：接受路径把玩家加入 <c>ExfiltrationController.BannedPlayers</c>，
    /// 取消他的撤离并禁用撤离交互，但唯一的原生释放只在经由
    /// <c>TransitController.EnableExits(player)</c> 离开时才运行。一次被取消、被中止或
    /// 从未停止其 raid 的转场，因此会让该玩家在本次 raid 剩余时间内无法撤离。
    /// </para>
    /// 幂等：移除一个未被禁的 id 不做任何改变，且只有再也没有被禁玩家时才
    /// 重新启用撤离交互，因此仍在等待自己转场的玩家绝不会被
    /// 解除阻塞。调用方不得在它周围添加自己的状态。
    /// </summary>
    private void ReleaseTransitBanFor(int raidId, string profileId, string reason)
    {
        var exfiltration = ExfiltrationController.Instance;
        if (exfiltration?.BannedPlayers == null)
        {
            
            return;
        }

        var removed = exfiltration.BannedPlayers.Remove(raidId);
        var remaining = exfiltration.BannedPlayers.Count;
        if (remaining == 0)
        {
            // 只有集合为空时才重新开放撤离点；其他玩家的禁令仍然生效。
            exfiltration.EnableExitsInteraction();
            // 已无人再等待转场，因此该点的撤离点也重新响应。
            TransitCoLocatedExits.Release("no player waits for a transit any more");
        }

        
    }

    public TransitEventPacket CreateInitialization(Player player)
    {
        if (player == null || !Native.summonedTransits.TryGetValue(player.ProfileId, out var history)) return null;
        var points = new Il2CppSystem.Collections.Generic.Dictionary<int, LocationSettings.Location.TransitParameters>();
        var requirements = new Il2CppSystem.Collections.Generic.Dictionary<int, bool>();
        var enabled = new List<int>();
        var registeredPoint = -1;
        foreach (var entry in Native.pointsById)
        {
            if (entry.Value.parameters.active) points.Add(entry.Key, entry.Value.parameters);
            // 原始原生结果会在每个客户端 UI 中禁用该点，因此把它上报为
            // 已满足；每玩家校验仍留在客户端以及主机上的
            // EFT.ClientTransitController.HasMetEntryRequirements 那里。
            requirements.Add(entry.Key, true);
            if (entry.Value.Enabled) enabled.Add(entry.Key);
            if (Native.transitPlayers.ContainsKey(player.ProfileId) && entry.Value._playerGroups.ContainsKey(player.ProfileId))
                registeredPoint = entry.Key;
        }
        Native.profileKeys.TryGetValue(player.ProfileId, out var key);
        var time = Native.profileTime.TryGetValue(player.ProfileId, out var selected) ? selected : Native._localRaidSettings.timeVariant;
        return new TransitEventPacket
        {
            EventType = TransitEventPacket.ETransitEventType.Init,
            TransitEvent = new TransitInitEvent
            {
                PlayerRaidId = player.RaidId, Points = points, CompletedQuestRequirementMetByPointId = requirements,
                TransitionCount = (ushort)history.count, EventPlayer = history.events
            },
            EnabledPointIds = enabled.ToArray(), RegisteredPointId = registeredPoint, KeyId = key, SelectedTime = time
        };
    }

    /// <summary>
    /// 本 raid 的原生转场入口（对
    /// <c>LocalTransitController.Transit</c> 的补丁把每次原生调用都路由到这里），以及本主机自行发出的
    /// 一切：当原生调用驱动本主机自己的玩家时，客户端也从
    /// 这里被转场，因为可游玩主机的原生 tick 只会驱动它自己的玩家。
    /// </summary>
    public void Transit(TransitPoint point, int playersCount, string hash, Il2CppSystem.Collections.Generic.Dictionary<string, ProfileKey> keys, Player player)
    {
        var callDestination = TransitDestination.Resolve(point, out var destinationSource);
        
        if (player == null || point == null)
        {
            
            
            return;
        }

        if (_transferIssued.Contains(player.RaidId))
        {
            // 本主机自己发起了该玩家的转场。第二个驱动不得重复发送该数据包，
            // 也不得解除待处理转场所依赖的禁令。
            
            
            return;
        }

        // 原生参数被保留：本主机自行发起的转场（倒计时结束、原生驱动倒计时的
        // 客户端）都从相同的值构建。
        _lastTransitHash = hash;
        _lastTransitKeys = keys;
        _lastTransitPlayersCount = playersCount;

        if (_transittedPlayers.Contains(player.RaidId) || !EnsureSummonedTransit(player.ProfileId))
        {
            // 转场不会开始，但已接受的交互已经禁止该玩家
            // 撤离：在此解除它，否则他在本次 raid 剩余时间内一直被禁。
            
            ReleaseTransitBanFor(player.RaidId, player.ProfileId,
                "transit did not start (already transferred or no transit history for this profile)");
            return;
        }
        var history = Native.summonedTransits[player.ProfileId];
        var destination = new LocationTransit
        {
            hash = hash, playersCount = playersCount, ip = "", location = callDestination,
            profiles = keys, transitionRaidId = history.raidId, raidMode = ERaidMode.Local,
            side = player.Side == EPlayerSide.Savage ? ESideType.Savage : ESideType.Pmc,
            dayTime = Native.profileTime.TryGetValue(player.ProfileId, out var time) ? time : Native._localRaidSettings.timeVariant
        };
        if (player.IsYourPlayer)
        {
            var fikaPlayer = player.TryCast<FikaPlayer>();
            if (fikaPlayer == null || FikaSingleton<IFikaGame>.Instance is not CoopGame game)
            {
                
                ReleaseTransitBanFor(player.RaidId, player.ProfileId,
                    "transit did not start (no host game for the transferring player)");
                return;
            }
            if (TarkovApplication.Exist(out var application))
                application.TransitionStatus = new(destination.location, false, destination.side, destination.raidMode, destination.dayTime);
            Native.alreadyTransits[player.ProfileId] = destination;
            _transittedPlayers.Add(player.RaidId);
            _transferIssued.Add(player.RaidId);
            // AliveTransitPlayers 就是 _transittedPlayers.Count，HostGameController 在此时把它作为
            // 后端玩家数量发送（HostGameController.cs:1117-1118），因此下一个 raid 的等待玩家门控
            // 所收到的值必须能从这份日志中读出来。
            
            try
            {
                game.Extract(fikaPlayer, null, point);
            }
            catch (System.Exception extractException)
            {
                
                throw;
            }

            

            // 原生 tick 只驱动了本主机自己的玩家，因此同一点的客户端
            // 在此被转场：这正是过去把每个客户端留在旧 raid 中的那一步。
            var clientTransfers = IssueTransferForClients(point.parameters.id, "host transit started");
            if (clientTransfers > 0)
            {
                
            }

            // Extract() 只撤离玩家。客户端在 ClientGameController.Extract 中自行结束 raid，
            // 但主机从未这样做：它停留在半撤离
            // 状态（重力关闭、被传送到 500 米高空）而 raid 仍在运行，这正是
            // 主机从未加载下一张地图、之后也无法移动的确切原因。
            var stopGame = game;
            var stopProfile = fikaPlayer.ProfileId;
            var stopRaidId = fikaPlayer.RaidId;
            var stopPointName = point.parameters.name;
            MainThreadDispatcher.RunAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(4000);
                MainThreadDispatcher.Post(() =>
                {
                    if (ReferenceEquals(FikaSingleton<IFikaGame>.Instance, stopGame))
                    {
                        
                        stopGame.Stop(stopProfile, ExitStatus.Transit, stopPointName, 0f);
                        return;
                    }

                    // 本应结束的那个 raid 已不再是正在运行的那个，因此该玩家
                    // 仍在某个 raid 中，而转场禁令让他无法撤离：解除它。
                    ReleaseTransitBanFor(stopRaidId, stopProfile, "host raid was replaced before its own transit stop");
                });
            }).Forget();
            return;
        }
        // 远程玩家：一个 Extract 数据包，经由倒计时结束所用的同一路径发送，因此
        // 玩家绝不会被转场两次（见 _transferIssued）。
        Native.alreadyTransits[player.ProfileId] = destination;
        var remoteSent = IssueClientTransfer(player, point, "native Transit");
        
    }

    protected override void DisposeNative() => Native.Dispose();

    protected override void DisposeManaged()
    {
        _playersInTransitZone.Clear();
        _zoneCheckLogged.Clear();
        _zoneExitLogged.Clear();
        _interactedPlayers.Clear();
        _interactionLogged.Clear();
        _answerLogged.Clear();
        _transferIssued.Clear();
        _countdownPointId = -1;
        _countdownEndsAt = 0f;
        _countdownSeconds = 0;
        _countdownPaused = false;
        _countdownPausedRemaining = 0;
        _pressNudgedAt.Clear();
        _pressNudges.Clear();
        _panelSurfacePointId = -1;
        _panelSurfaceSeconds = -1f;
        _lastBroadcastSecond = -1;
        _transferGraceEndsAt = -1f;
        _pendingTransferPointId = -1;
        _recheckLoggedSignature = null;
        _recheckCount = 0;
        TransitDropouts.Forget();
        _lastTransitHash = null;
        _lastTransitKeys = null;
        _lastTransitPlayersCount = 0;
        // raid 结束；下一个 raid 会得到一组全新的诊断字符集。
        TransitZonePresence.ResetLog();
        // 把交互还给该点的撤离点（已销毁的点会被跳过），然后关闭
        // 账本：下一个 raid 以空的抑制列表开始。
        TransitCoLocatedExits.Release("the raid ended");
        TransitCoLocatedExits.Forget();
        _transferFailures.Clear();
        // 本 raid 显式关闭的点不得在下一个 raid 中保持关闭。
    }

    public void Init()
    {
        // 新的 raid：已结束世界的任何东西都不得存活于此链的静态字段中。WorldLifecycle 的拆除
        // 序列也会清空两个账本（TransitCoLocatedExits 与 TransitDropouts
        // 为此暴露了 ForceResetForWorldTeardown）；这些调用是自愈的那一半，它还覆盖了
        // 从未到达本类的拆除，而且它们完全不触碰任何原生对象。
        TransitCoLocatedExits.ForceResetForWorldTeardown("a new raid starts");
        TransitDropouts.ForceResetForWorldTeardown("a new raid starts");
        // 上一个 raid 的显式关闭标记随之消亡；新的 raid 以空集合开始。
        // 为掉线注册表提供输入的那一个订阅每进程只就绪一次（幂等）。
        TransitDropouts.Arm();

        var enabledFlags = string.Empty;
        foreach (var entry in Native.pointsById)
        {
            enabledFlags += entry.Key + ":" + entry.Value.Enabled + " ";
        }
        
        Native.EnablePoints(true);
        if (!FikaBackendUtils.IsHeadless && GamePlayerOwner.MyPlayer is { } player)
        {
            Native.SetTimers(Native.pointsById.Values, player, false);
            Native.HandleExits(Native.pointsById.Values, player);
        }
    }
}
