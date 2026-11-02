using System.Reflection;
using EFT.RocketLauncher;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Rockets;

/// <summary>
/// 除非是服务器，否则不要执行该方法，以避免重复伤害
/// </summary>
public class RocketProjectile_TryVehicleCollision_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(RocketProjectile)
            .GetMethod(nameof(RocketProjectile.TryVehicleCollision));
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return FikaBackendUtils.ClientType == EClientType.None || FikaBackendUtils.IsServer;
    }
}
