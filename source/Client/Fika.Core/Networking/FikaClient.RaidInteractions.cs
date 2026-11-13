using System;
using System.Collections.Generic;
using System.Linq;
using CommonAssets.Scripts.Cutscenes;
using UnityEngine.Playables;
using EFT.GlobalEvents;
using EFT.Passcodes;
using EFT.Interactive;
using EFT;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Networking;

public sealed partial class FikaClient
{
    private void OnFinalMissionPacketReceived(FinalMissionPacket packet)
    {
        if (packet.Message != FinalMissionMessage.Complete) FikaFinalMissionDirector.Receive(packet);
    }

    private PasscodeInitPacket? _pendingPasscodes;
    private readonly Queue<PasscodeResultPacket> _pendingPasscodeResults = new();
    private readonly Queue<(object Packet, float Received)> _pendingCutscenes = new();
    private readonly Dictionary<int, string> _activeCutscenes = new();
    private readonly HashSet<int> _skippedCutscenes = new();

    /// <summary>
    /// (analysis/cutscene-client-dispatch) 本客户端的 cutscene 排空上一次拒绝运行的原因，保留它是为了让
    /// 什么都没变的一帧什么都不写。-1 = 从未上报，0 = 排空在运行。
    /// </summary>
    private int _drainBlock = -1;

    private void OnPasscodeInitPacketReceived(PasscodeInitPacket packet)
    {
        if (packet.IsRequest) return;
        _pendingPasscodes = packet;
        // BU (analysis/icebreaker-keypad-3)：本场 raid 的密码表。主机用游戏自己的
        // 生成器抽取它，本机把它应用到一个原生本地控制器上，因此从今往后由它自己的校验 —— 而不是
        // 一次往返 —— 来决定一个密码。
        KeypadCodeSync.NoteHostSnapshot(packet.Codes, packet.Entries, packet.Hints);
        // BO (analysis/icebreaker-keypad-2)：主机所在地点的 passcode id。一个必须构建自己
        // 控制器的对端正好使用这些 id，因此 raid 的两台机器命名的是同一个键盘。
        RaidInteractionSync.NoteHostPasscodeEntries(packet.Entries);
        // BB (analysis/icebreaker-keypad-door)：主机的 passcode 快照是主机自身
        // passcode 层仍存活的证明，也是本轮客户端回退中继的前置条件
        // (PasscodeSubmitRelayPatch)：没有它，中继只能把一次可读的拒绝变成静默等待。
        RaidInteractionSync.NoteHostPasscodeSnapshot(packet.Hints?.Count ?? 0);
    }

    private void OnPasscodeResultPacketReceived(PasscodeResultPacket packet)
    {
        if (_pendingPasscodeResults.Count >= 256) throw new InvalidOperationException("Passcode startup queue exceeded its limit");
        _pendingPasscodeResults.Enqueue(packet);
        
    }
    private void OnCutsceneStartPacketReceived(CutsceneStartPacket packet)
    {
        // (analysis/cutscene-client-native-trigger) 主机启动了这段 cutscene：本机上仍为它
        // 被按住的本地触发器会让位（FikaLocalCutsceneTrigger.Tick）。在到达时记录而不在
        // 排空中记录，因为否则被阻塞的排空会让本地触发器先跑。
        FikaLocalCutsceneTrigger.NoteHostStart(packet.CutsceneId);
        QueueCutscene(packet);
    }
    private void OnCutsceneSkipPacketReceived(CutsceneSkipPacket packet) => QueueCutscene(packet);
    private void OnCutsceneEndPacketReceived(CutsceneEndPacket packet) => QueueCutscene(packet);
    private void OnCutsceneTimePacketReceived(CutsceneTimePacket packet) => QueueCutscene(packet);

    private void QueueCutscene(object packet)
    {
        // 绝不在数据包处理器中抛异常。排空需要一个就绪的 timeline bank，所以一段
        // 无法播放的 cutscene 过去会顶住队列直到这里抛出异常；丢弃最旧的
        // 请求留下的是可恢复的失步，而不是一个坏掉的处理器。
        if (_pendingCutscenes.Count >= 256)
        {
            
            _pendingCutscenes.Dequeue();
        }
        _pendingCutscenes.Enqueue((packet, Time.unscaledTime));
    }

    /// <summary>
    /// 丢弃那些唯一消费者是刚刚结束的 world 的 raid 交互排空的缓冲区。否则一条
    /// 在上次排空之后到达的快照（换图 / raid 结束）会被应用到
    /// 下一场 raid 的 passcode 或 cutscene 状态上，而这正是一次重连或一次中转绝不能继承的东西。
    /// </summary>
    internal void DropRaidScopedInteractionCache()
    {
        _pendingPasscodes = null;
        _pendingPasscodeResults.Clear();
    }

    internal void ClearRaidInteractions()
    {
        _pendingPasscodes = null;
        _pendingPasscodeResults.Clear();
        _pendingCutscenes.Clear();
        _activeCutscenes.Clear();
        _skippedCutscenes.Clear();
        _drainBlock = -1;
    }

    private void ApplyPendingRaidInteractions()
    {
        var world = RaidInteractionSync.World;
        // timeline bank 必须在第一段排队的 cutscene 被排空之前链接好，否则排空会在
        // "_timelinesMapBank == null" 处返回，并连同它的门动作一起丢弃那段 cutscene。这过去是
        // 这个方法上的一个 Harmony prefix，而 IL2CPP 从未安装它（见 RaidInteractionPatches.cs），
        // 所以现在这次调用成为方法的一部分。
        CutsceneBoundary.Tick(world);
        // (analysis/cutscene-client-dispatch) 这些返回过去每一条都是静默的，于是一个从未进入
        // cutscene 的客户端没有留下任何说明原因的行：主机的数据包整场 raid 都留在队列里，
        // 而日志在 'result=ready' 之后什么都不显示。每个不同的原因一行、不加门禁；码值比较发生在
        // 构造任何字符串之前，所以什么都没变的一帧只花一次 int 比较。
        var block = !RaidInteractionSync.Ready ? 1 : world == null ? 2 : CoopHandler?.MyPlayer == null ? 3 : 0;
        if (block != 0)
        {
            return;
        }
        // BU (analysis/icebreaker-keypad-3)：这个排空服务于控制器的两种形态。已经收到主机密码表的
        // 客户端持有的是游戏自己的 LOCAL 控制器（这是想要的状态：它自己校验
        // 密码），网络控制器只是在此之前那段窗口期里的回退。触发原生
        // init 事件只留给回退路径；无论在哪种情况下，主机的答案都会被应用到本机的门
        // 上，因此在另一台机器上输入的密码仍然能打开键盘那扇门的每一份副本。
        if (world.PasscodeController?.TryCast<ClientPasscodeController>() is { } passcodes)
        {
            if (_pendingPasscodes is { } init)
            {
                var hints = new Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.List<string>>();
                foreach (var entry in init.Hints)
                {
                    var values = new Il2CppSystem.Collections.Generic.List<string>();
                    foreach (var hint in entry.Value) values.Add(hint);
                    hints.Add(entry.Key, values);
                }
                GlobalEventsController.CreateEvent<PasscodeInitSyncEvent>().Invoke(CoopHandler.MyPlayer.RaidId,
                    hints.Cast<Il2CppSystem.Collections.Generic.IReadOnlyDictionary<string, Il2CppSystem.Collections.Generic.List<string>>>());
                _pendingPasscodes = null;
                // BB (analysis/icebreaker-keypad-door)：从此刻起，主机的提示集合已经在
                // 本机的控制器里 —— 这一行把「主机从未回答」和「答案已被应用」区分开。
                RaidInteractionSync.NotePasscodeInitApplied(init.Hints?.Count ?? 0);
            }
            while (_pendingPasscodeResults.TryDequeue(out var result))
            {
                GlobalEventsController.CreateEvent<PasscodeAttemptResultEvent>().Invoke(RaidInteractionSync.ToRaidId(result.NetId),
                    result.PasscodeId, result.Successful, result.TerminalNetId);
                
                // 观察者结果处理只播放终止音效。也要把主机的
                // 解锁应用到每个对端，包括此终端上的额外门。
                if (result.Successful && passcodes._passcodeTerminals.TryGetValue(result.PasscodeId, out var terminals))
                    foreach (var terminal in terminals)
                    {
                        if (terminal.NetId != result.TerminalNetId) continue;
                        Unlock(terminal.ObjectToUnlock);
                        if (terminal.AdditionalObjectsToUnlock != null)
                            foreach (var door in terminal.AdditionalObjectsToUnlock) Unlock(door);
                    }
            }
        }

        var controller = world.CutscenesClientController;
        var cutsceneBlock = controller?._timelinesMapBank == null ? 5 : !CoopHandler.ShouldSync ? 6 : 0;
        if (cutsceneBlock != 0)
        {
            return;
        }
        while (_pendingCutscenes.TryDequeue(out var pending))
        {
            switch (pending.Packet)
            {
                case CutsceneStartPacket start:
                    if (_activeCutscenes.ContainsKey(start.ProcessId)) break;
                    _activeCutscenes.Add(start.ProcessId, start.CutsceneId);
                    // (analysis/cutscene-client-native-trigger) 本机可能已经通过自己的原生触发器
                    // 在播放同一段 cutscene，因为主机没能及时派发它。
                    // 在这里再触发一次 start 事件会让同一台机器把一个场景播两遍，所以只登记主机的
                    // 进程 —— 正因如此，它之后每一个 skip/end/time 数据包才能再次
                    // 到达本机的这个场景。
                    if (FikaLocalCutsceneTrigger.HasPlayedLocally(start.CutsceneId))
                    {
                        
                        break;
                    }
                    // 原生 StartCutscene 始终发出 currentSkip=true。ForceMode 是
                    // 主机的调度选项，不是原生事件的 currentSkip 标志。
                    var delay = Math.Max(0, Time.unscaledTime - pending.Received);
                    var viewers = start.NetIds.Select(RaidInteractionSync.ToPlayerId).ToArray();
                    var localPlayerId = RaidInteractionSync.LocalPlayerId;
                    var addressed = localPlayerId > 0 && viewers.Contains(localPlayerId);
                    // (analysis/cutscene-client-dispatch) 主机发来的座位表是客户端能够自己读取的
                    // 「主机有没有把我算进来」的唯一部分，所以那个分支的判决写在这里，
                    // 而不是写在日志够不到的那台机器上。当表里没有
                    // 本机、而下面的修复又开启时，座位就在这里补上：表是在主机上
                    // 由本地玩家列表构建的，本机的原生处理器会拿它做过滤，
                    // 而主机之后发送的任何内容都无法让一个没有座位的对端播放。
                    if (!addressed && CutsceneBoundary.JoinSharedCutscene && localPlayerId > 0)
                    {
                        var extended = new int[viewers.Length + 1];
                        Array.Copy(viewers, extended, viewers.Length);
                        extended[viewers.Length] = localPlayerId;
                        viewers = extended;
                        
                    }
                    else
                    {
                        
                    }
                    GlobalEventsController.CreateEvent<CutSceneStartEvent>().Invoke(start.CutsceneId, start.ProcessId,
                        viewers, start.InitTime + delay, true);
                    
                    break;
                case CutsceneSkipPacket skip:
                    if (!_activeCutscenes.ContainsKey(skip.ProcessId)) break;
                    if (skip.Skip)
                    {
                        if (_skippedCutscenes.Add(skip.ProcessId))
                            GlobalEventsController.CreateEvent<CutsceneSkipEventFromServer>().Invoke(skip.ProcessId);
                    }
                    else GlobalEventsController.CreateEvent<CutsceneSkipIntentionEventFromServer>().Invoke(skip.ProcessId,
                        skip.PlayersCount, skip.SkippingNetIds.Select(RaidInteractionSync.ToPlayerId).ToArray());
                    break;
                case CutsceneTimePacket time:
                    if (_activeCutscenes.TryGetValue(time.ProcessId, out var active) && active == time.CutsceneId)
                        GlobalEventsController.CreateEvent<CorrectCutsceneTimerEvent>().Invoke(
                            time.ElapsedTime + Math.Max(0, Time.unscaledTime - pending.Received), time.CutsceneId, time.ProcessId);
                    break;
                case CutsceneEndPacket end:
                    if (!_activeCutscenes.Remove(end.ProcessId, out var cutsceneId)) break;
                    // (2026-10-20) 原版会在 cutscene 的进程结束时丢弃它授予的狙击手免疫。
                    // 本机的进程在此结束，而主机只释放它自己的参与者，所以一个
                    // 客户端会在 raid 余下的时间里保留该条目 —— 这就是「客户端在
                    // 引擎室 cutscene 之后仍然无敌」的报告。该释放只触及本机自己的玩家。
                    GlobalEventsController.CreateEvent<CutSceneEndEvent>().Invoke(cutsceneId);
                    // 主机可能结束一个本机从未收到原生 EndCutscene 的进程（例如
                    // 一次主机侧的提前结束，或一个被强制关闭的进程）。不要依赖原生 end 动作
                    // 来撤销 cutscene 保护：也要显式地恢复本地玩家。
                    RaidInteractionSync.RestoreLocalDamageAfterCutscene("CutsceneEndPacket");
                    RaidInteractionSync.ReleaseLocalSniperImmunity("CutsceneEndPacket");
                    // 确保对应的活动 director 被结束，即使主机
                    // 提前结束了。skip 事件自己负责其异步淡出清理。
                    if (!_skippedCutscenes.Remove(end.ProcessId) && controller._cutsceneProcessId == end.ProcessId &&
                        controller._cutsceneId == cutsceneId)
                    {
                        if (TryGetDirector(controller, cutsceneId, out var director))
                        {
                            controller.StopCutscene(director);
                            
                        }
                        
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// 客户端控制器以 rig 所在的场景为键保存它可播放的 director，而
    /// 数据包携带的是 cutscene id；两者都会被尝试，这样一段主机已结束的 cutscene 总能在
    /// 本地被停止（一个仍在运行的 director 会把玩家锁在 cutscene 摄像机里）。
    /// </summary>
    private static bool TryGetDirector(CutscenesClientController controller, string cutsceneId, out PlayableDirector director)
    {
        director = null;
        if (controller._directorsByScene == null || string.IsNullOrEmpty(cutsceneId)) return false;
        if (controller._directorsByScene.TryGetValue(cutsceneId, out director) && director != null) return true;
        var timelines = controller._timelinesMapBank?.timelines;
        for (var i = 0; i < (timelines?.Count ?? 0); i++)
        {
            var timeline = timelines[i];
            if (timeline.cutsceneId != cutsceneId || string.IsNullOrEmpty(timeline.sceneName)) continue;
            if (controller._directorsByScene.TryGetValue(timeline.sceneName, out director) && director != null) return true;
        }
        if (controller._currentTimelineData.cutsceneId == cutsceneId &&
            !string.IsNullOrEmpty(controller._currentTimelineData.sceneName) &&
            controller._directorsByScene.TryGetValue(controller._currentTimelineData.sceneName, out director) && director != null)
            return true;
        director = null;
        return false;
    }

    private static void Unlock(WorldInteractiveObject door)
    {
        if (door != null && door.DoorState == EDoorState.Locked) door.Interact(EInteractionType.Unlock);
    }
}
