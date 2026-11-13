using System;
using System.Collections.Generic;
using CommonAssets.Scripts.Cutscenes;
using EFT;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.Components;

/// <summary>
/// BD (analysis/cutscene-client-native-trigger): the client plays the story cutscene through its own native
/// trigger chain, in place.
///
/// Why this exists. The route "host dispatch -&gt; CutSceneStartEvent -&gt; the local client controller" needs the
/// host to have sent a CutsceneStartPacket first. In the round of analysis/logs-coop3-20261013-122918 the client
/// has no single 'phase=dispatch' line - and that line is written outside the 'Coop / Diagnostics' switch, once
/// per process id - so this raid's host never dispatched at all. What the client did have is its own native
/// chain: the rally trigger reached SPTushonka's TriggerCutscenePatches.Start, which called the native
/// CutscenesServerController.StartCutscene, and Fika's own CutsceneStartSyncPatch.Prefix short-circuited it to
/// __result = null - which is exactly the "trigger cutscene: 'Icebreaker_1' refused" line of LogOutput.log:1947.
/// The client therefore had neither a picture nor a process.
///
/// This class takes that dropped request and runs it natively a moment later: once the client's own trigger asks
/// for cutscene X, Fika waits <see cref="LocalTriggerDelaySeconds"/> seconds for the host to start the same one
/// (a host dispatch wins, nothing is triggered locally), and if the host stays silent the bridge lets the
/// machine's own <c>CutscenesServerController.StartCutscene</c> run - the very call the host runs. The local
/// server controller creates the process, the bridge hands it to this machine's client controller, and when the
/// local viewer has watched it the native <c>EndCutscene</c> of that same controller runs the timeline's end
/// actions (the door of the map).
///
/// One cutscene is triggered locally at most once (<see cref="Played"/>); a cutscene the host dispatched is never
/// triggered locally (<see cref="HostStarted"/>); and a cutscene this machine already plays natively is not
/// started a second time when the host packet arrives later (see FikaClient.RaidInteractions.cs). The host path
/// and the single-player path are untouched: every entry point here starts with <see cref="Enabled"/>, which is
/// false on a host.
///
/// One switch reverts the whole behaviour (<see cref="UseNativeClientTrigger"/>).
/// </summary>
internal static class FikaLocalCutsceneTrigger
{
    /// <summary>
    /// Master switch of the whole repair: on (default) a client that asked its own native chain to start a
    /// cutscene runs that native chain itself when no host packet for the same cutscene arrives; off restores
    /// the previous behaviour (the local start is dropped, exactly as CutsceneStartSyncPatch did before).
    /// </summary>
    internal static bool UseNativeClientTrigger = true;

    /// <summary>
    /// The seat table of a locally triggered process: on (default) it holds this machine's own player only, so
    /// the native completion counter of the process reaches its own total (1 of 1) and the native EndCutscene -
    /// with the timeline's end actions - runs on this machine by itself. Off keeps the native list (every
    /// non-AI alive player of this world, the host character included), which for a scene nobody else can watch
    /// means the native end never comes.
    /// </summary>
    internal static bool SingleLocalViewer = true;

    /// <summary>
    /// Seconds the client's own request for a cutscene is held back while the host may still dispatch the same
    /// one. Reached without a host packet, the bridge triggers the native chain itself.
    /// </summary>
    internal static float LocalTriggerDelaySeconds = 2f;

    /// <summary>Cutscenes this machine's own trigger asked to start, with the moment it asked.</summary>
    private static readonly Dictionary<string, float> Requests = new(StringComparer.Ordinal);
    /// <summary>Cutscenes this machine already ran through its own native StartCutscene.</summary>
    private static readonly HashSet<string> Played = new(StringComparer.Ordinal);
    /// <summary>Cutscenes the host started (a CutsceneStartPacket for it was seen on this client).</summary>
    private static readonly HashSet<string> HostStarted = new(StringComparer.Ordinal);
    /// <summary>Process ids this machine created itself, mapped to their cutscene (the local native process).</summary>
    private static readonly Dictionary<int, string> LocalProcesses = new();
    /// <summary>The one cutscene the bridge granted to the next native StartCutscene call.</summary>
    private static string _grant;
    private static float _nextScan;

    /// <summary>True only on a client of a Fika raid - the host keeps its own path.</summary>
    internal static bool Enabled => UseNativeClientTrigger && FikaBackendUtils.IsClient;

    internal static void Reset()
    {
        Requests.Clear();
        Played.Clear();
        HostStarted.Clear();
        LocalProcesses.Clear();
        _grant = null;
        _nextScan = 0f;
    }

    /// <summary>Clears the cutscene state of one world when the raid ends (a new raid must not inherit it).</summary>
    internal static void ForgetWorld()
    {
        Requests.Clear();
        Played.Clear();
        HostStarted.Clear();
        LocalProcesses.Clear();
        _grant = null;
        _nextScan = 0f;
    }

    /// <summary>Whether this machine already plays (or played) this cutscene through its own native trigger.</summary>
    internal static bool HasPlayedLocally(string cutsceneId) =>
        !string.IsNullOrEmpty(cutsceneId) && Played.Contains(cutsceneId);

    /// <summary>
    /// Whether this process is one this machine created itself (the local native trigger). The boundary uses it
    /// to tell "my own cutscene, my own native end" from "the host's process, the host's end".
    /// </summary>
    internal static bool OwnsLocalProcess(int processId, out string cutscene) =>
        LocalProcesses.TryGetValue(processId, out cutscene) && !string.IsNullOrEmpty(cutscene);

    /// <summary>The host started this cutscene (a start packet reached this client): the local trigger stands down.</summary>
    internal static void NoteHostStart(string cutsceneId)
    {
        if (!string.IsNullOrEmpty(cutsceneId)) HostStarted.Add(cutsceneId);
    }

    /// <summary>
    /// The client's own native chain asked for a cutscene and Fika dropped the call (the prefix of
    /// <c>CutsceneStartSyncPatch</c>). The request is remembered and re-run natively unless the host speaks first.
    /// </summary>
    internal static void NoteNativeRequest(string cutsceneId)
    {
        if (!Enabled || string.IsNullOrEmpty(cutsceneId)) return;
        if (Played.Contains(cutsceneId) || HostStarted.Contains(cutsceneId))
        {
            return;
        }
        if (Requests.ContainsKey(cutsceneId)) return;
        Requests[cutsceneId] = UnityEngine.Time.unscaledTime;
    }

    /// <summary>
    /// The one grant the prefix reads: the bridge is about to let this machine's own native StartCutscene through.
    /// The seat table of that call is narrowed to this machine when <see cref="SingleLocalViewer"/> is on.
    /// </summary>
    internal static bool GrantNativeStart(string cutsceneId,
        Il2CppSystem.Collections.Generic.List<int> viewers)
    {
        if (!Enabled || string.IsNullOrEmpty(cutsceneId)) return false;
        if (!string.Equals(_grant, cutsceneId, StringComparison.Ordinal)) return false;
        _grant = null;
        if (!SingleLocalViewer || viewers == null) return true;
        var local = RaidInteractionSync.LocalPlayerId;
        if (local <= 0) return true;
        // The table is edited in place: the caller's own list is the one the native body reads.
        viewers.Clear();
        viewers.Add(local);
        return true;
    }

    /// <summary>The native body ran: this machine owns a process of its own now.</summary>
    internal static void NoteLocalProcess(CutsceneProcess process)
    {
        if (process == null) return;
        var cutscene = process.currentCutsceneId;
        if (string.IsNullOrEmpty(cutscene)) return;
        Played.Add(cutscene);
        Requests.Remove(cutscene);
        LocalProcesses[process.processId] = cutscene;
    }

    /// <summary>
    /// Runs on the raid tick of this machine (RaidInteractionSync.Tick). It is the decision point of the repair:
    /// a request whose host signal never came is turned into one native StartCutscene of this machine.
    /// </summary>
    internal static void Tick(GameWorld world)
    {
        if (!Enabled || world == null || Requests.Count == 0) return;
        var now = UnityEngine.Time.unscaledTime;
        if (now < _nextScan) return;
        _nextScan = now + 0.25f;
        foreach (var entry in new List<KeyValuePair<string, float>>(Requests))
        {
            var cutscene = entry.Key;
            if (Played.Contains(cutscene))
            {
                Requests.Remove(cutscene);
                continue;
            }
            if (HostStarted.Contains(cutscene))
            {
                Requests.Remove(cutscene);
                continue;
            }
            if (now - entry.Value < LocalTriggerDelaySeconds) continue;
            Requests.Remove(cutscene);
            Fire(world, cutscene);
        }
    }

    /// <summary>
    /// The repair itself: this machine's own native StartCutscene, with the grant that lets the call through the
    /// Fika prefix. Everything after it is native - the process, the local viewer's playback and the native end
    /// with the timeline's end actions.
    /// </summary>
    private static void Fire(GameWorld world, string cutscene)
    {
        var server = world == null ? null : world.CutscenesServerController;
        if (server == null)
        {
            return;
        }
        // The seat table of the native call: the native list (every non-AI alive player of this world, the host
        // character included) is built here so that SingleLocalViewer off behaves exactly like the trigger that
        // called us; on (default) the grant narrows it to this machine's own player instead.
        var viewers = new Il2CppSystem.Collections.Generic.List<int>();
        var local = RaidInteractionSync.LocalPlayerId;
        try
        {
            var all = world.AllAlivePlayersList;
            if (all != null)
                for (var i = 0; i < all.Count; i++)
                {
                    var player = all[i];
                    if (player == null || player.IsAI) continue;
                    viewers.Add(player.PlayerId);
                }
        }
        catch (Exception)
        {
            // A half-spawned player list is not a reason to skip the trigger: fall back to this machine's own id.
        }
        if (viewers.Count == 0 && local > 0) viewers.Add(local);
        _grant = cutscene;
        CutsceneProcess process;
        try
        {
            process = server.StartCutscene(cutscene, viewers, false, false, 0f);
        }
        catch (Exception error)
        {
            _grant = null;
            return;
        }
        if (process == null)
        {
            _grant = null;
            return;
        }
    }

}
