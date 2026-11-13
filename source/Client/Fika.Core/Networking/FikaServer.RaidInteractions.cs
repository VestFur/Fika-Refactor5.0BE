using System;
using EFT.Passcodes;
using EFT.Interactive;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
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

    /// <summary>
    /// BJ (analysis/terminal-rauf-card): a peer reports that a player passed the terminal card check. The
    /// host owns the answer (its own world holds the sentry whose rule decides), so it applies the fact to
    /// its own sentries and hands it to every other peer - which is what makes every machine agree.
    /// </summary>
    private void OnSentryAccessPacketReceived(SentryAccessPacket packet, NetPeer peer) =>
        Main.Components.FikaSentryAccess.Receive(packet.ProfileId, packet.RaidId, packet.Granted, true,
            packet.HostAccessYes, packet.HostAccessNo, packet.HostAllies);

    private void OnPasscodeInitPacketReceived(PasscodeInitPacket packet, NetPeer peer)
    {
        if (packet.IsRequest) RaidInteractionSync.RequestSnapshot(peer);
    }

    // BB (analysis/icebreaker-keypad-door): every well-formed attempt of a living participant is answered now.
    // The two paths that used to return silently (this host knows no such passcode id, no terminal carries the
    // net id of the attempt) left the peer's native panel in its waiting state with a locked keypad - the native
    // server always answers an attempt, so a rejection is answered as a rejection. Each branch writes one
    // [KeypadDiag] line, which is the only place the host's side of a keypad can be read from.
    private void OnPasscodeAttemptPacketReceived(PasscodeAttemptPacket packet, NetPeer peer)
    {
        var player = peer.Player;
        if (player == null || packet.NetId != player.NetId || player.HealthController?.IsAlive != true ||
            string.IsNullOrEmpty(packet.PasscodeId) || packet.Input == null || packet.Input.Length > 128)
        {
            
            return;
        }

        if (RaidInteractionSync.World?.PasscodeController?.TryCast<ClientLocalPasscodeController>() is not { } controller)
        {
            
            AnswerAttempt(peer, player.NetId, packet);
            return;
        }

        if (!controller._passcodeTerminals.TryGetValue(packet.PasscodeId, out var terminals))
        {
            
            AnswerAttempt(peer, player.NetId, packet);
            return;
        }

        foreach (var terminal in terminals)
        {
            if (terminal == null || terminal.NetId != packet.TerminalNetId) continue;
            if (!terminal.IsInteractable || Vector3.Distance(player.Position, terminal.transform.position) > Math.Max(3f, terminal._maxDistance))
            {
                
                AnswerAttempt(peer, player.NetId, packet);
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

        
        AnswerAttempt(peer, player.NetId, packet);
    }

    /// <summary>The native answer to an attempt this host cannot accept, so a peer's panel never waits forever.</summary>
    private void AnswerAttempt(NetPeer peer, int netId, PasscodeAttemptPacket packet)
    {
        var failure = new PasscodeResultPacket { NetId = netId, PasscodeId = packet.PasscodeId,
            TerminalNetId = packet.TerminalNetId, Successful = false };
        SendDataToPeer(ref failure, DeliveryMethod.ReliableOrdered, peer);
    }

    /// <summary>The passcode ids this host knows, so a rejection names what it could have accepted.</summary>
    private static string KnownPasscodeIds(ClientLocalPasscodeController controller)
    {
        try
        {
            var ids = new System.Text.StringBuilder();
            var count = 0;
            foreach (var entry in controller._passcodeTerminals)
            {
                if (count > 0) ids.Append(',');
                ids.Append(entry.Key);
                if (++count >= 8) break;
            }

            return count == 0 ? "none" : ids.ToString();
        }
        catch (Exception)
        {
            return "?";
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
