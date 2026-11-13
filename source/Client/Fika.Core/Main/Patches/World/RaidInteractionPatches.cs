using System.Reflection;
using CommonAssets.Scripts.Cutscenes;
using EFT;
using EFT.Interactive;
using EFT.Passcodes;
using EFT.GlobalEvents;
using EFT.UI;
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
    public static void Postfix(Player player, PasscodeTerminal terminal, bool __result, bool __state)
    {
        // AP (analysis/nonfika-raid-inertness): a passcode terminal is a world interaction every raid has.
        // RaidInteractionSync is only bound inside a raid of ours, so this was already a no-op outside one;
        // the guard makes that explicit and names it in the log when it happens.
        if (FikaRaidGate.Skip("ClientLocalPasscodeController.HandleSubmitPasscode postfix"))
        {
            return;
        }

        var handled = __result && __state;
        // BB (analysis/icebreaker-keypad-door): the one line a host log has to hold for a passcode door - which
        // attempt it announced, and whether it announced a success.
        // BU (analysis/icebreaker-keypad-3): every machine whose world holds the game's own local controller runs
        // this check, so the same method is also the native verdict of a client - the line says which one it is,
        // and only a host announces anything (BroadcastPasscodeResult refuses on every other role).
        var server = FikaBackendUtils.IsServer;
        
        RaidInteractionSync.BroadcastPasscodeResult(player, terminal, handled);
    }
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
    public static bool Prefix(ref CutsceneProcess __result, string __0,
        Il2CppSystem.Collections.Generic.List<int> __1)
    {
        if (RaidInteractionSync.World == null || !FikaBackendUtils.IsClient) return true;
        // (analysis/cutscene-client-native-trigger) The one local start that is let through: this machine's own
        // trigger asked for a cutscene the host never dispatched, so the bridge granted that very cutscene to the
        // native call. Everything after this return is native - the process, its seat table and the local viewer's
        // playback - which is the point: the client runs the same trigger chain the host runs.
        if (FikaLocalCutsceneTrigger.GrantNativeStart(__0, __1)) return true;
        // Local quest/trigger handlers may still call the offline server controller.
        // Only a host packet - or the granted local trigger above - starts the client's native playback. The
        // dropped request is handed to the bridge, which re-runs it natively when the host stays silent
        // (analysis/cutscene-client-native-trigger).
        FikaLocalCutsceneTrigger.NoteNativeRequest(__0);
        __result = null;
        
        return false;
    }

    [PatchPostfix]
    public static void Postfix(CutsceneProcess __result, bool forceMode, float initTime)
    {
        RaidInteractionSync.StartCutscene(__result, forceMode, initTime);
        
        // (analysis/cutscene-client-native-trigger) The second half of a granted local start: a process this
        // machine created itself has to be handed to this machine's own client controller. On a host that step is
        // the broadcast above; on a client the offline mode used to own it and Fika switched that off
        // (RaidInteractionSync.ConfigureControllers), so the bridge raises the same native event the host packet
        // would have raised - with this machine's own process id.
        if (FikaBackendUtils.IsClient && __result != null)
            RaidInteractionSync.PlayLocalNativeProcess(__result);
    }
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
        if (!skip && !__instance._currentCutsceneViewerOnly)
        {
            __state = __instance._cutsceneProcessId;
            
        }
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

// (analysis/cutscene-view-and-end) The two per-frame methods the whole cutscene clock depends on. The round-16
// host log proves nobody calls the server one (its elapsed time stands still while the process is alive), and the
// same question for the client controller - whose per-frame step is what starts and finishes this machine's own
// scene - could only be answered from the outside: it is never entered (no phase=view line exists anywhere) and
// nothing is ever rendered. These patches count only: no return value, no parameter and no instance is touched,
// so the game behaves exactly as it did. Their line is "phase=gate:ticks ... server engine=N bridge=M ..." and it
// splits every call into "a native caller" and "this bridge".
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
// ICEBREAKER-CUTSCENE-BRIDGE (analysis/icebreaker-cutscene): the client controller's timeline bank is
// repaired at the head of FikaClient.ApplyPendingRaidInteractions itself, before the queued host cutscene
// packets are drained - the same call this file used to place with a Harmony prefix. The prefix never
// installed: the patch manager detours through IL2CPP, and a method of the Fika assembly itself has no IL2CPP
// detour ("CutsceneClientBankTickPatch: NotSupportedException ... requires an IL2CPP detour", round-15 host
// log, analysis/cutscene-play-and-end/REPORT.md). The call site is in FikaClient.RaidInteractions.cs.

// =============================================================================================================
// BB (analysis/icebreaker-keypad-door): the keypad chain of one raid, named and repaired.
//
// The report of this round is "the icebreaker keypad cannot be used on a client". The game's own submit guard
// refuses such a code with "Tried to enter code on location without passcodes" before any Fika packet exists,
// so the host never learns about the attempt and the door never opens although the host knows the code. None of
// the three patches below filters, moves or replaces a native interaction:
//   * PasscodeMenuDiagPatch   postfix, reads only  - the interaction menu of a PasscodeTerminal (offered/empty);
//   * PasscodePanelShowDiagPatch prefix, void      - the native passcode panel of this machine opened;
//   * PasscodeSubmitRelayPatch postfix, ref __result - a code the game refused is handed to the host through the
//     very packet the native event path uses. Only on a client, only inside a bound Fika raid, and only after
//     that host has answered this client at least once; with KeypadDiag.RepairEnabled=false it never acts.
// =============================================================================================================

/// <summary>The shared text of the keypad lines: door, controller kind and the two states the guard decides on.</summary>
internal static class PasscodeText
{
    internal static string Door(PasscodeTerminal terminal)
    {
        try
        {
            return terminal == null ? "-" : (terminal.PasscodeId ?? "-") + "#" + terminal.NetId;
        }
        catch (System.Exception)
        {
            return "?";
        }
    }

    internal static string Controller(ClientPasscodeController controller)
    {
        try
        {
            if (controller == null) return "none";
            if (controller.TryCast<ClientNetworkPasscodeController>() != null) return "network";
            if (controller.TryCast<ClientLocalPasscodeController>() != null) return "local";
            return "client";
        }
        catch (System.Exception)
        {
            return "?";
        }
    }

    /// <summary>How many passcode entries the location of this controller carries (null = none at all).</summary>
    internal static string Settings(ClientPasscodeController controller)
    {
        try
        {
            var entries = controller?._passcodeLocationSettings?.Entries;
            return entries == null ? "null" : entries.Length.ToString();
        }
        catch (System.Exception)
        {
            return "?";
        }
    }

    internal static string Terminals(ClientPasscodeController controller)
    {
        try
        {
            return controller?._passcodeTerminals == null ? "-" : controller._passcodeTerminals.Count.ToString();
        }
        catch (System.Exception)
        {
            return "?";
        }
    }
}

/// <summary>
/// The native interaction menu of one keypad: a postfix that only reads the state the native dispatcher
/// returned. Nothing about the actions, their order or their delegates is touched - the menu of this machine
/// cannot change because of this patch, it only names it. "result=empty" is the line that proves the report's
/// "the keypad offers no interaction here" while "result=offered" rules the menu out as the cause.
/// </summary>
internal sealed class PasscodeMenuDiagPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var methods = typeof(InteractionContextHelper).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (var i = 0; i < methods.Length; i++)
        {
            var method = methods[i];
            if (method.Name != nameof(InteractionContextHelper.GetAvailableActions)) continue;
            var parameters = method.GetParameters();
            if (parameters.Length != 2) continue;
            if (parameters[1].ParameterType != typeof(PasscodeTerminal)) continue;
            return method;
        }

        throw new System.MissingMethodException(
            "EFT.InteractionContextHelper.GetAvailableActions(GamePlayerOwner, PasscodeTerminal)");
    }

    [PatchPostfix]
    public static void Postfix(AvailableInteractionState __result, PasscodeTerminal __1)
    {
        try
        {
            var actions = __result?.Actions;
            var names = new System.Text.StringBuilder();
            var count = 0;
            names.Append('[');
            if (actions != null)
            {
                for (var i = 0; i < actions.Count; i++)
                {
                    var action = actions[i];
                    if (action == null) continue;
                    if (count > 0) names.Append(',');
                    names.Append(string.IsNullOrEmpty(action.Name) ? "<unnamed>" : action.Name);
                    count++;
                }
            }

            names.Append(']');
            
        }
        catch (System.Exception)
        {
        }
    }
}

/// <summary>
/// The native keypad screen of this machine: its Show is what the interaction action of a PasscodeTerminal
/// calls, so one line here answers "did the entry screen of this report ever open on this machine, and with
/// which controller". A void prefix cannot skip or alter the call.
/// </summary>
internal sealed class PasscodePanelShowDiagPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(PasscodeInputPanel).GetMethod(nameof(PasscodeInputPanel.Show),
            BindingFlags.Instance | BindingFlags.Public, null,
            new[] { typeof(Player), typeof(PasscodeTerminal), typeof(ClientPasscodeController) }, null);
        if (target == null)
        {
            throw new System.MissingMethodException(
                "EFT.UI.PasscodeInputPanel.Show(Player, PasscodeTerminal, ClientPasscodeController)");
        }

        return target;
    }

    [PatchPrefix]
    public static void Prefix(Player __0, PasscodeTerminal __1, ClientPasscodeController __2)
    {
        try
        {
            
        }
        catch (System.Exception)
        {
        }
    }
}

/// <summary>
/// The shared entry of both native controllers (the local one and the network one Fika installs on a client)
/// and the one place that separates "the game handles this code" from "the game refused it". The prefix names
/// the state the guard decides on; the postfix - only when the game refused a code, only on a client of a bound
/// Fika raid whose host has answered this machine at least once - sends that very code to the host and reports
/// the attempt as handled: the same contract the native network path has (the panel waits for the host's answer
/// and plays its animation when it arrives). Outside that case nothing is changed.
/// </summary>
internal sealed class PasscodeSubmitRelayPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(ClientPasscodeController).GetMethod(nameof(ClientPasscodeController.SubmitPasscode),
            BindingFlags.Instance | BindingFlags.Public, null,
            new[] { typeof(Player), typeof(PasscodeTerminal), typeof(string) }, null);
        if (target == null)
        {
            throw new System.MissingMethodException(
                "EFT.Passcodes.ClientPasscodeController.SubmitPasscode(Player, PasscodeTerminal, string)");
        }

        return target;
    }

    [PatchPrefix]
    public static void Prefix(ClientPasscodeController __instance, PasscodeTerminal terminal, string passcode)
    {
        try
        {
            
        }
        catch (System.Exception)
        {
        }
    }

    [PatchPostfix]
    public static void Postfix(ClientPasscodeController __instance, Player player, PasscodeTerminal terminal,
        string passcode, ref bool __result)
    {
        try
        {
            if (__result)
            {
                // BU (analysis/icebreaker-keypad-3): when this machine's world checks codes itself (the game's
                // own local controller on the host's table), the answer is already made - and a code the native
                // check ACCEPTED is reported to the host, which repeats the same native check for its own copy
                // of the keypad and announces it to every peer. Fika transports that fact, it does not judge it.
                if (KeypadCodeSync.MatchesLocal(__instance, terminal?.PasscodeId, passcode))
                {
                    var detail = "door=" + PasscodeText.Door(terminal) + " input=" + (passcode?.Length ?? 0)
                        + " chars controller=" + PasscodeText.Controller(__instance)
                        + " settings=" + PasscodeText.Settings(__instance) + " terminals=" + PasscodeText.Terminals(__instance);
                    if (FikaBackendUtils.IsClient)
                    {
                        var synced = RaidInteractionSync.TrySyncLocalSuccess(player, terminal, passcode, out var syncReason);
                        
                    }
                    else
                    {
                        
                    }

                    return;
                }

                
                return;
            }

            // A code the local check of this machine refused is a finished decision: the relay below exists for
            // the fallback shape only (a world that checks through the host because it holds no table of its own).
            if (KeypadCodeSync.IsLocal(__instance))
            {
                
                return;
            }

            if (!RaidInteractionSync.TryRelayPasscodeAttempt(player, terminal, passcode, out var reason))
            {
                
                return;
            }

            // The host owns this attempt now: report it as handled so the native panel waits for the result event
            // instead of repeating a submit the game keeps refusing (the loop this round's log shows 13 times).
            __result = true;
            
        }
        catch (System.Exception)
        {
        }
    }
}
