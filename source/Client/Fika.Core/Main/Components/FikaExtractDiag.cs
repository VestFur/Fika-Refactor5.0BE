using System;
using System.Collections.Generic;
using System.Text;
using Comfort.Common;
using CommonAssets.Scripts.Game;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using UnityEngine;

namespace Fika.Core.Main.Components;

/// <summary>
/// The local player's extraction, reported per attempt, plus the release of a transit's exfiltration
/// guard that no longer has a transit to belong to.
/// <para>
/// What this file answers, and nothing else does:
/// </para>
/// <list type="bullet">
/// <item><b>Who refused him.</b> One line per distinct attempt state names the point, its status,
/// whether the native point entered the player, whether an extraction handler exists for him,
/// whether the flare gate allows him, the unmet requirement types, the countdown position and the
/// guard state (<c>ExfiltrationController.BannedPlayers</c>, <c>TransitController.transitPlayers</c>).
/// The two gates in <see cref="FikaExfilManager.Update"/> refuse silently today: a flare refusal is
/// deduplicated to one line per raid, and an unmet requirement says nothing at all.</item>
/// <item><b>Whether the raid stop was asked for.</b> Every call into <c>CoopGame.Extract</c> this
/// manager makes is bracketed, so a call that returns without registering the local player (the
/// early return in <c>HostGameController.Extract</c> / <c>ClientGameController.Extract</c>) is
/// visible instead of looking like nothing happened.</item>
/// <item><b>That the guard is released.</b> Committing to a transit bans the player from
/// exfiltration and disables the exit interaction
/// (<c>FikaHostTransitController.InteractWithTransit</c>, the host half and the remote half).
/// The native controller releases that pair when the transfer ends or is cancelled
/// (<c>TransitController.EnableExits(Player)</c>, which calls
/// <c>ExfiltrationController.EnableExitsInteraction()</c>); this port has no such release, so a
/// transfer that does not take the player - and any state that survives into the next location -
/// leaves him unable to extract. This class releases exactly those entries that no live
/// registration owns.</item>
/// </list>
/// <para>
/// A ban entry is considered live only while it still owns a player who is in this raid, alive and
/// not marked as extracted. That is the same bookkeeping the raid itself uses, so a release can
/// never fire while a transfer is really in flight.
/// </para>
/// </summary>
internal static class FikaExtractDiag
{
    /// <summary>
    /// A committed transit hands the player over within seconds. A registration that is still live
    /// this long after it was applied is a transfer that will not happen.
    /// </summary>
    private const float StaleGuardSeconds = 120f;

    private static float _nextSample;
    private static int _guardRaidId = int.MinValue;
    private static float _guardSince = -1f;

    /// <summary>1 Hz gate for <see cref="Sample"/>.</summary>
    internal static bool ShouldSample()
    {
        var now = Time.time;
        if (now < _nextSample)
        {
            return false;
        }

        _nextSample = now + 1f;
        return true;
    }

    /// <summary>
    /// Runs at raid start, right after the manager bound this raid's exits: reports what was bound
    /// and releases a guard pair left behind by an earlier location before the player can even walk
    /// to an exit.
    /// </summary>
    internal static void OnRaidBound(CoopGame game, int regularCount, int secretCount)
    {
        try
        {
            _guardRaidId = int.MinValue;
            _guardSince = -1f;

            var player = LocalPlayer();
            if (player == null)
            {
                                return;
            }

                        GuardTick(player, "raid start");
        }
        catch (Exception exception)
        {
                    }
    }

    /// <summary>
    /// Per second: the exfiltration guard check of this machine. It releases a transit exfiltration ban
    /// that no longer has a transit to belong to, which is what keeps a transfer that never took the
    /// player from stranding him at every exit of the raid.
    /// </summary>
    internal static void Sample(CoopGame game, ExfiltrationPoint[] points, List<string> handlerPoints, int countdownCount)
    {
        _ = game;
        _ = points;
        _ = handlerPoints;
        _ = countdownCount;
        try
        {
            var player = LocalPlayer();
            if (player == null)
            {
                return;
            }

            GuardTick(player, "tick");
        }
        catch (Exception)
        {
        }
    }

    /// <summary>About to call into <c>CoopGame.Extract</c> for the local player.</summary>
    internal static void ExtractCall(CoopGame game, string pointName, Player player, string source)
    {
        try
        {
            var netId = NetIdOf(player);
            var already = IsMarkedExtracted(game, player);
            FikaGlobals.LogWarning($"[ExtractDiag] extract call point='{pointName}' source={source} profile={player.ProfileId} raidId={player.RaidId} netId={netId} alreadyExtracted={already}{DescribeGuardState(player)} - {(already ? "the game's Extract returns immediately for an already extracted player" : "the raid stop routine is expected to start now")}");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[ExtractDiag] extract call diagnostics failed: {exception}");
        }
    }

    /// <summary>The <c>CoopGame.Extract</c> call returned: did it take the local player?</summary>
    internal static void ExtractReturned(CoopGame game, string pointName, Player player)
    {
        try
        {
            var netId = NetIdOf(player);
            var extracted = IsMarkedExtracted(game, player);
            var tail = extracted
                ? "the player is registered as extracted, the raid stop routine is running"
                : "the player is still not registered as extracted: the call was refused or lost, so nothing will happen at this exit (compare HostGameController.Extract / ClientGameController.Extract)";
            FikaGlobals.LogWarning($"[ExtractDiag] extract call returned point='{pointName}' netId={netId} registeredAsExtracted={extracted} - {tail}");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[ExtractDiag] extract result diagnostics failed: {exception}");
        }
    }

    /// <summary>
    /// Releases the exfiltration guard entry of every player this machine can see whose transit is
    /// no longer real, and re-enables the exit interaction when nothing is left to wait for.
    /// </summary>
    private static void GuardTick(Player player, string context)
    {
        try
        {
            var controller = global::CommonAssets.Scripts.Game.ExfiltrationController.Instance;
            if (controller == null)
            {
                return;
            }

            var banned = controller.BannedPlayers;
            if (banned == null)
            {
                return;
            }

            if (banned.Count == 0)
            {
                _guardSince = -1f;
                return;
            }

            var live = CollectLiveRegistrations();
            var localIds = LocalRaidIds(player);
            var anyLive = false;
            for (var i = 0; i < live.Count; i++)
            {
                if (banned.Contains(live[i]))
                {
                    anyLive = true;
                    break;
                }
            }

            var localLive = false;
            for (var i = 0; i < localIds.Count; i++)
            {
                if (live.Contains(localIds[i]))
                {
                    localLive = true;
                    break;
                }
            }

            if (!anyLive)
            {
                // Not one entry of the set still owns a player who is in this raid, alive and not
                // extracted: nothing is waiting for a transfer, so the whole pair is a leftover.
                var count = banned.Count;
                banned.Clear();
                controller.EnableExitsInteraction();
                _guardSince = -1f;
                FikaGlobals.LogWarning($"[ExtractDiag] released the whole exfiltration ban set ({count} entry(ies), not one of them owns a player still in transit in this raid) and re-enabled the exit interaction ({context}) - the port applies the ban and the interaction disable in FikaHostTransitController.InteractWithTransit (the BannedPlayers.Add and DisableExitsInteraction pair) and never calls the native release TransitController.EnableExits -> ExfiltrationController.EnableExitsInteraction");
                return;
            }

            if (localLive)
            {
                // His own registration is live: still a transfer in flight. Only the window decides.
                if (_guardRaidId != player.RaidId || _guardSince < 0f)
                {
                    _guardRaidId = player.RaidId;
                    _guardSince = Time.time;
                    FikaGlobals.LogWarning($"[ExtractDiag] the local player is registered for a transfer (profile={player.ProfileId} raidId={player.RaidId}): the exits stay disabled and are released after {StaleGuardSeconds:F0} s if the transfer does not take him ({context})");
                    return;
                }

                if (Time.time - _guardSince < StaleGuardSeconds)
                {
                    return;
                }

                FikaGlobals.LogWarning($"[ExtractDiag] the local player's transfer registration is {StaleGuardSeconds:F0} s old and the raid is still running: treating it as a transfer that will not happen ({context})");
            }
            else
            {
                _guardSince = -1f;
            }

            var released = new List<string>();
            for (var i = 0; i < localIds.Count; i++)
            {
                if (banned.Remove(localIds[i]))
                {
                    released.Add($"raidId={localIds[i]} (local player)");
                }
            }

            var others = OtherRaidIds(player);
            for (var i = 0; i < others.Count; i++)
            {
                if (banned.Remove(others[i]))
                {
                    released.Add($"raidId={others[i]} (other player)");
                }
            }

            if (released.Count == 0)
            {
                return;
            }

            var left = banned.Count;
            if (left == 0)
            {
                controller.EnableExitsInteraction();
            }

            FikaGlobals.LogWarning($"[ExtractDiag] released {released.Count} stale exfiltration ban(s) [{string.Join(", ", released)}] ({context}); entries left={left}, exit interaction {(left == 0 ? "re-enabled" : "kept disabled: another registration is still live")}");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[ExtractDiag] guard check failed: {exception}");
        }
    }

    /// <summary>
    /// Raid ids of every transit registration that still owns a player of this raid: the player has
    /// to be present, alive and not already marked as extracted, or the registration is dead.
    /// </summary>
    private static List<int> CollectLiveRegistrations()
    {
        var live = new List<int>();
        try
        {
            var transit = Singleton<GameWorld>.Instance?.TransitController;
            var registrations = transit?.transitPlayers;
            if (registrations == null)
            {
                return live;
            }

            var humans = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler?.HumanPlayers;
            var extracted = FikaSingleton<IFikaGame>.Instance?.ExtractedPlayers;
            var local = LocalPlayer();

            foreach (var entry in registrations)
            {
                var profileId = entry.Key;
                if (string.IsNullOrEmpty(profileId))
                {
                    continue;
                }

                var owner = FindHuman(humans, profileId) ?? (Matches(local, profileId) ? local : null);
                if (owner == null)
                {
                    continue;
                }

                if (owner.HealthController?.IsAlive != true)
                {
                    continue;
                }

                if (extracted != null && extracted.Contains(NetIdOf(owner)))
                {
                    continue;
                }

                live.Add(entry.Value);
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[ExtractDiag] could not read the transit registrations: {exception.Message} - the guard check keeps every ban this tick");
        }

        return live;
    }

    private static Player FindHuman(List<FikaPlayer> humans, string profileId)
    {
        if (humans == null)
        {
            return null;
        }

        for (var i = 0; i < humans.Count; i++)
        {
            var human = humans[i];
            if (human != null && string.Equals(human.ProfileId, profileId, StringComparison.Ordinal))
            {
                return human;
            }
        }

        return null;
    }

    private static bool Matches(Player player, string profileId)
    {
        return player != null && !string.IsNullOrEmpty(profileId)
            && string.Equals(player.ProfileId, profileId, StringComparison.Ordinal);
    }

    /// <summary>The raid ids that belong to the local player, in the two keys the game uses for bans.</summary>
    private static List<int> LocalRaidIds(Player player)
    {
        var ids = new List<int>();
        AddId(ids, player.RaidId);
        AddId(ids, SafeId(player));
        return ids;
    }

    /// <summary>The raid ids of the other human players of this raid.</summary>
    private static List<int> OtherRaidIds(Player local)
    {
        var ids = new List<int>();
        var humans = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler?.HumanPlayers;
        if (humans == null)
        {
            return ids;
        }

        for (var i = 0; i < humans.Count; i++)
        {
            var human = humans[i];
            if (human == null || Matches(local, human.ProfileId))
            {
                continue;
            }

            AddId(ids, human.RaidId);
            AddId(ids, SafeId(human));
        }

        return ids;
    }

    private static void AddId(List<int> ids, int id)
    {
        if (id <= 0 || ids.Contains(id))
        {
            return;
        }

        ids.Add(id);
    }

    /// <summary>
    /// <c>Player.Id</c> is the second key the exfiltration ban is written with natively; the port
    /// writes <c>RaidId</c>. Both are removed, so a set written by either path is released.
    /// </summary>
    private static int SafeId(Player player)
    {
        try
        {
            return player.Id;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static bool IsMarkedExtracted(CoopGame game, Player player)
    {
        try
        {
            var netId = NetIdOf(player);
            if (game != null && game.ExtractedPlayers != null && game.ExtractedPlayers.Contains(netId))
            {
                return true;
            }

            var extracted = FikaSingleton<IFikaGame>.Instance?.ExtractedPlayers;
            return extracted != null && extracted.Contains(netId);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int NetIdOf(Player player)
    {
        try
        {
            if (player.TryCast<FikaPlayer>() is { } fikaPlayer)
            {
                return fikaPlayer.NetId;
            }
        }
        catch (Exception)
        {
            // Reported as -1.
        }

        return -1;
    }

    private static Player LocalPlayer()
    {
        try
        {
            return Singleton<GameWorld>.Instance?.MainPlayer;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsEntered(ExfiltrationPoint point, Player player)
    {
        try
        {
            var entered = point.Entered;
            if (entered == null)
            {
                return false;
            }

            foreach (var other in entered)
            {
                if (other == null)
                {
                    continue;
                }

                if (ReferenceEquals(other, player) || Matches(other, player.ProfileId))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // A destroyed point reports nothing.
        }

        return false;
    }

    private static float DistanceTo(ExfiltrationPoint point, Vector3 position)
    {
        try
        {
            return Vector3.Distance(point.transform.position, position);
        }
        catch (Exception)
        {
            return -1f;
        }
    }

    private static string PointName(ExfiltrationPoint point)
    {
        try
        {
            if (point == null)
            {
                return null;
            }

            var name = point.Settings?.Name;
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static float ExfiltrationTime(ExfiltrationPoint point)
    {
        try
        {
            return point.Settings.ExfiltrationTime;
        }
        catch (Exception)
        {
            return -1f;
        }
    }

    /// <summary>
    /// The state of the signal-flare exit chain on this machine, for the raid start and attempt lines:
    /// <c>triggers=N</c> is the number of scene <c>TriggerFlareSuccess</c> components this map carries
    /// (0 = the map drives flares through detector zones only), <c>gated=[names]</c> are the exits the
    /// classification marked as flare gated, and <c>pending=N</c> counts exit-visual replays still waiting
    /// for the shooter's flare object. A machine that shows <c>triggers=0 gated=[]</c> cannot react to a
    /// flare at all, which is what a map without detector zones and without the trigger chain looks like.
    /// </summary>
    private static string DescribeHeliChain()
    {
        try
        {
            return "triggers=" + FikaFlareExfil.SceneFlareTriggerCount()
                + " pending=" + FikaFlareExfil.PendingVisualCount()
                + " gated=[" + string.Join(",", FikaFlareExfil.GatedPointNames()) + "]";
        }
        catch (Exception exception)
        {
            return "unavailable (" + exception.Message + ")";
        }
    }

    private static string CountdownStart(ExfiltrationPoint point)
    {
        try
        {
            return point.ExfiltrationStartTime.ToString("F1");
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string DescribeInteraction(ExfiltrationPoint point)
    {
        try
        {
            var collider = point.ExtendedCollider;
            if (collider == null)
            {
                return "unknown";
            }

            return collider.enabled ? "on" : "off";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static bool Contains(List<string> names, string name)
    {
        if (names == null)
        {
            return false;
        }

        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The requirement types the game still counts as unmet for this player.</summary>
    private static string DescribeUnmet(ExfiltrationPoint point, Player player)
    {
        try
        {
            var unmet = point.UnmetRequirements(player);
            if (unmet == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var requirement in unmet.AsManaged())
            {
                if (requirement == null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(',');
                }

                builder.Append(RequirementName(requirement));
            }

            return builder.ToString();
        }
        catch (Exception exception)
        {
            return "unreadable:" + exception.GetType().Name;
        }
    }

    private static string RequirementName(ExfiltrationRequirement requirement)
    {
        try
        {
            return requirement.GetIl2CppType().Name;
        }
        catch (Exception)
        {
            try
            {
                return requirement.GetType().Name;
            }
            catch (Exception)
            {
                return "requirement";
            }
        }
    }

    /// <summary>Null while the attempt still has every chance; otherwise the gate that refused it.</summary>
    private static string RefusalReason(bool entered, bool near, bool handled, string mayExtract, string unmet)
    {
        if (!entered && near)
        {
            return "the point never entered the local player (its trigger or its interaction is off, or its status is not usable for him)";
        }

        if (entered && !handled)
        {
            return "the native point entered the player but started no extraction for him (status or requirement gate inside ExfiltrationPoint)";
        }

        if (handled && string.Equals(mayExtract, "False", StringComparison.Ordinal))
        {
            return "the flare gate refused this profile: no activation covers it for this point (FikaFlareExfil.MayExtract)";
        }

        if (handled && !string.IsNullOrEmpty(unmet) && unmet.StartsWith("unreadable", StringComparison.Ordinal) == false && unmet.Length > 0)
        {
            return "unmet requirement(s) kept the countdown path from extracting: " + unmet;
        }

        return null;
    }

    private static string DescribeGuardState(Player player)
    {
        try
        {
            var controller = global::CommonAssets.Scripts.Game.ExfiltrationController.Instance;
            var banned = controller?.BannedPlayers;
            var raidId = 0;
            var alt = 0;
            try
            {
                raidId = player.RaidId;
                alt = SafeId(player);
            }
            catch (Exception)
            {
                // Both stay 0.
            }

            var isBanned = banned != null && (banned.Contains(raidId) || (alt != 0 && alt != raidId && banned.Contains(alt)));
            var registered = "no";
            try
            {
                var transit = Singleton<GameWorld>.Instance?.TransitController;
                var registrations = transit?.transitPlayers;
                if (registrations != null && !string.IsNullOrEmpty(player.ProfileId) && registrations.TryGetValue(player.ProfileId, out var registeredRaidId))
                {
                    registered = registeredRaidId.ToString();
                }
            }
            catch (Exception)
            {
                registered = "unreadable";
            }

            return $" ban={isBanned} bannedCount={banned?.Count ?? -1} transitRegistration={registered}";
        }
        catch (Exception)
        {
            return " ban=unreadable";
        }
    }

}
