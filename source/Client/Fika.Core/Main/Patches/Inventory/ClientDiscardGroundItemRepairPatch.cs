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
/// A client discard is an EFT.InventoryLogic.Operations.ThrowOperation. The host executes its own
/// copy and relays the operation to every peer EXCEPT the source peer, and Fika has no packet that
/// spawns a single world LootItem, so the only thing that can create the ground object for the
/// dropper is the dropper's own local execution of that operation. When that execution was aborted
/// (or finished without the world spawn) the item stayed in limbo: gone from the inventory, never
/// on the ground. Only the ABORT path (FailLocally) is repaired: the native operation spawns the
/// world item at the very END of its async execution, so repairing right after a NORMAL completion
/// created a second, non-interactable ghost entity with collision next to the real drop.
/// </summary>
internal static class ClientDiscardGroundItemRepairPatch
{
    internal static void Repair(ClientInventoryOperationHandler handler)
    {
        try
        {
            if (handler == null)
            {
                return;
            }

            var operation = handler.Operation;
            if (operation == null)
            {
                return;
            }
            // Still executing: its own GameWorld.ThrowItem call is still coming, so spawning now
            // would duplicate the drop.
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
            // Still inside the inventory: the native execution has not taken it out yet.
            if (item.CurrentAddress != null)
            {
                return;
            }

            var lootItem = world.ThrowItem(item, player, new Il2CppSystem.Nullable<Vector3>());
            FikaGlobals.LogWarning($"[DiscardRepair] the discard of {item.Id} ({item.TemplateId}) left no ground item; " +
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

/// <summary>The abort path itself; the repair runs after it released the operation.</summary>
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
