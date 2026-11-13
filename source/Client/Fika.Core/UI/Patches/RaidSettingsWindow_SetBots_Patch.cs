using System.Collections.Generic;
using System.Reflection;
using EFT.UI;
using EFT.UI.Matchmaker;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.UI.Patches;

/// <summary>
/// 这允许用户在修改 AI 数量/难度之后仍能修改所有 AI 设置
/// </summary>
public sealed class RaidSettingsWindow_SetBots_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(RaidSettingsWindow)
            .GetMethod(nameof(RaidSettingsWindow.SetBots));
    }

    [PatchPrefix]
    public static bool Prefix(EFT.UI.Matchmaker.RaidSettingsWindow __instance)
    {
        var hasAi = __instance._aiAmountDropdown.CurrentIndex != 1;
        foreach (var canvasGroup in __instance._wavesCanvasGroups)
        {
            canvasGroup.interactable = hasAi;
            canvasGroup.blocksRaycasts = hasAi;
            canvasGroup.alpha = hasAi ? 1f : 0.3f;
        }

        __instance._aiDifficultyCanvasGroup.SetUnlockStatus(hasAi, true);
        __instance._aiAmountCanvasGroup.SetUnlockStatus(true, true);

        return false;
    }
}