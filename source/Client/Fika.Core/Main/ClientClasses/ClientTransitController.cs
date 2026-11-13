using Fika.Core.Main.BaseClasses;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using JsonType;
using Il2CppSystem.Collections.Generic;
using EFT;
using EFT.GlobalEvents;
using EFT.Interactive;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.Communication;

namespace Fika.Core.Main.ClientClasses;

public class ClientTransitController : FikaWorldController<NetworkTransitController>
{
    private readonly Il2CppSystem.Action<TransitPoint, Player> _onPlayerEnter;
    private readonly System.Collections.Generic.Dictionary<int, TransitInitEvent> _pendingInits = new();
    private readonly System.Collections.Generic.Dictionary<int, TransitEventPacket> _pendingSnapshots = new();
    private bool _ready;
    private bool _initializationPending;
    private readonly System.Collections.Generic.Queue<(TransitEventPacket Packet, float ReceivedAt)> _pendingEvents = new();

    public ClientTransitController(TransitGlobalSettings settings, Il2CppReferenceArray<LocationSettings.Location.TransitParameters> parameters, Profile profile, LocalRaidSettings localRaidSettings)
        : base(new NetworkTransitController(settings, parameters))
    {
        _onPlayerEnter = (Il2CppSystem.Action<TransitPoint, Player>)OnClientPlayerEnter;
        Native.OnPlayerEnter += _onPlayerEnter;
        Native.summonedTransits[profile.Id] = new Transit(localRaidSettings.transition.transitionRaidId,
            localRaidSettings.transition.transitionCount, FikaTransitHistory.AppendLocation(localRaidSettings),
            localRaidSettings.transitionType.HasFlagNoBox(ELocationTransition.Event));
        Native.TransferItemsController.InitItemControllerServer(FikaGlobals.TransitTraderId, FikaGlobals.TransitTraderName);
        foreach (var point in Native.pointsById.Values) point.gameObject.SetActive(true);
    }

    internal void OnInitEvent(TransitInitEvent data)
    {
        var localPlayer = GamePlayerOwner.MyPlayer ?? FikaSingleton<Networking.FikaClient>.Instance?.MyPlayer;
        if (localPlayer != null && localPlayer.RaidId != data.PlayerRaidId) return;
        // Native Init can arrive before the timer panel / PlayerOwner exists.
        if (GamePlayerOwner.MyPlayer == null || !_ready ||
            !NetworkTransitController.IsTargetPlayer(data.PlayerRaidId, out var player))
        {
            _pendingInits[data.PlayerRaidId] = data;
            return;
        }
        _pendingInits.Remove(data.PlayerRaidId);
        Native._completedQuestRequirementMetByPointId = data.CompletedQuestRequirementMetByPointId ?? new();
        if (Native.summonedTransits.TryGetValue(player.ProfileId, out var transit))
        {
            transit.count = data.TransitionCount;
            transit.events = data.EventPlayer;
        }
        var list = Native.GetTransitPoints(data.Points, player.Side);
        Native.SetTimers(list, player, false);
        Native.HandleExits(list, player);
    }

    internal void ReceiveSnapshot(TransitEventPacket packet)
    {
        if (packet.TransitEvent?.TryCast<TransitInitEvent>() is not { } data) return;
        var localPlayer = GamePlayerOwner.MyPlayer ?? FikaSingleton<Networking.FikaClient>.Instance?.MyPlayer;
        if (localPlayer != null && localPlayer.RaidId != data.PlayerRaidId) return;
        // TransitEventPacket is reused by LiteNetLib: retain independent fields.
        _pendingSnapshots[data.PlayerRaidId] = new TransitEventPacket
        {
            TransitEvent = data, EnabledPointIds = packet.EnabledPointIds,
            RegisteredPointId = packet.RegisteredPointId, KeyId = packet.KeyId, SelectedTime = packet.SelectedTime
        };
        if (_ready) ApplySnapshot();
    }

    private void ApplySnapshot()
    {
        var player = GamePlayerOwner.MyPlayer;
        if (player == null || !_pendingSnapshots.TryGetValue(player.RaidId, out var snapshot)) return;
        var data = snapshot.TransitEvent.Cast<TransitInitEvent>();
        if (!NetworkTransitController.IsTargetPlayer(data.PlayerRaidId, out _)) return;
        _pendingSnapshots.Remove(player.RaidId);
        OnInitEvent(data);
        var enabled = new System.Collections.Generic.HashSet<int>(snapshot.EnabledPointIds ?? []);
        foreach (var entry in Native.pointsById) entry.Value.Enabled = enabled.Contains(entry.Key);
        if (snapshot.RegisteredPointId >= 0 && Native.pointsById.ContainsKey(snapshot.RegisteredPointId))
        {
            Native.transitPlayers[player.ProfileId] = player.RaidId;
            Native.profileKeys[player.ProfileId] = snapshot.KeyId;
            Native.profileTime[player.ProfileId] = snapshot.SelectedTime;
            InteractPacket = new() { hasInteraction = true, pointId = snapshot.RegisteredPointId,
                keyId = snapshot.KeyId, time = snapshot.SelectedTime };
            Native.DisableExits(player);
        }
        UpdateTimers();
    }

    public InteractWithTransitPacket InteractPacket { get; set; }

    internal void ReceiveEvent(TransitEventPacket packet)
    {
        var localPlayer = GamePlayerOwner.MyPlayer;
        if (!_ready || localPlayer == null ||
            !NetworkTransitController.IsTargetPlayer(localPlayer.RaidId, out _))
        {
            if (_pendingEvents.Count >= 256)
                throw new System.InvalidOperationException("Transit startup event queue exceeded its limit");
            _pendingEvents.Enqueue((new TransitEventPacket
            {
                EventType = packet.EventType, TransitEvent = packet.TransitEvent,
                TransitId = packet.TransitId, PlayerId = packet.PlayerId, Destination = packet.Destination
            }, Time.unscaledTime));
            return;
        }
        if (packet.EventType == TransitEventPacket.ETransitEventType.Extract)
        {
            HandleClientExtract(packet.TransitId, packet.PlayerId, packet.Destination);
            return;
        }

        // The host sends the group timer every tick, but a Fika-built event never passed through the
        // game's global event bus, so SyncEventFromServer.Invoke() below reaches no subscriber and the
        // client never shows the transit countdown. Dispatch the native overrides explicitly.
        if (packet.TransitEvent?.TryCast<TransitGroupTimerEvent>() is { } groupTimer)
        {
            Native.Timers(groupTimer.PointId, groupTimer.Timers);
            return;
        }
        if (packet.TransitEvent?.TryCast<TransitGroupSizeEvent>() is { } groupSize)
        {
            Native.Sizes(groupSize.PointId, groupSize.Sizes);
            return;
        }

        // TransitInteractionEvent has two private native subscribers in SPT 5:
        // NetworkTransitController.Interact updates the interaction/point state,
        // while ManageAntiLeaveFlag updates the normal-exit lock.  Calling only
        // SyncEventFromServer.Invoke() is not sufficient for packets created by
        // Fika because they did not pass through the game's global event bus.
        // Invoke the native handlers directly so Confirm/Hide and InactivePoint
        // have the same effects as an official network event.
        if (packet.TransitEvent?.TryCast<TransitInteractionEvent>() is { } interaction)
        {
            Native.Interact(interaction);
            Native.ManageAntiLeaveFlag(interaction);
            return;
        }

        packet.TransitEvent?.Invoke();
    }

    internal bool IsReady => _ready;

    private void OnClientPlayerEnter(TransitPoint point, Player player)
    {
        if (!Native.transitPlayers.ContainsKey(player.ProfileId))
            player.TryCast<FikaPlayer>()?.UpdateBtrTraderServiceData().HandleExceptions();
    }

    public void Init()
    {
        _ready = true;
        _initializationPending = true;
        ProcessPendingInitialization();
    }

    internal void ProcessPendingInitialization()
    {
        if (!_ready || GamePlayerOwner.MyPlayer is not { } player) return;
        if (!_initializationPending && _pendingInits.Count == 0 && _pendingSnapshots.Count == 0 && _pendingEvents.Count == 0) return;
        // The local owner can exist before the native transit target registry
        // has been populated. Keep snapshots and timer events for the next tick.
        if (!NetworkTransitController.IsTargetPlayer(player.RaidId, out _)) return;
        if (_pendingInits.TryGetValue(player.RaidId, out var data))
        {
            OnInitEvent(data);
        }
        ApplySnapshot();
        if (_initializationPending) UpdateTimers();
        _initializationPending = false;
        while (_pendingEvents.TryDequeue(out var pending))
        {
            if (pending.Packet.TransitEvent?.TryCast<TransitGroupTimerEvent>() is { } timer)
            {
                var remaining = new Dictionary<int, ushort>();
                foreach (var entry in timer.Timers)
                    remaining[entry.Key] = (ushort)System.Math.Max(0, entry.Value - (int)(Time.unscaledTime - pending.ReceivedAt));
                timer.Timers = remaining;
            }
            ReceiveEvent(pending.Packet);
        }
    }

    protected override void DisposeNative() => Native.Dispose();
    protected override void DisposeManaged()
    {
        Native.OnPlayerEnter -= _onPlayerEnter;
        _pendingInits.Clear();
        _pendingSnapshots.Clear();
        _ready = false;
        _initializationPending = false;
        _pendingEvents.Clear();
    }

    public void HandleClientExtract(int transitId, int playerId, LocationTransit destination)
    {
        if (!NetworkTransitController.IsTargetPlayer(playerId, out var player) ||
            !Native.pointsById.TryGetValue(transitId, out var point)) return;
        if (Native.alreadyTransits.ContainsKey(player.ProfileId)) return;
        if (destination == null || destination.location != point.parameters.location ||
            destination.profiles == null || !destination.profiles.ContainsKey(player.ProfileId))
        {
            FikaGlobals.LogError("Transit extract does not contain the local player's destination");
            return;
        }
        var fikaPlayer = player.TryCast<FikaPlayer>();
        if (fikaPlayer == null || FikaSingleton<IFikaGame>.Instance is not CoopGame game) return;
        if (TarkovApplication.Exist(out var application))
            application.TransitionStatus = new(destination.location, false, destination.side, destination.raidMode, destination.dayTime);
        Native.alreadyTransits[player.ProfileId] = destination;
        game.Extract(fikaPlayer, null, point);
    }

    public void UpdateTimers()
    {
        var player = GamePlayerOwner.MyPlayer;
        if (!_ready || player == null) return;
        var points = new List<TransitPoint>();
        foreach (var point in Native.pointsById.Values)
        {
            if (!Native.IsVisibleTransitPoint(point)) Native._waitForVisibleTransitPoints.Add(point);
            else points.Add(point);
        }
        Native.SetTimers(points, player, false);
    }
}
