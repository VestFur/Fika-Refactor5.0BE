using System.Reflection;
using CommonAssets.Scripts.Cutscenes;
using Fika.Core.Main.Components;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.World;

internal sealed class FinalMissionZoneInitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(FinallExitZone), nameof(FinallExitZone.InitZoneServer));
    [PatchPrefix]
    public static bool Prefix(FinallExitZone __instance)
    {
        if (!FikaFinalMissionDirector.Owns(__instance)) return true;
        FikaFinalMissionDirector.Arm(__instance);
        return false;
    }
}

internal sealed class FinalMissionZoneUpdatePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(FinallExitZone), nameof(FinallExitZone.Update));
    [PatchPrefix]
    public static bool Prefix(FinallExitZone __instance) => !FikaFinalMissionDirector.Owns(__instance);
}

internal sealed class FinalMissionTimerPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(StartCutsceneByTimer), nameof(StartCutsceneByTimer.Update));
    [PatchPrefix]
    public static bool Prefix(StartCutsceneByTimer __instance)
    {
        if (!FikaFinalMissionDirector.Bound) return true;
        FikaFinalMissionDirector.TickTimer(__instance);
        return false;
    }
}

internal sealed class FinalMissionClientExitPatch : ModulePatch
{
    // Intercept the SPT action shim, not the game's shared empty native Init body.
    protected override MethodBase GetTargetMethod() => AccessTools.Method(
        typeof(SPTushonka.SinglePlayer.Patches.Cutscenes.ServerActionPatches.ClientInitActionPatch), "Postfix");
    [PatchPrefix]
    public static bool Prefix(CutscenesClientController __0, string __1)
    {
        // The target is SPT's static Postfix; its parameter named __instance is
        // argument zero, not an instance of the target method.
        if (!FikaFinalMissionDirector.Bound || !FikaFinalMissionDirector.IsLocalFinalExitAction(__0, __1)) return true;
        FikaFinalMissionDirector.LocalExitAction(__0, __1);
        return false;
    }
}

internal sealed class FinalMissionTimeoutPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(FinalCutsceneTimer), nameof(FinalCutsceneTimer.ImmediateExitFromRaid));

    [PatchPrefix]
    public static bool Prefix() => !FikaFinalMissionDirector.Bound || !FikaFinalMissionDirector.EndLocalOnTimeout();
}

internal sealed class FinalMissionServerExitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(
        typeof(SPTushonka.SinglePlayer.Patches.Cutscenes.ServerActionPatches.InitActionPatch), "Prefix");

    [PatchPrefix]
    public static bool Prefix(CutscenesServerController __0, string __1, string __3, ref bool __result)
    {
        if (!FikaFinalMissionDirector.Bound || __0?._actions == null ||
            !__0._actions.TryGetValue(__1, out var action) ||
            action.TryCast<CutsceneActionExitAfterCutscene>() == null ||
            !FikaFinalMissionDirector.SuppressExitAction(__3)) return true;
        // The SPT shim would call StopRaid for the entire LocalGame. Final mission
        // outcomes are per-player and are acknowledged by FikaFinalMissionDirector.
        __result = false;
        return false;
    }
}
