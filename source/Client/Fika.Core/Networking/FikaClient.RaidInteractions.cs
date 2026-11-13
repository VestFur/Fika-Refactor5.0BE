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
    /// (analysis/cutscene-client-dispatch) The last reason this client's cutscene drain refused to run, kept so a
    /// frame that changes nothing writes nothing. -1 = never reported, 0 = the drain runs.
    /// </summary>
    private int _drainBlock = -1;

    private void OnPasscodeInitPacketReceived(PasscodeInitPacket packet)
    {
        if (packet.IsRequest) return;
        _pendingPasscodes = packet;
        // BU (analysis/icebreaker-keypad-3): the code table of this raid. The host draws it with the game's own
        // generator and this machine applies it to a native local controller, so its own validation - and not a
        // round trip - decides about a code from here on.
        KeypadCodeSync.NoteHostSnapshot(packet.Codes, packet.Entries, packet.Hints);
        // BO (analysis/icebreaker-keypad-2): the passcode ids of the host's location. A peer that has to build its
        // own controller uses exactly these ids, so both machines of the raid name the same keypad.
        RaidInteractionSync.NoteHostPasscodeEntries(packet.Entries);
        // BB (analysis/icebreaker-keypad-door): the host's passcode snapshot is the proof that the host's own
        // passcode layer is alive and the precondition of the client fallback relay of this round
        // (PasscodeSubmitRelayPatch): without it a relay could only turn a readable refusal into a silent wait.
        RaidInteractionSync.NoteHostPasscodeSnapshot(packet.Hints?.Count ?? 0);
    }

    private void OnPasscodeResultPacketReceived(PasscodeResultPacket packet)
    {
        if (_pendingPasscodeResults.Count >= 256) throw new InvalidOperationException("Passcode startup queue exceeded its limit");
        _pendingPasscodeResults.Enqueue(packet);
        
    }
    private void OnCutsceneStartPacketReceived(CutsceneStartPacket packet)
    {
        // (analysis/cutscene-client-native-trigger) The host started this cutscene: a local trigger of this machine
        // that is still held back for it stands down (FikaLocalCutsceneTrigger.Tick). Recorded on arrival and not in
        // the drain, because a blocked drain would otherwise let the local trigger run first.
        FikaLocalCutsceneTrigger.NoteHostStart(packet.CutsceneId);
        QueueCutscene(packet);
    }
    private void OnCutsceneSkipPacketReceived(CutsceneSkipPacket packet) => QueueCutscene(packet);
    private void OnCutsceneEndPacketReceived(CutsceneEndPacket packet) => QueueCutscene(packet);
    private void OnCutsceneTimePacketReceived(CutsceneTimePacket packet) => QueueCutscene(packet);

    private void QueueCutscene(object packet)
    {
        // Never throw from a packet handler. The drain needs a ready timeline bank, so an
        // unplayable cutscene used to back the queue up until this threw; dropping the oldest
        // request leaves a recoverable desynchronisation instead of a broken handler.
        if (_pendingCutscenes.Count >= 256)
        {
            
            _pendingCutscenes.Dequeue();
        }
        _pendingCutscenes.Enqueue((packet, Time.unscaledTime));
    }

    /// <summary>
    /// Drops the buffers whose only consumer is the raid-interaction drain of the world that just ended. A
    /// snapshot that arrived after the last drain (world swap / raid end) would otherwise be applied to the
    /// next raid's passcode or cutscene state, which is exactly what a reconnect or a transit must not inherit.
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
        // The timeline bank has to be linked before the first queued cutscene is drained, or the drain returns
        // on "_timelinesMapBank == null" and drops that cutscene together with its door actions. This used to be
        // a Harmony prefix on this very method, which IL2CPP never installed (see RaidInteractionPatches.cs),
        // so the call is part of the method now.
        CutsceneBoundary.Tick(world);
        // (analysis/cutscene-client-dispatch) Every one of these returns used to be silent, so a client that never
        // entered a cutscene left no line saying why: the host's packet stayed in the queue for the whole raid and
        // the log showed nothing after 'result=ready'. One ungated line per distinct reason; the code is compared
        // before any string is built, so a frame that changes nothing costs one int compare.
        var block = !RaidInteractionSync.Ready ? 1 : world == null ? 2 : CoopHandler?.MyPlayer == null ? 3 : 0;
        if (block != 0)
        {
            return;
        }
        // BU (analysis/icebreaker-keypad-3): this drain serves both shapes of the controller. A client that
        // already received the host's table holds the game's own LOCAL controller (the wanted state: it checks
        // codes itself), the network controller is only the fallback for the window before that. Raising the
        // native init event stays for the fallback; the answer of the host is applied to this machine's doors
        // in both cases, so a code typed on another machine still opens every copy of the keypad's door.
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
                // BB (analysis/icebreaker-keypad-door): the host's hint set is inside this machine's controllers
                // from here on - the line that separates "the host never answered" from "the answer was applied".
                RaidInteractionSync.NotePasscodeInitApplied(init.Hints?.Count ?? 0);
            }
            while (_pendingPasscodeResults.TryDequeue(out var result))
            {
                GlobalEventsController.CreateEvent<PasscodeAttemptResultEvent>().Invoke(RaidInteractionSync.ToRaidId(result.NetId),
                    result.PasscodeId, result.Successful, result.TerminalNetId);
                
                // Observer result handling only plays terminal sounds. Apply the host's
                // unlock to every peer too, including additional doors on this terminal.
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
                    // (analysis/cutscene-client-native-trigger) This machine may already play the very same
                    // cutscene through its own native trigger, because the host never dispatched it in time.
                    // Raising a second start event here would play one scene twice on one machine, so the host's
                    // process is only registered - which is what lets every later skip/end/time packet of it reach
                    // this machine's scene again.
                    if (FikaLocalCutsceneTrigger.HasPlayedLocally(start.CutsceneId))
                    {
                        
                        break;
                    }
                    // Native StartCutscene always emits currentSkip=true. ForceMode is
                    // a host scheduling option, not the native event's currentSkip flag.
                    var delay = Math.Max(0, Time.unscaledTime - pending.Received);
                    var viewers = start.NetIds.Select(RaidInteractionSync.ToPlayerId).ToArray();
                    var localPlayerId = RaidInteractionSync.LocalPlayerId;
                    var addressed = localPlayerId > 0 && viewers.Contains(localPlayerId);
                    // (analysis/cutscene-client-dispatch) The seat table the host sent is the only part of
                    // "did the host include me" a client can read by itself, so the verdict of that branch is
                    // written here and not on the machine whose log is out of reach. When the table does not
                    // hold this machine and the repair below is on, the seat is completed here: the table is
                    // built on the host from a local player list, the native handler of this machine filters
                    // against it, and nothing the host sends afterwards can make an unseated peer play.
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
                    GlobalEventsController.CreateEvent<CutSceneEndEvent>().Invoke(cutsceneId);
                    // Ensure the matching active director is finished, even when the
                    // host ended early. The skip event owns its asynchronous fade cleanup.
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
    /// The client controller keeps its playable directors keyed by the scene a rig lives in, while
    /// the packets carry the cutscene id; both are tried so a cutscene the host ended can always be
    /// stopped locally (a director left running keeps the player locked in the cutscene camera).
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
