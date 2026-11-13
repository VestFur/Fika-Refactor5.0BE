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

/// <summary>
/// (2026-10-19) Fika 客户端并不拥有原生需求所读取的过境记录（队伍人数、
/// 小队成员），于是每个过境点都回答“这个过境点现在无法使用”，交互
/// 提示始终不可用。主机自己会校验这次交互（<c>InteractWithTransit</c>），因此
/// 客户端侧的需求检查一律回答 true，其余交给现有的数据包完成。
/// </summary>
public sealed class FikaTransitClientRequirementsPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.DeclaredMethod(typeof(TransitPoint), nameof(TransitPoint.CheckInitialRequirements));

    [PatchPrefix]
    private static bool Prefix(ref bool __result)
    {
        if (!Fika.Core.Main.Utils.FikaBackendUtils.IsClient) return true;
        __result = true;
        return false;
    }
}

public sealed class FikaTransitClientPartySizePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.DeclaredMethod(typeof(TransitPoint), nameof(TransitPoint.CheckAlivePartySizeRequirement));

    [PatchPrefix]
    private static bool Prefix(ref bool __result)
    {
        if (!Fika.Core.Main.Utils.FikaBackendUtils.IsClient) return true;
        __result = true;
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
