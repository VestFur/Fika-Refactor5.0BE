using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Comfort.Common;
using CommonAssets.Scripts.Game;
using EFT;
using EFT.GlobalEvents;
using EFT.Interactive;
using EFT.InventoryLogic;
using Fika.Core;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Communication;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using JsonType;

namespace Fika.Headless.Classes;

/// <summary>
/// Transit controller for the <see cref="GameMode.HeadlessGame"/>
/// </summary>
public class FikaHeadlessTransitController : TransitController
{
    public FikaHeadlessTransitController(IntPtr pointer) : base(pointer)
    {
    }

    public FikaHeadlessTransitController(TransitGlobalSettings settings, Il2CppReferenceArray<LocationSettings.Location.TransitParameters> parameters,
        LocalRaidSettings localRaidSettings) : base(ClassInjector.DerivedConstructorPointer<FikaHeadlessTransitController>())
    {
        ClassInjector.DerivedConstructorBody(this);
        _server = FikaSingleton<FikaServer>.Instance;
        _playersInTransitZone = [];
        _transittedPlayers = [];
        ClassInjector.InvokeBaseConstructor<TransitController>(this, settings, parameters);
        _localRaidSettings = localRaidSettings;
        IsEvent = localRaidSettings.transitionType.HasFlagNoBox(ELocationTransition.Event);
        // The visited-location chain is derived in one place for every machine (FikaTransitHistory), so a
        // dedicated host and a playable host cannot disagree about the maps of a transfer history.
        var maps = FikaTransitHistory.AppendLocation(localRaidSettings);
        if (TarkovApplication.Exist(out var app) && app.Session?.Profile is { } profile)
        {
            summonedTransits[profile.Id] = new Transit(localRaidSettings.transition.transitionRaidId,
                localRaidSettings.transition.transitionCount, maps, IsEvent);
        }

        TransferItemsController.InitItemControllerServer(FikaGlobals.TransitTraderId, FikaGlobals.TransitTraderName);
        _onHeadlessPlayerEnter = new Action<TransitPoint, Player>(HeadlessOnPlayerEnter);
        _onHeadlessPlayerExit = new Action<TransitPoint, Player>(HeadlessOnPlayerExit);
        OnPlayerEnter += _onHeadlessPlayerEnter;
        OnPlayerExit += _onHeadlessPlayerExit;
    }

    public bool IsEvent { get; }

    public LocalRaidSettings LocalRaidSettings
    {
        get
        {
            return _localRaidSettings;
        }
    }

    private readonly LocalRaidSettings _localRaidSettings;
    private readonly FikaServer _server;
    private readonly Il2CppSystem.Action<TransitPoint, Player> _onHeadlessPlayerEnter;
    private readonly Il2CppSystem.Action<TransitPoint, Player> _onHeadlessPlayerExit;
    private readonly Dictionary<string, int> _playersInTransitZone;
    /// <summary>
    /// Point ids whose "not everyone is here yet" answer was already reported for the
    /// player that was interacting, so a retry does not repeat the same line.
    /// </summary>
    private readonly HashSet<string> _zoneCheckLogged = [];
    /// <summary>
    /// Zone-exit reports already explained once per player and point; a repeated spurious "left"
    /// from the client's geometric fallback is then one line instead of one per report.
    /// </summary>
    private readonly HashSet<string> _zoneExitLogged = [];
    private bool _headlessTransit;
    private readonly List<int> _transittedPlayers;
    private string _usedPoint;
    /// <summary>
    /// Profile id -> point id of every player whose transit interaction this host accepted. The strict
    /// start gate reads it (see <see cref="EveryoneInteracted"/>): a press of a player whose team is not
    /// complete yet is remembered, nothing is registered and no countdown runs.
    /// </summary>
    private readonly Dictionary<string, int> _interactedPlayers = [];
    /// <summary>One accepted-interaction line per player and point, so a retry does not repeat it.</summary>
    private readonly HashSet<string> _interactionLogged = [];
    /// <summary>Players this host already pushed a transfer for; keeps the drivers from double-sending.</summary>
    private readonly HashSet<int> _transferIssued = [];
    /// <summary>
    /// Why a registered player of the last transfer round did not get his transfer, one entry per player;
    /// the countdown-end report prints it (mirror of the playable host's ledger).
    /// </summary>
    private readonly List<string> _transferFailures = [];
    /// <summary>Point the countdown currently runs for, or -1.</summary>
    private int _countdownPointId = -1;
    /// <summary>Unscaled time the running countdown reaches zero.</summary>
    private float _countdownEndsAt;
    /// <summary>Length of the running countdown in seconds (the point's own time).</summary>
    private int _countdownSeconds;
    /// <summary>Last second that was broadcast, so one packet and one line per second.</summary>
    private int _lastBroadcastSecond = -1;
    /// <summary>Countdown generation: an old async clock of a previous raid stops on the next step.</summary>
    private int _countdownGeneration;
    /// <summary>True while the countdown clock of this controller runs.</summary>
    private bool _countdownLoopRunning;
    /// <summary>Hash, keys and player count of the last native Transit call, reused for the transfers this host issues itself.</summary>
    private string _lastTransitHash;
    private Il2CppSystem.Collections.Generic.Dictionary<string, ProfileKey> _lastTransitKeys;
    private int _lastTransitPlayersCount;
    /// <summary>Mirror of the playable host's per-raid interaction characters, so a retry does not repeat a line.</summary>
    private readonly HashSet<string> _gateLogged = [];
    /// <summary>Last gate-recheck line of the running countdown, so one line appears per verdict change.</summary>
    private string _recheckLoggedSignature;
    /// <summary>Gate rechecks of the running countdown, for the cancel line.</summary>
    private int _recheckCount;
    /// <summary>Failed gate rechecks of this raid, reported at most a few times.</summary>
    private int _recheckFailures;

    public int AliveTransitPlayers
    {
        get
        {
            return _transittedPlayers.Count;
        }
    }

    private void HeadlessOnPlayerEnter(TransitPoint point, Player player)
    {
#if DEBUG
        FikaGlobals.LogInfo($"{player.Profile.Info.Nickname} entered transit point {point.Description}");
#endif

        if (!TryGetAccessToLocation(player, point.parameters.id, out _))
        {
#if DEBUG
            FikaGlobals.LogWarning("Player is not eligible for this transit point");
#endif
            // An early exit of the entry path, named for every player (the DEBUG line above is not built
            // in a release build): without access no countdown can start for this player.
            return;
        }

        // Recorded through the shared helper so the headless log uses the same wording as a
        // player-hosted raid, and the dedup table covers both.
        TransitZonePresence.RecordInZone(_playersInTransitZone, player.ProfileId, point.parameters.id, "player entered the zone");

        if (!transitPlayers.ContainsKey(player.ProfileId))
        {
            var fikaPlayer = player.TryCast<FikaPlayer>();
            if (fikaPlayer != null)
            {
                fikaPlayer.UpdateBtrTraderServiceData().HandleExceptions();
            }

            TransitEventPacket packet = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Interaction,
                TransitEvent = new TransitInteractionEvent()
                {
                    PlayerRaidId = player.RaidId,
                    PointId = point.parameters.id,
                    Type = TransitInteractionEvent.EType.Show
                }
            };

            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            return;
        }

        pointsById[point.parameters.id].GroupEnter(player);
    }

    private bool TryGetAccessToLocation(Player player, int pointId, out string keyId)
    {
        keyId = string.Empty;
        if (!TarkovApplication.Exist(out var application)) return false;
        var session = application.Session;
        var locations = session?.TryCast<ClientBackendSession>()?.LocationSettings?.locations;
        if (locations == null || !pointsById.TryGetValue(pointId, out var point) ||
            !locations.TryGetValue(point.parameters.target, out var destination) || destination == null)
            return false;

        var requirements = destination.GetAccessRequirements(session.GameMode,
            LocationSettings.Location.EAccessUsageType.Transit);
        if (requirements == null || requirements.Count == 0) return true;

        if (player.Side == EPlayerSide.Savage)
        {
            return false;
        }

        var profile = player.Profile;
        var items = profile.Inventory.GetPlayerItems(EPlayerItems.Equipment);
        if (!LocationAccessRequirementsUtility.TryFindSatisfiedRequirement(items, requirements, new Func<Item, bool>(profile.Examined),
            out var item, out _, out _) || item == null)
        {
            return false;
        }

        keyId = item.Id;
        return true;
    }

    /// <summary>
    /// Applies a zone report from a client. The headless controller also gates its transit on
    /// <see cref="CheckForPlayers"/>, and the same asymmetry applies: the native trigger only
    /// drives the local callbacks, so a client's player can be missing from the zone map even
    /// though he is standing in the point.
    /// </summary>
    internal void OnRemotePlayerZonePresence(string profileId, int pointId,
        TransitZonePresencePacket.ETransitZonePresenceKind kind)
    {
        if (string.IsNullOrEmpty(profileId))
        {
            return;
        }
        if (kind == TransitZonePresencePacket.ETransitZonePresenceKind.Left)
        {
            // Applies even without a point id: the client's death path only knows its profile.
            // The packet carries no reason and the client-side senders are not equivalent, so the
            // removal is confirmed against this machine before the entry is dropped - see
            // OnRemotePlayerLeftZone.
            OnRemotePlayerLeftZone(profileId, pointId);
            return;
        }

        if (!pointsById.TryGetValue(pointId, out _))
        {
            return;
        }

        var reason = kind == TransitZonePresencePacket.ETransitZonePresenceKind.In
            ? "client reported entering the zone" : "client interaction fallback";
        // RecordInZone answers true exactly when this profile was not known at this point, so the offer
        // below happens once per entry and a repeated report cannot re-send it.
        if (!TransitZonePresence.RecordInZone(_playersInTransitZone, profileId, pointId, reason)) return;
        SendInteractionShow(profileId, pointId, reason);
    }

    /// <summary>
    /// Mirror of the playable host's <c>FikaHostTransitController.SendInteractionShow</c>: the transit
    /// prompt is authoritative, and a remote player never reaches this machine's native <c>PlayerEnter</c>
    /// (a dedicated host has no local prompt to fall back on either), so the prompt has to be pushed to
    /// the client as <c>TransitInteractionEvent{Show}</c> at the moment his presence report is accepted.
    /// Without it the client never sees a prompt and therefore never sends an interact request. The packet
    /// is built exactly like the playable host's remote branch; only remote players are addressed here -
    /// the headless machine's own player has no client to receive it.
    /// </summary>
    private void SendInteractionShow(string profileId, int pointId, string reason)
    {
        var player = FindHumanPlayer(profileId);
        if (player == null)
        {
                        return;
        }
        if (player.IsYourPlayer) return;
        if (transitPlayers.ContainsKey(profileId)) return;

        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.Interaction,
            TransitEvent = new TransitInteractionEvent()
            {
                PlayerRaidId = player.RaidId,
                PointId = pointId,
                Type = TransitInteractionEvent.EType.Show
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// A client reported that its player left the transit zone. Mirror of the playable host's check
    /// (<c>FikaHostTransitController.OnRemotePlayerLeftZone</c>): the report is not taken at face value,
    /// because a real step out of the trigger, a death, an extract and the client's 0.25 s geometric
    /// fallback all arrive as the same "left", and only the last one can be wrong while the player is
    /// still standing in the point. The player has to be gone for a reason this machine can confirm -
    /// dead, extracted, already transferred, or outside the point's own trigger volume. A confirmed
    /// removal keeps the existing reason string and log format.
    /// </summary>
    private void OnRemotePlayerLeftZone(string profileId, int reportedPointId)
    {
        if (!_playersInTransitZone.TryGetValue(profileId, out var registeredPoint))
        {
            return;
        }

        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        var player = FindHumanPlayer(profileId);
        var alive = player != null && player.HealthController?.IsAlive == true;
        var transited = player != null && _transittedPlayers.Contains(player.RaidId);
        var extracted = player != null && coopHandler != null && coopHandler.ExtractedPlayers.Contains(player.NetId);
        var inside = alive && !transited && !extracted && IsInsidePointBounds(registeredPoint, player);

        var verdict = player == null ? "player is no longer in this raid"
            : !alive ? "player is not alive"
            : transited ? "player already transferred"
            : extracted ? "player already extracted"
            : inside ? "still inside the point (spurious report)"
            : "outside the point";

        var detail = player == null ? "player=none" : DescribePlayerPlacement(registeredPoint, player);

        if (inside)
        {
            // Only the first one is reported; the client's fallback can repeat the same verdict.
            if (_zoneExitLogged.Add(profileId + "@" + registeredPoint))
            {
            }
            return;
        }

        TransitZonePresence.RemoveFromZone(_playersInTransitZone, profileId, "client left the zone");
    }

    /// <summary>Same lookup <see cref="PruneStaleZoneRegistrations"/> performs inline.</summary>
    private FikaPlayer FindHumanPlayer(string profileId)
    {
        var players = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler?.HumanPlayers;
        if (players == null) return null;
        foreach (var candidate in players)
        {
            if (candidate != null && string.Equals(candidate.ProfileId, profileId, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private TransitPoint FindPoint(int pointId) =>
        pointsById != null && pointsById.TryGetValue(pointId, out var point) ? point : null;

    /// <summary>
    /// Whether the player still stands at the point, judged by every collider the point owns. Testing
    /// only the first collider found would reproduce the very mis-evaluation this check exists to catch
    /// (the trigger volume can sit on a child of the point), and a hit on any collider counts - a
    /// superset of the trigger volume, the safe direction for a veto, because it can only keep a
    /// registration this host already counted.
    /// </summary>
    private bool IsInsidePointBounds(int pointId, Player player)
    {
        if (player == null) return false;
        var point = FindPoint(pointId);
        if (point == null) return false;

        var position = player.Position;
        var colliders = point.GetComponentsInChildren<UnityEngine.Collider>(true);
        if (colliders != null)
        {
            for (var i = 0; i < colliders.Count; i++)
            {
                var collider = colliders[i];
                if (collider != null && collider.bounds.Contains(position)) return true;
            }
        }

        return false;
    }

    /// <summary>Player position plus the point anchor, so a geometric verdict can be re-derived from the log.</summary>
    private string DescribePlayerPlacement(int pointId, Player player)
    {
        var position = player.Position;
        var builder = new System.Text.StringBuilder();
        builder.Append("pos=(").Append(position.x.ToString("F1")).Append(',')
            .Append(position.y.ToString("F1")).Append(',').Append(position.z.ToString("F1")).Append(')');

        var point = FindPoint(pointId);
        if (point == null)
        {
            builder.Append(" point=unavailable");
            return builder.ToString();
        }

        var anchor = point.transform.position;
        builder.Append(" point=(").Append(anchor.x.ToString("F1")).Append(',')
            .Append(anchor.y.ToString("F1")).Append(',').Append(anchor.z.ToString("F1")).Append(')')
            .Append(" distance=").Append(UnityEngine.Vector3.Distance(position, anchor).ToString("F1"))
            .Append(" colliders=").Append(point.GetComponentsInChildren<UnityEngine.Collider>(true)?.Count ?? 0);
        return builder.ToString();
    }

    private void HeadlessOnPlayerExit(TransitPoint point, Player player)
    {
#if DEBUG
        FikaGlobals.LogInfo($"{player.Profile.Info.Nickname} left transit point {point.Description}");
#endif

        // Whether this leave ends the transfer of the whole group is decided below, but the answer has to be
        // read before the registration is removed here.
        var registeredForThisPoint = transitPlayers != null &&
            transitPlayers.TryGetValue(player.ProfileId, out var registeredPoint) &&
            registeredPoint == point.parameters.id;

        if (_playersInTransitZone.TryGetValue(player.ProfileId, out var value))
        {
            if (value == point.parameters.id)
            {
                _playersInTransitZone.Remove(player.ProfileId);
            }
        }

        if (registeredForThisPoint && _countdownPointId == point.parameters.id &&
            TransitCountdownGuard.AbortWhenSomeoneLeaves)
        {
            // A registered player left the point while its countdown runs (user requirement 1): the whole
            // transfer is cancelled, and the cancellation applies the native half of this leave for him.
            CancelCountdown("a registered teammate left the point",
                " player=" + player.ProfileId + " " + DescribePlayerPlacement(point.parameters.id, player));
            return;
        }

        if (transitPlayers.ContainsKey(player.ProfileId))
        {
            point.GroupExit(player);
            // Dropping the native group stops the countdown that carries this player out of the raid; a
            // co-located exit makes the trigger flip and shows up here.
        }

        // Same rule as the playable host's OnHostPlayerExit: a player who leaves the point without a
        // started transfer has the accepted interaction undone, ban included, or he can never extract
        // again in this raid. A leave that happens after the transfer started keeps the ban, which is
        // what stops a pending transit from being turned into a normal exit. A dedicated host has no
        // local player, so this runs for whoever the native exit hook reports.
        if (_transittedPlayers.Contains(player.RaidId))
        {
        }
        else
        {
            var wasRegistered = transitPlayers.Remove(player.ProfileId);
            ReleaseTransitBanFor(player.RaidId, player.ProfileId,
                $"player left the point (transitRegistrationRemoved={wasRegistered})");
        }

        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.Interaction,
            TransitEvent = new TransitInteractionEvent()
            {
                PlayerRaidId = player.RaidId,
                PointId = point.parameters.id,
                Type = TransitInteractionEvent.EType.Hide
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    public override void InactivePointNotification(int playerId, int pointId)
    {
        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.Interaction,
            TransitEvent = new TransitInteractionEvent()
            {
                PlayerRaidId = playerId,
                PointId = pointId,
                Type = TransitInteractionEvent.EType.InactivePoint
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    public override void Sizes(int pointId, Il2CppSystem.Collections.Generic.Dictionary<int, byte> sizes)
    {
        _currentTransitPointId = pointId;
        // Mirror of the playable host's Sizes: the native dictionary is the point's own bookkeeping
        // (group size over the point's player groups), and a remote player only enters it through
        // InteractWithTransit - so it announced the players whose interact request had already been
        // accepted, while CheckForPlayers - which reads _playersInTransitZone - counted everyone
        // standing in the point. The numbers are rebuilt from that same map with the predicate this
        // controller's CheckForPlayers applies. No native bookkeeping is written here.
        var shown = BuildZoneSizes(pointId, sizes);

        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.GroupSize,
            TransitEvent = new TransitGroupSizeEvent()
            {
                PointId = pointId,
                Sizes = shown
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// The players this controller counts as standing in a point: the same predicate
    /// <see cref="CheckForPlayers"/> applies (alive, not the headless machine's own player, not already
    /// transferred, not extracted), read from the same map - so the announced group size cannot
    /// disagree with the gate that decides whether a transit may start.
    /// </summary>
    private List<FikaPlayer> CollectZonePlayers(int pointId)
    {
        var result = new List<FikaPlayer>();
        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        var players = coopHandler?.HumanPlayers;
        if (players == null) return result;

        foreach (var member in players)
        {
            if (member == null || member.HealthController?.IsAlive != true) continue;
            if (member.IsYourPlayer) continue;
            if (TransitDropouts.IsDropped(member)) continue;
            if (_transittedPlayers.Contains(member.RaidId)) continue;
            if (coopHandler.ExtractedPlayers.Contains(member.NetId)) continue;
            if (!_playersInTransitZone.TryGetValue(member.ProfileId, out var zone) || zone != pointId) continue;
            result.Add(member);
        }

        return result;
    }

    /// <summary>
    /// Replaces the native group-size numbers with the observed in-zone count, once per player that is
    /// in the point, so every machine finds its own raid id in the dictionary and all of them show the
    /// same number. The native dictionary is handed through unchanged when nothing is observed, and the
    /// slots it defined are kept, so a display this map cannot account for never disappears.
    /// </summary>
    private Il2CppSystem.Collections.Generic.Dictionary<int, byte> BuildZoneSizes(int pointId,
        Il2CppSystem.Collections.Generic.Dictionary<int, byte> nativeSizes)
    {
        var inZone = CollectZonePlayers(pointId);
        if (inZone.Count == 0) return nativeSizes;

        var value = (byte)System.Math.Clamp(inZone.Count, 0, byte.MaxValue);
        var result = new Il2CppSystem.Collections.Generic.Dictionary<int, byte>();
        foreach (var member in inZone)
        {
            if (!result.ContainsKey(member.RaidId)) result.Add(member.RaidId, value);
        }

        if (nativeSizes != null)
        {
            foreach (var entry in nativeSizes)
            {
                if (!result.ContainsKey(entry.Key)) result.Add(entry.Key, value);
            }
        }

        return result;
    }

    /// <summary>
    /// The teammates standing in a point, as the shared start-gate helper expects them. The collector of
    /// this controller returns the concrete Fika type, the helper works on EFT.Player for both hosts.
    /// </summary>
    private List<Player> ZonePlayersAsPlayers(int pointId)
    {
        var result = new List<Player>();
        foreach (var member in CollectZonePlayers(pointId))
        {
            if (member != null) result.Add(member);
        }

        return result;
    }

    /// <summary>Compact view of a raid-id to count dictionary for the diagnostics.</summary>
    private static string DescribeSizes(Il2CppSystem.Collections.Generic.Dictionary<int, byte> sizes)
    {
        if (sizes == null) return "null";
        var builder = new System.Text.StringBuilder();
        foreach (var entry in sizes)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(entry.Key).Append('=').Append(entry.Value);
        }
        return builder.Length == 0 ? "empty" : builder.ToString();
    }

    /// <summary>
    /// The native point bookkeeping the native number used to be built from: which profile belongs to
    /// which group, and each group's size. Evidence for whether a remote player ever reached the
    /// point's own groups on this machine.
    /// </summary>
    private string DescribePointGroups(int pointId)
    {
        var point = FindPoint(pointId);
        if (point == null) return "point-unknown";

        var builder = new System.Text.StringBuilder();
        foreach (var entry in point._playerGroups)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(entry.Key).Append("->").Append(entry.Value);
        }

        builder.Append(" size[");
        if (point._groupSize != null)
        {
            foreach (var entry in point._groupSize)
            {
                builder.Append(entry.Key).Append('=').Append(entry.Value).Append(';');
            }
        }

        // The native countdown of the point (group -> seconds left) and the confirmed set: the two pieces
        // the native transfer of a group is built from.
        builder.Append("] timers[");
        if (point._groupTimers != null)
        {
            foreach (var entry in point._groupTimers)
            {
                builder.Append(entry.Key).Append('=').Append(entry.Value).Append(';');
            }
        }

        builder.Append("] confirmed[");
        if (point._confirmedPlayers != null)
        {
            foreach (var profile in point._confirmedPlayers)
            {
                builder.Append(profile).Append(';');
            }
        }

        builder.Append("] enabled=").Append(point.Enabled).Append(" active=").Append(point.IsActive);
        return builder.ToString();
    }

    /// <summary>
    /// The native timer notification, forwarded to the clients with one addition: every player the native
    /// dictionary does not carry gets an entry keyed by his raid id, because both timer panels look the
    /// local player up by raid id and the native bookkeeping describes the local group only
    /// (see <c>TransitStartGate.BuildTimers</c> and the playable host's twin method).
    /// </summary>
    public override void Timers(int pointId, Il2CppSystem.Collections.Generic.Dictionary<int, ushort> timers)
    {
        try
        {
            if (_countdownPointId == pointId)
            {
                HeadlessCountdownTick();
                return;
            }

            var shown = TransitStartGate.BuildTimers(ZonePlayersAsPlayers(pointId), 0, timers);

            TransitEventPacket packet = new()
            {
                EventType = TransitEventPacket.ETransitEventType.GroupTimer,
                TransitEvent = new TransitGroupTimerEvent()
                {
                    PointId = pointId,
                    Timers = shown
                }
            };

            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }
        catch (System.Exception exception)
        {
                    }

        HeadlessCountdownTick();
    }

    /// <summary>
    /// The teammates this machine still counts for the gates and the panels: alive, not already
    /// transferred, not extracted, and not its own player (a dedicated host has no transit entry of its
    /// own). The same predicate <see cref="CheckForPlayers"/> and <see cref="CollectZonePlayers"/> apply.
    /// </summary>
    private List<Player> EligibleMembers()
    {
        var result = new List<Player>();
        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        var players = coopHandler?.HumanPlayers;
        if (players == null) return result;

        foreach (var member in players)
        {
            if (member == null || member.HealthController?.IsAlive != true) continue;
            if (member.IsYourPlayer) continue;
            if (TransitDropouts.IsDropped(member)) continue;
            if (_transittedPlayers.Contains(member.RaidId)) continue;
            if (coopHandler.ExtractedPlayers.Contains(member.NetId)) continue;
            result.Add(member);
        }

        return result;
    }

    /// <summary>Records an accepted interaction; the strict gate below needs it as the player's proof.</summary>
    private void RecordInteraction(Player player, int pointId)
    {
        _interactedPlayers[player.ProfileId] = pointId;
        if (!_interactionLogged.Add(player.ProfileId + "@" + pointId)) return;
            }

    /// <summary>Point's own "wait for the whole party" flag, read for the diagnostics only.</summary>
    private string ReadWaitForAllParty(int pointId)
    {
        var point = FindPoint(pointId);
        return point == null ? "point-unknown" : point.parameters.waitForAllParty.ToString();
    }

    /// <summary>Strict start gate: every eligible teammate interacted at this point and stands inside it.</summary>
    private bool EveryoneInteracted(Player player, int pointId)
    {
        if (!TransitStartGate.RequireAllTeammatesInteracted) return true;

        var members = EligibleMembers();
        var missing = TransitStartGate.DescribeNotReady(members, _playersInTransitZone, _interactedPlayers, pointId, out var ready);
        if (missing == null)
        {
                        return true;
        }

        if (_gateLogged.Add(pointId + ":" + player.ProfileId + ":interaction"))
        {
                    }

        NotifyTeammatesNotReady(player);
        return false;
    }

    /// <summary>Tells the waiting player that the countdown has not started (one message, no local player here).</summary>
    private void NotifyTeammatesNotReady(Player player)
    {
        try
        {
            Il2CppSystem.Collections.Generic.Dictionary<int, TransitMessagesEvent.EType> messages = new();
            messages.Add(player.RaidId, TransitMessagesEvent.EType.NonAllTeammates);
            TransitEventPacket messagePacket = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Messages,
                TransitEvent = new TransitMessagesEvent()
                {
                    Messages = messages
                }
            };

            _server.SendData(ref messagePacket, DeliveryMethod.ReliableOrdered);
        }
        catch (System.Exception exception)
        {
                    }
    }

    /// <summary>Compact view of the interaction ledger, one entry per profile, the current point marked.</summary>
    private string DescribeInteractions(int pointId)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var entry in _interactedPlayers)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(entry.Key).Append('=').Append(entry.Value);
            if (entry.Value == pointId) builder.Append('*');
        }
        return builder.Length == 0 ? "empty" : builder.ToString();
    }

    /// <summary>Teammates a point's countdown speaks for, as profile/raid-id pairs.</summary>
    private string DescribeParticipants(int pointId)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var member in CollectZonePlayers(pointId))
        {
            if (member == null) continue;
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(member.ProfileId).Append('/').Append(member.RaidId);
        }
        return builder.Length == 0 ? "none" : builder.ToString();
    }

    /// <summary>The native transit registry of this host.</summary>
    private string DescribeNativeRegistry()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var entry in transitPlayers)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(entry.Key).Append('=').Append(entry.Value);
        }
        return builder.Length == 0 ? "empty" : builder.ToString();
    }

    /// <summary>
    /// Registers every teammate standing in the point and answers each with the Confirm its controller
    /// waits for. Runs only after both gates passed, so the countdown starts for the whole group at once.
    /// </summary>
    private bool RegisterTransitParticipants(TransitPoint point, Player interacting, string keyId, EDateTime time)
    {
        var pointId = point.parameters.id;
        var members = EligibleMembers();
        var eligible = 0;
        var registered = 0;
        foreach (var member in members)
        {
            if (member == null) continue;
            if (!_playersInTransitZone.TryGetValue(member.ProfileId, out var zone) || zone != pointId) continue;
            eligible++;
            var preferredKey = member.ProfileId == interacting.ProfileId ? keyId : null;
            if (RegisterTransitParticipant(member, point, preferredKey, time)) registered++;
        }

        if (registered == 0)
        {
                        return false;
        }

                // The point is activated by this registration: the exits at its own position are taken out of the
        // interaction until the transfer is through, so the two cannot compete for the same press.
        TransitCoLocatedExits.Suppress(point, $"the point registered {registered} player(s)");
        return true;
    }

    /// <summary>Registers one player, guarded: one failing teammate never breaks the others or this call.</summary>
    private bool RegisterTransitParticipant(Player member, TransitPoint point, string preferredKeyId, EDateTime time)
    {
        var pointId = point.parameters.id;
        var profileId = member.ProfileId;
        if (transitPlayers.ContainsKey(profileId))
        {
                        return false;
        }

        var keyId = preferredKeyId;
        try
        {
            if (string.IsNullOrEmpty(keyId)) TryGetAccessToLocation(member, pointId, out keyId);
        }
        catch (System.Exception exception)
        {
                        keyId = null;
        }

        try
        {
            profileTime[profileId] = time;
            transitPlayers[profileId] = member.RaidId;
            profileKeys[profileId] = keyId;
            point.GroupEnter(member);
            ExfiltrationController.Instance.BannedPlayers.Add(member.RaidId);
            ExfiltrationController.Instance.CancelExtractionForPlayer(member);
            TransitEventPacket confirm = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Interaction,
                TransitEvent = new TransitInteractionEvent()
                {
                    PlayerRaidId = member.RaidId,
                    PointId = pointId,
                    Type = TransitInteractionEvent.EType.Confirm
                }
            };

            _server.SendData(ref confirm, DeliveryMethod.ReliableOrdered);
        }
        catch (System.Exception exception)
        {
                        ReleaseTransitBanFor(member.RaidId, profileId, "the registration threw; this player does not take part");
            return false;
        }

                return true;
    }

    /// <summary>
    /// Starts the countdown of a point whose gate passed: length from the point, announcements from this
    /// controller's own clock (a dedicated host has no player-driven native tick to rely on).
    /// </summary>
    private void BeginCountdown(TransitPoint point)
    {
        var seconds = TransitStartGate.CountdownSeconds(point);
        _countdownPointId = point.parameters.id;
        _countdownSeconds = seconds;
        _countdownEndsAt = Time.unscaledTime + seconds;
        _lastBroadcastSecond = -1;
        _recheckLoggedSignature = null;
        _recheckCount = 0;
        _countdownGeneration++;
                BroadcastCountdown(true);
        StartCountdownLoop();
    }

    /// <summary>One step of the countdown clock; also called by every native <see cref="Timers"/> call.</summary>
    private void HeadlessCountdownTick()
    {
        try
        {
            if (_countdownPointId < 0) return;

            // The continuous half of the rule: the participant list is re-checked on every tick of the
            // dedicated host's clock, not only when the countdown started.
            if (RecheckCountdownParticipants()) return;

            var remaining = System.Math.Max(0, (int)System.Math.Ceiling(_countdownEndsAt - Time.unscaledTime));
            if (remaining != _lastBroadcastSecond)
            {
                _lastBroadcastSecond = remaining;
                BroadcastCountdown(false);
            }

            if (remaining <= 0) FinishCountdown();
        }
        catch (System.Exception exception)
        {
                        AbortCountdown("the countdown tick threw");
        }
    }

    /// <summary>
    /// The clock of this controller: one step every 250 ms on the game update thread, stopped with the
    /// countdown. A dedicated host has no GameWorld.Update poll of its own driver, so the loop is owned
    /// here; the generation makes a step of an old raid a no-op.
    /// </summary>
    private void StartCountdownLoop()
    {
        if (_countdownLoopRunning) return;
        _countdownLoopRunning = true;
        var generation = _countdownGeneration;
        MainThreadDispatcher.RunAsync(async () =>
        {
            try
            {
                while (_countdownGeneration == generation && _countdownPointId >= 0)
                {
                    await Task.Delay(250);
                    MainThreadDispatcher.Post(() =>
                    {
                        if (_countdownGeneration != generation) return;
                        HeadlessCountdownTick();
                    });
                }
            }
            catch (System.Exception exception)
            {
                            }
            finally
            {
                _countdownLoopRunning = false;
            }
        }).Forget();
    }

    /// <summary>Announces the running countdown to every client, one group-timer packet per step.</summary>
    private void BroadcastCountdown(bool force)
    {
        var pointId = _countdownPointId;
        if (pointId < 0) return;

        var remaining = System.Math.Max(0, (int)System.Math.Ceiling(_countdownEndsAt - Time.unscaledTime));
        if (force) _lastBroadcastSecond = remaining;
        try
        {
            var timers = TransitStartGate.BuildTimers(ZonePlayersAsPlayers(pointId), remaining, null);
            TransitEventPacket packet = new()
            {
                EventType = TransitEventPacket.ETransitEventType.GroupTimer,
                TransitEvent = new TransitGroupTimerEvent()
                {
                    PointId = pointId,
                    Timers = timers
                }
            };

            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
                    }
        catch (System.Exception exception)
        {
                    }
    }

    /// <summary>The countdown reached zero: every registered client gets a transfer, each one at most once.</summary>
    private void FinishCountdown()
    {
        var pointId = _countdownPointId;
        _countdownPointId = -1;
        _lastBroadcastSecond = -1;
        _countdownGeneration++;
        var issued = IssueTransferForClients(pointId, "countdown end");
        var failures = _transferFailures.Count == 0 ? "none" : string.Join(";", _transferFailures);
            }

    /// <summary>
    /// Stops a running countdown that failed (the tick threw) and gives the players it registered their exit
    /// interaction back. It uses the same release as a cancellation, so an abort cannot leave a registration,
    /// a native group membership or a transit ban behind either.
    /// </summary>
    private void AbortCountdown(string reason)
    {
        var pointId = _countdownPointId;
        _countdownPointId = -1;
        _lastBroadcastSecond = -1;
        _recheckLoggedSignature = null;
        _countdownGeneration++;
                var released = ReleaseParticipantsOfCancelledCountdown(pointId, "countdown aborted: " + reason);
        var suppressed = TransitCoLocatedExits.Release("the countdown of point " + pointId + " was aborted: " + reason);
            }

    /// <summary>
    /// The profiles this machine has registered for a point - the participant list of the running countdown,
    /// read from the native registry (the same registry <see cref="IssueTransferForClients"/> requires, so
    /// both sides speak about one set). Mirror of the playable host's method of the same name.
    /// </summary>
    private List<(string ProfileId, int RaidId)> RegisteredParticipants(int pointId)
    {
        var result = new List<(string ProfileId, int RaidId)>();
        try
        {
            foreach (var entry in transitPlayers)
            {
                if (entry.Value == pointId) result.Add((entry.Key, entry.Value));
            }
        }
        catch (System.Exception exception)
        {
                    }

        return result;
    }

    /// <summary>True when this player has already extracted from this raid.</summary>
    private bool IsExtractedFromThisRaid(Player member)
    {
        try
        {
            var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
            return member != null && coopHandler != null && member.TryCast<FikaPlayer>() is { } fikaPlayer &&
                coopHandler.ExtractedPlayers.Contains(fikaPlayer.NetId);
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The continuous half of the start rule (user requirement 1): while the countdown of a point runs, its
    /// participant list is re-checked on every tick of the clock instead of once when it started. The
    /// verdicts come from <see cref="TransitCountdownGuard.Classify"/> and every verdict change writes one
    /// criterion line. Returns true when the countdown was cancelled, i.e. when the caller must not continue
    /// this tick; it never throws, because a broken check must not break the transfer it observes.
    /// </summary>
    private bool RecheckCountdownParticipants()
    {
        var pointId = _countdownPointId;
        if (pointId < 0) return false;

        var members = 0;
        var inside = new List<string>();
        var missing = new List<string>();
        var left = new List<string>();
        var dead = new List<string>();
        var dropped = new List<string>();
        var gone = new List<string>();
        var toDrop = new List<string>();
        string cancelReason = null;
        string cancelDetail = null;
        var verdict = "continue";

        try
        {
            var participants = RegisteredParticipants(pointId);
            members = participants.Count;
            foreach (var entry in participants)
            {
                var profileId = entry.ProfileId;
                var player = FindHumanPlayer(profileId);
                var inRaid = player != null && !TransitDropouts.IsDropped(player);
                var alive = player != null && player.HealthController?.IsAlive == true;
                var goneFromRaid = player != null &&
                    (_transittedPlayers.Contains(player.RaidId) || IsExtractedFromThisRaid(player));
                var insidePoint = player != null && alive && !goneFromRaid && IsInsidePointBounds(pointId, player);
                var zoneEntryMatches = _playersInTransitZone.TryGetValue(profileId, out var zone) && zone == pointId;
                var state = TransitCountdownGuard.Classify(inRaid, alive, goneFromRaid, insidePoint, zoneEntryMatches);

                switch (state)
                {
                    case ETransitMemberState.Inside:
                        inside.Add(profileId);
                        break;
                    case ETransitMemberState.Missing:
                        // Still inside the point, only the zone report of this host is missing: the entry is
                        // restored from the point's own colliders instead of dropping a player who is there.
                        inside.Add(profileId);
                        missing.Add(profileId);
                        TransitZonePresence.RecordInZone(_playersInTransitZone, profileId, pointId,
                            "gate recheck: inside the point although this host had no zone report for him");
                        break;
                    case ETransitMemberState.Left:
                        left.Add(profileId + "(" + DescribePlayerPlacement(pointId, player) + ")");
                        if (TransitCountdownGuard.AbortWhenSomeoneLeaves)
                        {
                            cancelReason ??= "a registered teammate left the point";
                            cancelDetail = " player=" + profileId + " " + DescribePlayerPlacement(pointId, player);
                        }

                        break;
                    case ETransitMemberState.Dead:
                        dead.Add(profileId);
                        if (TransitCountdownGuard.AbortWhenSomeoneDies)
                        {
                            cancelReason ??= "a registered teammate died";
                            cancelDetail = " player=" + profileId + " " + DescribePlayerPlacement(pointId, player);
                        }
                        else
                        {
                            toDrop.Add(profileId);
                        }

                        break;
                    default:
                        if (inRaid) gone.Add(profileId);
                        else dropped.Add(profileId);
                        toDrop.Add(profileId);
                        break;
                }
            }

            verdict = cancelReason != null ? "cancel(" + cancelReason + ")" : "continue";
        }
        catch (System.Exception exception)
        {
            _recheckFailures++;
            if (_recheckFailures <= 3)
            {
                            }

            return false;
        }

        _recheckCount++;
        var line = TransitCountdownGuard.DescribeRecheck(pointId, members, inside, missing, left, dead, dropped, gone, verdict);
        if (!string.Equals(line, _recheckLoggedSignature, System.StringComparison.Ordinal))
        {
            _recheckLoggedSignature = line;
            FikaGlobals.LogWarning(line);
        }

        if (cancelReason != null)
        {
            CancelCountdown(cancelReason, (cancelDetail ?? string.Empty) +
                $" (checked {_recheckCount}x, switches: {TransitCountdownGuard.DescribeSwitches()})");
            return true;
        }

        foreach (var profileId in toDrop)
        {
            DropCountdownParticipant(pointId, profileId, true, "no longer a participant in this raid");
        }

        if (members > 0 && toDrop.Count >= members)
        {
            // Every registered teammate is gone: there is nobody left for this transfer to carry.
            CancelCountdown("no registered teammate is left in this raid",
                $" (checked {_recheckCount}x, switches: {TransitCountdownGuard.DescribeSwitches()})");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Removes one participant from a running countdown without cancelling it: the registration, the native
    /// group membership of the point and - for a player who is not already on his way out - his transit ban.
    /// Used for a teammate who died (with the death switch off) and for every teammate who left this raid,
    /// which is the bug fix of user requirement 3.
    /// </summary>
    private bool DropCountdownParticipant(int pointId, string profileId, bool releaseBan, string reason)
    {
        var point = FindPoint(pointId);
        var player = FindHumanPlayer(profileId);
        var raidId = player == null ? -1 : player.RaidId;
        var removed = false;
        try
        {
            if (transitPlayers.TryGetValue(profileId, out var registeredRaidId)) raidId = registeredRaidId;
            removed = transitPlayers.Remove(profileId);
        }
        catch (System.Exception exception)
        {
                    }

        _interactedPlayers.Remove(profileId);
        var groupExited = false;
        if (player != null && point != null)
        {
            try
            {
                point.GroupExit(player);
                point._confirmedPlayers.Remove(profileId);
                groupExited = true;
            }
            catch (System.Exception exception)
            {
                            }
        }

        if (releaseBan && raidId >= 0 && !_transittedPlayers.Contains(raidId))
        {
            ReleaseTransitBanFor(raidId, profileId, reason);
        }

                return removed || groupExited;
    }

    /// <summary>
    /// Ends the countdown of a point and the transfer it was preparing (user requirement 1), the mirror of
    /// the playable host's method of the same name: every registered player leaves the point's native group
    /// and loses its confirmed flag, the native registry and the interaction ledger drop him, his transit ban
    /// is lifted and the exits at the point's position react again. Every client is told about it - which makes
    /// its own controller drop the local registration, re-enable its exits and close its countdown panel.
    /// A dedicated host has no panel and no local player of its own.
    /// </summary>
    private void CancelCountdown(string reason, string detail)
    {
        var pointId = _countdownPointId;
        if (pointId < 0) return;

        _countdownPointId = -1;
        _lastBroadcastSecond = -1;
        _recheckLoggedSignature = null;
        _countdownGeneration++;

                var released = ReleaseParticipantsOfCancelledCountdown(pointId, "countdown cancelled: " + reason);
        var suppressed = TransitCoLocatedExits.Release("the countdown of point " + pointId + " was cancelled: " + reason);
        var banned = ExfiltrationController.Instance == null ? -1 : ExfiltrationController.Instance.BannedPlayers.Count;
            }

    /// <summary>
    /// Releases every player a cancelled countdown registered: the native group membership (through the same
    /// <c>point.GroupExit</c> the native exit path uses), the confirmed flag, the native registry entry, his
    /// transit ban, and the message that tells the client to do the same locally. Returns the detail fragment
    /// of the confirmation line.
    /// </summary>
    private string ReleaseParticipantsOfCancelledCountdown(int pointId, string reason)
    {
        var point = FindPoint(pointId);
        var released = 0;
        var groupsExited = 0;
        var clientsTold = 0;
        var failed = new System.Text.StringBuilder();

        foreach (var entry in RegisteredParticipants(pointId))
        {
            var profileId = entry.ProfileId;
            var raidId = entry.RaidId;
            var player = FindHumanPlayer(profileId);
            if (player != null && point != null)
            {
                try
                {
                    point.GroupExit(player);
                    point._confirmedPlayers.Remove(profileId);
                    groupsExited++;
                }
                catch (System.Exception exception)
                {
                    if (failed.Length > 0) failed.Append(';');
                    failed.Append(profileId).Append(":GroupExit=").Append(exception.GetType().Name);
                }
            }

            try
            {
                transitPlayers.Remove(profileId);
            }
            catch (System.Exception)
            {
            }

            _interactedPlayers.Remove(profileId);
            var raidIdForPacket = player == null ? raidId : player.RaidId;
            SendTransitInteraction(raidIdForPacket, pointId, TransitInteractionEvent.EType.Hide);
            NotifyTransitCancelled(raidIdForPacket, profileId);
            clientsTold++;

            if (!_transittedPlayers.Contains(raidId))
            {
                ReleaseTransitBanFor(raidId, profileId, reason);
            }

            released++;
        }

        return $"participants={released} nativeGroupsExited={groupsExited} remoteClientsTold={clientsTold} failed=[{failed}]";
    }

    /// <summary>One native interaction event of this host to all clients (the Hide of a cancelled countdown).</summary>
    private void SendTransitInteraction(int playerRaidId, int pointId, TransitInteractionEvent.EType type)
    {
        try
        {
            TransitEventPacket packet = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Interaction,
                TransitEvent = new TransitInteractionEvent()
                {
                    PlayerRaidId = playerRaidId,
                    PointId = pointId,
                    Type = type
                }
            };

            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }
        catch (System.Exception exception)
        {
                    }
    }

    /// <summary>
    /// Tells one client that the transfer of his point was cancelled, with the message the native zone check
    /// uses for the same fact ("not all teammates are at the point").
    /// </summary>
    private void NotifyTransitCancelled(int playerRaidId, string profileId)
    {
        try
        {
            Il2CppSystem.Collections.Generic.Dictionary<int, TransitMessagesEvent.EType> messages = new();
            messages.Add(playerRaidId, TransitMessagesEvent.EType.NonAllTeammates);
            TransitEventPacket messagePacket = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Messages,
                TransitEvent = new TransitMessagesEvent()
                {
                    Messages = messages
                }
            };

            _server.SendData(ref messagePacket, DeliveryMethod.ReliableOrdered);
        }
        catch (System.Exception exception)
        {
                    }
    }

    /// <summary>
    /// Sends the transfer to every remote teammate registered for this point. The native tick may already
    /// have driven some of them (its own calls land in <see cref="Transit"/>), and the issued set keeps
    /// this method from sending the same player twice.
    /// </summary>
    private int IssueTransferForClients(int pointId, string source)
    {
        var point = FindPoint(pointId);
        if (point == null)
        {
                        return 0;
        }

        _transferFailures.Clear();
        var issued = 0;
        var eligible = 0;
        foreach (var member in EligibleMembers())
        {
            if (member == null) continue;
            eligible++;
            if (!transitPlayers.TryGetValue(member.ProfileId, out var registeredPoint) || registeredPoint != pointId)
            {
                _transferFailures.Add($"{member.ProfileId}=not registered for point {pointId}");
                                continue;
            }

            if (IssueClientTransfer(member, point, source)) issued++;
            else _transferFailures.Add($"{member.ProfileId}=not sent, see the line above");
        }

                return issued;
    }

    /// <summary>Sends one client its transit, guarded and idempotent (see the playable host's twin method).</summary>
    private bool IssueClientTransfer(Player member, TransitPoint point, string source)
    {
        if (!_transferIssued.Add(member.RaidId))
        {
                        return false;
        }

        try
        {
            if (!EnsureSummonedTransit(member.ProfileId))
            {
                                _transferIssued.Remove(member.RaidId);
                return false;
            }

            var destination = BuildDestination(member, point, _lastTransitHash, _lastTransitKeys,
                _lastTransitPlayersCount > 0 ? _lastTransitPlayersCount : CountRegisteredParticipants(point.parameters.id));
            alreadyTransits[member.ProfileId] = destination;
            _transittedPlayers.Add(member.RaidId);
            TransitEventPacket packet = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Extract,
                PlayerId = member.RaidId,
                TransitId = point.parameters.id,
                Destination = destination
            };

            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
                        return true;
        }
        catch (System.Exception exception)
        {
            _transferIssued.Remove(member.RaidId);
                        return false;
        }
    }

    /// <summary>Players this host counts as registered for a point, as the transfer's player count.</summary>
    private int CountRegisteredParticipants(int pointId)
    {
        var count = 0;
        try
        {
            foreach (var entry in transitPlayers)
            {
                if (entry.Value == pointId) count++;
            }
        }
        catch (System.Exception)
        {
            return 1;
        }

        return count > 0 ? count : 1;
    }

    /// <summary>Creates the transit history of a profile this host has none for yet.</summary>
    private bool EnsureSummonedTransit(string profileId)
    {
        try
        {
            if (summonedTransits.ContainsKey(profileId)) return true;
            summonedTransits[profileId] = new Transit(_localRaidSettings.transition.transitionRaidId,
                _localRaidSettings.transition.transitionCount, FikaTransitHistory.AppendLocation(_localRaidSettings), IsEvent);
                        return true;
        }
        catch (System.Exception exception)
        {
                        return false;
        }
    }

    /// <summary>The destination of one player's transfer (mirror of the playable host's builder).</summary>
    private LocationTransit BuildDestination(Player player, TransitPoint point, string hash,
        Il2CppSystem.Collections.Generic.Dictionary<string, ProfileKey> keys, int playersCount)
    {
        var profiles = new Il2CppSystem.Collections.Generic.Dictionary<string, ProfileKey>();
        if (keys != null)
        {
            foreach (var entry in keys)
            {
                if (!profiles.ContainsKey(entry.Key)) profiles.Add(entry.Key, entry.Value);
            }
        }

        var location = TransitDestination.Resolve(point, out var destinationSource);
        if (destinationSource != "parameters.location")
        {
                    }

        return new LocationTransit
        {
            hash = hash ?? string.Empty,
            playersCount = playersCount,
            ip = string.Empty,
            location = location,
            profiles = profiles,
            transitionRaidId = summonedTransits.TryGetValue(player.ProfileId, out var history)
                ? history.raidId : _localRaidSettings.transition.transitionRaidId,
            raidMode = ERaidMode.Local,
            side = player.Side == EPlayerSide.Savage ? ESideType.Savage : ESideType.Pmc,
            dayTime = profileTime.TryGetValue(player.ProfileId, out var time) ? time : _localRaidSettings.timeVariant
        };
    }

    public override void InteractWithTransit(Player player, InteractWithTransitPacket packet)
    {
        // Match the host's native eligibility checks before mutating transit
        // state. A remote request can arrive after a point has been disabled,
        // or carry a stale/mismatched key and time selection.
        // Self-healing: an interact request that reaches this controller proves the sender is
        // standing at the point, so it registers him as well. Everything after this point
        // (access, entry requirements, completed quest, group size gate) is unchanged, so the
        // fallback cannot move a player who is somewhere else.
        if (player != null && packet != null && packet.hasInteraction &&
            player.HealthController?.IsAlive == true && pointsById.TryGetValue(packet.pointId, out _))
        {
            if (!_playersInTransitZone.TryGetValue(player.ProfileId, out var currentPoint) || currentPoint != packet.pointId)
            {
                TransitZonePresence.RecordInZone(_playersInTransitZone, player.ProfileId, packet.pointId,
                    "interact request at the point");
            }
        }

        if (player == null || packet == null || !packet.hasInteraction ||
            player.HealthController?.IsAlive != true ||
            !pointsById.TryGetValue(packet.pointId, out var point) || !point.Enabled ||
            transitPlayers.ContainsKey(player.ProfileId) ||
            !TryGetAccessToLocation(player, packet.pointId, out var keyId) ||
            !EFT.ClientTransitController.HasMetEntryRequirements(point, player) ||
            (!string.IsNullOrEmpty(point.parameters.completedQuestId) &&
             !new TransitCompletedQuestRequirement(point.parameters.completedQuestId).Met(player)))
        {
                        return;
        }

        if (_countdownPointId == packet.pointId)
        {
            // The group is at the point and the countdown runs; a repeated press must not start a second one.
            FikaGlobals.LogWarning($"[TransitDiag] InteractWithTransit ignored for {player.ProfileId}: the countdown of point " +
                $"{packet.pointId} is already running ({_countdownSeconds}s in total)");
            return;
        }


        // The press is remembered first: the strict gate needs it as this player's proof, and nothing is
        // registered before the whole team has done its part and stands inside the point.
        RecordInteraction(player, packet.pointId);
        if (!CheckForPlayers(player, packet.pointId)) return;
        if (!EveryoneInteracted(player, packet.pointId)) return;
        if (!RegisterTransitParticipants(point, player, keyId, packet.time)) return;

        BeginCountdown(point);
    }

    private bool CheckForPlayers(Player player, int pointId)
    {
        // Dead, extracted and transferred players are excluded by both loops below, so their
        // zone entries only darken the map; drop them here.
        PruneStaleZoneRegistrations();

        var humanPlayers = 0;
        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        if (coopHandler == null)
        {
            FikaGlobals.LogError("Cannot validate headless transit without a CoopHandler");
            return false;
        }

        foreach (var fikaPlayer in coopHandler.HumanPlayers)
        {
            if (fikaPlayer.HealthController?.IsAlive == true &&
                !fikaPlayer.IsYourPlayer &&
                !TransitDropouts.IsDropped(fikaPlayer) &&
                !_transittedPlayers.Contains(fikaPlayer.RaidId) &&
                !coopHandler.ExtractedPlayers.Contains(fikaPlayer.NetId))
            {
                humanPlayers++;
            }
        }

        var playersInPoint = 0;
        foreach (var fikaPlayer in coopHandler.HumanPlayers)
        {
            var inPoint = _playersInTransitZone.TryGetValue(fikaPlayer.ProfileId, out var zone) && zone == pointId;
            if (fikaPlayer.HealthController?.IsAlive == true &&
                !fikaPlayer.IsYourPlayer &&
                !TransitDropouts.IsDropped(fikaPlayer) &&
                !_transittedPlayers.Contains(fikaPlayer.RaidId) &&
                !coopHandler.ExtractedPlayers.Contains(fikaPlayer.NetId) && inPoint)
            {
                playersInPoint++;
            }
        }

        if (playersInPoint < humanPlayers)
        {
            if (_zoneCheckLogged.Add(pointId + ":" + player.ProfileId))
            {
                            }

            Il2CppSystem.Collections.Generic.Dictionary<int, TransitMessagesEvent.EType> messages = new();
            messages.Add(player.RaidId, TransitMessagesEvent.EType.NonAllTeammates);

            TransitEventPacket messagePacket = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Messages,
                TransitEvent = new TransitMessagesEvent()
                {
                    Messages = messages
                }
            };

            _server.SendData(ref messagePacket, DeliveryMethod.ReliableOrdered);
            return false;
        }

                return true;
    }

    /// <summary>Compact view of the zone map for the diagnostics: one entry per known player.</summary>
    private string DescribeZoneMap(int pointId)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var entry in _playersInTransitZone)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(entry.Key).Append("=").Append(entry.Value);
            if (entry.Value == pointId) builder.Append('*');
        }
        return builder.Length == 0 ? "empty" : builder.ToString();
    }

    /// <summary>Drops registrations of players that can never transfer again.</summary>
    private void PruneStaleZoneRegistrations()
    {
        if (_playersInTransitZone.Count == 0) return;
        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        if (coopHandler == null) return;

        List<string> stale = null;
        foreach (var entry in _playersInTransitZone)
        {
            FikaPlayer member = null;
            foreach (var candidate in coopHandler.HumanPlayers)
            {
                if (candidate != null && string.Equals(candidate.ProfileId, entry.Key, StringComparison.Ordinal))
                {
                    member = candidate;
                    break;
                }
            }

            // Not in HumanPlayers any more: the profile left the raid and neither loop above
            // counts it, so the entry can stay until the raid ends.
            if (member == null) continue;
            if (TransitDropouts.IsDropped(member) ||
                member.HealthController?.IsAlive != true ||
                _transittedPlayers.Contains(member.RaidId) ||
                coopHandler.ExtractedPlayers.Contains(member.NetId))
            {
                stale ??= [];
                stale.Add(entry.Key);
            }
        }

        if (stale == null) return;
        foreach (var profileId in stale)
        {
            TransitZonePresence.RemoveFromZone(_playersInTransitZone, profileId, "dead, extracted or already transferred");
        }
    }

    /// <summary>
    /// The native transfer entry point of this raid plus everything this host issues itself. The native
    /// calls carry the players it knows; the countdown end of this controller covers the rest, and the
    /// issued set keeps a player from being transferred twice.
    /// </summary>
    public override Il2CppSystem.Threading.Tasks.Task Transit(TransitPoint point, int playersCount, string hash,
        Il2CppSystem.Collections.Generic.Dictionary<string, ProfileKey> keys, Player player)
    {
        var callDestination = TransitDestination.Resolve(point, out var destinationSource);
                if (point == null || player == null)
        {
                        return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        }

        if (_transferIssued.Contains(player.RaidId))
        {
                                    return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        }

        // The native arguments are kept for the transfers this host issues itself.
        _lastTransitHash = hash;
        _lastTransitKeys = keys;
        _lastTransitPlayersCount = playersCount;

        if (keys == null || !keys.ContainsKey(player.ProfileId))
        {
            // The native call may carry the key set of another group; the transfer itself does not depend
            // on this machine's entry (the client validates the destination against its own profile), so
            // this is reported instead of dropping the player.
            FikaGlobals.LogWarning($"[TransitDiag] Transit for {player.ProfileId}: the native key set did not contain this profile");
        }

        if (!pointsById.TryGetValue(point.parameters.id, out var registeredPoint) ||
            !registeredPoint.Enabled || _transittedPlayers.Contains(player.RaidId))
        {
                        ReleaseTransitBanFor(player.RaidId, player.ProfileId,
                "transit did not start (invalid request, disabled point or already transferred)");
            return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        }

        if (!EnsureSummonedTransit(player.ProfileId))
        {
                                    ReleaseTransitBanFor(player.RaidId, player.ProfileId,
                "transit did not start (no transit history for this profile)");
            return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        }

        var destination = BuildDestination(player, point, hash, keys, playersCount);
        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.Extract,
            PlayerId = player.RaidId,
            TransitId = point.parameters.id,
            Destination = destination
        };

        alreadyTransits[player.ProfileId] = destination;
        _transittedPlayers.Add(player.RaidId);
        _transferIssued.Add(player.RaidId);
                _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);

        if (!_headlessTransit)
        {
            ExtractHeadlessClient(point, destination);
        }

                return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
    }

    private void ExtractHeadlessClient(TransitPoint point, LocationTransit destination)
    {
        _headlessTransit = true;
        _usedPoint = point.parameters.name;

        if (TarkovApplication.Exist(out var tarkovApplication))
        {
            // A dedicated host has no local player transit entry. Follow the
            // first remote player's validated destination for the next raid.
            tarkovApplication.TransitionStatus = new TransitionStatus(destination.location, false,
                destination.side, destination.raidMode, destination.dayTime);
        }

        FikaBackendUtils.IsTransit = true;
        // Keep every continuation on Unity's update thread while checking
        // native player state across the transit wait.
        MainThreadDispatcher.RunAsync(DelayHeadlessExtract).Forget();
    }

    private async Task DelayHeadlessExtract()
    {
        var game = FikaSingleton<IFikaGame>.Instance;
        var usedPoint = _usedPoint;
        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        if (coopHandler == null)
        {
            FikaGlobals.LogError("CoopHandler was null, quitting after 3 seconds");
            await Task.Delay(3000);
        }
        else
        {
            while (coopHandler.HumanPlayers.Exists(player =>
                !player.IsYourPlayer && player.HealthController?.IsAlive == true &&
                !_transittedPlayers.Contains(player.RaidId) &&
                !coopHandler.ExtractedPlayers.Contains(player.NetId)))
            {
                await Task.Delay(1000);
            }
        }

        // Task.Delay resumes on a pool thread. Game teardown touches Unity and
        // native EFT objects, so hand it back to the game update thread. A
        // delayed completion from an old raid must not stop a newer raid.
        MainThreadDispatcher.Post(() =>
        {
            if (ReferenceEquals(FikaSingleton<IFikaGame>.Instance, game))
            {
                game?.Stop(string.Empty, ExitStatus.Transit, usedPoint);
                return;
            }

            // The raid that should have ended is not the running one any more, so the players this raid
            // registered for a transfer are still in a raid while the ban keeps them from extracting:
            // lift it for each of them, exactly like the playable host does for its own player.
            ReleaseTransitBansOfThisRaid("headless raid was replaced before its transit stop");
        });
    }

    /// <summary>
    /// Lifts the transit ban an accepted interaction applied (see <see cref="InteractWithTransit"/>) when
    /// the transfer is not going to happen after all. Mirror of the playable host's
    /// <c>FikaHostTransitController.ReleaseTransitBanFor</c> (this controller is a native
    /// <see cref="TransitController"/>, so the state lives on the shared <c>ExfiltrationController</c>).
    /// <para>
    /// The accept path adds the player to <c>ExfiltrationController.BannedPlayers</c> and cancels his
    /// extraction, while the only native release runs on the way out through
    /// <c>TransitController.EnableExits(player)</c> - an aborted transit therefore left the player unable
    /// to extract for the rest of the raid.
    /// </para>
    /// Idempotent: removing an id that is not banned changes nothing, and exit interaction is re-enabled
    /// only when no banned player is left, so a player still waiting for his own transit is never
    /// unblocked.
    /// </summary>
    private void ReleaseTransitBanFor(int raidId, string profileId, string reason)
    {
        var exfiltration = ExfiltrationController.Instance;
        if (exfiltration?.BannedPlayers == null)
        {
                        return;
        }

        var removed = exfiltration.BannedPlayers.Remove(raidId);
        var remaining = exfiltration.BannedPlayers.Count;
        if (remaining == 0)
        {
            // Only the empty set re-opens the exits; another player's ban stays enforced.
            exfiltration.EnableExitsInteraction();
            // Nobody waits for a transit any more, so the exits at the point react again as well.
            TransitCoLocatedExits.Release("no player waits for a transit any more");
        }

        var who = string.IsNullOrEmpty(profileId) ? $"raidId={raidId}" : $"{profileId} (raidId={raidId})";
            }

    /// <summary>
    /// Releases the transit ban of every player this raid registered for a transfer. Used when the raid
    /// that should have ended is not the running one any more: its players are still in a raid, and the
    /// ban would keep them from extracting for good.
    /// </summary>
    private void ReleaseTransitBansOfThisRaid(string reason)
    {
        if (_transittedPlayers.Count == 0) return;
        foreach (var raidId in _transittedPlayers)
        {
            ReleaseTransitBanFor(raidId, ProfileIdForRaidId(raidId), reason);
        }
    }

    /// <summary>Profile that owns a raid id in this raid, or null when that player is already gone.</summary>
    private string ProfileIdForRaidId(int raidId)
    {
        var players = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler?.HumanPlayers;
        if (players == null) return null;
        foreach (var candidate in players)
        {
            if (candidate != null && candidate.RaidId == raidId) return candidate.ProfileId;
        }

        return null;
    }

    public override void Dispose()
    {
        OnPlayerEnter -= _onHeadlessPlayerEnter;
        OnPlayerExit -= _onHeadlessPlayerExit;
        _zoneExitLogged.Clear();
        _gateLogged.Clear();
        _interactedPlayers.Clear();
        _interactionLogged.Clear();
        _transferIssued.Clear();
        _countdownPointId = -1;
        _countdownEndsAt = 0f;
        _countdownSeconds = 0;
        _lastBroadcastSecond = -1;
        _recheckLoggedSignature = null;
        _recheckCount = 0;
        TransitDropouts.Forget();
        // Stops the clock of the old raid: its next step sees another generation and returns.
        _countdownGeneration++;
        _lastTransitHash = null;
        _lastTransitKeys = null;
        _lastTransitPlayersCount = 0;
        // Give the exits of the point their interaction back (a destroyed point is skipped), then close the
        // ledger: the next raid starts with an empty suppression list.
        TransitCoLocatedExits.Release("the raid ended");
        TransitCoLocatedExits.Forget();
        _transferFailures.Clear();
    }

    public void Init()
    {
        // A new raid: nothing of the world that ended may survive in a static of this chain. The teardown
        // sequence of WorldLifecycle clears both ledgers as well (TransitCoLocatedExits and TransitDropouts
        // expose ForceResetForWorldTeardown for it); these calls are the self-healing half that also covers a
        // teardown which never reached this class, and they touch no native object at all.
        TransitCoLocatedExits.ForceResetForWorldTeardown("a new raid starts");
        TransitDropouts.ForceResetForWorldTeardown("a new raid starts");
        // The one subscription that feeds the dropout registry is armed once per process (idempotent).
        TransitDropouts.Arm();
        EnablePoints(true);
    }
}

