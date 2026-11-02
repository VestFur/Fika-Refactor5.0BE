using System.Reflection;
using EFT;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.UI.Patches.LoadingScreen;

public class MapLoadingPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        // 显示类的序号随构建版本变化（移植构建为 216，47888 上为 237）。
        return LoadMapAndDataTargets.Resolve("_LoadMapAndData_b__1");
    }

    [PatchPrefix]
    public static void Postfix(float pr)
    {
        if (FikaBackendUtils.ClientType == EClientType.None) return;
        var progress = 0f + (pr * (FikaBackendUtils.IsHeadless ? 50f : 25f)); // 无头端不缓存剔除
        LoadingScreenUI.Instance.UpdateAndBroadcast(progress);
    }
}
