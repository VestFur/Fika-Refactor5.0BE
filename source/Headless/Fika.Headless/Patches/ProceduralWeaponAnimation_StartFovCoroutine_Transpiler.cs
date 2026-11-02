﻿using EFT;
using EFT.Animations;
using SPTushonka.Reflection.Patching;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Fika.Headless.Patches;

/// <summary>
/// 本补丁旨在减少用于更新无头端看不到的数据所消耗的 CPU 周期
/// </summary>
public class ProceduralWeaponAnimation_StartFovCoroutine_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ProceduralWeaponAnimation).GetMethod(nameof(ProceduralWeaponAnimation.StartFovCoroutine), [typeof(Player)]);
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }
}
