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
    /// 用于在订阅事件时避免报错
    /// </summary>
    private bool _eventsSubscribed;
    // Profile.OnItemZoneDropped、Player.OnSpecialPlaceVisited 与
    // InventoryController.AddItemEvent 在 47888 中都是托管事件。
    private System.Action<string, string> _itemZoneDropped;
    private System.Action<string, int> _specialPlaceVisited;
    private System.Action<AddItemEventArgs> _itemAdded;

    public virtual void LateInit()
    {
        if (_eventsSubscribed) return;
        _itemZoneDropped = Profile_OnItemZoneDropped;
        _specialPlaceVisited = Player_OnSpecialPlaceVisited;
        _itemAdded = InventoryController_AddItemEvent;
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
