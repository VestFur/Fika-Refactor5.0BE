﻿using SPTushonka.Reflection.Patching;
using System.Reflection;

namespace Fika.Headless.Patches.DestroyGraphics;

public class StaticDeferredDecal_OnEnable_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(StaticDeferredDecal)
            .GetMethod(nameof(StaticDeferredDecal.OnEnable));
    }

    [PatchPrefix]
    public static bool Prefix(StaticDeferredDecal __instance)
    {
        // 其他脚本和碰撞体可能共用此对象；只移除可视组件。
        Object.Destroy(__instance);
        return false;
    }
}
