﻿using EFT.Settings.Graphics;
using SPTushonka.Reflection.Patching;
using System.Reflection;

namespace Fika.Headless.Patches;

/// <summary>
/// 阻止游戏覆盖无头端的帧率上限
/// </summary>
public class GraphicsSettingsController_ChangeFramerate_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(GraphicsSettingsController).GetMethod(nameof(GraphicsSettingsController.ChangeFramerate));
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }
}
