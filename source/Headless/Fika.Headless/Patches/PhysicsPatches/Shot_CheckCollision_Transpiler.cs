﻿using EFT;
using EFT.Ballistics;
using EFT.Vehicle;
using HarmonyLib;
using SPTushonka.Reflection.Patching;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Fika.Headless.Patches.PhysicsPatches;

/// <summary>
/// 本补丁在子弹检测是否命中之前同步所有变换
/// </summary>
internal class Shot_CheckCollision_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Shot)
            .GetMethod(nameof(Shot.CheckCollision), [typeof(Vector3), typeof(Vector3)]);
    }

    [PatchPrefix]
    public static void Prefix()
    {
        Physics.SyncTransforms();
    }
}
