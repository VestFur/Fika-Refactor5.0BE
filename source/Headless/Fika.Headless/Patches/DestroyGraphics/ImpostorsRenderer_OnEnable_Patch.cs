﻿using EFT.Impostors;
using SPTushonka.Reflection.Patching;
using System.Reflection;

namespace Fika.Headless.Patches.DestroyGraphics;

/// <summary>
/// 本补丁主要处理删除树木渲染器。
/// </summary>
public class ImpostorsRenderer_OnEnable_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ImpostorsRenderer).GetMethod(nameof(ImpostorsRenderer.OnEnable)); // 这个类相当可疑
    }

    [PatchPrefix]
    public static bool Prefix(ImpostorsRenderer __instance)
    {
        Object.Destroy(__instance);
        return false;
    }
}
