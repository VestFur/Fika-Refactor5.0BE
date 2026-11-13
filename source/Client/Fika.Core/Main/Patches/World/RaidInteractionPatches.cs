using System.Reflection;
using CommonAssets.Scripts.Cutscenes;
using EFT;
using EFT.Interactive;
using EFT.Passcodes;
using EFT.GlobalEvents;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.World;

internal sealed class PasscodeResultSyncPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ClientLocalPasscodeController),
        nameof(ClientLocalPasscodeController.HandleSubmitPasscode));

    [PatchPrefix]
    public static void Prefix(ClientLocalPasscodeController __instance, PasscodeTerminal terminal, string passcode, out bool __state)
    {
        // Native return value means "attempt handled", including a WRONG code.
        // Capture the code comparison before the native method changes the door state.
        __state = terminal != null && terminal.IsInteractable &&
            __instance._generatedPasscodes.TryGetValue(terminal.PasscodeId, out var expected) &&
            string.Equals(expected, passcode, System.StringComparison.Ordinal);
    }

    [PatchPostfix]
    public static void Postfix(Player player, PasscodeTerminal terminal, bool __result, bool __state) =>
        RaidInteractionSync.BroadcastPasscodeResult(player, terminal, __result && __state);
}

internal sealed class RaidDialogClaimPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(RaidDialogEntryPoint),
        nameof(RaidDialogEntryPoint.StartDialogOffline));

    [PatchPrefix]
    public static bool Prefix(RaidDialogEntryPoint __instance, ref Il2CppSystem.Threading.Tasks.Task __result)
    {
        if (!FikaBackendUtils.IsClient || RaidInteractionSync.World == null ||
            DialogEntryOccupancy.HasLocalClaim(__instance.PointId)) return true;
        __result = NativeTask.FromManaged(MainThreadDispatcher.RunAsync(() => DialogEntryOccupancy.StartDialog(__instance)));
        return false;
    }
}

internal sealed class CutsceneStartSyncPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(CutscenesServerController),
        nameof(CutscenesServerController.StartCutscene));

    [PatchPrefix]
    public static bool Prefix(ref CutsceneProcess __result)
    {
        if (RaidInteractionSync.World == null || !FikaBackendUtils.IsClient) return true;
        // Local quest/trigger handlers may still call the offline server controller.
        // Only a host packet is allowed to start the client's native playback.
        __result = null;
        return false;
    }

    [PatchPostfix]
    public static void Postfix(CutsceneProcess __result, bool forceMode, float initTime) =>
        RaidInteractionSync.StartCutscene(__result, forceMode, initTime);
}

internal sealed class CutsceneViewCompleteSyncPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(CutscenesClientController),
        nameof(CutscenesClientController.TimelineShowed));

    [PatchPrefix]
    public static void Prefix(CutscenesClientController __instance, ref bool skip, out int __state)
    {
        __state = 0;
        if (RaidInteractionSync.World == null) return;
        if (__instance._currentCutsceneClientOnly)
        {
            // Client-only finals still reach the ClientPlayer cast in this build.
            // They have no server process to acknowledge, but need native cleanup.
            skip = true;
            return;
        }
        if (!skip && !__instance._currentCutsceneViewerOnly) __state = __instance._cutsceneProcessId;
        // The native network branch casts the local player to ClientPlayer, while
        // FikaPlayer derives from LocalPlayer. Keep its UI/effect cleanup and replace
        // only the completion notification (the same cleanup is used for skip=true).
        skip = true;
    }

    [PatchPostfix]
    public static void Postfix(string timelineId, int __state)
    {
        if (__state != 0) GlobalEventsController.CreateEvent<SendEndCutsceneOnServerEvent>().Invoke(timelineId, __state);
        FikaFinalMissionDirector.LocalTimelineComplete(timelineId);
    }
}

internal sealed class CutsceneEndSyncPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(CutscenesServerController),
        nameof(CutscenesServerController.EndCutscene));

    [PatchPostfix]
    public static void Postfix(int cutsceneProcessId, bool skip) => RaidInteractionSync.EndCutscene(cutsceneProcessId, skip);
}

// Fika replaces the single-player driver on both host and peers. Preserve SPT's
// implementation outside a bound Fika world.
internal sealed class FinalMissionClientStartGuardPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(
        typeof(SPTushonka.SinglePlayer.Patches.Cutscenes.FinalMissionDirectorPatches.StartPatch), "Postfix");

    [PatchPrefix]
    public static bool Prefix() => !FikaFinalMissionDirector.Bound;
}

internal sealed class FinalMissionClientTickGuardPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(
        typeof(SPTushonka.SinglePlayer.Patches.Cutscenes.FinalMissionDirectorPatches.TickPatch), "Postfix");

    [PatchPrefix]
    public static bool Prefix() => !FikaFinalMissionDirector.Bound;
}
