using System;
using System.Reflection;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.AI;

/// <summary>
/// 修复一个线上 bug：<see cref="AIPlaceLogicPartisan.Dispose"/> 期间对 <see cref="AIPlaceInfo"/> 没有空值检查，若其为 null 就会引发 <see cref="NullReferenceException"/>
/// </summary>
public class AIPlaceLogicPartisan_Dispose_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(AIPlaceLogicPartisan)
            .GetMethod(nameof(AIPlaceLogicPartisan.Dispose));
    }

    [PatchPrefix]
    public static bool Prefix(AIPlaceLogicPartisan __instance)
    {
        if (__instance._aiPlaceInfo == null)
        {
            return false;
        }

        return true;
    }
}
