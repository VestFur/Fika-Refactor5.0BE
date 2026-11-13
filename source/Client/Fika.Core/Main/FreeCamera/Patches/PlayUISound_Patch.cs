using System.Reflection;
using EFT.UI;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.FreeCamera.Patches;

public class PlayUISound_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(GUISounds)
            .GetMethod(nameof(GUISounds.PlayUISound), [typeof(EUISoundType)]);
    }

    [PatchPrefix]
    private static bool Prefix(ref EUISoundType soundType)
    {
        if (soundType == EUISoundType.PlayerIsDead)
        {
            // 观众模式下不要播放玩家死亡音效
            if (FikaBackendUtils.IsSpectator)
            {
                return false;
            }
        }

        return true;
    }
}