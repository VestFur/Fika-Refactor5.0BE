using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;
using UnityEngine;

namespace Fika.Core.Main.Patches.Inventory;

/// <summary>
/// 客户端丢弃是一个 EFT.InventoryLogic.Operations.ThrowOperation。主机会执行自己的一份，
/// 并把该操作转发给除源节点之外的每个节点，而 Fika 没有能够
/// 生成单个世界 LootItem 的包，因此唯一能为丢弃者创建地面物体的
/// 途径是丢弃者自己对该操作的本地执行。当该执行被中止
/// （或完成但没有世界生成）时，物品就滞留在中间态：从库存中消失，却从未
/// 出现在地面上。只修复 ABORT 路径（FailLocally）：原生操作会在其异步执行
/// 的最末端才生成世界物品，因此在 NORMAL 完成之后立刻修复
/// 会在真实掉落物旁生成一个带碰撞、不可交互的第二个幽灵实体。
/// </summary>
internal static class ClientDiscardGroundItemRepairPatch
{
    internal static void Repair(ClientInventoryOperationHandler handler)
    {
        try
        {
            // AP (analysis/nonfika-raid-inertness)：修复会通过 GameWorld.ThrowItem 生成地面物品，
            // 因此它只能在我们自己的战局内运行。它所挂靠的处理器（我们自己的客户端
            // 库存操作处理器）在原生战局中并不存在，这就是为什么这里是一个防护
            // 而不是修复——若它真的触发，日志行会点名它。
            if (FikaRaidGate.Skip("ClientDiscardGroundItemRepairPatch.Repair"))
            {
                return;
            }

            if (handler == null)
            {
                return;
            }

            var operation = handler.Operation;
            if (operation == null)
            {
                return;
            }
            // 仍在执行：它自己的 GameWorld.ThrowItem 调用还没到，此时生成
            // 会让掉落物重复。
            if (operation.Status.InProgress())
            {
                return;
            }
            var throwOperation = operation.TryCast<ThrowOperation>();
            var item = throwOperation?.Item;
            if (item == null)
            {
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            var player = world?.MainPlayer;
            if (world == null || player == null)
            {
                return;
            }

            if (IsOnGround(world, item))
            {
                return;
            }
            // 仍在库存中：原生执行还没有把它取出。
            if (item.CurrentAddress != null)
            {
                return;
            }

            var lootItem = world.ThrowItem(item, player, new Il2CppSystem.Nullable<Vector3>());
            FikaGlobals.LogWarning($"[DiscardRepair] the discard of {item.Id} ({LogSafe.Ref(item.TemplateId)}) left no ground item; " +
                $"re-spawned it (lootItem={(lootItem == null ? "null" : "ok")}, netId={(lootItem == null ? -1 : lootItem.GetNetId())})");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[DiscardRepair] re-spawning the discarded item failed: {exception}");
        }
    }

    private static bool IsOnGround(GameWorld world, Item item)
    {
        var lootItems = world.LootItems;
        if (lootItems == null)
        {
            return false;
        }

        var id = item.Id.ToString();
        for (var i = 0; i < lootItems.Count; i++)
        {
            var lootItem = lootItems.GetByIndex(i);
            if (lootItem == null)
            {
                continue;
            }
            if (lootItem.ItemId == id)
            {
                return true;
            }
            var lootedItem = lootItem.Item;
            if (lootedItem != null && lootedItem.Id.ToString() == id)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>中止路径本身；修复在它释放该操作之后运行。</summary>
internal sealed class ClientDiscardGroundItemFailRepairPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ClientInventoryOperationHandler)
            .GetMethod("FailLocally", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    [PatchPostfix]
    public static void Postfix(ClientInventoryOperationHandler __instance)
        => ClientDiscardGroundItemRepairPatch.Repair(__instance);
}
