using EFT.InventoryLogic;
using System.Reflection;
using Comfort.Common;
using Fika.Core.Main.GameMode;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches;

/// <summary>
/// 用于帮助我们在一场战局中跟踪已投出的手雷，以统计击杀进度
/// </summary>
public class GrenadeFactory_Init_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(GrenadeFactory)
            .GetMethod(nameof(GrenadeFactory.Create));
    }

    [PatchPostfix]
    public static void Postfix(ThrowWeap item)
    {
        var fikaGame = FikaSingleton<IFikaGame>.Instance;
        if (fikaGame != null)
        {
            fikaGame.GameController.ThrownGrenades.Add(item);
        }
    }
}
