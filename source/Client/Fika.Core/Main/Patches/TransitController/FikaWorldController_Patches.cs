using System.Reflection;
using EFT;
using EFT.GlobalEvents;
using EFT.Interactive;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.HostClasses;
using HarmonyLib;
using SPTushonka.Reflection.Patching;
using FikaClientTransit = Fika.Core.Main.ClientClasses.ClientTransitController;
using FikaClientRunddans = Fika.Core.Main.ClientClasses.ClientRunddansController;

namespace Fika.Core.Main.Patches.TransitController;

public sealed class FikaTransitEnterPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(LocalTransitController), nameof(LocalTransitController.PlayerEnter));
    [PatchPrefix]
    private static bool Prefix(LocalTransitController __instance, TransitPoint point, Player player)
    {
        var controller = FikaWorldControllers.Find<FikaHostTransitController>(__instance);
        if (controller == null) return true;
        controller.OnHostPlayerEnter(point, player);
        return false;
    }
}

public sealed class FikaTransitExitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(LocalTransitController), nameof(LocalTransitController.PlayerExit));
    [PatchPrefix]
    private static bool Prefix(LocalTransitController __instance, TransitPoint point, Player player)
    {
        var controller = FikaWorldControllers.Find<FikaHostTransitController>(__instance);
        if (controller == null) return true;
        controller.OnHostPlayerExit(point, player);
        return false;
    }
}

public sealed class FikaTransitSizesPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(LocalTransitController), nameof(LocalTransitController.Sizes));
    [PatchPrefix]
    private static bool Prefix(LocalTransitController __instance, int pointId, Il2CppSystem.Collections.Generic.Dictionary<int, byte> sizes)
    {
        var controller = FikaWorldControllers.Find<FikaHostTransitController>(__instance);
        if (controller == null) return true;
        controller.Sizes(pointId, sizes);
        return false;
    }
}

public sealed class FikaTransitTimersPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(LocalTransitController), nameof(LocalTransitController.Timers));
    [PatchPrefix]
    private static bool Prefix(LocalTransitController __instance, int pointId, Il2CppSystem.Collections.Generic.Dictionary<int, ushort> timers)
    {
        var controller = FikaWorldControllers.Find<FikaHostTransitController>(__instance);
        if (controller == null) return true;
        controller.Timers(pointId, timers);
        return false;
    }
}

public sealed class FikaTransitInactivePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(EFT.ClientTransitController), nameof(EFT.ClientTransitController.InactivePointNotification));
    [PatchPrefix]
    private static bool Prefix(EFT.ClientTransitController __instance, int playerId, int pointId)
    {
        var controller = FikaWorldControllers.Find<FikaHostTransitController>(__instance);
        if (controller == null) return true;
        controller.InactivePointNotification(playerId, pointId);
        return false;
    }
}

public sealed class FikaTransitInteractPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(LocalTransitController), nameof(LocalTransitController.InteractWithTransit));
    [PatchPrefix]
    private static bool Prefix(LocalTransitController __instance, Player player, InteractWithTransitPacket packet)
    {
        var controller = FikaWorldControllers.Find<FikaHostTransitController>(__instance);
        if (controller == null) return true;
        controller.InteractWithTransit(player, packet);
        return false;
    }
}

public sealed class FikaTransitExtractPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(LocalTransitController), nameof(LocalTransitController.Transit));
    [PatchPrefix]
    private static bool Prefix(LocalTransitController __instance, TransitPoint point, int playersCount, string hash,
        Il2CppSystem.Collections.Generic.Dictionary<string, ProfileKey> keys, Player player, ref Il2CppSystem.Threading.Tasks.Task __result)
    {
        var controller = FikaWorldControllers.Find<FikaHostTransitController>(__instance);
        if (controller == null) return true;
        controller.Transit(point, playersCount, hash, keys, player);
        __result = Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        return false;
    }
}

public sealed class FikaTransitInitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(NetworkTransitController), nameof(NetworkTransitController.Init));
    [PatchPrefix]
    private static bool Prefix(NetworkTransitController __instance, TransitInitEvent data)
    {
        var controller = FikaWorldControllers.Find<FikaClientTransit>(__instance);
        if (controller == null) return true;
        controller.OnInitEvent(data);
        return false;
    }
}

public sealed class FikaTransitDisposePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(EFT.TransitController), nameof(EFT.TransitController.Dispose));
    [PatchPostfix]
    private static void Postfix(EFT.TransitController __instance)
    {
        FikaWorldControllers.Find<FikaHostTransitController>(__instance)?.OnNativeDisposed();
        FikaWorldControllers.Find<FikaClientTransit>(__instance)?.OnNativeDisposed();
    }
}

public sealed class FikaRunddansInteractPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(LocalRunddansController), nameof(LocalRunddansController.InteractWithEventObject));
    [PatchPrefix]
    private static bool Prefix(LocalRunddansController __instance, Player player, InteractWithEventObjectPacket packet)
    {
        var controller = FikaWorldControllers.Find<HostRunddansController>(__instance);
        if (controller == null) return true;
        controller.InteractWithEventObject(player, packet);
        return false;
    }
}

public sealed class FikaRunddansStatePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(LocalRunddansController), nameof(LocalRunddansController.OnTriggerStateChanged));
    [PatchPostfix]
    private static void Postfix(LocalRunddansController __instance, EventObject.EState state) =>
        FikaWorldControllers.Find<HostRunddansController>(__instance)?.OnTriggerStateChanged(state);
}

public sealed class FikaRunddansDisposePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(RunddansController), nameof(RunddansController.Dispose));
    [PatchPostfix]
    private static void Postfix(RunddansController __instance)
    {
        FikaWorldControllers.Find<HostRunddansController>(__instance)?.OnNativeDisposed();
        FikaWorldControllers.Find<FikaClientRunddans>(__instance)?.OnNativeDisposed();
    }
}
