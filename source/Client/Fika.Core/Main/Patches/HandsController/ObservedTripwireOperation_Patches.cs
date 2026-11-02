using System.Reflection;
using Comfort.Common;
using EFT;
using Fika.Core.Main.ObservedClasses.HandsControllers;
using HarmonyLib;
using SPTushonka.Reflection.Patching;
using Operation = EFT.Player.GrenadeHandsController.PlantTripwireOperation;

namespace Fika.Core.Main.Patches.HandsController;

internal static class ObservedTripwireOperation
{
    internal static bool IsObserved(Operation operation) =>
        operation.Controller.TryCast<ObservedGrenadeController>() != null;
}

public sealed class ObservedTripwireStartPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(Operation), nameof(Operation.Start));
    [PatchPrefix]
    private static bool Prefix(Operation __instance)
    {
        if (!ObservedTripwireOperation.IsObserved(__instance)) return true;
        new ObservedTripwireState(__instance).Start();
        return false;
    }
}

public sealed class ObservedTripwireIdlePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(Operation), nameof(Operation.OnIdleStartAction));
    [PatchPrefix]
    private static bool Prefix(Operation __instance)
    {
        if (!ObservedTripwireOperation.IsObserved(__instance)) return true;
        new ObservedTripwireState(__instance).OnIdleStartAction();
        return false;
    }
}

public sealed class ObservedTripwirePlantPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(Operation), nameof(Operation.PlantTripwire));
    [PatchPrefix]
    private static bool Prefix(Operation __instance)
    {
        if (!ObservedTripwireOperation.IsObserved(__instance)) return true;
        new ObservedTripwireState(__instance).PlantTripwire();
        return false;
    }
}

public sealed class ObservedTripwireExecutePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(Operation), nameof(Operation.Execute));
    [PatchPrefix]
    private static bool Prefix(Operation __instance, Callback callback)
    {
        if (!ObservedTripwireOperation.IsObserved(__instance)) return true;
        new ObservedTripwireState(__instance).Execute(callback);
        return false;
    }
}

// 远程安放由 Fika 包驱动。原生输入以及安放结束时的
// 库存变更不得再执行一次权威的安放操作。
public sealed class ObservedTripwireEndPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(Operation), nameof(Operation.OnEnd));
    [PatchPrefix]
    private static bool Prefix(Operation __instance)
    {
        if (!ObservedTripwireOperation.IsObserved(__instance)) return true;
        new ObservedTripwireState(__instance).End();
        return false;
    }
}

public sealed class ObservedTripwireOnDropFinishedPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(Operation), nameof(Operation.OnDropFinishedAction));
    [PatchPrefix]
    private static bool Prefix(Operation __instance)
    {
        if (!ObservedTripwireOperation.IsObserved(__instance)) return true;
        new ObservedTripwireState(__instance).DropFinished();
        return false;
    }
}

public sealed class ObservedTripwireFastForwardPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(Operation), nameof(Operation.FastForward));
    [PatchPrefix]
    private static bool Prefix(Operation __instance)
    {
        if (!ObservedTripwireOperation.IsObserved(__instance)) return true;
        new ObservedTripwireState(__instance).FastForward();
        return false;
    }
}

public sealed class ObservedTripwireFireInputPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(Operation), nameof(Operation.HandleFireInput));
    [PatchPrefix]
    private static bool Prefix(Operation __instance) => !ObservedTripwireOperation.IsObserved(__instance);
}

public sealed class ObservedTripwireAltFireInputPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(Operation), nameof(Operation.HandleAltFireInput));
    [PatchPrefix]
    private static bool Prefix(Operation __instance) => !ObservedTripwireOperation.IsObserved(__instance);
}
