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

    private long _lastCharacterReannounce;
    private const long CharacterReannounceIntervalMs = 3000;

    private void OnProceedResponsePacketReceived(ProceedResponsePacket packet)
    {
        if (!string.IsNullOrEmpty(packet.Error) && packet.Error.Contains("unbound player", StringComparison.Ordinal))
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
            LoadingScreenUI.Instance.SetProgress(packet.NetId, packet.Progress);
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
            _logger.LogError("OnNewWorldPacketReceived: GameWorld was null!");
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

    private void OnReconnectPacketReceived(ReconnectPacket packet)
    {
        if (!packet.IsRequest)
        {
            var fikaGame = FikaSingleton<IFikaGame>.Instance;
            if (fikaGame is not CoopGame reconnectGame || !FikaBackendUtils.IsReconnect ||
                !reconnectGame.AcceptsReconnectPacket(packet))
            {
                return;
            }

            // The profile reply precedes local-player creation. World replies
            // belong only to the later snapshot request in this raid.
            if (packet.Type is not (ReconnectPacket.EReconnectDataType.OwnCharacter or
                ReconnectPacket.EReconnectDataType.Failed) &&
                (MyPlayer?.ProfileId != packet.ProfileId || ReconnectDone)) return;

            switch (packet.Type)
            {
                case ReconnectPacket.EReconnectDataType.Throwable:
                    if (packet.ThrowableData != null)
                    {
#if DEBUG
                        _logger.LogWarning("Received reconnect packet for throwables: " + packet.ThrowableData.Count);
#endif
                        var localizedString = LocaleUtils.UI_SYNC_THROWABLES.Localized();
                        fikaGame.GameController.GameInstance.SetMatchmakerStatus(localizedString);
                        var smokeData = new Il2CppSystem.Collections.Generic.List<SmokeGrenadeNetworkData>();
                        foreach (var item in packet.ThrowableData) smokeData.Add(item);
                        Singleton<GameWorld>.Instance.OnSmokeGrenadesDeserialized(smokeData);
                    }
                    break;
                case ReconnectPacket.EReconnectDataType.Interactives:
                    {
                        if (packet.InteractivesData != null)
                        {
#if DEBUG
                            _logger.LogWarning("Received reconnect packet for interactives: " + packet.InteractivesData.Count);
#endif
                            var localizedString = LocaleUtils.UI_SYNC_INTERACTABLES.Localized();
                            var worldInteractiveObjects = Singleton<GameWorld>.Instance.World._interactableObjectsForNetSync;
                            Dictionary<int, WorldInteractiveObject.InteractiveObjectStatusInfo> netIdDictionary = [];
                            {
                                foreach (var data in packet.InteractivesData)
                                {
                                    netIdDictionary.Add(data.NetId, data);
                                }
                            }

                            float total = packet.InteractivesData.Count;
                            var progress = 0f;
                            foreach (var item in worldInteractiveObjects)
                            {
                                if (netIdDictionary.TryGetValue(item.NetId, out var value))
                                {
                                    progress++;
                                    fikaGame.GameController.GameInstance.SetMatchmakerStatus(localizedString, progress / total);
                                    item.SetInitialSyncState(value);
                                }
                            }
                        }
                        break;
                    }
                case ReconnectPacket.EReconnectDataType.LampControllers:
                    {
                        if (packet.LampStates != null)
                        {
#if DEBUG
                            _logger.LogWarning("Received reconnect packet for lampStates: " + packet.LampStates.Count);
#endif
                            var localizedString = LocaleUtils.UI_SYNC_LAMP_STATES.Localized();
                            var lampControllerDictionary = LocationScene.GetAllObjects<LampController>(true).AsManaged()
                                                    .Where(FikaGlobals.LampControllerNetIdNot0)
                                                    .ToDictionary(FikaGlobals.LampControllerGetNetId);

                            float total = packet.LampStates.Count;
                            var progress = 0f;
                            foreach (var lampState in packet.LampStates)
                            {
                                progress++;
                                fikaGame.GameController.GameInstance.SetMatchmakerStatus(localizedString, progress / total);
                                if (lampControllerDictionary.TryGetValue(lampState.Key, out var lampController))
                                {
                                    if (lampController.LampState != (Turnable.EState)lampState.Value)
                                    {
                                        lampController.Switch((Turnable.EState)lampState.Value);
                                    }
                                }
                            }
                        }
                        break;
                    }
                case ReconnectPacket.EReconnectDataType.Windows:
                    {
#if DEBUG
                        _logger.LogWarning("Received reconnect packet for windowBreakers: " +
                            (packet.WindowBreakerStates?.Count ?? 0));
#endif
                        if (packet.WindowBreakerStates != null)
                        {
                            var windowBreakerStates = packet.WindowBreakerStates;
                            var localizedString = LocaleUtils.UI_SYNC_WINDOWS.Localized();

                            float total = packet.WindowBreakerStates.Count;
                            var progress = 0f;
                            foreach (var windowBreaker in LocationScene.GetAllObjects<WindowBreaker>(true).AsManaged()
                                .Where(FikaGlobals.WindowBreakerAvailableToSync))
                            {
                                if (windowBreakerStates.TryGetValue(windowBreaker.NetId, out var hitPosition))
                                {
                                    progress++;

                                    fikaGame.GameController.GameInstance.SetMatchmakerStatus(localizedString, progress / total);
                                    try
                                    {
                                        // DamageInfo is a native reference
                                        // wrapper on SPT 5. A default CLR
                                        // value has no native object behind it.
                                        DamageInfo damageInfo = new();
                                        damageInfo.HitPoint = hitPosition;
                                        windowBreaker.MakeHit(ref damageInfo, true);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError("OnReconnectPacketReceived: Exception caught while setting up WindowBreakers: " + ex.Message);
                                    }
                                }
                            }
                        }
                        break;
                    }
                case ReconnectPacket.EReconnectDataType.OwnCharacter:
#if DEBUG
                    _logger.LogWarning("Received reconnect packet for own player");
#endif
                    fikaGame.GameController.GameInstance.SetMatchmakerStatus(LocaleUtils.UI_RECEIVE_OWN_PLAYERS.Localized());
                    if (fikaGame is CoopGame coopGame)
                    {
                        coopGame.ReceiveReconnectProfile(packet);
                    }
                    break;
                case ReconnectPacket.EReconnectDataType.Finished:
                    reconnectGame.ReceiveReconnectRevival(packet);
                    QueueBtrPassengers(packet.BtrPassengers);
                    QueueBtrServices(packet.BtrServices);
                    fikaGame.GameController.GameInstance.SetMatchmakerStatus(LocaleUtils.UI_FINISH_RECONNECT.Localized());
                    ReconnectDone = true;
                    break;
                case ReconnectPacket.EReconnectDataType.Failed:
                    // AcceptsReconnectPacket already checked the pending profile
                    // (or the active world request). During the first request
                    // CoopGame.Profile is deliberately null, so ProfileId is
                    // not available for a second comparison here.
                    ReconnectError = packet.Error;
                    break;
                case ReconnectPacket.EReconnectDataType.WorldObjects:
                    if (packet.WorldObjectUpdates != null)
                    {
                        // The peer can disconnect while the native world is
                        // being torn down. Do not let a late snapshot throw
                        // from the network callback and leave the reconnect
                        // task waiting without an actionable error.
                        if (FikaClientWorld == null)
                        {
                            ReconnectError = "Reconnect world was unavailable while applying world objects";
                            break;
                        }
                        FikaClientWorld.SyncObjectPackets.AddRange(packet.WorldObjectUpdates);
                    }
                    break;
                case ReconnectPacket.EReconnectDataType.Quests:
                    if (MyPlayer != null && MyPlayer.FikaQuests is ClientQuestController clientQuestController)
                    {
                        clientQuestController.ReceiveReconnectQuestSync(packet.QuestSyncPackets);
                    }
                    else
                    {
                        _logger.LogError("Could not find own player to resync quests from host during reconnect");
                    }
                    break;
            }
        }
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

    private void OnInformationPacketReceived(InformationPacket packet)
    {
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
