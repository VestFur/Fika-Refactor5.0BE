using System;
using BepInEx.Configuration;
using EFT;
using EFT.UI.Matchmaker;
using EFT.UI.Screens;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.GameMode;

/// <summary>
/// The opening countdown of the raid - the overlay that counts the deploy time down - put back while the
/// countdown body is skipped (analysis/countdown-restore).
///
/// Why it is gone today: 'Skip Countdown Body' (default ON) skips every statement of
/// <see cref="BaseGameController.CountdownScreen"/>, and the statement that shows the countdown is the first
/// one of them (statement 389, <c>new MatchmakerFinalCountdown.FinalCountdownScreenController(profile,
/// dateTime).ShowScreen(EScreenState.Root)</c>). The routine still reaches the deploy wait, so the player
/// deploys out of a screen that never counted anything down.
///
/// Why it is safe to put back now: the master switch was added because the host raid died inside the window
/// "countdown -> deploy wait" (0x80131506). Round 3 read the trail of the last of those deaths back and the
/// routine was *parked* in the deploy wait when it happened - no statement of the body was on the stack
/// (analysis/countdown-bisect-round3 section 1.2) - and the two conditions that the earlier rounds did blame
/// are gone from this path: no managed read of an engine <c>WaitForSeconds.m_Seconds</c> (the deploy wait is
/// the managed <see cref="WaitSeconds"/>), no engine coroutine started from managed code, no engine
/// reflection into an injected method. Every call this class makes is managed -> native on a *native* object
/// (the screen manager, the countdown screen controller), i.e. the legal direction.
///
/// What the restore is, in the order the player sees it:
/// <list type="number">
/// <item><description><b>1/4 - the overlay</b>: the same call statement 389 makes, so the countdown the
/// player sees is the game's own screen (it reads its remaining time from the <c>gameStartTime</c> the
/// controller is built with, which is "now + TimeBeforeDeployLocal" - exactly the value the vanilla body
/// passes).</description></item>
/// <item><description><b>the deploy wait</b>: statement 3/8, which runs with or without the master switch and
/// is therefore the clock of the overlay. This class does not add a second wait - the countdown is as long as
/// the deploy wait and no longer.</description></item>
/// <item><description><b>2/4 - the close</b>: takes the overlay down again (<c>CloseAllScreensForced</c>,
/// guarded by "is the countdown screen the current screen"). On its own switch because "the overlay could not
/// be taken down" and "the raid view was not restored" are different faults.</description></item>
/// <item><description><b>3/4 - the raid view</b>: the body of statement 4/8
/// (<see cref="BaseGameController.RestoreRaidViewAfterCountdown()"/>). It exists precisely for "the countdown
/// left its blur/effects/menu camera behind", so it is reused rather than re-derived.</description></item>
/// <item><description><b>4/4 - the re-assert</b>: the body of statement 5/8, started on the Update pump
/// (never as an engine coroutine): the native screen can apply its post-processing one frame after it closed.
/// </description></item>
/// </list>
///
/// Every step is its own config switch and its own journal label, so the next trail names the step that
/// died; the master switch of the whole restore is one line in the config file.
/// </summary>
internal static class CountdownRestore
{
    /// <summary>Journal label of the master switch: the whole opening countdown is restored.</summary>
    internal const string BoundaryPlay = "countdown restore 0/4: MASTER SWITCH 'Countdown: Play Opening Countdown' is ON - the opening countdown (the overlay and its teardown) runs while 'Skip Countdown Body' skips the body";

    /// <summary>Journal label of step 1/4: the vanilla countdown overlay is constructed and shown.</summary>
    internal const string BoundaryOverlay = "countdown restore 1/4: FinalCountdownScreenController + ShowScreen(EScreenState.Root) (the countdown overlay, the same call as statement 389)";

    /// <summary>Journal label written right after the overlay went up: the deploy wait that follows is the countdown the player sees.</summary>
    internal const string BoundaryOverlayUp = "countdown restore 1/4: the countdown overlay is ON SCREEN NOW - the deploy wait that follows is the countdown the player sees, and its length is what 'Countdown: Deploy Wait' decides";

    /// <summary>Journal label of the teardown block (steps 2/4-4/4), written after the deploy wait returned.</summary>
    internal const string BoundaryTeardown = "countdown restore: the deploy wait returned - the countdown's teardown follows (2/4 close, 3/4 raid view, 4/4 re-assert)";

    /// <summary>Journal label of step 2/4: the countdown screen is closed.</summary>
    internal const string BoundaryClose = "countdown restore 2/4: EftScreenManager.CloseAllScreensForced() while the FinalCountdown screen is the current screen";

    /// <summary>Journal label of step 3/4: the raid view the countdown left behind is restored.</summary>
    internal const string BoundaryViewRestore = "countdown restore 3/4: RestoreRaidViewAfterCountdown() - the countdown's camera/effect/screen restore (the body of statement 4/8)";

    /// <summary>Journal label of step 4/4: the post-processing re-assert routine is started on the pump.</summary>
    internal const string BoundaryReassert = "countdown restore 4/4: FikaRoutinePump.Start(RestoreRaidViewAfterCountdownRoutine) - the post-processing re-assert (the body of statement 5/8)";

    /// <summary>Journal label: the restore ran and the countdown was taken down.</summary>
    internal const string BoundaryDone = "countdown restore: done - the countdown ran and was taken down (the labels after this one are in-raid)";

    /// <summary>What the opening countdown is allowed to do in this raid. Read once per raid.</summary>
    internal readonly struct Switches
    {
        internal Switches(bool play, bool overlay, bool close, bool viewRestore, bool reassert)
        {
            Play = play;
            Overlay = overlay;
            Close = close;
            ViewRestore = viewRestore;
            Reassert = reassert;
        }

        /// <summary>Master switch: run the opening countdown at all (config: 'Countdown: Play Opening Countdown').</summary>
        internal bool Play { get; }

        /// <summary>Step 1/4: show the overlay (config: 'Countdown: Opening Countdown Overlay').</summary>
        internal bool Overlay { get; }

        /// <summary>Step 2/4: close it again (config: 'Countdown: Opening Countdown Close').</summary>
        internal bool Close { get; }

        /// <summary>Step 3/4: restore the raid view (config: 'Countdown: Opening Countdown View Restore').</summary>
        internal bool ViewRestore { get; }

        /// <summary>Step 4/4: re-assert the clear view for a bounded number of frames (config: 'Countdown: Opening Countdown Reassert').</summary>
        internal bool Reassert { get; }

        internal string Describe()
        {
            return "play=" + Play + ", 1/4 overlay=" + Overlay + ", 2/4 close=" + Close
                + ", 3/4 viewRestore=" + ViewRestore + ", 4/4 reassert=" + Reassert;
        }
    }

    /// <summary>
    /// Reads the five switches. An entry that is not bound yet - headless, or a raid that starts before the
    /// locales were loaded - counts as ON, the same convention the in-raid HUD restore uses: the countdown is
    /// what the player sees, and every switch has its own label, so an unbound read is visible in the trail
    /// rather than silent. The headless case is decided by <see cref="FikaBackendUtils.IsHeadless"/>, not by
    /// this default: a headless client has no countdown screen to show.
    /// </summary>
    internal static Switches Read()
    {
        try
        {
            var settings = FikaPlugin.Instance?.Settings;
            if (settings is null)
            {
                return Unbound();
            }

            // (analysis/config-audit-2, 2026-10-13) The four step entries were merged into the umbrella:
            // the steps are the shipped behaviour (all on) and 'Countdown: Play Opening Countdown' is the one
            // line that turns the whole opening countdown off.
            return new Switches(Statement(settings.CountdownPlayOpening), true, true, true, true);
        }
        catch (Exception)
        {
            return Unbound();
        }
    }

    /// <summary>Every step ON, see <see cref="Read"/>.</summary>
    private static Switches Unbound()
    {
        return new Switches(true, true, true, true, true);
    }

    /// <summary>A step switch: on unless the entry says otherwise (an unbound entry is not an "off").</summary>
    private static bool Statement(ConfigEntry<bool> entry)
    {
        try
        {
            return entry is null || entry.Value;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Step 1/4: shows the countdown overlay, i.e. the statement the master switch skips. Returns true when the
    /// overlay is up, which is also the condition for the teardown after the deploy wait ("nothing to take
    /// down" is not the same statement as "the overlay could not be shown", and the trail says which one it
    /// was).
    ///
    /// The call is the one the vanilla body makes, with the same arguments: the active profile and the
    /// game-start time the routine already computed as "now + TimeBeforeDeployLocal".
    /// </summary>
    internal static bool ShowIfEnabled(Switches switches, Profile profile, Il2CppSystem.DateTime gameStartTime)
    {
        if (FikaBackendUtils.IsHeadless)
        {
            return false;
        }

        if (!switches.Play)
        {
            return false;
        }



        if (!switches.Overlay)
        {
            return false;
        }

        if (profile is null)
        {
            
            return false;
        }

        if (!FikaBoundaries.Run(BoundaryOverlay, () => Show(profile, gameStartTime)))
        {
            // The label was written before the call and there is no RETURNED line after it: the [FAIL]
            // [Boundary] line above names the exception. A fail fast cannot be caught at all - then the trail
            // simply ends on this label, which is what makes the step readable.
            return false;
        }

        
        return true;
    }

    /// <summary>Step 2/4: takes the overlay down again. Runs only when step 1/4 really showed it.</summary>
    internal static void CloseIfEnabled(Switches switches)
    {

        if (!switches.Close)
        {
            return;
        }

        if (FikaBoundaries.Run(BoundaryClose, CloseOverlay))
        {
        }
    }

    /// <summary>
    /// The countdown screen is closed the way the vanilla post-countdown path closes it, and only when it really
    /// is the current screen: the native screen may have closed itself the moment its own clock reached the
    /// game-start time ("ShowControls" style screens do), in which case there is nothing to do and the report
    /// line says so. The player-owner re-ready that belongs to this pairing in the vanilla tail is part of step
    /// 3/4, which calls the same statement 4/8 the body calls.
    /// </summary>
    private static void CloseOverlay()
    {
        var screens = EftScreenManager.Instance;
        if (screens is null)
        {
            
            return;
        }

        if (screens.CheckCurrentScreen(EEftScreenType.FinalCountdown))
        {
            screens.CloseAllScreensForced();
            
            return;
        }

        
    }

    /// <summary>
    /// The overlay: the exact call statement 389 makes (kept as one expression so the two call sites cannot
    /// drift apart). The controller reads its remaining time from the game-start time it is built with, so the
    /// overlay counts the deploy time down by itself; this port only decides when it is shown and taken down.
    /// </summary>
    private static void Show(Profile profile, Il2CppSystem.DateTime gameStartTime)
    {
        new MatchmakerFinalCountdown.FinalCountdownScreenController(profile, gameStartTime).ShowScreen(EScreenState.Root);
    }
}
