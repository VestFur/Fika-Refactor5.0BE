using System.Reflection;
using EFT;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.ArmorSystem;

internal class Player_TryGetArmorResistData_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.TryGetArmorResistData));

    [PatchPrefix]
    public static bool Prefix(Player __instance, BodyPartCollider bodyPart, float penetrationPower,
        ref ArmorResistanceData armorResistanceData, ref bool __result)
    {
        if (__instance.TryCast<Fika.Core.Main.Players.FikaPlayer>() == null) return true;
        armorResistanceData = default;
        __result = false;
        var plate = bodyPart.TryCast<ArmorPlateCollider>();
        var plateType = plate != null ? plate.ArmorPlateColliderType : (EArmorPlateCollider)0;
        var armors = __instance._preAllocatedArmorComponents;
        for (var i = 0; i < armors.Count; i++)
        {
            var armor = armors[i];
            if (!armor.ShotMatches(bodyPart.BodyPartColliderType, plateType)) continue;
            // 用第一款匹配的护甲套用游戏的穿透公式，
            // 与原生方法重建护甲列表后的做法完全一致。
            armorResistanceData = ShotSharedMethods.RealResistance(armor.Repairable.Durability,
                armor.Repairable.TemplateDurability, armor.ArmorClass, penetrationPower);
            __result = true;
            break;
        }
        return false;
    }
}
