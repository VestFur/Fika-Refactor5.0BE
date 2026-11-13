using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.ClientClasses.HandsControllers;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.HandsController;

/// <summary>SPT 5 的射击入口不是虚方法，来自原生操作的调用也在其中。</summary>
public sealed class FirearmController_InitiateShot_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(
        typeof(Player.FirearmController), nameof(Player.FirearmController.InitiateShot),
        [typeof(IWeapon), typeof(Ammo), typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(int), typeof(float)]);

    [PatchPrefix]
    private static void Prefix(Player.FirearmController __instance, IWeapon weapon, Ammo ammo,
        Vector3 shotPosition, Vector3 shotDirection, Vector3 fireportPosition, int chamberIndex, float overheat)
    {
        // 对于被注入的控制器，IL2CPP 可能提供基类包装。
        var controller = __instance.TryCast<FikaClientFirearmController>();
        controller?.SendInitiatedShot(weapon, ammo, shotPosition, shotDirection, fireportPosition, chamberIndex, overheat);
    }
}
