﻿using EFT.Vehicle;
using HarmonyLib;
using SPTushonka.Reflection.Patching;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Fika.Headless.Patches.BTR;

/// <summary>
/// 防止无头端因没有玩家而出现空引用
/// </summary>
public class BtrController_ClientSyncServicePurchaseEvent_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BtrController)
            .GetMethod(nameof(BtrController.ClientSyncServicePurchaseEvent));
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }
}
