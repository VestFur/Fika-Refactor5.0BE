using System.Reflection;
using EFT;
using EFT.Ballistics;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.PlayerPatches;

/// <summary>
/// 此补丁停用 BSG 的狗牌处理，因为它的实现质量很差
/// </summary>
public class Player_ManageAggressor_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocalPlayer)
            .GetMethod(nameof(LocalPlayer.ManageAggressor));
    }

    [PatchPrefix]
    public static bool Prefix(LocalPlayer __instance, DamageInfo damageInfo, EBodyPart bodyPart, EBodyPartColliderType colliderType)
    {
        if (__instance.TryCast<FikaPlayer>() == null)
        {
            return true;
        }

        NativeBaseCalls.CallPlayerManageAggressor(__instance, damageInfo, bodyPart, colliderType);
        return false;
    }
}
