using System;
using System.Collections.Generic;
using System.Linq;
using CommonAssets.Scripts.Cutscenes;
using EFT;
using EFT.GlobalEvents;
using EFT.Passcodes;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Main.Components;

/// <summary>Owns native event subscriptions for one raid; transport IDs are always Fika NetIds.</summary>
internal static class RaidInteractionSync
{
    internal static GameWorld World { get; private set; }
    internal static bool Ready { get; private set; }
    private static readonly List<Il2CppSystem.Action> Unsubscribe = new();
    private static readonly Dictionary<int, CutsceneStartPacket> Cutscenes = new();
    private static readonly HashSet<(int Process, int Player)> Completed = new();
    private static readonly Dictionary<int, HashSet<int>> Votes = new();
    private static readonly HashSet<NetPeer> PendingInit = new();
    private static readonly HashSet<int> DisconnectedViewers = new();
    private static bool _requested;
    private static IntPtr _configuredPasscodes;

    private static IFikaNetworkManager Manager => FikaSingleton<IFikaNetworkManager>.Instance;

    internal static void Bind(GameWorld world)
    {
        if (World?.Pointer == world.Pointer) return;
        if (World != null) Release(World);
        World = world;
        DialogEntryOccupancy.Bind(world.RaidDialogEntryController);
        Unsubscribe.Add(GlobalEventsController.Instance.SubscribeOnEvent<PasscodeAttemptEvent>(
            (Action<PasscodeAttemptEvent>)OnPasscodeAttempt));
        Unsubscribe.Add(GlobalEventsController.Instance.SubscribeOnEvent<CutsceneSkipIntentionEventFromClient>(
            (Action<CutsceneSkipIntentionEventFromClient>)OnSkipIntention));
        Unsubscribe.Add(GlobalEventsController.Instance.SubscribeOnEvent<SendEndCutsceneOnServerEvent>(
            (Action<SendEndCutsceneOnServerEvent>)OnViewComplete));
        Unsubscribe.Add(GlobalEventsController.Instance.SubscribeOnEvent<CorrectCutsceneTimerEvent>(
            (Action<CorrectCutsceneTimerEvent>)OnCutsceneTime));
    }

    internal static void LevelReady(GameWorld world)
    {
        if (World?.Pointer != world.Pointer) return;
        ConfigureControllers();
        Ready = true;
    }

    private static void ConfigureControllers()
    {
        var world = World;
        if (FikaBackendUtils.IsClient && world.PasscodeController?.TryCast<ClientLocalPasscodeController>() is { } local &&
            local.Pointer != _configuredPasscodes)
        {
            var settings = local._passcodeLocationSettings;
            local.Dispose();
            world.PasscodeController = new ClientNetworkPasscodeController(settings);
            _configuredPasscodes = world.PasscodeController.Pointer;
            _requested = false;
        }
        // Fika carries the offline client's native events over its own connection.
        // The native client must not perform single-player skip/completion locally.
        if (world.CutscenesClientController != null) world.CutscenesClientController._offlineMode = false;
        if (world.CutscenesServerController != null) world.CutscenesServerController._offlineMode = false;
    }

    internal static void Release(GameWorld world)
    {
        if (World?.Pointer != world.Pointer) return;
        FikaFinalMissionDirector.Reset();
        foreach (var unsubscribe in Unsubscribe) unsubscribe.Invoke();
        Unsubscribe.Clear();
        DialogEntryOccupancy.Reset();
        Cutscenes.Clear();
        Completed.Clear();
        Votes.Clear();
        PendingInit.Clear();
        DisconnectedViewers.Clear();
        FikaSingleton<FikaClient>.Instance?.ClearRaidInteractions();
        World = null;
        Ready = _requested = false;
        _configuredPasscodes = IntPtr.Zero;
    }

    internal static void Tick()
    {
        if (World == null) return;
        if (Ready) ConfigureControllers();
        DialogEntryOccupancy.Tick();
        if (!Ready || Manager?.CoopHandler?.MyPlayer == null || !Manager.CoopHandler.ShouldSync) return;
        if (FikaBackendUtils.IsClient && !_requested && Manager.CoopHandler.MyPlayer != null)
        {
            _requested = true;
            var request = new PasscodeInitPacket(true);
            Manager.SendData(ref request, DeliveryMethod.ReliableOrdered);
        }
        if (FikaBackendUtils.IsServer)
        {
            foreach (var peer in PendingInit.ToArray())
            {
                if (peer.ConnectionState != ConnectionState.Connected) PendingInit.Remove(peer);
                else if (peer.Player != null) { PendingInit.Remove(peer); SendSnapshot(peer); }
            }
        }
    }

    internal static int ToRaidId(int netId) => netId == 0 ? 0 :
        Manager?.CoopHandler?.Players != null && Manager.CoopHandler.Players.TryGetValue(netId, out var player)
            ? player.RaidId : netId;

    internal static int ToNetId(int raidId)
    {
        if (raidId == 0) return 0;
        if (Manager?.CoopHandler?.Players != null)
            foreach (var player in Manager.CoopHandler.Players.Values)
                if (player.RaidId == raidId) return player.NetId;
        return raidId;
    }

    internal static int ToPlayerId(int netId) =>
        Manager?.CoopHandler?.Players != null && Manager.CoopHandler.Players.TryGetValue(netId, out var player)
            ? player.PlayerId : netId;

    private static int PlayerIdToNetId(int playerId)
    {
        if (Manager?.CoopHandler?.Players != null)
            foreach (var player in Manager.CoopHandler.Players.Values)
                if (player.PlayerId == playerId) return player.NetId;
        return playerId;
    }

    internal static void RequestSnapshot(NetPeer peer)
    {
        if (!Ready || peer.Player == null) PendingInit.Add(peer);
        else SendSnapshot(peer);
    }

    internal static void SendSnapshot(NetPeer peer)
    {
        var server = FikaSingleton<FikaServer>.Instance;
        if (World == null || server == null || peer.Player == null) return;
        DisconnectedViewers.Remove(peer.Player.NetId);
        var init = new PasscodeInitPacket(false) { Hints = new() };
        if (World.PasscodeController?.TryCast<ClientLocalPasscodeController>() is { } local)
            foreach (var entry in local._generatedHints)
                init.Hints.Add(entry.Key, entry.Value.ToArray());
        server.SendDataToPeer(ref init, DeliveryMethod.ReliableOrdered, peer);
        // An assigned final ending supersedes earlier shared scenes on reconnect.
        if (FikaFinalMissionDirector.SendSnapshot(peer)) return;
        foreach (var start in Cutscenes.Values.ToArray())
        {
            if (!start.NetIds.Contains(peer.Player.NetId) || Completed.Contains((start.ProcessId, peer.Player.NetId))) continue;
            var snapshot = start;
            if (World.CutscenesServerController?._cutsceneProcesses.TryGetValue(start.ProcessId, out var process) == true)
                snapshot.InitTime = Math.Max(start.InitTime, process.afterStartTimeElapsed);
            server.SendDataToPeer(ref snapshot, DeliveryMethod.ReliableOrdered, peer);
            if (Votes.TryGetValue(start.ProcessId, out var votes))
            {
                var eligible = start.NetIds.Where(id => !Completed.Contains((start.ProcessId, id)) && IsActiveViewer(id)).ToArray();
                var vote = new CutsceneSkipPacket { ProcessId = start.ProcessId,
                    PlayersCount = eligible.Length, SkippingNetIds = votes.Where(eligible.Contains).ToArray() };
                server.SendDataToPeer(ref vote, DeliveryMethod.ReliableOrdered, peer);
            }
        }
    }

    private static void OnPasscodeAttempt(PasscodeAttemptEvent attempt)
    {
        if (!Ready || !FikaBackendUtils.IsClient || Manager?.CoopHandler?.MyPlayer is not { } player) return;
        var packet = new PasscodeAttemptPacket { NetId = player.NetId, PasscodeId = attempt.PasscodeId,
            TerminalNetId = attempt.TerminalNetId, Input = attempt.Input };
        Manager.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    internal static void BroadcastPasscodeResult(Player player, PasscodeTerminal terminal, bool success)
    {
        if (!Ready || !FikaBackendUtils.IsServer || player == null || terminal == null) return;
        var result = new PasscodeResultPacket { NetId = ToNetId(player.RaidId), PasscodeId = terminal.PasscodeId,
            TerminalNetId = terminal.NetId, Successful = success };
        Manager.SendData(ref result, DeliveryMethod.ReliableOrdered);
    }

    internal static void StartCutscene(CutsceneProcess process, bool forceMode, float initTime)
    {
        if (!FikaBackendUtils.IsServer || World == null || process == null || Cutscenes.ContainsKey(process.processId)) return;
        var start = new CutsceneStartPacket { ProcessId = process.processId, CutsceneId = process.currentCutsceneId,
            InitTime = initTime, FixedTime = process.fixedTimeOn, ForceMode = forceMode,
            NetIds = process.playersInCurrentView.ToArray().Select(PlayerIdToNetId).ToArray() };
        Cutscenes[start.ProcessId] = start;
        Votes[start.ProcessId] = new();
        Manager?.SendData(ref start, DeliveryMethod.ReliableOrdered);
    }

    internal static void EndCutscene(int processId, bool skip)
    {
        if (!FikaBackendUtils.IsServer || !Cutscenes.Remove(processId)) return;
        Votes.Remove(processId);
        Completed.RemoveWhere(x => x.Process == processId);
        if (skip)
        {
            var skipped = new CutsceneSkipPacket { ProcessId = processId, Skip = true };
            Manager.SendData(ref skipped, DeliveryMethod.ReliableOrdered);
        }
        var end = new CutsceneEndPacket { ProcessId = processId };
        Manager.SendData(ref end, DeliveryMethod.ReliableOrdered);
    }

    private static void OnSkipIntention(CutsceneSkipIntentionEventFromClient intention)
    {
        if (Manager?.CoopHandler?.MyPlayer is not { } player) return;
        var packet = new CutsceneSkipIntentionPacket { NetId = player.NetId, ProcessId = intention.ProcessId,
            CutsceneId = intention.CutsceneId, WantsToSkip = intention.WantsToSkip };
        if (FikaBackendUtils.IsServer) HandleSkip(packet, player);
        else Manager.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    private static void OnCutsceneTime(CorrectCutsceneTimerEvent correction)
    {
        if (!FikaBackendUtils.IsServer || World == null) return;
        var packet = new CutsceneTimePacket { ProcessId = correction.processId,
            CutsceneId = correction.cutsceneId, ElapsedTime = correction.elapsedTime };
        Manager?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    private static void OnViewComplete(SendEndCutsceneOnServerEvent completed)
    {
        if (Manager?.CoopHandler?.MyPlayer is not { } player) return;
        var packet = new CutsceneViewCompletePacket { NetId = player.NetId, ProcessId = completed.CutsceneID };
        if (FikaBackendUtils.IsServer) HandleComplete(packet, player);
        else Manager.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    internal static void HandleSkip(CutsceneSkipIntentionPacket packet, FikaPlayer player)
    {
        if (!FikaBackendUtils.IsServer || !Cutscenes.TryGetValue(packet.ProcessId, out var start) ||
            packet.CutsceneId != start.CutsceneId || !start.NetIds.Contains(player.NetId) ||
            Completed.Contains((packet.ProcessId, player.NetId)) || !Votes.TryGetValue(packet.ProcessId, out var votes)) return;
        if (packet.WantsToSkip) votes.Add(player.NetId);
        else votes.Remove(player.NetId);
        BroadcastVotes(start, votes);
    }

    private static void BroadcastVotes(CutsceneStartPacket start, HashSet<int> votes)
    {
        var eligible = start.NetIds.Where(id => !Completed.Contains((start.ProcessId, id)) && IsActiveViewer(id)).ToArray();
        var update = new CutsceneSkipPacket { ProcessId = start.ProcessId,
            PlayersCount = eligible.Length, SkippingNetIds = votes.Where(eligible.Contains).ToArray() };
        Manager.SendData(ref update, DeliveryMethod.ReliableOrdered);
        GlobalEventsController.CreateEvent<CutsceneSkipIntentionEventFromServer>().Invoke(
            start.ProcessId, update.PlayersCount, update.SkippingNetIds.Select(ToPlayerId).ToArray());
        if (eligible.Length > 0 && eligible.All(votes.Contains) &&
            World?.CutscenesServerController?._cutsceneProcesses.TryGetValue(start.ProcessId, out var process) == true)
            World.CutscenesServerController.EndCutscene(start.CutsceneId, start.ProcessId, process, process.playersInCurrentView, true);
    }

    internal static void HandleComplete(CutsceneViewCompletePacket packet, FikaPlayer player)
    {
        if (!FikaBackendUtils.IsServer || !Cutscenes.TryGetValue(packet.ProcessId, out var start) ||
            !start.NetIds.Contains(player.NetId) || !Completed.Add((packet.ProcessId, player.NetId))) return;
        World?.CutscenesServerController?.PlayerViewCutsceneComplete(start.CutsceneId, packet.ProcessId, player.PlayerId);
        if (Votes.TryGetValue(packet.ProcessId, out var votes))
        {
            votes.Remove(player.NetId);
            BroadcastVotes(start, votes);
        }
    }

    internal static void OnDisconnected(FikaPlayer player)
    {
        if (player == null) return;
        DisconnectedViewers.Add(player.NetId);
        DialogEntryOccupancy.ReleaseAllFor(player.NetId);
        // Disconnecting is not a completed view: reconnect must still resume it.
        foreach (var entry in Votes.ToArray())
        {
            entry.Value.Remove(player.NetId);
            if (Cutscenes.TryGetValue(entry.Key, out var start) && start.NetIds.Contains(player.NetId))
                BroadcastVotes(start, entry.Value);
        }
    }

    private static bool IsActiveViewer(int netId)
    {
        if (DisconnectedViewers.Contains(netId)) return false;
        if (Manager?.CoopHandler?.Players == null || !Manager.CoopHandler.Players.TryGetValue(netId, out var player) ||
            player.HealthController?.IsAlive != true || Manager.CoopHandler.ExtractedPlayers.Contains(netId)) return false;
        if (player.IsYourPlayer) return !FikaBackendUtils.IsHeadless;
        var peers = new List<NetPeer>();
        FikaSingleton<FikaServer>.Instance?.NetServer.GetConnectedPeers(peers);
        return peers.Any(peer => peer.ConnectionState == ConnectionState.Connected && peer.Player?.NetId == netId);
    }
}
