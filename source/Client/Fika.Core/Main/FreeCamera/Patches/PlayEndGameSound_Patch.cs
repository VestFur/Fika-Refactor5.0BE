using System.Reflection;
using EFT.UI;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.FreeCamera.Patches;

public class PlayEndGameSound_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(GUISounds)
            .GetMethod(nameof(GUISounds.PlayEndGameSound), [typeof(EEndGameSoundType)]);
    }

    [PatchPrefix]
    private static bool Prefix()
    {
        // 观众模式下不要播放游戏结束音效
        if (FikaBackendUtils.IsSpectator)
        {
            return false;
        }

        return true;
    }
}
