using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CommonAssets.Scripts.Cutscenes;
using EFT;
using EFT.GlobalEvents;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Patches.World;
using Fika.Core.Main.Utils;
using UnityEngine;
using UnityEngine.Playables;

namespace Fika.Core.Main.Components;

/// <summary>
/// ICEBREAKER-CUTSCENE-BRIDGE (analysis/icebreaker-cutscene).
///
/// The native cutscene controllers are created together with the world, but on a Fika raid their
/// <c>_timelinesMapBank</c> stays null for the whole raid: the round-12 host log shows
/// <c>[CutsceneDiag] ... the cutscene server controller has no timeline bank yet</c> while the map's own
/// <see cref="MapTimelinesBank"/> exists and publishes 'Icebreaker_1' - SPT's native side even hid that rig in the
/// same second. With a null bank the host controller refuses every <c>StartCutscene</c> (so the pier cutscene,
/// and with it the story doors opened by its actions, never runs) and the client controller cannot decode a
/// timeline at all.
///
/// This class is the ONE place that repairs that chain, in three steps:
/// <list type="number">
/// <item><see cref="EnsureControllers"/>, called from the world creation chain (<see cref="FikaHostWorld.Create"/> /
/// <see cref="FikaClientWorld.Create"/>), creates a cutscene controller the world does not have yet. Both
/// controllers are plain objects that the native <c>GameWorld.Create&lt;T&gt;</c> body hands to the world, and
/// that body is exactly what this port replaces - which is why a Fika raid has neither of them
/// (analysis/cutscene-world-factory). An existing controller is never replaced.</item>
/// <item><see cref="Tick"/> hands the map's <see cref="MapTimelinesBank"/> to the controllers (native public
/// setter, no native method is called) once the world is ready and the bank object exists. An existing
/// (non-null) link is never overwritten.</item>
/// <item><see cref="Tick"/> also runs the playback repair of the host
/// (<see cref="TryRepairHostPlayback"/>): a process that was created for this machine's own player is handed
/// to the local controller if nothing else does, a process whose clock nothing advances is ticked through the
/// native <c>CutscenesServerController.OnUpdate</c>, and a process that stalls after this machine's viewer
/// finished is ended through the native <c>EndCutscene</c> - which is what runs the timeline's end actions
/// (<c>CutsceneActionOpenDoors</c>) and opens the story gate of the map
/// (analysis/cutscene-play-and-end).</item>
/// <item><see cref="PumpLocalPlayback"/> drives the <b>second</b> controller Fika replaces the per-frame
/// caller of (analysis/cutscene-view-and-end). The round-16 host log shows a process that plays forever:
/// <c>viewed=0/1</c> while <c>afterStartTimeElapsed</c> grew to 494 s. The viewed counter is only written by
/// the native <c>PlayerViewCutsceneComplete</c>, whose local trigger is <c>TimelineShowed</c> - and that
/// method never ran (its own patch writes a <c>phase=view</c> line at its first instruction and no such line
/// exists in any log). A timeline only reaches that point when the client controller's per-frame
/// <c>OnUpdate</c> first consumes the start request the <c>CutSceneStartEvent</c> handler left behind; with no
/// caller the scene is never started either, which is why the timeline rig stays hidden and the player sees
/// nothing at all. The bridge therefore ticks the local controller while this machine owns a process whose
/// scene makes no progress, and reports a viewer that never finished.</item>
/// <item>A viewer that has not finished is reported through the native
/// <c>CutscenesServerController.PlayerViewCutsceneComplete</c> - the same call the host runs for a viewer's
/// network acknowledgement - as soon as the timeline is over, or after
/// <see cref="ViewerAbandonSeconds"/> when nothing was ever played. A process the native side still keeps
/// alive <see cref="PostCompleteGraceSeconds"/> later is ended through the native <c>EndCutscene</c> with
/// <c>skip: true</c>, the call the skip vote uses, so the end actions of the timeline (the pier gate of
/// <c>CutsceneActionOpenDoors</c>) run instead of leaving the player in a cutscene camera for the rest of
/// the raid.</item>
/// <item>The prompt of that playback (<c>EFT.UI.CutsceneSkipUI</c>) is watched and, once this machine owns no
/// process any more, taken down through its native hiding method (<see cref="PumpSkipOverlay"/>) - the raid of
/// 2026-10-13 00:4x played the scene through and still showed "0/1 players want to skip", because the process
/// is torn down inside the completion branch this port replaced
/// (analysis/cutscene-skip-overlay).</item>
/// </list>
/// </summary>
internal static class CutsceneBoundary
{
    /// <summary>
    /// One switch for the whole repair: set to false and rebuild to return to the exact pre-bridge behaviour
    /// (nothing was created or disposed, no game data was touched - see REPORT, section "一键回退").
    /// </summary>
    internal const bool Enabled = true;

    /// <summary>
    /// Kept as the rollback of the world-factory creation (analysis/cutscene-world-factory). The two cutscene
    /// controllers used to be created inside the world creation chain of this port; since
    /// analysis/cutscene-native-registration they are created at the native position instead (the raid start
    /// coroutine, <see cref="CreateCutsceneControllers"/>, called from <c>CoopGame.InitPlayer</c>) - the same
    /// place the native build creates the controllers of this family. <c>false</c> (default) = the world
    /// factory only reports and never creates; one edit to <c>true</c> restores the previous, too early
    /// creation and disables nothing else.
    /// </summary>
    internal const bool CreateControllerIfMissing = false;

    /// <summary>
    /// One switch for the registration half of analysis/cutscene-native-registration: the two controllers are
    /// handed the first per-frame caller of the native build right after they are created
    /// (<see cref="TickHookServer"/> / <see cref="TickHookClient"/>). This is the step that turns
    /// <c>[CutsceneDiag] phase=gate:ticks ... engine=0</c> into <c>engine&gt;0</c> - before it, both controllers
    /// were plain objects nobody entered, which is why the bridge had to tick them itself
    /// (<c>bridge=&gt;0</c> in the round-17 solo log).
    ///
    /// It is a <c>static bool</c> and not a <c>const</c> on purpose, exactly like
    /// <see cref="ProbeControllerTicks"/>: its only use is a guard whose body would be unreachable code
    /// (CS0162) if the compiler could fold the switch away. Reverting is the same single edit - the config key
    /// <c>Coop / Cutscene: Native Tick Hook</c> writes it, default <c>true</c>.
    /// </summary>
    internal static bool NativeTickHook = true;

    /// <summary>The hook the server controller is registered on, for the <c>phase=tick-hook</c> line.</summary>
    internal const string TickHookServer = "BaseLocalGame.UpdateByUnity (HostGameController.add_UpdateByUnity)";

    /// <summary>The hook the client controller is registered on, for the <c>phase=tick-hook</c> line.</summary>
    internal const string TickHookClient = "GameWorld.OnLateUpdate (GameWorld.LateUpdateWorld)";

    /// <summary>
    /// One switch for the playback repair (analysis/cutscene-play-and-end): with <c>false</c> the bridge only
    /// links the bank, exactly as before this round - no scene is dispatched to this machine's own viewer and
    /// no process is advanced or ended by the bridge.
    ///
    /// It is a <c>static bool</c> and not a <c>const</c> since analysis/cutscene-native-registration: the config
    /// key <c>Coop / Cutscene: Repair Bridge Playback</c> writes it, and it is the one switch the "native tick
    /// hook" proof run turns off (with <see cref="RepairLocalPlayback"/>) to show that the engine - and not the
    /// bridge - ticks the controllers. Default stays <c>true</c>.
    /// </summary>
    internal static bool RepairHostPlayback = true;

    /// <summary>
    /// The last resort of the repair: a process that is still frozen long after this machine's own viewer
    /// finished watching is ended through the native <c>EndCutscene</c>, so the timeline's end actions (the
    /// pier gate of <c>CutsceneActionOpenDoors</c>) run instead of leaving the player in a cutscene camera.
    /// <c>false</c> = a stalled process is only reported.
    /// </summary>
    internal const bool ForceEndStalledProcess = true;

    /// <summary>
    /// One switch for the client-side half of the repair (analysis/cutscene-view-and-end): the bridge ticks
    /// <c>CutscenesClientController.OnUpdate</c> while this machine owns a process whose scene makes no
    /// progress, reports this machine's viewer as having watched a scene that is over, and ends a process the
    /// native side keeps alive afterwards. <c>false</c> = the local controller is only watched and reported,
    /// nothing is ticked, reported or ended from here.
    ///
    /// Like <see cref="RepairHostPlayback"/> it is a <c>static bool</c> since
    /// analysis/cutscene-native-registration, written by <c>Coop / Cutscene: Repair Bridge Playback</c>. Default
    /// stays <c>true</c>.
    /// </summary>
    internal static bool RepairLocalPlayback = true;

    /// <summary>
    /// One switch for the second half of the client-side repair (analysis/cutscene-rig-play): when the scene of
    /// this machine's own process stands still at time 0, the bridge enters the native timeline itself - first
    /// through the controller's own playback entry point, then through the signal-track setup and the director's
    /// <c>Play()</c> the native entry point uses, and finally by switching the timeline rig on, which is the one
    /// object SPT's patches keep off until a timeline starts. <c>false</c> = the scene is only watched, ticked
    /// and reported, exactly as before this round.
    /// </summary>
    internal static bool RescueLocalScene = true;

    /// <summary>
    /// (analysis/cutscene-client-dispatch) The client half of the dispatch repair: when a host
    /// <c>CutsceneStartPacket</c> arrives for a process whose seat table does not list this machine, the bridge adds
    /// its own seat to the player array of the native <c>CutSceneStartEvent</c> it raises. The seat table is built
    /// on the host from a local player list (SPTushonka's trigger handler uses
    /// <c>GameWorld.AllAlivePlayersList</c>), the native handler of the receiving machine filters against it, and
    /// nothing else the host sends can make an unseated peer play - so this is the one repair of that branch that
    /// can be decided and verified on the client side alone (the host's log is out of reach). With <c>false</c> the
    /// packet is passed through exactly as it arrived and the <c>phase=dispatch ... addressed=no</c> line is the
    /// only trace. Written by the config key 'Coop / Cutscene: Join Cutscenes Started By The Host'.
    /// </summary>
    internal static bool JoinSharedCutscene = true;

    /// <summary>
    /// (analysis/cutscene-client-dispatch) The host-side counterpart of <see cref="JoinSharedCutscene"/>: a peer
    /// that sends a cutscene signal (a skip intention, a finished timeline) is demonstrably inside the scene, so
    /// the host accepts it as a viewer even when its own seat table did not list it - instead of dropping the
    /// signal and holding the process open for a viewer that can never report. With <c>false</c> such a signal is
    /// dropped exactly as before. Written by the config key 'Coop / Cutscene: Accept Signals From Unlisted Viewers'.
    /// </summary>
    internal static bool AcceptUnlistedViewerCompletion = true;

    /// <summary>
    /// (analysis/cutscene-skip-overlay) One switch for the skip prompt that outlives its cutscene. The prompt
    /// the raid of 2026-10-13 00:4x left on screen ("0/1 players want to skip" / "hold ESC to skip") is
    /// <c>EFT.UI.CutsceneSkipUI</c>: shown by <c>CutscenesClientController.ShowCutsceneSkipUI</c>, taken down
    /// by <c>HideCutsceneSkipUI(bool notifyServer)</c> again, and mirrored in the controller's
    /// <c>_skipUiActive</c>. With <c>true</c> (default) the bridge performs the call the native teardown of a
    /// process owes - that one hiding method - as soon as this machine owns no process any more, and reports it
    /// as <c>[CutsceneDiag] phase=overlay ... -> cleared=yes|no</c>. <c>false</c> = the prompt is only reported,
    /// never touched.
    ///
    /// It is a <c>static bool</c> and not a <c>const</c> on purpose, exactly like
    /// <see cref="ProbeControllerTicks"/>: its only use is a guard whose body would be unreachable code
    /// (CS0162) if the compiler could fold the switch away. Reverting is the same single edit - the config key
    /// <c>Coop / Cutscene: Dismiss Skip Overlay</c> writes it, default <c>true</c>.
    /// </summary>
    internal static bool DismissSkipOverlay = true;

    /// <summary>
    /// (analysis/cutscene-rig-play) One switch for the native-per-frame path: with <c>true</c> the ported world
    /// classes (<c>FikaClientGameWorld</c>, <c>FikaHostGameWorld</c>) forward their empty
    /// <c>AfterPlayerTick(float)</c> override to the native base implementation of <c>ClientLocalGameWorld</c>
    /// instead of swallowing it. That per-frame step is the native caller of the cutscene controllers: the
    /// round-17 solo log shows <c>gate:ticks ... engine=0 bridge=820</c>, i.e. nobody but the bridge entered
    /// either controller, while the scene stayed at 0.0 s and its rig off. Default <c>false</c>: the native body
    /// also performs work this port deliberately moved into Fika (airdrop points, synchronizable objects, the
    /// camera/UI chain), so it is switched on as one deliberate experiment, not as a silent behaviour change.
    /// <c>phase=gate:ticks</c> and the one-time <c>phase=gate:ticks ... result=engine-first|mixed</c> lines are
    /// the judgement: engine counts above zero mean the engine's chain is back.
    /// It is a <c>static readonly</c> and not a <c>const</c> on purpose, exactly like
    /// <see cref="ProbeControllerTicks"/>: its use is a guard whose body would be unreachable code (CS0162) if
    /// the compiler could fold the switch away.
    /// </summary>
    internal static bool ForwardAfterPlayerTick = false;

    /// <summary>
    /// (analysis/cutscene-slowmo) One switch for <see cref="TickDelta"/>: with <c>true</c> (default) a scaled game
    /// clock is repaired to real time at every call site of this bridge (the two native tick hooks and the two
    /// pumps that advance a controller), with <c>false</c> every call site feeds exactly the delta it fed before
    /// this round - the native frame delta - which is the pre-change behaviour in one assignment.
    /// </summary>
    internal static bool RealTimeTicks = true;

    /// <summary>Seconds between two samples of the sync probe (one line per second while a cutscene is on).</summary>
    private const float SyncSampleSeconds = 1f;

    /// <summary>How far a measured rate may be from 1.00 before the line judges it slow or fast.</summary>
    private const float SyncTolerance = 0.1f;

    /// <summary>Seconds between adoption attempts while the bank object is not resolvable yet.</summary>
    private const float RetrySeconds = 1f;

    /// <summary>Seconds between the (expensive) scene-wide scans for an inactive bank object.</summary>
    private const float FallbackScanSeconds = 10f;

    /// <summary>Seconds between the playback repair's samples of the native process table.</summary>
    private const float PlaybackIntervalSeconds = 0.25f;

    /// <summary>Seconds between the (scene scanning) state lines.</summary>
    private const float DiagnoseIntervalSeconds = 1f;

    /// <summary>How long the engine may take to hand a fresh process to this machine's own viewer.</summary>
    private const float LocalStartGraceSeconds = 0.5f;

    /// <summary>
    /// How long this machine's controller may hold a cutscene start it never consumed before the bridge ticks
    /// it (analysis/rally-gate-deadlock). A start event only sets <c>_needStartTimeline</c>, and the
    /// controller's own per-frame <c>OnUpdate</c> is the only thing that turns it into a scene - the state a
    /// Fika raid has no caller for.
    /// </summary>
    private const float LocalPendingStartGraceSeconds = 1f;

    /// <summary>How long <c>CutsceneProcess.afterStartTimeElapsed</c> may stand still before the bridge ticks it.</summary>
    private const float FrozenSeconds = 2f;

    /// <summary>How long a finished but unadvanced process may stay before the bridge ends it.</summary>
    private const float StalledSeconds = 25f;

    /// <summary>How long this machine's own scene may stay without any progress before the bridge ticks it.</summary>
    private const float LocalPlaybackGraceSeconds = 2f;

    /// <summary>Seconds between the samples of the local scene (a director lookup crosses the interop boundary).</summary>
    private const float LocalSampleSeconds = 0.25f;

    /// <summary>
    /// How long after a played timeline may a completion still be missing before the bridge treats the scene as
    /// finished but unreported (analysis/cutscene-view-and-end).
    /// </summary>
    private const float TimelineStoppedGraceSeconds = 3f;

    /// <summary>
    /// How long a viewer that never saw a single frame is waited for before the bridge reports it as having
    /// watched and ends the process: the scene is not playing on this machine, so there is nothing to watch and
    /// the alternative is a player locked in a cutscene camera with the story gate of the map still shut.
    /// 30 s is the configurable value this repair was asked for; it never applies while a scene is playing.
    /// </summary>
    internal const float ViewerAbandonSeconds = 30f;

    /// <summary>How long the native side may take to end a process whose viewers are complete.</summary>
    private const float PostCompleteGraceSeconds = 3f;

    /// <summary>How long after the timeline's own duration the completion of a viewer is still expected.</summary>
    private const float TimelineSlackSeconds = 2f;

    /// <summary>How long the tick counters are accumulated before they report themselves.</summary>
    private const float TickReportSeconds = 5f;

    // The scene rescue (analysis/cutscene-rig-play). The round-17 solo log is the shape of the failure it
    // repairs: the native controller was handed the process (local=playing#1) and its own per-frame step was
    // driven by the bridge for 118 s, but the director stayed at "Paused:0.0/29.2" and the rig of the timeline
    // at 0/1 - so the timeline never started, the rig SPT had hidden was never switched back on, no frame was
    // rendered, the viewer counter could not fill and the player stayed in the cutscene camera. The rescue
    // runs only while this machine's own scene has not advanced a single frame.

    /// <summary>How long the scene may stay untouched after the process arrived before the bridge enters it.</summary>
    private const float SceneRescueGraceSeconds = 1f;

    /// <summary>
    /// (analysis/cutscene-slowmo) How long the controller's own start counts as 「in flight」: while
    /// <c>_needStartTimeline</c> or <c>_waitFramesLeft</c> is still set, the native chain of this machine is
    /// between the start event and the timeline, and the bridge must not enter the same scene through
    /// <c>StartShowTimeline</c> - a second entry rebuilds the graph and starts the audio a second time, which is
    /// the audio/picture split this report is about. The native wait itself is
    /// <c>_waitFramesLeft = 50</c> frames (about 0.8 s at 60 fps); the window is three times that, so a start
    /// that is genuinely running is never raced, and a start that never lands still gets the rescue.
    /// </summary>
    private const float NativeStartWindowSeconds = 3f;

    /// <summary>Seconds between the rescue's steps while the scene still does not play.</summary>
    private const float SceneRescueRetrySeconds = 2f;

    /// <summary>How often the rescue is attempted before it stops (the scene may not be enterable at all).</summary>
    private const int SceneRescueMaxAttempts = 5;

    /// <summary>How long a stop of the local scene is waited for before the bridge ends it itself.</summary>
    private const float LocalStopGraceSeconds = 5f;

    /// <summary>How long the bridge may own the tick before it stops driving an unchanged stopped director.</summary>
    private const float LocalStopWatchdogSeconds = 20f;

    /// <summary>Seconds between the scene-visibility lines while a cutscene is on.</summary>
    private const float SceneDiagSeconds = 5f;

    /// <summary>Seconds between the samples of this machine's own skip prompt (analysis/cutscene-skip-overlay).</summary>
    private const float OverlaySampleSeconds = 0.25f;

    /// <summary>How long the bridge waits between two attempts to take a stuck skip prompt down.</summary>
    private const float OverlayRetrySeconds = 5f;

    /// <summary>How many of the first native <c>ticked=</c> lines are written (one per owner change).</summary>
    private const int TickAttentionReports = 4;

    private static MapTimelinesBank _native;
    private static bool _adopted;
    private static IntPtr _preparedWorld;
    private static bool _createdClient;
    private static bool _createdServer;
    // (analysis/cutscene-native-registration) The per-frame registration of the two controllers, one entry
    // per world so a reconnect/transit installs it again for the new world and never twice for the same one.
    private static IntPtr _cutsceneHookWorld;
    private static bool _serverTickHooked;
    private static bool _clientTickHooked;
    private static Exception _serverTickHookError;
    private static Exception _clientTickHookError;
    private static bool _subscribeAttempted;
    private static float _nextAttempt;

    /// <summary>
    /// (perf 2026-10-14, measured with the read-only perf probe) This method's body runs once per
    /// <see cref="RetrySeconds"/> and, while no MapTimelinesBank can be found, every run pays a whole-scene
    /// lookup (FindObjectOfType plus FindNativeBankInScene's FindObjectsOfType with includeInactive).
    /// Measured on Streets: 39,257 ms over a 282 s raid, worst single call 320 ms - i.e. 139 ms every second,
    /// which is exactly the "stutter every second or two" the player feels, and it scales with the scene
    /// object count (Streets 154 ms class, Icebreaker about a tenth of it).
    /// A failing attempt therefore grows the interval: 1, 2, 4, 8, 16, 30, 30 ... seconds. The link is still
    /// adopted as soon as it becomes possible, at worst 30 s later.
    /// </summary>
    private const float MaxRetrySeconds = 30f;

    private static int _attemptFailures;

    private static float _nextFallback;
    private static string _reportedState;
    private static bool _reportedWaiting;

    /// <summary>
    /// What the playback repair knows about one cutscene process the host created
    /// (analysis/cutscene-play-and-end): when it appeared, whether this machine's own viewer was handed the
    /// scene, whether the elapsed time still moves, and whether the bridge had to take the tick over.
    /// </summary>
    private sealed class Watch
    {
        internal string Cutscene;
        internal float Seen;
        internal float Elapsed;
        internal float Advanced;
        internal bool Dispatched;
        internal bool TookOver;
        internal bool Ended;
        /// <summary>
        /// This machine's own viewer was reported as having watched the scene
        /// (analysis/cutscene-view-and-end), and when - the process is ended
        /// <see cref="PostCompleteGraceSeconds"/> later if the native side still keeps it alive.
        /// </summary>
        internal bool Reported;
        internal float ReportedAt;
        /// <summary>The cutscene's own duration, resolved once from the map's timeline bank (0 = unknown).</summary>
        internal float Duration;
    }

    private static readonly Dictionary<int, Watch> Watches = new();
    private static readonly List<int> Takeovers = new();
    private static float _nextPlaybackAttempt;
    private static float _nextDiagnose;

    // The local (client controller) half of the playback repair (analysis/cutscene-view-and-end): which process
    // this machine's own controller is playing, when its scene last made progress, whether the bridge had to
    // take the per-frame tick over, and what the last sample of the scene found.
    private static int _localProcess;
    private static float _localPendingSince;
    private static int _pendingTickFrame = -1;
    /// <summary>(analysis/cutscene-client-dispatch) The last pending verdict written, so a change is one int compare.</summary>
    private static int _pendingVerdict = -1;
    /// <summary>(analysis/cutscene-client-native-trigger) The cutscene id that verdict was written for.</summary>
    private static string _pendingVerdictCutscene;
    private static float _localSeen;
    private static float _localProgress;
    private static bool _localTakenOver;
    private static bool _localPlaying;
    private static bool _localPlayed;
    private static int _localTickFrame = -1;
    private static double _localDirectorTime = -1d;
    private static float _nextLocalSample;
    private static float _nextLocalError;
    private static PlayableDirector _localDirector;
    private static string _localScene;

    // (analysis/cutscene-client-native-trigger) The end half of a process this machine created itself with its own
    // native trigger: the process id whose local viewer was already reported through the native
    // PlayerViewCutsceneComplete, and when. The report happens once, and a process the native end did not close
    // afterwards is closed by the bridge through the very native EndCutscene the skip vote uses.
    private static int _localNativeCompleted;
    private static float _localNativeCompletedAt;

    // (analysis/cutscene-slowmo) The sample the phase=sync line is built from: the three clocks of this machine
    // (real time, the director of the local scene, the process clock of the host) and the two tick counters of
    // the controllers, all read once per second while a cutscene is on. The counters below are the ones the
    // probe owns: the counters of the Coop/Diagnostics family are gated by that switch and reset by their own
    // report, and a timing judgement must not depend on either.
    private static float _nextSyncSample;
    private static float _syncRealLast;
    private static double _syncDirectorLast = -1d;
    private static float _syncProcessLast = -1f;
    private static double _syncAudioLast = -1d;
    private static int _syncEngineLast;
    private static int _syncBridgeLast;
    private static int _syncFramesLast;
    private static int _syncNotOkSamples;
    private static bool _syncWarned;
    private static int _syncServerEngine;
    private static int _syncServerBridge;
    private static int _syncClientEngine;
    private static int _syncClientBridge;
    private static int _syncServerEngineLast;
    private static int _syncServerBridgeLast;
    private static bool _syncTimeScaleReported;
    /// <summary>(analysis/cutscene-slowmo) The rescue stood back once because the native start was in flight.</summary>
    private static bool _sceneRescueNativeDeferred;

    // The scene rescue (analysis/cutscene-rig-play): the native playback entry point is attempted once, the
    // fallback (signal tracks + Play + rig) once, and the whole rescue is bounded so a scene that cannot be
    // entered at all does not turn into one interop call per frame.
    private static bool _sceneRescueTried;
    private static bool _sceneRescueDone;
    private static int _sceneRescueAttempts;
    private static float _nextSceneRescue;
    private static bool _localStoppedSeen;
    private static float _localStoppedSince;
    private static bool _staleProcessSeen;
    private static float _staleProcessSince;
    private static float _nextSceneDiag;

    // The skip prompt of this machine's own viewer (analysis/cutscene-skip-overlay): the native
    // EFT.UI.CutsceneSkipUI that CutscenesClientController shows and hides. The sample below is the one place
    // that answers "is the prompt on screen", so a raid leaves the state of the prompt next to the state of
    // the scene, and the prompt of a process that is over is taken down through its own native hiding method.
    private static float _nextOverlaySample;
    private static bool _overlaySeen;
    private static string _overlayCutscene;
    private static float _nextOverlayAttempt;
    private static int _overlayAttempts;

    // The native playback entry point (CutscenesClientController.StartShowTimeline) and the stop the skip path
    // uses (StopCutscene), resolved once - both are private, so the bridge reaches them through the same
    // reflection Fika already uses for a private controller method.
    private static MethodInfo _showTimeline;
    private static MethodInfo _stopCutscene;
    private static MethodInfo _rebuildGraph;

    // The tick counters (analysis/cutscene-view-and-end): every executed OnUpdate of the two controllers is
    // counted per frame and attributed to the engine or to this bridge, which is what tells "nobody ticks this
    // controller" from "the engine does, the scene is broken somewhere else".
    private static int _bridgeServerFrame = -1;
    private static bool _insideBridgeServerCall;
    private static bool _insideBridgeClientCall;
    private static int _serverEngineTicks;
    private static int _serverBridgeTicks;
    private static int _clientEngineTicks;
    private static int _clientBridgeTicks;
    private static float _nextTickReport;
    private static int _serverTickOwner;
    private static int _clientTickOwner;

    internal static bool Adopted => _adopted;

    internal static void Reset()
    {
        _native = null;
        _adopted = false;
        _preparedWorld = IntPtr.Zero;
        _createdClient = false;
        _createdServer = false;
        _nextAttempt = 0;
        _nextFallback = 0;
        _reportedState = null;
        _reportedWaiting = false;
        Watches.Clear();
        Takeovers.Clear();
        _nextPlaybackAttempt = 0;
        _nextDiagnose = 0;
        _localProcess = 0;
        _localPendingSince = 0f;
        _pendingTickFrame = -1;
        _pendingVerdict = -1;
        _pendingVerdictCutscene = null;
        _localSeen = _localProgress = 0;
        _localTakenOver = _localPlaying = _localPlayed = false;
        _localTickFrame = -1;
        _nextLocalSample = 0;
        _localDirector = null;
        _localScene = null;
        _localDirectorTime = -1d;
        _localNativeCompleted = 0;
        _localNativeCompletedAt = 0f;
        _nextLocalError = 0;
        _bridgeServerFrame = -1;
        _insideBridgeServerCall = _insideBridgeClientCall = false;
        _serverEngineTicks = _serverBridgeTicks = _clientEngineTicks = _clientBridgeTicks = 0;
        _nextTickReport = 0;
        _serverTickOwner = _clientTickOwner = 0;
        _sceneRescueTried = _sceneRescueDone = false;
        _sceneRescueAttempts = 0;
        _nextSceneRescue = 0;
        _sceneRescueNativeDeferred = false;
        // (analysis/cutscene-slowmo) The sync probe belongs to the raid that just ended: a new world must not
        // compare its first sample against the clocks of the old one.
        _nextSyncSample = 0;
        _syncRealLast = 0;
        _syncDirectorLast = -1d;
        _syncProcessLast = -1f;
        _syncAudioLast = -1d;
        _syncEngineLast = _syncBridgeLast = _syncFramesLast = 0;
        _syncNotOkSamples = 0;
        _syncWarned = false;
        _syncServerEngine = _syncServerBridge = _syncClientEngine = _syncClientBridge = 0;
        _syncServerEngineLast = _syncServerBridgeLast = 0;
        _syncTimeScaleReported = false;
        _localStoppedSeen = false;
        _localStoppedSince = 0;
        _staleProcessSeen = false;
        _staleProcessSince = 0;
        _nextSceneDiag = 0;
        _nextOverlaySample = 0;
        _overlaySeen = false;
        _overlayCutscene = null;
        _nextOverlayAttempt = 0;
        _overlayAttempts = 0;
        _cutsceneHookWorld = IntPtr.Zero;
        _serverTickHooked = _clientTickHooked = false;
        _subscribeAttempted = false;
    }

    private static bool _nativeEngineTickLogged;
    private static int _nativeEngineTickErrors;

    /// <summary>
    /// Runs one forwarded native per-frame step and reports what it did (analysis/cutscene-rig-play). The first
    /// entry of the native body is reported once - it is the evidence that the engine's own chain (and not only
    /// this bridge) reaches the cutscene controllers, and the <c>phase=gate:ticks</c> counters of the following
    /// seconds then show whether the controllers are really entered from it. A failure is reported at most once
    /// per 32 calls: a native base call can throw, and a broken per-frame step must not flood the raid tick.
    /// </summary>
    internal static void NativeEngineTick(string who, Action step)
    {
        if (step == null) return;
        try
        {
            step();
        }
        catch (Exception error)
        {
            if (_nativeEngineTickErrors >= 32) return;
            _nativeEngineTickErrors++;
            FikaGlobals.LogWarning("CutsceneBoundary: the forwarded native AfterPlayerTick of the " + who
                + " world failed (" + error.GetType().Name + ": " + error.Message + ")"
                + (_nativeEngineTickErrors >= 32 ? "; further failures are not reported" : ""));
            return;
        }
        if (_nativeEngineTickLogged) return;
        _nativeEngineTickLogged = true;
        
    }

    /// <summary>
    /// The world-factory entry of the controller creation (analysis/cutscene-world-factory) - now only the
    /// rollback path. The section below (analysis/cutscene-native-registration) corrected the original
    /// assumption: the native <c>GameWorld.Create&lt;T&gt;</c> body does <b>not</b> create the cutscene
    /// controllers (4.0 <c>EFT/GameWorld.cs:1111-1123</c> is seven lines and creates none of this family). The
    /// world only owns them; they are created - and registered on their per-frame hook - by the raid-start
    /// coroutine <c>BaseLocalGame.method_4</c>, which this port replaces with <c>CoopGame.InitPlayer</c>.
    ///
    /// Evidence that the world owns them and nothing else does: both controllers are plain
    /// <c>Il2CppSystem.Object</c>s owned by the world
    /// (<c>GameWorld.CutscenesClientController</c> / <c>CutscenesServerController</c>, each with its own
    /// backing field, interop <c>:229305-229310</c>), have no world field of their own and are never created
    /// by the ported factory - so without a creation at the native position a Fika raid has neither, and
    /// nothing can start a process, decode a timeline or open the story doors.
    ///
    /// Called from <see cref="FikaHostWorld.Create"/> / <see cref="FikaClientWorld.Create"/> through
    /// <see cref="EnsureControllers"/>, i.e. inside the world creation chain - <b>not</b> at the native
    /// position. analysis/cutscene-native-registration moved the default creation to the raid start coroutine
    /// (<c>CoopGame.InitPlayer</c>, which replaces <c>BaseLocalGame.method_4</c>), the place the native build
    /// itself creates and registers this whole controller family; the native order settles the timing question
    /// (the level of the raid is already loaded when <c>method_4</c> runs, and the map's cutscene objects
    /// register with the controllers of the live world later, when their scene is created).
    ///
    /// Only a missing controller is created; an existing one is never replaced, never re-initialised and
    /// never disposed. A failure only warns, the raid continues.
    /// </summary>
    internal static void EnsureControllers(GameWorld world, string source)
    {
        // Rollback path of analysis/cutscene-world-factory: since analysis/cutscene-native-registration the
        // controllers are created at the native position (CreateCutsceneControllers, called from
        // CoopGame.InitPlayer). One const switches the old behaviour - and only it - back on.
        if (!Enabled || !CreateControllerIfMissing || world == null) return;
        CreateCutsceneControllers(world, source, false);
    }

    /// <summary>
    /// The native creation and registration position of the two cutscene controllers
    /// (analysis/cutscene-native-registration).
    ///
    /// Evidence: the native raid-start coroutine <c>BaseLocalGame&lt;TPlayerOwner&gt;.method_4</c> creates this
    /// whole family of world controllers - shelling, Halloween, BTR, transit, Runddans, the broadcast sync
    /// controller - and each of them registers itself on a per-frame hook in the very same place
    /// (<c>ServerShellingControllerClass.cs:271-291</c> <c>localGame.UpdateByUnity += OnUpdate</c>;
    /// <c>ClientShellingControllerClass.cs:46-49</c> <c>GameWorld_0.OnLateUpdate += method_1</c>). This port
    /// replaces that coroutine with <c>CoopGame.InitPlayer</c> (<c>CoopGame.cs:394-439</c>), whose list carries
    /// every one of those controllers except the cutscene pair - which is why a Fika raid had two controllers
    /// that no caller ever entered (<c>[CutsceneDiag] phase=gate:ticks ... engine=0</c>).
    ///
    /// What this method does, in the order the native build does it:
    /// <list type="number">
    /// <item>creates a controller the world does not have yet (a controller that is already there is never
    /// replaced - so the native world factory, the rollback path of
    /// <see cref="CreateControllerIfMissing"/> and a reconnect all stay safe);</item>
    /// <item>registers it on the per-frame hook of the native build (<see cref="RegisterTickHooks"/>, one line
    /// per controller, idempotent);</item>
    /// <item>reports the two facts in one grep-able line: <c>phase=world ... at=InitPlayer</c> and
    /// <c>phase=tick-hook ... ok=true|false</c>.</item>
    /// </list>
    ///
    /// The native <c>Init</c> (which links the bank and subscribes the controller to the cutscene global
    /// events) is deliberately NOT called here: it is the private <c>Task</c> the native constructor starts,
    /// and the bridge already drives the same two outcomes through its own guarded paths (<see cref="Tick"/> /
    /// <see cref="SubscribeControllers"/>), which report themselves. Creating and registering is the whole of
    /// this method.
    /// </summary>
    internal static void CreateCutsceneControllers(GameWorld world, string source, bool viaNativePosition)
    {
        if (!Enabled || world == null) return;
        if (_preparedWorld == world.Pointer) return;
        _preparedWorld = world.Pointer;
        try
        {
            if (!FikaBackendUtils.IsServer && !FikaBackendUtils.IsClient)
            {
                

                return;
            }

            var hadClient = world.CutscenesClientController != null;
            var hadServer = world.CutscenesServerController != null;
            if (!hadClient)
            {
                world.CutscenesClientController = new CutscenesClientController();
                _createdClient = world.CutscenesClientController != null;
            }
            if (!hadServer)
            {
                world.CutscenesServerController = new CutscenesServerController();
                _createdServer = world.CutscenesServerController != null;
            }

            var clientOk = world.CutscenesClientController != null;
            var serverOk = world.CutscenesServerController != null;

            // The same two lines the bridge's own ConfigureControllers writes on every drain. They are written
            // here too so a controller is never alive in the single-player mode while the first per-frame steps
            // arrive (analysis/cutscene-play-and-end: a bound raid carries its events over the Fika connection,
            // so neither native controller may play or finish locally).
            try
            {
                if (clientOk) world.CutscenesClientController._offlineMode = false;
                if (serverOk) world.CutscenesServerController._offlineMode = false;
            }
            catch (Exception offlineError)
            {
                FikaGlobals.LogWarning("CutsceneBoundary: writing the offline flags of the cutscene controllers failed ("
                    + offlineError.GetType().Name + ": " + offlineError.Message + "); the bridge retries it every drain");
            }

            // The registration that was missing: the native build hands each controller its first per-frame
            // caller in the same lines that create it (analysis/cutscene-native-registration, C2).
            var hooks = RegisterTickHooks(world);

            
        }
        catch (Exception error)
        {
            FikaGlobals.LogWarning("CutsceneBoundary: creating the cutscene controllers failed ("
                + error.GetType().Name + ": " + error.Message + "); the raid continues without them");
        }
    }

    /// <summary>
    /// The one line of analysis/cutscene-native-registration that was missing from the port: give the two
    /// controllers the per-frame caller the native build gives them in the same place it creates them.
    ///
    /// The hooks are the ones this port already uses for the same purpose, i.e. the ones the native side
    /// registers on in the sibling controllers:
    /// <list type="bullet">
    /// <item><b>server</b> - <c>BaseLocalGame&lt;T&gt;.UpdateByUnity</c>, the event the native
    /// <c>ServerShellingControllerClass</c> subscribes to (<c>ServerShellingControllerClass.cs:291</c>) and the
    /// one this port already drives for artillery
    /// (<c>ArtilleryShellingControllerServer_Init_Patch.cs:58-60</c>,
    /// <c>HostGameController.Update</c> at <c>HostGameController.cs:192-198</c>);</item>
    /// <item><b>client</b> - <c>GameWorld.OnLateUpdate</c>, the event the native
    /// <c>ClientShellingControllerClass</c> subscribes to (<c>ClientShellingControllerClass.cs:46-49</c>),
    /// raised by <c>GameWorld.LateUpdateWorld</c> from <c>GameWorldUnityTickListener.LateUpdate</c> - the one
    /// world step that has no camera guard.</item>
    /// </list>
    ///
    /// Both are idempotent (one registration per world, tracked by <see cref="_cutsceneHookWorld"/>) and both
    /// are bounded: a hook that throws is written once and never again, and it cannot take the world tick down.
    /// <c>NativeTickHook = false</c> (config <c>Coop / Cutscene: Native Tick Hook</c>) removes both and leaves
    /// the bridge as the only caller, exactly the state before this file changed.
    /// </summary>
    private static bool RegisterTickHooks(GameWorld world)
    {
        if (!NativeTickHook) return false;
        if (_cutsceneHookWorld == world.Pointer) return true;
        if (FikaSingleton<IFikaGame>.Instance?.GameController is not HostGameController host)
        {
            // A client has no BaseLocalGame of its own here: its world tick is the GameWorld event below,
            // which needs no controller. The server controller of a client is never ticked natively either.
            return RegisterClientHook(world);
        }

        var ok = RegisterServerHook(world, host) && RegisterClientHook(world);
        if (ok) _cutsceneHookWorld = world.Pointer;
        return ok;
    }

    private static bool RegisterServerHook(GameWorld world, HostGameController host)
    {
        var controller = world.CutscenesServerController;
        if (controller == null) return false;
        if (_serverTickHooked) return true;
        try
        {
            // Same bind as the artillery repair (ArtilleryShellingControllerServer_Init_Patch.cs:58-60): the
            // controller's native per-frame step is handed to the event the native BaseLocalGame raises. The
            // controller's step takes the frame delta (interop :217554 OnUpdate(Single)), the event carries no
            // argument, so the delta of this frame is passed - exactly what the native caller of the world
            // family does with the value it receives.
            System.Action step = () => controller.OnUpdate(TickDelta(Time.deltaTime));
            host.add_UpdateByUnity(step);
            _serverTickHooked = true;
            return true;
        }
        catch (Exception error)
        {
            _serverTickHookError ??= error;
            return false;
        }
    }

    private static bool RegisterClientHook(GameWorld world)
    {
        var controller = world.CutscenesClientController;
        if (controller == null) return false;
        if (_clientTickHooked) return true;
        try
        {
            // The interop event takes an Action<float>; the method group of the injected type is wrapped
            // explicitly because the IL2CPP side needs a real delegate object (a managed method group is not
            // one for this interop). The native body is reached through the generated wrapper of OnUpdate.
            System.Action<float> step = Dt => controller.OnUpdate(TickDelta(Dt));
            world.add_OnLateUpdate(step);
            _clientTickHooked = true;
            return true;
        }
        catch (Exception error)
        {
            _clientTickHookError ??= error;
            return false;
        }
    }

    /// <summary>
    /// (analysis/cutscene-slowmo) The delta every per-frame step of a cutscene controller is fed with - the one
    /// place that decides how fast a cutscene may run.
    ///
    /// The native contract is «the delta of this frame», and every caller here hands in the value the game would
    /// have handed in (the world passes <c>Time.deltaTime</c>, the raid tick computes it). That is real time as
    /// long as <c>Time.timeScale</c> is 1 - which is exactly the assumption a slow motion playback breaks:
    /// <c>Time.deltaTime</c> is the <b>scaled</b> frame delta, so anything that slows the game clock down makes
    /// every tick this bridge or its hooks feed shorter than the wall clock, and a timeline driven by those
    /// ticks crawls while the audio (an <c>AudioSource</c> of the native CutsceneAudioConfigurator, which knows
    /// nothing about <c>timeScale</c>) keeps running at real time. The symptom is exactly the reported one:
    /// slow motion, out of sync with the sound.
    ///
    /// So: while the clock is unscaled (the normal case) the native delta is passed through untouched; while it
    /// is scaled the real delta of the same frame is passed instead, and the state is written down once
    /// (<see cref="ReportTimeScale"/>) so the log names the cause. A clock at or below zero is left alone on
    /// purpose: a paused game (<c>timeScale == 0</c>, the menu) is meant to hold the picture, not to run it.
    /// </summary>
    private static float TickDelta(float nativeDt)
    {
        if (!RealTimeTicks) return nativeDt;
        float scale;
        try { scale = Time.timeScale; }
        catch (Exception) { return nativeDt; }
        if (scale <= 0f || (scale > 0.999f && scale < 1.001f)) return nativeDt;
        ReportTimeScale(scale);
        var real = Time.unscaledDeltaTime;
        return real > 0f ? real : nativeDt;
    }

    /// <summary>
    /// The one line that names a scaled game clock (analysis/cutscene-slowmo): written once per distinct value,
    /// outside the Coop/Diagnostics switch, because it is the difference between «the CG is slow» and «the whole
    /// game clock is scaled and the CG is the only thing that reads it in real time».
    /// </summary>
    private static void ReportTimeScale(float scale)
    {
        if (_syncTimeScaleReported) return;
        _syncTimeScaleReported = true;
        
    }

    /// <summary>Reports a hook that could not be installed, exactly once per raid.</summary>
    private static void ReportHookErrors()
    {
        var server = _serverTickHookError;
        var client = _clientTickHookError;
        if (server == null && client == null) return;
        _serverTickHookError = _clientTickHookError = null;
        FikaGlobals.LogWarning("CutsceneBoundary: the per-frame hook of a cutscene controller could not be installed ("
            + (server != null ? "server: " + server.GetType().Name + " " + server.Message : "")
            + (server != null && client != null ? "; " : "")
            + (client != null ? "client: " + client.GetType().Name + " " + client.Message : "")
            + "); the bridge keeps ticking the controllers itself");
    }

    /// <summary>
    /// The call <c>CoopGame.InitPlayer</c> makes where the native <c>BaseLocalGame.method_4</c> creates this
    /// controller family (analysis/cutscene-native-registration, C1). It is only a wrapper so the raid start
    /// reads like the native list:
    /// <code>
    /// GameController.InitShellingController(instance, gameWorld, Location);
    /// ...
    /// GameController.InitCutsceneControllers(gameWorld);
    /// gameWorld.ClientBroadcastSyncController = new ClientBroadcastSyncController();
    /// </code>
    /// </summary>
    internal static void InitCutsceneControllers(GameWorld world) =>
        CreateCutsceneControllers(world, "CoopGame.InitPlayer", true);

    private static string Origin(bool existed, bool created) => existed ? "present" : created ? "created" : "failed";

    /// <summary>
    /// Runs the repair on the raid tick (host) and on the client's raid-interaction drain: link the bank,
    /// tick a process the engine leaves alone, tick this machine's own scene when nothing else does, then
    /// sample the process table for the playback repair. Every step is idempotent and throttled, so the two
    /// call sites (host tick and client drain) may both run it in one frame.
    /// </summary>
    internal static void Tick(GameWorld world)
    {
        TryAdoptLink(world);
        PumpTakeover(world);
        PumpLocalPlayback(world);
        PumpSkipOverlay(world);
        TryRepairHostPlayback(world);
        // (analysis/cutscene-slowmo) The timing judgement of a running cutscene, last so it reads the state
        // every pump above just updated. One sample per second, and only while a process exists.
    }

    private static void TryAdoptLink(GameWorld world)
    {
        if (!Enabled || _adopted || world == null || RaidInteractionSync.World?.Pointer != world.Pointer) return;
        if (!RaidInteractionSync.Ready) return;
        var now = Time.unscaledTime;
        if (now < _nextAttempt) return;
        // (perf) see MaxRetrySeconds: back off while the lookup keeps failing instead of paying a
        // whole-scene scan every single second.
        _attemptFailures++;
        if (_attemptFailures > 3)
        {
            // (perf 2026-10-14, functional fix) The map's timeline bank is either there when the level loads or
            // the map carries no story cutscene at all - there is no third case, so three attempts are enough
            // and they all happen inside the raid load (0 s, 1 s, 3 s). After that this raid never pays the
            // whole-scene lookup again. Before: 15 scans / 1,370 ms per raid, worst single 157 ms, felt as a
            // hitch every ~30 s. Reset() re-arms this on the next raid or transit, and a map WITH a story
            // cutscene adopts on the first attempt.
            _nextAttempt = float.MaxValue;
            return;
        }

        _nextAttempt = now + (_attemptFailures == 1 ? 0f : _attemptFailures == 2 ? 1f : 3f);

        var host = FikaBackendUtils.IsServer;
        var client = FikaBackendUtils.IsClient;
        if (!host && !client)
        {
            
            return;
        }

        var clientController = world.CutscenesClientController;
        var serverController = world.CutscenesServerController;
        if (clientController == null && serverController == null)
        {
            
            return;
        }

        if (_native == null)
        {
            _native = UnityEngine.Object.FindObjectOfType<MapTimelinesBank>();
            if (_native == null) _native = FindNativeBankInScene();
            if (_native == null)
            {
                if (!_reportedWaiting)
                {
                    _reportedWaiting = true;
                    
                }
                return;
            }
            
        }

        if (clientController == null)
        {
            
        }
        else if (clientController._timelinesMapBank == null)
        {
            clientController._timelinesMapBank = _native;
        }
        else if (clientController._timelinesMapBank.Pointer != _native.Pointer)
        {
            
        }

        if (serverController != null && serverController._timelinesMapBank == null)
        {
            serverController._timelinesMapBank = _native;
        }

        _adopted = true;
    }

    /// <summary>
    /// The playback repair (analysis/cutscene-play-and-end). A Fika raid replaces the native world factory,
    /// so the native half of "trigger to play to end" has to be checked on every machine that runs it. The
    /// round-15 host log is the shape of the gap: the host created process=1 for 'Icebreaker_1' for 1 viewer
    /// (itself) and wrote no play, view or end line afterwards, and the pier gate stayed shut.
    ///
    /// This runs on the host and only touches a process whose viewer list contains this machine's own player.
    /// Per process it makes at most one decision per stage, and every decision writes its own
    /// <see cref="CutsceneDiag.Note"/> line so the next log names the reason:
    /// <list type="number">
    /// <item><b>dispatch</b> - if the native client controller has still not been handed the process
    /// <see cref="LocalStartGraceSeconds"/> after it appeared, the bridge raises the native
    /// <see cref="CutSceneStartEvent"/> for it. That is the same event a Fika client receives in
    /// <c>FikaClient.ApplyPendingRaidInteractions</c> and the same one the host's own final ending raises in
    /// <c>FikaFinalMissionDirector.StartLocalEnding</c>; without it the host is the one viewer of its own
    /// process that is never told to play.</item>
    /// <item><b>advance</b> - <c>CutscenesServerController.OnUpdate(dt)</c> is the only writer of
    /// <c>CutsceneProcess.afterStartTimeElapsed</c>. If that value stands still for <see cref="FrozenSeconds"/>,
    /// no native caller ticks the controller on this machine and the bridge calls the very same method itself
    /// (see <see cref="PumpTakeover"/>), so the timeline's clock and its end still happen.</item>
    /// <item><b>end</b> - with <see cref="ForceEndStalledProcess"/> a process that is still unadvanced
    /// <see cref="StalledSeconds"/> after this machine's own viewer finished is ended through the native
    /// <c>EndCutscene</c>, the same call the skip vote uses, so the end actions (the pier gate) still run.</item>
    /// </list>
    /// Nothing here runs for a process this machine does not watch, and nothing runs while the engine does its
    /// job: the dispatch step is skipped as soon as the local controller plays the process, and the advance step
    /// is skipped while the elapsed time moves.
    /// </summary>
    private static void TryRepairHostPlayback(GameWorld world)
    {
        if (!Enabled || !RepairHostPlayback || world == null || !FikaBackendUtils.IsServer) return;
        var server = world.CutscenesServerController;
        var client = world.CutscenesClientController;
        if (server?._cutsceneProcesses == null) return;
        var now = Time.unscaledTime;
        if (now < _nextPlaybackAttempt) return;
        _nextPlaybackAttempt = now + PlaybackIntervalSeconds;

        var local = RaidInteractionSync.LocalPlayerId;
        var live = new List<int>();
        var toEnd = 0;
        var toEndCutscene = (string)null;
        var toEndReason = (string)null;
        foreach (var entry in server._cutsceneProcesses)
        {
            var process = entry.Value;
            if (process == null) continue;
            var cutscene = process.currentCutsceneId;
            if (string.IsNullOrEmpty(cutscene)) continue;
            var id = process.processId;
            live.Add(id);
            if (!Watches.TryGetValue(id, out var watch))
            {
                watch = new Watch { Cutscene = cutscene, Seen = now, Elapsed = process.afterStartTimeElapsed, Advanced = now };
                Watches[id] = watch;
            }

            var viewers = process.playersInCurrentView;
            var viewerCount = viewers?.Count ?? 0;
            // The event carries a native Il2CppStructArray<int> of PlayerIds, which is what the process holds.
            var viewerIds = viewers == null ? null : ((IEnumerable<int>)viewers).ToArray();
            var isViewer = local >= 0 && viewerIds != null && viewerIds.Contains(local);

            if (!watch.Dispatched)
            {
                if (!isViewer)
                {
                    // This machine does not watch this process: nothing to dispatch here.
                    watch.Dispatched = true;
                }
                else if (client != null && client._cutsceneProcessId == id)
                {
                    watch.Dispatched = true;
                    
                }
                else if (now - watch.Seen >= LocalStartGraceSeconds)
                {
                    watch.Dispatched = true;
                    GlobalEventsController.CreateEvent<CutSceneStartEvent>().Invoke(cutscene, id, viewerIds, 0f, true, false);
                    
                }
            }

            // CutsceneProcess.afterStartTimeElapsed is written by CutscenesServerController.OnUpdate(dt) and by
            // nothing else: a value that moves proves the engine calls it, a frozen value proves nobody does.
            var elapsed = process.afterStartTimeElapsed;
            if (elapsed > watch.Elapsed + 0.0001f)
            {
                watch.Elapsed = elapsed;
                watch.Advanced = now;
            }
            else if (!watch.TookOver && now - watch.Seen >= FrozenSeconds && now - watch.Advanced >= FrozenSeconds)
            {
                watch.TookOver = true;
                watch.Elapsed = elapsed;
                watch.Advanced = now;
                Takeovers.Add(id);
                
            }

            

            // (a) Last resort of the previous round: a process the engine leaves alone, that has not moved for
            // StalledSeconds even though the bridge ticks it, and that no longer has a scene running on this
            // machine. The round-16 log is why it is not enough on its own: as soon as the bridge ticks the
            // process, elapsed moves every frame, so "nothing moved for 25 s" can never become true again.
            var localOwnsProcess = client != null && client._cutsceneProcessId == id;
            if (!watch.Ended && watch.TookOver && now - watch.Advanced >= StalledSeconds &&
                (RaidInteractionSync.HasLocalViewCompleted(id) || !localOwnsProcess))
            {
                watch.Ended = true;
                toEnd = id;
                toEndCutscene = cutscene;
                toEndReason = "nothing advanced the process for " + StalledSeconds + "s";
            }

            // (b) The chain the round-16 log shows broken (analysis/cutscene-view-and-end): the process runs, this
            // machine's viewer is the only one in it, and its completion never arrives. The viewed counter is
            // written by nothing but the native PlayerViewCutsceneComplete, whose local trigger TimelineShowed
            // never entered - because this machine never played the scene at all. Report the completion through
            // that same native method once the scene is provably over (or once a viewer that never saw a frame
            // has waited ViewerAbandonSeconds), and end what the native side still keeps alive afterwards.
            if (!watch.Ended && local >= 0 && viewerCount > 0)
            {
                if (watch.Duration <= 0f) watch.Duration = TimelineDuration(server, cutscene);
                var complete = process.playersCutsceneViewed >= viewerCount;
                var over = watch.Duration > 0f && elapsed >= watch.Duration + TimelineSlackSeconds;
                var stopped = _localPlayed && _localProcess == id && !_localPlaying &&
                    now - _localProgress >= TimelineStoppedGraceSeconds;
                // "_localProcess == 0" is the other side of the same coin: this machine is a viewer of the
                // process but its controller was never even handed one (the dispatch step above is what repairs
                // that), so there is nothing to watch here either.
                var abandoned = !_localPlayed && (_localProcess == id || _localProcess == 0) &&
                    now - watch.Seen >= ViewerAbandonSeconds;
                if (!watch.Reported && (complete || over || stopped || abandoned))
                {
                    watch.Reported = true;
                    watch.ReportedAt = now;
                    var reason = complete ? "every viewer of the process is counted as done but the process is alive"
                        : over ? "the timeline is over (elapsed=" + elapsed.ToString("0.00") + "s duration="
                            + watch.Duration.ToString("0.00") + "s) and this machine's viewer never reported"
                        : stopped ? "the local scene stopped playing " + (now - _localProgress).ToString("0.00")
                            + "s ago and this machine's viewer never reported"
                        : "nothing was ever played on this machine within " + ViewerAbandonSeconds
                            + "s, so this machine's viewer has nothing to watch";
                    if (!complete) ReportLocalCompletion(server, process, cutscene, local, reason);
                    
                }
                else if (watch.Reported && now - watch.ReportedAt >= PostCompleteGraceSeconds)
                {
                    watch.Ended = true;
                    toEnd = id;
                    toEndCutscene = cutscene;
                    toEndReason = "the process was still alive " + PostCompleteGraceSeconds
                        + "s after the viewer completion was reported";
                }
            }
        }

        Prune(live);
        if (toEnd != 0) ForceEnd(server, toEnd, toEndCutscene, toEndReason);
    }

    /// <summary>
    /// Calls the native <c>CutscenesServerController.OnUpdate(dt)</c> once per frame for every process the
    /// engine leaves unadvanced, with the same frame delta the engine would pass. SPTushonka's
    /// <c>CutsceneUpdateGuardPatches</c> drops a second call inside one frame, so the engine keeps priority
    /// whenever it does run and the process is never advanced twice.
    /// </summary>
    private static void PumpTakeover(GameWorld world)
    {
        if (!Enabled || !RepairHostPlayback || Takeovers.Count == 0 || world == null || !FikaBackendUtils.IsServer) return;
        var server = world.CutscenesServerController;
        if (server?._cutsceneProcesses == null) return;
        var dt = TickDelta(Time.deltaTime);
        for (var i = Takeovers.Count - 1; i >= 0; i--)
        {
            var id = Takeovers[i];
            if (!server._cutsceneProcesses.ContainsKey(id))
            {
                Takeovers.RemoveAt(i);
                continue;
            }
            // One advance per frame at most, whatever calls the raid tick: the engine's own call (when it
            // happens to run) keeps priority through SPTushonka's same-frame guard, and this frame check keeps
            // the bridge from advancing twice when both call sites of Tick() fire in the same frame.
            if (_bridgeServerFrame == Time.frameCount) continue;
            _bridgeServerFrame = Time.frameCount;
            // The marker the tick counter reads: it is only set while this very call runs, so an engine call
            // in the same frame is still attributed to the engine.
            _insideBridgeServerCall = true;
            try { server.OnUpdate(dt); }
            finally { _insideBridgeServerCall = false; }
        }
    }

    /// <summary>Drops the watches (and the takeover of a process that is gone with them).</summary>
    private static void Prune(List<int> live)
    {
        if (Watches.Count == 0) return;
        List<int> gone = null;
        foreach (var id in Watches.Keys)
            if (!live.Contains(id)) (gone ??= new List<int>()).Add(id);
        if (gone == null) return;
        foreach (var id in gone)
        {
            Watches.Remove(id);
            Takeovers.Remove(id);
        }
    }

    /// <summary>
    /// Native <c>EndCutscene</c> for a process that cannot reach its own end any more: the end actions of the
    /// timeline (the pier gate of <c>CutsceneActionOpenDoors</c>) are part of that call. It is the very same
    /// call the all-viewers-voted-to-skip path uses (<c>RaidInteractionSync.BroadcastVotes</c> is its only other
    /// caller), and the native body announces itself
    /// through <c>CutsceneEndSyncPatch</c>, which prints the timeline's end actions and the state of every door
    /// they name right afterwards.
    /// </summary>
    private static void ForceEnd(CutscenesServerController server, int processId, string cutscene, string reason)
    {
        if (!server._cutsceneProcesses.TryGetValue(processId, out var process) || process == null) return;
        
        server.EndCutscene(cutscene, processId, process, process.playersInCurrentView, true);
    }

    /// <summary>
    /// The native server half of "this viewer watched the scene":
    /// <c>CutscenesServerController.PlayerViewCutsceneComplete(String cutsceneId, Int32 processId, Int32 playerId)</c>.
    /// The host calls exactly this method when a viewer's acknowledgement arrives over the network
    /// (<see cref="RaidInteractionSync.HandleComplete"/>), and it is the only writer of
    /// <c>CutsceneProcess.playersCutsceneViewed</c>. Reporting the local viewer through it keeps the process'
    /// own counter and the native end that follows it native - the bridge adds no end logic of its own here.
    /// </summary>
    private static void ReportLocalCompletion(CutscenesServerController server, CutsceneProcess process,
        string cutscene, int viewer, string reason)
    {
        var viewers = process.playersInCurrentView;
        
        server.PlayerViewCutsceneComplete(cutscene, process.processId, viewer);
    }

    /// <summary>
    /// The client half of the repair (analysis/cutscene-view-and-end).
    ///
    /// <c>CutscenesClientController.OnUpdate(dt)</c> is the per-frame step that consumes what the
    /// <c>CutSceneStartEvent</c> handler left behind - the pending start of the scene this machine was handed -
    /// and that follows the running scene to its end. On a Fika raid nothing calls it: the same reason that
    /// leaves <c>CutscenesServerController.OnUpdate</c> uncalled (its elapsed time provably stands still, see
    /// <see cref="TryRepairHostPlayback"/>) applies to both controllers, and the round-16 host log is the proof
    /// for this one - the process reached the controller (<c>local=playing#1</c>) and then nothing happened any
    /// more: no picture, no <c>TimelineShowed</c>, no completion, with the timeline clock running to 494 s.
    ///
    /// The bridge therefore advances this machine's own scene while it makes no progress, and it keeps doing so
    /// for the life of the process: the very same per-frame method is what later notices that the scene is over,
    /// which is the step the viewer completion depends on.
    /// </summary>
    private static void PumpLocalPlayback(GameWorld world)
    {
        if (!Enabled || world == null) return;
        // (analysis/cutscene-client-dispatch) The switch that decides whether a start nobody consumes can ever
        // become a scene here - named in a line, because with it off the whole pending path is unreachable and
        // the client would look exactly as it does when the host never addressed it.
        if (!RepairLocalPlayback)
        {
            ReportPending(3, null, "switch Cutscene: Repair Bridge Playback is off, so the bridge never ticks the local"
                + " cutscene controller; a start the engine does not consume stays pending for the whole raid");
            return;
        }
        var client = world.CutscenesClientController;
        if (client == null) return;
        var processId = client._cutsceneProcessId;
        var now = Time.unscaledTime;
        if (processId == 0)
        {
            // (analysis/rally-gate-deadlock) A start event this machine received leaves the controller in its
            // "pending" state: the handler only sets _needStartTimeline / _waitFramesLeft, and the
            // controller's own per-frame OnUpdate is the only thing that consumes them. Nothing calls that
            // method on a Fika raid (analysis/cutscene-view-and-end), so the state used to be a dead end -
            // the player never enters the cutscene, sees no picture, and a peer that never entered it cannot
            // vote either. The same tick is therefore driven from here as well, from a state the old code
            // returned out of.
            if (PumpPendingStart(client, now)) return;
            if (_localProcess != 0)
            {
                _localProcess = 0;
                _localTakenOver = _localPlaying = _localPlayed = false;
                _localDirector = null;
                _localScene = null;
                
            }
            return;
        }

        var cutscene = client._cutsceneId;
        if (_localProcess != processId)
        {
            _localProcess = processId;
            _localSeen = _localProgress = now;
            _localTakenOver = _localPlaying = _localPlayed = false;
            _localDirector = null;
            _localScene = null;
            _localDirectorTime = -1d;
            _nextLocalSample = 0;
            _localNativeCompleted = 0;
            _localNativeCompletedAt = 0f;
            // (analysis/rally-gate-deadlock) This machine's own viewer just entered a cutscene: the one signal
            // that tells a peer "inside the scene and quiet" from "never entered it" - which is what the
            // host-side vote and completion watchdogs need before they may stop waiting for it.
            try { RaidInteractionSync.AnnounceLocalCutsceneStart(cutscene, processId); }
            catch (Exception error)
            {
                FikaGlobals.LogWarning("CutsceneBoundary: announcing the local cutscene start failed ("
                    + error.GetType().Name + ": " + error.Message + ")");
            }
        }

        // The scene is sampled, not read per frame: the director lookup crosses the interop boundary and every
        // dictionary access hands out a managed wrapper.
        if (now >= _nextLocalSample)
        {
            _nextLocalSample = now + LocalSampleSeconds;
            // The sample comes first: it is what resolves the director this machine's scene lives on, and the
            // rescue below needs that director before it can enter the native playback.
            SampleLocalScene(client, cutscene);
            if (_localPlaying)
            {
                // A scene that is really playing needs no rescue state any more; only the once-per-5s line
                // keeps reporting it (analysis/cutscene-rig-play, phase=scene).
                _sceneRescueTried = true;
                _sceneRescueDone = true;
                // (analysis/perf-diagnostics-cleanup) Sampling the scene stays (the rescue and the tick
                // decision need it), but building the report of the sample does not: with the master switch
                // off neither this line nor the LocalScene() string below is produced - this block runs four
                // times per second while a cutscene is on.
                
            }
            else
            {
                TryRescueLocalScene(client, cutscene, processId);
                
            }
        }

        // (analysis/cutscene-slowmo) The take-over of the local tick, and the one condition that makes it a
        // proof instead of a guess: the scene must be <b>stopped</b>. A director that is playing is being
        // driven - by the engine's per-frame chain (which the native tick hook of this raid provides, proven by
        // phase=gate:ticks engine>0) or by the timeline's own clock - and a second caller would advance it
        // twice per frame. The old condition («no progress for 2 s») could not tell the two cases apart: a
        // scene that plays <b>slowly</b> (the reported slow motion) makes the same little progress as a scene
        // that nobody advances, so the bridge took the tick over <i>while the engine was already driving it</i>
        // and the timeline then ran on two clocks. A stopped director is the one state that provably needs this
        // bridge: nothing else turns it into a playing scene (analysis/cutscene-rig-play).
        if (!_localTakenOver && !_localPlaying && now - _localProgress >= LocalPlaybackGraceSeconds)
        {
            _localTakenOver = true;
            
        }

        // (analysis/cutscene-rig-play, the "never lock the player" net) A director that was stopped once and
        // never moves again, on a bridge that owns the tick: driving the native step forever would only keep
        // the cutscene camera alive. After LocalStopWatchdogSeconds of that state the bridge ends the local
        // scene through the native StopCutscene - the call the skip path uses - so the camera and the input of
        // this machine's player are restored instead of staying in a cutscene that does not play.
        if (_localTakenOver)
        {
            if (!_localPlaying && _localPlayed && _localDirectorTime >= 0d)
            {
                if (!_localStoppedSeen)
                {
                    _localStoppedSeen = true;
                    _localStoppedSince = now;
                }
                else if (now - _localStoppedSince >= LocalStopWatchdogSeconds)
                {
                    ForceStopLocal(client, cutscene, "the local director was stopped and never moved again for "
                        + LocalStopWatchdogSeconds + "s while the bridge owned the tick");
                    return;
                }
            }
            else _localStoppedSeen = false;
        }

        // (analysis/cutscene-rig-play) The second way a player stays locked: the host ended the process (a skip
        // vote, the viewer fallback, the mission ending) while this machine's controller still holds it. The
        // native step is what clears that state, so it is checked here too - a controller that owns a process
        // the host no longer knows about is released through the native StopCutscene above.
        var hosted = true;
        try
        {
            var server = world.CutscenesServerController;
            hosted = server?._cutsceneProcesses == null || server._cutsceneProcesses.ContainsKey(processId);
        }
        catch (Exception) { }
        if (hosted) { _staleProcessSeen = false; }
        else if (!_staleProcessSeen) { _staleProcessSeen = true; _staleProcessSince = now; }
        if (_staleProcessSeen && now - _staleProcessSince >= LocalStopGraceSeconds && !_localPlaying)
        {
            ForceStopLocal(client, cutscene, "the host no longer owns process=" + processId + " ("
                + (now - _staleProcessSince).ToString("0.0") + "s) while this machine's controller still holds it and the "
                + "scene is not playing");
            return;
        }

        // (analysis/cutscene-client-native-trigger) The end half of a process this machine created itself: the
        // local viewer is reported through the native completion call, and a process the native end leaves behind
        // is closed here - the two steps that make the timeline's end actions (the door of the map) happen on
        // this machine instead of nowhere.
        CompleteLocalNativeProcess(world, client, cutscene, processId, now);

        if (!_localTakenOver || _localTickFrame == Time.frameCount) return;
        _localTickFrame = Time.frameCount;
        // Only set while this very call runs, so the tick counter can tell this call from the engine's.
        _insideBridgeClientCall = true;
        try { client.OnUpdate(TickDelta(Time.deltaTime)); }
        catch (Exception error)
        {
            // The tick is handed back to nobody and retried after the grace period, not every frame: a scene
            // whose start makes the native method throw must not turn into one exception per frame.
            _localTakenOver = false;
            _localProgress = Time.unscaledTime;
            if (Time.unscaledTime >= _nextLocalError)
            {
                _nextLocalError = Time.unscaledTime + 5f;
                FikaGlobals.LogWarning("CutsceneBoundary: CutscenesClientController.OnUpdate failed ("
                    + error.GetType().Name + ": " + error.Message + "); the bridge stops ticking the local scene here");
            }
        }
        finally { _insideBridgeClientCall = false; }
    }

    /// <summary>
    /// (analysis/cutscene-client-native-trigger) The end half of a cutscene process this machine created itself
    /// (see <see cref="FikaLocalCutsceneTrigger"/>). The native counter <c>playersCutsceneViewed</c> is written by
    /// one method only - <c>CutscenesServerController.PlayerViewCutsceneComplete</c> - and on a Fika client the
    /// chain that reaches it is replaced by the patch that turns the viewer's acknowledgement into a packet for
    /// the host (<c>RaidInteractionPatches.CutsceneViewCompleteSyncPatch</c>), a host that never started this
    /// process knows nothing about. Without this step the process of a locally triggered cutscene would stay at
    /// "0 of N watched" for the rest of the raid, and its native <c>EndCutscene</c> - the call the timeline's end
    /// actions (the door of the map) hang on - would never run.
    ///
    /// The viewer is reported once, through that same native method, as soon as the local scene of this very
    /// process has stopped playing. A process the native end does not close afterwards (a seat table holding a
    /// viewer that can never be counted) is closed by the bridge through the native <c>EndCutscene</c> the skip
    /// vote uses.
    /// </summary>
    private static void CompleteLocalNativeProcess(GameWorld world, CutscenesClientController client,
        string cutscene, int processId, float now)
    {
        if (!FikaLocalCutsceneTrigger.OwnsLocalProcess(processId, out _)) return;
        var server = world.CutscenesServerController;
        if (server?._cutsceneProcesses == null) return;
        if (!server._cutsceneProcesses.TryGetValue(processId, out var process) || process == null) return;
        var viewerCount = process.playersInCurrentView?.Count ?? 0;
        if (viewerCount == 0) return;
        var local = RaidInteractionSync.LocalPlayerId;
        if (local < 0) return;
        if (_localNativeCompleted == processId)
        {
            if (process.playersCutsceneViewed >= viewerCount) return;
            if (now - _localNativeCompletedAt < PostCompleteGraceSeconds) return;
            
            ForceEnd(server, processId, cutscene, "this machine's own process stayed alive " + PostCompleteGraceSeconds
                + "s after its viewer was reported through the native PlayerViewCutsceneComplete");
            return;
        }
        if (process.playersCutsceneViewed >= viewerCount) return;
        if (!_localPlayed || _localPlaying) return;
        if (now - _localProgress < TimelineStoppedGraceSeconds) return;
        _localNativeCompleted = processId;
        _localNativeCompletedAt = now;
        
        ReportLocalCompletion(server, process, cutscene, local,
            "the scene of this machine's own process (created by its own native trigger) stopped playing "
            + (now - _localProgress).ToString("0.00")
            + "s ago, and no host owns this process to hear the acknowledgement");
    }

    /// <summary>
    /// Repairs the state a cutscene start event leaves behind when nobody consumes it: the controller holds a
    /// pending start (<c>_needStartTimeline</c> / <c>_waitFramesLeft</c>) and owns no process yet, which is
    /// why every process-based path of this bridge - the dispatch, the advance and the local tick - is still
    /// inactive. The per-frame <c>OnUpdate</c> is the only consumer of that state, so this is the one place
    /// where driving it matters even before a process exists.
    /// </summary>
    private static bool PumpPendingStart(CutscenesClientController client, float now)
    {
        bool pending;
        string cutscene;
        int waitFrames;
        try
        {
            pending = client._needStartTimeline || client._waitFramesLeft > 0;
            cutscene = client._cutsceneId;
            waitFrames = client._waitFramesLeft;
        }
        catch (Exception error)
        {
            ReportPending(4, null, "the start state of the local cutscene controller could not be read ("
                + error.GetType().Name + ": " + error.Message + "), so the bridge cannot tell whether a start is pending here");
            return false;
        }
        if (!pending)
        {
            _localPendingSince = 0f;
            // The verdict of the branch that decides whether the client ever enters a scene: the native start
            // event only sets this state, and this machine holding none of it is the proof that the event either
            // never arrived or was filtered out by the native handler (see the phase=dispatch line of the drain).
            ReportPending(0, cutscene, "the local cutscene controller holds no start request (needStartTimeline=false,"
                + " waitFrames=0); nothing of the host's cutscene is pending on this machine");
            return false;
        }
        if (_localPendingSince <= 0f) _localPendingSince = now;
        // (analysis/perf-diagnostics-cleanup) This entry point is reached once per frame while the local
        // controller holds an unconsumed start, so the report is built only while the probes are on.
        
        if (now - _localPendingSince < LocalPendingStartGraceSeconds)
        {
            ReportPending(1, cutscene, "cutscene=" + cutscene + " waitFrames=" + waitFrames + " the local controller holds a"
                + " start request and the engine is given " + LocalPendingStartGraceSeconds.ToString("0.0")
                + "s before this bridge ticks the controller itself");
            return true;
        }
        if (_pendingTickFrame == Time.frameCount) return true;
        _pendingTickFrame = Time.frameCount;
        _insideBridgeClientCall = true;
        try { client.OnUpdate(TickDelta(Time.deltaTime)); }
        catch (Exception error)
        {
            // Retried after a pause instead of every frame: a pending start that makes the native method
            // throw must not turn into one exception per frame.
            _localPendingSince = Time.unscaledTime;
            ReportPending(5, cutscene, "cutscene=" + cutscene + " waitFrames=" + waitFrames
                + " the bridge ticked CutscenesClientController.OnUpdate and it threw ("
                + error.GetType().Name + ": " + error.Message + "); the pending start stays and is retried after a pause");
            if (Time.unscaledTime >= _nextLocalError)
            {
                _nextLocalError = Time.unscaledTime + 5f;
                FikaGlobals.LogWarning("CutsceneBoundary: CutscenesClientController.OnUpdate failed while consuming a "
                    + "pending start (" + error.GetType().Name + ": " + error.Message + "); the bridge retries after a pause");
            }
        }
        finally { _insideBridgeClientCall = false; }
        ReportPending(2, cutscene, "cutscene=" + cutscene + " waitFrames=" + waitFrames + " the bridge ticked"
            + " CutscenesClientController.OnUpdate to consume the start the engine left behind; if the controller owns a"
            + " process now, the scene of this machine is on and the phase=play-local line of the bridge reports it");
        return true;
    }

    /// <summary>
    /// (analysis/cutscene-client-dispatch) One ungated line per distinct pending verdict of this machine. The
    /// verdict is a small code that is compared before any string is built, so the per-frame caller (a controller
    /// that holds no start at all is the common case in a raid without a cutscene) costs one int compare; a change
    /// writes one line and then nothing until the state changes again.
    /// </summary>
    private static void ReportPending(int code, string cutscene, string detail)
    {
        // (analysis/cutscene-client-native-trigger) The verdict is (code, cutscene), not the code alone: the start
        // the controller holds is reported once per state, and a log that only carried the first of them showed
        // 'cutscene=' - the empty id of the loading state - for a whole raid in which the id did change. One int
        // and one string compare per frame keep the cost where it was.
        if (_pendingVerdict == code && string.Equals(_pendingVerdictCutscene, cutscene, StringComparison.Ordinal)) return;
        _pendingVerdict = code;
        _pendingVerdictCutscene = cutscene;
        
    }

    /// <summary>
    /// One sample of this machine's own scene: the playable director of the current cutscene, and whether its
    /// time moved. A director whose time does not move is either not started yet (the state that never resolves
    /// without <c>OnUpdate</c>) or the rig object it sits on is switched off.
    /// </summary>
    private static void SampleLocalScene(CutscenesClientController client, string cutscene)
    {
        if (_localDirector == null || _localDirector.Pointer == IntPtr.Zero)
        {
            _localDirector = FindLocalDirector(client, cutscene);
            _localScene = SceneOf(client, cutscene);
        }
        if (_localDirector == null || _localDirector.Pointer == IntPtr.Zero) return;
        var now = Time.unscaledTime;
        var time = _localDirector.time;
        if (time > _localDirectorTime)
        {
            _localDirectorTime = time;
            _localProgress = now;
            _localPlayed = true;
        }
        _localPlaying = _localDirector.state == PlayState.Playing;
        if (_localPlaying)
        {
            _localProgress = now;
            _localPlayed = true;
        }
    }

    /// <summary>
    /// The rescue of this machine's own scene (analysis/cutscene-rig-play). The round-17 solo log left exactly
    /// two facts: the scene object is there - scene=Icebreaker_cutscene_01 director=Paused:0.0/29.2 - and
    /// it never starts, while the rig of the timeline stays at 0/1 because SPT switches those objects off
    /// at raid start and only the native start of the timeline switches them back on. Driving the controller's
    /// own per-frame method was therefore not enough: 118 s of it changed nothing. The rescue enters the native
    /// playback itself, in this order:
    /// <list type="number">
    /// <item><b>the native entry point</b> - CutscenesClientController.StartShowTimeline(cutsceneId, 0),
    /// the method that turns the loaded timeline into a playing scene (it is what raises
    /// TimelineShowed at its end, the only writer of the viewer completion). It is private, so it is
    /// reached through reflection - the same way Fika already calls the private StopCutscene;</item>
    /// <item><b>the native setup every playback runs</b> - SetupSignalTracks(timeline, director), which
    /// is also the one method SPT's reveal patch hangs on, followed by the director's own Play();</item>
    /// <item><b>the rig itself</b> - the timelineObjects of the timeline are switched on when they are
    /// still off after the two steps above, because those objects are the scene (camera, actors, props) and the
    /// native reveal cannot run for a timeline that never started. Nothing is created or destroyed here.</item>
    /// </list>
    /// Never more than <see cref="SceneRescueMaxAttempts"/> times, and never while the scene plays: as soon as
    /// the director is playing this method is not called at all.
    /// </summary>
    private static void TryRescueLocalScene(CutscenesClientController client, string cutscene, int processId)
    {
        if (!Enabled || !RepairLocalPlayback || !RescueLocalScene || client == null || _localPlaying) return;
        var now = Time.unscaledTime;
        if (now - _localProgress < SceneRescueGraceSeconds) return;
        if (now < _nextSceneRescue) return;

        // (analysis/cutscene-slowmo) Native first, and only once: while this controller's own start is still in
        // flight, the native chain owns the scene. Entering it through StartShowTimeline in that state builds
        // the timeline a second time and starts the cutscene audio a second time - which is one of the two ways
        // a picture and its sound drift apart (the other is a feed of scaled deltas, see TickDelta). The wait
        // is bounded by NativeStartWindowSeconds, so a start that never lands still gets the rescue.
        if (now - _localSeen < NativeStartWindowSeconds && NativeStartInFlight(client, out var needStart, out var waitFrames))
        {
            if (!_sceneRescueNativeDeferred)
            {
                _sceneRescueNativeDeferred = true;
                
            }
            return;
        }

        if (!_sceneRescueTried && _sceneRescueAttempts == 0)
        {
            _sceneRescueAttempts = 1;
            
        }
        else
        {
            _sceneRescueAttempts++;
            if (_sceneRescueAttempts > SceneRescueMaxAttempts)
            {
                if (!_sceneRescueTried)
                {
                    _sceneRescueTried = true;
                    
                }
                return;
            }
        }
        _nextSceneRescue = now + SceneRescueRetrySeconds;

        var director = _localDirector;
        if (director == null || director.Pointer == IntPtr.Zero)
        {
            
            _sceneRescueTried = true;
            return;
        }

        var staged = new System.Text.StringBuilder();
        var native = TryShowTimeline(client, cutscene, staged);

        if (!native)
        {
            var timeline = SceneTimeline(client, cutscene);
            if (timeline == null)
            {
                staged.Append(" the timeline asset of this cutscene is not readable (no SetupSignalTracks possible);");
            }
            else
            {
                var setup = TryInvoke(typeof(CutscenesClientController), "SetupSignalTracks", client,
                    new object[] { timeline, director }, out var setupError);
                staged.Append(" SetupSignalTracks=").Append(setup ? "ok" : "failed" + Suffix(setupError)).Append(';');
            }
            TryRebuildGraph(director, staged);
            var playable = director.playableAsset;
            if (playable != null && playable.Pointer != IntPtr.Zero)
            {
                try
                {
                    director.Play();
                    staged.Append(" Play() issued;");
                }
                catch (Exception error)
                {
                    staged.Append(" Play() failed (").Append(error.GetType().Name).Append(": ")
                        .Append(error.Message).Append(");");
                }
            }
            else staged.Append(" the director has no playable asset, so it cannot play;");
        }

        var activated = ActivateRig(client, cutscene, staged);
        
        _sceneRescueTried = true;

        SampleLocalScene(client, cutscene);
        if (_localPlaying)
        {
            _sceneRescueDone = true;
            
        }
    }

    /// <summary>
    /// The native playback entry point of the client controller:
    /// CutscenesClientController.StartShowTimeline(String, Single). Returns true when the call returned
    /// without throwing, i.e. the native side accepted the start; the caller still samples the director, since
    /// the scene can start a frame later.
    /// </summary>
    private static bool TryShowTimeline(CutscenesClientController client, string cutscene, System.Text.StringBuilder staged)
    {
        try
        {
            _showTimeline ??= typeof(CutscenesClientController).GetMethod("StartShowTimeline",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (_showTimeline == null)
            {
                staged.Append(" StartShowTimeline is not reachable in this build;");
                return false;
            }
            // (analysis/cutscene-slowmo) The time parameter is the controller's own start time - the value the
            // native start event wrote into _timelineTimeStart - and not a constant of this bridge: it is the
            // one parameter of the native entry point that decides where in the timeline the scene begins, so a
            // hard-coded 0 would restart a timeline the native chain had already positioned.
            var startTime = NativeStartTime(client);
            _showTimeline.Invoke(client, new object[] { cutscene, startTime });
            staged.Append(" StartShowTimeline(").Append(cutscene).Append(", ").Append(startTime.ToString("0.00"))
                .Append(") returned;");
            return true;
        }
        catch (Exception error)
        {
            staged.Append(" StartShowTimeline failed").Append(Suffix(Describe(error))).Append(';');
            return false;
        }
    }

    /// <summary>
    /// (analysis/cutscene-slowmo) Whether this controller stands between the native start event and its timeline:
    /// the two fields the event handler writes and only the controller's own per-frame step consumes
    /// (<c>_needStartTimeline</c> / <c>_waitFramesLeft</c>). Reading them fails safe - a controller that cannot be
    /// read is not reported as «in flight», so the rescue keeps its old behaviour.
    /// </summary>
    private static bool NativeStartInFlight(CutscenesClientController client, out bool needStart, out int waitFrames)
    {
        needStart = false;
        waitFrames = -1;
        try
        {
            needStart = client._needStartTimeline;
            waitFrames = client._waitFramesLeft;
            return needStart || waitFrames > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// (analysis/cutscene-slowmo) The time the native timeline of this machine starts at: <c>_timelineTimeStart</c>,
    /// written by the native start event handler (<c>start.initTime</c>). It is passed on to
    /// <c>StartShowTimeline(cutsceneId, time)</c> unchanged, so the bridge never repositions a timeline the native
    /// chain has already placed; an unreadable controller reports 0, which is the value the bridge used before.
    /// </summary>
    private static float NativeStartTime(CutscenesClientController client)
    {
        try { return client._timelineTimeStart; }
        catch (Exception) { return 0f; }
    }

    /// <summary>The stop of the native skip path: CutscenesClientController.StopCutscene(director, skip).</summary>
    private static bool TryStopCutscene(CutscenesClientController client, PlayableDirector director, bool skip, out string error)
    {
        try
        {
            _stopCutscene ??= typeof(CutscenesClientController).GetMethod("StopCutscene",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (_stopCutscene == null)
            {
                error = "StopCutscene is not reachable in this build";
                return false;
            }
            _stopCutscene.Invoke(client, new object[] { director, skip });
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name + ": " + exception.Message;
            return false;
        }
    }

    private static bool TryRebuildGraph(PlayableDirector director, System.Text.StringBuilder staged)
    {
        try
        {
            _rebuildGraph ??= typeof(PlayableDirector).GetMethod("RebuildGraph", Type.EmptyTypes);
            if (_rebuildGraph == null) return false;
            _rebuildGraph.Invoke(director, null);
            staged.Append(" RebuildGraph=ok;");
            return true;
        }
        catch (Exception error)
        {
            staged.Append(" RebuildGraph failed").Append(Suffix(Describe(error))).Append(';');
            return false;
        }
    }

    private static bool TryInvoke(Type type, string name, object instance, object[] arguments, out string error)
    {
        try
        {
            var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
            {
                error = name + " is not reachable in this build";
                return false;
            }
            method.Invoke(instance, arguments);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name + ": " + exception.Message;
            return false;
        }
    }

    private static string Suffix(string error) => string.IsNullOrEmpty(error) ? string.Empty : " (" + error + ")";

    private static string Describe(Exception error) => error == null ? null : error.GetType().Name + ": " + error.Message;

    /// <summary>
    /// Switches the timeline rig of <paramref name="cutscene"/> on when it is still off. Those objects are the
    /// scene itself: SPT's timeline-bank scan hides them at raid start and only the native start of a timeline
    /// reveals them again, which is why a scene that never plays renders nothing at all.
    /// </summary>
    private static int ActivateRig(CutscenesClientController client, string cutscene, System.Text.StringBuilder staged)
    {
        try
        {
            var objects = RigObjects(client, cutscene);
            if (objects == null)
            {
                staged.Append(" the rig of this cutscene is not resolvable;");
                return -1;
            }
            var changed = 0;
            for (var i = 0; i < objects.Count; i++)
            {
                var item = objects[i];
                if (item == null || item.activeSelf) continue;
                item.SetActive(true);
                changed++;
            }
            staged.Append(" rig objects activated=").Append(changed).Append('/').Append(objects.Count).Append(';');
            return changed;
        }
        catch (Exception error)
        {
            staged.Append(" activating the rig failed").Append(Suffix(Describe(error))).Append(';');
            return -1;
        }
    }

    /// <summary>
    /// The timeline asset of a cutscene (the first argument of the native SetupSignalTracks), read from the
    /// controller's current timeline or from the map's bank.
    /// </summary>
    private static UnityEngine.Timeline.TimelineAsset SceneTimeline(CutscenesClientController client, string cutscene)
    {
        try
        {
            if (string.Equals(client._currentTimelineData.cutsceneId, cutscene, StringComparison.Ordinal))
                return client._currentTimelineData.timeline;
            var timelines = client._timelinesMapBank?.timelines;
            for (var i = 0; i < (timelines?.Count ?? 0); i++)
            {
                var timeline = timelines[i];
                if (string.Equals(timeline.cutsceneId, cutscene, StringComparison.Ordinal)) return timeline.timeline;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>
    /// Whether the timeline rig of a cutscene is switched on, and how many of its objects are. This is the one
    /// fact that separates "the scene is running but invisible" from "the timeline never started".
    /// </summary>
    private static bool LocalSceneVisible(CutscenesClientController client, string cutscene)
    {
        try
        {
            var objects = RigObjects(client, cutscene);
            return objects != null && objects.Count > 0 && objects[0] != null && objects[0].activeSelf;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// The scene name of a cutscene: the controller's current timeline when it is that cutscene, otherwise the
    /// map's bank (the same lookup the director is found with).
    /// </summary>
    private static string SceneName(CutscenesClientController client, string cutscene)
    {
        if (!string.IsNullOrEmpty(_localScene)) return _localScene;
        try
        {
            if (string.Equals(client._currentTimelineData.cutsceneId, cutscene, StringComparison.Ordinal))
                return client._currentTimelineData.sceneName;
        }
        catch (Exception) { }
        return SceneOf(client, cutscene);
    }

    /// <summary>
    /// The one line that separates the four ways a cutscene can stay invisible (analysis/cutscene-rig-play):
    /// the rig is off (rig=0/1), the director never started (director=Paused:0.0/29.2), the camera never took
    /// over (cam=none while the rig is on) or the layer of the rig is not rendered. It shows the numbers behind
    /// it, not only the verdict: the controller's scene name, the director's state, time and duration, whether
    /// its playable graph is valid, the active rig objects, the cutscene camera of the scene, the UI state count
    /// and the pending start of the controller (start=pending|done:frames). It is a probe, so it repeats every
    /// <see cref="SceneDiagSeconds"/> seconds while a scene is on.
    /// </summary>
    private static string LocalScene(CutscenesClientController client, string cutscene)
    {
        var play = _localDirector;
        if (play != null && play.Pointer == IntPtr.Zero) play = null;
        var graphValid = false;
        if (play != null)
        {
            try { graphValid = play.playableGraph.IsValid(); }
            catch (Exception) { }
        }
        var playState = play == null
            ? "none"
            : play.state + ":" + play.time.ToString("0.0") + "/" + play.duration.ToString("0.0")
                + (graphValid ? "+graph" : "+no-graph");
        string rig;
        try
        {
            var objects = RigObjects(client, cutscene);
            var active = 0;
            for (var i = 0; i < (objects?.Count ?? 0); i++)
            {
                var item = objects[i];
                if (item != null && item.activeSelf) active++;
            }
            rig = objects == null ? "none" : active + "/" + objects.Count;
        }
        catch (Exception) { rig = "?"; }
        string ui;
        try { ui = client._gameUIChildrenActiveStates == null ? "none" : client._gameUIChildrenActiveStates.Count.ToString(); }
        catch (Exception) { ui = "?"; }
        string start;
        try { start = (client._needStartTimeline ? "pending" : "done") + ":" + client._waitFramesLeft; }
        catch (Exception) { start = "?"; }
        return "scene=" + (SceneName(client, cutscene) ?? "-") + " director=" + playState + " rig=" + rig
            + " cam=" + Camera() + " ui=" + ui + " start=" + start + " rescue=" + RescueState();
    }

    /// <summary>
    /// What the scene rescue did so far, for the scene line: <c>none</c> before it ran, <c>tried:N</c> while it
    /// did not start a scene, <c>started</c> once it did and <c>gave-up</c> when its attempts are used up - the
    /// last two are the states in which only the watchers can end the cutscene.
    /// </summary>
    private static string RescueState()
    {
        if (_sceneRescueDone) return "started";
        if (_sceneRescueAttempts == 0) return "none";
        return _sceneRescueTried ? "gave-up" : "tried:" + _sceneRescueAttempts;
    }

    /// <summary>The cutscene camera of the loaded scene: the first active camera under it, or none.</summary>
    private static string Camera()
    {
        try
        {
            var scene = _localScene;
            if (string.IsNullOrEmpty(scene)) return "none";
            var found = UnityEngine.SceneManagement.SceneManager.GetSceneByName(scene);
            if (!found.IsValid() || !found.isLoaded) return "none";
            foreach (var root in found.GetRootGameObjects())
            {
                foreach (var camera in root.GetComponentsInChildren<UnityEngine.Camera>(true))
                {
                    if (camera == null || !camera.gameObject.activeInHierarchy) continue;
                    return camera.enabled ? "active" : "off";
                }
            }
            return "none";
        }
        catch (Exception) { return "?"; }
    }

    /// <summary>
    /// The once-per-<see cref="SceneDiagSeconds"/> verdict line of a scene that is on (analysis/cutscene-rig-play):
    /// it names the reason the scene cannot be seen, which is what a single grep of CutsceneDiag has to
    /// answer after the next raid.
    /// </summary>
    private static void ReportScene(CutscenesClientController client, string cutscene, bool playing)
    {
        // (analysis/perf-diagnostics-cleanup) Purely a report: LocalSceneVisible() reads the rig objects of
        // the scene through il2cpp and LocalScene() builds a longer string, so with the master switch off the
        // method returns before either.
        
        var now = Time.unscaledTime;
        if (now < _nextSceneDiag) return;
        _nextSceneDiag = now + SceneDiagSeconds;
        var visible = LocalSceneVisible(client, cutscene);
        string reason;
        if (playing) reason = "the timeline is playing; the fields above name what is on screen";
        else if (!visible) reason = "the timeline never started: the rig of this cutscene is still switched off, so the "
            + "scene cannot render a single frame (the state the native start of a timeline repairs)";
        else if (_localDirector == null || _localDirector.Pointer == IntPtr.Zero)
            reason = "the rig is on but no playable director of this cutscene exists, so nothing drives the timeline";
        else reason = "the rig is on and the director exists, but the director is not playing and its time does not "
            + "advance (the scene stays invisible until the director plays)";
        
    }

    /// <summary>
    /// Ends this machine's own scene through the native skip call (StopCutscene): the camera and the input
    /// of the local player are restored by the native body and the controller is left without a process, so no
    /// cutscene state can keep this machine's player locked. Only ever called when the scene provably does not
    /// play, so it can never cut a scene that is running.
    /// </summary>
    private static void ForceStopLocal(CutscenesClientController client, string cutscene, string reason)
    {
        var director = _localDirector;
        var stopped = false;
        string error = null;
        if (director != null && director.Pointer != IntPtr.Zero)
            stopped = TryStopCutscene(client, director, false, out error);
        if (!stopped && client != null && client._actions != null)
            TryInvoke(typeof(CutscenesClientController), "DeinitAllActions", client, null, out error);
        
        _localProcess = 0;
        _localTakenOver = _localPlaying = _localPlayed = false;
        _localDirector = null;
        _localScene = null;
        _localDirectorTime = -1d;
        _localStoppedSeen = false;
    }

    /// <summary>
    /// What the sample of this machine's own skip prompt found (analysis/cutscene-skip-overlay). Every field is
    /// read out of the native objects; nothing here writes a state.
    /// </summary>
    private struct SkipPromptSample
    {
        /// <summary>The prompt is on screen: its object is active and one canvas of it is not faded out.</summary>
        internal bool Shown;
        /// <summary><c>CutsceneSkipUI._skipActive</c> - the panel's own "this machine is voting" flag.</summary>
        internal bool Active;
        /// <summary>The cutscene screen controller of this machine's controller exists.</summary>
        internal bool Screen;
        /// <summary>Alpha of the info canvas, the one that carries "{0}/{1} players want to skip".</summary>
        internal float Info;
        /// <summary>Alpha of the button canvas, the one that carries "hold ESC to skip".</summary>
        internal float Button;

        /// <summary>The fade state of the two canvases, as one token for the line.</summary>
        internal string Fade => Info.ToString("0.00") + "/" + Button.ToString("0.00");
    }

    /// <summary>
    /// The skip prompt of this machine's own viewer (analysis/cutscene-skip-overlay).
    ///
    /// The screenshot of the 2026-10-13 00:4x raid shows the two halves of the native prompt
    /// (<c>EFT.UI.CutsceneSkipUI</c>) still on screen after the cutscene was over: the left label
    /// ("{0}/{1} players want to skip") and the right button ("hold ESC to skip"). The controller puts the
    /// prompt up through <c>ShowCutsceneSkipUI()</c> and takes it down through
    /// <c>HideCutsceneSkipUI(bool notifyServer)</c>; while this machine owns a cutscene process the prompt
    /// belongs on screen and is only reported here (that line is the proof it was up during the scene).
    ///
    /// As soon as the controller owns no process any more, a prompt that is still up (or a
    /// <c>_skipUiActive</c> that is still set) is the one state this raid ended in: it is taken down through
    /// the native hiding method, and the sample is read back afterwards, so the line says <c>cleared=yes|no</c>
    /// instead of leaving the question to the next screenshot.
    /// </summary>
    private static void PumpSkipOverlay(GameWorld world)
    {
        if (!Enabled || !DismissSkipOverlay || world == null) return;
        CutscenesClientController client;
        try { client = world.CutscenesClientController; }
        catch (Exception) { return; }
        if (client == null) return;

        var now = Time.unscaledTime;
        if (now < _nextOverlaySample) return;
        _nextOverlaySample = now + OverlaySampleSeconds;

        int processId;
        bool flagged;
        string cutscene;
        try
        {
            processId = client._cutsceneProcessId;
            flagged = client._skipUiActive;
            cutscene = client._cutsceneId;
        }
        catch (Exception) { return; }
        if (string.IsNullOrEmpty(cutscene)) cutscene = "-";

        var prompt = SampleSkipPrompt(client);

        if (processId != 0)
        {
            // The prompt of a running scene belongs on screen: this machine's viewer is being asked whether it
            // wants to skip. The sample is kept, so the case below can name the process a stuck prompt belonged
            // to, and the line proves the prompt was really up while the scene ran.
            if (flagged || prompt.Shown)
            {
                _overlaySeen = true;
                _overlayCutscene = cutscene;
                
                
            }
            return;
        }

        if (!flagged && !prompt.Shown)
        {
            // Nothing of this kind is up any more (the native side took it down with the process, or this
            // machine never showed one): there is nothing to do and nothing to report per frame.
            _overlaySeen = false;
            _overlayCutscene = null;
            return;
        }

        if (now < _nextOverlayAttempt) return;
        _nextOverlayAttempt = now + OverlayRetrySeconds;
        // The prompt is remembered by the cutscene it belonged to: the controller clears its own id with the
        // process, so the remembered one is what names the scene in the line below.
        var known = string.IsNullOrEmpty(_overlayCutscene) ? cutscene : _overlayCutscene;
        DismissSkipPrompt(client, known, prompt, _overlaySeen
            ? "the prompt of the finished cutscene " + known + " is still up (this machine's controller owns no process any more)"
            : "this machine's controller owns no process while its skip flag is still set");
        _overlaySeen = false;
        _overlayCutscene = null;
    }

    /// <summary>
    /// Reads the on-screen state of this machine's skip prompt. Read-only and fully guarded: the prompt of a
    /// torn-down scene is a Unity object that may already be destroyed, and every read of it can throw across
    /// the interop boundary - a failed read answers "hidden" and never breaks the raid tick.
    /// </summary>
    private static SkipPromptSample SampleSkipPrompt(CutscenesClientController client)
    {
        SkipPromptSample sample;
        try { sample = SampleSkipPanel(client._cutsceneSkipUI); }
        catch (Exception) { sample = new SkipPromptSample(); }
        try { sample.Screen = client._cutsceneScreenController != null; }
        catch (Exception) { }
        return sample;
    }

    /// <summary>The on-screen state of one prompt object, without its controller (read-only, guarded).</summary>
    private static SkipPromptSample SampleSkipPanel(EFT.UI.CutsceneSkipUI panel)
    {
        var sample = new SkipPromptSample();
        try
        {
            if (panel == null) return sample;
            sample.Active = panel._skipActive;
            var info = panel._skipCanvas;
            if (info != null) sample.Info = info.alpha;
            var button = panel._skipButtonCanvas;
            if (button != null) sample.Button = button.alpha;
            var active = true;
            var host = panel.gameObject;
            if (host != null) active = host.activeInHierarchy;
            sample.Shown = active && (sample.Active || sample.Info > 0.01f || sample.Button > 0.01f);
        }
        catch (Exception) { }
        return sample;
    }

    /// <summary>The controller's own "the prompt is up" flag, read with a fallback when its object is gone.</summary>
    private static bool ReadSkipFlag(CutscenesClientController client)
    {
        try { return client != null && client._skipUiActive; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Takes the skip prompt of this machine down, natively first, and reports what the three steps left behind
    /// (analysis/cutscene-skip-overlay). The order is deliberate:
    /// <list type="number">
    /// <item><c>CutscenesClientController.HideCutsceneSkipUI(false)</c> - the method the native teardown of a
    /// process owes and the one call this port's completion branch replaced (<c>TimelineShowed</c>, see
    /// <c>RaidInteractionPatches.CutsceneViewCompleteSyncPatch</c>). <c>notifyServer: false</c> keeps a prompt
    /// of a finished process from turning into a skip intention on the wire.</item>
    /// <item><c>EFT.UI.CutsceneSkipUI.Close()</c> - the prompt's own public hide, if step one left it on
    /// screen (a prompt whose controller flag was already cleared is only reachable this way).</item>
    /// <item>The controller flag itself, if the prompt is gone but <c>_skipUiActive</c> survived: a flag that
    /// stays set keeps the prompt away from the next cutscene of the raid, so this is the last thing cleared -
    /// and only after the prompt is off screen.</item>
    /// </list>
    /// </summary>
    private static void DismissSkipPrompt(CutscenesClientController client, string cutscene,
        SkipPromptSample before, string reason)
    {
        _overlayAttempts++;
        var staged = new System.Text.StringBuilder();

        try
        {
            client.HideCutsceneSkipUI(false);
            staged.Append(" HideCutsceneSkipUI(false);");
        }
        catch (Exception error)
        {
            staged.Append(" HideCutsceneSkipUI failed").Append(Suffix(Describe(error))).Append(';');
        }

        var after = SampleSkipPrompt(client);
        if (after.Shown)
        {
            try
            {
                var panel = client._cutsceneSkipUI;
                if (panel != null)
                {
                    panel.Close();
                    staged.Append(" CutsceneSkipUI.Close();");
                }
                else staged.Append(" no prompt object to close;");
            }
            catch (Exception error)
            {
                staged.Append(" CutsceneSkipUI.Close failed").Append(Suffix(Describe(error))).Append(';');
            }
            after = SampleSkipPrompt(client);
        }

        var flagged = ReadSkipFlag(client);
        if (flagged && !after.Shown)
        {
            try
            {
                client._skipUiActive = false;
                staged.Append(" _skipUiActive=false;");
            }
            catch (Exception error)
            {
                staged.Append(" clearing _skipUiActive failed").Append(Suffix(Describe(error))).Append(';');
            }
            flagged = ReadSkipFlag(client);
        }

        var cleared = !after.Shown && !flagged;
        var detail = "skip=" + (after.Shown ? "shown" : "hidden") + " fade=" + after.Fade + " screen="
            + (after.Screen ? "present" : "absent") + " ui=" + (flagged ? "active" : "inactive") + " panel="
            + (after.Active ? "active" : "inactive") + " process=0 was=" + before.Fade + " attempts="
            + _overlayAttempts + " -> cleared=" + (cleared ? "yes" : "no") + " " + reason + ";"
            + staged;
        
        
    }

    // =====================================================================================================
    // BG (analysis/terminal-ending-settlement): the personal ending of the final mission, sampled, entered and
    // ended through the same native calls that drive a story scene.
    //
    // The native ActivateCutsceneForPlayer plays that ending as a *client-only* cutscene of this machine's own
    // viewer; the port reproduces it exactly (FikaFinalMissionDirector.StartLocalEnding raises the same
    // CutSceneStartEvent with clientOnly=true and processId=0), so this machine's controller owns NO
    // CutscenesServerController process for it. Every pump of this bridge keys on that process id
    // (FikaLocalCutsceneTrigger.OwnsLocalProcess, _cutsceneProcesses, PlayerViewCutsceneComplete) and therefore
    // stood aside for exactly this one scene: nothing sampled it, nothing entered it when its director stayed
    // idle, and nothing ended it when it was over. That is the state the raid of 2026-10-13 leaves behind - the
    // ending picture on screen and no raid end after it.
    //
    // The four helpers below are that missing half. They use the same lookups as the process-based paths above
    // (FindLocalDirector tries the cutscene id and the scene name of its bank entry) and touch no process table,
    // no viewer counter and no packet: outcome and exit status stay owned by the director.
    // =====================================================================================================

    /// <summary>One sample of a scene this machine plays without owning a cutscene process.</summary>
    internal struct LocalSceneSample
    {
        /// <summary>A playable director of that cutscene exists on this machine.</summary>
        internal bool Director;

        /// <summary>The director is in <c>PlayState.Playing</c>.</summary>
        internal bool Playing;

        /// <summary>The rig objects of the timeline are on, i.e. the scene can render.</summary>
        internal bool Visible;

        /// <summary>The director's own clock and the length of its timeline (0 when unreadable).</summary>
        internal double Time;
        internal double Duration;

        /// <summary>The scene the timeline of that cutscene lives in.</summary>
        internal string Scene;

        /// <summary>Why the sample could not be taken, or null when it could.</summary>
        internal string Error;
    }

    /// <summary>
    /// Samples the scene of <paramref name="cutscene"/> on this machine, for a caller that owns no process for
    /// it. Read-only: no native method is called, nothing is started or stopped.
    /// </summary>
    internal static LocalSceneSample SampleSceneOf(string cutscene)
    {
        var sample = new LocalSceneSample();
        try
        {
            var client = RaidInteractionSync.World?.CutscenesClientController;
            if (client == null)
            {
                sample.Error = "no client cutscene controller";
                return sample;
            }

            sample.Scene = SceneOf(client, cutscene);
            var director = FindLocalDirector(client, cutscene);
            if (director == null || director.Pointer == IntPtr.Zero)
            {
                sample.Error = "no playable director of '" + cutscene + "'";
                return sample;
            }

            sample.Director = true;
            sample.Playing = director.state == PlayState.Playing;
            sample.Time = director.time;
            sample.Duration = director.duration;
            sample.Visible = LocalSceneVisible(client, cutscene);
        }
        catch (Exception error)
        {
            sample.Error = Describe(error);
        }

        return sample;
    }

    /// <summary>
    /// Enters the native playback of a scene this machine owns no process for - the same native order the
    /// process-based rescue uses: the controller's own entry point first, then the signal-track setup with the
    /// director's <c>Play()</c>, then the timeline rig. Returns a one-line verdict for the [FinalDiag] line and
    /// never throws.
    /// </summary>
    internal static string RescueSceneOf(string cutscene)
    {
        try
        {
            var client = RaidInteractionSync.World?.CutscenesClientController;
            if (client == null) return "no-client-controller";
            var director = FindLocalDirector(client, cutscene);
            if (director == null || director.Pointer == IntPtr.Zero) return "no-director";
            var staged = new System.Text.StringBuilder();
            var native = TryShowTimeline(client, cutscene, staged);
            if (!native)
            {
                var timeline = SceneTimeline(client, cutscene);
                if (timeline == null) staged.Append(" timeline=none;");
                else
                {
                    var setup = TryInvoke(typeof(CutscenesClientController), "SetupSignalTracks", client,
                        new object[] { timeline, director }, out var setupError);
                    staged.Append(" SetupSignalTracks=").Append(setup ? "ok" : "failed" + Suffix(setupError)).Append(';');
                }

                TryRebuildGraph(director, staged);
                var playable = director.playableAsset;
                if (playable != null && playable.Pointer != IntPtr.Zero)
                {
                    try
                    {
                        director.Play();
                        staged.Append(" Play();");
                    }
                    catch (Exception error)
                    {
                        staged.Append(" Play failed").Append(Suffix(Describe(error))).Append(';');
                    }
                }
                else staged.Append(" no-playable-asset;");
            }

            var rig = ActivateRig(client, cutscene, staged);
            return (native ? "StartShowTimeline" : "staged") + " rig=" + rig + staged;
        }
        catch (Exception error)
        {
            return "failed " + Describe(error);
        }
    }

    /// <summary>
    /// Ends a scene this machine owns no process for, through the native call the skip path uses
    /// (<c>CutscenesClientController.StopCutscene</c>), with the action teardown as the fallback. Nothing is
    /// created or destroyed and no game data is written: the native body releases the cutscene camera and the
    /// local player. Returns a one-line verdict, never throws.
    /// </summary>
    internal static string StopSceneOf(string cutscene)
    {
        try
        {
            var client = RaidInteractionSync.World?.CutscenesClientController;
            if (client == null) return "no-client-controller";
            var director = FindLocalDirector(client, cutscene);
            if (director == null || director.Pointer == IntPtr.Zero)
            {
                var only = TryInvoke(typeof(CutscenesClientController), "DeinitAllActions", client, null, out var onlyError);
                return only ? "no-director actions=deinit" : "no-director actions=deinit-failed " + onlyError;
            }

            if (TryStopCutscene(client, director, false, out var error))
                return "StopCutscene(" + (SceneOf(client, cutscene) ?? "-") + ")";

            var deinit = TryInvoke(typeof(CutscenesClientController), "DeinitAllActions", client, null, out var deinitError);
            return "StopCutscene-failed" + Suffix(error) + " actions=" + (deinit ? "deinit" : "deinit-failed " + deinitError);
        }
        catch (Exception error)
        {
            return "failed " + Describe(error);
        }
    }

    /// <summary>
    /// Drives the controller's own per-frame step for a scene this machine owns no process for, exactly as the
    /// process-based pump does for a story scene. The native method that consumes a start event and follows the
    /// scene is the one step no engine caller reliably reaches on this port's raids
    /// (analysis/cutscene-view-and-end), so a caller that owns the scene must be able to run it. Returns the
    /// delta it was called with, or 0 when it could not be called.
    /// </summary>
    internal static float TickSceneOf()
    {
        var client = RaidInteractionSync.World?.CutscenesClientController;
        if (client == null) return 0f;
        var dt = TickDelta(Time.deltaTime);
        _insideBridgeClientCall = true;
        try
        {
            client.OnUpdate(dt);
        }
        catch (Exception error)
        {
            FikaGlobals.LogWarning("CutsceneBoundary: ticking the local controller for a process-less scene failed ("
                + error.GetType().Name + ": " + error.Message + ")");
            return 0f;
        }
        finally
        {
            _insideBridgeClientCall = false;
        }

        return dt;
    }

    /// <summary>
    /// The playable director the local controller plays <paramref name="cutscene"/> with. The controller keys
    /// its directors by the scene a rig lives in, so both the cutscene id and the scene name of its timeline
    /// entry are tried - the same lookup <c>FikaClient.TryGetDirector</c> performs when the host ends a
    /// cutscene, which is why a host-ended scene can always be stopped locally.
    /// </summary>
    private static PlayableDirector FindLocalDirector(CutscenesClientController client, string cutscene)
    {
        try
        {
            var directors = client._directorsByScene;
            if (directors == null || string.IsNullOrEmpty(cutscene)) return null;
            if (directors.TryGetValue(cutscene, out var byId) && byId != null && byId.Pointer != IntPtr.Zero)
                return byId;
            var scene = SceneOf(client, cutscene);
            if (!string.IsNullOrEmpty(scene) && directors.TryGetValue(scene, out var byScene) && byScene != null &&
                byScene.Pointer != IntPtr.Zero) return byScene;
        }
        catch (Exception error)
        {
            FikaGlobals.LogWarning("CutsceneBoundary: reading the local playable director failed ("
                + error.GetType().Name + ": " + error.Message + ")");
        }
        return null;
    }

    /// <summary>The scene the timeline of <paramref name="cutscene"/> lives in, or null when the bank has no entry.</summary>
    private static string SceneOf(CutscenesClientController client, string cutscene)
    {
        try
        {
            if (string.Equals(client._currentTimelineData.cutsceneId, cutscene, StringComparison.Ordinal))
                return client._currentTimelineData.sceneName;
            var timelines = client._timelinesMapBank?.timelines;
            for (var i = 0; i < (timelines?.Count ?? 0); i++)
            {
                var timeline = timelines[i];
                if (string.Equals(timeline.cutsceneId, cutscene, StringComparison.Ordinal)) return timeline.sceneName;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>The rig objects of a timeline: the scene objects SPTushonka switches off at raid start and the
    /// client controller switches back on when it sets the timeline's signal tracks up, i.e. the objects the
    /// scene itself is made of (the timeline camera among them).</summary>
    private static Il2CppSystem.Collections.Generic.List<GameObject> RigObjects(CutscenesClientController client, string cutscene)
    {
        if (string.Equals(client._currentTimelineData.cutsceneId, cutscene, StringComparison.Ordinal))
            return client._currentTimelineData.timelineObjects;
        var timelines = client._timelinesMapBank?.timelines;
        for (var i = 0; i < (timelines?.Count ?? 0); i++)
        {
            var timeline = timelines[i];
            if (string.Equals(timeline.cutsceneId, cutscene, StringComparison.Ordinal)) return timeline.timelineObjects;
        }
        return null;
    }

    /// <summary>What this machine's own scene looks like right now, for the local playback line.</summary>
    private static string Scene(CutscenesClientController client, string cutscene)
    {
        var director = _localDirector;
        var play = director == null || director.Pointer == IntPtr.Zero
            ? "none"
            : director.state + ":" + director.time.ToString("0.0") + "/" + director.duration.ToString("0.0");
        string rig;
        try
        {
            var objects = RigObjects(client, cutscene);
            var active = 0;
            for (var i = 0; i < (objects?.Count ?? 0); i++)
            {
                var item = objects[i];
                if (item != null && item.activeSelf) active++;
            }
            rig = objects == null ? "none" : active + "/" + objects.Count;
        }
        catch (Exception) { rig = "?"; }
        string ui;
        try { ui = client._gameUIChildrenActiveStates == null ? "none" : client._gameUIChildrenActiveStates.Count.ToString(); }
        catch (Exception) { ui = "?"; }
        string start;
        try { start = (client._needStartTimeline ? "pending" : "done") + ":" + client._waitFramesLeft; }
        catch (Exception) { start = "?"; }
        return "scene=" + (_localScene ?? "-") + " director=" + play + " rig=" + rig + " ui=" + ui + " start=" + start;
    }

    /// <summary>The duration of <paramref name="cutscene"/>'s timeline, or 0 when the bank has no entry for it.</summary>
    private static float TimelineDuration(CutscenesServerController server, string cutscene)
    {
        var timelines = server?._timelinesMapBank?.timelines;
        for (var i = 0; i < (timelines?.Count ?? 0); i++)
        {
            var timeline = timelines[i];
            if (string.Equals(timeline.cutsceneId, cutscene, StringComparison.Ordinal)) return timeline.timelineDuration;
        }
        return 0f;
    }

    /// <summary>What a verdict means, in one sentence (the WARN line of the probe).</summary>
    private static string Explain(string verdict) => verdict == "slow"
        ? "the picture advanced slower than real time: either the delta this machine feeds the controller is shorter "
            + "than the frame it belongs to (compare timeScale= with dt=, and see whether driver=both fed it twice) "
            + "or the clock every viewer is corrected to is slow itself (compare procRatio=)"
        : verdict == "fast"
            ? "the picture advanced faster than real time: two callers advanced one timeline (driver=both) or the "
                + "correction clock of the process runs ahead (procRatio= above 1.00)"
            : verdict == "stalled"
                ? "the scene is not playing and its time did not move: nothing inside the cutscene advances it"
                : verdict == "drift"
                    ? "the sound and the picture advanced at different rates in this second (aratio= against ratio=)"
                    : "no clock of this cutscene could be measured in this second";

    /// <summary>A rate, or n/a when the sample could not measure one.</summary>
    private static string Rate(double rate) => double.IsNaN(rate) ? "n/a" : rate.ToString("0.00");

    /// <summary>One advance of a clock between two samples, or n/a.</summary>
    private static string Advance(double advance) => double.IsNaN(advance) ? "n/a" : advance.ToString("0.00");

    /// <summary>The game clock's scale, or -1 when it cannot be read.</summary>
    private static string TimeScale()
    {
        try { return Time.timeScale.ToString("0.00"); }
        catch (Exception) { return "-1"; }
    }

    /// <summary>The process of the host's table this machine's own controller is playing, or null.</summary>
    private static void FindProcess(CutscenesServerController server, int processId, out CutsceneProcess process)
    {
        process = null;
        try
        {
            if (server?._cutsceneProcesses != null) server._cutsceneProcesses.TryGetValue(processId, out process);
        }
        catch (Exception) { process = null; }
    }

    /// <summary>The first process of the host's table, for the sample of a second its own machine does not watch.</summary>
    private static CutsceneProcess FirstProcess(CutscenesServerController server, out int processId)
    {
        processId = 0;
        try
        {
            var processes = server?._cutsceneProcesses;
            if (processes == null || processes.Count == 0) return null;
            foreach (var entry in processes)
            {
                var process = entry.Value;
                if (process == null) continue;
                processId = entry.Key;
                return process;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>What the local controller plays right now, for the play line.</summary>
    private static string LocalPlayback(CutscenesClientController client, int processId, string cutscene)
    {
        if (client == null) return "controller-absent";
        if (client._cutsceneProcessId == processId) return "playing#" + processId;
        if (!string.IsNullOrEmpty(cutscene) && client._cutsceneId == cutscene) return "playing#" + client._cutsceneProcessId;
        return "idle";
    }

    private static string Link(CutscenesClientController controller) =>
        controller == null ? "absent" : controller._timelinesMapBank == null ? "null" : controller._timelinesMapBank.gameObject.name;

    private static string Link(CutscenesServerController controller) =>
        controller == null ? "absent" : controller._timelinesMapBank == null ? "null" : controller._timelinesMapBank.gameObject.name;

    /// <summary>
    /// A map can keep its bank object switched off until the game reaches the running state, so the active-only
    /// lookup above can miss it. The scan walks every loaded object, hence the 10 s spacing.
    /// </summary>
    private static MapTimelinesBank FindNativeBankInScene()
    {
        if (Time.unscaledTime < _nextFallback) return null;
        _nextFallback = Time.unscaledTime + FallbackScanSeconds;
        MapTimelinesBank found = null;
        var candidates = 0;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<MapTimelinesBank>(true);
            candidates = all.Length;
            for (var i = 0; i < all.Length; i++)
            {
                var bank = all[i];
                if (bank == null) continue;
                found = bank;
                if (bank.gameObject.activeInHierarchy) break;
            }
        }
        catch (Exception error)
        {
            FikaGlobals.LogWarning("CutsceneBoundary: the inactive bank scan failed: " + error.Message);
            return null;
        }
        if (found != null)
        {
            
        }
        return found;
    }


    /// <summary>
    /// The end actions of the timeline this machine's controller currently owns, and whether each of them is
    /// registered in either controller's action table. That is the difference between "this map's cutscene has
    /// no door action at all" (<c>ids=0</c>) and "the door action exists but was never registered", which is
    /// what a rig that was never switched on causes (analysis/cutscene-rig-play). Written per end id, so a grep
    /// names the exact action that is missing.
    /// </summary>
    private static string EndActions(GameWorld world)
    {
        try
        {
            var client = world.CutscenesClientController;
            var server = world.CutscenesServerController;
            var bank = client?._timelinesMapBank ?? server?._timelinesMapBank;
            var owned = client == null ? null : client._cutsceneId;
            if (string.IsNullOrEmpty(owned)) return "none";
            var timelines = bank?.timelines;
            for (var i = 0; i < (timelines?.Count ?? 0); i++)
            {
                var timeline = timelines[i];
                if (!string.Equals(timeline.cutsceneId, owned, StringComparison.Ordinal)) continue;
                var ids = timeline.endActionsId;
                var total = ids?.Length ?? 0;
                if (total == 0) return "ids=0 (this cutscene has no end action)";
                var text = new System.Text.StringBuilder("ids=").Append(total).Append('[');
                for (var n = 0; n < total; n++)
                {
                    if (n > 0) text.Append(',');
                    var id = ids[n];
                    var registered = FindAction(server, id) != null ? "server" : FindAction(client, id) != null ? "client" : "missing";
                    text.Append(id).Append('=').Append(registered);
                }
                return text.Append(']').ToString();
            }
            return "no timeline for '" + owned + "'";
        }
        catch (Exception error) { return "? (" + error.GetType().Name + ")"; }
    }

    private static CutsceneBaseAction FindAction(CutscenesServerController controller, string actionId)
    {
        try
        {
            if (controller?._actions == null || string.IsNullOrEmpty(actionId)) return null;
            return controller._actions.TryGetValue(actionId, out var action) ? action : null;
        }
        catch (Exception) { return null; }
    }

    private static CutsceneBaseAction FindAction(CutscenesClientController controller, string actionId)
    {
        try
        {
            if (controller?._actions == null || string.IsNullOrEmpty(actionId)) return null;
            return controller._actions.TryGetValue(actionId, out var action) ? action : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The local scene of this machine, for the state line: which cutscene's scene it owns and whether it plays.</summary>
    private static string LocalState(GameWorld world)
    {
        var client = world.CutscenesClientController;
        if (client == null) return "absent";
        if (client._cutsceneProcessId == 0) return "idle";
        try { return client._cutsceneId + "/process=" + client._cutsceneProcessId + "/played=" + (_localPlayed ? "yes" : "no"); }
        catch (Exception) { return "?"; }
    }

    /// <summary>
    /// The <c>_offlineMode</c> flag both controllers carry. Fika sets it to false for a bound raid
    /// (<c>RaidInteractionSync.ConfigureControllers</c>); the line records what the bridge sees, which is what
    /// decides whether the native side delivers a scene locally or expects a network that a Fika raid has not
    /// got (analysis/cutscene-play-and-end).
    /// </summary>
    private static string OfflineFlags(GameWorld world)
    {
        var client = world.CutscenesClientController;
        var server = world.CutscenesServerController;
        return "client:" + (client == null ? "absent" : client._offlineMode ? "yes" : "no")
            + ",server:" + (server == null ? "absent" : server._offlineMode ? "yes" : "no");
    }

    /// <summary>
    /// Whether the controllers ran their own <c>Subscribe()</c>: only that step registers them on the global
    /// events (<c>CutSceneStartEvent</c> and friends) and writes <c>_unsubscribeOnDestroy</c>. <c>no</c> here
    /// next to a <c>play</c> line means the scene was raised and nothing was listening.
    /// </summary>
    private static string SubscribeFlags(GameWorld world)
    {
        var client = world.CutscenesClientController;
        var server = world.CutscenesServerController;
        return "client:" + (client == null ? "absent" : client._unsubscribeOnDestroy != null ? "yes" : "no")
            + ",server:" + (server == null ? "absent" : server._unsubscribeOnDestroy != null ? "yes" : "no");
    }

    private static string Controllers(GameWorld world) =>
        "client:" + Presence(world.CutscenesClientController != null, _createdClient)
        + ",server:" + Presence(world.CutscenesServerController != null, _createdServer);

    private static string Presence(bool exists, bool created) => !exists ? "absent" : created ? "created" : "present";

    private static string BankLinks(GameWorld world) =>
        "client:" + LinkState(world.CutscenesClientController) + ",server:" + LinkState(world.CutscenesServerController);

    private static string LinkState(CutscenesClientController controller) =>
        controller == null ? "absent" : controller._timelinesMapBank == null ? "no" : "yes";

    private static string LinkState(CutscenesServerController controller) =>
        controller == null ? "absent" : controller._timelinesMapBank == null ? "no" : "yes";

    private static string InitFlags(GameWorld world)
    {
        var client = world.CutscenesClientController;
        var server = world.CutscenesServerController;
        return "client:" + Initialized(client) + ",server:" + Initialized(server);
    }

    private static string Initialized(CutscenesClientController controller)
    {
        if (controller == null) return "absent";
        try { return controller._initializeCancellationTokenSource != null ? "yes" : "no"; }
        catch (Exception) { return "?"; }
    }

    private static string Initialized(CutscenesServerController controller)
    {
        if (controller == null) return "absent";
        try { return controller._initializeCancellationTokenSource != null ? "yes" : "no"; }
        catch (Exception) { return "?"; }
    }

    private static string ActionCounts(GameWorld world) =>
        "client:" + Actions(world.CutscenesClientController) + ",server:" + Actions(world.CutscenesServerController);

    private static string Actions(CutscenesClientController controller)
    {
        if (controller == null) return "absent";
        if (controller._actions == null) return "none";
        try
        {
            var total = 0;
            var doors = 0;
            foreach (var entry in controller._actions)
            {
                if (entry.Value == null) continue;
                total++;
                if (entry.Value.TryCast<CutsceneActionOpenDoors>() != null) doors++;
            }
            return total + "(doors=" + doors + ")";
        }
        catch (Exception) { return "?"; }
    }

    private static string Actions(CutscenesServerController controller)
    {
        if (controller == null) return "absent";
        if (controller._actions == null) return "none";
        try
        {
            var total = 0;
            var doors = 0;
            foreach (var entry in controller._actions)
            {
                if (entry.Value == null) continue;
                total++;
                if (entry.Value.TryCast<CutsceneActionOpenDoors>() != null) doors++;
            }
            return total + "(doors=" + doors + ")";
        }
        catch (Exception) { return "?"; }
    }

    private static string Name(MapTimelinesBank bank) => bank == null ? "none" : bank.gameObject.name;
}
