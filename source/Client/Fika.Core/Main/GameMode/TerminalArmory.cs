using System;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using JsonType;

namespace Fika.Core.Main.GameMode;

/// <summary>
/// (2026-10-19, 方案 A) Terminal 装备库没收。
///
/// 原版会把玩家的装备移入 Terminal 地图五个装备库容器之一；玩家
/// 必须走到装备库，在变电站院子里的尸体上找到钥匙并打开藏匿处才能拿回
/// 装备。转移本身在构建战局战利品时完成，因此在 Fika 上它必须发生在主机上、
/// 恰好在战利品描述符被序列化并发给客户端之前。
///
/// 数据（SPT 1.2.0，已核验）：容器模板 67a0e494e8fc6968ef0fc9da = lc_spec_stash_player_terminal，
/// Terminal_Area_05_Administration 中有五个始终生成的实例，静态战利品表为空——它们只会
/// 存放本方法放入的内容。MaxPlayers 为 5 时对应五个容器，每个玩家一个。
/// </summary>
internal static class TerminalArmory
{
    private const string ContainerTemplateId = "67a0e494e8fc6968ef0fc9da";

    private static readonly string[] Containers =
    [
        "container_Terminal_Design_Stuff_00159",
        "container_Terminal_Design_Stuff_00161",
        "container_Terminal_Design_Stuff_00164",
        "container_Terminal_Design_Stuff_00165",
        "container_Terminal_Design_Stuff_00167"
    ];

    private static bool _stripped;

    private static readonly EquipmentSlot[] Slots =
    [
        EquipmentSlot.Headwear, EquipmentSlot.Earpiece, EquipmentSlot.Eyewear, EquipmentSlot.FaceCover,
        EquipmentSlot.ArmorVest, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack, EquipmentSlot.Pockets,
        EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster,
        EquipmentSlot.Scabbard
    ];

    internal static void Reset() => _stripped = false;

    /// <summary>
    /// (2026-10-19, 方案 A 第 2 步) 在 Terminal 上每场战局清空一次本地玩家的装备。装备
    /// 已经在装备库中（快照已注入战利品描述符），因此玩家必须带着地图给他的东西
    /// 走过去——即原版循环。每台机器为自己的玩家运行：主机无法
    /// 移动另一台机器的真实库存。
    /// </summary>
    internal static void Tick(GameWorld world, Player localPlayer)
    {
        if (_stripped || world == null || localPlayer == null)
        {
            return;
        }

        try
        {
            var controller = localPlayer.InventoryController;
            var equipment = localPlayer.Inventory?.Equipment;
            if (controller == null || equipment == null)
            {
                return;
            }

            _stripped = true;

            var removed = 0;
            var failed = 0;
            foreach (var slot in Slots)
            {
                Item item;
                try { item = equipment.GetSlot(slot).ContainedItem; }
                catch (Exception) { continue; }

                if (item == null)
                {
                    continue;
                }

                try
                {
                    var result = ItemManipulator.Remove(item, controller, true);
                    if (result == null || result.Error != null)
                    {
                        failed++;
                        continue;
                    }

                    controller.TryRunNetworkTransaction(result, null);
                    removed++;
                }
                catch (Exception)
                {
                    failed++;
                }
            }

            FikaGlobals.LogInfo($"[TerminalArmory] confiscated {removed} slot(s), {failed} kept (the armory holds the snapshot)");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[TerminalArmory] confiscation failed, the player keeps the equipment: {exception}");
        }
    }

    internal static bool Applies(string locationId) =>
        !string.IsNullOrEmpty(locationId) &&
        string.Equals(locationId, "terminal", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 为每个参与者追加一条战利品条目。条目保持 ValidProfiles 为 null，使主机侧的 profile 过滤
    /// 无法丢弃它们；容器实例 id 就是玩家之后必须打开的那个装备库槽位。
    /// </summary>
    /// <summary>
    /// 序列化该树并构建一棵独立的树，使得之后对玩家库存的改动
    /// 无法影响到装备库容器。
    /// </summary>
    private static Item Clone(Item item)
    {
        try
        {
            var descriptor = ItemBinarySerializer.SerializeItem(item, FikaGlobals.SearchControllerSerializer);
            return ItemBinarySerializer.DeserializeItem(descriptor, Singleton<ItemFactory>.Instance,
                new Il2CppSystem.Collections.Generic.Dictionary<MongoID, Item>());
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[TerminalArmory] clone failed: {exception}");
            return null;
        }
    }

    internal static void Inject(LocationSettings.Location location, CoopHandler handler)
    {
        try
        {
            if (location?.Loot == null || handler?.HumanPlayers == null)
            {
                return;
            }

            var players = handler.HumanPlayers;
            if (players.Count == 0)
            {
                return;
            }

            var entries = new Il2CppSystem.Collections.Generic.List<JsonLootItem>();
            foreach (var existing in location.Loot)
            {
                entries.Add(existing);
            }

            var injected = 0;
            for (var i = 0; i < players.Count && i < Containers.Length; i++)
            {
                var player = players[i];
                if (player == null)
                {
                    continue;
                }

                Item equipment;
                try { equipment = player.Inventory?.Equipment; }
                catch (Exception) { equipment = null; }

                if (equipment == null)
                {
                    FikaGlobals.LogWarning($"[TerminalArmory] no equipment tree for {player.ProfileId}, that player keeps the gear");
                    continue;
                }

                // 深拷贝：战利品条目必须是战局开始时取得的快照，而不是对玩家
                // 自身 Equipment 树的活引用（活引用会让装备库容器在整个战局期间
                // 镜像玩家的库存）。
                var snapshot = Clone(equipment);
                if (snapshot == null)
                {
                    FikaGlobals.LogWarning($"[TerminalArmory] could not snapshot the equipment of {player.ProfileId}, that player keeps the gear");
                    continue;
                }

                entries.Add(new JsonLootItem
                {
                    Id = Containers[i],
                    IsContainer = true,
                    Item = snapshot,
                    ValidProfiles = null
                });
                injected++;
            }

            if (injected == 0)
            {
                return;
            }

            location.Loot = new LootData(entries);
            FikaGlobals.LogInfo($"[TerminalArmory] injected {injected} equipment stash(es) into the armory containers");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[TerminalArmory] injection failed, the raid keeps its unobstructed equipment: {exception}");
        }
    }
}
