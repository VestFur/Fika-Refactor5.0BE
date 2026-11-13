using Fika.Core.Main.BaseClasses;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.Communications;
using EFT.Interactive;
using EFT.InventoryLogic;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Components;
using Fika.Core.Main.Factories;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.ObservedClasses;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.Networking.Packets.Communication;
#if DEBUG
using Fika.Core.Networking.Packets.Debug;
using EFT.UI;
using static Fika.Core.Networking.Packets.Debug.CommandPacket;
using Fika.Core.ConsoleCommands;
#endif
using Fika.Core.Networking.Packets.FirearmController;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.Player;
using Fika.Core.Networking.Packets.Player.Common;
using Fika.Core.Networking.Packets.World;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static Fika.Core.Networking.Packets.World.ReconnectPacket;
using EFT.NetworkPackets;
using Diz.Jobs;
using static Fika.Core.Networking.NetworkUtils;

namespace Fika.Core.Networking;

public sealed partial class FikaServer
{
    private void SpawnItemInInventoryPacketReceived(SpawnItemInInventoryPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        if (!CoopHandler.Players.TryGetValue(packet.NetId, out var player))
        {
            _logger.LogError($"Could not find player with id [{packet.NetId}] when trying to spawn item in inventoy");
            return;
        }

        var item = Singleton<ItemFactory>.Instance.CreateItem(packet.ItemId, packet.TemplateId, null);
        if (item == null)
        {
            _logger.LogError($"Failed to create item [{packet.ItemId}] with templateId [{packet.TemplateId}]");
            return;
        }

        if (packet.Amount > 1 && item.StackMaxSize > 1)
        {
            item.StackObjectsCount = Mathf.Clamp(packet.Amount, 1, item.StackMaxSize);
        }
        else
        {
            item.StackObjectsCount = 1;
        }

        var tempMove = TemporaryStash.Grid.AddAnywhere(item, EErrorHandlingType.Ignore);
        if (tempMove.Failed)
        {
            _logger.LogError($"Failed to move item to temporary stash: {tempMove.Error}");
            return;
        }

        new ItemController(TemporaryStash, player.InventoryController.ID, "temp item stash", false, EOwnerType.Profile);

        var inventoryController = player.InventoryController;
        var result = ItemManipulator.Move(item, packet.ItemAddress, inventoryController);
        if (result.Failed)
        {
            _logger.LogError($"Failed to move spawned item: {result.Error}");
        }

        var resources = new Il2CppSystem.Collections.Generic.List<ResourceKey>();
        foreach (var resource in item.Template.AllResources) resources.Add(resource);
        Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid, ObjectsFactory.AssemblyType.Online,
            resources, JobYieldPriority.Immediate, null, new Il2CppSystem.Threading.CancellationToken())
            .HandleExceptions();
    }

    private void OnQuestSyncPacketReceived(QuestSyncPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        if (!CoopHandler.Players.TryGetValue(packet.NetId, out var player))
        {
            _logger.LogError($"Could not find player with id [{packet.NetId}] when trying to sync quest packet");
            return;
        }

        if (player.FikaQuests is ObservedQuestController observedQuestController)
        {
            observedQuestController.UpdateQuestStatusForClient(packet);
            return;
        }

        _logger.LogError($"QuestController on player [{player.Profile.GetCorrectedNickname()}] was not of type observed, was [{player.QuestController.GetType().Name}]");
    }

    private void OnKnifeHitPacketReceived(KnifeHitPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld == null)
        {
            FikaGlobals.LogError("GameWorld was null when receiving KnifeHitPacket");
            return;
        }

        if (gameWorld.TryCast<FikaHostGameWorld>() is not { } hostGameWorld)
        {
            FikaGlobals.LogError($"GameWorld not a FikaHostGameWorld, was: {gameWorld.GetType().Name}");
            return;
        }

        switch (packet.HitType)
        {
            case EHitType.Window:
                {
                    if (hostGameWorld.Windows == null || !hostGameWorld.Windows.TryGetByKey(packet.HitId, out var window))
                    {
                        FikaGlobals.LogError($"Could not find window with NetId {packet.HitId}");
                        return;
                    }

                    var damageInfo = new DamageInfo
                    {
                        HitPoint = packet.HitPoint
                    };
                    window.MakeHit(ref damageInfo);
                }
                break;
            case EHitType.Btr:
                {
                    if (!CoopHandler.Players.TryGetValue(packet.NetId, out var player))
                    {
                        FikaGlobals.LogError($"Could not find player with NetId {packet.NetId}");
                        return;
                    }

                    if (hostGameWorld.BtrController != null)
                    {
                        hostGameWorld.BtrController.HitFromPlayer(player);
                    }
                }
                break;
            case EHitType.Default:
            case EHitType.Lamp:
                {
                    if (hostGameWorld.TurnablesDict == null || !hostGameWorld.TurnablesDict.TryGetValue(packet.HitId, out var turnable))
                    {
                        FikaGlobals.LogError($"Could not find turnable with NetId {packet.HitId}");
                        return;
                    }

                    turnable.OnHit(new DamageInfo
                    {
                        HitPoint = packet.HitPoint
                    });
                }
                break;
            case EHitType.Tripwire:
            case EHitType.Event:
                break;
        }
    }

    private void OnProceedRequestPacketReceived(ProceedRequestPacket packet, NetPeer peer)
    {
        var response = new ProceedResponsePacket
        {
            CallbackId = packet.CallbackId
        };

        if (peer.Player == null || packet.NetId != peer.Player.NetId)
        {
            response.Error = $"Proceed request for unbound player {packet.NetId}";
            SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
            return;
        }

        if (!CoopHandler.Players.TryGetValue(packet.NetId, out var player))
        {
            response.Error = $"Could not find player with id {packet.NetId}";
            SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
            return;
        }

        if (packet.ProceedType is EProceedType.EmptyHands) // always accept empty hands
        {
            SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
            return;
        }

        if (!TryFindItemForProceedPacket(packet.ItemId, out var item))
        {
            response.Error = $"Could not find item with id {packet.ItemId}";
            SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
            return;
        }

        if (item.CurrentAddress != null)
        {
            var result = item.CheckAction(null);
            if (result.Failed)
            {
                response.Error = $"Player cannot equip item with id {packet.ItemId}: {result.Error}";
                SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
                return;
            }
        }

        SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
    }

    /// <summary>
    /// Attempts to find the item with the given id
    /// </summary>
    /// <param name="itemId">The id to search for</param>
    /// <param name="item">The item if found</param>
    /// <returns><see langword="true"/> if found; otherwise <see langword="false"/></returns>
    private bool TryFindItemForProceedPacket(MongoID itemId, out Item item)
    {
        var gameWorld = Singleton<GameWorld>.Instance;

        var search = gameWorld.FindItemWithWorldData(itemId);
        if (search.Succeeded)
        {
            item = search.Value.Item1;
            return true;
        }

        var stationary = gameWorld.FindStationaryWeaponByItemId(itemId);
        if (stationary != null)
        {
            item = stationary.Item;
            return true;
        }

        item = null;
        return false;
    }

    private void OnClearSnapshotterPacketReceived(ClearSnapshotterPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var player) && player.TryCast<ObservedPlayer>() is { } observedPlayer)
        {
            observedPlayer.HealthBar.RemoveAllActiveEffects();
            observedPlayer.Snapshotter.Clear();
            return;
        }

        _logger.LogError($"Could not find player {packet.NetId} to reset snapshotter");
    }

    private void OnLoadingScreenPlayersPacketReceived(LoadingScreenPlayersPacket packet, NetPeer peer)
    {
        if (!_assignedPeerNetIds.TryGetValue(peer, out var assignedNetId) || packet.NetIds?.Length != 1 ||
            packet.NetIds[0] != assignedNetId || packet.Nicknames?.Length != 1 || LoadingScreenUI.Instance == null) return;

        LoadingScreenUI.Instance.AddPlayer(assignedNetId, packet.Nicknames[0]);

        var newPacket = LoadingScreenUI.Instance.GetPlayersPacket();
        SendData(ref newPacket, DeliveryMethod.ReliableUnordered);
    }

    private void OnLoadingScreenPacketReceived(LoadingScreenPacket packet, NetPeer peer)
    {
        if (!_assignedPeerNetIds.TryGetValue(peer, out var assignedNetId) || packet.NetId != assignedNetId) return;
        if (LoadingScreenUI.Instance != null)
        {
            LoadingScreenUI.Instance.SetProgress(packet.NetId, packet.Progress);
        }

        SendData(ref packet, DeliveryMethod.Unreliable, peer);
    }

#if DEBUG
    private void OnCommandPacketReceived(CommandPacket packet, NetPeer peer)
    {
        switch (packet.CommandType)
        {
            case ECommandType.SpawnAI:
                FikaCommands.SpawnNPC(packet.SpawnType, packet.SpawnAmount);
                break;
            case ECommandType.DespawnAI:
                FikaCommands.DespawnAllAI();
                break;
            case ECommandType.Bring:
                FikaCommands.BringReplicated(packet.NetId);
                break;
            case ECommandType.SpawnAirdrop:
                FikaCommands.SpawnAirdrop();
                break;
            default:
                break;
        }
    }

    private void OnSpawnItemPacketReceived(SpawnItemPacket packet, NetPeer peer)
    {
        SendData(ref packet, DeliveryMethod.ReliableOrdered, peer);

        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            FikaGlobals.SpawnItemInWorld(packet.Item, playerToApply);
        }
    }
#endif

    private void OnEventControllerInteractPacketReceived(EventControllerInteractPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        var gameWorld = Singleton<GameWorld>.Instance;
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var player))
        {
            if (FikaWorldControllers.Find<HostRunddansController>(gameWorld.RunddansController) is { } controller)
            {
                controller.ObservedInteractWithEventObject(player, packet.Data, peer);
                return;
            }

            _logger.LogError("Could not find the RunddansController when player interacted");
        }
    }

    private void OnInraidQuestPacketReceived(InRaidQuestPacket packet, NetPeer peer)
    {
        if (peer.Player == null || peer.Player.NetId != packet.NetId)
        {
            _logger.LogWarning($"Rejected quest inventory update for net id {packet.NetId} from an unbound peer");
            return;
        }

        if (_coopHandler.Players.TryGetValue(packet.NetId, out var player))
        {
            if (player.FikaQuests is ObservedQuestController controller)
            {
                // Resource loading can suspend. Keep the decoded packet value
                // and relay only after the host observer has applied it.
                var relay = packet;
                MainThreadDispatcher.RunAsync(async () =>
                {
                    try
                    {
                        await controller.HandleInraidQuestPacketAsync(relay);
                        // The sender can disconnect while reward bundles load.
                        // The host has accepted the update, so other clients
                        // must still receive it after the observer applies it.
                        SendData(ref relay, DeliveryMethod.ReliableOrdered, peer);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError($"Quest inventory synchronization failed for {relay.NetId}: {exception}");
                    }
                }).Forget();
            }
        }
    }

    private void OnNetworkSettingsPacketReceived(NetworkSettingsPacket packet, NetPeer peer)
    {
#if DEBUG
        _logger.LogInfo($"Received connection from {packet.ProfileId}");
#endif
        if (string.IsNullOrEmpty(packet.ProfileId)) return;
        var scavLateJoin = _scavLateJoinPeers.Contains(peer);
        if (_assignedPeerProfiles.TryGetValue(peer, out var assignedProfile) && assignedProfile != packet.ProfileId)
        {
            if (!scavLateJoin)
            {
                _logger.LogWarning($"Rejected profile change on peer {peer.Id}");
                return;
            }

            // SPT regenerated the scav profile for this late joiner.
            _logger.LogInfo($"Rebinding peer {peer.Id} from {assignedProfile} to {packet.ProfileId} (player scav late join)");
            _assignedPeerProfiles[peer] = packet.ProfileId;
        }
        if (_assignedPeerProfiles.Any(entry => entry.Key != peer && entry.Value == packet.ProfileId &&
            entry.Key.ConnectionState == ConnectionState.Connected))
        {
            _logger.LogWarning($"Rejected simultaneous connection for profile {packet.ProfileId}");
            return;
        }
        if (!_cachedConnections.TryGetValue(packet.ProfileId, out var netId))
        {
            netId = PopNetId();
            _cachedConnections.Add(packet.ProfileId, netId);
        }

        _assignedPeerNetIds[peer] = netId;
        _assignedPeerProfiles[peer] = packet.ProfileId;

        NetworkSettingsPacket response = new()
        {
            SendRate = _sendRate,
            NetId = netId,
            AllowVOIP = AllowVOIP,
            StrictSync = StrictInventorySync
        };
        SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnWorldPacketReceived(WorldPacket packet, NetPeer peer)
    {
        if (peer.Player == null) return;
        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld == null)
        {
            _logger.LogError("OnNewWorldPacketReceived: GameWorld was null!");
            return;
        }

        FikaHostWorld.LootSyncPackets.AddRange(packet.LootSyncStructs);

        packet.Flush();
    }

    private void OnRequestPacketReceived(RequestPacket packet, NetPeer peer)
    {
        if (packet.RequestSubPacket == null)
        {
            _logger.LogError("OnRequestPacketReceived: RequestSubPacket was null!");
            return;
        }

        packet.RequestSubPacket.HandleRequest(peer, this);
    }

    private void OnSideEffectPacketReceived(SideEffectPacket packet, NetPeer peer)
    {
#if DEBUG
        _logger.LogWarning("OnSideEffectPacketReceived: Received");
#endif

        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld == null)
        {
            _logger.LogError("OnSideEffectPacketReceived: GameWorld was null!");
            return;
        }

        var gstruct2 = gameWorld.FindItemById(packet.ItemId);
        if (gstruct2.Failed)
        {
            _logger.LogError("OnSideEffectPacketReceived: " + gstruct2.Error);
            return;
        }
        var item = gstruct2.Value;
        if (item.TryGetItemComponent(out SideEffectComponent sideEffectComponent))
        {
#if DEBUG
            _logger.LogInfo("Setting value to: " + packet.Value + ", original: " + sideEffectComponent.Value);
#endif
            sideEffectComponent.Value = packet.Value;
            item.RaiseRefreshEvent(false, false);
            return;
        }
        _logger.LogError("OnSideEffectPacketReceived: SideEffectComponent was not found!");
    }

    private void OnLoadingProfilePacketReceived(LoadingProfilePacket packet, NetPeer peer)
    {
        if (packet.Profiles == null || packet.Profiles.Count != 1)
        {
            _logger.LogError("OnLoadingProfilePacketReceived: Expected one client profile");
            return;
        }

        (var profile, var isLeader) = packet.Profiles.First();
        if (!_assignedPeerProfiles.TryGetValue(peer, out var assignedProfile) || profile.ProfileId != assignedProfile)
        {
            if (!_scavLateJoinPeers.Contains(peer))
            {
                _logger.LogWarning($"Rejected loading profile for peer {peer.Id}: profile did not match settings");
                return;
            }

            // Player scav late join: SPT regenerated the profile for this raid.
            _logger.LogInfo($"Accepting loading profile {profile.ProfileId} from player scav peer {peer.Id}");
            _assignedPeerProfiles[peer] = profile.ProfileId;
        }
        if (!_visualProfiles.Any(x => x.Key.ProfileId == profile.ProfileId))
        {
            _visualProfiles.Add(profile, _visualProfiles.Count == 0 || isLeader);
        }
        FikaBackendUtils.AddPartyMembers(_visualProfiles);
        packet.Profiles = _visualProfiles;
        SendData(ref packet, DeliveryMethod.ReliableOrdered);

        var clientConnected = ClientConnected.FromValue(profile.Info.MainProfileNickname);
        if (!FikaBackendUtils.IsHeadless)
        {
            clientConnected.Execute();
        }

        SendGenericPacket(EGenericSubPacketType.ClientConnected,
            clientConnected, true);

        peer.Tag = profile.Info.MainProfileNickname;
    }

    private void OnPingPacketReceived(PingPacket packet, NetPeer peer)
    {
        if (FikaPlugin.Instance.Settings.UsePingSystem.Value && !FikaBackendUtils.IsHeadless)
        {
            PingFactory.ReceivePing(packet.PingLocation, packet.PingType, packet.PingColor, packet.Nickname, packet.LocaleId);
        }
    }

    private void OnBotStatePacketReceived(BotStatePacket packet, NetPeer peer)
    {
        // A client acknowledges loading host-created bots; it cannot own
        // their enable, disable, or disposal transitions.
        if (packet.Type != BotStatePacket.EStateType.LoadBot) return;
        var host = FikaSingleton<IFikaGame>.Instance?.GameController as HostGameController;
        host?.IncreaseLoadedPlayers(packet.NetId, peer);
    }

    private void OnTransitInteractPacketReceived(TransitInteractPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            var transitController = Singleton<GameWorld>.Instance?.TransitController;
            if (transitController != null)
            {
                transitController.InteractWithTransit(playerToApply, packet.Data);
            }
        }
    }

    private void OnSyncTransitControllersPacketReceived(SyncTransitControllersPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.ProfileId != peer.Player.ProfileId) return;
        var transitController = Singleton<GameWorld>.Instance?.TransitController;
        if (transitController != null)
        {
            if (!transitController.summonedTransits.ContainsKey(packet.ProfileId))
                transitController.summonedTransits[packet.ProfileId] = new(packet.RaidId, packet.Count, packet.Maps, packet.Events);
            if (FikaSingleton<IFikaGame>.Instance?.GameController?.GameInstance?.Status == GameStatus.Started)
            {
                var player = _coopHandler.HumanPlayers.FirstOrDefault(x => x.ProfileId == packet.ProfileId);
                if (player != null) SendMapStateForReconnect(player, peer);
            }
            return;
        }

        _logger.LogError("OnSyncTransitControllersPacketReceived: TransitController was null!");
    }

    private void OnBTRInteractionPacketReceived(BTRInteractionPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId || packet.IsResponse)
        {
            return;
        }

        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld.BtrController != null && gameWorld.BtrController.BtrVehicle != null)
            {
                var status = gameWorld.BtrController.BtrVehicle.InteractInternal(playerToApply, packet.Data);
                BTRInteractionPacket response = new(packet.NetId)
                {
                    IsResponse = true,
                    Status = status,
                    Data = packet.Data
                };

                SendData(ref response, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    private void OnResyncInventoryIdPacketReceived(ResyncInventoryIdPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            if (playerToApply.TryCast<ObservedPlayer>() is { } observedPlayer)
            {
                if (observedPlayer.InventoryController?.TryCast<ObservedInventoryController>() is { } observedController)
                {
                    observedController.SetNewID(packet.MongoId);
                }
            }
        }
    }

    private void OnReconnectPacketReceived(ReconnectPacket packet, NetPeer peer)
    {
        if (!packet.IsRequest) return;
        if (!_assignedPeerProfiles.TryGetValue(peer, out var assignedProfile) || packet.ProfileId != assignedProfile)
        {
            // A reconnect request that carries an identity this connection never announced can
            // never be answered, and dropping it silently leaves the waiting client stuck on a
            // 120 s timeout. Answer it with the reason instead: the client needs the error to
            // stop waiting, and the host log needs it to explain the refusal.
            if (peer?.ConnectionState == ConnectionState.Connected)
            {
                var announced = assignedProfile ?? "none";
                var reason = $"Reconnect request rejected: this connection announced profile {announced}, " +
                    $"request carried {packet.ProfileId}";
                FikaGlobals.LogWarning(reason);
                var rejected = new ReconnectPacket { Type = EReconnectDataType.Failed, ProfileId = packet.ProfileId, Error = reason };
                SendDataToPeer(ref rejected, DeliveryMethod.ReliableOrdered, peer);
            }
            return;
        }
        // SubscribeNetSerializable reuses its instance. The async world snapshot
        // must not observe the next peer's request after awaiting container setup.
        packet = new ReconnectPacket { IsRequest = true, InitialRequest = packet.InitialRequest, ProfileId = packet.ProfileId };
        var world = Singleton<GameWorld>.Instance;
        MainThreadDispatcher.RunAsync(async () =>
        {
            try { await HandleReconnectPacket(packet, peer, world); }
            catch (Exception ex)
            {
                _logger.LogError($"Reconnect failed for {packet.ProfileId}: {ex}");
                // An older raid's asynchronous snapshot must not send a failure
                // into a newer raid, or back to a peer that has disconnected.
                if (!IsCurrentReconnectWorld(world, peer)) return;
                var failed = new ReconnectPacket { Type = EReconnectDataType.Failed, ProfileId = packet.ProfileId, Error = ex.Message };
                SendDataToPeer(ref failed, DeliveryMethod.ReliableOrdered, peer);
            }
        }).Forget();
    }

    private static bool IsCurrentReconnectWorld(GameWorld world, NetPeer peer) =>
        world != null && peer?.ConnectionState == ConnectionState.Connected &&
        Singleton<GameWorld>.Instance?.Pointer == world.Pointer;

    private async System.Threading.Tasks.Task HandleReconnectPacket(ReconnectPacket packet, NetPeer peer, GameWorld world)
    {
        if (packet.IsRequest)
        {
            if (!IsCurrentReconnectWorld(world, peer))
            {
                FikaGlobals.LogWarning($"Reconnect {packet.ProfileId}: ignored, the world this request belongs to is no longer current (the client waits for its own timeout)");
                return;
            }
            // The client asks for its profile as the very first step of InitPlayer, so a request
            // can arrive while this host is still registering the raid (new raid, transit, host
            // restart). Retry the lookup for a bounded time instead of failing on the first miss.
            var reconnectDeadline = Time.unscaledTimeAsDouble + 20.0;
            FikaPlayer reconnectingPlayer = null;
            while (true)
            {
                reconnectingPlayer = _coopHandler.HumanPlayers.FirstOrDefault(x => x.ProfileId == packet.ProfileId);
                if (reconnectingPlayer != null)
                {
                    break;
                }
                if (Time.unscaledTimeAsDouble >= reconnectDeadline)
                {
                    break;
                }
                await Task.Delay(250);
                if (!IsCurrentReconnectWorld(world, peer))
                {
                    FikaGlobals.LogWarning($"Reconnect {packet.ProfileId}: aborted, the world changed while waiting for the player to register");
                    return;
                }
            }
            if (reconnectingPlayer == null)
            {
                // A player scav announces the profile SPT regenerated for this
                // raid, so its body can be registered under a different profile id
                // than the one the reconnecting client sends (the scav character
                // id differs from the account/PMC id by design). The netId is
                // cached per announced profile, so the body this peer owned before
                // the disconnect is still addressable through it.
                FikaPlayer matchedBody = null;
                if (!_assignedPeerNetIds.TryGetValue(peer, out var reconnectNetId))
                {
                    var announcedNetId = _assignedPeerProfiles.FirstOrDefault(entry => entry.Value == packet.ProfileId);
                    if (announcedNetId.Key != null)
                    {
                        reconnectNetId = _assignedPeerNetIds.FirstOrDefault(entry => entry.Key == announcedNetId.Key).Value;
                    }
                }
                if (reconnectNetId != 0 && _coopHandler.Players.TryGetValue(reconnectNetId, out var bodyByNetId))
                {
                    matchedBody = bodyByNetId.TryCast<FikaPlayer>();
                }
                if (matchedBody == null)
                {
                    foreach (var candidate in _coopHandler.Players)
                    {
                        var typed = candidate.Value.TryCast<FikaPlayer>();
                        if (typed?.Profile != null && typed.Profile.Id == packet.ProfileId)
                        {
                            matchedBody = typed;
                            break;
                        }
                    }
                }
                if (matchedBody != null)
                {
                    _logger.LogInfo($"Reconnect {packet.ProfileId}: matched netId {reconnectNetId} " +
                        $"carrying profile {matchedBody.ProfileId} instead");
                    reconnectingPlayer = matchedBody;
                }
            }
            if (reconnectingPlayer == null)
            {
                throw new InvalidOperationException($"Reconnect player {packet.ProfileId} was not found");
            }
            if (packet.InitialRequest)
            {
                FikaNotifications.DisplayMessageNotification(LocaleUtils.RECONNECT_REQUESTED.Localized(),
                    iconType: EFT.Communications.ENotificationIconType.Alert);
                if (reconnectingPlayer.TryCast<ObservedPlayer>() is not { } observedPlayer)
                    throw new InvalidOperationException($"Reconnect profile {packet.ProfileId} is not an observed player");
#if DEBUG
                _logger.LogInfo($"Found player to send back: {observedPlayer.Profile.GetCorrectedNickname()}");
#endif
                ReconnectPacket ownCharacterPacket = new()
                {
                    Type = EReconnectDataType.OwnCharacter,
                    ProfileId = packet.ProfileId,
                    Profile = observedPlayer.Profile,
                    ProfileHealthClass = observedPlayer.NetworkHealthController.Store(),
                    PlayerPosition = observedPlayer.Position,
                    PlayerRotation = observedPlayer.Rotation,
                    ControllerId = observedPlayer.InventoryController.CurrentId,
                    FirstOperationId = observedPlayer.InventoryController.NextOperationId,
                    ControllerType = observedPlayer.HandsController != null
                        ? HandsControllerTypeConvert.FromController(observedPlayer.HandsController)
                        : EHandsControllerType.Empty,
                    ItemId = observedPlayer.HandsController?.Item?.Id ?? new MongoID(),
                    IsStationary = observedPlayer.MovementContext.IsStationaryWeaponInHands,
                    LastEquippedItemId = observedPlayer.LastEquippedWeaponOrKnifeItem?.Id ?? new MongoID(),
                    PlayerBodyRotation = observedPlayer.Transform.rotation,
                    RevivalState = observedPlayer.CaptureRevivalState()
                };
                if (!IsCurrentReconnectWorld(world, peer)) return;
                SendDataToPeer(ref ownCharacterPacket, DeliveryMethod.ReliableOrdered, peer);
                return;
            }

            var gameWorld = world;

            var grenades = gameWorld.Grenades.GetValuesEnumerator();
            List<SmokeGrenadeNetworkData> smokeData = [];
            foreach (var item in grenades)
            {
                // Values returned by the IL2CPP native collection are often
                // materialized through the CLR Grenade wrapper. A CLR
                // `is` check can therefore miss the native SmokeGrenade
                // identity during reconnect snapshots. TryCast preserves the
                // native type test while keeping the same payload semantics.
                if (item.TryCast<SmokeGrenade>() is { } smokeGrenade)
                {
                    smokeData.Add(smokeGrenade.NetworkData);
                }
            }

            if (smokeData.Count > 0)
            {
                ReconnectPacket throwablePacket = new()
                {
                    Type = EReconnectDataType.Throwable,
                    ProfileId = packet.ProfileId,
                    ThrowableData = smokeData
                };

                SendDataToPeer(ref throwablePacket, DeliveryMethod.ReliableOrdered, peer);
            }

            List<WorldInteractiveObject.InteractiveObjectStatusInfo> interactivesData = [];
            foreach (var interactiveObject in gameWorld.World._interactableObjectsForNetSync)
            {
                if ((interactiveObject.DoorState != interactiveObject.InitialDoorState
                    && interactiveObject.DoorState != EDoorState.Interacting)
                    || (interactiveObject.TryCast<Door>() is { IsBroken: true }))
                {
                    interactivesData.Add(interactiveObject.GetStatusInfo(true));
                }
            }

            if (interactivesData.Count > 0)
            {
                ReconnectPacket interactivePacket = new()
                {
                    Type = EReconnectDataType.Interactives,
                    ProfileId = packet.ProfileId,
                    InteractivesData = interactivesData
                };

                SendDataToPeer(ref interactivePacket, DeliveryMethod.ReliableOrdered, peer);
            }

            var lampControllers = LocationScene.GetAllObjects<LampController>(false);
            Dictionary<int, byte> lampStates = [];
            foreach (var controller in lampControllers)
            {
                lampStates.Add(controller.NetId, (byte)controller.LampState);
            }

            if (lampStates.Count > 0)
            {
                ReconnectPacket lampPacket = new()
                {
                    Type = EReconnectDataType.LampControllers,
                    ProfileId = packet.ProfileId,
                    LampStates = lampStates
                };

                SendDataToPeer(ref lampPacket, DeliveryMethod.ReliableOrdered, peer);
            }

            var windows = gameWorld.Windows.GetValuesEnumerator();
            Dictionary<int, Vector3> windowData = [];
            foreach (var window in windows)
            {
                if (window == null)
                {
                    continue;
                }
                if (window.AvailableToSync && window.IsDamaged)
                {
                    // FirstHitPosition is an inline IL2CPP Nullable<Vector3>.
                    // The generated getter boxes an absent value as null and
                    // reading .Value can therefore throw during reconnect.
                    var firstHitPosition = NativeNullable<Vector3>.Read(
                        window, "<FirstHitPosition>k__BackingField");
                    if (!firstHitPosition.HasValue)
                    {
                        _logger.LogWarning($"Skipping damaged window {window.NetId} without a first-hit position");
                        continue;
                    }

                    windowData.Add(window.NetId, firstHitPosition.Value);
                }
            }

            if (windowData.Count > 0)
            {
                ReconnectPacket windowPacket = new()
                {
                    Type = EReconnectDataType.Windows,
                    ProfileId = packet.ProfileId,
                    WindowBreakerStates = windowData
                };

                SendDataToPeer(ref windowPacket, DeliveryMethod.ReliableOrdered, peer);
            }

            foreach (var player in _coopHandler.Players.Values)
            {
                if (player.ProfileId == packet.ProfileId)
                {
                    continue;
                }

                var characterPacket = SendCharacterPacket.FromValue(new()
                {
                    Profile = player.Profile,
                    ControllerId = player.InventoryController.CurrentId,
                    FirstOperationId = player.InventoryController.NextOperationId
                },
                player.HealthController.IsAlive, player.IsAI, player.Position, player.NetId, player.CaptureRevivalState());

                if (player.ActiveHealthController != null)
                {
                    characterPacket.PlayerInfoPacket.HealthByteArray = player.ActiveHealthController.SerializeState();
                }
                else if (player.TryCast<ObservedPlayer>() is { } observedPlayer)
                {
                    characterPacket.PlayerInfoPacket.HealthByteArray = observedPlayer.NetworkHealthController.Store().SerializeHealthInfo();
                }

                if (player.HandsController != null)
                {
                    characterPacket.PlayerInfoPacket.ControllerType = HandsControllerTypeConvert.FromController(player.HandsController);
                    characterPacket.PlayerInfoPacket.ItemId = player.HandsController.Item?.Id;
                    characterPacket.PlayerInfoPacket.IsStationary = player.MovementContext.IsStationaryWeaponInHands;
                }

                SendGenericPacketToPeer(EGenericSubPacketType.SendCharacter, characterPacket, peer);
            }

            StashesPacket stashesPacket = new();
            if (gameWorld.BtrController?.TransferItemsController != null)
            {
                stashesPacket.HasBTR = true;
                var length = gameWorld.BtrController.TransferItemsController._transferContainers.Count;
                stashesPacket.BTRStashes = new Stash[length];
                for (var i = 0; i < length; i++)
                {
                    stashesPacket.BTRStashes[i] = gameWorld.BtrController.TransferItemsController._transferContainers[i];
                }
            }

            if (gameWorld.TransitController?.TransferItemsController != null)
            {
                stashesPacket.HasTransit = true;
                var length = gameWorld.TransitController.TransferItemsController._transferContainers.Count;
                stashesPacket.TransitStashes = new Stash[length];
                for (var i = 0; i < length; i++)
                {
                    stashesPacket.TransitStashes[i] = gameWorld.TransitController.TransferItemsController._transferContainers[i];
                }
            }

            SendDataToPeer(ref stashesPacket, DeliveryMethod.ReliableOrdered, peer);

            foreach (var player in _coopHandler.HumanPlayers)
            {
                if (player.ProfileId == packet.ProfileId && player.TryCast<ObservedPlayer>() is { } observedPlayer && observedPlayer.FikaQuests is ObservedQuestController questController)
                {
                    if (questController.TryGetReconnectQuestSyncPackets(out var packets))
                    {
                        ReconnectPacket questSyncPacket = new()
                        {
                            Type = EReconnectDataType.Quests,
                            ProfileId = packet.ProfileId,
                            QuestSyncPackets = packets
                        };

#if DEBUG
                        _logger.LogInfo($"Sending {packets.Count} quest sync packets back to reconnecting client {observedPlayer.Profile.GetCorrectedNickname()}");
#endif

                        SendDataToPeer(ref questSyncPacket, DeliveryMethod.ReliableOrdered, peer);
                    }

                    break;
                }
            }

            await SendWorldObjectsForReconnect(peer, packet.ProfileId);
            if (!IsCurrentReconnectWorld(world, peer)) return;
            SendMapStateForReconnect(reconnectingPlayer, peer);

            if (!IsCurrentReconnectWorld(world, peer)) return;
            ReconnectPacket finishPacket = new()
            {
                Type = EReconnectDataType.Finished,
                ProfileId = packet.ProfileId,
                RevivalState = reconnectingPlayer.CaptureRevivalState(),
                BtrPassengers = CaptureBtrPassengers(),
                BtrServices = CaptureBtrServices(reconnectingPlayer)
            };

            SendDataToPeer(ref finishPacket, DeliveryMethod.ReliableOrdered, peer);
        }
    }

    private void OnWorldLootPacketReceived(WorldLootPacket packet, NetPeer peer)
    {
        var fikaGame = FikaSingleton<IFikaGame>.Instance;
        if (fikaGame != null)
        {
            WorldLootPacket response = new()
            {
                Data = (fikaGame.GameController as HostGameController).GetHostLootItems()
            };

            SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
        }
    }

    private void OnInteractableInitPacketReceived(InteractableInitPacket packet, NetPeer peer)
    {
        if (packet.IsRequest)
        {
            if (Singleton<GameWorld>.Instantiated)
            {
                var world = Singleton<GameWorld>.Instance.World;
                if (world.Interactables != null)
                {
                    var response = InteractableInitPacket.FromInteractables(world.Interactables);

                    SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
                }
            }
        }
    }

    private void OnQuestDropItemPacketReceived(QuestDropItemPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        packet.Nickname = peer.Player.Profile.Info.MainProfileNickname;

        if (_hostPlayer?.HealthController?.IsAlive == true)
        {
            if (_hostPlayer.FikaQuests is ClientSharedQuestController sharedQuestController)
            {
                sharedQuestController.ReceiveQuestDropItemPacket(packet);
            }
        }
        SendData(ref packet, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnQuestItemPacketReceived(QuestItemPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        packet.Nickname = peer.Player.Profile.Info.MainProfileNickname;

        if (_hostPlayer?.HealthController?.IsAlive == true)
        {
            if (_hostPlayer.FikaQuests is ClientSharedQuestController sharedQuestController)
            {
                sharedQuestController.ReceiveQuestItemPacket(packet);
            }
        }
        SendData(ref packet, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnQuestConditionPacketReceived(QuestConditionPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        packet.Nickname = peer.Player.Profile.Info.MainProfileNickname;

        if (_hostPlayer?.HealthController?.IsAlive == true)
        {
            if (_hostPlayer.FikaQuests is ClientSharedQuestController sharedQuestController)
            {
                sharedQuestController.ReceiveQuestPacket(packet);
            }
        }
        SendData(ref packet, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnTextMessagePacketReceived(TextMessagePacket packet, NetPeer peer)
    {
        if (peer.Player == null)
        {
            return;
        }

        packet.Nickname = peer.Player.Profile.Info.MainProfileNickname;
        _logger.LogInfo($"Received message from: {packet.Nickname}, Message: {packet.Message}");

        if (_fikaChat != null)
        {
            _fikaChat.AddMessage(new(packet.Nickname, packet.Message), true);
            _fikaChat.OpenChat(true);
        }

        SendData(ref packet, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnGenericPacketReceived(GenericPacket packet, NetPeer peer)
    {
        if (!_assignedPeerNetIds.TryGetValue(peer, out var assignedNetId) || packet.NetId != assignedNetId ||
            IsHostOnlyGenericPacket(packet.Type))
        {
            if (packet.Type is EGenericSubPacketType.SendCharacter)
            {
                _logger.LogWarning($"Dropped a character announcement from peer {peer.Id}: " +
                    $"assignedNetId={(assignedNetId == 0 ? "none" : assignedNetId.ToString())}, " +
                    $"packetNetId={packet.NetId}, hostOnly={IsHostOnlyGenericPacket(packet.Type)}");
            }

            return;
        }
        if (packet.Type is EGenericSubPacketType.SendCharacter)
        {
            // The first character announcement precedes the final information
            // packet that binds peer.Player. Validate against the settings
            // assignment instead of requiring that later binding.
            if (packet.SubPacket is SendCharacterPacket logged)
            {
                _logger.LogInfo($"Character announcement from peer {peer.Id}: netId={logged.NetId} " +
                    $"(assigned {assignedNetId}), isAI={logged.IsAI}, " +
                    $"profile={logged.PlayerInfoPacket.Profile?.ProfileId}, scavPeer={_scavLateJoinPeers.Contains(peer)}");
            }

            if (packet.SubPacket is not SendCharacterPacket character)
            {
                _logger.LogWarning($"Character announcement from peer {peer.Id} carried " +
                    $"{packet.SubPacket?.GetType().Name ?? "no sub packet"} instead of a character packet");
                return;
            }

            if (character.IsAI || character.NetId != assignedNetId || character.PlayerInfoPacket.Profile == null)
            {
                // Without this the peer silently never gets a player, and every one
                // of its inventory operations is rejected afterwards.
                _logger.LogWarning($"Rejected the character announcement from peer {peer.Id}: " +
                    $"isAI={character.IsAI}, netId={character.NetId} (assigned {assignedNetId}), " +
                    $"profile={(character.PlayerInfoPacket.Profile == null ? "null" : "present")}");
                return;
            }

            if (!_assignedPeerProfiles.TryGetValue(peer, out var assignedProfile) ||
                character.PlayerInfoPacket.Profile.ProfileId != assignedProfile)
            {
                _logger.LogInfo($"Peer {peer.Id} announced profile " +
                    $"{character.PlayerInfoPacket.Profile.ProfileId} while the server assigned " +
                    $"{(_assignedPeerProfiles.TryGetValue(peer, out var current) ? current : "none")}");
                // A player scav that joined a running raid announces the profile
                // SPT regenerated for this raid; accept and rebind it, otherwise
                // the host never creates a player for it.
                if (!_scavLateJoinPeers.Contains(peer)) return;
                _logger.LogInfo($"Accepting regenerated scav profile " +
                    $"{character.PlayerInfoPacket.Profile.ProfileId} from peer {peer.Id}");
                _assignedPeerProfiles[peer] = character.PlayerInfoPacket.Profile.ProfileId;
            }
        }
        if (packet.Type is EGenericSubPacketType.MuffledState &&
            (peer.Player == null || ((MuffledState)packet.SubPacket).NetId != peer.Player.NetId)) return;
        if (packet.Type is EGenericSubPacketType.ClientExtract &&
            (peer.Player == null || ((ClientExtract)packet.SubPacket).NetId != peer.Player.NetId)) return;
        if (packet.Type is EGenericSubPacketType.FlareShootReport)
        {
            OnFlareShootReportReceived(packet.SubPacket as FlareShootReport, peer);
            return;
        }
        if (packet.Type is EGenericSubPacketType.DialogEntry)
        {
            OnDialogEntryPacketReceived((DialogEntryPacket)packet.SubPacket, peer);
            return;
        }
        if (packet.Type is EGenericSubPacketType.InventoryOperation)
        {
            OnInventoryPacketReceived((InventoryPacket)packet.SubPacket, peer);
            return;
        }
        packet.Execute();
        if (packet.Type is EGenericSubPacketType.SendCharacter)
        {
            // This announcement was validated above. Forward it explicitly:
            // the raw broadcast path suppresses unvalidated character data.
            // Do not clear the receive packet; NetPacketProcessor owns its pool.
            var writer = new NetDataWriter();
            writer.PutEnum(EPacketType.Serializable);
            _packetProcessor.WriteNetReusable(writer, ref packet);
            _netServer.SendToAll(writer.AsReadOnlySpan(), DeliveryMethod.ReliableOrdered, peer);
        }
    }

    /// <summary>
    /// A client fired a signal flare inside a flare detector zone. The native
    /// activation only happened on that client's machine, so the host validates
    /// the report against the sender, decides the activation scope (squad wide by
    /// default) and broadcasts the canonical set, which every peer applies.
    /// </summary>
    private void OnFlareShootReportReceived(FlareShootReport report, NetPeer peer)
    {
        var sender = peer.Player;
        if (!FikaFlareExfilManager.ValidateReport(report, sender, out var reason))
        {
            _logger.LogWarning($"[FlareExfil] rejected report from peer {peer.Id}: reason={reason}");
            return;
        }

        var manager = FikaFlareExfilManager.Instance;
        if (manager == null)
        {
            _logger.LogWarning("[FlareExfil] rejected report from peer " + peer.Id +
                ": reason=flare manager is not active on the host");
            return;
        }

        // The host recomputes the squad from its own player table: a report may
        // not widen an activation to profiles the shooter is not grouped with.
        var squad = FikaFlareExfilManager.CollectSquadProfiles(sender);
        var canonical = manager.BuildCanonical(report.ZoneId, report.FlareType, report.ShooterProfileId,
            report.ShooterRaidId, squad);
        manager.ApplyCanonical(canonical, onHost: true);
        SendGenericPacket(EGenericSubPacketType.FlareShootReport, canonical, true);
    }

    private void OnInformationPacketReceived(InformationPacket packet, NetPeer peer)
    {
        // The client polls this request and can retry the ready message during
        // reconnect. Count the connection once for this raid's ready barrier.
        // A packet can arrive while the native server component is being
        // constructed (or while a reconnect is tearing down the old raid).
        // Treat the unbound state as not ready instead of dereferencing the
        // dictionaries before Init has populated them.
        if (_assignedPeerProfiles?.ContainsKey(peer) == true && packet.ReadyPlayers > 0 &&
            _readyPeers?.Add(peer) == true) ReadyClients++;

        var gameExists = _coopHandler != null && _coopHandler.LocalGameInstance != null;

        InformationPacket respondPackage = new()
        {
            RaidStarted = gameExists && FikaSingleton<IFikaGame>.Instance?.GameController?.RaidStarted == true,
            ReadyPlayers = ReadyClients,
            HostReady = HostReady,
            HostLoaded = RaidInitialized,
            HostReceivedLocation = LocationReceived,
            AmountOfPeers = (_netServer?.ConnectedPeersCount ?? 0) + 1
        };

        if (gameExists && packet.RequestStart && _assignedPeerProfiles?.ContainsKey(peer) == true)
        {
            if (FikaSingleton<IFikaGame>.Instance?.GameController is { } gameController)
            {
                gameController.RaidStarted = true;
                respondPackage.RaidStarted = true;
            }
        }

        if (gameExists && HostReady)
        {
            var gameTimer = _coopHandler.LocalGameInstance.GameController.GameInstance.GameTimer;
            var sessionTime = NativeNullable<Il2CppSystem.TimeSpan>.Read(gameTimer, "<SessionTime>k__BackingField");
            // During reconnect the native GameTimer can briefly exist without its
            // SessionTime payload. Do not read Nullable.Value in that window and
            // do not advertise a partially populated timing packet to the client.
            if (_gameStartTime is not { } gameStartTime || !sessionTime.HasValue)
            {
                respondPackage.HostReady = false;
            }
            else
            {
                respondPackage.GameTime = gameStartTime;
                respondPackage.SessionTime = sessionTime.Value.ToManaged();
                respondPackage.GameDateTime = (FikaSingleton<IFikaGame>.Instance.GameController as HostGameController).GameDateTime;
            }
        }

        if (_assignedPeerNetIds?.TryGetValue(peer, out var assignedNetId) == true && assignedNetId != 0)
        {
            // A player scav that walks into a running raid can announce itself
            // before it ever reports a remote net id, so fall back to the net id
            // the server assigned to this peer. Without the binding every proceed
            // request is answered with "unbound player".
            var remoteNetId = packet.RemoteNetId != 0 ? packet.RemoteNetId : assignedNetId;
            if (remoteNetId == assignedNetId &&
                _assignedPeerProfiles?.TryGetValue(peer, out var assignedProfile) == true &&
                _coopHandler?.Players?.TryGetValue(remoteNetId, out var player) == true &&
                player.ProfileId == assignedProfile && !ReferenceEquals(peer.Player, player))
            {
                peer.Player = player;
                _logger.LogInfo($"Bound peer {peer.Id} to player {player.NetId} ({player.ProfileId}), " +
                    $"remoteNetId={packet.RemoteNetId}");
            }
        }

        SendData(ref respondPackage, DeliveryMethod.ReliableOrdered);
    }

    private void OnCommonPlayerPacketReceived(CommonPlayerPacket packet, NetPeer peer)
    {
        if (!CanPeerSendCommonPlayerPacket(peer, packet.NetId, packet.Type)) return;
        if (_coopHandler.QueueSpawningPlayerState(packet)) return;
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            packet.Execute(playerToApply);
        }
    }

    private void OnInventoryPacketReceived(InventoryPacket packet, NetPeer peer)
    {
        // A client must not choose another player's inventory as the host-side
        // execution target. Reconnect associates the peer on the information
        // exchange before normal inventory traffic starts.
        if (peer.Player == null || peer.Player.NetId != packet.NetId)
        {
            _logger.LogWarning($"Rejected inventory operation for net id {packet.NetId} from an unbound peer");
            return;
        }

        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            // [CarExtract] A paid exfiltration point ("car extract") handover is a
            // local prediction on the paying client: the native interaction already
            // told the player the money was transferred. Rejecting the request here
            // is therefore the only way to stop a second payment, and the rejection
            // carries the authoritative inventory back so the client can roll its
            // prediction back.
            ExfiltrationPoint carExtractPoint = null;
            try
            {
                if (packet.Descriptor.TryCast<EFT.InventoryLogic.Operations.TransferExfiltrationPaymentOperationDescriptor>() is { } payment)
                {
                    carExtractPoint = FikaExfilManager.FindExfiltrationPoint(payment.PointName);
                    _logger.LogInfo($"[CarExtract] received a payment request from {playerToApply.ProfileId} " +
                        $"(net id {packet.NetId}) for point '{payment.PointName}': {FikaExfilManager.DescribePaymentItems(payment)}");
                    var rejection = carExtractPoint == null
                        ? $"There is no exfiltration point named '{payment.PointName}'."
                        : FikaExfilManager.HasPlayerPaid(carExtractPoint, playerToApply)
                            ? $"Player already paid for '{payment.PointName}'."
                            : null;
                    if (rejection != null)
                    {
                        _logger.LogWarning($"[CarExtract] rejected the payment from {playerToApply.ProfileId} for " +
                            $"'{payment.PointName}': {rejection}");
                        SendGenericPacketToPeer(EGenericSubPacketType.OperationCallback,
                            OperationCallbackPacket.FromValue(packet.NetId, packet.CallbackId, EOperationStatus.Failed, rejection), peer);
                        SendInventoryResyncToPeer(packet.NetId, packet.CallbackId, peer);
                        ResyncInventoryIdPacket rejectedResyncPacket = new(playerToApply.NetId);
                        SendDataToPeer(ref rejectedResyncPacket, DeliveryMethod.ReliableOrdered, peer);
                        return;
                    }
                }

                if (playerToApply.InventoryController is IOperationHandler inventoryController)
                {
                    var result = inventoryController.CreateOperationFromDescriptor(packet.Descriptor);
#if DEBUG
                    if (result.Succeeded)
                    {
                        ConsoleScreen.Log($"Received InvOperation: {result.Value.GetType().Name}, Id: {result.Value.Id}");
                    }
#endif

                    if (result.Failed)
                    {
                        _logger.LogError($"ItemControllerExecutePacket::Operation conversion failed: {result.Error}");
                        // The client's descriptor can name items the host never spawned
                        // (profile-filtered loot). Log enough to tell a resolver defect
                        // apart from a genuinely missing world item.
                        try
                        {
                            _logger.LogError($"ItemControllerExecutePacket::Descriptor {packet.Descriptor}");
                            if (packet.Descriptor is MoveOperationDescriptor moveDescriptor)
                            {
                                var world = Singleton<GameWorld>.Instance;
                                var fromId = moveDescriptor.From?.Container?.ContainerId;
                                var toId = moveDescriptor.To?.Container?.ContainerId;
                                _logger.LogError($"ItemControllerExecutePacket::Move from {fromId} to {toId} " +
                                    $"fromFound={(fromId != null && world?.FindItemById(fromId).Succeeded == true)} " +
                                    $"toFound={(toId != null && world?.FindItemById(toId).Succeeded == true)}");
                            }
                        }
                        catch (Exception diagnosticException)
                        {
                            _logger.LogError($"ItemControllerExecutePacket::Descriptor dump failed: {diagnosticException}");
                        }
                        SendGenericPacketToPeer(EGenericSubPacketType.OperationCallback,
                            OperationCallbackPacket.FromValue(packet.NetId, packet.CallbackId, EOperationStatus.Failed, result.Error.ToString()), peer);

                        SendInventoryResyncToPeer(packet.NetId, packet.CallbackId, peer);

                        ResyncInventoryIdPacket resyncPacket = new(playerToApply.NetId);
                        SendDataToPeer(ref resyncPacket, DeliveryMethod.ReliableOrdered, peer);
                        return;
                    }

                    var handler = _inventoryOperationHandlerPool.Get();
                    handler.Set(result, packet.CallbackId, packet.NetId, peer, this);
                    try
                    {
                        // Prepare the relay before acknowledging Started. If
                        // serialization fails, the client only sees Failed and
                        // the native operation has not been executed.
                        handler.SetRelayPayload(CreateInventoryRelayPayload(packet));
                        SendGenericPacketToPeer(EGenericSubPacketType.OperationCallback,
                            OperationCallbackPacket.FromValue(packet.NetId, packet.CallbackId, EOperationStatus.Started), peer);

                        // Native execution can finish after the receive packet
                        // has been returned to its pool.
                        handler.OperationResult.Value.Execute(handler.HandleResultDelegate);

                        if (carExtractPoint != null)
                        {
                            _logger.LogInfo($"[CarExtract] applied the payment from {playerToApply.ProfileId} on the host: " +
                                $"point '{carExtractPoint.Settings.Name}' status {carExtractPoint.Status}, " +
                                $"queued {carExtractPoint.QueuedPlayers.Count}");
                        }
                    }
                    catch (Exception exception)
                    {
                        // A native Execute may invoke its completion synchronously
                        // and still throw while unwinding. Its terminal status has
                        // already been sent in that case; do not contradict it.
                        if (handler.TerminalStatusReported)
                        {
                            _logger.LogError($"Inventory operation threw after completion for {packet.NetId}: {exception}");
                            return;
                        }
                        // No completion was sent. Invalidate the callback before
                        // sending the failure and authoritative snapshot below.
                        handler.Cancel();
                        throw;
                    }
                    finally
                    {
                        handler.FinishDispatch();
                    }
                }
                else
                {
                    throw new InvalidTypeException($"Inventory controller was not of type {nameof(IOperationHandler)}!");
                }
            }
            catch (Exception exception)
            {
                _logger.LogError($"ItemControllerExecutePacket::Exception thrown: {exception}");
                SendGenericPacketToPeer(EGenericSubPacketType.OperationCallback,
                            OperationCallbackPacket.FromValue(packet.NetId, packet.CallbackId, EOperationStatus.Failed, exception.Message), peer);

                SendInventoryResyncToPeer(packet.NetId, packet.CallbackId, peer);

                ResyncInventoryIdPacket resyncPacket = new(playerToApply.NetId);
                SendDataToPeer(ref resyncPacket, DeliveryMethod.ReliableOrdered, peer);
            }
        }
    }

    private void OnWeaponPacketReceived(WeaponPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            packet.Execute(playerToApply);
        }
    }
}
