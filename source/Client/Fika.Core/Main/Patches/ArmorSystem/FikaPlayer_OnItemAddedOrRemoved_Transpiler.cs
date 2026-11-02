using System.Reflection;
using EFT;
using Fika.Core.Main.Players;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.ArmorSystem;

internal class FikaPlayer_OnItemAddedOrRemoved_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.OnItemAddedOrRemoved));

    [PatchPostfix]
    public static void Postfix(Player __instance)
    {
        if (__instance.TryCast<FikaPlayer>() == null) return;
        // 包含嵌套的护板变更，这类变更不一定替换护甲槽位。
        // 保留原生的物品事件路径，随后刷新 Fika 的缓存。
        var armors = __instance._preAllocatedArmorComponents;
        armors.Clear();
        __instance.Inventory.GetPutOnArmorsNonAlloc(armors);
    }
}
