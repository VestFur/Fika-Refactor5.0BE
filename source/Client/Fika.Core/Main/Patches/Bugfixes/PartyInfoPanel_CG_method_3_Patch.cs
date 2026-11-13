using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using EFT.UI.Matchmaker;
using EFT.UI;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Bugfixes;

/// <summary>
/// 经授权借自 Tyfon 的 UI Fixes，用于修复在加载期间控制器刚被实例化时查看玩家所引发的 bug <br/><br/>
/// 源码地址：<see href="https://github.com/tyfon7/UIFixes/blob/main/src/Patches/FixPlayerInspectPatch.cs"/>
/// </summary>
[IgnoreAutoPatch]
public class PartyInfoPanel_CG_method_3_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ItemUiContext).GetMethod(nameof(ItemUiContext.ShowPlayerEquipmentWindow));
    }

    [PatchPrefix]
    public static bool Prefix(GroupPlayer groupPlayer, ref PlayerEquipmentWindowContext __result)
    {
        var equipment = groupPlayer.PlayerVisualRepresentation?.Equipment;
        var owner = equipment?.CurrentAddress?.GetOwnerOrNull();
        if (owner?.TryCast<Player.PlayerOwnerInventoryController>() != null)
        {
            // 战局中正在使用的背包已经持有这套装备。再创建一个
            // 检视控制器会让它从该持有者身上脱钩。
            __result = null;
            return false;
        }

        return true;
    }
}
