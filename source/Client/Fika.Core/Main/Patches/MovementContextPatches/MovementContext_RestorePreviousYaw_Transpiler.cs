using System.Reflection;
using EFT;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.MovementContextPatches;

// 保留 Fika 的本地旋转路径，不再走原版的在线控制器查找。
// SPT 5 的原生 setter 还会刷新缓存的移动方向。
internal class MovementContext_RestorePreviousYaw_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(MovementContext), "RestorePreviousYaw");

    [PatchPrefix]
    public static bool Prefix(MovementContext __instance)
    {
        var rotation = __instance._rotation;
        rotation.x = __instance._previousRotation.x;
        __instance._rotation = rotation;
        __instance.UpdateCachedDirections();
        return false;
    }
}
