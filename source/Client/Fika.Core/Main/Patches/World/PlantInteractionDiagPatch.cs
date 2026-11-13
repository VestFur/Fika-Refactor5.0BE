using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.World;

/// <summary>
/// Diagnostics (plus one opt-out fix) for the native "plant an item" chain the Icebreaker door uses.
///
/// Reported symptom: the plant interaction exists and can be pressed, and the objective panel then
/// answers "Obtain an explosive charge to plant on the door" (locale key
/// Interactives/need_bomb_terminal, the failed arm of the requirement gate) although the charge sits
/// in the player's backpack. The gate that answers it is
/// EFT.GameTriggers.ItemTriggerRequirement.Check(InventoryController, SkillManager): it is handed an
/// inventory controller and a string id and answers whether that inventory satisfies the requirement.
/// HandlerRequirementsWithFailedTrigger turns a "no" into the failed output trigger, HandlerPlantAction
/// shows the panel text and the planting time, and the plant itself is carried by
/// EFT.Player.PlantItemNetwork (FikaPlayer overrides it) -> EFT.Player.PlantItem -> PlantItemLocalOnly.
///
/// Every line below starts with [PlantDiag] and carries the whole search in one place: which
/// controller was searched, which id was asked for, what the local profile holds (with addresses, so
/// "in the backpack" is visible), and what the answer was.
///
/// The fix never invents an item: it only overrides a "no" when the required id was found in the
/// local player's own inventory. Fika.cfg -> [Gameplay] -> "Plant Interaction Fix" = false is the
/// one-flip rollback.
/// </summary>
internal static class PlantDiag
{
    /// <summary>The Icebreaker charge: item_ice_c3 / "SZ-1 explosive charge".</summary>
    internal const string IcebreakerChargeTemplate = "69a0174087a75d2cbd0842e8";

    /// <summary>The Terminal twin of the charge (item_spec_sz_1), printed next to the Icebreaker one.</summary>
    internal const string TerminalChargeTemplate = "6819f8df28294ec0730db6b4";

    private const string BackpackMarker = "Backpack";
    private const int MaxSameLine = 3;

    private static readonly Dictionary<string, int> _seenLines = new(StringComparer.Ordinal);

    /// <summary>
    /// The template id of the plant that was reported last on this machine, i.e. the item the plant
    /// itself resolved. The consume repair falls back to it when an item id cannot be resolved, and
    /// only when that template is still in the inventory exactly once.
    /// </summary>
    internal static string LastPlantedTemplate { get; set; }

    /// <summary>The filter (<c>_itemRequirementId</c>) of the requirement that was redeemed last.</summary>
    internal static string LastRequirementFilter { get; set; }

    /// <summary>
    /// The single item of a template in this machine's own inventory, or null when the template is
    /// absent or matches more than one item. The consume repair never picks between candidates.
    /// </summary>
    internal static Item FindLocalByTemplate(string filter)
    {
        if (!string.IsNullOrEmpty(filter))
        {
            LastRequirementFilter = filter;
        }

        if (string.IsNullOrEmpty(filter))
        {
            return null;
        }

        var inventory = LocalPlayer?.Profile?.Inventory;
        if (inventory == null)
        {
            return null;
        }

        try
        {
            var matches = inventory.GetAllItemByTemplate(filter);
            if (matches == null)
            {
                return null;
            }

            Item found = null;
            var count = 0;
            foreach (var candidate in matches)
            {
                if (candidate == null)
                {
                    continue;
                }

                count++;
                found = candidate;
            }

            return count == 1 ? found : null;
        }
        catch (Exception exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The behaviour switch. On (default) a requirement that answers "no" while the required id is
    /// present in the local player's own inventory is allowed through - the inventory is right, the
    /// check is wrong. Off: diagnosis only, every answer is forwarded untouched.
    /// </summary>
    internal static bool FixEnabled
    {
        get
        {
            try
            {
                var settings = FikaPlugin.Instance?.Settings;
                return settings?.PlantInteractionFix == null || settings.PlantInteractionFix.Value;
            }
            catch
            {
                // The switch only widens a check that already answered "no" while the item is
                // provably present: keep it on when the settings object cannot be read.
                return true;
            }
        }
    }

    /// <summary>
    /// The behaviour switch of analysis/plant-item-consumption. On (default) a successful plant is
    /// completed with the native removal step (ItemManipulator.Discard plus the RemoveOperation built
    /// from it) when the planted item is still in this machine's own inventory. Off: the inventory is
    /// never touched and only the [PlantDiag] consume lines remain.
    /// </summary>
    internal static bool ConsumesItem
    {
        get
        {
            try
            {
                var settings = FikaPlugin.Instance?.Settings;
                return settings?.PlantConsumesItem == null || settings.PlantConsumesItem.Value;
            }
            catch
            {
                // The repair only completes a removal the game itself would have performed: keep it
                // on when the settings object cannot be read.
                return true;
            }
        }
    }

    /// <summary>
    /// AY (analysis/plant-client-host-crash): whether a *client* may relay its plant removal. On
    /// (default) it does, exactly once, through the ordinary inventory-operation relay the host is
    /// proven to apply. Off: the removal stays strictly on the host, so a client's plant consumes
    /// nothing and sends nothing to the host.
    /// </summary>
    internal static bool ConsumeOnClient
    {
        get
        {
            try
            {
                var settings = FikaPlugin.Instance?.Settings;
                return settings?.PlantConsumeOnClient == null || settings.PlantConsumeOnClient.Value;
            }
            catch
            {
                // Reading the switch must never decide a removal: treat "unreadable" as unchanged.
                return true;
            }
        }
    }

    /// <summary>Whether this machine is the authority side of the raid (the host or a headless host).</summary>
    internal static bool IsHostMachine
    {
        get
        {
            try
            {
                return FikaBackendUtils.IsServer;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// The template id of an item id, for the case where the plant packet carries an id the native
    /// lookup cannot resolve (the packet carries the item id, not the template).
    /// </summary>
    internal static string TemplateIdOf(string itemId)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            return null;
        }

        foreach (var item in LocalInventoryItems())
        {
            try
            {
                if (string.Equals(item.Id, itemId, StringComparison.Ordinal))
                {
                    return SafeTemplate(item);
                }
            }
            catch
            {
                // A detached item can refuse its id; keep scanning.
            }
        }

        return null;
    }

    /// <summary>Which side of the raid this machine runs, for the diagnostic lines.</summary>
    internal static string Role
    {
        get
        {
            try
            {
                if (FikaBackendUtils.IsServer)
                {
                    return "host";
                }
                return FikaBackendUtils.IsClient ? "client" : "none";
            }
            catch
            {
                return "unknown";
            }
        }
    }

    /// <summary>The machine's own player, the only inventory a plant can legitimately read here.</summary>
    internal static FikaPlayer LocalPlayer
    {
        get
        {
            try
            {
                return Fika.Core.Main.Components.RaidInteractionSync.LocalPlayer;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Whether the controller a requirement was handed belongs to this machine's own player. A gate
    /// that asks about somebody else's inventory must never be widened from here: the local
    /// inventory would not be the inventory in question.
    /// </summary>
    internal static bool IsLocalController(InventoryController controller)
    {
        if (controller == null)
        {
            return false;
        }
        try
        {
            var local = LocalPlayer?.InventoryController;
            return local != null && local.Pointer == controller.Pointer;
        }
        catch
        {
            return false;
        }
    }

    internal static bool FixAllowed(bool foundLocally)
    {
        if (!foundLocally)
        {
            // The charge really is not in this player's inventory: forward the native answer
            // untouched, whatever the switch says.
            return false;
        }
        return FixEnabled;
    }

    /// <summary>
    /// Whether a native item still hangs in a container. Item.get_Parent() throws for an item that
    /// has no parent at all ("Trying to get parent of an item that doesn't have a parent: ..."), so
    /// the answer can only be read through that exception.
    /// </summary>
    internal static bool HasParent(Item item)
    {
        if (item == null)
        {
            return false;
        }
        try
        {
            _ = item.Parent;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string ItemRef(Item item)
    {
        if (item == null)
        {
            return "none";
        }
        try
        {
            return $"{item.Id}/{SafeTemplate(item)}@{DescribeAddress(item)}";
        }
        catch (Exception exception)
        {
            return "unreadable:" + exception.Message;
        }
    }

    internal static string DescribeAddress(Item item)
    {
        try
        {
            var address = item?.CurrentAddress;
            return address == null ? "none" : address.ToString();
        }
        catch
        {
            return "none";
        }
    }

    internal static bool IsInBackpack(string address)
    {
        return !string.IsNullOrEmpty(address)
            && address.IndexOf(BackpackMarker, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal static string SafeTemplate(Item item)
    {
        try
        {
            return item?.TemplateId.ToString() ?? "none";
        }
        catch
        {
            return "none";
        }
    }

    internal static List<Item> LocalInventoryItems()
    {
        var result = new List<Item>();
        var inventory = LocalPlayer?.Profile?.Inventory;
        if (inventory == null)
        {
            return result;
        }

        try
        {
            foreach (var item in inventory.GetPlayerItems(EPlayerItems.All))
            {
                if (item != null)
                {
                    result.Add(item);
                }
            }
        }
        catch (Exception exception)
        {
            // EPlayerItems.All also walks the hideout stashes; the raid containers are enough then.
            try
            {
                foreach (var item in inventory.GetPlayerItems(EPlayerItems.NonQuestItems))
                {
                    if (item != null)
                    {
                        result.Add(item);
                    }
                }
            }
            catch
            {
                // Nothing left to scan: return what we have.
            }
        }
        return result;
    }

    private static string InventoryApiReport(InventoryController controller, string filter)
    {
        var inventory = LocalPlayer?.Profile?.Inventory;
        if (inventory == null)
        {
            return "none";
        }

        var all = "n/a";
        try
        {
            all = inventory.GetAllItemByTemplate(filter ?? string.Empty)?.Count().ToString() ?? "null";
        }
        catch (Exception exception)
        {
            all = "failed:" + exception.Message;
        }
        return $"GetAllItemByTemplate({filter ?? "none"})={all} profilePtr=0x{(LocalPlayer?.Profile?.Inventory?.Pointer.ToInt64() ?? 0):X}";
    }

    private static Item FindByTemplate(List<Item> items, string filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            return null;
        }
        foreach (var item in items)
        {
            if (string.Equals(SafeTemplate(item), filter, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }
        return null;
    }

    private static Item FindById(List<Item> items, string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }
        foreach (var item in items)
        {
            try
            {
                if (string.Equals(item.Id, id, StringComparison.Ordinal))
                {
                    return item;
                }
            }
            catch
            {
                // A detached item can refuse its id; keep scanning.
            }
        }
        return null;
    }

    private static List<Item> ChargeItems(List<Item> items)
    {
        var result = new List<Item>();
        foreach (var item in items)
        {
            var template = SafeTemplate(item);
            if (string.Equals(template, IcebreakerChargeTemplate, StringComparison.OrdinalIgnoreCase)
                || string.Equals(template, TerminalChargeTemplate, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(item);
            }
        }
        return result;
    }

    private static Item HandsItem(List<Item> items)
    {
        foreach (var item in items)
        {
            var address = DescribeAddress(item);
            if (!string.IsNullOrEmpty(address)
                && (address.IndexOf("PrimaryWeapon", StringComparison.OrdinalIgnoreCase) >= 0
                    || address.IndexOf("Holster", StringComparison.OrdinalIgnoreCase) >= 0
                    || address.IndexOf("Scabbard", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return item;
            }
        }
        return null;
    }

    private static string DescribeCharges(List<Item> charges)
    {
        if (charges.Count == 0)
        {
            return "none";
        }
        return string.Join(", ", charges.Select(item => $"{ItemRef(item)}"));
    }

    private static string ControllerRef(InventoryController controller)
    {
        if (controller == null)
        {
            return "none";
        }
        try
        {
            return $"{controller.GetType().Name}#0x{controller.Pointer.ToInt64():X}";
        }
        catch
        {
            return controller.GetType().Name;
        }
    }

    private static string SafeField(Func<string> read)
    {
        try
        {
            return read() ?? "none";
        }
        catch
        {
            return "none";
        }
    }
}

/// <summary>
/// The gate that decides whether the plant may start: the string id the object asks for and the
/// inventory it asks - printed next to what the local profile actually holds.
/// </summary>
internal sealed class ItemTriggerRequirement_Check_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(EFT.GameTriggers.ItemTriggerRequirement)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "Check" && method.GetParameters().Length == 2);
        if (target == null)
        {
            throw new MissingMethodException("EFT.GameTriggers.ItemTriggerRequirement.Check(InventoryController, SkillManager)");
        }
        return target;
    }

    [PatchPostfix]
    public static void Postfix(EFT.GameTriggers.ItemTriggerRequirement __instance, InventoryController __0, ref bool __result)
    {
        try
        {
            var filter = __instance?._itemRequirementId;
            var items = PlantDiag.LocalInventoryItems();
            var found = items.FirstOrDefault(item =>
                string.Equals(PlantDiag.SafeTemplate(item), filter, StringComparison.OrdinalIgnoreCase));
            
            if (!__result && PlantDiag.IsLocalController(__0) && PlantDiag.FixAllowed(found != null))
            {
                __result = true;
                
            }
        }
        catch (Exception exception)
        {
                    }
    }
}

/// <summary>The redeeming half of the same gate: the item is consumed here, so its answer is logged too.</summary>
internal sealed class ItemTriggerRequirement_TryRedeem_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(EFT.GameTriggers.ItemTriggerRequirement)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "TryRedeem" && method.GetParameters().Length == 2);
        if (target == null)
        {
            throw new MissingMethodException("EFT.GameTriggers.ItemTriggerRequirement.TryRedeem(InventoryController, SkillManager)");
        }
        return target;
    }

    [PatchPostfix]
    public static void Postfix(EFT.GameTriggers.ItemTriggerRequirement __instance, InventoryController __0, ref bool __result)
    {
        try
        {
            var filter = __instance?._itemRequirementId;
            var items = PlantDiag.LocalInventoryItems();
            var found = items.FirstOrDefault(item =>
                string.Equals(PlantDiag.SafeTemplate(item), filter, StringComparison.OrdinalIgnoreCase));
            
            
            if (!__result && PlantDiag.IsLocalController(__0) && PlantDiag.FixAllowed(found != null))
            {
                __result = true;
                
            }
        }
        catch (Exception exception)
        {
                    }
    }
}

/// <summary>The authority side of the plant: the item the game resolved for the plant zone.</summary>
internal sealed class Player_PlantItemLocalOnly_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = HarmonyLib.AccessTools.Method(typeof(Player), "PlantItemLocalOnly", new[] { typeof(Item), typeof(string) });
        if (target == null)
        {
            throw new MissingMethodException("EFT.Player.PlantItemLocalOnly(Item, string)");
        }
        return target;
    }

    [PatchPrefix]
    public static void Prefix(Item __0, string __1)
    {
        try
        {
            
        }
        catch (Exception exception)
        {
                    }
    }
}

/// <summary>Fires when a plant point starts planting: proves the interaction reached the map handler.</summary>
internal sealed class HandlerPlantAction_OnTriggerEvent_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(EFT.GameTriggers.HandlerPlantAction)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "OnTriggerEvent");
        if (target == null)
        {
            throw new MissingMethodException("EFT.GameTriggers.HandlerPlantAction.OnTriggerEvent(TriggerEvent)");
        }
        return target;
    }

    [PatchPrefix]
    public static void Prefix(EFT.GameTriggers.HandlerPlantAction __instance)
    {
        
    }
}

/// <summary>Fires while a requirement gate runs, so "which gate answered no" is in the log.</summary>
internal sealed class HandlerRequirementsWithFailedTrigger_OnInputTrigger_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(EFT.GameTriggers.HandlerRequirementsWithFailedTrigger)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "OnInputTrigger");
        if (target == null)
        {
            throw new MissingMethodException("EFT.GameTriggers.HandlerRequirementsWithFailedTrigger.OnInputTrigger(TriggerEvent)");
        }
        return target;
    }

    [PatchPrefix]
    public static void Prefix(EFT.GameTriggers.HandlerRequirementsWithFailedTrigger __instance)
    {
        try
        {
            
        }
        catch (Exception exception)
        {
                    }
    }
}

/// <summary>The plain requirement gate (no failed output): same question, same log.</summary>
internal sealed class HandlerRequirements_OnInputTrigger_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(EFT.GameTriggers.HandlerRequirements)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "OnInputTrigger");
        if (target == null)
        {
            throw new MissingMethodException("EFT.GameTriggers.HandlerRequirements.OnInputTrigger(TriggerEvent)");
        }
        return target;
    }

    [PatchPrefix]
    public static void Prefix(EFT.GameTriggers.HandlerRequirements __instance)
    {
        try
        {
            
        }
        catch (Exception exception)
        {
                    }
    }
}
