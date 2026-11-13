using Diz.LanguageExtensions;
using Diz.Jobs;
using System.Collections.Generic;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using EFT.Quests;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.Networking.Packets.Communication;
using Fika.Core.Main.BaseClasses;

namespace Fika.Core.Main.ObservedClasses;

public class ObservedQuestController(Profile profile, InventoryController inventoryController, IPlayerSearchController searchController, IQuestSession session)
    : FikaQuestController(profile, inventoryController, searchController, session)
{
    private Dictionary<string, List<QuestInformation>> _questsDict;
    private List<ZoneDropInformation> _zoneDrops;
    private List<string> _visitedPlaces;
    private List<MongoID> _lootedQuestItems;
    private Task _inventoryPacketTail = Task.CompletedTask;

    internal bool HasCollectedQuestItem(string templateId) =>
        _lootedQuestItems?.Exists(id => id.ToString() == templateId) == true;

    public override void Run()
    {
        if (FikaBackendUtils.IsServer)
        {
            _questsDict = [];
            _zoneDrops = [];
            _visitedPlaces = [];
            _lootedQuestItems = [];
        }
    }

    public void UpdateQuestStatusForClient(QuestSyncPacket packet)
    {
        if (packet.Type is QuestSyncPacket.EQuestSyncType.ItemDrop)
        {
#if DEBUG
            FikaGlobals.LogInfo($"Received item drop quest status from client for zone [{packet.ZoneId}]");
#endif
            UpdateClientZoneDrop(packet.ItemId, packet.ZoneId);
            return;
        }

        if (packet.Type is QuestSyncPacket.EQuestSyncType.PlaceVisited)
        {
#if DEBUG
            FikaGlobals.LogInfo($"Received visited place for zone [{packet.ZoneId}]");
#endif
            if (!_visitedPlaces.Contains(packet.ZoneId)) _visitedPlaces.Add(packet.ZoneId);
            return;
        }

        if (packet.Type is QuestSyncPacket.EQuestSyncType.PickUpQuestItem)
        {
#if DEBUG
            FikaGlobals.LogInfo($"Received loot quest item [{packet.ItemId}]");
#endif
            if (!ReferenceEquals(packet.ItemId, null) && !_lootedQuestItems.Contains(packet.ItemId))
                _lootedQuestItems.Add(packet.ItemId);
            return;
        }

#if DEBUG
        FikaGlobals.LogInfo($"Received from client for quest [{packet.QuestId}] for conditional [{packet.ConditionId}] with a value of [{packet.Value}]");
#endif

        if (!_questsDict.TryGetValue(packet.QuestId, out var questInformation))
        {
            questInformation = [];
            _questsDict.Add(packet.QuestId, questInformation);
        }

        for (var i = 0; i < questInformation.Count; i++)
        {
            var quest = questInformation[i];
            if (quest.ConditionId == packet.ConditionId)
            {
                quest.Value = packet.Value;

                return;
            }
        }

        questInformation.Add(new QuestInformation
        {
            ConditionId = packet.ConditionId,
            Value = packet.Value
        });
    }

    private void UpdateClientZoneDrop(MongoID? itemId, string zoneId)
    {
        // 重放其掉落之后，不能重新创建已放置的任务物品，
        // 重连期间也是如此。之后真正的拾取可以再次加入它。
        if (!ReferenceEquals(itemId, null)) _lootedQuestItems.RemoveAll(id => id == itemId);
        // MongoID 是原生值包装器。其相等运算符会拆箱
        // 两个操作数，无法比较两个缺失的可选 ID。
        if (!_zoneDrops.Exists(drop => SameOptionalItemId(drop.ItemId, itemId) && drop.ZoneId == zoneId))
            _zoneDrops.Add(new ZoneDropInformation(itemId, zoneId));
    }

    private static bool SameOptionalItemId(MongoID left, MongoID right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (ReferenceEquals(left, null) || ReferenceEquals(right, null)) return false;
        return string.Equals(left.ToString(), right.ToString(), System.StringComparison.Ordinal);
    }


    protected override void DisposeNative() => DisposeObservedNative();

    internal void DisposeObservedNative()
    {
        // Observed Run 刻意不创建本地任务连接器。
        // SPT 5 的客户端 Dispose 及其泛型基类都需要该管理器。
        // 保持 Fika 仅观察的清理方式，而不是为了给拆除流程
        // 造一个管理器去运行本地任务。
        Native._disposable.Dispose();
        ConditionalBook.Dispose();
        foreach (var quest in ConditionalBook)
        {
            Native.RemovedConditionalHandlers(quest);
        }
    }

    protected override void DisposeManaged()
    {
        _questsDict?.Clear();
        _zoneDrops?.Clear();
        _visitedPlaces?.Clear();
        _lootedQuestItems?.Clear();
    }

    public Task HandleInraidQuestPacketAsync(InRaidQuestPacket packet)
    {
        // bundle 加载是异步的。主机和客户端都要让此观察玩家的
        // 任务奖励与交接保持网络顺序。
        var previous = _inventoryPacketTail;
        return _inventoryPacketTail = ProcessAfterPrevious(previous, packet);
    }

    private async Task ProcessAfterPrevious(Task previous, InRaidQuestPacket packet)
    {
        try { await previous; }
        catch { /* 失败的包不得阻塞后续有效的任务操作。 */ }
        if (IsDisposed) throw new System.ObjectDisposedException(nameof(ObservedQuestController));
        await ApplyInraidQuestPacket(packet);
    }

    private async Task ApplyInraidQuestPacket(InRaidQuestPacket packet)
    {
        packet.Validate();
        if (packet.Type == InRaidQuestPacket.InraidQuestType.Finish)
        {
            var pendingRewards = new List<(Item Item, ItemAddress Address)>();
            for (var i = 0; i < packet.Items.Count; i++)
            {
                var flat = packet.Items[i];
                // 发送方发送的是实际生成的树，包括所有 ID。
                // 在此处克隆 QuestReward 会生成不同的物品树。
                var rootId = packet.RewardRootIds[i].ToString();
                if (InventoryController.TryFindItem(rootId, out _)) continue;
                var tree = Singleton<ItemFactory>.Instance.FlatItemsToTree(
                    new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<JsonType.FlatItem>(flat), false, null);
                if (tree.DeserializationErrors.Count > 0 || !tree.Items.TryGetValue(rootId, out var item))
                    throw new System.InvalidOperationException($"Cannot reconstruct quest reward {rootId}");
                var address = InventoryController.ToItemAddress(packet.RewardAddresses[i]);
                if (address == null)
                    throw new System.InvalidOperationException($"Cannot resolve quest reward address for {rootId}");
                pendingRewards.Add((item, address));
            }
            // 在修改观察者的库存之前检查整个包。
            // 后面格式错误的条目不能导致前面的奖励已被应用。
            // 像原生任务完成那样先加载所有资源；
            // 嵌套的奖励物品在观察者的装备中可能没有对应 bundle。
            var resources = new Il2CppSystem.Collections.Generic.List<ResourceKey>();
            var paths = new HashSet<string>();
            foreach (var (reward, _) in pendingRewards)
                foreach (var component in reward.GetAllItems())
                    foreach (var resource in component.Template.AllResources)
                        if (paths.Add(resource.path)) resources.Add(resource);
            if (resources.Count > 0)
                await Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(
                    ObjectsFactory.PoolsCategory.Raid, ObjectsFactory.AssemblyType.Online,
                    resources, JobYieldPriority.Immediate, null, new Il2CppSystem.Threading.CancellationToken())
                    .AsManaged();
            if (IsDisposed) throw new System.ObjectDisposedException(nameof(ObservedQuestController));
            foreach (var (item, address) in pendingRewards)
                InventoryController.AddAndRaiseEvents(item, address);
            return;
        }

        if (packet.Type != InRaidQuestPacket.InraidQuestType.Handover || packet.ItemIdsToRemove == null) return;
        var pendingHandovers = new List<(Item Item, int Remaining)>();
        for (var i = 0; i < packet.ItemIdsToRemove.Count; i++)
        {
            var itemId = packet.ItemIdsToRemove[i];
            if (!InventoryController.TryFindItem(itemId.ToString(), out var item))
            {
                if (packet.ItemRemainingCounts[i] == 0) continue; // 之前的更新已将其移除。
                throw new System.InvalidOperationException($"Cannot find handed-over item {itemId}");
            }
            var remaining = packet.ItemRemainingCounts[i];
            if (remaining > item.StackObjectsCount)
                throw new System.InvalidOperationException($"Quest handover would increase stack {itemId}");
            pendingHandovers.Add((item, remaining));
        }
        foreach (var (item, remaining) in pendingHandovers)
        {
            if (remaining == 0)
            {
                var removed = ItemManipulator.Remove(item, InventoryController, false);
                if (removed.Failed) throw new System.InvalidOperationException(removed.Error.ToString());
                removed.Value.RaiseEvents(InventoryController, CommandStatus.Succeed);
            }
            else
            {
                item.StackObjectsCount = remaining;
                item.RaiseRefreshEvent(false, true);
            }
        }
    }
}

public sealed class QuestInformation
{
    public MongoID ConditionId { get; set; }

    public int Value { get; set; }
}

public sealed class ZoneDropInformation(MongoID? itemId, string zoneId)
{
    public MongoID? ItemId { get; set; } = itemId;

    public string ZoneId { get; set; } = zoneId;
}
