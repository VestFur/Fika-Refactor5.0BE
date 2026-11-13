using System;
using EFT.Passcodes;
using EFT.Interactive;
using Fika.Core.Main.Components;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Networking;

public sealed partial class FikaServer
{
    private void OnFinalMissionPacketReceived(FinalMissionPacket packet, NetPeer peer) =>
        FikaFinalMissionDirector.HandleCompletion(packet, peer.Player, peer);

    private void OnFinalMissionQuestPacketReceived(FinalMissionQuestPacket packet, NetPeer peer) =>
        FikaFinalMissionDirector.ReceiveQuestStates(packet, peer.Player);

    private void OnPasscodeInitPacketReceived(PasscodeInitPacket packet, NetPeer peer)
    {
        if (packet.IsRequest) RaidInteractionSync.RequestSnapshot(peer);
    }

    private void OnPasscodeAttemptPacketReceived(PasscodeAttemptPacket packet, NetPeer peer)
    {
        var player = peer.Player;
        if (player == null || packet.NetId != player.NetId || player.HealthController?.IsAlive != true ||
            string.IsNullOrEmpty(packet.PasscodeId) || packet.Input == null || packet.Input.Length > 128 ||
            !RaidInteractionSync.Ready || RaidInteractionSync.World?.PasscodeController?.TryCast<ClientLocalPasscodeController>() is not { } controller)
            return;
        if (!controller._passcodeTerminals.TryGetValue(packet.PasscodeId, out var terminals)) return;
        foreach (var terminal in terminals)
        {
            if (terminal == null || terminal.NetId != packet.TerminalNetId) continue;
            if (!terminal.IsInteractable || Vector3.Distance(player.Position, terminal.transform.position) > Math.Max(3f, terminal._maxDistance))
            {
                var failure = new PasscodeResultPacket { NetId = player.NetId, PasscodeId = packet.PasscodeId,
                    TerminalNetId = packet.TerminalNetId, Successful = false };
                SendDataToPeer(ref failure, DeliveryMethod.ReliableOrdered, peer);
                return;
            }
            // Use the same native validation, unlock and interaction-result path as the host.
            // Its postfix broadcasts the actual result, including failed attempts.
            controller.HandleSubmitPasscode(player, terminal, packet.Input);
            // The host has no password panel subscribed for this observed player.
            var successful = controller._generatedPasscodes.TryGetValue(packet.PasscodeId, out var expected) &&
                string.Equals(expected, packet.Input, StringComparison.Ordinal);
            terminal.PlayResultSound(successful, false);
            return;
        }
    }

    private void OnCutsceneSkipIntentionPacketReceived(CutsceneSkipIntentionPacket packet, NetPeer peer)
    {
        if (peer.Player != null && peer.Player.NetId == packet.NetId) RaidInteractionSync.HandleSkip(packet, peer.Player);
    }

    private void OnCutsceneViewCompletePacketReceived(CutsceneViewCompletePacket packet, NetPeer peer)
    {
        if (peer.Player != null && peer.Player.NetId == packet.NetId) RaidInteractionSync.HandleComplete(packet, peer.Player);
    }

    private void OnDialogEntryPacketReceived(DialogEntryPacket packet, NetPeer peer)
    {
        if (packet.Operation == EDialogEntryOperation.RequestFullState)
        {
            SendGenericPacketToPeer(EGenericSubPacketType.DialogEntry,
                DialogEntryPacket.FullState(new(DialogEntryOccupancy.Current)), peer);
            return;
        }
        var player = peer.Player;
        if (player == null || packet.OccupantId != player.NetId || string.IsNullOrEmpty(packet.PointId)) return;
        if (packet.Operation == EDialogEntryOperation.Release)
        {
            DialogEntryOccupancy.HandleReleaseRequest(packet.PointId, player.NetId);
            return;
        }
        if (packet.Operation != EDialogEntryOperation.Claim) return;
        if (player.HealthController?.IsAlive == true && !CoopHandler.ExtractedPlayers.Contains(player.NetId))
        {
            foreach (var point in UnityEngine.Object.FindObjectsOfType<RaidDialogEntryPoint>(true))
                if (point.PointId == packet.PointId && point.isActiveAndEnabled && point.DistanceCheck(player.Position))
                {
                    DialogEntryOccupancy.HandleClaimRequest(packet.PointId, player.NetId);
                    break;
                }
        }
        // A denial must answer too, otherwise the client's native interaction would hang.
        DialogEntryOccupancy.Current.TryGetValue(packet.PointId, out var occupant);
        SendGenericPacketToPeer(EGenericSubPacketType.DialogEntry, DialogEntryPacket.Occupancy(packet.PointId, occupant), peer);
    }
}
