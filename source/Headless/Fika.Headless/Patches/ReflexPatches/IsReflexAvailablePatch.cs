﻿using EFT.CameraControl;
using SPTushonka.Reflection.Patching;
using System.Reflection;

namespace Fika.Headless.Patches.ReflexPatches;

/// <summary>
/// 定位无参数的 IsReflexAvailable() 方法
/// </summary>
public class IsReflexAvailablePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ReflexController)
            .GetMethod(nameof(ReflexController.IsReflexAvailable),
            BindingFlags.Public | BindingFlags.Static,
            null, [], null);
    }

    [PatchPrefix]
    public static bool Prefix(ref bool __result)
    {
        __result = false;
        return false;
    }
}
