using System;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using Diz.LanguageExtensions;
using Diz.Utils;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.World;

/// <summary>
/// Client side ground loot pickup that skips the native interaction state machine.
/// The native take runs inside EFT.PickUpState and, when its completion callback is
/// lost, leaves the player locked in that state: the movement state machine refuses
/// view rotation, movement and crouch, and MovementContext.CanInteract answers
/// "Can't interact in Pickup", so no interaction prompt appears and ground loot can
/// never be taken again for the rest of the raid.
/// Intercepting InteractionContextHelper.PickupAction lets us run the same inventory
/// operation the verified startup probe uses (ItemManipulator placement +
/// ConvertOperationResultToOperation + Execute) and skip entering that state.
/// The host stays authoritative: the operation still travels through
/// ClientInventoryController, so loot cannot be duplicated locally.
/// </summary>
internal sealed class InteractionContextHelper_PickupAction_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(InteractionContextHelper)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(x => x.Name == "PickupAction"
                && x.GetParameters().Length == 5
                && x.GetParameters()[0].ParameterType == typeof(Player)
                && x.GetParameters()[2].ParameterType == typeof(Item));
        if (target == null)
        {
            throw new MissingMethodException(
                "InteractionContextHelper.PickupAction(Player, IItemOperationResult, Item, IPlayer, Action)");
        }
        return target;
    }

    [PatchPrefix]
    public static bool Prefix(Player __0, Item __2)
    {
        if (__0 == null || __2 == null || !FikaBackendUtils.IsClient)
        {
            return true;
        }

        try
        {
            if (GroundLootPickup.TryTake(__0, __2))
            {
                return false;
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[PickupBypass] failed for {__2.Id}: {exception}");
        }

        return true;
    }
}

/// <summary>
/// Puts ground loot into the matching equipment slot. Armour, helmets, rigs, backpacks and
/// headsets can only live in an equipment slot, which the generic container placement never
/// offers, so these items used to be left on the ground. Uses the verified native path:
/// InventoryEquipment.GetSlot + Slot.CreateItemAddress + ItemManipulator.Move (dry run) +
/// ConvertOperationResultToOperation + Execute, so the host stays authoritative.
/// </summary>
internal static class GroundLootEquip
{
    private static readonly EquipmentSlot[] CandidateSlots =
    {
        EquipmentSlot.Headwear, EquipmentSlot.Earpiece, EquipmentSlot.ArmorVest,
        EquipmentSlot.TacticalVest, EquipmentSlot.Backpack
    };

    internal static bool TryEquipFromGround(Player player, Item item, out string error)
    {
        error = null;
        if (player == null || item == null)
        {
            error = "no player or item";
            return false;
        }

        var controller = player.InventoryController;
        var equipment = player.Equipment;
        if (controller == null || equipment == null)
        {
            error = "no inventory controller or equipment";
            return false;
        }

        var preferred = PreferredSlot(item);
        var ordered = new System.Collections.Generic.List<EquipmentSlot>();
        if (preferred != null)
        {
            ordered.Add(preferred.Value);
        }
        foreach (var candidate in CandidateSlots)
        {
            if (preferred == null || candidate != preferred.Value)
            {
                ordered.Add(candidate);
            }
        }

        var reasons = new System.Collections.Generic.List<string>();
        foreach (var slotName in ordered)
        {
            Slot slot;
            try
            {
                slot = equipment.GetSlot(slotName);
            }
            catch (Exception slotException)
            {
                reasons.Add(slotName + ": lookup failed (" + slotException.Message + ")");
                continue;
            }
            if (slot == null)
            {
                continue;
            }
            if (slot.ContainedItem != null)
            {
                continue;
            }
            if (slot.Locked)
            {
                reasons.Add(slotName + ": locked");
                continue;
            }
            bool compatible;
            try
            {
                compatible = slot.CheckCompatibility(item);
            }
            catch (Exception compatibilityException)
            {
                reasons.Add(slotName + ": compatibility check failed (" + compatibilityException.Message + ")");
                continue;
            }
            if (!compatible)
            {
                continue;
            }

            try
            {
                var placement = ItemManipulator.Move(item, slot.CreateItemAddress(), controller, true);
                if (placement.Failed)
                {
                    reasons.Add(slotName + ": " + (placement.Error == null ? "rejected" : placement.Error.ToString()));
                    continue;
                }

                var operation = controller.ConvertOperationResultToOperation(placement.Value);
                if (operation == null)
                {
                    reasons.Add(slotName + ": no operation from the placement result");
                    continue;
                }

                controller.Execute(operation, (Callback)(result =>
                {
                    try
                    {
                        if (result?.Failed == true)
                        {
                            FikaGlobals.LogWarning($"[PickupBypass] equipping {item.Id} into {slotName} failed: {result.Error}");
                        }
                        player.ForceInteractionsChanged();
                    }
                    catch (Exception callbackException)
                    {
                        FikaGlobals.LogError($"[PickupBypass] equipment pickup callback failed: {callbackException}");
                    }
                }));
                return true;
            }
            catch (Exception moveException)
            {
                reasons.Add(slotName + ": " + moveException.Message);
            }
        }

        error = reasons.Count == 0 ? "no equipment slot accepted the item" : string.Join("; ", reasons);
        return false;
    }

    private static EquipmentSlot? PreferredSlot(Item item)
    {
        try
        {
            if (item.TryCast<Armor>() != null) return EquipmentSlot.ArmorVest;
            if (item.TryCast<Headwear>() != null) return EquipmentSlot.Headwear;
            if (item.TryCast<Vest>() != null) return EquipmentSlot.TacticalVest;
            if (item.TryCast<Backpack>() != null) return EquipmentSlot.Backpack;
            if (item.TryCast<Headphones>() != null) return EquipmentSlot.Earpiece;
        }
        catch
        {
            // Type probing must never break the pickup.
        }
        return null;
    }
}

/// <summary>Direct ground loot take used by the bypass patch.</summary>
internal static class GroundLootPickup
{
    // The native interaction action fires repeatedly while the key is held, and every
    // invocation used to dispatch its own operation. The second one is rejected by the
    // host ("Could not find item owner"), and that rejection triggers the authoritative
    // inventory replacement, which leaves the inventory screen empty for the rest of the
    // raid. Ignore repeats of the same item for a few seconds; the map is bounded and
    // self-expiring so a failed pickup can never block an item permanently.
    private static readonly System.Collections.Generic.Dictionary<string, float> RecentTakes = new();

    private static bool IsRepeatedTake(string itemId)
    {
        lock (RecentTakes)
        {
            var now = Time.realtimeSinceStartup;
            if (RecentTakes.TryGetValue(itemId, out var last) && now - last < 1.5f)
            {
                return true;
            }
            if (RecentTakes.Count > 256)
            {
                RecentTakes.Clear();
            }
            RecentTakes[itemId] = now;
            return false;
        }
    }

    internal static bool TryTake(Player player, Item item)
    {
        var controller = player.InventoryController;
        var localRoot = controller?.RootItem?.TryCast<CompoundItem>();
        if (controller == null || localRoot == null)
        {
            return false;
        }

        var itemKey = item.Id.ToString();
        if (IsRepeatedTake(itemKey))
        {
            FikaGlobals.LogInfo($"[PickupBypass] {itemKey} is already being taken; ignoring the repeated action");
            return true;
        }

        // Armour, helmets and rigs only fit into an equipment slot, and pockets, the
        // tactical rig, the backpack and the secure container are separate compound
        // items, so search every container the player carries instead of only the
        // inventory root. Otherwise a take can fail placement and the item is left
        // on the ground even though the player has room for it.
        var targets = new Il2CppSystem.Collections.Generic.List<CompoundItem>();
        var seen = new System.Collections.Generic.HashSet<IntPtr>();
        void AddTarget(CompoundItem candidate)
        {
            if (candidate != null && seen.Add(candidate.Pointer))
            {
                targets.Add(candidate);
            }
        }

        AddTarget(localRoot);
        if (player.Equipment != null && player.Equipment.TryCast<CompoundItem>() is { } equipment)
        {
            AddTarget(equipment);
        }

        try
        {
            var carried = controller.Inventory?.GetPlayerItems(EPlayerItems.NonQuestItems);
            if (carried != null)
            {
                foreach (var carriedItem in carried)
                {
                    if (carriedItem is CompoundItem compound)
                    {
                        AddTarget(compound);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[PickupBypass] could not enumerate the carried containers: {exception.Message}");
        }

        // Equipment (armour, helmet, rig, backpack, headset) only fits an equipment slot
        // and the generic container search never offers one, so try the dedicated slot
        // move first. Verified native path: Slot.CreateItemAddress + ItemManipulator.Move
        // (dry run) + ConvertOperationResultToOperation + Execute.
        if (GroundLootEquip.TryEquipFromGround(player, item, out var equipError))
        {
            FikaGlobals.LogInfo($"[PickupBypass] equip {item.Id} into its equipment slot " +
                "(native pickup state skipped)");
            return true;
        }

        var placement = ItemManipulator.QuickFindAppropriatePlace(item, controller, targets,
            ItemManipulator.EMoveItemOrder.IgnoreItemParent | ItemManipulator.EMoveItemOrder.TryEquip, true);
        if (placement.Failed)
        {
            // Never fall back to the native pickup action here: that path enters
            // EFT.PickUpState and can lock the player for the rest of the raid.
            // Log the item class and the localised error so the next report says why
            // the placement failed (equipment needs a dedicated slot operation).
            var reason = "unknown";
            try
            {
                reason = placement.Error == null
                    ? "no error object"
                    : placement.Error.GetIl2CppType().Name + " / " + placement.Error.ToString();
            }
            catch (Exception errorException)
            {
                reason = "unreadable (" + errorException.Message + ")";
            }
            FikaGlobals.LogWarning($"[PickupBypass] cannot place {item.Id} template={item.TemplateId.ToString()} " +
                $"class={item.GetIl2CppType().Name}: {reason}; slot attempt: {equipError ?? "not equipment"}; " +
                "keeping it on the ground instead of entering the native pickup state");
            player.ForceInteractionsChanged();
            return true;
        }

        var operation = controller.ConvertOperationResultToOperation(placement.Value);
        if (operation == null)
        {
            FikaGlobals.LogWarning($"[PickupBypass] no operation for {item.Id}");
            return false;
        }

        FikaGlobals.LogInfo($"[PickupBypass] taking {item.Id} through {operation.GetIl2CppType().Name} " +
            "(native pickup state skipped)");
        controller.Execute(operation, (Callback)(result =>
        {
            try
            {
                if (result?.Failed == true)
                {
                    FikaGlobals.LogWarning($"[PickupBypass] pickup of {item.Id} failed: {result.Error}");
                }
                player.ForceInteractionsChanged();
            }
            catch (Exception exception)
            {
                FikaGlobals.LogError($"[PickupBypass] callback failed: {exception}");
            }
        }));
        return true;
    }
}
