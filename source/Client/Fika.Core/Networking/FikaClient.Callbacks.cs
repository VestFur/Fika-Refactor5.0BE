using Fika.Core.Main.BaseClasses;
using System;
using System.Collections.Generic;
using System.Linq;
using BitPacking;
using Comfort.Common;
using Diz.Jobs;
using EFT;
using EFT.AssetsManager;
using EFT.Ballistics;
using EFT.BufferZone;
using EFT.Communications;
using EFT.GlobalEvents;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.Vehicle;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Factories;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.ObservedClasses;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.Networking.Packets.Communication;
using Fika.Core.Networking.Packets.Debug;
using Fika.Core.Networking.Packets.FirearmController;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.Player;
using Fika.Core.Networking.Packets.Player.Common;
using Fika.Core.Networking.Packets.World;
using HarmonyLib;
using ClientRunddansController = Fika.Core.Main.ClientClasses.ClientRunddansController;
using ClientTransitController = Fika.Core.Main.ClientClasses.ClientTransitController;
using Grid = EFT.InventoryLogic.Grid;

namespace Fika.Core.Networking;

public sealed partial class FikaClient
{
    private void SpawnItemInInventoryPacketReceived(SpawnItemInInventoryPacket packet)
    {
        if (!CoopHandler.Players.TryGetValue(packet.NetId, out var player))
        {
            _logger.LogError($"Could not find player with id [{packet.NetId}] when trying to spawn item in inventoy");
            return;
        }

        var item = Singleton<ItemFactory>.Instance.CreateItem(packet.ItemId, packet.TemplateId, null);
        if (item == null)
        {
            _logger.LogError($"Failed to create item [{packet.ItemId}] with templateId [{LogSafe.Ref(packet.TemplateId)}]");
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

    private long _lastCharacterReannounce;
    private const long CharacterReannounceIntervalMs = 3000;

    private void OnProceedResponsePacketReceived(ProceedResponsePacket packet)
    {
        if (!string.IsNullOrEmpty(packet.Error) &&
            (packet.Error.Contains("unbound player", StringComparison.Ordinal)
             || packet.Error.Contains("Could not find player with id", StringComparison.Ordinal)))
        {
            // The host never received our character announcement (a player scav
            // entering a running raid can send it before the link is up), so it
            // has no player and rejects every proceed. Announce again.
            if (Environment.TickCount64 - _lastCharacterReannounce >= CharacterReannounceIntervalMs)
            {
                _lastCharacterReannounce = Environment.TickCount64;
                FikaGlobals.LogWarning("The host has no player for us yet, re-announcing our character");
                CoopGame.ReannounceLocalCharacter();
            }
        }

        MyPlayer.HandleCallbackResponse(packet.CallbackId, packet.Error);
    }

    private void OnClearSnapshotterPacketReceived(ClearSnapshotterPacket packet)
    {
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var player) && player.TryCast<ObservedPlayer>() is { } observedPlayer)
        {
            observedPlayer.HealthBar.RemoveAllActiveEffects();
            observedPlayer.Snapshotter.Clear();
            return;
        }

        _logger.LogError($"Could not find player {packet.NetId} to reset snapshotter");
    }

    private void OnSyncEventPacketReceived(SyncEventPacket packet)
    {
        _logger.LogInfo($"Received sync event: {packet.Type}");
        var reader = NetworkUtils.EventDataReader;
        reader.Reset();
        Array.Copy(packet.Data, reader.Buffer, packet.Data.Length);
        switch (packet.Type)
        {
            case 0:
                var initEvent = new TransitInitEvent();
                initEvent.Deserialize(ref reader);
                initEvent.Invoke();
                break;
            case 1:
                var updateEvent = new TransitUpdateEvent();
                updateEvent.Deserialize(ref reader);
                updateEvent.Invoke();
                break;
            case 2:
                var btrPurchaseEvent = new BtrServicePurchaseEvent();
                btrPurchaseEvent.Deserialize(ref reader);
                btrPurchaseEvent.Invoke(btrPurchaseEvent.PlayerRaidIds, btrPurchaseEvent.ServiceType);
                break;
        }
    }

    private void OnLoadingScreenPlayersPacketReceived(LoadingScreenPlayersPacket packet)
    {
        if (LoadingScreenUI.Instance != null)
        {
            for (var i = 0; i < packet.NetIds.Length; i++)
            {
                LoadingScreenUI.Instance.AddPlayer(packet.NetIds[i], packet.Nicknames[i]);
            }
        }
    }

    private void OnLoadingScreenPacketReceived(LoadingScreenPacket packet)
    {
        if (LoadingScreenUI.Instance != null)
        {
            // (analysis/loading-screen-peer-state) ApplyPeerProgress creates the row when this machine's
            // re-arm dropped it, instead of losing the value to the row lookup (see LoadingScreenUI).
            LoadingScreenUI.Instance.ApplyPeerProgress(packet.NetId, packet.Progress, "host");
        }
    }

    private void OnStashesPacketReceived(StashesPacket packet) => QueueStashes(packet);

    private void OnSyncTrapsPacketReceived(SyncTrapsPacket packet)
    {
        var reader = new BitReaderStream(packet.Data);
        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld.SyncModule != null)
        {
            gameWorld.SyncModule.Deserialize(reader);
        }
        else
        {
            _logger.LogError("Received SyncTrapsPacket but the SyncModule was null!");
        }
    }

    private void OnEventControllerInteractPacketReceived(EventControllerInteractPacket packet)
    {
        _logger.LogWarning("Ignored client-direction Runddans interaction; item changes require a host acknowledgement");
    }
    private void OnEventControllerEventPacketReceived(EventControllerEventPacket packet)
    {
        if (packet.Type == EventControllerEventPacket.EEventType.StartedEvent)
        {
            var world = Singleton<GameWorld>.Instance;
            var clientTransitController = FikaWorldControllers.Find<ClientTransitController>(world?.TransitController);
            if (clientTransitController != null)
            {
                clientTransitController.Native.EnablePoints(false);
                clientTransitController.UpdateTimers();
            }
            else
            {
                QueueRunddansStarted();
            }
            return;
        }

        if (packet.Type == EventControllerEventPacket.EEventType.RemoveItem)
        {
            QueueEventItemRemoval(packet.NetId, packet.ItemId);
            return;
        }

        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld != null)
        {
#if DEBUG
            _logger.LogInfo($"Received event: {packet.Event}");
#endif
            if (packet.Type == EventControllerEventPacket.EEventType.StateEvent && packet.Event != null)
            {
                QueueRunddansState(packet);
            }
            else if (packet.Type == EventControllerEventPacket.EEventType.MessageEvent && packet.Event != null)
            {
                if (FikaWorldControllers.Find<ClientRunddansController>(gameWorld.RunddansController) != null)
                    packet.Event.Invoke();
                else
                    QueueRunddansMessage(packet);
            }
        }
        else if (packet.Type == EventControllerEventPacket.EEventType.StateEvent && packet.Event != null)
        {
            QueueRunddansState(packet);
        }
        else if (packet.Type == EventControllerEventPacket.EEventType.MessageEvent && packet.Event != null)
        {
            QueueRunddansMessage(packet);
        }
    }

    private void OnInraidQuestPacketReceived(InRaidQuestPacket packet)
    {
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var player))
        {
            if (player.FikaQuests is ObservedQuestController controller)
            {
                MainThreadDispatcher.RunAsync(async () =>
                {
                    try { await controller.HandleInraidQuestPacketAsync(packet); }
                    catch (Exception exception)
                    {
                        _logger.LogError($"Quest inventory synchronization failed for {packet.NetId}: {exception}");
                    }
                }).Forget();
            }
        }
    }

    public void OnCharacterSyncPacketReceived(CharacterSyncPacket packet)
    {
        _missingIds.Clear();
        if (_coopHandler == null)
        {
            return;
        }

        if (packet.PlayerIds == null)
        {
            return;
        }

        _coopHandler.CheckIds(packet.PlayerIds, _missingIds);

        // (2026-10-17) The host's list is what the host has. When this machine's own net id is missing from
        // it, the host has no player for us and every request of ours is answered with "unbound player"
        // (weapon switches roll back to empty hands, item moves are never executed). The character is
        // announced again; the same 3 s interval as the unbound answer bounds it, and QueueProfile
        // deduplicates a repeated announcement, so this cannot spawn a second player.
        if (NetId != 0 && !packet.PlayerIds.Contains(NetId)
            && Environment.TickCount64 - _lastCharacterReannounce >= CharacterReannounceIntervalMs)
        {
            _lastCharacterReannounce = Environment.TickCount64;
            FikaGlobals.LogWarning($"The host's character list does not contain us (netId {NetId}), re-announcing our character");
            CoopGame.ReannounceLocalCharacter();
        }

        if (_missingIds.Count > 0)
        {
            RequestPacket request = new()
            {
                Type = ERequestSubPacketType.CharacterSync,
                RequestSubPacket = new RequestSubPackets.RequestCharactersPacket(_missingIds)
            };

            SendData(ref request, DeliveryMethod.ReliableOrdered);
        }
    }

    private void OnRequestPacketReceived(RequestPacket packet)
    {
        packet.RequestSubPacket.HandleResponse();
    }

    private void OnWorldPacketReceived(WorldPacket packet)
    {
        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld == null)
        {
            // The world is replaced by a transit: keep the packet of that swap window (as bytes, see
            // FikaClient.TryQueueWorldPacket - the receive path hands the same WorldPacket instance to
            // every receive) so it can be replayed into the new world. Outside the swap window a world
            // packet cannot be applied at all and is counted instead of logged once per packet.
            if (TryQueueWorldPacket(ref packet, "OnWorldPacketReceived"))
            {
                LoadSync.Trace("world packet queued while the world was missing (ragdolls=" + (packet?.RagdollPackets?.Count ?? 0)
                    + ", grenades=" + (packet?.GrenadePackets?.Count ?? 0) + ", artillery=" + (packet?.ArtilleryPackets?.Count ?? 0)
                    + ", syncObjects=" + (packet?.SyncObjectPackets?.Count ?? 0) + ", loot=" + (packet?.LootSyncStructs?.Count ?? 0) + ")");
                // The receive path keeps one WorldPacket instance for every receive
                // (NetPacketProcessor.SubscribeReusable), and the payload was copied to bytes above, so
                // the shared instance is flushed exactly like the applied path does. Without this the
                // next receive would deserialize on top of this packet's lists.
                packet?.Flush();
                return;
            }

            ReportDroppedWorldPacket("OnWorldPacketReceived");
            return;
        }

        if (packet.RagdollPackets != null)
        {
            foreach (var ragdollPacket in packet.RagdollPackets)
            {
                if (gameWorld.ObservedPlayersCorpses.TryGetValue(ragdollPacket.Id, out var corpse) && corpse.HasRagdoll)
                {
                    corpse.ApplyNetPacket(ragdollPacket);
                    if (ragdollPacket.Done && ragdollPacket.TransformSyncs != null)
                    {
                        corpse.ForceApplyTransformSync(ragdollPacket.TransformSyncs);
                    }
                }
                else if (FikaClientWorld != null)
                {
                    FikaClientWorld.RagdollPackets.Add(ragdollPacket);
                }
            }
        }

        if (packet.ArtilleryPackets.Count > 0)
        {
            var packets = new Il2CppSystem.Collections.Generic.List<ArtilleryProjectileSyncPacket>();
            foreach (var packetItem in packet.ArtilleryPackets) packets.Add(packetItem);
            gameWorld.ClientShellingController.SyncProjectilesStates(ref packets);
        }

        for (var i = 0; i < packet.GrenadePackets.Count; i++)
        {
            var throwablePacket = packet.GrenadePackets[i];
            var grenades = gameWorld.Grenades;
            if (grenades.TryGetByKey(throwablePacket.Id, out var throwable))
            {
                throwable.ApplyNetPacket(throwablePacket, packet.RemoteTime);
            }
        }

        FikaClientWorld.SyncObjectPackets.AddRange(packet.SyncObjectPackets);
        FikaClientWorld.LootSyncPackets.AddRange(packet.LootSyncStructs);

        packet.Flush();
    }

    private void OnSideEffectPacketReceived(SideEffectPacket packet)
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
            sideEffectComponent.Value = packet.Value;
            item.RaiseRefreshEvent(false, false);
            return;
        }
        _logger.LogError("OnSideEffectPacketReceived: SideEffectComponent was not found!");
    }

    private void OnLoadingProfilePacketReceived(LoadingProfilePacket packet)
    {
        if (packet.Profiles != null)
        {
#if DEBUG
            _logger.LogWarning($"OnLoadingProfilePacketReceived: Received {packet.Profiles.Count} profiles");
#endif
            FikaBackendUtils.AddPartyMembers(packet.Profiles);
            return;
        }

        _logger.LogWarning("OnLoadingProfilePacketReceived: Profiles was null!");
    }

    private void OnPingPacketReceived(PingPacket packet)
    {
        if (FikaPlugin.Instance.Settings.UsePingSystem.Value)
        {
            PingFactory.ReceivePing(packet.PingLocation, packet.PingType, packet.PingColor, packet.Nickname, packet.LocaleId);
        }
    }

    private void OnBotStatePacketReceived(BotStatePacket packet)
    {
        if (_coopHandler == null)
        {
            return;
        }

        if (_coopHandler.Players.TryGetValue(packet.NetId, out var bot))
        {
            switch (packet.Type)
            {
                case BotStatePacket.EStateType.DisposeBot:
                    {
                        if (!bot.gameObject.activeSelf)
                        {
                            bot.gameObject.SetActive(true);
                        }

                        if (_coopHandler.Players.Remove(packet.NetId))
                        {
                            bot.Dispose();
                            AssetPoolObject.ReturnToPool(bot.gameObject, true);
#if DEBUG
                            _logger.LogInfo("Disposing bot: " + packet.NetId);
#endif
                        }
                        else
                        {
                            _logger.LogWarning("Unable to dispose of bot: " + packet.NetId);
                        }
                    }
                    break;
                case BotStatePacket.EStateType.EnableBot:
                    {
                        if (!bot.gameObject.activeSelf)
                        {
#if DEBUG
                            _logger.LogWarning("Enabling " + packet.NetId);
#endif
                            bot.gameObject.SetActive(true);
                        }
                        else
                        {
                            _logger.LogWarning($"Received packet to enable {bot.ProfileId}, netId {packet.NetId} but the bot was already enabled!");
                        }
                    }
                    break;
                case BotStatePacket.EStateType.DisableBot:
                    {
                        if (bot.gameObject.activeSelf)
                        {
#if DEBUG
                            _logger.LogWarning("Disabling " + packet.NetId);
#endif
                            bot.gameObject.SetActive(false);
                        }
                        else
                        {
                            _logger.LogWarning($"Received packet to disable {bot.ProfileId}, netId {packet.NetId} but the bot was already disabled!");
                        }
                    }
                    break;
            }
        }
    }

    private void OnTransitEventPacketReceived(TransitEventPacket packet)
    {
        // The reload-start signal of a transit reload is answered *before* the controller lookup: in that
        // window this machine has no world at all and therefore no transit controller, and queueing the
        // signal for the controller of the next raid would deliver it far too late
        // (analysis/transit-reload-sync).
        if (TransitReloadGate.HandlePacket(packet))
        {
            return;
        }

        var world = Singleton<GameWorld>.Instance;
        if (FikaWorldControllers.Find<ClientTransitController>(world?.TransitController) is not { } transit)
        {
            QueueTransitEvent(packet);
            return;
        }
        if (packet.EventType == TransitEventPacket.ETransitEventType.Init) transit.ReceiveSnapshot(packet);
        else transit.ReceiveEvent(packet);
    }
    private void OnSyncTransitControllersPacketReceived(SyncTransitControllersPacket packet)
    {
        var transitController = Singleton<GameWorld>.Instance?.TransitController;
        if (transitController != null)
        {
            transitController.summonedTransits[packet.ProfileId] = new(packet.RaidId, packet.Count, packet.Maps, packet.Events);
            return;
        }
        QueueTransitHistory(packet);
    }

    private void OnSpawnItemPacketReceived(SpawnItemPacket packet)
    {
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            FikaGlobals.SpawnItemInWorld(packet.Item, playerToApply);
        }
    }

    private void OnNetworkSettingsPacketReceived(NetworkSettingsPacket packet)
    {
        _logger.LogInfo($"Received settings from server. SendRate: {packet.SendRate}, NetId: {packet.NetId}, AllowVOIP: {packet.AllowVOIP}");
        _sendRate = packet.SendRate;
        NetId = packet.NetId;
        AllowVOIP = packet.AllowVOIP;
        StrictInventorySync = packet.StrictSync;

        _genericPacket.NetId = packet.NetId;

        LoadingScreenUI.Instance.AddPlayer(NetId, FikaBackendUtils.PMCName);
        var loadingPacket = new LoadingScreenPlayersPacket
        {
            NetIds = [NetId],
            Nicknames = [FikaBackendUtils.PMCName]
        };
        SendData(ref loadingPacket, DeliveryMethod.ReliableUnordered, true);
    }

    private void OnResyncInventoryIdPacketReceived(ResyncInventoryIdPacket packet)
    {
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            if (playerToApply.TryCast<ObservedPlayer>() is { } observedPlayer)
            {
                if (observedPlayer.InventoryController?.TryCast<ObservedInventoryController>() is { } observedController)
                {
                    observedController.SetNewID(packet.MongoId);
                }
                return;
            }

            if (playerToApply.IsYourPlayer)
            {
                ResyncInventoryIdPacket response = new(playerToApply.NetId)
                {
                    MongoId = playerToApply.InventoryController.CurrentId
                };
                SendData(ref response, DeliveryMethod.ReliableOrdered, true);
            }
        }
    }

    private void OnInventoryResyncPacketReceived(InventoryResyncPacket packet)
    {
        if (_coopHandler?.Players.TryGetValue(packet.NetId, out var playerToApply) != true || !playerToApply.IsYourPlayer)
        {
            return;
        }

        if (playerToApply.InventoryController?.TryCast<ClientInventoryController>() is { } clientController)
        {
            clientController.ReceiveInventoryResync(packet);
        }
    }

    private void OnBufferZonePacketReceived(BufferZonePacket packet)
    {
        switch (packet.Status)
        {
            case EFT.BufferZone.EBufferZoneData.Availability:
            case EFT.BufferZone.EBufferZoneData.DisableByZryachiyDead:
            case EFT.BufferZone.EBufferZoneData.DisableByPlayerDead:
                {
                    BufferZoneController.Instance.SetInnerZoneAvailabilityStatus(packet.Available, packet.Status);
                }
                break;
            case EFT.BufferZone.EBufferZoneData.PlayerAccessStatus:
                {
                    var accessPlayer = Singleton<GameWorld>.Instance.GetAlivePlayerByProfileID(packet.ProfileId);
                    if (accessPlayer != null) BufferZoneController.Instance.SetPlayerAccessStatus(accessPlayer.RaidId, packet.Available);
                }
                break;
            case EFT.BufferZone.EBufferZoneData.PlayerInZoneStatusChange:
                {
                    var zonePlayer = Singleton<GameWorld>.Instance.GetAlivePlayerByProfileID(packet.ProfileId);
                    if (zonePlayer != null) BufferZoneController.Instance.SetPlayerInZoneStatus(zonePlayer.RaidId, packet.Available);
                }
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// BJ (analysis/terminal-rauf-card): the sentry access line of the terminal. The host owns the
    /// emission of the native event, so this is where a peer learns that a player passed the card check -
    /// and applies it through the native entry point of its own sentries
    /// (<c>SentryBossLogic.OnTerminalAccessGained</c>), so its <c>BossSentryHostilityRule</c> answers the
    /// same way the host's does.
    /// </summary>
    private void OnSentryAccessPacketReceived(SentryAccessPacket packet)
    {
        Main.Components.FikaSentryAccess.Receive(packet.ProfileId, packet.RaidId, packet.Granted, packet.FromClient != 0,
            packet.HostAccessYes, packet.HostAccessNo, packet.HostAllies);
    }

    private void OnFlareSuccessPacketReceived(FlareSuccessPacket packet)
    {
        if (Singleton<GameWorld>.Instance.MainPlayer.ProfileId == packet.ProfileId)
        {
            if (!packet.Success)
            {
                NotificationManager.DisplayNotification(new CustomNotification("AirplaneDelayMessage".Localized(null),
                            ENotificationDurationType.Default, ENotificationIconType.Default, new Il2CppSystem.Nullable<Color>()));
            }
        }
    }

    private void OnBTRInteractionPacketReceived(BTRInteractionPacket packet)
    {
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld.BtrController != null && gameWorld.BtrController.BtrView != null)
            {
                if (packet.IsResponse && packet.Status != EBtrInteractionStatus.Confirmed)
                {
                    if (packet.Status - EBtrInteractionStatus.Blacklisted <= 2 && playerToApply.IsYourPlayer)
                    {
                        GlobalEventsController.CreateEvent<BtrNotificationInteractionMessageEvent>().Invoke(playerToApply.RaidId, packet.Status);
                        return;
                    }

                    return;
                }

                gameWorld.BtrController.BtrView.Interaction(playerToApply, packet.Data);
            }
        }
    }

    private void OnSpawnSyncObjectPacketReceived(SpawnSyncObjectPacket packet)
    {
        packet.SubPacket.Execute();
    }


    private void OnWorldLootPacketReceived(WorldLootPacket packet)
    {
        var fikaGame = FikaSingleton<IFikaGame>.Instance;
        if (fikaGame != null)
        {
            // An unready host answers with an empty payload so the client polls
            // again. Reading it as a descriptor would throw out of the packet
            // handler and abort the rest of this receive.
            if (packet.Data == null || packet.Data.Length == 0)
            {
                _emptyLootDescriptors++;
                _logger.LogWarning($"Host has no loot descriptor ready yet " +
                    $"({_emptyLootDescriptors}/{MaxEmptyLootDescriptors}), retrying");
                return;
            }

            var reader = new NetDataReader(packet.Data);
            var lootData = reader.GetEFTLootDataDescriptor();
            var lootItems = ItemBinarySerializer.DeserializeLootData(lootData);
            if (lootItems == null)
            {
                _logger.LogError("Host returned a null loot descriptor");
                return;
            }

            // The client polls for loot while the host may still be building the
            // authoritative raid descriptor, so an empty answer is an expected
            // intermediate state and must not abort the packet handler. Keep
            // polling; only a host that never produces loot ends the wait.
            if (lootItems.Count < 1)
            {
                _emptyLootDescriptors++;
                if (_emptyLootDescriptors < MaxEmptyLootDescriptors)
                {
                    _logger.LogWarning($"Host returned an empty loot descriptor " +
                        $"({_emptyLootDescriptors}/{MaxEmptyLootDescriptors}), retrying");
                    return;
                }

                _logger.LogError($"Host kept returning an empty loot descriptor after " +
                    $"{MaxEmptyLootDescriptors} attempts, continuing without world loot");
            }

            fikaGame.GameController.LootItems = lootItems;
            if (fikaGame.GameController is ClientGameController clientGameController)
                clientGameController.HasReceivedLoot = true;
        }
    }

    private void OnStatisticsPacketReceived(StatisticsPacket packet)
    {
        ServerFPS = packet.ServerFPS;
    }

    private void OnInteractableInitPacketReceived(InteractableInitPacket packet)
    {
        if (!packet.IsRequest)
        {
            var world = Singleton<GameWorld>.Instance.World;
            if (world.Interactables == null)
            {
                var interactables = new Il2CppSystem.Collections.Generic.Dictionary<string, int>();
                foreach (var item in packet.Interactables) interactables[item.Key] = item.Value;
                world.RegisterNetworkInteractionObjects(interactables);
                var fikaGame = FikaSingleton<IFikaGame>.Instance;
                if (fikaGame != null)
                {
                    if (fikaGame.GameController is ClientGameController clientGameController)
                        clientGameController.InteractablesInitialized = true;
                }
            }
        }
    }

    private void OnQuestDropItemPacketReceived(QuestDropItemPacket packet)
    {
        if (MyPlayer == null)
        {
            return;
        }

        if (MyPlayer.HealthController.IsAlive)
        {
            if (MyPlayer.FikaQuests is ClientSharedQuestController sharedQuestController)
            {
                sharedQuestController.ReceiveQuestDropItemPacket(packet);
            }
        }
    }

    private void OnQuestItemPacketReceived(QuestItemPacket packet)
    {
        if (MyPlayer == null)
        {
            return;
        }

        if (MyPlayer.HealthController.IsAlive)
        {
            if (MyPlayer.FikaQuests is ClientSharedQuestController sharedQuestController)
            {
                sharedQuestController.ReceiveQuestItemPacket(packet);
            }
        }
    }

    private void OnQuestConditionPacketReceived(QuestConditionPacket packet)
    {
        if (MyPlayer == null)
        {
            return;
        }

        if (MyPlayer.HealthController.IsAlive)
        {
            if (MyPlayer.FikaQuests is ClientSharedQuestController sharedQuestController)
            {
                sharedQuestController.ReceiveQuestPacket(packet);
            }
        }
    }

    private void OnTextMessagePacketReceived(TextMessagePacket packet)
    {
        _logger.LogInfo($"Received message from: {packet.Nickname}, Message: {packet.Message}");

        if (_fikaChat != null)
        {
            _fikaChat.AddMessage(new(packet.Nickname, packet.Message), true);
            _fikaChat.OpenChat(true);
        }
    }

    private void OnOperationCallbackPacketReceived(OperationCallbackPacket packet)
    {
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var player) && player.IsYourPlayer)
        {
            player.HandleCallbackFromServer(packet);
        }
    }

    private void OnGenericPacketReceived(GenericPacket packet)
    {
        if (packet.Type is EGenericSubPacketType.InventoryOperation)
        {
            OnInventoryPacketReceived((InventoryPacket)packet.SubPacket);
            return;
        }
        if (packet.Type is EGenericSubPacketType.OperationCallback)
        {
            OnOperationCallbackPacketReceived((OperationCallbackPacket)packet.SubPacket);
            return;
        }
        packet.Execute();
    }

    /// <summary>
    /// The last host state this client was told about, so the [LoadSync] line is written on a change
    /// and not once per response (the loading wait polls once per second, for minutes).
    /// </summary>
    private bool _lastHostReceivedLocation;
    private bool _lastHostLoaded;
    private bool _lastHostReady;
    private bool _hostStateReported;

    private void OnInformationPacketReceived(InformationPacket packet)
    {
        // Counted before anything else: the loading wait uses this counter to tell a host that is
        // still loading (it answers, only with HostReceivedLocation=false) from a host that is gone
        // (no answer at all). Without that split the client aborted a perfectly healthy join after
        // its fixed 40 s cap (analysis/loading-screen-sync).
        NoteHostResponse();
        ReportHostState(packet);

        if (_coopHandler != null)
        {
            var fikaGame = FikaSingleton<IFikaGame>.Instance;
            if (fikaGame != null)
            {
                fikaGame.GameController.RaidStarted = packet.RaidStarted;
                if (packet.HostReady)
                {
                    fikaGame.GameController.SetClientTime(packet.GameTime, packet.SessionTime, packet.GameDateTime);
                }
            }
        }
        ReadyClients = packet.ReadyPlayers;
        HostReady = packet.HostReady;
        HostLoaded = packet.HostLoaded;
        HostReceivedLocation = packet.HostReceivedLocation;
        if (packet.AmountOfPeers > 0)
        {
            FikaSingleton<IFikaNetworkManager>.Instance.PlayerAmount = packet.AmountOfPeers;
        }
    }

    /// <summary>
    /// One [LoadSync] line per *change* of the host's handshake state, i.e. the moment this client may
    /// continue its own raid start. The four states are the whole gate between the two sides:
    /// HostReceivedLocation (the host is at its own CreateFikaGameCore for this raid), HostLoaded
    /// (the host finished InitPlayer), HostReady (the host's countdown runs), plus the response counter
    /// that proves the host is still there.
    /// </summary>
    private void ReportHostState(InformationPacket packet)
    {
        if (_hostStateReported
            && _lastHostReceivedLocation == packet.HostReceivedLocation
            && _lastHostLoaded == packet.HostLoaded
            && _lastHostReady == packet.HostReady)
        {
            return;
        }

        _hostStateReported = true;
        _lastHostReceivedLocation = packet.HostReceivedLocation;
        _lastHostLoaded = packet.HostLoaded;
        _lastHostReady = packet.HostReady;

        LoadSync.Report("FikaClient.OnInformationPacketReceived",
            "host state changed: hostReceivedLocation=" + packet.HostReceivedLocation + ", hostLoaded=" + packet.HostLoaded
            + ", hostReady=" + packet.HostReady + ", readyClients=" + packet.ReadyPlayers + ", responses=" + HostResponseCount
            + ", connected=" + (ServerConnection != null)
            + " (" + LoadSync.DescribeScreen() + ") => "
            + (packet.HostReceivedLocation
                ? "this client may create its game object"
                : "the host has not reached this raid's game creation yet; the client keeps waiting"));
    }

    private void OnCommonPlayerPacketReceived(CommonPlayerPacket packet)
    {
        if (_coopHandler.QueueSpawningPlayerState(packet)) return;
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            packet.Execute(playerToApply);
        }
    }

    private void OnInventoryPacketReceived(InventoryPacket packet)
    {
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            HandleInventoryPacket(packet, playerToApply);
        }
    }

    private void OnWeaponPacketReceived(WeaponPacket packet)
    {
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            packet.Execute(playerToApply);
        }
    }

    private void OnHalloweenEventPacketReceived(HalloweenEventPacket packet)
    {
        var controller = HalloweenEventController.Instance;

        if (controller == null)
        {
            _logger.LogError("OnHalloweenEventPacketReceived: controller was null!");
            return;
        }

        if (packet.SyncEvent == null)
        {
            _logger.LogError("OnHalloweenEventPacketReceived: event was null!");
            return;
        }

        packet.SyncEvent.Invoke();
    }

    private void OnKolotunStatePacketReceived(KolotunStatePacket packet)
    {
        QueueKolotunState(packet);
    }

    /// <summary>
    /// Used by server to notify of important events, usually errors
    /// </summary>
    /// <param name="packet">The packet containing the data</param>
    private void OnMessagePacketReceived(MessagePacket packet)
    {
            FikaNotifications.DisplayMessageNotification(packet.Message.Localized(), packet.NotificationDurationType,
            packet.NotificationIconType, new Il2CppSystem.Nullable<Color>(packet.Color));
    }
}
