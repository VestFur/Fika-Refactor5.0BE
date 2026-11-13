using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.Interactive;
using UnityEngine;

namespace Fika.Core.Main.Components;

/// <summary>
/// BT (analysis/terminal-hostility-graph): the OTHER systems of the terminal / RAUF area that decide to fire.
///
/// <para>The card check of the transfer point is ONE fact, but this build makes more than one system fire on
/// the player of that area. Read together with <see cref="FikaSentryAccess"/> (the sentry rule and its group
/// mirror), the complete set is:</para>
///
/// <list type="number">
/// <item>SENTRY - the sentry boss rule: <c>SentryBossLogic</c> (state <c>_accessedPlayerIds</c>, written by
/// the native entry <c>OnTerminalAccessGained</c>, read by <c>BossSentryHostilityRule</c> through
/// <c>IAccessBossLogic.HaveAccess</c>). Delivered by <see cref="FikaSentryAccess"/> on every machine.</item>
/// <item>GROUP - the hostility tables of every bot group of the area (<c>BotGroupHostility</c>, one container
/// per rule inside <c>BotGroupInnerHostilityData</c>). The sentry access has its own container there
/// (<c>AlliesBySentryAcces</c>), which is the second landing of <see cref="FikaSentryAccess"/>. The other
/// containers (<c>EnemiesByHit</c>, <c>NeutralsByVsRF</c>, <c>EnemiesByAiPlaceAllEnemy</c>, ...) are the
/// product of the other rules and are NOT written by this component - they are only reported, because the
/// brief forbids clearing enemy tables.</item>
/// <item>ZONE - the CODE-DRIVEN shooters of the scene, which fire no bot at all: <c>BorderZone</c> and its
/// subclasses <c>SniperFiringZone</c> (an imitating sniper: shot frequency, hit probabilities, lethal after
/// N shots) and <c>Minefield</c>. They ask <c>PlayersWithImmuneToSniperFire</c>
/// (<c>playersWithImmune : HashSet&lt;Int32&gt;</c>) through <c>IsPlayerCanBeDamaged</c>; the map trigger
/// <c>HandlerSniperZoneImmunity</c> is the native writer of that container. The shot itself is computed by
/// the authority (Fika delivers it with <c>BorderZoneEvent</c> /
/// <c>BorderZone.ProcessIncomingPacket</c>), so the container that matters is the one of the AUTHORITY -
/// which is why this component writes it on every machine that learns the fact.</item>
/// <item>GUARDED - <c>GuardedZone</c>, which keeps its own profile-id list (<c>_guardedPlayerIds</c>) of the
/// players its <c>SniperFireTargetManager</c> does not treat as aggressors. Read only here: the list means
/// 「the player the zone protects」 (its hit fires <c>_onGuardedPlayerHitTriggerId</c>), not 「the player who
/// passed the check」, so it is not a landing of the card.</item>
/// </list>
///
/// <para>WHAT THIS COMPONENT DOES: it hands the fact to system 3 through the native container and the native
/// method (<c>AddImmunePlayer(Int32)</c>), and writes the verdict of systems 2, 3 and 4 for that player into
/// the log of the machine that owns them - so 「which system still refuses this player」 is a fact of the log
/// and not an impression. Nothing else is touched: no rule, no threshold, no enemy table.</para>
///
/// <para>DIAGNOSIS (one grep, the lines this component adds):</para>
/// <code>
/// [SentryDiag] card=swiped sys=immunity state=applied applied=yes side=host source=packet-from-client via=native-immunity-registry registries=2 immune=yes:2/no:0 raidId=2 profile=6acdcb85 -> result=the code-driven zones of this machine now treat the player as immune
/// [SentryDiag] card=swiped sys=zone state=verify applied=yes side=host zones=2 sniper=1 minefield=0 targeted=0 immune=1/1 guarded=1 guardedMine=0 -> result=no code-driven zone of this machine fires at the player
/// [SentryDiag] card=swiped sys=group state=verify applied=- side=host groups=4 hostile=1 profile=6acdcb85 -> result=1 group(s) of this machine still hold the player as an enemy
/// [SentryDiag] card=swiped sys=damage state=verify side=client type=Sniper source=none hits=3 profile=6acdcb85 -> result=a code-driven zone fired at this player (no bot fired)
/// [SentryDiag] card=swiped sys=immunity state=verify applied=no side=host raidId=2 -> result=the native chain of this machine wrote the immunity container (not this component)
/// </code>
/// <para>One switch reverts each half (<see cref="Enabled"/>, <see cref="ImmunityEnabled"/>,
/// <see cref="CensusEnabled"/>, <see cref="HitProbeEnabled"/>), see the report for the one-line rollback.</para>
/// </summary>
internal static class FikaTerminalHostility
{
    /// <summary>
    /// Master switch of the fan-out. OFF (now the default) = the other firing systems are left exactly as the
    /// native chain of this machine left them.
    /// </summary>
    /// <remarks>
    /// (hotfix 2026-10-14) OFF by default after the first live test: the client took no damage at all and the
    /// host process died the moment its own player took fall damage. Both symptoms sit on exactly this half of
    /// the round, so it stays off until the scope of the write is settled (the authority only, and only what the
    /// native trigger of that map writes), and until the damage-path observation patches can be installed behind
    /// a startup switch instead of always (see <c>TerminalHostilityPatches</c>).
    /// </remarks>
    internal static bool Enabled = false;

    /// <summary>
    /// Landing 3 of the same fact: the native immunity container of the code-driven zones. OFF (now the default)
    /// = the container is never written from here; the refusal is still reported as a line.
    /// </summary>
    /// <remarks>(hotfix 2026-10-14) OFF by default: this is the write that makes a player untouchable.</remarks>
    internal static bool ImmunityEnabled = false;

    /// <summary>The read-only verdicts of the systems of this machine (the evidence half).</summary>
    internal static bool CensusEnabled = true;

    /// <summary>The damage provenance line: what actually hit this machine's player.</summary>
    /// <remarks>
    /// (hotfix 2026-10-14) OFF by default together with the observation patches of the damage path; without them
    /// installed this line has no trigger.
    /// </remarks>
    internal static bool HitProbeEnabled = false;

    /// <summary>The landing a line names: the native immunity container (<c>AddImmunePlayer</c>).</summary>
    internal const string ViaImmunityRegistry = "native-immunity-registry";

    /// <summary>No landing of this component reached the system of the line.</summary>
    internal const string ViaNone = "none";

    private const int MaxZoneLines = 6;
    private const int MaxGroupLines = 6;
    private const int MaxHitLines = 48;
    private const int EveryNthHit = 10;

    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> HitCounts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> ImmuneVerified = new(StringComparer.Ordinal);
    private static int _censusRuns;

    /// <summary>True while this component itself writes the immunity container (the native patch tells them apart).</summary>
    private static bool _writingImmunity;

    internal static void Reset()
    {
        lock (Keys)
        {
            Keys.Clear();
            HitCounts.Clear();
            ImmuneVerified.Clear();
            _censusRuns = 0;
            _writingImmunity = false;
        }
    }

    /// <summary>
    /// The fan-out: one call per (player, access fact) on the machine that owns the answer. Called from
    /// <see cref="FikaSentryAccess.Apply"/> (every machine that applies the fact, the authority included)
    /// and from <c>FikaSentryAccess.NoteNativeEmission</c> (the machine that ran the interaction itself).
    /// </summary>
    internal static void Deliver(Player player, string source, bool granted)
    {
        try
        {
            if (player == null)
            {
                return;
            }

            var profileId = player.ProfileId;
            if (!Enabled)
            {
                Line("fanout-off:" + Short(profileId) + ":" + source,
                    "card=swiped sys=fanout state=applied applied=no side=" + Side + " source=" + source
                    + " profile=" + Short(profileId)
                    + " -> result=fanout-off (FikaTerminalHostility.Enabled=false): the other firing systems of this machine keep whatever the native chain gave them");
                return;
            }

            if (granted && ImmunityEnabled)
            {
                ApplyImmunity(player, source);
            }
            else if (granted)
            {
                Line("immunity-off:" + Short(profileId) + ":" + source,
                    "card=swiped sys=immunity state=applied applied=no side=" + Side + " source=" + source
                    + " via=" + ViaNone + " profile=" + Short(profileId)
                    + " -> result=immunity-off (FikaTerminalHostility.ImmunityEnabled=false): the code-driven zones of this machine were not told");
            }

            Census(player, source);
        }
        catch (Exception exception)
        {
            Line("fanout-error:" + Short(player?.ProfileId),
                "card=swiped sys=fanout state=applied applied=no side=" + Side + " source=" + source
                + " -> result=fanout-threw " + exception.GetType().Name);
        }
    }

    /// <summary>
    /// Landing 3: the native container the code-driven zones read. The write goes through the native method
    /// of the container itself (<c>AddImmunePlayer(Int32)</c>, the same entry point the flare collector and
    /// the map trigger <c>HandlerSniperZoneImmunity</c> use) and the answer is read back with the native
    /// question the zones ask (<c>IsPlayerImmuneForFire(Int32)</c>).
    ///
    /// <para>The containers of this build are <c>PlayersWithImmuneToSniperFire</c> and its subclass
    /// <c>SniperFireTargetManager</c> (which overrides both members). A raid without any of them has no
    /// code-driven zone at all, and the line says so.</para>
    /// </summary>
    internal static void ApplyImmunity(Player player, string source)
    {
        var profileId = player.ProfileId;
        var raidId = RaidIdOf(player);
        var registries = FindImmunityRegistries();
        var verified = 0;
        var failed = 0;
        var kinds = new List<string>();
        var raidIdText = raidId > 0 ? raidId.ToString() : "-";

        for (var i = 0; i < registries.Length; i++)
        {
            var registry = registries[i];
            var kind = RegistryKind(registry);
            if (!kinds.Contains(kind))
            {
                kinds.Add(kind);
            }

            if (raidId <= 0)
            {
                failed++;
                continue;
            }

            try
            {
                lock (Keys)
                {
                    _writingImmunity = true;
                }

                registry.AddImmunePlayer(raidId);
            }
            catch (Exception exception)
            {
                failed++;
                Line("immunity-write-error:" + Short(profileId) + ":" + i,
                    "card=swiped sys=immunity state=applied applied=no side=" + Side + " source=" + source
                    + " registry=" + kind + " raidId=" + raidIdText
                    + " -> result=AddImmunePlayer threw " + exception.GetType().Name);
            }
            finally
            {
                lock (Keys)
                {
                    _writingImmunity = false;
                }
            }

            try
            {
                if (registry.IsPlayerImmuneForFire(raidId))
                {
                    verified++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception)
            {
                failed++;
            }
        }

        var changed = true;
        lock (Keys)
        {
            if (ImmuneVerified.TryGetValue(profileId, out var last))
            {
                changed = last != verified;
            }

            ImmuneVerified[profileId] = verified;
        }

        if (!changed)
        {
            return;
        }

        var verdict = registries.Length == 0
            ? "no-code-driven-zone-on-this-machine (a raid without a PlayersWithImmuneToSniperFire container has no sniper zone to stand down)"
            : verified == registries.Length && verified > 0
                ? "the code-driven zones of this machine now treat the player as immune"
                : "the native container did not take the raid id on every registry: the code-driven zones of this machine can still fire at the player";
        Line("immunity:" + Short(profileId) + ":" + source + ":" + verified + ":" + registries.Length,
            "card=swiped sys=immunity state=applied applied=" + (verified > 0 ? "yes" : "no")
            + " side=" + Side + " source=" + source + " via=" + (registries.Length > 0 ? ViaImmunityRegistry : ViaNone)
            + " registries=" + registries.Length + " kinds=[" + string.Join(",", kinds) + "]"
            + " immune=yes:" + verified + "/no:" + failed
            + " raidId=" + raidIdText + " profile=" + Short(profileId)
            + " -> result=" + verdict);
    }

    /// <summary>
    /// The read-only verdict of every firing system of this machine for one player: the code-driven zones
    /// (3), the group hostility tables of the bots of this machine (2) and the guarded zone (4). Written at
    /// the moment the fact lands and at the moment the player is hit, so the two can be aligned.
    /// </summary>
    internal static void Census(Player player, string source)
    {
        if (!CensusEnabled || player == null)
        {
            return;
        }

        var profileId = player.ProfileId;
        var raidId = RaidIdOf(player);

        try
        {
            var zones = FindZones();
            var sniper = 0;
            var minefield = 0;
            var targeted = 0;
            var immune = 0;
            var immuneKnown = 0;
            var registryMissing = 0;

            for (var i = 0; i < zones.Length; i++)
            {
                var zone = zones[i];
                var kind = ZoneKind(zone);
                if (kind == "sniper")
                {
                    sniper++;
                }
                else if (kind == "minefield")
                {
                    minefield++;
                }

                var isTargeted = IsTargeted(zone, profileId);
                if (isTargeted)
                {
                    targeted++;
                }

                var immuneState = ZoneImmune(zone, raidId);
                if (immuneState < 0)
                {
                    registryMissing++;
                }
                else
                {
                    immuneKnown++;
                    if (immuneState == 1)
                    {
                        immune++;
                    }
                }

                if (i < MaxZoneLines)
                {
                    Line("zone:" + Short(profileId) + ":" + zone.Id + ":" + isTargeted + ":" + immuneState,
                        "card=swiped sys=zone state=verify applied=" + (isTargeted && immuneState != 1 ? "no" : "yes")
                        + " side=" + Side + " source=" + source + " zone=" + zone.Id + " kind=" + kind
                        + " targeted=" + (isTargeted ? "yes" : "no")
                        + " immune=" + (immuneState < 0 ? "no-registry" : immuneState == 1 ? "yes" : "no")
                        + " profile=" + Short(profileId)
                        + " -> result=" + (isTargeted ? "the code-driven zone of this machine holds the player as a target" : "this player is not in the target list of this zone"));
                }
            }

            var guarded = FindGuardedZones();
            var guardedMine = 0;
            for (var i = 0; i < guarded.Length; i++)
            {
                if (GuardedContains(guarded[i], profileId))
                {
                    guardedMine++;
                }
            }

            Line("zone-summary:" + Short(profileId) + ":" + source + ":" + zones.Length + ":" + targeted + ":" + immune,
                "card=swiped sys=zone state=verify applied=" + (targeted > 0 && immune == 0 ? "no" : "yes")
                + " side=" + Side + " source=" + source
                + " zones=" + zones.Length + " sniper=" + sniper + " minefield=" + minefield
                + " targeted=" + targeted + " immune=" + immune + "/" + immuneKnown
                + " noRegistry=" + registryMissing
                + " guarded=" + guarded.Length + " guardedMine=" + guardedMine
                + " profile=" + Short(profileId)
                + " -> result=" + (zones.Length == 0
                    ? "this machine holds no code-driven zone at all"
                    : targeted > 0
                        ? "a code-driven zone of this machine holds this player as a target (the immunity container is the place to look)"
                        : "no code-driven zone of this machine targets this player"));
        }
        catch (Exception exception)
        {
            Line("zone-scan-error:" + Short(profileId),
                "card=swiped sys=zone state=verify applied=no side=" + Side + " source=" + source
                + " -> result=zone-scan-threw " + exception.GetType().Name);
        }

        try
        {
            var groups = LocalGroups();
            var hostile = 0;
            for (var i = 0; i < groups.Count; i++)
            {
                var verdict = GroupVerdict(groups[i], raidId);
                var isHostile = verdict.StartsWith("enemy", StringComparison.Ordinal);
                if (isHostile)
                {
                    hostile++;
                }

                if (i < MaxGroupLines)
                {
                    Line("group:" + Short(profileId) + ":" + IdOf(groups[i]) + ":" + source + ":" + verdict,
                        "card=swiped sys=group state=verify applied=" + (isHostile ? "no" : "yes")
                        + " side=" + Side + " source=" + source + " group=" + IdOf(groups[i])
                        + " profile=" + Short(profileId) + " " + verdict
                        + " -> result=" + (isHostile
                            ? "this group of this machine still decides to fire on the player, and the container above names the rule that holds him"
                            : "this group of this machine does not fire on the player"));
                }
            }

            Line("group-summary:" + Short(profileId) + ":" + source + ":" + groups.Count + ":" + hostile,
                "card=swiped sys=group state=verify applied=" + (hostile > 0 ? "no" : "yes")
                + " side=" + Side + " source=" + source + " groups=" + groups.Count + " hostile=" + hostile
                + " profile=" + Short(profileId)
                + " -> result=" + (groups.Count == 0
                    ? "this machine owns no bot group at all (a Fika client holds every bot as an observed puppet)"
                    : hostile > 0
                        ? hostile + " group(s) of this machine still hold the player as an enemy"
                        : "no group of this machine holds the player as an enemy"));
        }
        catch (Exception exception)
        {
            Line("group-scan-error:" + Short(profileId),
                "card=swiped sys=group state=verify applied=no side=" + Side + " source=" + source
                + " -> result=group-scan-threw " + exception.GetType().Name);
        }

        lock (Keys)
        {
            _censusRuns++;
        }
    }

    /// <summary>
    /// The provenance of one hit on this machine's player: which damage type arrived and whether a player of
    /// this raid caused it. This is the line that tells a bot volley (a source) from the code-driven systems
    /// (no source: <c>Sniper</c> for the zones of the map border and of the guarded area, <c>Explosion</c> or
    /// <c>Landmine</c> for a minefield, shelling or a script handler).
    /// </summary>
    internal static void NoteDamage(Player victim, DamageInfo info)
    {
        try
        {
            if (!HitProbeEnabled || victim == null)
            {
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            var local = world?.MainPlayer;
            if (local == null || local.ProfileId != victim.ProfileId)
            {
                return;
            }

            var type = info.DamageType;

            // A code-driven source of the scene carries no shooter at all; a bot or a player carries both a
            // source object and its id. Both readings are taken defensively: this probe is a diagnostic.
            var hasSource = false;
            try
            {
                hasSource = info.Player != null;
            }
            catch (Exception)
            {
            }

            var sourceId = "-";
            try
            {
                var id = info.SourceId;
                if (!string.IsNullOrEmpty(id))
                {
                    sourceId = Short(id);
                }
            }
            catch (Exception)
            {
            }

            var source = hasSource ? sourceId : "none";

            var key = "hit:" + type + ":" + source;
            int count;
            lock (Keys)
            {
                HitCounts.TryGetValue(key, out count);
                count++;
                HitCounts[key] = count;
            }

            if (count > MaxHitLines || (count != 1 && count % EveryNthHit != 0))
            {
                return;
            }

            var verdict = type == EDamageType.Sniper
                ? "a code-driven zone fired at this player (no bot fired: the sniper of the map border or of the guarded area of this machine or of the authority)"
                : source == "none"
                    ? "the damage carries no shooter: a code-driven source of the scene applied it (zone, minefield, shelling or a script handler)"
                    : "a shooter of this raid applied it (the group tables of the firing machine are the place to look)";
            Line(key + ":" + count,
                "card=swiped sys=damage state=verify applied=- side=" + Side + " source=" + source
                + " type=" + type + " damage=" + Math.Round(info.Damage, 1)
                + " hits=" + count + " profile=" + Short(victim.ProfileId)
                + " -> result=" + verdict);

            if (count == 1)
            {
                // The moment the player is hit is the moment every verdict has to be read at: the fact that
                // the swipe landed and the fact that a system still fires have to be comparable line by line.
                Census(victim, "hit");
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The shot of a code-driven zone ARRIVED FROM THE AUTHORITY on this machine
    /// (<c>BorderZone.ProcessIncomingPacket</c> is what the Fika <c>BorderZoneEvent</c> calls). A line here
    /// names the zone and proves that the machine that decided to fire is the authority - and that its
    /// immunity container did not hold this player.
    /// </summary>
    internal static void NoteZonePacket(BorderZone zone, IObserverToPlayerBridge bridge)
    {
        try
        {
            if (zone == null || bridge == null)
            {
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            var local = world?.MainPlayer;
            if (local == null)
            {
                return;
            }

            var profileId = "-";
            try
            {
                var player = bridge.iPlayer;
                if (player != null)
                {
                    profileId = player.ProfileId;
                }
            }
            catch (Exception)
            {
            }

            if (string.IsNullOrEmpty(profileId) || profileId != local.ProfileId)
            {
                return;
            }

            var kind = ZoneKind(zone);
            Line("zone-hit:" + Short(profileId) + ":" + zone.Id + ":" + kind,
                "card=swiped sys=zone state=verify applied=no side=" + Side + " source=host-packet zone=" + zone.Id
                + " kind=" + kind + " profile=" + Short(profileId)
                + " -> result=the authority fired a code-driven zone of this map at this player (this machine only applies the shot)");
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The verdict of the code-driven sniper of this machine for one player, read through the native question
    /// (<c>SniperFiringZone.IsPlayerCanBeDamaged(IPlayer)</c>). One line per (zone, player, verdict).
    /// </summary>
    internal static void NoteZoneVerdict(SniperFiringZone zone, IPlayer player, bool canBeDamaged)
    {
        try
        {
            if (zone == null || player == null || player.IsAI)
            {
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            var local = world?.MainPlayer;
            if (local == null || local.ProfileId != player.ProfileId)
            {
                return;
            }

            var raidId = RaidIdOf(local);
            var registry = ZoneRegistry(zone);
            Line("zone-verdict:" + Short(player.ProfileId) + ":" + zone.Id + ":" + canBeDamaged,
                "card=swiped sys=zone state=verify applied=" + (canBeDamaged ? "no" : "yes") + " side=" + Side
                + " zone=" + zone.Id + " canBeDamaged=" + (canBeDamaged ? "yes" : "no")
                + " immune=" + (registry == null ? "no-registry" : registry.IsPlayerImmuneForFire(raidId) ? "yes" : "no")
                + " profile=" + Short(player.ProfileId)
                + " -> result=" + (canBeDamaged
                    ? "the code-driven sniper zone of this machine still fires at this player (the native question said yes)"
                    : "the code-driven sniper zone of this machine stands down for this player"));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The native container was written BY THE NATIVE CHAIN of this machine (the flare collector or the map
    /// trigger <c>HandlerSniperZoneImmunity</c>), not by this component. This is the evidence that answers
    /// 「does the native chain of this map grant the immunity at all, and on which machine」.
    /// </summary>
    internal static void NoteNativeImmunityWrite(PlayersWithImmuneToSniperFire registry, int raidId, bool added)
    {
        try
        {
            lock (Keys)
            {
                if (_writingImmunity)
                {
                    return;
                }
            }

            Line("immunity-native:" + Short(registry.GetType().Name) + ":" + raidId + ":" + added,
                "card=swiped sys=immunity state=verify applied=" + (added ? "yes" : "no") + " side=" + Side
                + " via=native-chain registry=" + registry.GetType().Name + " raidId=" + raidId
                + " -> result=the native chain of this machine " + (added
                    ? "wrote the sniper immunity container (the code-driven zones of this machine stand down for that player)"
                    : "removed the player from the sniper immunity container"));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The guarded area marked a player as an aggressor of its sniper target manager: the code-driven system
    /// of this machine decided to shoot him, with no bot involved at all.
    /// </summary>
    internal static void NoteAggressorMark(SniperFireTargetManager manager, int raidId)
    {
        try
        {
            if (manager == null || raidId <= 0)
            {
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            var local = world?.MainPlayer;
            if (local == null || RaidIdOf(local) != raidId)
            {
                return;
            }

            Line("aggressor:" + raidId + ":" + manager.GetInstanceID(),
                "card=swiped sys=zone state=verify applied=no side=" + Side + " source=guarded-zone"
                + " registry=" + RegistryKind(manager) + " raidId=" + raidId
                + " -> result=the guarded area of this machine marked the player as an aggressor of its sniper (a code-driven system, no bot involved)");
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The enemy table of one group of this machine received a player. The container the group keeps him in
    /// names the rule that decided it, which is what the report reads when a group of the authority still
    /// fires after the fact landed.
    /// </summary>
    internal static void NoteGroupEnemy(BotsGroup group, IPlayer player)
    {
        try
        {
            if (player == null || player.IsAI)
            {
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            var local = world?.MainPlayer;
            if (local == null || local.ProfileId != player.ProfileId)
            {
                return;
            }

            var raidId = RaidIdOf(local);
            Line("group-enemy:" + Short(player.ProfileId) + ":" + IdOf(group) + ":" + raidId,
                "card=swiped sys=group state=verify applied=no side=" + Side + " source=enemy-table"
                + " group=" + IdOf(group)
                + " profile=" + Short(player.ProfileId) + " " + GroupVerdict(group, raidId)
                + " -> result=the hostility recalculation of this machine put the player into the enemy side of this group");
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The verdict of one group of this machine for one player, read from the native state itself: the four
    /// tables of <c>BotGroupHostility</c> and every per-rule container of <c>BotGroupInnerHostilityData</c>
    /// that speaks about a player id (the same id space as <c>Player.RaidId</c>). The container that holds
    /// the player names the system that still refuses him.
    /// </summary>
    private static string GroupVerdict(BotsGroup group, int raidId)
    {
        try
        {
            var hostility = group?.BotGroupHostility;
            if (hostility == null)
            {
                return "state=no-hostility-data";
            }

            var inner = hostility.InnerHostilityData;
            var status = StatusOf(hostility, raidId);
            var enemy = status == "Enemy";
            var text = (enemy ? "enemy" : "not-enemy")
                + " status=" + status
                + " tables=enemies:" + Number(() => hostility.Enemies.Count)
                + "/warns:" + Number(() => hostility.Warns.Count)
                + "/neutrals:" + Number(() => hostility.Neutrals.Count)
                + "/allies:" + Number(() => hostility.Allies.Count)
                + " rules="
                + "sentryAccess:" + Flag(() => inner.AlliesBySentryAcces.Contains(raidId))
                + ",byHit:" + Flag(() => inner.EnemiesByHit.Contains(raidId))
                + ",vsrfHit:" + Flag(() => inner.EnemiesByHitVsRF.ContainsKey(raidId))
                + ",vsrfNeutral:" + Flag(() => inner.NeutralsByVsRF.Contains(raidId))
                + ",placeAllEnemy:" + Flag(() => inner.EnemiesByAiPlaceAllEnemy.Contains(raidId))
                + ",follow:" + Flag(() => inner.NeutralsByFollow.Contains(raidId))
                + ",followHit:" + Flag(() => inner.EnemiesByFollowGetHit.Contains(raidId))
                + ",warn:" + Flag(() => inner.EnemiesByWarn.Contains(raidId))
                + ",pairLogic:" + Flag(() => inner.EnemiesByPairLogic.Contains(raidId))
                + ",khorovod:" + Flag(() => inner.EnemiesByKhorovod.Contains(raidId))
                + ",migrate:" + Flag(() => inner.EnemiesByMigrate.Contains(raidId))
                + ",markUnknown:" + Flag(() => inner.EnemiesByMarkOfUnknownDistance.Contains(raidId));
            return text;
        }
        catch (Exception exception)
        {
            return "state=read-threw-" + exception.GetType().Name;
        }
    }

    private static string StatusOf(BotGroupHostility hostility, int raidId)
    {
        try
        {
            var status = hostility.LastHostilityStatus;
            if (status != null && status.TryGetValue(raidId, out var value))
            {
                return value.ToString();
            }
        }
        catch (Exception)
        {
        }

        return "unknown";
    }

    /// <summary>
    /// One container of the native state answers about a player id. The read is a lambda because the il2cpp
    /// wrappers of the containers are not a CLR collection type this file could name; a throw (a missing
    /// container) is reported as such instead of being read as a no.
    /// </summary>
    private static string Flag(Func<bool> read)
    {
        try
        {
            return read() ? "yes" : "no";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    /// <summary>The number of entries of one native table, or -1 when this machine has none of it.</summary>
    private static int Number(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static PlayersWithImmuneToSniperFire[] FindImmunityRegistries()
    {
        try
        {
            PlayersWithImmuneToSniperFire[] registries = UnityEngine.Object.FindObjectsOfType<PlayersWithImmuneToSniperFire>(true);
            return registries ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static BorderZone[] FindZones()
    {
        try
        {
            BorderZone[] zones = UnityEngine.Object.FindObjectsOfType<BorderZone>(true);
            return zones ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static GuardedZone[] FindGuardedZones()
    {
        try
        {
            GuardedZone[] zones = UnityEngine.Object.FindObjectsOfType<GuardedZone>(true);
            return zones ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string ZoneKind(BorderZone zone)
    {
        try
        {
            if (zone.TryCast<SniperFiringZone>() != null)
            {
                return "sniper";
            }

            if (zone.TryCast<Minefield>() != null)
            {
                return "minefield";
            }
        }
        catch (Exception)
        {
        }

        return "border";
    }

    private static string RegistryKind(PlayersWithImmuneToSniperFire registry)
    {
        try
        {
            if (registry.TryCast<SniperFireTargetManager>() != null)
            {
                return "SniperFireTargetManager";
            }
        }
        catch (Exception)
        {
        }

        try
        {
            return registry.GetType().Name;
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static PlayersWithImmuneToSniperFire ZoneRegistry(SniperFiringZone zone)
    {
        try
        {
            return zone._playersWithImmuneToFire;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int ZoneImmune(BorderZone zone, int raidId)
    {
        try
        {
            var sniper = zone.TryCast<SniperFiringZone>();
            if (sniper == null || raidId <= 0)
            {
                return -1;
            }

            var registry = ZoneRegistry(sniper);
            if (registry == null)
            {
                return -1;
            }

            return registry.IsPlayerImmuneForFire(raidId) ? 1 : 0;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static bool IsTargeted(BorderZone zone, string profileId)
    {
        try
        {
            var targeted = zone.TargetedPlayers;
            if (targeted == null || string.IsNullOrEmpty(profileId))
            {
                return false;
            }

            for (var i = 0; i < targeted.Count; i++)
            {
                var player = targeted[i];
                if (player != null && string.Equals(player.ProfileId, profileId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    private static bool GuardedContains(GuardedZone zone, string profileId)
    {
        try
        {
            var guarded = zone._guardedPlayerIds;
            return guarded != null && !string.IsNullOrEmpty(profileId) && guarded.Contains(profileId);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The distinct bot groups of this machine (a Fika client owns none: every bot of the raid is an
    /// observed puppet there, which is what keeps the group half of the census empty on that machine).
    /// </summary>
    private static List<BotsGroup> LocalGroups()
    {
        var result = new List<BotsGroup>();
        var seen = new HashSet<int>();
        try
        {
            var world = Singleton<GameWorld>.Instance;
            var list = world?.AllAlivePlayersList;
            if (list == null)
            {
                return result;
            }

            for (var i = list.Count - 1; i >= 0; i--)
            {
                try
                {
                    var player = list[i];
                    var ai = player?.AIData;
                    if (ai == null || !ai.IsAI)
                    {
                        continue;
                    }

                    var owner = ai.BotOwner;
                    var group = owner?.BotsGroup;
                    if (group == null)
                    {
                        continue;
                    }

                    var id = IdOf(group);
                    if (seen.Add(id))
                    {
                        result.Add(group);
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    /// <summary>
    /// The identity of one native group of this machine. <c>BotsGroup</c> is a plain il2cpp object (it is not
    /// a UnityEngine.Object), so its own pointer is what keeps two groups apart in the log.
    /// </summary>
    private static int IdOf(BotsGroup group)
    {
        try
        {
            return group == null ? 0 : unchecked((int)group.Pointer.ToInt64());
        }
        catch (Exception)
        {
            return 0;
        }
    }

    internal static int RaidIdOf(Player player)
    {
        try
        {
            return player?.RaidId ?? -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    internal static string Side => FikaSentryAccess.Side;

    private static string Short(string value) =>
        string.IsNullOrEmpty(value) ? "-" : value.Length > 8 ? value.Substring(0, 8) : value;

    /// <summary>
    /// The same bounded, deduplicated writer as the sentry family (<c>[SentryDiag]</c>, dual written into
    /// LogOutput.log and Fika-diagnostics.log), so one grep reads both families at once.
    /// </summary>
    internal static void Line(string key, string text) => FikaSentryAccess.Line(key, text);
}
