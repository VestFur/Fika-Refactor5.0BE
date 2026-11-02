using System.Reflection;
using EFT;
using HarmonyLib;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.UI.Patches.LoadingScreen;

public class MapCachingPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        // 显示类的序号随构建版本而变化（移植构建上是 216，47888 上是 237）。
        return LoadMapAndDataTargets.Resolve("_LoadMapAndData_b__0");
    }

    [PatchPrefix]
    public static void Postfix(float totalProgress)
    {
        if (FikaBackendUtils.ClientType == EClientType.None) return;
        var progress = 25f + (totalProgress * 25f);
        LoadingScreenUI.Instance.UpdateAndBroadcast(progress);
    }
}
