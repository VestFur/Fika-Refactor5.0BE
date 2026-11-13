using System.Reflection;
using EFT;
using Fika.Core.Main.ClientClasses;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Revival;

[IgnoreAutoPatch]
internal sealed class ClientHealthController_Kill_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(EFT.HealthSystem.ActiveHealthController)
            .GetMethod(nameof(EFT.HealthSystem.ActiveHealthController.Kill));
    }

    [PatchPrefix]
    public static bool Prefix(EFT.HealthSystem.ActiveHealthController __instance, EDamageType damageType)
    {
        var controller = __instance.TryCast<ClientHealthController>();
        if (controller != null && controller.Player.IsYourPlayer && controller.ReviveEnabled && controller.CanBeDowned && !controller.CheckIfDamageShouldInstantKill())
        {
            controller.IsAlive = false;
            controller.NetworkSyncIsAlive(damageType);
            return false;
        }

        return true;
    }
}
