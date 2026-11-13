using System;
using System.Threading.Tasks;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.Networking.Packets.Communication;

namespace Fika.Core.Main.Utils;

/// <summary>
/// The "both machines start loading at the same time" rule of a transit reload
/// (analysis/transit-reload-sync/REPORT.md).
///
/// The measured problem (round 8, host Fika-diagnostics.log 400404 ms extract of the host's own player ->
/// 412284 ms the world is gone and the loading screen is re-armed -> 464548 ms LocalGameMatching ->
/// 609161 ms LocalGameCreate; client Fika-diagnostics.log 528635 ms re-armed -> 543132 ms LocalGameMatching
/// -> 611823 ms LocalGameCreate): after a transit the client ends its raid about a second after the host's
/// own extract and already begins its next-raid flow 14.5 s later, while the host needs 60.1 s to get
/// there (4 s of the deliberate stop delay in FikaHostTransitController.Transit, 7.9 s inside
/// CoopGame.Stop, 52.3 s of native application work until its next LocalGameMatching). The client is
/// therefore ~45 s ahead before either machine has loaded a byte - and because the host's own map load is
/// also the longer one (144.6 s for TarkovStreets against the client's 68.7 s) the client sat in
/// WaitForServerToReceiveLocation for ~130 s while its own row of the loading screen was already at 100 %.
///
/// What this gate does about it: the client still ends its raid as soon as the host's landing arrives
/// (that keeps the raid-end bookkeeping, the profile save and the extraction flow where they were), but it
/// holds its own level creation until the host announces that it is starting its own load. That
/// announcement is sent from FikaHostGameWorld.InitLevelManaged, i.e. from the native step that runs before
/// TarkovApplication.LocalGameCreate, so both machines enter their native InitLevel within one round trip
/// of each other instead of ~45 s apart ("the same second"). Whoever has the shorter way to its own load
/// waits - here that is the client - and the host never waits for a peer, so a slow or absent client cannot
/// delay the host.
///
/// Every release is bounded and reported, so a machine can never be parked by a peer that is gone:
///   * the reload-start signal (the normal path, reason=host-reload-start),
///   * the transport to the host not being Connected any more (reason=transport-gone),
///   * a fresh host response that says the host has already created its raid, i.e. the host finished its
///     own load while this machine was still holding - the signal was lost or the host runs a build
///     without it (reason=host-finished-load),
///   * <see cref="ClientHoldSeconds"/> as the last resort (reason=hold-ceiling).
/// The default is chosen from the measurement (analysis/transit-reload-sync2/REPORT.md): the ceiling is
/// counted from the moment this machine reaches its level creation, and the host's own way from the
/// client's landing to its load start took 84.1 s of wall clock in the worst round measured so far
/// (2026-10-11 round 10: client LogOutput.log:2610 park -> client LogOutput.log:3193 signal, host side
/// host LogOutput.log:2693), i.e. 102.4 s counted from the arm point on the client's own clock (103.4 s on
/// the host's clock; the two wall clocks differ by ~1.1 s, measured from the signal's send and receive). 240 s covers that 2.8 times over, which matters because the host's native part grew
/// from 52.3 s (round 8) to 92.2 s (round 10) without a mod change. A host that never sends the signal
/// still ends the hold through host-finished-load or transport-gone, and the hold loads nothing while it
/// waits, so the large ceiling costs nothing on the normal path.
///
/// The signal travels as a <see cref="TransitEventPacket.ETransitEventType.ReloadGo"/> packet without a
/// payload. A receiver that does not know the type reads the one type byte and ignores it (both the
/// serializer's and the reader's default branches handle an unknown type without a payload), so a mixed
/// pair of builds cannot misparse its packet stream; a host without the signal simply ends the client's
/// hold through the fallbacks above.
/// </summary>
public static class TransitReloadGate
{
    /// <summary>
    /// Client side: how long a transit reload hold may last before this machine loads the next map without
    /// the host's signal. A config entry is bound to this field (the same pattern as
    /// <see cref="LoadSyncGate"/>, which FikaConfig binds at startup and on every SettingChanged), so the
    /// number stays tunable in the field ("Coop / Transit: Client Reload Hold"). Default 240 s.
    ///
    /// The ceiling is counted from the moment this machine reaches its level creation. The host needed
    /// 84.1 s of wall clock to reach its own load start in round 10 (client LogOutput.log:2610 park
    /// 20:16:06.297 -> client LogOutput.log:3193 signal 20:17:29.336; 102.4 s counted from the arm point),
    /// and its native part is what grows between rounds (52.3 s in round 8 -> 92.2 s in round 10),
    /// so the ceiling has to sit well above the worst measurement. The previous 60 s released this machine
    /// 22.8 s before the host's signal arrived (client LogOutput.log:2625 vs :3193) - the misalignment this
    /// ceiling exists to prevent.
    /// </summary>
    public static int ClientHoldSeconds = 240;

    /// <summary>
    /// The worst distance from this machine's arm point to the host's own reload start measured so far, in
    /// seconds. It is printed in the judgement lines as <c>hostWorstLead=</c> so a reader can tell whether
    /// the configured ceiling covers the case at hand without opening the reports - the ceiling has to stay
    /// above it. Round 10 (2026-10-11): 102.4 s on the client's own clock (arm uptime 479.6 s -> signal
    /// uptime 582.0 s; 103.4 s on the host's clock - the two wall clocks differ by ~1.1 s); round 8: ~64 s. The "signal arrived too late" line prints the distance it
    /// saw, so a new worst is visible before this number is updated.
    /// </summary>
    public static int HostLeadWorstSeconds = 105;

    /// <summary>
    /// Poll interval of the hold, in milliseconds. The poll is the same InformationPacket round trip the
    /// location wait uses: it keeps this machine's liveness visible to the host and lets the hold read the
    /// host's own progress back.
    /// </summary>
    public static int ClientHoldPollMilliseconds = 500;

    /// <summary>This machine ended a transit raid and has not started the next map yet.</summary>
    private static bool _armed;

    /// <summary>What armed the hold, for the judgement line ("a transit landing of point 1").</summary>
    private static string _armedSource = "none";

    /// <summary>realtimeSinceStartup of the arming, so every hold line can name the age of the state.</summary>
    private static float _armedAt;

    /// <summary>The host announced that its own reload started (and when).</summary>
    private static bool _goReceived;
    private static float _goAt;

    /// <summary>
    /// The end of the last hold. A signal that arrives after it is a *late* signal - the host broadcast,
    /// but this machine was no longer waiting - and that has to stay distinguishable from "the host never
    /// broadcast at all", because the two need different fixes (raise the ceiling vs. upgrade the host).
    /// </summary>
    private static bool _holdEnded;
    private static float _holdEndedAt;
    private static string _holdEndedReason = "none";
    private static float _holdEndedSinceArm;

    /// <summary>How often each release reason was taken, so one line can carry the whole history of a run.</summary>
    private static int _signalsReceived;
    private static int _parks;
    private static int _releasedBySignal;
    private static int _releasedByHostLoaded;
    private static int _releasedByCeiling;
    private static int _releasedByTransport;

    /// <summary>Signals that arrived after this machine's hold had already ended (the host broadcast too late).</summary>
    private static int _lateSignals;

    /// <summary>Signals that arrived with no transit reload on this machine at all (no arm, no hold).</summary>
    private static int _signalsWithoutContext;

    /// <summary>Worst arm-point-to-signal distance actually seen in this process (0 = none seen yet).</summary>
    private static float _worstSinceArm;

    /// <summary>One compact sentence about the gate, for a [LoadSync] header or a diagnostics line.</summary>
    internal static string Describe()
    {
        return "transitReloadGate hold=" + ClientHoldSeconds + "s armed=" + _armed + " (source=" + _armedSource
            + ") hostSignal=" + _goReceived + " parks=" + _parks + " released=signal:" + _releasedBySignal
            + "/hostLoaded:" + _releasedByHostLoaded + "/ceiling:" + _releasedByCeiling
            + "/transport:" + _releasedByTransport + ", signalsReceived=" + _signalsReceived
            + " late:" + _lateSignals + " noContext:" + _signalsWithoutContext
            + ", sinceArmWorst=" + (_worstSinceArm > 0f ? _worstSinceArm.ToString("0.0") + "s" : "none")
            + ", hostWorstLead=" + HostLeadWorstSeconds + "s";
    }

    /// <summary>
    /// Arms the hold: called by the client when it applies a transit landing for its own player, i.e. when
    /// this machine knows that it is in a transit reload. Idempotent per transit; a second landing of the
    /// same transit only refreshes the source text.
    /// </summary>
    public static void ArmForTransitReload(string source)
    {
        _armed = true;
        _armedSource = source ?? "unspecified";
        _armedAt = Time.realtimeSinceStartup;
        // A new transit owns a new signal: the announcement of the previous reload must not release this
        // hold before the host has started this one.
        _goReceived = false;
        _goAt = 0f;
        // A new transit also owns a new verdict: the previous reload's hold must not classify this one's
        // signal, and it must not swallow this one's late-signal line either.
        _holdEnded = false;
        _holdEndedAt = 0f;
        _holdEndedReason = "none";
        _holdEndedSinceArm = 0f;
        LoadSync.Trace("transit reload gate armed by " + _armedSource + " (" + Describe() + ")");
    }

    /// <summary>
    /// Drops the hold without parking: called when the raid start fails (<see cref="NetManagerUtils.AbortFailedJoin"/>)
    /// and at the end of every hold, so a state that can no longer be released never parks a later raid
    /// start.
    /// </summary>
    public static void Disarm(string reason)
    {
        if (!_armed && !_goReceived)
        {
            return;
        }

        _armed = false;
        _armedSource = "none";
        _goReceived = false;
        LoadSync.Trace("transit reload gate disarmed: " + reason);
    }

    /// <summary>
    /// The client side of the signal. Handled in <c>FikaClient</c>'s packet callback before the transit
    /// controller lookup: during the hold this machine has no world and therefore no transit controller at
    /// all, and that is exactly the window the signal exists for.
    /// </summary>
    /// <returns>True when the packet was this signal (the caller must not queue or forward it).</returns>
    public static bool HandlePacket(TransitEventPacket packet)
    {
        if (packet == null || packet.EventType != TransitEventPacket.ETransitEventType.ReloadGo)
        {
            return false;
        }

        var first = !_goReceived;
        _goReceived = true;
        _goAt = Time.realtimeSinceStartup;
        _signalsReceived++;
        if (first)
        {
            var sinceArm = _armedAt > 0f ? _goAt - _armedAt : -1f;

            if (_armed)
            {
                // The normal path: the signal covers a hold that is still running.
                _worstSinceArm = Math.Max(_worstSinceArm, sinceArm);
                LoadSync.Report("TransitReloadGate.HostReloadStart",
                    "the host started its own reload: the signal arrived at " + WallClock() + " (uptime=" + Uptime()
                    + ", armed=True, sinceArm=" + sinceArm.ToString("0.0") + "s of a " + ClientHoldSeconds + "s hold, hostWorstLead="
                    + HostLeadWorstSeconds + "s) => this machine releases its hold and starts loading the same map now");
            }
            else if (_holdEnded)
            {
                // The host did broadcast - only later than this machine's hold covered. This is a different
                // defect from "the host never broadcast", and it names its own fix.
                _lateSignals++;
                _worstSinceArm = Math.Max(_worstSinceArm, sinceArm);
                LoadSync.Warn("TransitReloadGate.HostReloadStart",
                    "transit reload signal arrived too late: the host started its own reload at " + WallClock() + " (uptime="
                    + Uptime() + "), but this machine was no longer waiting - its hold ended "
                    + (Time.realtimeSinceStartup - _holdEndedAt).ToString("0.0") + "s earlier (reason=" + _holdEndedReason
                    + ", the hold had covered sinceArm=" + _holdEndedSinceArm.ToString("0.0") + "s of the " + ClientHoldSeconds + "s hold when it ended; this"
                    + " signal arrived at sinceArm=" + sinceArm.ToString("0.0") + "s) => the two machines did not start loading together; raise"
                    + " 'Coop / Transit: Client Reload Hold' to at least " + SuggestedHoldSeconds(sinceArm) + "s (hostWorstLead=" + HostLeadWorstSeconds + "s, worst sinceArm seen in this run=" + _worstSinceArm.ToString("0.0") + "s)");
            }
            else
            {
                // No transit reload on this machine at all: the signal is not this machine's to act on.
                _signalsWithoutContext++;
                LoadSync.Trace("transit reload signal without a transit reload on this machine (no arm, no hold; sinceArm="
                    + (sinceArm > 0f ? sinceArm.ToString("0.0") + "s" : "n/a") + ")");
            }
        }

        return true;
    }

    /// <summary>
    /// The host side of the signal: called from the native level-creation step of the host's next raid
    /// (FikaHostGameWorld.InitLevelManaged), i.e. from the moment this host really starts loading the map.
    /// </summary>
    public static void AnnounceHostReloadStart(FikaServer server, string location)
    {
        var sent = false;
        var playerAmount = -1;

        try
        {
            if (server != null)
            {
                playerAmount = server.PlayerAmount;
                TransitEventPacket packet = new()
                {
                    EventType = TransitEventPacket.ETransitEventType.ReloadGo
                };
                server.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
                sent = true;
            }
        }
        catch (Exception exception)
        {
                    }

        LoadSync.Report("TransitReloadGate.HostReloadStart",
            "transit reload starting on this machine at " + WallClock() + " (uptime=" + Uptime() + ") role=host"
            + " reason=own-level-load location=" + location + " peerNotified=" + (sent ? "True" : "False")
            + " playerAmount=" + playerAmount
            + " => this host is creating the next map now and told every client to start its own load");
    }

    /// <summary>
    /// The client side of the hold: awaited from FikaClientGameWorld.InitLevel directly before the native
    /// level load, so the wait delays exactly the step that loads the next map and nothing else. Returns
    /// immediately when this machine is not in a transit reload (every normal raid start), and never throws.
    /// </summary>
    public static async Task WaitForHostReloadStartAsync(string site)
    {
        if (!_armed)
        {
            return;
        }

        var client = FikaSingleton<FikaClient>.Instance;
        if (client == null)
        {
            Disarm("no Fika client on this machine, the host cannot be heard (" + site + ")");
            LoadSync.Warn("TransitReloadGate.ClientHold",
                "transit reload hold skipped at " + site + ": this machine has no Fika client, so it cannot know "
                + "when the host starts its own reload; the next map is loaded without alignment");
            return;
        }

        var ceiling = Math.Max(5, ClientHoldSeconds);
        var pollMilliseconds = Math.Max(100, ClientHoldPollMilliseconds);
        var startedAt = Time.realtimeSinceStartup;
        var responsesAtPark = client.HostResponseCount;
        var stateAge = startedAt - _armedAt;
        _parks++;

        LoadSync.Report("TransitReloadGate.ClientHold",
            "transit reload hold on this machine at " + WallClock() + " (uptime=" + Uptime() + ") role=client site="
            + site + " peerNotified=" + _goReceived + " sinceArm=" + stateAge.ToString("0.0") + "s source=" + _armedSource
            + " ceiling=" + ceiling + "s hostWorstLead=" + HostLeadWorstSeconds + "s, " + LoadSync.DescribePeerTraffic()
            + " => this machine waits with its level creation until the host starts its own load; nothing is loaded while it waits"
            + " (only the liveness poll), and both maps then load in the same second");

        var reason = "hold-ceiling";
        var reportedAt = 0;

        while (true)
        {
            if (_goReceived)
            {
                reason = "host-reload-start";
                _releasedBySignal++;
                break;
            }

            if (client.ServerConnection == null || client.ServerConnection.ConnectionState != ConnectionState.Connected)
            {
                reason = "transport-gone";
                _releasedByTransport++;
                break;
            }

            // Only a *fresh* host response counts: HostReceivedLocation still holds the previous raid's
            // acknowledgement until this machine has polled the host inside the new reload window.
            if (client.HostResponseCount != responsesAtPark && client.HostReceivedLocation)
            {
                reason = "host-finished-load";
                _releasedByHostLoaded++;
                break;
            }

            var elapsed = Time.realtimeSinceStartup - startedAt;
            if (elapsed >= ceiling)
            {
                reason = "hold-ceiling";
                _releasedByCeiling++;
                
                break;
            }

            if (elapsed >= reportedAt)
            {
                reportedAt = (int)elapsed + 5;
                var sinceArmNow = stateAge + elapsed;
                LoadSync.Report("TransitReloadGate.ClientHold",
                    "transit reload hold continues: role=client site=" + site + " elapsed=" + ((int)elapsed).ToString()
                    + "s of " + ceiling + "s, peerNotified=" + _goReceived + " sinceArm=" + sinceArmNow.ToString("0.0")
                    + "s hostWorstLead=" + HostLeadWorstSeconds + "s, " + LoadSync.DescribePeerTraffic()
                    + " => the host has not started its own load yet; this machine keeps the next map unloaded and loads nothing while it waits");
            }

            try
            {
                var information = new InformationPacket();
                client.SendData(ref information, DeliveryMethod.ReliableUnordered);
            }
            catch (Exception exception)
            {
                // The poll is a convenience (liveness and the host's own flags); a failed one must never
                // end the hold, the ceiling and the transport check still decide.
                LoadSync.Trace("transit reload hold poll failed (" + exception.GetType().Name + "): " + exception.Message);
            }

            await Task.Delay(pollMilliseconds);
        }

        var held = Time.realtimeSinceStartup - startedAt;
        var peerNotified = _goReceived;
        var sinceArm = _armedAt > 0f ? Time.realtimeSinceStartup - _armedAt : held;
        _armed = false;
        _armedSource = "none";
        _goReceived = false;
        // The bookkeeping a late signal is classified against (HandlePacket).
        _holdEnded = true;
        _holdEndedAt = Time.realtimeSinceStartup;
        _holdEndedReason = reason;
        _holdEndedSinceArm = sinceArm;

        var line = "transit reload starting on this machine at " + WallClock() + " (uptime=" + Uptime()
            + ") role=client reason=" + reason + " site=" + site + " held=" + held.ToString("0.0") + "s sinceArm="
            + sinceArm.ToString("0.0") + "s peerNotified=" + peerNotified + " signals=" + _signalsReceived
            + " hostWorstLead=" + HostLeadWorstSeconds + "s"
            + " => the native level load of " + site + " starts here now";

        if (reason == "host-reload-start")
        {
            LoadSync.Report("TransitReloadGate.ClientHold", line);
            return;
        }

        LoadSync.Warn("TransitReloadGate.ClientHold", line
            + " (degraded: this machine starts its load without the host's reload-start signal. If the host broadcasts late, a"
            + " 'transit reload signal arrived too late' line follows this one - no such line in this run means the host did not"
            + " broadcast at all. See TransitReloadGate.Describe: " + Describe() + ")");
    }

    /// <summary>
    /// The ceiling a late signal asks for: the distance this machine actually waited for, plus a safety
    /// margin, rounded up to the next half minute. Printed so the fix is a number, not an investigation.
    /// </summary>
    private static int SuggestedHoldSeconds(float sinceArm)
    {
        var wanted = Math.Max(30f, sinceArm) + 15f;
        return (int)(Math.Ceiling(wanted / 30f) * 30f);
    }

    /// <summary>Local wall clock, so the two machines' lines can be subtracted directly across two logs.</summary>
    private static string WallClock()
    {
        return System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
    }

    /// <summary>Process uptime in seconds, the stamp the Fika diagnostics file uses.</summary>
    private static string Uptime()
    {
        return Time.realtimeSinceStartup.ToString("0.0") + "s";
    }
}
