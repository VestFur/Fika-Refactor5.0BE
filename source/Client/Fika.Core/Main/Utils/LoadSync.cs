using System;
using Comfort.Common;
using EFT;

namespace Fika.Core.Main.Utils;

/// <summary>
/// The one place that writes the "[LoadSync]" judgement lines of the loading screen / session wait path.
///
/// Why a helper: the lines have to be greppable by a tester with a single pattern
/// ("[LoadSync]") in both logs - the game log next to the loading screen and the
/// crash-trail diagnostics file - and they have to survive the quiet diagnostics mode,
/// which only keeps the scopes listed in FikaDiagnostics.QuietSteps. That list is prefix
/// matched, so the scope "LoadingScreen" (already on the list) also keeps
/// "LoadingScreen.LoadSync" without touching the diagnostics infrastructure itself.
///
/// The shape of a line, fixed on purpose (a tester reads these across two machines):
///   [LoadSync] &lt;what is happening&gt;: role=host|client, ... =&gt; &lt;the verdict&gt;
/// Every line names the local state, the peer state that matters and, where it applies,
/// what would release the wait.
/// </summary>
internal static class LoadSync
{
    /// <summary>Diagnostics scope: prefix-matched against the quiet-mode allow list ("LoadingScreen").</summary>
    internal const string Scope = "LoadingScreen.LoadSync";

    /// <summary>
    /// The host's pre-raid barrier backstop, in seconds, from <see cref="LoadSyncGate"/> (a config entry
    /// is bound to that field; see the class comment there for why the value must be tunable).
    /// </summary>
    internal static float PeerLoadTimeoutSeconds => LoadSyncGate.HostPeerLoadTimeoutSeconds;

    // ------------------------------------------------------------------ peer liveness stamps
    //
    // Two facts a [LoadSync] line needs to be readable in *both* outcomes ("everything worked" and
    // "nothing happened"): when the last packet from the peer arrived, and - on the client - whether the
    // transport is still connected. The transport is maintained by LiteNetLib's own thread (acks and
    // keepalives), so it stays alive while the peer's *main thread* is busy loading a map, which is
    // exactly the case that must not be mistaken for a dead peer.

    private static float _lastPeerPacketAt;
    private static string _lastPeerPacketSource = "none";
    private static int _peerPacketCount;

    /// <summary>Counts one inbound packet of the session (called from the receive entry points).</summary>
    internal static void NotePeerPacket(string source)
    {
        try
        {
            _lastPeerPacketAt = UnityEngine.Time.realtimeSinceStartup;
            _lastPeerPacketSource = source;
            _peerPacketCount++;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Seconds since the last inbound packet, or -1 when none arrived yet.</summary>
    internal static int SecondsSincePeerPacket()
    {
        var last = _lastPeerPacketAt;
        if (last <= 0f)
        {
            return -1;
        }

        return (int)(UnityEngine.Time.realtimeSinceStartup - last);
    }

    /// <summary>One sentence of the inbound traffic, for a judgement line.</summary>
    internal static string DescribePeerTraffic()
    {
        return "inbound=" + _peerPacketCount + " sinceLastInbound=" + SecondsSincePeerPacket() + "s (last="
            + _lastPeerPacketSource + ")";
    }

    /// <summary>
    /// A line a tester must see in both logs: the game log (next to the loading screen) and the
    /// diagnostics file. The caller name is passed explicitly so the game log reads
    /// "[WaitForOtherPlayersToLoad]: [LoadSync] ..." and not "[Report]: ...".
    /// </summary>
    internal static void Report(string caller, string message)
    {
        FikaGlobals.LogInfo("[LoadSync] " + message, caller);
        
    }

    /// <summary>A line that names a degraded outcome (a wait that timed out, a peer that never came).</summary>
    internal static void Warn(string caller, string message)
    {
        FikaGlobals.LogWarning("[LoadSync] " + message, caller);
        
    }

    /// <summary>Diagnostics only: the per-poll detail that would flood the game log.</summary>
    internal static void Trace(string message)
    {
        
    }

    /// <summary>
    /// The rows the local loading screen currently holds, as one string: "screen=2 row(s)
    /// [netId(nickname)=progress, ...]". This is the "local progress / peer progress" half of every
    /// judgement line, and the only way to see from a log whether the engine-driven screen really
    /// received the rows and the progress values.
    /// </summary>
    internal static string DescribeScreen()
    {
        try
        {
            var screen = LoadingScreenUI.Instance;
            return screen == null ? "screen=absent" : "screen=" + screen.DescribeRows();
        }
        catch (System.Exception exception)
        {
            return "screen=unreadable (" + exception.GetType().Name + ")";
        }
    }

    /// <summary>True while a game world exists on this machine (the state every world-bound packet needs).</summary>
    internal static bool WorldReady
    {
        get
        {
            try
            {
                return Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance != null;
            }
            catch (System.Exception)
            {
                return false;
            }
        }
    }
}
