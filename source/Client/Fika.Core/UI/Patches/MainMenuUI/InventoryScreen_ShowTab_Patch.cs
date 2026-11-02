using System.Reflection;
using EFT;
using EFT.UI;
using Fika.Core.Main.Utils;
using Fika.Core.Main.GameMode;
using Fika.Core.Networking;
using Fika.Core.UI.Custom;
using SPTushonka.Reflection.Patching;
using static Fika.Core.UI.FikaUIGlobals;

namespace Fika.Core.UI.Patches.MainMenuUI;

/// <summary>在主菜单库存标签页被打开时更新在线状态。</summary>
public sealed class InventoryScreen_ShowTab_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(InventoryScreen).GetMethod(nameof(InventoryScreen.ShowTab));

    [PatchPostfix]
    private static void Postfix()
    {
        if (FikaSingleton<IFikaGame>.Instantiated || FikaSingleton<IFikaNetworkManager>.Instantiated)
            return;

        if (MainMenuUIScript.Exist)
            MainMenuUIScript.Instance.UpdatePresence(EFikaPlayerPresence.IN_STASH);
    }
}
