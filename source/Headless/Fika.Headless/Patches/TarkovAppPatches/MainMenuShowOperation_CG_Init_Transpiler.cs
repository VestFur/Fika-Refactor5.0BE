using EFT.Hideout;
using EFT;
using SPTushonka.Reflection.Patching;
using System.Reflection;

namespace Fika.Headless.Patches.TarkovAppPatches;

/// <summary>
/// 阻止 <see cref="MainMenuShowOperation"/> 尝试设置藏身处库存
/// </summary>
public class MainMenuShowOperation_CG_Init_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(HideoutRepresentation).GetMethod(nameof(HideoutRepresentation.SetupControllers));
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }
}
