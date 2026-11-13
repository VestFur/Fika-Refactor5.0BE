using EFT.Communications;
using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using EFT.Quests;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.Communication;
using static Fika.Core.UI.FikaUIGlobals;
using Diz.Utils;

namespace Fika.Core.Main.ClientClasses;

public sealed class ClientSharedQuestController(Profile profile, InventoryController inventoryController,
    IPlayerSearchController searchController, IQuestSession session, FikaPlayer player) : ClientQuestController(profile, inventoryController, searchController, session, player)
{
    private readonly HashSet<string> _lastFromNetwork = [];
    private readonly HashSet<string> _acceptedTypes = [];
    private readonly HashSet<string> _lootedTemplateIds = [];
    private readonly HashSet<string> _pendingSharedItems = [];
    private readonly HashSet<(string ItemId, string ZoneId)> _pendingDrops = [];

    private bool _isItemBeingDropped;
    // Profile.OnItemZoneDropped 在 47888 中是一个托管事件。
    private System.Action<string, string> _sharedItemZoneDropped;

    public override void Init()
    {
        _canSendAndReceive = true;
        base.Init();
        var array = (FikaPlugin.EQuestSharingTypes[])Enum.GetValues(typeof(FikaPlugin.EQuestSharingTypes));
        for (var i = 0; i < array.Length; i++)
        {
            var shareType = array[i];
            if (FikaPlugin.Instance.Settings.QuestTypesToShareAndReceive.Value.HasFlag(shareType))
            {
                switch (shareType)
                {
                    case FikaPlugin.EQuestSharingTypes.Kills:
                        if (!FikaPlugin.Instance.Settings.EasyKillConditions.Value)
                        {
                            _acceptedTypes.Add("Elimination");
                            _acceptedTypes.Add(shareType.ToString());
                        }
                        break;
                    case FikaPlugin.EQuestSharingTypes.Item:
                        _acceptedTypes.Add("FindItem");
                        break;
                    case FikaPlugin.EQuestSharingTypes.Location:
                        _acceptedTypes.Add("Exploration");
                        _acceptedTypes.Add("Discover");
                        _acceptedTypes.Add("VisitPlace");
                        _acceptedTypes.Add(shareType.ToString());
                        break;
                    case FikaPlugin.EQuestSharingTypes.PlaceBeacon:
                        _acceptedTypes.Add(shareType.ToString());
                        break;
                }
            }
        }
    }

    /// <summary>
    /// 用于在订阅该事件时避免报错
    /// </summary>
    public override void LateInit()
    {
        base.LateInit();
        if (_acceptedTypes.Contains("PlaceBeacon") && _sharedItemZoneDropped == null)
        {
            _sharedItemZoneDropped = Shared_Profile_OnItemZoneDropped;
            _player.Profile.OnItemZoneDropped += _sharedItemZoneDropped;
        }
    }

    protected override void DisposeManaged()
    {
        base.DisposeManaged();
        _canSendAndReceive = false;
        _lastFromNetwork.Clear();
        _pendingSharedItems.Clear();
        _pendingDrops.Clear();
        _lootedTemplateIds.Clear();
        if (_sharedItemZoneDropped != null)
        {
            _player.Profile.OnItemZoneDropped -= _sharedItemZoneDropped;
            _sharedItemZoneDropped = null;
        }
    }

    private void Shared_Profile_OnItemZoneDropped(string itemId, string zoneId)
    {
        // 已放置的战局任务物品之后仍可能被再次拾取。拾取去重必须
        // 限定在物品实际被携带的那段时间内。
        if (!string.IsNullOrWhiteSpace(itemId)) _lootedTemplateIds.Remove(itemId);

        if (!_canSendAndReceive)
        {
            return;
        }

        if (_isItemBeingDropped)
        {
            return;
        }

        QuestDropItemPacket packet = new(_player.NetId, _player.Profile.Info.MainProfileNickname, itemId, zoneId);
#if DEBUG
        FikaGlobals.LogInfo($"Profile_OnItemZoneDropped: Sending quest progress itemId: {itemId} zoneId: {zoneId}");
#endif
        _player.PacketSender.NetworkManager.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
    }

    public override void OnConditionValueChanged(Quest conditional, EQuestStatus status, Condition condition, bool notify = true)
    {
        base.OnConditionValueChanged(conditional, status, condition, notify);
        if (!_canSendAndReceive)
        {
            return;
        }

        if (_lastFromNetwork.Contains(condition.id))
        {
            return;
        }
        SendQuestPacket(conditional, condition);
    }

    public bool ContainsAcceptedType(string type)
    {
        return _acceptedTypes.Contains(type);
    }

    protected override void OnQuestItemPickedUp(Item item)
    {
        if (!_canSendAndReceive || !_acceptedTypes.Contains("FindItem") ||
            _pendingSharedItems.Contains(item.TemplateId) || !_lootedTemplateIds.Add(item.TemplateId)) return;

        // AddItemEvent(Succeed) 由主机与客户端共享。失败的本地操作
        // 绝不应把物品结算给队友，也不应消耗去重状态。
        var packet = new QuestItemPacket
        {
            NetId = _player.NetId,
            Nickname = _player.Profile.Info.MainProfileNickname,
            ItemId = item.TemplateId
        };
        _player.PacketSender.NetworkManager.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
    }

    public void ToggleQuestSharing(bool state)
    {
        _canSendAndReceive = state;
    }

    private void SendQuestPacket(Quest quest, Condition condition)
    {
        if (!_canSendAndReceive)
        {
            return;
        }

        if (quest != null)
        {
            var counter = quest.ConditionCountersManager.GetCounter(condition.id);
            if (counter != null)
            {
                if (!ValidateQuestType(counter))
                {
                    return;
                }

                QuestConditionPacket packet = new()
                {
                    NetId = _player.NetId,
                    Nickname = _player.Profile.Info.MainProfileNickname,
                    Id = counter.Id,
                    SourceId = counter.SourceId
                };
#if DEBUG
                FikaGlobals.LogInfo("SendQuestPacket: Sending quest progress");
#endif
                _player.PacketSender.NetworkManager.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
            }
        }
    }

    internal void ReceiveQuestPacket(QuestConditionPacket packet)
    {
        if (!_canSendAndReceive)
        {
            return;
        }

        for (var i = 0; i < Quests.Count; i++)
        {
            var quest = Quests[i];
            // 额外校验，用于避免重复通知
            if (!IsQuestActive(quest))
            {
                continue;
            }

            if (quest.Id == packet.SourceId)
            {
#if DEBUG
                FikaGlobals.LogInfo($"Quest id matched sourceId, status: {quest.QuestStatus}, name: {quest.Template.Name.ParseLocalization()}");
#endif
                var counter = quest.ConditionCountersManager.GetCounter(packet.Id);
                if (counter != null && !quest.CompletedConditions.Contains(counter.Template))
                {
                    if (!ValidateQuestType(counter))
                    {
#if DEBUG
                        FikaGlobals.LogInfo($"Failed to verify quest type for {quest.Template.Name.ParseLocalization()}");
#endif
                        return;
                    }

                    // 只抑制本次更新产生的通知。
                    // 未激活或不存在的任务不得抑制之后的本地击杀。
                    var added = _lastFromNetwork.Add(packet.Id);
                    try { counter.Value++; }
                    finally { if (added) _lastFromNetwork.Remove(packet.Id); }
                    if (FikaPlugin.Instance.Settings.QuestSharingNotifications.Value)
                    {
                        FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.RECEIVED_SHARED_QUEST_PROGRESS.Localized(),
                            [ColorizeText(EColor.GREEN, packet.Nickname), ColorizeText(EColor.BROWN, quest.Template.Name)]),
                            iconType: EFT.Communications.ENotificationIconType.Quest);
                    }
                }
            }
        }
    }

    internal void ReceiveQuestItemPacket(QuestItemPacket packet)
    {
        if (!_canSendAndReceive || !_acceptedTypes.Contains("FindItem"))
        {
            return;
        }

        if (packet.ItemId is not null && !_lootedTemplateIds.Contains(packet.ItemId) &&
            !_pendingSharedItems.Contains(packet.ItemId))
        {
            // 重连会恢复库存，但会重建本控制器的
            // 内存去重集合。不要重复结算第二份。
            foreach (var carried in _player.Inventory.QuestRaidItems.GetAllItems())
            {
                if (!carried.QuestItem || carried.TemplateId != packet.ItemId) continue;
                _lootedTemplateIds.Add(packet.ItemId);
                return;
            }

            var item = _player.FindQuestItem(packet.ItemId);
            if (item != null)
            {
                var playerInventory = _player.InventoryController;
                var equipment = playerInventory.Inventory.Equipment?.TryCast<CompoundItem>();
                if (equipment == null) return;
                var pickupResult = ItemManipulator.QuickFindAppropriatePlace(item, playerInventory,
                    equipment.ToEnumerable(),
                    ItemManipulator.EMoveItemOrder.PickUp, true);

                if (pickupResult.Succeeded && playerInventory.CanExecute(pickupResult.Value))
                {
                    _pendingSharedItems.Add(item.TemplateId);
                    try
                    {
                        playerInventory.RunNetworkTransaction(pickupResult.Value, (Comfort.Common.Callback)(result =>
                        {
                            if (IsDisposed) return;
                            _pendingSharedItems.Remove(item.TemplateId);
                            if (!result.Succeed)
                            {
                                FikaGlobals.LogError($"Shared quest item pickup failed: {result.Error}");
                                return;
                            }
                            _lootedTemplateIds.Add(item.TemplateId);
                            if (FikaPlugin.Instance.Settings.QuestSharingNotifications.Value)
                            {
                                FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.RECEIVED_SHARED_ITEM_PICKUP.Localized(),
                                    [ColorizeText(EColor.GREEN, packet.Nickname), ColorizeText(EColor.BLUE, item.Name.Localized())]),
                                    iconType: EFT.Communications.ENotificationIconType.Quest);
                            }
                        }));
                    }
                    catch { _pendingSharedItems.Remove(item.TemplateId); throw; }
                }
            }
        }
    }

    internal void ReceiveQuestDropItemPacket(QuestDropItemPacket packet)
    {
        if (!_canSendAndReceive || !_acceptedTypes.Contains("PlaceBeacon")) return;
        string itemId = packet.ItemId;
        var zoneId = packet.ZoneId;
        if (!HasQuestForItem(itemId, zoneId, out var questName)) return;
        var key = (itemId, zoneId);
        if (!_pendingDrops.Add(key)) return;

        // 执行移除前先做快照：成功的操作会同步修改
        // 原生库存集合。
        var items = new List<Item>();
        try
        {
            foreach (var item in _player.Inventory.QuestRaidItems.GetAllItems())
                if (item.QuestItem && item.TemplateId == itemId) items.Add(item);
            RemoveNext(0);
        }
        catch { _pendingDrops.Remove(key); throw; }

        void RemoveNext(int index)
        {
            if (IsDisposed) return;
            if (index == items.Count)
            {
                var previous = _isItemBeingDropped;
                _isItemBeingDropped = true;
                try
                {
                    _lootedTemplateIds.Remove(itemId);
                    _player.Profile.ItemDroppedAtPlace(itemId, zoneId);
                }
                finally { _isItemBeingDropped = previous; _pendingDrops.Remove(key); }

                if (FikaPlugin.Instance.Settings.QuestSharingNotifications.Value)
                    FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.RECEIVED_SHARED_ITEM_PLANT.Localized(),
                        [ColorizeText(EColor.GREEN, packet.Nickname), ColorizeText(EColor.BROWN, questName)]),
                        iconType: ENotificationIconType.Quest);
                return;
            }

            var removal = ItemManipulator.Remove(items[index], _player.InventoryController, true);
            _player.InventoryController.TryRunNetworkTransaction(removal, (Comfort.Common.Callback)(result =>
            {
                if (!result.Succeed)
                {
                    _pendingDrops.Remove(key);
                    FikaGlobals.LogError("ReceiveQuestDropItemPacket: Discard failed: " + result.Error);
                    return;
                }
                try { RemoveNext(index + 1); }
                catch { _pendingDrops.Remove(key); throw; }
            }));
        }
    }

    // (2026-10-17) 目标已完成但尚未交付的任务会停留在 AvailableForFinish 中，
    // 并且仍然接收共享进度（任务的最后一次击杀经常正好出现在这一状态——
    // 此前共享计数器被直接丢弃了）。
    private bool IsQuestActive(Quest quest) =>
        quest.QuestStatus == EQuestStatus.Started || quest.QuestStatus == EQuestStatus.AvailableForFinish;

    private bool HasQuestForItem(string itemId, string zoneId, out string questName)
    {
        for (var i = 0; i < Quests.Count; i++)
        {
            var quest = Quests[i];
            if (!IsQuestActive(quest) ||
                !quest.Conditions.TryGetValue(EQuestStatus.AvailableForFinish, out var conditions)) continue;

            // 使用原生条件集合，避免为每个任务子类型新建
            // IL2CPP 的 GetConditions<T> 泛型闭包。
            for (var j = 0; j < conditions.Count; j++)
            {
                var condition = conditions[j];
                if (condition.TryCast<ConditionPlaceBeacon>() == null &&
                    condition.TryCast<ConditionLeaveItemAtLocation>() == null) continue;
                var zoneCondition = condition.Cast<ConditionZone>();
                if (zoneCondition.target.Contains(itemId) && zoneCondition.zoneIds.Contains(zoneId) &&
                    !quest.CompletedConditions.Contains(condition) && quest.CheckVisibilityStatus(condition))
                {
                    questName = quest.Template.Name;
                    return true;
                }
            }
        }
        questName = null;
        return false;
    }

    /// <summary>
    /// 校验任务类型，部分任务使用 CounterCreator，这种类型同样需要校验。
    /// </summary>
    /// <param name="counter">待校验的计数器</param>
    /// <returns>任务类型合法时返回 true，否则返回 false</returns>
    private bool ValidateQuestType(TaskConditionCounter counter)
    {
#if DEBUG
        FikaGlobals.LogInfo($"Validating counter of type {counter.Type}");
#endif
        if (_acceptedTypes.Contains(counter.Type))
        {
            return true;
        }

        if (counter.Type == "CounterCreator")
        {
            var CounterCreator = counter.Template?.TryCast<ConditionCounterCreator>();
            if (CounterCreator == null) return false;
#if DEBUG
            FikaGlobals.LogInfo($"CounterCreator Type {CounterCreator.type}");
#endif

            var creatorType = CounterCreator.type.ToString();
            if (_acceptedTypes.Contains(creatorType))
            {
                return true;
            }

            // (2026-10-17) 击杀目标并非总被包在 "Elimination" 计数器中：线上数据库的 204 个
            // 击杀条件里有 8 个位于 "Completion"（少数位于 "Multi"）计数器内，
            // 上面的白名单从未匹配到它们——这些击杀完全没有被共享，而且
            // 失败是静默的。只有确实带有击杀条件且启用了击杀共享时才会接受该复合类型，
            // 因此其他复合类型的行为不受影响。
            if (_acceptedTypes.Contains("Elimination")
                && (creatorType is "Completion" || creatorType is "Multi")
                && HasKillCondition(CounterCreator))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 当复合计数器的子条件中包含击杀条件时为 true。
    /// </summary>
    private static bool HasKillCondition(ConditionCounterCreator counterCreator)
    {
        try
        {
            var conditions = counterCreator.Conditions;
            if (conditions == null)
            {
                return false;
            }

            for (var i = 0; i < conditions.Count; i++)
            {
                var condition = conditions[i];
                if (condition != null && condition.TryCast<ConditionKills>() != null)
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
        }

        return false;
    }
}
