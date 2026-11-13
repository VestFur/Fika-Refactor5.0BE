using System.Reflection;
using EFT;
using Fika.Core.Main.Players;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.ArmorSystem;

internal class Player_RecalculateEquipmentParams_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.RecalculateEquipmentParams));

    [PatchPostfix]
    public static void Postfix(Player __instance)
    {
        if (__instance.TryCast<FikaPlayer>() == null) return;
        // SPT 5 把这个列表当作临时存储，并在返回前清空它。
        // 保留全部原生装备/数值更新，然后恢复 Fika 的伤害、耐久度
        // 与复制处理器所使用的那个持久护甲列表。
        var armors = __instance._preAllocatedArmorComponents;
        armors.Clear();
        __instance.Inventory.GetPutOnArmorsNonAlloc(armors);
    }
}
