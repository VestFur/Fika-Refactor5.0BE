﻿using HarmonyLib;
using SPTushonka.Reflection.Patching;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Fika.Headless.Patches;

/// <summary>
/// 阻止不必要的代码运行
/// </summary>
public class LevelSettings_ApplySettings_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(LevelSettings)
            .GetMethod(nameof(LevelSettings.ApplySettings));
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }
}
