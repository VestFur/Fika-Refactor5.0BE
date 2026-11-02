using System.Reflection;
using EFT;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.PlayerPatches;

/// <summary>
/// 此补丁停用 BSG 的狗牌处理，因为它的实现质量很差
/// </summary>
public class Player_OnDead_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocalPlayer)
            .GetMethod(nameof(LocalPlayer.OnDead));
    }

    [PatchPrefix]
    public static bool Prefix(LocalPlayer __instance, EDamageType damageType)
    {
        if (__instance.TryCast<FikaPlayer>() == null)
        {
            return true;
        }

        if (__instance.IsAI && __instance.botPlayerCulling != null)
        {
            __instance.botPlayerCulling.SetMode(OfflinePlayerCulling.EMode.Disabled);
            __instance.botPlayerCulling._cullingObject?.Dispose();
        }

        NativeBaseCalls.CallPlayerOnDead(__instance, damageType);
        return false;
    }
}
