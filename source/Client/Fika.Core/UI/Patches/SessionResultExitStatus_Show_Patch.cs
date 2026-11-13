using System;
using System.Reflection;
using EFT;
using EFT.UI;
using EFT.UI.SessionEnd;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.UI.Patches;

public class SessionResultExitStatus_Show_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(SessionResultExitStatus).GetMethod(nameof(SessionResultExitStatus.Show), [typeof(Profile),
            typeof(PlayerVisualRepresentation),
            typeof(ESideType),
            typeof(ExitStatus),
            typeof(Il2CppSystem.TimeSpan),
            typeof(IEftSession),
            typeof(bool)]);
    }

    [PatchPostfix]
    static void PatchPostfix(SessionResultExitStatus __instance)
    {
        // 以观战者身份时跳过战局结算退出状态界面
        if (FikaBackendUtils.IsSpectator)
        {
            __instance._mainMenuButton.OnClick.Invoke();
        }
    }
}
