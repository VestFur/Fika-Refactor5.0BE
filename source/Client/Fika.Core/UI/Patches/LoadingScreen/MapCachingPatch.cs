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
        return AccessTools.Method(typeof(TarkovApplication.__c__DisplayClass216_0), "_LoadMapAndData_b__0");
    }

    [PatchPrefix]
    public static void Postfix(float totalProgress)
    {
        if (FikaBackendUtils.ClientType == EClientType.None) return;
        var progress = 25f + (totalProgress * 25f);
        LoadingScreenUI.Instance.UpdateAndBroadcast(progress);
    }
}
