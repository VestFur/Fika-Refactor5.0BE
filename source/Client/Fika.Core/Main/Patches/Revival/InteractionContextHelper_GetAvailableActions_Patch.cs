using EFT.UI;
using System.Linq;
using System.Reflection;
using EFT;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Revival;

// (2026-10-27 W39 / TARGETS T7) 这一行原来会让 PatchManager 直接跳过本补丁（`[IgnoreAutoPatch]` 在
// PatchManager.cs 的过滤里被排除），于是 `ActiveHealthController.Kill` 从来没有被拦过 —— Fika 的倒地/救援
// 整条链在结构上就是死的（永远不会进入 Downed，也就不会有救援交互）。本补丁自带运行期闸门
// （`controller.ReviveEnabled && controller.CanBeDowned && ...`），关闭复活时它返回 true 放行原生，行为不变。
internal sealed class InteractionContextHelper_GetAvailableActions_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(InteractionContextHelper)
            .GetMethods()
            .First(x => x.Name == nameof(InteractionContextHelper.GetAvailableActions)
                && x.GetParameters()[0].ParameterType.Equals(typeof(GamePlayerOwner)));
    }

    [PatchPrefix]
    public static bool Prefix(GamePlayerOwner owner, IInteractive interactive, ref AvailableInteractionState __result)
    {
        // AP (analysis/nonfika-raid-inertness)：这是本地玩家的交互菜单，也就是
        // “我能对这个东西执行哪些操作”这一问题的热路径。ReviveInteractable 只
        // 存在于我们自己的突袭中，但该守卫也能让本移植避开原生突袭自己的菜单。
        if (FikaRaidGate.Skip("InteractionContextHelper.GetAvailableActions prefix"))
        {
            return true;
        }

        if (interactive is not ReviveInteractable reviveInteractable)
        {
            return true;
        }

        __result = reviveInteractable.GetActions(owner);
        return false;
    }
}
