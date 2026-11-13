using Diz.LanguageExtensions;
using JsonType;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.Quests;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.Networking.Packets.Communication;
using Diz.Utils;
using Fika.Core.Main.BaseClasses;
using EFT.InventoryLogic.Operations;

namespace Fika.Core.Main.ClientClasses;

public class ClientQuestController(Profile profile, InventoryController inventoryController, IPlayerSearchController searchController, IQuestSession session, FikaPlayer player)
    : FikaQuestController(profile, inventoryController, searchController, session)
{
    protected readonly FikaPlayer _player = player;
    protected bool _canSendAndReceive;
    private readonly bool _isClient = FikaBackendUtils.IsClient;
    private bool _sendQuestSync = true;

    /// <summary>
    /// Used to prevent errors when subscribing to the event
    /// </summary>
    private bool _eventsSubscribed;
    private Il2CppSystem.Action<string, string> _itemZoneDropped;
    private Il2CppSystem.Action<string, int> _specialPlaceVisited;
    private Il2CppSystem.Action<AddItemEventArgs> _itemAdded;

    public virtual void LateInit()
    {
        if (_eventsSubscribed) return;
        _itemZoneDropped = (Il2CppSystem.Action<string, string>)Profile_OnItemZoneDropped;
        _specialPlaceVisited = (Il2CppSystem.Action<string, int>)Player_OnSpecialPlaceVisited;
        _itemAdded = (Il2CppSystem.Action<AddItemEventArgs>)InventoryController_AddItemEvent;
        _eventsSubscribed = true;
        _player.Profile.OnItemZoneDropped += _itemZoneDropped;
        _player.OnSpecialPlaceVisited += _specialPlaceVisited;
        _player.InventoryController.AddItemEvent += _itemAdded;
    }

    public void ToggleSend(bool enabled)
    {
        _sendQuestSync = enabled;
    }

    private void InventoryController_AddItemEvent(AddItemEventArgs eventArgs)
    {
        if (eventArgs.Status != CommandStatus.Succeed || !_sendQuestSync)
        {
            return;
        }

        if (eventArgs.Item.QuestItem)
        {
            OnQuestItemPickedUp(eventArgs.Item);
            if (!_isClient) return;

            var packet = new QuestSyncPacket
            {
                NetId = _player.NetId,
                Type = QuestSyncPacket.EQuestSyncType.PickUpQuestItem,
                ItemId = eventArgs.Item.TemplateId
            };

            FikaSingleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }
    }

    protected virtual void OnQuestItemPickedUp(Item item) { }

    protected override void DisposeManaged()
    {
        base.DisposeManaged();
        if (!_eventsSubscribed) return;
        _eventsSubscribed = false;
        _player.Profile.OnItemZoneDropped -= _itemZoneDropped;
        _player.OnSpecialPlaceVisited -= _specialPlaceVisited;
        _player.InventoryController.AddItemEvent -= _itemAdded;
    }

    private void Player_OnSpecialPlaceVisited(string zoneId, int experience)
    {
        if (!_isClient || !_sendQuestSync)
        {
            return;
        }

        var packet = new QuestSyncPacket
        {
            NetId = _player.NetId,
            Type = QuestSyncPacket.EQuestSyncType.PlaceVisited,
            ZoneId = zoneId
        };
        FikaSingleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    protected void Profile_OnItemZoneDropped(string itemId, string zoneId)
    {
        if (!_isClient || !_sendQuestSync)
        {
            return;
        }

        var packet = new QuestSyncPacket
        {
            NetId = _player.NetId,
            Type = QuestSyncPacket.EQuestSyncType.ItemDrop,
            ItemId = !string.IsNullOrWhiteSpace(itemId) ? itemId : null,
            ZoneId = zoneId
        };
        FikaSingleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    public virtual void OnConditionValueChanged(Quest conditional, EQuestStatus status, Condition condition, bool notify = true)
    {
        if (_isClient && _sendQuestSync)
        {
            var counter = conditional.ConditionCountersManager.GetCounter(condition.id);
            if (counter == null)
            {
#if DEBUG
                FikaGlobals.LogWarning($"There was no counter for condition [{condition.id}]");
#endif
                return;
            }

            var packet = new QuestSyncPacket
            {
                NetId = _player.NetId,
                Type = QuestSyncPacket.EQuestSyncType.Conditional,
                QuestId = conditional.Id,
                ConditionId = condition.id,
                Value = counter.Value
            };
            FikaSingleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }
    }

    internal InRaidQuestPacket CaptureFinish(QuestFinishOperation operation)
    {
        var packet = new InRaidQuestPacket
        {
            NetId = _player.NetId,
            Type = InRaidQuestPacket.InraidQuestType.Finish,
            Items = new(),
            RewardAddresses = new(),
            RewardRootIds = new()
        };
        var rewards = operation._finishResult.Rewards;
        for (var i = 0; i < rewards.Count; i++)
        {
            var reward = rewards[i];
            var roots = new Il2CppSystem.Collections.Generic.List<Item>();
            roots.Add(reward.Item);
            var flat = Singleton<ItemFactory>.Instance.TreeToFlatItems(roots);
            var items = new FlatItem[flat.Length];
            for (var j = 0; j < flat.Length; j++) items[j] = flat[j];
            packet.Items.Add(items);
            packet.RewardRootIds.Add(reward.Item.Id);
            packet.RewardAddresses.Add(reward.To.ToDescriptor());
        }
        return packet;
    }

    internal InRaidQuestPacket CaptureHandover(QuestHandoverOperation operation)
    {
        var packet = new InRaidQuestPacket
        {
            NetId = _player.NetId,
            Type = InRaidQuestPacket.InraidQuestType.Handover,
            ItemIdsToRemove = new(),
            ItemRemainingCounts = new()
        };
        var items = operation._result.Items.GetEnumerator();
        try
        {
            while (items.MoveNext())
            {
                var entry = items.Current;
                if (entry.Item.QuestItem) continue;
                packet.ItemIdsToRemove.Add(entry.Item.Id);
                packet.ItemRemainingCounts.Add(entry.NumberToPreserve);
            }
        }
        finally { items.Dispose(); }
        return packet;
    }

    internal Il2CppSystem.Threading.Tasks.Task<IResult> SynchronizeAfterOperation(
        Il2CppSystem.Threading.Tasks.Task<IResult> operation, InRaidQuestPacket packet)
    {
        return NativeTask.FromManaged(MainThreadDispatcher.RunAsync(() => WaitForQuestOperation(operation, packet)));
    }

    private async Task<IResult> WaitForQuestOperation(Il2CppSystem.Threading.Tasks.Task<IResult> operation, InRaidQuestPacket packet)
    {
        var result = await operation.AsManaged();
        if (!IsDisposed && result.Succeed && ((packet.Items?.Count ?? 0) > 0 || (packet.ItemIdsToRemove?.Count ?? 0) > 0))
        {
            _player.PacketSender.NetworkManager.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
        }
        return result;
    }

    internal void ReceiveReconnectQuestSync(List<QuestSyncPacket> packets)
    {
        var shouldReset = _canSendAndReceive;
        var shouldSend = _sendQuestSync;
        _canSendAndReceive = false;
        _sendQuestSync = false;

        try
        {
            for (var i = 0; i < packets.Count; i++)
            {
                var packet = packets[i];
                switch (packet.Type)
                {
                    case QuestSyncPacket.EQuestSyncType.Conditional:
                        QuestConditionValueChanged(packet.QuestId, packet.ConditionId, packet.Value);
                        break;
                    case QuestSyncPacket.EQuestSyncType.ItemDrop:
                        Profile.ItemDroppedAtPlace(packet.ItemId, packet.ZoneId);
                        break;
                    case QuestSyncPacket.EQuestSyncType.PlaceVisited:
                        _player.SpecialPlaceVisited(packet.ZoneId, 0);
                        break;
                    case QuestSyncPacket.EQuestSyncType.PickUpQuestItem:
                        if (!ReferenceEquals(packet.ItemId, null)) LootReconnectQuestItem(packet.ItemId);
                        break;
                }
            }
        }
        finally
        {
            _canSendAndReceive = shouldReset;
            _sendQuestSync = shouldSend;
        }
    }

    private void LootReconnectQuestItem(MongoID itemId)
    {
        // The authoritative profile may already contain this quest item.
        foreach (var existing in _player.Inventory.QuestRaidItems.GetAllItems())
        {
            if (existing.QuestItem && existing.TemplateId == itemId) return;
        }

        var gameWorld = _player.GameWorld?.TryCast<FikaClientGameWorld>();
        if (gameWorld == null) return;
        var lootItems = gameWorld._questLootItems;

#if DEBUG
        FikaGlobals.LogInfo($"Looking for quest item [{itemId}]");
#endif

        for (var i = lootItems.Count - 1; i >= 0; i--)
        {
            var lootItem = lootItems[i];
#if DEBUG
            FikaGlobals.LogInfo($"Scanning quest item [{lootItem.Item.TemplateId}]");
#endif
            if (lootItem.Item.TemplateId == itemId)
            {
                var item = gameWorld.CreateReconnectQuestItem(lootItem, true, _player);
                var playerInventory = _player.InventoryController;
                var equipment = playerInventory.Inventory.Equipment?.TryCast<CompoundItem>();
                if (equipment == null) return;
                var pickupResult = ItemManipulator.QuickFindAppropriatePlace(item, playerInventory,
                    equipment.ToEnumerable(),
                    ItemManipulator.EMoveItemOrder.PickUp, true);

                if (pickupResult.Succeeded && playerInventory.CanExecute(pickupResult.Value))
                {
                    playerInventory.RunNetworkTransaction(pickupResult.Value, (Callback)(result =>
                    {
                        if (IsDisposed) return;
                        if (result.Succeed) lootItems.Remove(lootItem);
                        else FikaGlobals.LogError($"Quest item reconnect pickup failed for {itemId}: {result.Error}");
                    }));
                }
                else
                {
                    FikaGlobals.LogError($"There was an error when looting the quest item [{item.LocalizedShortName()}] during resync: {pickupResult.Error}");
                }

                return;
            }
        }

        FikaGlobals.LogError($"Could not find item [{itemId}] during resync");
    }

    public void QuestConditionValueChanged(string questId, MongoID conditionId, double value)
    {
        var conditional = ConditionalBook.GetConditional(questId);
        if (conditional == null)
        {
            FikaGlobals.LogError("Quest with id (" + questId.ToString() + ") is null!");
            return;
        }
        var counter = conditional.ConditionCountersManager.GetCounter(conditionId);
        if (counter != null)
        {
            counter.Value = (int)value;
            return;
        }

        FikaGlobals.LogError($"Could not find counter [{conditionId}]");
    }
}
