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
    /// BJ (analysis/terminal-rauf-card)：某个节点报告有玩家通过了终端刷卡检查。
    /// 主机拥有答案（它自己的世界中持有做出判定的哨兵），因此它把该事实应用到
    /// 自己的哨兵，并转交给其他每个节点——这正是让每台机器达成一致的原因。
    /// </summary>
    private void OnSentryAccessPacketReceived(SentryAccessPacket packet, NetPeer peer) =>
        Main.Components.FikaSentryAccess.Receive(packet.ProfileId, packet.RaidId, packet.Granted, true,
            packet.HostAccessYes, packet.HostAccessNo, packet.HostAllies);

    private void OnPasscodeInitPacketReceived(PasscodeInitPacket packet, NetPeer peer)
    {
        if (packet.IsRequest) RaidInteractionSync.RequestSnapshot(peer);
    }

    // BB (analysis/icebreaker-keypad-door)：活着的参与者发出的每个格式正确的尝试现在都会得到应答。
    // 过去会静默返回的两条路径（本主机不知道这样的通行码 id、没有终端持有该尝试的
    // net id）会让节点的原生面板停在等待状态、键盘被锁——而原生
    // 服务器总会应答一次尝试，因此拒绝也会作为拒绝被应答。每个分支写一行
    // [KeypadDiag] 日志，这是唯一能读到主机侧键盘情况的地方。
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
            // 使用与主机相同的原生校验、解锁与交互结果路径。
            // 它的 postfix 会广播实际结果，包括失败的尝试。
            controller.HandleSubmitPasscode(player, terminal, packet.Input);
            // 主机没有为该观察玩家订阅密码面板。
            var successful = controller._generatedPasscodes.TryGetValue(packet.PasscodeId, out var expected) &&
                string.Equals(expected, packet.Input, StringComparison.Ordinal);
            
            terminal.PlayResultSound(successful, false);
            return;
        }

        
        AnswerAttempt(peer, player.NetId, packet);
    }

    /// <summary>对本主机无法接受的尝试给出的原生应答，这样节点的面板永远不会无限等待。</summary>
    private void AnswerAttempt(NetPeer peer, int netId, PasscodeAttemptPacket packet)
    {
        var failure = new PasscodeResultPacket { NetId = netId, PasscodeId = packet.PasscodeId,
            TerminalNetId = packet.TerminalNetId, Successful = false };
        SendDataToPeer(ref failure, DeliveryMethod.ReliableOrdered, peer);
    }

    /// <summary>本主机已知的通行码 id，这样拒绝时能指明它本可接受什么。</summary>

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
        // 拒绝也必须应答，否则客户端的原生交互会挂起。
        DialogEntryOccupancy.Current.TryGetValue(packet.PointId, out var occupant);
        SendGenericPacketToPeer(EGenericSubPacketType.DialogEntry, DialogEntryPacket.Occupancy(packet.PointId, occupant), peer);
    }
}
