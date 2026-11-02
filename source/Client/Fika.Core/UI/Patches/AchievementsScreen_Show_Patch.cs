using System.Reflection;
using EFT.UI;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.UI.Patches;

/// <summary>
/// 默认情况下，若会话模式为 <see cref="ESessionMode.Pve"/>，则不显示已获得某成就的玩家数量，此补丁强制无论何种模式都显示它
/// </summary>
public class AchievementsScreen_Show_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(AchievementsScreen).GetMethod(nameof(AchievementsScreen.Show));
    }

    [PatchPostfix]
    public static void Postfix(EFT.UI.AchievementsScreen __instance)
    {
        __instance._allPlayersPercentCanvasGroup.alpha = 1f;
        __instance._allPlayersPercentCanvasGroup.interactable = true;
    }
}
