using System.Reflection;
using Comfort.Common;
using EFT.UI.Matchmaker;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Patches.LocalGame;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.UI.Patches;

/// <summary>在 SPT 5 的进度视图中保留 Fika 已本地化的连接阶段。</summary>
public sealed class FikaMatchingStatus_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(MatchingProgressView), nameof(MatchingProgressView.UpdateStatus));

    [PatchPostfix]
    private static void Postfix(MatchingProgressView __instance)
    {
        var status = ScreenUpdater.StatusText;
        if (string.IsNullOrEmpty(status) && FikaSingleton<IFikaGame>.Instance is CoopGame game)
            status = game.MatchingStatusText;

        if (!string.IsNullOrEmpty(status) && __instance._stageTitle != null)
        {
            __instance._stageTitle.text = status;
        }
    }
}
