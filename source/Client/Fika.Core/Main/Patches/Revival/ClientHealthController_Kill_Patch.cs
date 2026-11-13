using System.Reflection;
using EFT;
using Fika.Core.Main.ClientClasses;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Revival;

// (2026-10-27 W39 / TARGETS T7) 这一行原来会让 PatchManager 直接跳过本补丁（`[IgnoreAutoPatch]` 在
// PatchManager.cs 的过滤里被排除），于是 `ActiveHealthController.Kill` 从来没有被拦过 —— Fika 的倒地/救援
// 整条链在结构上就是死的（永远不会进入 Downed，也就不会有救援交互）。本补丁自带运行期闸门
// （`controller.ReviveEnabled && controller.CanBeDowned && ...`），关闭复活时它返回 true 放行原生，行为不变。
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
