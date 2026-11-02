using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.AI;

/// <summary>
/// 此补丁通过阻止开火 0.7s，缓解 bot 过快补充弹匣导致 <see cref="Item.Id"/> 失步的问题
/// </summary>
public class BotReload_AddAmmoToMagazines_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BotReload)
            .GetMethod(nameof(BotReload.AddAmmoToMagazines));
    }

    [PatchPostfix]
    public static void Postfix(BotReload __instance)
    {
        __instance._owner.ShootData.BlockFor(0.7f);
    }
}
