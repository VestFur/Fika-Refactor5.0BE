﻿using HarmonyLib;
using SPTushonka.Reflection.Patching;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Fika.Headless.Patches;

/// <summary>
/// 本补丁的目的是在无头主机上禁用机器人休眠
/// </summary>
[IgnoreAutoPatch]
public class BotStandBy_Update_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BotStandBy)
            .GetMethod(nameof(BotStandBy.Update));
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }
}
