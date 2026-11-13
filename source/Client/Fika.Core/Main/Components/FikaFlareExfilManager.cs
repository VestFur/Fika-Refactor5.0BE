using System.Collections.Generic;
using CommonAssets.Scripts.Game;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.Interactive.SecretExfiltrations;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace Fika.Core.Main.Components;

/// <summary>
/// Makes flare-gated exfiltration points ("signal flare" exits) usable in co-op.
/// <para>
/// Native flow (verified against the SPT 5.0 interop surface):
/// <c>FlareShootDetectorZone</c> adds the firing player (and, through
/// <c>EZoneEventType.PlayerByPartyAddedInShotList</c>, its squad) to its own shot
/// list and to <c>PlayersWithImmuneToSniperFire</c>, and the exit trigger then
/// evaluates <c>HasSniperImmunityTriggerRequirement.Check(profileId)</c> per
/// player. Every one of those objects is a scene object that only ever reacts to
/// a flare fired on its own machine, so a client's flare is invisible to the host
/// and to the other peers.
/// </para>
/// <para>
/// This component closes that gap: it observes the local detector zones, reports
/// an activation to the host (<c>[FlareExfil]</c>), the host decides the scope
/// (squad wide by default, single player when
/// <c>Flare Extract: Squad Shares Activation</c> is disabled) and broadcasts the
/// canonical result, which every peer applies through
/// <c>PlayersWithImmuneToSniperFire.AddImmunePlayer</c>.
/// </para>
/// </summary>
public sealed class FikaFlareExfilManager : MonoBehaviour
{
    public FikaFlareExfilManager(System.IntPtr pointer) : base(pointer) { }

    public FikaFlareExfilManager() : this(ClassInjector.DerivedConstructorPointer<FikaFlareExfilManager>())
    {
        ClassInjector.DerivedConstructorBody(this);
    }

    private const float PollInterval = 0.25f;
    private const float RefreshInterval = 15f;
    private const float ReaffirmInterval = 60f;

    internal static FikaFlareExfilManager Instance { get; private set; }

    private const float FlareDetectorGrace = 3f;

    private readonly Dictionary<System.IntPtr, bool> _zoneLatch = [];
    private float _lastLocalFlareShot;
    private bool _localFlarePending;
    private readonly Dictionary<string, float> _lastReportByZone = [];
    private readonly Dictionary<string, HashSet<string>> _nativeSquadGrants = [];
    private readonly Dictionary<System.IntPtr, ExfiltrationPoint> _boundPoints = [];
    private FlareShootDetectorZone[] _zones = [];
    private ExfiltrationPoint[] _points = [];
    private Il2CppSystem.Action<ExfiltrationPoint, EExfiltrationStatus> _statusChanged;
    private float _nextRefresh;
    private float _nextPoll;

    private void Awake()
    {
        Instance = this;
        _statusChanged = (Il2CppSystem.Action<ExfiltrationPoint, EExfiltrationStatus>)OnPointStatusChanged;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }

        foreach (var point in _boundPoints.Values)
        {
            try
            {
                if (point != null)
                {
                    point.OnStatusChanged -= _statusChanged;
                }
            }
            catch (System.Exception)
            {
                // The point may already be torn down with the level.
            }
        }
        _boundPoints.Clear();
        _zoneLatch.Clear();
        _lastReportByZone.Clear();
        _nativeSquadGrants.Clear();
    }

    private void Update()
    {
        var time = Time.time;
        if (time >= _nextRefresh)
        {
            _nextRefresh = time + RefreshInterval;
            RefreshTargets();
        }

        if (time >= _nextPoll)
        {
            _nextPoll = time + PollInterval;
            PollLocalZones(time);
        }
    }

    private void RefreshTargets()
    {
        try
        {
            _zones = UnityEngine.Object.FindObjectsOfType<FlareShootDetectorZone>();
        }
        catch (System.Exception exception)
        {
            FikaGlobals.LogWarning($"[FlareExfil] could not enumerate flare zones: {exception.Message}");
            _zones = [];
        }

        try
        {
            _points = UnityEngine.Object.FindObjectsOfType<ExfiltrationPoint>();
        }
        catch (System.Exception)
        {
            _points = [];
        }

        foreach (var point in _points)
        {
            if (point == null || !_boundPoints.TryAdd(point.Pointer, point))
            {
                continue;
            }

            try
            {
                point.OnStatusChanged += _statusChanged;
            }
            catch (System.Exception)
            {
                _boundPoints.Remove(point.Pointer);
            }
        }
    }

    private void OnPointStatusChanged(ExfiltrationPoint point, EExfiltrationStatus previousStatus)
    {
        if (!VerboseProbes() || point == null)
        {
            return;
        }

        var local = LocalPlayer;
        Probe($"[FlareProbe] point=<{point.Settings?.Name}> status=<{point.Status}> previous=<{previousStatus}> " +
            $"localProfile=<{local?.ProfileId ?? "none"}> immune=<{HasLocalImmunity(local)}>");
    }

    /// <summary>
    /// Watches the native detector zones for the local player. The native zone
    /// only ever reacts to the flare fired on its own machine, so a newly added
    /// local profile is exactly "this player fired a flare inside this zone".
    /// </summary>
    /// <summary>
    /// Called by the local flare gun the moment a flare is fired. It only feeds
    /// the probe below: the activation itself is still taken from the native
    /// detector zone, which is what the exit requirement reads.
    /// </summary>
    internal static void NotifyLocalFlareShot()
    {
        var instance = Instance;
        if (instance == null)
        {
            return;
        }

        instance._lastLocalFlareShot = Time.time;
        instance._localFlarePending = true;
    }

    private void PollLocalZones(float time)
    {
        var local = LocalPlayer;
        if (local == null)
        {
            return;
        }

        if (_localFlarePending && time - _lastLocalFlareShot > FlareDetectorGrace)
        {
            Probe($"[FlareProbe] flare fired {time - _lastLocalFlareShot:F1}s ago but no detector zone " +
                "registered the local player");
            _localFlarePending = false;
        }

        if (_zones is null || _zones.Length == 0)
        {
            return;
        }

        var profileId = local.ProfileId;
        foreach (var zone in _zones)
        {
            if (zone == null)
            {
                continue;
            }

            bool present;
            try
            {
                present = zone._shotPlayerProfiles != null && zone._shotPlayerProfiles.Contains(profileId);
            }
            catch (System.Exception)
            {
                continue;
            }

            var pointer = zone.Pointer;
            _zoneLatch.TryGetValue(pointer, out var wasPresent);
            if (present == wasPresent)
            {
                // Re-affirm periodically: a late joiner or a reconnect must end up
                // with the same immunity set as everybody else.
                if (present)
                {
                    var zoneId = zone.zoneID ?? string.Empty;
                    if (_lastReportByZone.TryGetValue(zoneId, out var last) && time - last >= ReaffirmInterval)
                    {
                        ReportLocalActivation(zone, local, reaffirm: true);
                    }
                }
                continue;
            }

            _zoneLatch[pointer] = present;
            if (!present)
            {
                _lastReportByZone.Remove(zone.zoneID ?? string.Empty);
                continue;
            }

            ReportLocalActivation(zone, local, reaffirm: false);
        }
    }

    private void ReportLocalActivation(FlareShootDetectorZone zone, Player local, bool reaffirm)
    {
        var zoneId = zone.zoneID ?? string.Empty;
        var flareType = (byte)zone.flareTypeForHandle;
        var ownProfile = local.ProfileId;
        var raidId = local.RaidId;
        var squad = CollectSquadProfiles(local);
        _lastReportByZone[zoneId] = Time.time;
        if (_localFlarePending)
        {
            _localFlarePending = false;
            Probe($"[FlareProbe] local flare shot matched zone=<{zoneId}> after " +
                $"{Time.time - _lastLocalFlareShot:F2}s (native detector path)");
        }
        // Remember what the native detector zone granted locally so the
        // per-player mode can undo the squad part again once the host answered.
        NotifyNativeSquadGrants(zoneId, ownProfile, squad);

        if (!reaffirm)
        {
            Probe($"[FlareProbe] detectorzone=<{zoneId}> flareType=<{flareType}> profile=<{ownProfile}> raid=<{raidId}> " +
                "-> native shot list contains the local player");
        }

        if (IsHost)
        {
            // The host owns the canonical scope, so its own flare follows the
            // same code path the client reports do.
            var canonical = BuildCanonical(zoneId, flareType, ownProfile, raidId, squad);
            ApplyCanonical(canonical, onHost: true);
            FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(
                EGenericSubPacketType.FlareShootReport, canonical, true);
            return;
        }

        FikaGlobals.LogInfo(reaffirm
            ? $"[FlareExfil] fired zone=<{zoneId}> profile=<{ownProfile}> -> report re-sent (reaffirm)"
            : $"[FlareExfil] fired zone=<{zoneId}> profile=<{ownProfile}> -> report sent");
        var report = FlareShootReport.FromValue(zoneId, flareType, ownProfile, raidId, squad, false);
        FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.FlareShootReport, report);
    }

    /// <summary>Host side: decide who a reported activation covers.</summary>
    internal FlareShootReport BuildCanonical(string zoneId, byte flareType, string shooterProfileId, int shooterRaidId,
        string[] squadProfiles)
    {
        string[] profiles;
        if (SquadShares())
        {
            profiles = squadProfiles is { Length: > 0 } ? squadProfiles : [shooterProfileId];
        }
        else
        {
            profiles = [shooterProfileId];
        }

        return FlareShootReport.FromValue(zoneId, flareType, shooterProfileId, shooterRaidId, profiles, true);
    }

    /// <summary>Applies a canonical activation locally (grants sniper immunity).</summary>
    internal void ApplyCanonical(FlareShootReport report, bool onHost)
    {
        if (report?.ProfileIds == null)
        {
            return;
        }

        var granted = 0;
        var rejected = false;
        foreach (var profileId in report.ProfileIds)
        {
            if (string.IsNullOrEmpty(profileId))
            {
                continue;
            }

            if (!TryResolveRaidId(profileId, out var raidId, out var reason))
            {
                LogRejected($"profile <{profileId}>: {reason}");
                rejected = true;
                continue;
            }

            if (IsBanned(raidId))
            {
                LogRejected($"profile <{profileId}> raid <{raidId}>: banned player");
                rejected = true;
                continue;
            }

            if (!GrantImmunity(raidId))
            {
                rejected = true;
                continue;
            }

            granted++;
        }

        var partyMembers = Mathf.Max(0, granted - 1);
        var scope = SquadShares() ? "squad" : "single";
        if (onHost)
        {
            FikaGlobals.LogInfo(SquadShares()
                ? $"[FlareExfil] report profile=<{report.ShooterProfileId}> zone=<{report.ZoneId}> -> granted to party (<{partyMembers}> members)"
                : $"[FlareExfil] report profile=<{report.ShooterProfileId}> zone=<{report.ZoneId}> -> granted for <{report.ShooterProfileId}> only");
        }
        else
        {
            FikaGlobals.LogInfo(
                $"[FlareExfil] applied (fired by <{report.ShooterProfileId}>, {(SquadShares() ? "squad-wide" : "single")})");
        }

        if (rejected)
        {
            Probe($"[FlareProbe] apply zone=<{report.ZoneId}> scope=<{scope}> granted=<{granted}> finished with rejections");
        }

        ReconcileNativeGrants(report, onHost);
    }

    /// <summary>
    /// With <c>Flare Extract: Squad Shares Activation</c> disabled the native
    /// detector zone on the firing machine has still added the whole squad to its
    /// own immunity bookkeeping. The host's canonical set only contains the
    /// shooter, so those extra native grants are dropped here, which is what
    /// makes the per-player mode observable on every machine.
    /// </summary>
    private void ReconcileNativeGrants(FlareShootReport report, bool onHost)
    {
        if (SquadShares() || string.IsNullOrEmpty(report.ZoneId))
        {
            return;
        }

        if (!_nativeSquadGrants.TryGetValue(report.ZoneId, out var nativeGrants))
        {
            return;
        }

        foreach (var profileId in nativeGrants)
        {
            if (string.IsNullOrEmpty(profileId) || profileId == report.ShooterProfileId)
            {
                continue;
            }

            if (report.ProfileIds != null && System.Array.IndexOf(report.ProfileIds, profileId) >= 0)
            {
                continue;
            }

            if (!TryResolveRaidId(profileId, out var raidId, out _))
            {
                continue;
            }

            RemoveImmunity(raidId);
            FikaGlobals.LogInfo($"[FlareExfil] ignored (report profile=<{profileId}> != me)");
        }

        _nativeSquadGrants.Remove(report.ZoneId);
    }

    internal static void HandleReport(FlareShootReport report)
    {
        if (report == null)
        {
            return;
        }

        // Only the host produces the authoritative set; a peer that receives a
        // raw client report would otherwise apply an unvalidated activation.
        if (!report.Canonical)
        {
            FikaGlobals.LogWarning($"[FlareExfil] rejected report profile=<{report.ShooterProfileId}> " +
                $"zone=<{report.ZoneId}>: reason=report was not validated by the host");
            return;
        }

        Instance?.ApplyCanonical(report, onHost: false);
    }

    /// <summary>Host side validation for reports coming from a peer.</summary>
    internal static bool ValidateReport(FlareShootReport report, FikaPlayer sender, out string reason)
    {
        reason = null;
        if (report == null)
        {
            reason = "empty report";
            return false;
        }

        if (sender == null)
        {
            reason = "no bound player";
            return false;
        }

        if (report.ShooterRaidId != sender.RaidId || report.ShooterProfileId != sender.ProfileId)
        {
            reason = $"sender mismatch (report {report.ShooterProfileId}/{report.ShooterRaidId}, " +
                $"peer {sender.ProfileId}/{sender.RaidId})";
            return false;
        }

        if (string.IsNullOrEmpty(report.ZoneId))
        {
            reason = "missing zone id";
            return false;
        }

        // The detector zones are level objects. A graphical nuker on a headless
        // host may strip them, in which case there is nothing to validate
        // against and the report is accepted so that client flares still work.
        var zones = FindZones();
        if (zones.Length > 0)
        {
            var zone = System.Array.Find(zones, z => z != null && z.zoneID == report.ZoneId);
            if (zone == null)
            {
                reason = $"zone <{report.ZoneId}> is not present on the host";
                return false;
            }

            if ((byte)zone.flareTypeForHandle != report.FlareType)
            {
                reason = $"flare type mismatch (zone <{zone.flareTypeForHandle}>, report <{report.FlareType}>)";
                return false;
            }
        }

        if (!TryResolveRaidId(report.ShooterProfileId, out var raidId, out var resolveReason))
        {
            reason = resolveReason;
            return false;
        }

        if (IsBanned(raidId))
        {
            reason = $"profile <{report.ShooterProfileId}> is banned from exfiltration";
            return false;
        }

        return true;
    }

    private static FlareShootDetectorZone[] FindZones()
    {
        try
        {
            return UnityEngine.Object.FindObjectsOfType<FlareShootDetectorZone>();
        }
        catch (System.Exception exception)
        {
            FikaGlobals.LogWarning($"[FlareExfil] could not enumerate flare zones: {exception.Message}");
            return [];
        }
    }

    internal void NotifyNativeSquadGrants(string zoneId, string shooterProfileId, string[] squadProfiles)
    {
        if (SquadShares() || string.IsNullOrEmpty(zoneId) || squadProfiles == null)
        {
            return;
        }

        var grants = new HashSet<string>();
        foreach (var profileId in squadProfiles)
        {
            if (!string.IsNullOrEmpty(profileId) && profileId != shooterProfileId)
            {
                grants.Add(profileId);
            }
        }

        if (grants.Count > 0)
        {
            _nativeSquadGrants[zoneId] = grants;
        }
    }

    /// <summary>
    /// Squad of <paramref name="local"/>: the same group/team the native
    /// <c>PlayerByPartyAddedInShotList</c> channel expands to.
    /// </summary>
    internal static string[] CollectSquadProfiles(Player local)
    {
        var profiles = new List<string> { local.ProfileId };
        try
        {
            var players = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler?.Players;
            if (players == null)
            {
                return [.. profiles];
            }

            foreach (var other in players.Values)
            {
                if (other == null || other.ProfileId == local.ProfileId)
                {
                    continue;
                }

                if (SameSquad(local, other))
                {
                    profiles.Add(other.ProfileId);
                }
            }
        }
        catch (System.Exception exception)
        {
            FikaGlobals.LogWarning($"[FlareExfil] squad lookup failed: {exception.Message}");
        }
        return [.. profiles];
    }

    private static bool SameSquad(Player first, Player second)
    {
        try
        {
            var groupId = first.GroupId;
            if (!string.IsNullOrEmpty(groupId) && groupId == second.GroupId)
            {
                return true;
            }

            var teamId = first.TeamId;
            return !string.IsNullOrEmpty(teamId) && teamId == second.TeamId;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    internal static bool SquadShares()
    {
        try
        {
            return FikaPlugin.Instance?.Settings?.FlareExtractSquadShares?.Value ?? true;
        }
        catch (System.Exception)
        {
            return true;
        }
    }

    private static bool VerboseProbes()
    {
        try
        {
            return FikaPlugin.Instance?.Settings?.FlareExtractVerboseProbe?.Value ?? false;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    private static void Probe(string message)
    {
        if (VerboseProbes())
        {
            FikaGlobals.LogInfo(message);
        }
    }

    private static void LogRejected(string reason)
    {
        FikaGlobals.LogWarning($"[FlareExfil] rejected {reason}");
    }

    internal static bool TryResolveRaidId(string profileId, out int raidId, out string reason)
    {
        raidId = 0;
        reason = null;
        if (string.IsNullOrEmpty(profileId))
        {
            reason = "empty profile id";
            return false;
        }

        try
        {
            var player = Singleton<GameWorld>.Instance?.GetAlivePlayerByProfileID(profileId);
            if (player == null)
            {
                reason = $"raid id resolution failed for profile <{profileId}>";
                return false;
            }

            raidId = player.RaidId;
            return true;
        }
        catch (System.Exception exception)
        {
            reason = $"raid id resolution threw for profile <{profileId}>: {exception.Message}";
            return false;
        }
    }

    internal static bool IsBanned(int raidId)
    {
        try
        {
            var controller = ExfiltrationController.Instance;
            var banned = controller?.BannedPlayers;
            return banned != null && banned.Contains(raidId);
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    private static bool GrantImmunity(int raidId)
    {
        var granted = false;
        try
        {
            foreach (var registry in UnityEngine.Object.FindObjectsOfType<PlayersWithImmuneToSniperFire>())
            {
                if (registry == null)
                {
                    continue;
                }

                registry.AddImmunePlayer(raidId);
                granted = true;
            }
        }
        catch (System.Exception exception)
        {
            FikaGlobals.LogWarning($"[FlareExfil] failed to grant sniper immunity to raid <{raidId}>: {exception.Message}");
        }

        if (granted)
        {
            Probe($"[FlareProbe] immunity raid=<{raidId}> registries={CountRegistries()} " +
                $"confirm=<{HasImmunity(raidId)}>");
        }
        return granted;
    }

    private static void RemoveImmunity(int raidId)
    {
        try
        {
            foreach (var registry in UnityEngine.Object.FindObjectsOfType<PlayersWithImmuneToSniperFire>())
            {
                if (registry != null)
                {
                    registry.RemoveImmunePlayer(raidId);
                }
            }
        }
        catch (System.Exception exception)
        {
            FikaGlobals.LogWarning($"[FlareExfil] failed to revoke sniper immunity from raid <{raidId}>: {exception.Message}");
        }
    }

    private static int CountRegistries()
    {
        try
        {
            return UnityEngine.Object.FindObjectsOfType<PlayersWithImmuneToSniperFire>().Length;
        }
        catch (System.Exception)
        {
            return 0;
        }
    }

    internal static bool HasImmunity(int raidId)
    {
        try
        {
            foreach (var registry in UnityEngine.Object.FindObjectsOfType<PlayersWithImmuneToSniperFire>())
            {
                if (registry != null && registry.IsPlayerImmuneForFire(raidId))
                {
                    return true;
                }
            }
        }
        catch (System.Exception)
        {
            return false;
        }
        return false;
    }

    private static bool HasLocalImmunity(Player local)
    {
        if (local == null)
        {
            return false;
        }

        try
        {
            foreach (var registry in UnityEngine.Object.FindObjectsOfType<PlayersWithImmuneToSniperFire>())
            {
                if (registry != null && registry.IsPlayerImmuneForFire(local.ProfileId))
                {
                    return true;
                }
            }
        }
        catch (System.Exception)
        {
            return false;
        }
        return false;
    }

    private static Player LocalPlayer => Singleton<GameWorld>.Instance?.MainPlayer;

    private static bool IsHost => FikaSingleton<IFikaNetworkManager>.Instance is FikaServer;

    /// <summary>
    /// Scope of a shared exit countdown that this machine just started, when it
    /// was unlocked by the local player's own flare. Returns false for the
    /// default (squad wide) behaviour and for every non flare gated exit, which
    /// keeps paid exits and secret exits on their existing code path.
    /// </summary>
    internal static bool TryGetLocalActivationScope(ExfiltrationPoint point, out string ownerProfileId, out bool squadWide)
    {
        ownerProfileId = string.Empty;
        squadWide = true;
        if (SquadShares())
        {
            return false;
        }

        var local = LocalPlayer;
        if (local == null || point == null || point is SecretExfiltrationPoint ||
            FikaExfilManager.IsPaidExfiltrationPoint(point))
        {
            return false;
        }

        if (!HasLocalImmunity(local) || !PlayerIsInPoint(point, local))
        {
            return false;
        }

        ownerProfileId = local.ProfileId;
        squadWide = false;
        return true;
    }

    /// <summary>Host side variant used for the exfiltration snapshot.</summary>
    internal static bool TryGetSnapshotScope(ExfiltrationPoint point, out string ownerProfileId, out bool squadWide)
    {
        ownerProfileId = string.Empty;
        squadWide = true;
        if (SquadShares() || point == null || point is SecretExfiltrationPoint ||
            FikaExfilManager.IsPaidExfiltrationPoint(point))
        {
            return false;
        }

        try
        {
            var entered = point.Entered;
            if (entered == null)
            {
                return false;
            }

            string owner = null;
            for (var i = 0; i < entered.Count; i++)
            {
                var player = entered[i];
                if (player == null || !HasLocalImmunity(player))
                {
                    continue;
                }

                if (owner != null)
                {
                    return false;
                }
                owner = player.ProfileId;
            }

            if (owner == null)
            {
                return false;
            }

            ownerProfileId = owner;
            squadWide = false;
            return true;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a scoped activation must be applied on this machine.
    /// <paramref name="ownerProfileId"/> empty means "point wide" (unchanged
    /// behaviour).
    /// </summary>
    internal static bool ShouldApplyScope(string ownerProfileId, bool squadWide)
    {
        if (string.IsNullOrEmpty(ownerProfileId) || SquadShares())
        {
            return true;
        }

        var local = LocalPlayer;
        if (local == null)
        {
            return false;
        }

        if (local.ProfileId == ownerProfileId)
        {
            return true;
        }

        if (!squadWide)
        {
            return false;
        }

        try
        {
            var players = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler?.Players;
            if (players == null)
            {
                return false;
            }

            foreach (var other in players.Values)
            {
                if (other != null && other.ProfileId == ownerProfileId)
                {
                    return SameSquad(local, other);
                }
            }
        }
        catch (System.Exception)
        {
            return false;
        }
        return false;
    }

    private static bool PlayerIsInPoint(ExfiltrationPoint point, Player local)
    {
        try
        {
            var entered = point.Entered;
            if (entered != null)
            {
                for (var i = 0; i < entered.Count; i++)
                {
                    var player = entered[i];
                    if (player != null && player.ProfileId == local.ProfileId)
                    {
                        return true;
                    }
                }
            }

            var queued = point.QueuedPlayers;
            return queued != null && (queued.Contains(local.Id) || queued.Contains(local.RaidId));
        }
        catch (System.Exception)
        {
            return false;
        }
    }
}
