using System;
using System.Collections.Generic;

namespace Fika.Core.Main.Utils;

/// <summary>
/// (analysis/transit-terminal-20261013) The two things the transit chain owes a tester: a criterion that
/// can be read <b>without</b> turning on 'Coop / Diagnostics', and a bounded recovery for a press that was
/// refused or that never left the machine.
/// <para>
/// Why this exists, measured on the 2026-10-13 co-op round: the whole chain writes its [TransitDiag] lines
/// behind <see cref="FikaDiagnostics.Probes"/> ('Coop / Diagnostics', default false), so the two runs that
/// tested the Shoreline -&gt; Terminal transit point contain <b>zero</b> transit lines - neither in
/// LogOutput.log nor in Fika-diagnostics.log, while the same file carries the ungated [ExtractDiag] lines
/// of the same session and every other probe family is absent too. The user's report ("interacted, no
/// transfer, the host showed NonAllTeammates, the client stayed on the interaction") could therefore not be
/// decided from the log, and the only remaining evidence is the code path below.
/// </para>
/// <para>
/// The rule this class applies is the one analysis/cutscene-client-dispatch already uses for the dispatch
/// chain (CutsceneDiag.Verdict): a line that decides whether a player can do something is <b>not</b> behind
/// a switch a tester did not turn on. Only state changes are written - one line per distinct verdict - so
/// the cost while nothing changes is a dictionary miss that the callers reach at most once per press or
/// per gate evaluation, never per frame.
/// </para>
/// <para>
/// One switch per behaviour, all default true, all revertible by assigning false (a config entry can be
/// bound to any of them without another edit here, exactly like TransitStartGate.RequireAllTeammatesInteracted):
/// <list type="bullet">
/// <item><see cref="ReportVerdicts"/> false - the whole criterion family becomes a no-op (the pre-round
/// behaviour: the lines exist only behind 'Coop / Diagnostics');</item>
/// <item><see cref="RearmAfterRefusal"/> false - a refusal no longer clears the local press latch of the
/// client;</item>
/// <item><see cref="ReassertPresenceOnRefusal"/> false - a refusal no longer re-asserts this client's "I am
/// in the point" report to the host;</item>
/// <item><see cref="EnsureTransferStash"/> false - the per-player transfer container (the destination of
/// the in-raid gear transfer) is no longer created for a player who reaches the point without one.</item>
/// </list>
/// </para>
/// </summary>
public static class TransitRecovery
{
    /// <summary>Clears the local press latch of a client whose press the host refused (default true).</summary>
    public static bool RearmAfterRefusal = true;

    /// <summary>Re-sends this client's zone presence once per refusal of the host (default true).</summary>
    public static bool ReassertPresenceOnRefusal = true;

    /// <summary>Creates the per-player transfer container for a player who has none (default true).</summary>
    public static bool EnsureTransferStash = true;

    /// <summary>
    /// (analysis/client-transit-terminal) The client half of the same container rule. The native local
    /// controller creates the per-player transfer container for the player it owns at the moment that
    /// player enters the point (4.0 eft40-decompiled/LocalGameTransitControllerClass.cs:194, inside
    /// OnPlayerEnter, right before the prompt is raised), and a client never runs that callback: its
    /// controller is a NetworkTransitController, which declares no PlayerEnter at all. Off restores the
    /// previous behaviour - a client only ever has the container the host published for it.
    /// </summary>
    public static bool ClientEnsureTransferStash = true;

    /// <summary>
    /// (analysis/client-transit-terminal) The client sends the interact request itself when the native
    /// press step demonstrably produced none. The press reached EnsureConfirmed, the player stands in the
    /// point, is idle and 2.5 s later neither a local registration nor a host answer exists - the state the
    /// 2026-10-13 two-player round left behind ('press-reached-no-host-request', twice, while the host
    /// transited to Terminal alone). The request is the one the native path builds (GetInteractPacket with
    /// this machine's own key and time) and the host keeps every gate it has (enabled point, access, entry
    /// requirements, zone, strict interaction rule).
    /// </summary>
    public static bool PressFallback = true;

    /// <summary>Upper bound of fallback requests per raid, so a broken native step cannot loop.</summary>
    public static int PressFallbackLimit = 8;

    /// <summary>
    /// (analysis/transit-terminal-and-countdown) The fallback above issues the request through the native exit of
    /// the press chain (<c>Player.TransitInteraction</c>, the very call <c>method_16</c>'s screen-close callback
    /// makes) instead of building and sending the packet by hand. False returns to the hand-built packet of the
    /// previous round; both send the same fields, the native call is simply the game's own step.
    /// </summary>
    public static bool PressFallbackNativeExit = true;

    /// <summary>
    /// (analysis/transit-terminal-and-countdown) The host answers a press it refuses with the answer the native
    /// protocol has for that case instead of staying silent. Without it, "the host refused" and "the request never
    /// arrived" are the same thing for the player - he stands at the point, the prompt stays up and nothing
    /// happens (the state the 2026-10-13 Shoreline -&gt; Terminal round ended in).
    /// </summary>
    public static bool NativeAnswerOnRefusal = true;

    /// <summary>
    /// (analysis/transit-terminal-and-countdown) The host asks a teammate whose press it never received to run its
    /// own press step. The answer is the native <c>TransitInteractionEvent.CheckAccess</c>: that client's own native
    /// handler resolves its access key and sends the request itself, or shows 'Transit/AccessNotGranted' when the
    /// point is not open for that player - the only native mechanism this chain has for a press that died on the
    /// client. Off restores the previous behaviour: the strict gate waits for a press that may never come.
    /// </summary>
    public static bool PressNudge = true;

    /// <summary>Seconds between two nudges of the same player and point, so a stalled press cannot loop.</summary>
    public static float PressNudgeIntervalSeconds = 5f;

    /// <summary>Upper bound of nudges per player, point and raid.</summary>
    public static int PressNudgeLimit = 4;

    /// <summary>
    /// (analysis/transit-terminal-and-countdown) The client hides its countdown surface as soon as the host
    /// announces a countdown that does not carry this machine (a paused countdown, or a point this machine is not
    /// registered for). False keeps the surface running with its last value.
    /// </summary>
    public static bool HideCountdownWithoutOwnEntry = true;

    /// <summary>
    /// (analysis/terminal-transit-mechanism) The client brings the transit countdown panel online before it
    /// drives it. 'Skip Countdown Body' is the only statement of the game that switches the in-raid HUD on,
    /// and the restore this port runs for it covers the extraction timer panel and the battle-UI screen - the
    /// transit timer panel (<c>EFT.UI.LocationTransitTimerPanel</c>, GameObject 'LocationTransitTimerPanel')
    /// is not part of it and therefore stays inactive, which makes the engine refuse the coroutines
    /// <c>Display()</c>/<c>Show(seconds)</c> start: the host's announcements arrive, are counted, and are
    /// never drawn on that machine. With this on, the panel's own object is switched on once per countdown
    /// and the drive follows; false restores the previous behaviour (the drive is skipped while the panel is
    /// inactive). Nothing is forced when the inactivity sits above the panel (a disabled canvas or HUD root).
    /// </summary>
    public static bool ActivateTransitTimerPanel = true;

    /// <summary>Idempotent reset hook of the world teardown; the criterion family it used to clear is gone.</summary>
    public static void Reset(string reason)
    {
        _ = reason;
    }

    /// <summary>Compact bracket list of a bucket; an empty bucket prints as nothing at all.</summary>
    public static string Join(List<string> bucket)
    {
        if (bucket == null || bucket.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(",", bucket);
    }
}