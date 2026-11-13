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
            // 主机从未收到我们的角色宣告(玩家 scav
            // 进入正在进行的 raid 时可能在连接建立之前就发送),因此它
            // 没有对应玩家,并拒绝每一次 proceed。重新宣告一次。
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
            // (analysis/loading-screen-peer-state)当本机的
            // 重新装载把它丢掉时,由 ApplyPeerProgress 创建该行,而不是让值在查行时丢失(见 LoadingScreenUI)。
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

        // (2026-10-17)主机的列表就是主机所拥有的东西。当本机自己的 net id 不在
        // 其中时,主机就没有我们的玩家,我们的每个请求都会被回答 "unbound player"
        // (换武器会回滚到空手,物品移动永不执行)。角色会被
        // 重新宣告;与 unbound 回答相同的 3 秒间隔为它设界,而 QueueProfile
        // 会对重复宣告去重,因此这不会多生成一个玩家。
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
            // 世界被一次转发替换:保留该交换窗口的数据包(以字节保存,见
            // FikaClient.TryQueueWorldPacket——接收路径会把同一个 WorldPacket 实例交给
            // 每一次接收),以便它能被重放进新世界。在交换窗口之外,世界
            // 数据包根本无法应用,因此只做计数而不是每个包记一条日志。
            if (TryQueueWorldPacket(ref packet, "OnWorldPacketReceived"))
            {
                // 接收路径对每一次接收都复用同一个 WorldPacket 实例
                // (NetPacketProcessor.SubscribeReusable),而载荷上面已被复制成字节,因此
                // 这个共享实例会像已应用路径那样被清空。不这样做的话,
                // 下一次接收就会在这个数据包的列表之上反序列化。
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
        // 转发重载的重载开始信号会在查找控制器*之前*就得到应答:在那个
        // 窗口里本机根本没有世界,因此也没有转发控制器,而把该信号排入
        // 下一场 raid 的控制器队列会把它的投递拖到太晚
        // (analysis/transit-reload-sync)。
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
    /// BJ(analysis/terminal-rauf-card):终端的哨兵通行线。原生事件的发出归主机所有,
    /// 因此这里是某个对端得知某名玩家通过了卡检的地方——
    /// 并通过它自己哨兵的原生入口点应用这件事
    /// (<c>SentryBossLogic.OnTerminalAccessGained</c>),使它自己的 <c>BossSentryHostilityRule</c>
    /// 给出与主机相同的回答。
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
            // 未就绪的主机会回一个空载荷,以便客户端
            // 再次轮询。若把它当成 descriptor 来读,就会从数据包
            // 处理器中抛出,并中断本次接收的其余部分。
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

            // 客户端轮询战利品时,主机可能仍在构建
            // 权威 raid descriptor,因此空回答是一种预期的
            // 中间状态,不得中断数据包处理器。继续
            // 轮询;只有始终不产出战利品的主机才会结束这次等待。
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
    /// 本客户端最后被告知的主机状态,这样 [LoadSync] 行只写在状态变化时,
    /// 而不是每个响应写一次(加载等待每秒轮询一次,要持续好几分钟)。
    /// </summary>
    private bool _lastHostReceivedLocation;
    private bool _lastHostLoaded;
    private bool _lastHostReady;
    private bool _hostStateReported;

    private void OnInformationPacketReceived(InformationPacket packet)
    {
        // 先于其他一切进行计数:加载等待用这个计数器来区分仍在加载的主机
        // (它会有回答,只是 HostReceivedLocation=false)与已经消失的主机
        // (完全无回答)。没有这一区分,客户端曾在一次完全健康的加入
        // 达到固定的 40 秒上限后中止它(analysis/loading-screen-sync)。
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
    /// 主机握手状态每*变化*一次写一行 [LoadSync],也就是本客户端可以
    /// 继续自己 raid 启动的那一刻。这四个状态就是两侧之间的全部闸门:
    /// HostReceivedLocation(主机正处于自己这场 raid 的 CreateFikaGameCore)、HostLoaded
    /// (主机已完成 InitPlayer)、HostReady(主机的倒计时在跑),外加那个响应计数器
    /// 它证明主机仍然在线。
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
    /// 供服务器通知重要事件使用,通常是错误
    /// </summary>
    /// <param name="packet">携带数据的数据包</param>
    private void OnMessagePacketReceived(MessagePacket packet)
    {
            FikaNotifications.DisplayMessageNotification(packet.Message.Localized(), packet.NotificationDurationType,
            packet.NotificationIconType, new Il2CppSystem.Nullable<Color>(packet.Color));
    }
}
