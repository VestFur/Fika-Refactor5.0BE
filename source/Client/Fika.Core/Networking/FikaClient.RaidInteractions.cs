using System;
using System.Collections.Generic;
using System.Linq;
using CommonAssets.Scripts.Cutscenes;
using EFT.GlobalEvents;
using EFT.Passcodes;
using EFT.Interactive;
using EFT;
using Fika.Core.Main.Components;
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

    private void OnPasscodeInitPacketReceived(PasscodeInitPacket packet)
    {
        if (!packet.IsRequest) _pendingPasscodes = packet;
    }

    private void OnPasscodeResultPacketReceived(PasscodeResultPacket packet)
    {
        if (_pendingPasscodeResults.Count >= 256) throw new InvalidOperationException("Passcode startup queue exceeded its limit");
        _pendingPasscodeResults.Enqueue(packet);
    }
    private void OnCutsceneStartPacketReceived(CutsceneStartPacket packet) => QueueCutscene(packet);
    private void OnCutsceneSkipPacketReceived(CutsceneSkipPacket packet) => QueueCutscene(packet);
    private void OnCutsceneEndPacketReceived(CutsceneEndPacket packet) => QueueCutscene(packet);
    private void OnCutsceneTimePacketReceived(CutsceneTimePacket packet) => QueueCutscene(packet);

    private void QueueCutscene(object packet)
    {
        if (_pendingCutscenes.Count >= 256) throw new InvalidOperationException("Cutscene startup queue exceeded its limit");
        _pendingCutscenes.Enqueue((packet, Time.unscaledTime));
    }

    internal void ClearRaidInteractions()
    {
        _pendingPasscodes = null;
        _pendingPasscodeResults.Clear();
        _pendingCutscenes.Clear();
        _activeCutscenes.Clear();
        _skippedCutscenes.Clear();
    }

    private void ApplyPendingRaidInteractions()
    {
        var world = RaidInteractionSync.World;
        if (!RaidInteractionSync.Ready || world == null || CoopHandler?.MyPlayer == null) return;
        if (world.PasscodeController?.TryCast<ClientNetworkPasscodeController>() is { } passcodes)
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
        if (controller?._timelinesMapBank == null || !CoopHandler.ShouldSync) return;
        while (_pendingCutscenes.TryDequeue(out var pending))
        {
            switch (pending.Packet)
            {
                case CutsceneStartPacket start:
                    if (_activeCutscenes.ContainsKey(start.ProcessId)) break;
                    _activeCutscenes.Add(start.ProcessId, start.CutsceneId);
                    // Native StartCutscene always emits currentSkip=true. ForceMode is
                    // a host scheduling option, not the native event's currentSkip flag.
                    GlobalEventsController.CreateEvent<CutSceneStartEvent>().Invoke(start.CutsceneId, start.ProcessId,
                        start.NetIds.Select(RaidInteractionSync.ToPlayerId).ToArray(),
                        start.InitTime + Math.Max(0, Time.unscaledTime - pending.Received), true);
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
                    if (!_skippedCutscenes.Remove(end.ProcessId) && controller._cutsceneProcessId == end.ProcessId && controller._cutsceneId == cutsceneId &&
                        controller._directorsByScene.TryGetValue(cutsceneId, out var director))
                        controller.StopCutscene(director);
                    break;
            }
        }
    }

    private static void Unlock(WorldInteractiveObject door)
    {
        if (door != null && door.DoorState == EDoorState.Locked) door.Interact(EInteractionType.Unlock);
    }
}
