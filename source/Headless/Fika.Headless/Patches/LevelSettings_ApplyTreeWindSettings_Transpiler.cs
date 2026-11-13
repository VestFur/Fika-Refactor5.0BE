﻿using SPTushonka.Reflection.Patching;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Fika.Headless.Patches;

public class LevelSettings_ApplyTreeWindSettings_Transpiler : ModulePatch
{
    /// <summary>
    /// 阻止不必要的代码运行
    /// </summary>
    protected override MethodBase GetTargetMethod()
    {
        return typeof(LevelSettings)
            .GetMethod(nameof(LevelSettings.ApplyTreeWindSettings));
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }
}
