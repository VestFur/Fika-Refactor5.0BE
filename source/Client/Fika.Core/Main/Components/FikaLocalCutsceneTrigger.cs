using System;
using System.Collections.Generic;
using CommonAssets.Scripts.Cutscenes;
using EFT;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.Components;

/// <summary>
/// BD（analysis/cutscene-client-native-trigger）：
/// 客户端通过自己的原生触发链就地播放剧情过场。
///
/// 为什么需要它。"host dispatch -&gt; CutSceneStartEvent -&gt; the local
/// client controller" 这条路径要求主机先发送过 CutsceneStartPacket。在
/// analysis/logs-coop3-20261013-122918 这一轮里，客户端连一行 'phase=dispatch'
/// 都没有——而那一行写在 'Coop / Diagnostics' 开关之外，每个进程 id 只写一次——所以这次突袭的主机根本没有派发。
/// 客户端确实拥有的是它自己的原生链：集结点触发器走到了 SPTushonka 的 TriggerCutscenePatches.Start，
/// 它调用了原生的 CutscenesServerController.StartCutscene，而 Fika 自己的
/// CutsceneStartSyncPatch.Prefix 把它短路成 __result = null——这正是 LogOutput.log:1947
/// 里 "trigger cutscene: 'Icebreaker_1' refused" 那一行。因此客户端既没有画面，也没有进程。
///
/// 本类接手那个被丢弃的请求并就地以原生方式运行它：一旦客户端自己的触发器请求过场
/// X，桥接就让本机自己的 <c>CutscenesServerController.StartCutscene</c>
/// 跑起来——正是主机所运行的那一次调用——除非主机已经派发过同一个过场（主机派发优先，
/// 本地不触发任何东西；而这个判断在任何等待之前就已知晓，
/// 因为起始数据包一到就被记录下来了）。本地服务器控制器创建进程，
/// 桥接把它交给本机的客户端控制器，当本地观看者看完之后，同一控制器的原生
/// <c>EndCutscene</c> 会执行时间轴的结束动作（地图的门）。
///
/// 一个过场在本地最多触发一次（<see cref="Played"/>）；
/// 主机派发过的过场绝不在本地触发（<see cref="HostStarted"/>）；
/// 而本机已经以原生方式播放的过场，在主机数据包稍后到达时不会被再次启动（见
/// FikaClient.RaidInteractions.cs）。主机路径与单人路径未被改动：
/// 这里的每个入口都以 <see cref="Enabled"/> 开头，而它在主机上为 false。
///
/// 一个开关即可还原整个行为（<see cref="UseNativeClientTrigger"/>）。
/// </summary>
internal static class FikaLocalCutsceneTrigger
{
    /// <summary>
    /// 整个修复的总开关：开启（默认）时，一个曾请求自己的原生链启动过场的客户端，
    /// 在同一过场没有主机数据包到达时会自己运行那条原生链；关闭则恢复先前的行为（本地启动被丢弃，
    /// 正如 CutsceneStartSyncPatch 以前所做的那样）。
    /// </summary>
    internal static bool UseNativeClientTrigger = true;

    /// <summary>
    /// 本地触发的进程的座位表：开启（默认）时它只包含本机自己的玩家，
    /// 因此进程的原生完成计数会达到它自己的总数（1 of 1），而原生
    /// EndCutscene——连同时间轴的结束动作——会在本机自行运行。
    /// 关闭则保留原生列表（这个世界里所有非 AI 的存活玩家，
    /// 包括主机角色），而这对一个没有别人能看的场景意味着原生结束永远不会到来。
    /// </summary>
    internal static bool SingleLocalViewer = true;

    /// <summary>
    /// 排队中的本地请求在桥接自己触发原生链之前等待的秒数。
    /// 故意设为零：主机派发过的过场在数据包落地时就已知晓（HostStarted），
    /// 所以等待只会拖延那些仅存在于本机的过场。
    /// </summary>
    // (2026-10-27 T5) 0f：客机自起**不等主机**。这个值只决定"排队中的请求什么时候由本机点火"，不决定
    // 谁先起过场——主机派发的过场由 HostStarted 让路（NoteHostStart 在收包时立刻记下，比任何延时都早），
    // 所以给主机留 2 秒既拦不住"两个进程撞同一个 cutscene id"，又让只存在于本机的过场（终点站 / 破冰船）
    // 白等 2 秒。W30 的 2f 是这个误判，按用户要求改回 0。
    internal static float LocalTriggerDelaySeconds = 0f;

    /// <summary>
    /// （2026-10-21）本地进程在本机自行结束它之前最多可以保持打开的时长。它是
    /// <see cref="PendingEnds"/> 的第二个结束条件：
    /// 一个无人确认的场景，否则会在余下的突袭时间里一直占着该进程——以及它启动时授予的免疫。
    /// </summary>
    internal static float LocalProcessTimeoutSeconds = 60f;

    /// <summary>本机自己的触发器要求启动的过场，以及请求发起的时刻。</summary>
    private static readonly Dictionary<string, float> Requests = new(StringComparer.Ordinal);
    /// <summary>本机已经通过自己的原生 StartCutscene 播放过的过场。</summary>
    private static readonly HashSet<string> Played = new(StringComparer.Ordinal);
    /// <summary>主机已启动的过场（本客户端曾看到过为它准备的 CutsceneStartPacket）。</summary>
    private static readonly HashSet<string> HostStarted = new(StringComparer.Ordinal);
    /// <summary>本机自己创建的进程 id，映射到它们各自的过场（本地原生进程）。</summary>
    private static readonly Dictionary<int, string> LocalProcesses = new();
    /// <summary>
    /// （2026-10-21）尚未结束的本地进程，以进程 id 为键：本机自己创建的进程从这里关闭——在本地
    /// <c>TimelineShowed</c> 时，或在超过 <see cref="LocalProcessTimeoutSeconds"/>
    /// 之后——用的是原生 <c>CutscenesServerController.EndCutscene(cutsceneId,
    /// processId, process, playersId,
    /// skip)</c>（interop :94617）。一个座位表被本机收窄到自身的进程，
    /// 其原生结束可能永远不会到来；而一个保持打开的进程会一直保留它启动时授予的狙击手免疫。
    /// </summary>
    private static readonly Dictionary<int, PendingLocalProcess> PendingEnds = new();
    /// <summary>桥接授予下一次原生 StartCutscene 调用的那个过场。</summary>
    private static string _grant;
    private static float _nextScan;

    /// <summary>仅在 Fika 突袭的客户端上为 true——主机保留自己的路径。</summary>
    internal static bool Enabled => UseNativeClientTrigger && FikaBackendUtils.IsClient;

    internal static void Reset()
    {
        Requests.Clear();
        Played.Clear();
        HostStarted.Clear();
        LocalProcesses.Clear();
        PendingEnds.Clear();
        _grant = null;
        _nextScan = 0f;
    }

    /// <summary>突袭结束时清除某个世界的过场状态（新的突袭不得继承它）。</summary>
    internal static void ForgetWorld()
    {
        Requests.Clear();
        Played.Clear();
        HostStarted.Clear();
        LocalProcesses.Clear();
        PendingEnds.Clear();
        _grant = null;
        _nextScan = 0f;
    }

    /// <summary>本机是否已经（或曾经）通过自己的原生触发器播放这个过场。</summary>
    internal static bool HasPlayedLocally(string cutsceneId) =>
        !string.IsNullOrEmpty(cutsceneId) && Played.Contains(cutsceneId);

    /// <summary>
    /// 这个进程是否是本机自己创建的（本地原生触发器）。边界用它来区分
    /// "我自己的过场，我自己的原生结束" 与 "主机的进程，主机的结束"。
    /// </summary>
    internal static bool OwnsLocalProcess(int processId, out string cutscene) =>
        LocalProcesses.TryGetValue(processId, out cutscene) && !string.IsNullOrEmpty(cutscene);

    /// <summary>主机已启动这个过场（起始数据包到达了本客户端）：本地触发器让位。</summary>
    internal static void NoteHostStart(string cutsceneId)
    {
        if (!string.IsNullOrEmpty(cutsceneId)) HostStarted.Add(cutsceneId);
    }

    /// <summary>
    /// 客户端自己的原生链请求了一个过场，而 Fika 丢弃了这次调用（<c>CutsceneStartSyncPatch</c>
    /// 的 prefix）。该请求会被记住并以原生方式重新运行，除非主机先开口。
    /// </summary>
    internal static void NoteNativeRequest(string cutsceneId)
    {
        if (!Enabled || string.IsNullOrEmpty(cutsceneId)) return;
        if (Played.Contains(cutsceneId) || HostStarted.Contains(cutsceneId))
        {
            return;
        }
        if (Requests.ContainsKey(cutsceneId)) return;

        // （2026-10-21）事件驱动：请求就在这里以原生方式运行，
        // 而不再等主机数据包两秒。如果主机自己的启动先到达，主机仍然获胜（HostStarted）；
        // 而那个让门在场景开始前两秒就打开的两秒窗口已经没有了。
        var world = Comfort.Common.Singleton<GameWorld>.Instance;
        if (world?.CutscenesServerController != null)
        {
            Fire(world, cutsceneId);
            return;
        }

        Requests[cutsceneId] = UnityEngine.Time.unscaledTime;
    }

    /// <summary>
    /// prefix 读取的那一次授权：桥接即将放行本机自己的原生 StartCutscene。当
    /// <see cref="SingleLocalViewer"/> 开启时，这次调用的座位表会被收窄到本机。
    /// </summary>
    internal static bool GrantNativeStart(string cutsceneId,
        Il2CppSystem.Collections.Generic.List<int> viewers)
    {
        if (!Enabled || string.IsNullOrEmpty(cutsceneId)) return false;
        if (!string.Equals(_grant, cutsceneId, StringComparison.Ordinal)) return false;
        _grant = null;
        if (!SingleLocalViewer || viewers == null) return true;
        var local = RaidInteractionSync.LocalPlayerId;
        if (local <= 0) return true;
        // 表是就地编辑的：原生实现读取的正是调用方自己的那个列表。
        viewers.Clear();
        viewers.Add(local);
        return true;
    }

    /// <summary>原生实现已运行：本机现在拥有自己的一个进程。</summary>
    internal static void NoteLocalProcess(CutsceneProcess process)
    {
        if (process == null) return;
        var cutscene = process.currentCutsceneId;
        if (string.IsNullOrEmpty(cutscene)) return;
        Played.Add(cutscene);
        Requests.Remove(cutscene);
        LocalProcesses[process.processId] = cutscene;
        // （2026-10-21）进程在这里被登记，以便自行结束：当本地观看者看完，
        // 或当它超过了超时时间，<see cref="Tick"/> 会关闭它，
        // 这样一个原生结束永远不会到来的场景就无法保留它启动时授予的免疫。
        PendingEnds[process.processId] = new PendingLocalProcess
        {
            Cutscene = cutscene,
            Process = process,
            StartedAt = UnityEngine.Time.unscaledTime
        };
        FikaGlobals.LogInfo("[SyncDiag] local-process start pid=" + process.processId, "FikaLocalCutsceneTrigger");
    }

    /// <summary>
    /// 在本机的突袭 tick 上运行（RaidInteractionSync.Tick）。
    /// 它是这项修复的决策点：一个主机信号从未到来的请求，会被转成本机的一次原生 StartCutscene。
    /// </summary>
    internal static void Tick(GameWorld world)
    {
        // （2026-10-21）结束的那一半先运行，且与开关无关：一个已经登记的进程，即使在突袭中途关闭了
        // <see cref="UseNativeClientTrigger"/> 之后也必须被关闭。
        PumpEnds(world);
        if (!Enabled || world == null || Requests.Count == 0) return;
        var now = UnityEngine.Time.unscaledTime;
        if (now < _nextScan) return;
        _nextScan = now + 0.25f;
        foreach (var entry in new List<KeyValuePair<string, float>>(Requests))
        {
            var cutscene = entry.Key;
            if (Played.Contains(cutscene))
            {
                Requests.Remove(cutscene);
                continue;
            }
            if (HostStarted.Contains(cutscene))
            {
                Requests.Remove(cutscene);
                continue;
            }
            if (now - entry.Value < LocalTriggerDelaySeconds) continue;
            Requests.Remove(cutscene);
            Fire(world, cutscene);
        }
    }

    /// <summary>
    /// 修复本身：本机自己的原生 StartCutscene，
    /// 附上让这次调用穿过 Fika prefix 的授权。
    /// 它之后的一切都是原生的——进程、本地观看者的播放，以及带时间轴结束动作的原生结束。
    /// </summary>
    private static void Fire(GameWorld world, string cutscene)
    {
        var server = world == null ? null : world.CutscenesServerController;
        if (server == null)
        {
            return;
        }
        // 原生调用的座位表：原生列表（这个世界里所有非 AI 的存活玩家，包括主机角色）
        // 在这里构建，使得关闭 SingleLocalViewer
        // 时的行为与调用我们的那个触发器完全一致；开启（默认）时则由授权把它收窄为本机自己的玩家。
        var viewers = new Il2CppSystem.Collections.Generic.List<int>();
        var local = RaidInteractionSync.LocalPlayerId;
        // （2026-10-20）没有本地玩家 id，授权就无法把座位表收窄到
        // 1/1，进程随后会等待本机无法凑齐的座位——正是这个触发器存在要避免的死锁。
        // 跳过这一次轮询；请求留在队列里，下一次 tick 重试。
        if (local <= 0) return;
        try
        {
            var all = world.AllAlivePlayersList;
            if (all != null)
                for (var i = 0; i < all.Count; i++)
                {
                    var player = all[i];
                    if (player == null || player.IsAI) continue;
                    viewers.Add(player.PlayerId);
                }
        }
        catch (Exception)
        {
            // 只生成了一半的玩家列表，不是跳过触发器的理由：退回使用本机自己的 id。
        }
        if (viewers.Count == 0 && local > 0) viewers.Add(local);
        _grant = cutscene;
        CutsceneProcess process;
        try
        {
            process = server.StartCutscene(cutscene, viewers, false, false, 0f);
        }
        catch (Exception error)
        {
            _grant = null;
            return;
        }
        if (process == null)
        {
            _grant = null;
            return;
        }

        // （2026-10-21）这里不碰进程：原生启动的 postfix（CutsceneStartSyncPatch
        // -> RaidInteractionSync.PlayLocalNativeProcess ->
        // NoteLocalProcess）把它交给结束登记表，那是本地进程被打开和关闭的唯一位置。
    }

    /// <summary>
    /// （2026-10-21）本地观看者的时间轴已在本机显示（<c>CutsceneViewedLocalProcessPatch</c>
    /// 的 <c>TimelineShowed</c> postfix）。
    /// 本机的进程被标记为等待它的原生结束；结束本身会在 <see cref="Tick"/>
    /// 里只运行一次，因此该通知绝不会在报告它的那个原生回调内部结束一个进程。
    /// </summary>
    internal static void NoteViewed(int processId, string cutsceneId)
    {
        if (PendingEnds.Count == 0) return;
        if (processId > 0 && PendingEnds.TryGetValue(processId, out var byId))
        {
            byId.Viewed = true;
            return;
        }

        if (string.IsNullOrEmpty(cutsceneId)) return;
        foreach (var entry in PendingEnds)
        {
            if (string.Equals(entry.Value.Cutscene, cutsceneId, StringComparison.Ordinal))
            {
                entry.Value.Viewed = true;
            }
        }
    }

    /// <summary>
    /// 生命周期中结束的那一半：每个已登记且观看者已看完的进程，或超过
    /// <see cref="LocalProcessTimeoutSeconds"/> 的进程，
    /// 都被结束一次。在突袭 tick 上运行，位于请求扫描之前，因此结束绝不会被一个待处理的本地启动拖后。
    /// </summary>
    private static void PumpEnds(GameWorld world)
    {
        if (PendingEnds.Count == 0) return;
        var now = UnityEngine.Time.unscaledTime;
        List<int> ending = null;
        foreach (var entry in PendingEnds)
        {
            var pending = entry.Value;
            if (pending.Viewed || PlaybackLeftThisProcess(world, pending) ||
                now - pending.StartedAt >= LocalProcessTimeoutSeconds)
            {
                (ending ??= new List<int>()).Add(entry.Key);
            }
        }

        if (ending == null) return;
        for (var i = 0; i < ending.Count; i++)
        {
            EndLocal(world, ending[i]);
        }
    }

    /// <summary>
    /// （2026-10-27 T1）本机的播放是否留下了一个本地进程。
    /// <para>
    /// 为什么需要它。本地进程是由客户端控制器自己的 "timeline shown"
    /// 通知关闭的；当那条通知始终不来时，条目只会在
    /// <see cref="LocalProcessTimeoutSeconds"/>（六十秒）
    /// 时才结束，而在整个这段窗口里本机都 "处于过场中"：玩家站在里面，该点的转场交互不可用，
    /// 而队伍的倒计时只有二十秒——于是客户端眼看着主机在他之外完成了转移，这正是本次要修复的报告。
    /// 控制器会说出它正在播放的进程（<c>_cutsceneProcessId</c>），
    /// 而那与通知携带的是同一个事实：一旦它不再指向我们的进程，本机就不再有任何东西正在被观看。
    /// </para>
    /// <para>
    /// 条目必须先被看到正在播放（<see cref="PendingLocalProcess.SeenPlaying"/>）：
    /// 一个刚刚创建的进程还没有被交给客户端控制器，
    /// 在那一帧结束它会取消每一个场景，甚至在它们的第一张画面之前。
    /// </para>
    /// </summary>
    private static bool PlaybackLeftThisProcess(GameWorld world, PendingLocalProcess pending)
    {
        if (pending?.Process == null) return false;
        try
        {
            var client = world == null ? null : world.CutscenesClientController;
            if (client == null) return false;
            if (client._cutsceneProcessId == pending.Process.processId)
            {
                pending.SeenPlaying = true;
                return false;
            }

            return pending.SeenPlaying;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// （2026-10-27 T1）在 tick 之外，立即结束本机自己创建的每一个进程。
    /// <para>
    /// 唯一的调用者是本机的一次转移落地的那一刻（<c>ClientTransitController.HandleClientExtract</c>）：
    /// 本机的突袭在那里结束，
    /// 而一个仍然打开的进程会让玩家在关卡被替换期间留在过场里——这种进程的原生结束随后会运行在一个已经在关闭的世界之上（2026-10-21
    /// 20:57 那次通关的 NullReferenceException）。
    /// 在撤离之前结束它，也会释放它启动时授予的免疫，因此下一张地图两样都无法继承。
    /// </para>
    /// <para>幂等且受限：它只遍历本机自己登记的条目。</para>
    /// </summary>
    internal static int EndOpenLocalProcesses(string reason)
    {
        if (PendingEnds.Count == 0) return 0;
        var ending = new List<int>();
        foreach (var entry in PendingEnds)
        {
            ending.Add(entry.Key);
        }

        var world = Comfort.Common.Singleton<GameWorld>.Instance;
        for (var i = 0; i < ending.Count; i++)
        {
            EndLocal(world, ending[i], reason);
        }

        return ending.Count;
    }

    /// <summary>
    /// 本机自己创建的一个进程的原生结束（<c>EndCutscene(cutsceneId,
    /// processId, process, playersId, skip)</c>，interop
    /// :94617；skip = false，以便时间轴的结束动作——地图的门——仍然运行），
    /// 随后条目被丢弃，启动时授予的狙击手免疫被释放。两步都是幂等的，所以一个同样到达的原生结束也无害。
    /// <paramref name="forcedReason"/>
    /// 在本次结束的那一行里标明调用者自己的结束条件（一次落地的转移），这样 grep 就能把它与两个每 tick 的条件区分开。
    /// </summary>
    private static void EndLocal(GameWorld world, int processId, string forcedReason = null)
    {
        if (!PendingEnds.Remove(processId, out var pending)) return;
        LocalProcesses.Remove(processId);
        var reason = forcedReason ?? (pending.Viewed ? "viewed" : "timeout");
        var endStatus = "skipped";
        try
        {
            var server = world == null ? null : world.CutscenesServerController;
            if (server != null && pending.Process != null)
            {
                // （2026-10-21）座位表被重建为一个全新的非空列表：2026-10-21
                // 20:57 那次通关显示这次调用会抛出原生 NullReferenceException，
                // 而我们交给它的那个列表——进程自己的
                // <c>playersInCurrentView</c>——正是原生完成处理器所清空的字段。那里的
                // null 列表会在结束动作之前中止原生结束，这就是客户端一侧的场景结束从未运行它们的原因。
                var viewers = new Il2CppSystem.Collections.Generic.List<int>();
                try
                {
                    var current = pending.Process.playersInCurrentView;
                    if (current != null)
                    {
                        for (var i = 0; i < current.Count; i++) viewers.Add(current[i]);
                    }
                }
                catch (Exception) { }
                if (viewers.Count == 0 && RaidInteractionSync.LocalPlayerId > 0)
                {
                    viewers.Add(RaidInteractionSync.LocalPlayerId);
                }
                server.EndCutscene(pending.Cutscene, processId, pending.Process, viewers, false);
                endStatus = "native";
            }
            else
            {
                endStatus = pending.Process == null ? "no-process" : "no-controller";
            }
        }
        catch (Exception exception)
        {
            // 无论哪种情况条目都会被丢弃：一个超过结束时限的进程不得保留免疫。
            // 之所以报告这条消息，是因为抛异常的原生结束同样会跳过时间轴的结束动作。
            endStatus = "threw:" + exception.GetType().Name + ":" + exception.Message;
        }

        FikaGlobals.LogInfo("[SyncDiag] local-process end pid=" + processId + " reason=" + reason +
            " end=" + endStatus, "FikaLocalCutsceneTrigger");
        // 进程的原生结束由 EndCutscene 的 postfix
        // 释放；即使上面那次原生调用被跳过或抛了异常，这次调用仍保留该释放。
        RaidInteractionSync.ReleaseLocalSniperImmunity("local-process end pid=" + processId);
    }

    /// <summary>本机尚未结束的一个进程（见 <see cref="PendingEnds"/>）。</summary>
    private sealed class PendingLocalProcess
    {
        internal string Cutscene;
        internal CutsceneProcess Process;
        internal float StartedAt;
        internal bool Viewed;
        /// <summary>（2026-10-27 T1）一旦本机的客户端控制器把该进程指名它为正在播放的那个，
        /// 就置位；只有到那时，它从该字段中消失才可以结束这个条目（见
        /// <see cref="PlaybackLeftThisProcess"/>）。</summary>
        internal bool SeenPlaying;
    }
}
