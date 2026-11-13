using System.Reflection;
using EFT;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.MovementContextPatches;

// 保留 Fika 的本地旋转路径，不再走原版的在线控制器查找。
// SPT 5 的原生 setter 还会刷新缓存的移动方向。
internal class MovementContext_Rotation_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(MovementContext), "get_Rotation");

    [PatchPrefix]
    public static bool Prefix(MovementContext __instance, ref Vector2 __result)
    {
        __result = __instance._rotation;
        return false;
    }
}
