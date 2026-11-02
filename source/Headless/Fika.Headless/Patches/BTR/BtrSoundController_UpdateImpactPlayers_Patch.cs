﻿using Audio.NPC.BtrDriver;
using Audio.Vehicles.BTR;
using SPTushonka.Reflection.Patching;
using System.Reflection;

namespace Fika.Headless.Patches.BTR;

/// <summary>
/// 防止无头端因音频未被处理而出现空引用
/// </summary>
public class BtrSoundController_UpdateImpactPlayers_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BtrSoundController)
            .GetMethod(nameof(BtrSoundController.Update));
    }
    
    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }
}
