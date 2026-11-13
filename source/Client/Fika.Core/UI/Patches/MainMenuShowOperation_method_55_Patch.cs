using System.Reflection;
using SPTushonka.Reflection.Patching;
using EFT;

namespace Fika.Core.UI.Patches;

/// <summary>
/// 这允许所有游戏版本编辑 <see cref="RaidSettings"/>
/// </summary>
public class MainMenuShowOperation_method_55_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(MatchmakerOperation)
            .GetMethod(nameof(MatchmakerOperation.CoopModeCheck));
    }

    [PatchPrefix]
    public static bool Prefix(ref bool __result)
    {
        __result = true;
        return false;
    }
}
