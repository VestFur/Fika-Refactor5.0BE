using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.RocketLauncher;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Rockets;

/// <summary>
/// 除非是服务器，否则不要执行该方法，以避免重复伤害
/// </summary>
public class RocketProjectile_Launch_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(RocketProjectile)
            .GetMethod(nameof(RocketProjectile.Launch));
    }

    [PatchPrefix]
    public static bool Prefix(RocketProjectile __instance)
    {
        if (FikaBackendUtils.ClientType == EClientType.None) return true;
        if (Singleton<GameWorld>.Instance is ClientLocalGameWorld && FikaBackendUtils.IsServer)
        {
            __instance._coneBlastCoroutine = __instance.StartCoroutine(__instance._backBlastModel.ConeBlast(__instance._coneBlastCoroutine));
        }
        __instance.CreateShot();
        __instance.SetVisibleModel(true);
        __instance._isLaunched = true;

        return false;
    }
}
