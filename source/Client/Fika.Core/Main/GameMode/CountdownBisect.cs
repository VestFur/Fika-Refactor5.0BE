using System;
using BepInEx.Configuration;

namespace Fika.Core.Main.GameMode;

/// <summary>
/// The bisect switches of the raid-start countdown
/// (<see cref="BaseGameController.CountdownScreen"/>).
///
/// Round 1 (analysis/countdown-bisect) put the three cosmetic statements behind a switch and shipped them
/// off. The 11:17 run proved that this is necessary but not sufficient: with
/// screen=False, audioFades=False, menuMusic=False the body was still entered and the process still died a
/// few seconds later, and the journal could not name the statement either - the trail had gone stale
/// (analysis/countdown-bisect-round2, Fika-crash.log).
///
/// Round 2 therefore bisects the *kept* statements as well:
///   * each of the eight remaining statements has its own journal label and its own switch, all shipped
///     ON; the label is written to the crash trail immediately (one WriteFile on the calling thread), so
///     the next run's trail names the statement even when the trail's persistor thread never writes again;
///   * a master switch - 'Skip Countdown Body', shipped ON - skips the body entirely (cosmetic and kept
///     statements alike; it jumps straight to the deploy wait and returns), which gives a raid that starts no matter which statement is the
///     aborter. The re-enable order is documented in analysis/countdown-bisect-round2/REPORT.md.
///
/// Suspect order after the 11:17 run (the death came seconds after the body was entered, i.e. while the
/// deploy wait was still pending, so nothing in the body was on the stack): 1. TimerPanel.ProfileId (a
/// managed call into the native ExtractionTimersPanel setter), 2. GameUi.gameObject.SetActive(true)
/// (activates a native hierarchy, i.e. every component's OnEnable), 3. the VOIP block
/// (FixVOIPAudioDevice -> Dissonance natives), then the pump start of the raid-view routine,
/// RestoreRaidViewAfterCountdown itself, SyncTransitControllers and the raid-started event. The master
/// switch exists so this order does not have to be guessed before the raid can be played.
/// </summary>
internal static class CountdownBisect
{
    /// <summary>Journal label: the countdown body started, i.e. the loading screen was hidden.</summary>
    internal const string BoundaryBody = "countdown: body entered (the loading screen was hidden)";

    /// <summary>Journal label: the body finished (the deploy wait returned and every allowed statement ran).</summary>
    internal const string BoundaryBodyDone = "countdown: body done (the deploy wait returned; the labels after this one are post-countdown)";

    /// <summary>Journal label of the master switch: the body skips every statement it has.</summary>
    internal const string BoundarySkipBody = "countdown stmt 0/8: MASTER SWITCH 'Skip Countdown Body' is ON - the whole body is skipped (cosmetic and kept statements alike) and only the deploy wait runs";

    /// <summary>
    /// Journal label of round 3's second master switch: the whole post-wait block is skipped, so the raid
    /// reaches the session sequence (step 3/3) without running any of statements 4/8-8/8.
    /// </summary>
    internal const string BoundaryPostWait = "countdown post-wait block (stmts 4/8-8/8: restore raid view, raid view routine, transit sync, raid-started event, VOIP)";

    /// <summary>Journal label: the routine yields into the deploy wait (the pump parks it here).</summary>
    internal const string BoundaryDeployWaitYield = "countdown 399: yielding into the deploy wait NOW - the pump parks this routine, nothing of it runs until it is resumed";

    /// <summary>Journal label: the deploy wait returned - the first line that runs after the wait.</summary>
    internal const string BoundaryDeployWaitResumed = "countdown 399: the deploy wait RETURNED - the routine resumed (the next label is the first statement after the wait)";

    // ------------------------------------------------------------------------------------------------
    // Round 3 - step 1/3 of the session sequence (FinishRaidSetup).
    // The 13:45 run froze the frame counter for 3.1 s inside step 1/3: the pump wrote its tick label for
    // frame 17849 and then the main thread wrote nothing at all for 3.1 s, so the frame the process was in
    // is bracketed but not named. These label the statements of that routine exactly like the countdown
    // body is labelled - a label before the call, RETURNED after it - so the next trail names the window.
    // Diagnostics only: none of them has a switch and none of them changes behaviour.
    // ------------------------------------------------------------------------------------------------

    /// <summary>Journal label: step 1/3 of the session sequence is entered.</summary>
    internal const string BoundaryFinishSetup = "FinishRaidSetup (session sequence step 1/3): entered";

    /// <summary>Journal label: the loading screen broadcast of step 1/3.</summary>
    internal const string BoundaryFinishLoading90 = "FinishRaidSetup: LoadingScreenUI.UpdateAndBroadcast(90f)";

    /// <summary>Journal label: the music fade and its per-frame wait.</summary>
    internal const string BoundaryFinishMusicWait = "FinishRaidSetup: FadeBackgroundMusicAsync + wait for the music task";

    /// <summary>Journal label: the audio buffer reset.</summary>
    internal const string BoundaryFinishAudioBuffer = "FinishRaidSetup: AudioUtils.ResetAudioBuffer()";

    /// <summary>Journal label: the triggers module is added to the game object.</summary>
    internal const string BoundaryFinishTriggers = "FinishRaidSetup: AddComponent<LocalFikaTriggersModule>()";

    /// <summary>Journal label: the lamp controllers are filled.</summary>
    internal const string BoundaryFinishLamps = "FinishRaidSetup: _gameWorld.FillLampControllers()";

    /// <summary>Journal label: the seasons controller runs and its task is awaited.</summary>
    internal const string BoundaryFinishSeasonRun = "FinishRaidSetup: SeasonsController.Run + wait for the season task";

    /// <summary>Journal label: the radio broadcast is started.</summary>
    internal const string BoundaryFinishBroadcast = "FinishRaidSetup: RadioBroadcastController.StartBroadcast()";


    /// <summary>Journal label of countdown line 389: the final-countdown overlay (cosmetic).</summary>
    internal const string BoundaryScreen = "countdown 389: FinalCountdownScreenController + ShowScreen (cosmetic)";

    /// <summary>Journal label of countdown lines 390-394: the audio fades (cosmetic).</summary>
    internal const string BoundaryAudioFades = "countdown 390-394: AmbientAudioSystem.Initialize + BetterAudio.FadeInVolumeBeforeRaid (cosmetic)";

    /// <summary>Journal label of countdown lines 395-396: the menu-music calls (cosmetic).</summary>
    internal const string BoundaryMenuMusic = "countdown 395-396: GUISounds.BackgroundMusicActive + StopMenuBackgroundMusicWithDelay (cosmetic)";

    /// <summary>Journal label of kept statement 1/8: the game-UI switch (native hierarchy activation).</summary>
    internal const string BoundaryGameUi = "countdown 397 (kept stmt 1/8): _abstractGame.GameUi.gameObject.SetActive(true)";

    /// <summary>Journal label of kept statement 2/8: the extraction-timer panel's profile binding.</summary>
    internal const string BoundaryTimerPanel = "countdown 398 (kept stmt 2/8): _abstractGame.GameUi.TimerPanel.ProfileId = profileId";

    /// <summary>Journal label of kept statement 3/8: the deploy wait.</summary>
    internal const string BoundaryDeployWait = "countdown 399 (kept stmt 3/8): WaitForSeconds(timeBeforeDeployLocal)";

    /// <summary>Journal label of kept statement 4/8: the raid-view restore (native camera and screen work).</summary>
    internal const string BoundaryRestoreRaidView = "countdown post-wait (kept stmt 4/8): RestoreRaidViewAfterCountdown()";

    /// <summary>Journal label of kept statement 5/8: the pump start of the raid-view routine.</summary>
    internal const string BoundaryRestoreRoutine = "countdown post-wait (kept stmt 5/8): FikaRoutinePump.Start(RestoreRaidViewAfterCountdownRoutine)";

    /// <summary>Journal label of kept statement 6/8: the transit-controller sync.</summary>
    internal const string BoundarySyncTransits = "countdown post-wait (kept stmt 6/8): SyncTransitControllers(profileId)";

    /// <summary>Journal label of kept statement 7/8: the raid-started event.</summary>
    internal const string BoundaryRaidStartedEvent = "countdown post-wait (kept stmt 7/8): FikaEventDispatcher.DispatchEvent(FikaRaidStartedEvent)";

    /// <summary>Journal label of kept statement 8/8: the VOIP device fix.</summary>
    internal const string BoundaryVoipFix = "countdown post-wait (kept stmt 8/8): AllowVOIP -> FikaRoutinePump.Start(FixVOIPAudioDevice)";

    // ------------------------------------------------------------------------------------------------
    // In-raid HUD restore (analysis/inraid-ui).
    //
    // The master switches above ship ON, so statements 1/8 (GameUi.gameObject.SetActive(true)) and 2/8
    // (TimerPanel.ProfileId) - the two statements that switch the raid HUD hierarchy on - never run in a
    // default raid. The whole HUD hierarchy then stays inactive for the entire raid: the timer key does
    // nothing, the extraction list and the extraction countdown never appear, the game's own HUD code gets
    // its coroutine starts refused by Unity on the closed panels ("Coroutine couldn't be started because
    // the the game object 'TimerPanel' is inactive!", analysis/red-errors-coroutines), and the panel's
    // spawn-time SetTime is skipped by the drive guard (analysis/ui-log-noise) - so nothing ever fills the
    // panel either.
    //
    // These two labels bracket the restore that runs after the deploy wait, in a pump routine of its own
    // (BaseGameController.RestoreInRaidHud), so the trail says whether the HUD was switched back on and
    // whether the extraction list was re-pushed into it. Diagnostics only: the switches that decide it are
    // the master switches and the 'Countdown: Restore In-Raid HUD' entry.
    // ------------------------------------------------------------------------------------------------

    /// <summary>Journal label: the post-countdown HUD restore routine was started on the pump.</summary>
    internal const string BoundaryHudRestore = "countdown post-wait: in-raid HUD restore routine started (analysis/inraid-ui) - GameUi activation + extraction timer panel";

    /// <summary>Journal label: the HUD restore routine wrote its final state and returns.</summary>
    internal const string BoundaryHudRestoreDone = "countdown post-wait: in-raid HUD restore done (the next labels are in-raid)";

    /// <summary>What the countdown body is allowed to do in this raid. Read once per raid.</summary>
    internal readonly struct Switches
    {
        internal Switches(bool screen, bool audioFades, bool menuMusic, bool skipBody, bool skipPostWait,
            bool gameUi, bool timerPanel, bool deployWait, bool restoreRaidView, bool restoreRoutine,
            bool syncTransits, bool raidStartedEvent, bool voipFix)
        {
            Screen = screen;
            AudioFades = audioFades;
            MenuMusic = menuMusic;
            SkipBody = skipBody;
            SkipPostWait = skipPostWait;
            GameUi = gameUi;
            TimerPanel = timerPanel;
            DeployWait = deployWait;
            RestoreRaidView = restoreRaidView;
            RestoreRoutine = restoreRoutine;
            SyncTransits = syncTransits;
            RaidStartedEvent = raidStartedEvent;
            VoipFix = voipFix;
        }

        /// <summary>Show the final-countdown overlay (config: 'Show Final Countdown Screen').</summary>
        internal bool Screen { get; }

        /// <summary>Run the ambient/BetterAudio fades (config: 'Raid Start Audio Fades').</summary>
        internal bool AudioFades { get; }

        /// <summary>Stop the menu background music (config: 'Raid Start Menu Music').</summary>
        internal bool MenuMusic { get; }

        /// <summary>Master switch: skip the whole body (config: 'Skip Countdown Body').</summary>
        internal bool SkipBody { get; }

        /// <summary>
        /// Round 3 master switch of the post-wait block: skip statements 4/8-8/8 as a group
        /// (config: 'Countdown: Skip Post Wait Block' = 'Skip Post Wait Block').
        /// </summary>
        internal bool SkipPostWait { get; }

        /// <summary>Statement 1/8: activate the game UI (config: 'Countdown: Activate Game UI').</summary>
        internal bool GameUi { get; }

        /// <summary>Statement 2/8: bind the extraction-timer panel (config: 'Countdown: Bind Extraction Timer Panel').</summary>
        internal bool TimerPanel { get; }

        /// <summary>Statement 3/8: wait the deploy time (config: 'Countdown: Deploy Wait').</summary>
        internal bool DeployWait { get; }

        /// <summary>Statement 4/8: restore the raid view (config: 'Countdown: Restore Raid View').</summary>
        internal bool RestoreRaidView { get; }

        /// <summary>Statement 5/8: start the raid-view routine (config: 'Countdown: Start Raid View Routine').</summary>
        internal bool RestoreRoutine { get; }

        /// <summary>Statement 6/8: sync the transit controllers (config: 'Countdown: Sync Transit Controllers').</summary>
        internal bool SyncTransits { get; }

        /// <summary>Statement 7/8: dispatch the raid-started event (config: 'Countdown: Dispatch Raid Started Event').</summary>
        internal bool RaidStartedEvent { get; }

        /// <summary>Statement 8/8: run the VOIP audio fix (config: 'Countdown: VOIP Audio Fix').</summary>
        internal bool VoipFix { get; }

        internal string Describe()
        {
            return "screen=" + Screen + ", audioFades=" + AudioFades + ", menuMusic=" + MenuMusic
                + ", skipBody=" + SkipBody + ", skipPostWait=" + SkipPostWait
                + ", stmt=[1/8 gameUi=" + GameUi + ", 2/8 timerPanel=" + TimerPanel
                + ", 3/8 deployWait=" + DeployWait + ", 4/8 restoreRaidView=" + RestoreRaidView
                + ", 5/8 restoreRoutine=" + RestoreRoutine + ", 6/8 syncTransits=" + SyncTransits
                + ", 7/8 raidStartedEvent=" + RaidStartedEvent + ", 8/8 voipFix=" + VoipFix + "]";
        }
    }

    /// <summary>
    /// Reads the two master switches ('Skip Countdown Body', 'Skip Post Wait Block'); the cosmetics and the
    /// eight kept statements are the constants <see cref="Read"/> documents (analysis/config-audit-2). Both
    /// masters default to ON when the config is not bound yet (headless, or a raid started before the locales
    /// were loaded): a skipped statement cannot break a raid - the body then touches no native object at all -
    /// while a running one can. That default is also what the shipped configuration uses.
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

            // (analysis/config-audit-2, 2026-10-13) The eleven per-statement/per-cosmetic entries were
            // retired: with both master switches ON (the shipped configuration) none of those values is
            // reachable, and the deaths the bisect hunted were raised while this routine was parked in the
            // deploy wait (analysis/countdown-bisect-round3 section 1.2), not by a statement of the body. The
            // constants below are the shipped defaults of the retired entries: the three cosmetics stay off
            // (a running cosmetic statement is what the master switch exists to avoid) and the kept statements
            // stay on, so turning a master switch off again restores the round-3 behaviour exactly.
            // (2026-10-14, diagnostics removal) The two bisect master switches went with the rest of the
            // port diagnostics: the shipped configuration had both of them ON, i.e. the ported countdown body
            // and its post-wait block are skipped and the countdown the player sees is played by
            // CountdownRestore. The unreachable statement paths stay in BaseGameController for reference.
            _ = settings;
            return new Switches(false, false, false, true, true,
                true, true, true, true, true, true, true, true);
        }
        catch (Exception)
        {
            return Unbound();
        }
    }

    /// <summary>
    /// cosmetics off, both master switches on (skip the body and the post-wait block), statements on -
    /// see <see cref="Read"/>. The two masters stay on because a skipped statement cannot break a raid.
    /// </summary>
    private static Switches Unbound()
    {
        return new Switches(false, false, false, true, true, true, true, true, true, true, true, true, true);
    }

    /// <summary>A statement switch: on unless the entry says otherwise (an unbound entry is not an "off").</summary>
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
}
