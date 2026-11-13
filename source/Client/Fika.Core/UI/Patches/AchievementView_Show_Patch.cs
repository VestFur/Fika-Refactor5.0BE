using System.Reflection;
using EFT.Achievements;
using SPTushonka.Reflection.Patching;
using TMPro;

namespace Fika.Core.UI.Patches;

/// <summary>
/// 默认情况下，若会话模式为 <see cref="ESessionMode.Pve"/>，则不显示已获得某成就的玩家数量，此补丁强制无论何种模式都显示它
/// </summary>
public class AchievementView_Show_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(AchievementView).GetMethod("EFT.Achievements.IAchievementView.Show", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
    }

    [PatchPostfix]
    public static void Postfix(EFT.Achievements.AchievementView __instance)
    {
        __instance._globalProgressText.alpha = 1f;
    }
}
