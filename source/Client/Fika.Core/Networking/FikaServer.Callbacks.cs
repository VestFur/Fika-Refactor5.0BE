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

// BJ（analysis/terminal-rauf-card）：终端的哨兵通行日志行，主机侧——见
// FikaSentryAccess 以及 SentryTerminalAccessPatches.cs 中的那些补丁。
using System;
using System.Collections.Generic;
using System.Linq;
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
    /// 尝试查找具有给定 id 的物品
    /// </summary>
    /// <param name="itemId">要搜索的 id</param>
    /// <param name="item">找到时的物品</param>
    /// <returns>找到时为 <see langword="true"/>；否则为 <see langword="false"/></returns>
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
            packet.NetIds[0] != assignedNetId || packet.Nicknames?.Length != 1 || LoadingScreenUI.Instance == null)
        {
            // (analysis/loading-screen-peer-state) 一条被丢弃的行公告，就是一个其加载状态
            // 永远不可能出现的对等端的指纹：把它点出来，而不是静默返回。
            LoadingScreenUI.DiagThrottled("players-rejected-" + peer.Id, 5f,
                "row announcement from peer " + peer.Id + " did not pass the check and was dropped: netIds="
                + (packet.NetIds?.Length.ToString() ?? "null") + ", first="
                + (packet.NetIds is { Length: > 0 } ? packet.NetIds[0].ToString() : "n/a")
                + ", assigned=" + (_assignedPeerNetIds.TryGetValue(peer, out var known) ? known.ToString() : "none")
                + ", nicknames=" + (packet.Nicknames?.Length.ToString() ?? "null")
                + ", screen=" + (LoadingScreenUI.Instance == null ? "absent" : "present")
                + " => this peer's row stays unknown on this machine");
            return;
        }

        var hadRow = LoadingScreenUI.Instance.EnsureRow(assignedNetId, packet.Nicknames[0]);
        if (hadRow)
        {
            // 该行已由这次公告重新创建：对等端在
            // 本机自己的重新装填丢掉它的行之后再次自我公告（round10 的缺口）——也可能只是一次正常的迟到加入。
            LoadingScreenUI.Diag("row for peer " + peer.Id + " (netId=" + assignedNetId + ", '"
                + packet.Nicknames[0] + "') did not exist on this screen and was created from the peer's "
                + "announcement (" + LoadingScreenUI.Instance.DescribeRows() + ")");
        }

        var newPacket = LoadingScreenUI.Instance.GetPlayersPacket();
        SendData(ref newPacket, DeliveryMethod.ReliableUnordered);
    }

    private void OnLoadingScreenPacketReceived(LoadingScreenPacket packet, NetPeer peer)
    {
        if (!_assignedPeerNetIds.TryGetValue(peer, out var assignedNetId) || packet.NetId != assignedNetId)
        {
            // (analysis/loading-screen-peer-state) 本机无法匹配到某个已分配
            // net id 的对等端的进度，否则会不留一行地消失。
            LoadingScreenUI.DiagThrottled("progress-rejected-" + peer.Id, 5f,
                "progress from peer " + peer.Id + " was dropped: packetNetId=" + packet.NetId + ", assigned="
                + (_assignedPeerNetIds.TryGetValue(peer, out var known) ? known.ToString() : "none")
                + " => the peer's loading state cannot be shown on this machine");
            return;
        }

        if (LoadingScreenUI.Instance != null)
        {
            // (analysis/loading-screen-peer-state) 当本机自己的重新装填丢掉该行时，
            // ApplyPeerProgress 会把该行创建出来；正是这一行才让对等端的加载状态在这里可见。
            LoadingScreenUI.Instance.ApplyPeerProgress(packet.NetId, packet.Progress, "peer#" + peer.Id);
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
                // 资源加载可能挂起。保留已解码的包值，
                // 并且只在主机观察者应用它之后再转发。
                var relay = packet;
                MainThreadDispatcher.RunAsync(async () =>
                {
                    try
                    {
                        await controller.HandleInraidQuestPacketAsync(relay);
                        // 在奖励包加载期间，发送方可能断开连接。
                        // 主机已经接受了这次更新，因此其他客户端
                        // 在观察者应用它之后仍然必须收到它。
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

            // SPT 为这名迟到加入者重新生成了 scav 档案。
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

            // 玩家 scav 迟到加入：SPT 为本场突袭重新生成了档案。
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
        // 客户端只确认加载主机创建的 bot；它不能拥有
        // 这些 bot 的启用、禁用或销毁状态变迁。
        if (packet.Type != BotStatePacket.EStateType.LoadBot) return;
        var host = FikaSingleton<IFikaGame>.Instance?.GameController as HostGameController;
        host?.IncreaseLoadedPlayers(packet.NetId, peer);
    }

    private void OnTransitInteractPacketReceived(TransitInteractPacket packet, NetPeer peer)
    {
        // (analysis/client-transit-terminal) 这次丢弃原本是静默的。当客户端的按压没有产生请求时，
        // 修复方案会在它自己的机器上重发该请求，而这里的不匹配是它仍然可能
        // 消失的唯一地方——下面这一行与客户端的 'press fallback' 行是一对，因此「闸门拒绝了」与
        // 「请求从未到达」可以用一次 grep 区分开。
        if (peer.Player == null || packet.NetId != peer.Player.NetId)
        {
            return;
        }
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
                if (player != null) SendMapStateSnapshot(player, peer);
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
        if (!_assignedPeerNetIds.TryGetValue(peer, out var assignedNetId) && packet.Type is EGenericSubPacketType.SendCharacter)
        {
            // (2026-10-17) 中转会保留 UDP 连接，但主机可能被留成没有
            // 逐对等端分配的状态（每当服务器对象被重建，分配表都会重建，
            // 而一条存活下来的连接从不自行重发 NetworkSettings）。对等端随后
            // 就把自己的角色公告进一个缺失的分配里，于是永远不会为它创建玩家，
            // 之后每个请求都被回答 "unbound player"——这就是中转之后被报告的状态：
            // 无法切换武器、无法移动物品，客户端在主机上站着不动。
            // 分配只从这台主机自己为那个档案发出的 net id 恢复，
            // 绝不使用对等端自己选的值。
            if (packet.SubPacket is SendCharacterPacket restoring
                && restoring.PlayerInfoPacket.Profile != null
                && _cachedConnections.TryGetValue(restoring.PlayerInfoPacket.Profile.ProfileId, out var cachedNetId)
                && cachedNetId == packet.NetId)
            {
                _assignedPeerNetIds[peer] = cachedNetId;
                _assignedPeerProfiles[peer] = restoring.PlayerInfoPacket.Profile.ProfileId;
                assignedNetId = cachedNetId;
                _logger.LogWarning($"Restored the net id {cachedNetId} of peer {peer.Id} from its character " +
                    $"announcement (profile {restoring.PlayerInfoPacket.Profile.ProfileId})");
            }
        }
        if (!_assignedPeerNetIds.TryGetValue(peer, out assignedNetId) || packet.NetId != assignedNetId ||
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
            // 第一次角色公告发生在绑定 peer.Player 的最终信息
            // 包之前。要针对设置里的分配来校验，
            // 而不是要求那个稍后才会发生的绑定。
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
                // 没有这一步，对等端就会静默地永远得不到玩家，而它的每一项
                // 物品栏操作此后都会被拒绝。
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
                // 加入一场进行中突袭的玩家 scav 会公告 SPT 为这场突袭
                // 重新生成的档案；接受并重新绑定它，否则
                // 主机永远不会为它创建玩家。
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
            // 这次公告已在上方校验过。要显式转发它：
            // 原始广播路径会抑制未校验的角色数据。
            // 不要清空接收包；NetPacketProcessor 拥有它的对象池。
            var writer = new NetDataWriter();
            writer.PutEnum(EPacketType.Serializable);
            _packetProcessor.WriteNetReusable(writer, ref packet);
            _netServer.SendToAll(writer.AsReadOnlySpan(), DeliveryMethod.ReliableOrdered, peer);
        }
    }

    private void OnInformationPacketReceived(InformationPacket packet, NetPeer peer)
    {
        // 客户端会轮询这个请求，并可能在
        // 重连期间重试就绪消息。对本次突袭的就绪屏障，这条连接只计一次。
        // 包可能在原生服务器组件正在构造时到达
        // （或者在重连正在拆除旧突袭时到达）。
        // 要把未绑定状态当作未就绪处理，而不是在 Init 填充那些
        // 字典之前就去解引用它们。
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
            // 在重连期间，原生 GameTimer 可能短暂存在却没有它的
            // SessionTime 载荷。不要在那个窗口里读取 Nullable.Value，也
            // 不要把填了一半的计时包公布给客户端。
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
            // 走进一场进行中突袭的玩家 scav 可能在它报告任何远程
            // net id 之前就自我公告，因此退回使用服务器分配给
            // 这个对等端的 net id。没有这个绑定，每个 proceed
            // 请求都会被回答 "unbound player"。
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
        // 客户端不得把别人的物品栏选作主机侧的
        // 执行目标。重连会在信息交换阶段就把对等端
        // 关联好，而这是在正常物品栏流量开始之前。
        if (peer.Player == null || peer.Player.NetId != packet.NetId)
        {
            _logger.LogWarning($"Rejected inventory operation for net id {packet.NetId} from an unbound peer");
            return;
        }

        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            // [CarExtract] 付费撤离点位（"car extract"）的交付，在付款客户端上
            // 是一次本地预测：原生交互已经
            // 告诉玩家钱已经转走了。因此在这里拒绝该请求，
            // 是阻止第二次付款的唯一办法，而这次拒绝
            // 会带回权威物品栏，好让客户端把自己的
            // 预测回滚。
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

                // [PlantGuard]（analysis/plant-client-host-crash）：主机会根据客户端的
                // 描述符重建它的操作，然后再原生执行它。对于
                // 销毁形态的描述符，那次重建会遍历发送方的物品图，并在操作对象还不存在时
                // 就构建出一个 ItemManipulator.Discard 结果；这次主机已经能判断自己
                // 无法完成的移除，必须在托管代码里、就在这里
                // 被拦下，而不是交给那条原生链。每一次拒绝的回答都与
                // 下面的转换失败完全一致（Failed + 权威重同步 + id 重同步），因此
                // 客户端永远不会被晾在那里等回调。
                if (PlantRelayGuard.TryRefuse(playerToApply, packet.Descriptor, out var guardRefusal))
                {
                    PlantRelayGuard.ReportRefusal(packet.NetId, packet.CallbackId, packet.Descriptor, guardRefusal);
                    SendGenericPacketToPeer(EGenericSubPacketType.OperationCallback,
                        OperationCallbackPacket.FromValue(packet.NetId, packet.CallbackId, EOperationStatus.Failed,
                        "The host does not support this operation: " + guardRefusal), peer);
                    SendInventoryResyncToPeer(packet.NetId, packet.CallbackId, peer);
                    ResyncInventoryIdPacket guardResyncPacket = new(playerToApply.NetId);
                    SendDataToPeer(ref guardResyncPacket, DeliveryMethod.ReliableOrdered, peer);
                    return;
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
                        // 客户端的描述符可能点到主机从未生成过的物品
                        // （被档案过滤掉的战利品）。要记录足以把解析器缺陷与
                        // 真正缺失的世界物品区分开的日志。
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
                        // 在确认 Started 之前先准备好中继。如果
                        // 序列化失败，客户端只会看到 Failed，而
                        // 原生操作尚未被执行。
                        handler.SetRelayPayload(CreateInventoryRelayPayload(packet));
                        SendGenericPacketToPeer(EGenericSubPacketType.OperationCallback,
                            OperationCallbackPacket.FromValue(packet.NetId, packet.CallbackId, EOperationStatus.Started), peer);

                        // 原生执行可能在接收包已经归还到
                        // 它的对象池之后才结束。
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
                        // 原生 Execute 可能同步调用它的完成回调，
                        // 同时在展开过程中仍然抛异常。那种情况下它的终态已经
                        // 发出过了；不要与它矛盾。
                        if (handler.TerminalStatusReported)
                        {
                            _logger.LogError($"Inventory operation threw after completion for {packet.NetId}: {exception}");
                            return;
                        }
                        // 没有发送任何完成通知。要在下面发送失败与权威快照
                        // 之前，先把该回调作废。
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
