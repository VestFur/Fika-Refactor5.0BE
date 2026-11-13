using System;
using System.Collections.Generic;
using CommonAssets.Scripts.Cutscenes;
using EFT.GameTriggers;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using UnityEngine;

namespace Fika.Core.Main.Components;

/// <summary>
/// ICEBREAKER-RALLY-GATE (analysis/rally-gate-deadlock).
///
/// Icebreaker 的集合触发器（“等到所有玩家都到达”）会向原生
/// <c>TriggerRallyZone.GetAliveHumanPlayerCount()</c> 询问本机需要等待多少名玩家，而它在一场 Fika 战局中
/// 给出的答案并不是该战局的玩家数：原生统计遍历的是世界自身的记账数据，而在一个装了 mod 的 IL2CPP 战局里，
/// 那并不是游戏交给主机的那份列表。于是该区域等待一个无人能够达到的数字——客户端从主机广播的
/// 于是该区域等待一个无人能够达到的数字——客户端从主机广播的
/// <c>RallyZoneUpdateEvent</c> 中读到同一个数字，并永远显示“正在等待玩家 N/M”。
///
/// 这个类掌管决定该闸门的那一个数字。它给出的答案取自本场战局真正拥有的玩家
/// （<c>CoopHandler.HumanPlayers</c>，存活且已应用 headless），绝不为零，并且该答案在链路的
/// 两端保持一致：
/// <list type="number">
/// <item><see cref="ExpectedParticipants"/> 替换主机以及每个对等端上的原生计数
/// （补丁：<c>RallyZonePlayerCountPatch</c>），因此两台机器等待的是同一批玩家；</item>
/// <item><see cref="CorrectTotalAlive"/> 替换广播事件 <c>TotalAlive</c> 字段（该字段位于
/// <c>RallyZoneUpdateEvent</c> 中），因此客户端显示的“正在等待玩家”文本也是同一个计数；</item>
/// <item><see cref="EnsureRallyViewers"/> 确保集合触发器在主机上启动的过场动画
/// 被交付给每一位参与者，这样对等端就不会被留在主机已经进入的场景之外。</item>
/// </list>
///
/// 这里的其余内容都是下一轮排查所需要的诊断：闸门一行（区域、场内人数、
/// 被请求的计数、该计数的来源以及判定结论），广播事件一行，
/// 每个进程的跳过投票一行，对等端发出的跳过信号一行。每一行都以
/// <c>phase=</c> 开头，并在日志中带 <c>[RallyDiag]</c> 前缀，因此单次 grep 即可筛出：
/// <code>
/// [RallyDiag]: phase=gate zone=rally_point playersHere=2 expected=2 source=fika side=client -> gate=pass
/// [RallyDiag]: phase=skip:vote process=1 voteYes=1 voteNo=1 viewersInState=2 side=host result=waiting
/// </code>
///
/// 一个开关即可一次性回退所有这些部件的行为（见 REPORT 的“一键回退”一节）。
/// </summary>
internal static class FikaRallyGate
{
    /// <summary>
    /// 整个修复的总开关：开启（默认）时集合闸门等待本场战局的玩家，
    /// 关闭时把所有计数都交还给原生值（补丁仍然安装，但把原生值原样透传，
    /// 只保留诊断输出行）。
    /// </summary>
    internal static bool UseFikaPlayerCount = true;

    /// <summary>
    /// 死锁的另一半：观看者若在
    /// <see cref="SkipVoteTimeoutSeconds"/> 秒内一次过场动画信号都没有发出，就视为“不在过场动画中”——跳过投票与
    /// 完成度记账都不再等它。关闭 = 投票永远等待每一个观看者（
    /// 即修复前的行为）。
    /// </summary>
    internal static bool AllowSkipWithoutAllViewers = true;

    /// <summary>进程可以运行多久之后不再等待沉默的观看者（单位：秒）。</summary>
    internal static int SkipVoteTimeoutSeconds = 30;

    /// <summary>
    /// （analysis/cutscene-client-dispatch）派发链的席位修复：开启（默认）时，
    /// 本场战局的每一位参与者都是主机创建的每个过场动画进程的观看者，而不只是
    /// 出现在集合窗口内的进程（<see cref="EnsureRallyViewers"/>）。关闭：只有集合窗口
    /// 会补全该表，也就是本次构建之前的行为。
    ///
    /// 为什么只有窗口还不够：窗口由 <c>TriggerRallyZone.EmitTrigger</c> 布防，而
    /// Icebreaker 的触发器实际上是通过 SPTushonka 的集合监视器发出的
    /// （<c>TriggersEmitter.Emit</c>，SPTSinglePlayer 反编译第 1795 行）——这是另一条路径上的
    /// 另一个调用。当过场动画由该路径启动时，窗口并未布防，表里仍然只是
    /// 那一刻本地玩家列表所持有的内容，而不在其中的对等端，其原生客户端控制器
    /// 永远不会进入该场景（主机发送的其它任何东西都无法让它播放）。由配置键
    /// 'Coop / Cutscene: Every Participant Joins The Cutscene' 写入。
    /// </summary>
    internal static bool IncludeEveryParticipant = true;

    /// <summary>在没有任何变化的情况下，状态行重复输出的间隔。</summary>
    private const float RefreshSeconds = 60f;

    /// <summary>集合触发事件可以回溯多久，超过该时间后新进程不再归因于它。</summary>
    private const float RallyWindowSeconds = 3f;

    private static readonly Dictionary<string, string> Lines = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> LinesAt = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, float> ProcessSeen = new();
    private static readonly Dictionary<int, Dictionary<int, float>> Signals = new();
    private static float _nextScan;
    private static float _lastEmit = -1f;
    private static string _lastEmitZone;

    internal static bool InFika => FikaBackendUtils.ClientType != EClientType.None;

    internal static string Side => FikaBackendUtils.IsHeadless ? "headless"
        : FikaBackendUtils.IsServer ? "host"
        : FikaBackendUtils.IsClient ? "client" : "single";

    /// <summary>闸门所用计数来自何处——该行必须写明它。</summary>
    internal static string Source => UseFikaPlayerCount && InFika ? "fika" : "native";

    private static CoopHandler Handler => FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;

    /// <summary>
    /// 本场战局真正拥有的玩家：Fika 所知的、且仍然存活的每一位真人玩家。这就是
    /// 每台机器上集合闸门所等待的数字，也正是两端能够达成一致的原因。结果
    /// 从不小于 1——原生闸门会把“场内人数”与它比较，而零将成为一个
    /// 永远无法开启的闸门（SPTushonka 集合监视器的单人作者对同一问题也做了防护）。
    /// </summary>
    internal static int ExpectedParticipants()
    {
        var handler = Handler;
        if (handler?.HumanPlayers == null) return 1;
        var alive = 0;
        for (var i = 0; i < handler.HumanPlayers.Count; i++)
        {
            try
            {
                var player = handler.HumanPlayers[i];
                if (player == null) continue;
                var health = player.HealthController;
                if (health == null || !health.IsAlive) continue;
                alive++;
            }
            catch (Exception)
            {
                // 只生成了一半的玩家还没有健康控制器，此刻不是参与者。
            }
        }
        return alive < 1 ? 1 : alive;
    }

    internal static void Reset()
    {
        Lines.Clear();
        LinesAt.Clear();
        ProcessSeen.Clear();
        Signals.Clear();
        _nextScan = 0f;
        _lastEmit = -1f;
        _lastEmitZone = null;
    }

    /// <summary>
    /// （perf 2026-10-14，功能性修复）以前每个间隔会对所有集合区域采样一次，用于写入
    /// 诊断状态行。该采样是一次全场景的 FindObjectsOfType（在 Streets 实测 154 ms；
    /// 限流前每场战局 35 次，限流后每 300 秒一次），而闸门逻辑
    /// （GetAliveHumanPlayerCount / CheckAllPlayersInZone / EmitTrigger）从不读取它的结果——闸门的
    /// 状态来自区域自身的原生事件。因此这里直接移除周期性扫描，而不是把它拉长：
    /// 状态行仍由触发路径通过 ZoneLine() 写入。
    /// Tick() 保留为 tick 入口，这样无需改动任何调用点。
    /// </summary>
    internal static void Tick()
    {
        if (!InFika) return;
    }


    /// <summary>
    /// 闸门行：谁在场内、闸门等待什么、该数字来自何处以及判定结论。
    /// 其中任一项发生变化时写入，其余情况每分钟写一次。
    /// </summary>
    internal static void ZoneLine(TriggerRallyZone zone, string source)
    {
        if (zone == null) return;
        string id;
        int here;
        bool triggered;
        bool localIn;
        try
        {
            id = zone._triggerId;
            here = zone._playersInside == null ? 0 : zone._playersInside.Count;
            triggered = zone._wasTriggered;
            localIn = zone._localPlayerInZone;
        }
        catch (Exception) { return; }
        var expected = ExpectedParticipants();
    }

    /// <summary>
    /// 计数补丁的行：它在原生方法给出的结果（<c>native=</c>）旁边，
    /// 记下闸门现在使用的数字（<c>expected=</c>）。正是这一行点明了某场战局
    /// “正在等待玩家 N/M”的来源：远高于战局自身玩家数的 <c>native=</c> 就是那个坏掉的数字。
    /// </summary>
    internal static void CountLine(TriggerRallyZone zone, int native, int applied)
    {
        if (zone == null) return;
        string id;
        int here;
        try
        {
            id = zone._triggerId;
            here = zone._playersInside == null ? 0 : zone._playersInside.Count;
        }
        catch (Exception) { return; }
    }

    /// <summary>
    /// <c>RallyZoneUpdateEvent</c> 的行，以及对其 <c>TotalAlive</c> 字段的修正。客户端显示的
    /// “正在等待玩家 PlayersInZone/TotalAlive”正是该事件，因此这一行可以证明
    /// 客户端显示的数字是由哪一端产生的。
    /// </summary>
    internal static byte CorrectTotalAlive(int zoneId, int playersInZone, int totalAlive)
    {
        var expected = ExpectedParticipants();
        var corrected = UseFikaPlayerCount && InFika;
        if (!corrected) return totalAlive < 0 ? (byte)0 : totalAlive > 255 ? (byte)255 : (byte)totalAlive;
        return (byte)Mathf.Clamp(expected, 1, 255);
    }

    /// <summary>本机上的集合触发器已触发（<c>EmitTrigger</c>）。</summary>
    internal static void NoteEmit(string zone, int count)
    {
        _lastEmit = Time.unscaledTime;
        _lastEmitZone = zone;
    }

    private static bool InRallyWindow => _lastEmit >= 0f && Time.unscaledTime - _lastEmit <= RallyWindowSeconds;

    /// <summary>
    /// 把由集合触发器启动的进程交给本场战局的每一位参与者。
    ///
    /// 该进程由触发器所到达的那个 handler 创建，其观看者列表由
    /// 那一刻世界的玩家列表构成——主机尚未生成的玩家（或计数时被
    /// 跳过的对等端）于是被留在了一个其他人都已进入的过场动画之外，而这恰恰是
    /// “跳过”投票永远无法完成的状态。只有出现在集合窗口内的进程会被处理，
    /// 并且只在主机上处理；没有任何人添加观看者的进程保持不变。
    /// </summary>
    internal static void EnsureRallyViewers(CutsceneProcess process)
    {
        if (process == null || !FikaBackendUtils.IsServer || !UseFikaPlayerCount || !InFika || !InRallyWindow) return;
        AddMissingViewers(process, "rally");
    }

    /// <summary>
    /// （analysis/cutscene-client-dispatch）席位修复中与窗口无关的那一半：由主机的派发
    /// （<see cref="RaidInteractionSync.StartCutscene"/>）为它即将广播的每个进程调用，这样即使集合窗口
    /// 从未布防，对等端也处于本场战局所播放的过场动画的观看者表之中。观看者
    /// 表就是原生的 <c>CutsceneProcess.playersInCurrentView</c> 列表，它正是广播所携带的内容
    /// （也是对等端原生客户端控制器过滤的依据），因此这是唯一决定
    /// 对等端究竟能否进入该场景的地方。
    /// </summary>
    internal static void IncludeAllParticipants(CutsceneProcess process)
    {
        if (process == null || !IncludeEveryParticipant || !FikaBackendUtils.IsServer || !InFika) return;
        AddMissingViewers(process, "dispatch");
    }

    /// <summary>两个入口的唯一实现：把表中缺失的每一位参与者追加进去。</summary>
    private static void AddMissingViewers(CutsceneProcess process, string source)
    {
        try
        {
            var handler = Handler;
            var viewers = process.playersInCurrentView;
            if (handler?.Players == null || viewers == null) return;
            var before = viewers.Count;
            var missing = new List<int>();
            foreach (var entry in handler.Players)
            {
                var player = entry.Value;
                if (player == null) continue;
                var playerId = player.PlayerId;
                if (playerId <= 0 || viewers.Contains(playerId)) continue;
                missing.Add(playerId);
            }
            if (missing.Count == 0) return;
            for (var i = 0; i < missing.Count; i++) viewers.Add(missing[i]);
        }
        catch (Exception error)
        {
        }
    }

    /// <summary>主机已广播某个进程：沉默观看者规则的计时从这里开始。</summary>
    internal static void NoteProcessStart(int processId)
    {
        if (processId == 0) return;
        ProcessSeen[processId] = Time.unscaledTime;
    }

    internal static void ForgetProcess(int processId)
    {
        ProcessSeen.Remove(processId);
        Signals.Remove(processId);
    }

    /// <summary>
    /// 观看者的一个过场动画信号。观看者发送的内容中只有三样能证明它确实
    /// 在该过场动画内部——它的跳过投票、它的“已观看”确认以及它的计时校正——
    /// 正是它们把“这个对等端在场景内且安静”与“这个对等端从未进入过”区分开来。
    /// </summary>
    internal static void NoteSignal(int processId, int netId, string kind)
    {
        if (processId == 0 || netId <= 0) return;
        if (!Signals.TryGetValue(processId, out var viewers))
        {
            viewers = new Dictionary<int, float>();
            Signals[processId] = viewers;
        }
        viewers[netId] = Time.unscaledTime;
    }

    internal static bool HasSignal(int processId, int netId) =>
        Signals.TryGetValue(processId, out var viewers) && viewers.ContainsKey(netId);

    /// <summary>主机广播该进程距今多久（从未见过时为 0）。</summary>
    internal static float ProcessAge(int processId) =>
        ProcessSeen.TryGetValue(processId, out var seen) ? Time.unscaledTime - seen : 0f;

    /// <summary>
    /// 当某个进程运行足够久之后，仍未发出任何信号的观看者不会再加入时返回 true。
    /// 关闭（或超时为零）时投票依旧等待每一个观看者，与之前完全一致。
    /// </summary>
    internal static bool SilentViewerTimeoutReached(int processId)
    {
        if (!AllowSkipWithoutAllViewers || SkipVoteTimeoutSeconds <= 0) return false;
        return ProcessAge(processId) >= SkipVoteTimeoutSeconds;
    }

    /// <summary>写入某个进程的跳过行（主机：统计之后；客户端：收到之后）。</summary>
    internal static void SkipLine(string key, int processId, int voteYes, int voteNo, int viewers, string result, string detail)
    {
    }

    /// <summary>跳过行的客户端那一半：本机被告知了什么，又回答了什么。</summary>
    internal static void SkipReceived(CutsceneSkipIntentionEventFromServer intention)
    {
        if (intention == null) return;
        var skipping = 0;
        try { skipping = intention.SkippingPlayerIds?.Length ?? 0; }
        catch (Exception) { }
        var players = 0;
        try { players = intention.PlayersCount; }
        catch (Exception) { }
        SkipLine("skiprecv:" + intention.ProcessId, intention.ProcessId, skipping, Math.Max(0, players - skipping),
            players, "received", "the host broadcast a skip vote and this machine is expected to vote too");
    }

    /// <summary>本地长按（hold）的跳过行：本机即将发送自己的投票。</summary>
    internal static void SkipSent(CutscenesClientController controller, bool wantsToSkip)
    {
        if (controller == null) return;
        int processId;
        string cutscene;
        bool ui;
        try
        {
            processId = controller._cutsceneProcessId;
            cutscene = controller._cutsceneId;
            ui = controller._skipUiActive;
        }
        catch (Exception) { return; }
    }

    /// <summary>主机通知本机执行跳过：在每个对等端上都会写入。</summary>
    internal static void SkipApplied(CutsceneSkipEventFromServer skip)
    {
        if (skip == null) return;
        var processId = 0;
        try { processId = skip.processId; }
        catch (Exception) { }
    }

    private static string Id(string value) => string.IsNullOrEmpty(value) ? "-" : value;
}
