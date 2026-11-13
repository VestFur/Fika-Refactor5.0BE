using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.ArmorSystem;

internal class Player_IsBleedingBlockedByLightArmor_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.IsBleedingBlockedByLightArmor));

    [PatchPrefix]
    public static bool Prefix(Player __instance, EBodyPartColliderType colliderType, ref bool __result)
    {
        if (__instance.TryCast<Fika.Core.Main.Players.FikaPlayer>() == null) return true;
        __result = false;
        if (!__instance.Skills.LightVestBleedingProtection) return false;
        var armors = __instance._preAllocatedArmorComponents;
        for (var i = 0; i < armors.Count; i++)
        {
            var armor = armors[i];
            // 原生 SPT 5 用的是与 0.01f 的比较（不是一点耐久值）。
            if (armor.Repairable.Durability < 0.01f || armor.ArmorType != EArmorType.Light) continue;
            foreach (var covered in armor.ArmorColliders)
            {
                if (covered != colliderType) continue;
                __result = true;
                return false;
            }
        }
        return false;
    }
}
