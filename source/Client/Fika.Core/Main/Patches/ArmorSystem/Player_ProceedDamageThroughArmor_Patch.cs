using EFT.Ballistics;
using Fika.Core.Main.Utils;
using Il2CppSystem.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.ArmorSystem;

/// <summary>
/// 这里跳过护甲重算，因为 Fika 已缓存护甲 <br/>
/// 它还移除了对开发者巴拉克拉瓦的检查
/// </summary>
internal class Player_ProceedDamageThroughArmor_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(Player),
            nameof(Player.ProceedDamageThroughArmor));
    }

    [PatchPrefix]
    public static bool Prefix(ref DamageInfo damageInfo, EBodyPartColliderType colliderType, EArmorPlateCollider armorPlateCollider, ref List<ArmorComponent> __result, Player __instance, bool damageInfoIsLocal = true)
    {
        List<ArmorComponent> hitArmorList = null;
        var hasBlockedShot = false;
        var hasDeflectedShot = false;

        for (var i = 0; i < __instance._preAllocatedArmorComponents.Count; i++)
        {
            var armor = __instance._preAllocatedArmorComponents[i];
            var armorDamageDealt = 0f;

            if (armor.ShotMatches(colliderType, armorPlateCollider))
            {
                if (hasBlockedShot || hasDeflectedShot)
                {
                    var bluntThroughput = armor.BluntThroughput;

                    if (armor.ArmorType == EArmorType.Heavy)
                    {
                        bluntThroughput *= (1f - __instance.Skills.HeavyVestBluntThroughputDamageReduction);
                    }

                    damageInfo.Damage *= bluntThroughput;
                }
                else
                {
                    hitArmorList ??= new();
                    hitArmorList.Add(armor);

                    if (__instance.HealthController.IsAlive)
                    {
                        armorDamageDealt = armor.ApplyDamage(ref damageInfo, colliderType, armorPlateCollider, damageInfoIsLocal,
                            __instance._preAllocatedArmorComponents, __instance.Skills.LightVestMeleeWeaponDamageReduction,
                            __instance.Skills.HeavyVestBluntThroughputDamageReduction
                        );

                        __instance.OnArmorDamaged(armorDamageDealt, armor);
                    }

                    var blockedBy = NativeNullable<MongoID>.Read(damageInfo, "BlockedBy");
                    var deflectedBy = NativeNullable<MongoID>.Read(damageInfo, "DeflectedBy");
                    hasBlockedShot = blockedBy.HasValue && armor.Item.Id == blockedBy.Value.ToString();
                    hasDeflectedShot = deflectedBy.HasValue && armor.Item.Id == deflectedBy.Value.ToString();
                }
            }

            if (armorDamageDealt > 0.1f)
            {
                __instance.OnArmorPointsChanged(armor, false);
            }
        }

        __result = hitArmorList;
        return false;
    }
}
