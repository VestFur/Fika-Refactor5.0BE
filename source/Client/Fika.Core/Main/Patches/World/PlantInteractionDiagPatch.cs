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
/// 针对破冰船那扇门所用的原生「放置物品」链路的诊断（外加一项可关闭的修复）。
///
/// 报告的症状：放置交互存在且可以按下，随后目标面板却
/// 回答 "Obtain an explosive charge to plant on the door"（语言包键
/// Interactives/need_bomb_terminal，即需求闸门的失败分支），尽管炸药就在
/// 玩家的背包里。给出这个回答的闸门是
/// EFT.GameTriggers.ItemTriggerRequirement.Check(InventoryController, SkillManager)：它接收一个
/// 物品栏控制器和一个字符串 id，并回答该物品栏是否满足需求。
/// HandlerRequirementsWithFailedTrigger 把 "no" 转成失败的输出触发器，HandlerPlantAction
/// 显示面板文本与放置时间，而放置本身由
/// EFT.Player.PlantItemNetwork（FikaPlayer 重写了它）-> EFT.Player.PlantItem -> PlantItemLocalOnly 承载。
///
/// 下面每一行都以 [PlantDiag] 开头，并把整条检索放在一处：
/// 检索了哪个控制器、索取的是哪个 id、本地档案持有什么（带地址，因此
/// "在背包里" 是可见的），以及回答是什么。
///
/// 这项修复从不凭空造出物品：只有当本地玩家自己的物品栏里找到了所要求的 id 时，
/// 它才会推翻一个 "no"。Fika.cfg -> [Gameplay] -> "Plant Interaction Fix" = false 就是
/// 一键回滚。
/// </summary>
internal static class PlantDiag
{
    /// <summary>破冰船的炸药：item_ice_c3 / "SZ-1 explosive charge"。</summary>
    internal const string IcebreakerChargeTemplate = "69a0174087a75d2cbd0842e8";

    /// <summary>该炸药在 Terminal 的对应物（item_spec_sz_1），与破冰船的那个并列打印。</summary>
    internal const string TerminalChargeTemplate = "6819f8df28294ec0730db6b4";

    private const string BackpackMarker = "Backpack";
    private const int MaxSameLine = 3;

    private static readonly Dictionary<string, int> _seenLines = new(StringComparer.Ordinal);

    /// <summary>
    /// 本机最后一次上报的放置所对应的模板 id，也就是放置本身
    /// 解析出的那个物品。当某个物品 id 无法解析时，消耗修复会退回使用它，而
    /// 前提是该模板在物品栏里仍然恰好存在一个。
    /// </summary>
    internal static string LastPlantedTemplate { get; set; }

    /// <summary>最后一次被兑现的需求所用的过滤条件（<c>_itemRequirementId</c>）。</summary>
    internal static string LastRequirementFilter { get; set; }

    /// <summary>
    /// 本机自己物品栏中某个模板的唯一物品；若该模板
    /// 不存在、或匹配到不止一个物品，则为 null。消耗修复从不在候选之间做挑选。
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
    /// 行为开关。开启（默认）时，若某个需求回答 "no" 而所要求的 id 却
    /// 确实存在于本地玩家自己的物品栏中，则放行——物品栏是对的，
    /// 检查是错的。关闭：只做诊断，每个回答都原样转发。
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
                // 该开关只会放宽一个已经回答了 "no" 的检查，且此时物品
                // 可证明就在身上：设置对象读不到时保持开启。
                return true;
            }
        }
    }

    /// <summary>
    /// analysis/plant-item-consumption 的行为开关。开启（默认）时，一次成功的放置会
    /// 用原生移除步骤补完（由 ItemManipulator.Discard 加上由其构建的 RemoveOperation
    /// 共同完成），前提是被放置的物品仍在本机自己的物品栏里。关闭：物品栏
    /// 绝不被触碰，只留下 [PlantDiag] 的消耗日志行。
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
                // 该修复只是补完游戏本身也会执行的一次移除：设置对象
                // 读不到时保持开启。
                return true;
            }
        }
    }

    /// <summary>
    /// AY（analysis/plant-client-host-crash）：*客户端*是否可以中继它自己的放置移除。开启
    /// （默认）时它会，且只中继一次，走的是主机被证明会执行的那条普通物品栏操作
    /// 中继。关闭：移除严格留在主机上，因此客户端放下的物品不会消耗
    /// 任何东西，也不会向主机发送任何内容。
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
                // 读取开关绝不能决定一次移除：把 "读不到" 视为保持不变。
                return true;
            }
        }
    }

    /// <summary>本机是否为这场突袭的权威侧（主机或无头主机）。</summary>
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
    /// 由物品 id 求模板 id，用于放置包携带的 id 是原生查表
    /// 无法解析的那种情况（包里带的是物品 id，而不是模板）。
    /// </summary>

    /// <summary>本机运行在突袭的哪一侧，供诊断行使用。</summary>
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

    /// <summary>本机自己的玩家，是此处放置唯一可以合法读取的物品栏。</summary>
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
    /// 某个需求所收到的控制器是否属于本机自己的玩家。一个在打听
    /// 别人物品栏的闸门，绝不能从这里被放宽：本地
    /// 物品栏并不是那个被问到的物品栏。
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
            // 炸药确实不在该玩家的物品栏里：无论开关怎么设置，都原样转发
            // 原生回答。
            return false;
        }
        return FixEnabled;
    }

    /// <summary>
    /// 某个原生物品是否仍挂在容器里。对根本没有父级的物品，Item.get_Parent() 会抛
    /// 异常（"Trying to get parent of an item that doesn't have a parent: ..."），因此
    /// 只能通过该异常来读取答案。
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
            // EPlayerItems.All 还会遍历藏身处的储物箱；那么在突袭里遍历突袭容器就够了。
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
                // 没有可继续扫描的了：返回已有的结果。
            }
        }
        return result;
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






}

/// <summary>
/// 决定放置是否可以开始的闸门：对象所索取的字符串 id，以及它
/// 所问的物品栏——并与本地档案实际持有的内容并列打印。
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

/// <summary>同一个闸门的兑现侧：物品在这里被消耗，因此它的回答也被记录。</summary>
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

/// <summary>放置的权威侧：游戏为放置区域解析出的那个物品。</summary>
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

/// <summary>当放置点开始放置时触发：证明该交互已到达地图处理器。</summary>
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

/// <summary>在需求闸门运行时触发，好让「哪个闸门回答 no」出现在日志里。</summary>
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

/// <summary>普通的需求闸门（没有失败输出）：同样的问题，同样的日志。</summary>
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
