using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Audio.Vehicles.BTR;
using Comfort.Common;
using EFT;
using EFT.GlobalEvents;
using EFT.InventoryLogic;
using EFT.Vehicle;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.Networking.Packets.Communication;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.World;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Grid = EFT.InventoryLogic.Grid;

namespace Fika.Core.Networking;

public sealed partial class FikaClient
{
    private GameWorld _mapStateWorld;
    private Stash[] _pendingBtrStashes;
    private Stash[] _pendingTransitStashes;
    private float _stashDeadline;
    private BtrSpawn _pendingBtrSpawn;
    private uint _appliedBtrGeneration;
    private BTRView _readyBtrView;
    private ShapshotBTRMessage _pendingBtrState;
    private bool _hasBtrState;
    private List<ReconnectPacket.BtrPassengerReconnectState> _pendingBtrPassengers;
    private List<TraderServiceAvailabilityData> _pendingBtrServices;
    private RunddansStateEvent _pendingRunddans;
    private System.Collections.Generic.Dictionary<int, float> _pendingRunddansDurability;
    private System.Collections.Generic.Dictionary<int, long> _pendingRunddansDateTimes;
    private bool _pendingRunddansStarted;
    private KolotunStatePacket? _pendingKolotunState;
    private float _kolotunStateDeadline;
    private readonly Queue<EventControllerEventPacket> _pendingRunddansMessages = new();
    private SyncTransitControllersPacket? _pendingTransitHistory;
    private readonly Dictionary<(int NetId, string ItemId), float> _pendingEventItems = new();
    internal bool HasPendingMapLoads => _pendingBtrStashes != null || _pendingTransitStashes != null ||
        _pendingBtrSpawn != null || _pendingBtrPassengers != null || _pendingBtrServices != null || _hasBtrState ||
        _pendingRunddans != null || _pendingRunddansStarted || _pendingRunddansMessages.Count > 0 ||
        _pendingKolotunState.HasValue ||
        _pendingTransitHistory.HasValue || _pendingEventItems.Count > 0;

    private bool BindMapWorld()
    {
        var world = Singleton<GameWorld>.Instance;
        if (world == null) return false;
        if (_mapStateWorld != null && _mapStateWorld.Pointer != world.Pointer)
        {
            ClearMapState();
        }
        _mapStateWorld ??= world;
        return true;
    }

    private void ClearMapState()
    {
        _mapStateWorld = null;
        _pendingBtrStashes = _pendingTransitStashes = null;
        _pendingBtrSpawn = null;
        _readyBtrView = null;
        _hasBtrState = false;
        _pendingBtrPassengers = null;
        _pendingBtrServices = null;
        _appliedBtrGeneration = 0;
        _pendingRunddans = null;
        _pendingRunddansDurability = null;
        _pendingRunddansDateTimes = null;
        _pendingRunddansStarted = false;
        _pendingRunddansMessages.Clear();
        _pendingKolotunState = null;
        _kolotunStateDeadline = 0f;
        _pendingTransitHistory = null;
        _pendingEventItems.Clear();
        _pendingTransitEvents.Clear();
    }

    private void QueueStashes(StashesPacket packet)
    {
        if (packet.HasBTR) _pendingBtrStashes = packet.BTRStashes;
        if (packet.HasTransit) _pendingTransitStashes = packet.TransitStashes;
        _stashDeadline = Time.unscaledTime + 60f;
    }

    internal void QueueBtrSpawn(BtrSpawn packet)
    {
        if (packet.Generation <= _appliedBtrGeneration ||
            (_pendingBtrSpawn != null && packet.Generation < _pendingBtrSpawn.Generation)) return;
        // Generic packets return to the pool immediately after Execute.
        _pendingBtrSpawn = BtrSpawn.CreateInstance();
        _pendingBtrSpawn.Position = packet.Position;
        _pendingBtrSpawn.Rotation = packet.Rotation;
        _pendingBtrSpawn.PlayerRaidId = packet.PlayerRaidId;
        _pendingBtrSpawn.Generation = packet.Generation;
    }

    internal void MarkBtrViewReady(BTRView view)
    {
        if (view == null) return;
        if (Singleton<GameWorld>.Instance != null) BindMapWorld();
        _readyBtrView = view;
    }

    private void QueueBtrState(ShapshotBTRMessage state)
    {
        _pendingBtrState = state;
        _hasBtrState = true;
    }

    private void QueueBtrPassengers(List<ReconnectPacket.BtrPassengerReconnectState> passengers)
    {
        // The host always sends a passenger list, including on maps where no
        // BTR view will ever spawn. An empty list must not hold reconnect open.
        _pendingBtrPassengers = passengers is { Count: > 0 } ? passengers : null;
    }

    private void QueueBtrServices(List<TraderServiceAvailabilityData> services)
    {
        if (services == null) return;
        _pendingBtrServices = services;
    }

    private void QueueRunddansState(EventControllerEventPacket packet)
    {
        _pendingRunddans = packet.Event?.TryCast<RunddansStateEvent>();
        _pendingRunddansDurability = packet.Durability;
        _pendingRunddansDateTimes = packet.DateTimeTicks;
        if (_pendingRunddans?.Objects != null)
        {
            // A state update must not erase an earlier StartedEvent. The host
            // locks transit for the event even after the generator stops running.
            foreach (var entry in _pendingRunddans.Objects)
            {
                if (entry.Value != EventObject.EState.Running) continue;
                _pendingRunddansStarted = true;
                break;
            }
        }
    }

    private void QueueKolotunState(KolotunStatePacket packet)
    {
        if (Singleton<GameWorld>.Instance != null) BindMapWorld();
        _pendingKolotunState = packet;
        _kolotunStateDeadline = Time.unscaledTime + 60f;
    }

    private void QueueRunddansStarted()
    {
        _pendingRunddansStarted = true;
        BindMapWorld();
    }

    private void QueueRunddansMessage(EventControllerEventPacket packet)
    {
        if (packet.Event == null) return;
        BindMapWorld();
        if (_pendingRunddansMessages.Count >= 256)
        {
            _logger.LogError("Too many pending Runddans messages");
            return;
        }
        _pendingRunddansMessages.Enqueue(packet);
    }

    private void QueueTransitHistory(SyncTransitControllersPacket packet)
    {
        _pendingTransitHistory = packet;
        BindMapWorld();
    }

    private void QueueEventItemRemoval(int netId, string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return;
        if (_pendingEventItems.Count >= 256)
            throw new InvalidOperationException("Too many pending event inventory changes");
        _pendingEventItems.TryAdd((netId, itemId), Time.unscaledTime + 60f);
    }

    private void ApplyPendingMapState()
    {
        var world = Singleton<GameWorld>.Instance;
        if (world == null) return;
        if (!BindMapWorld()) return;
        if (world.Pointer != _mapStateWorld.Pointer)
        {
            ClearMapState();
            _mapStateWorld = world;
            return;
        }
        try
        {
            if (_pendingTransitHistory is { } history && world.TransitController != null)
            {
                world.TransitController.summonedTransits[history.ProfileId] =
                    new(history.RaidId, history.Count, history.Maps, history.Events);
                _pendingTransitHistory = null;
            }
            if (_pendingBtrStashes != null && ApplyTransferStashes(world.BtrController?.TransferItemsController, _pendingBtrStashes, "BTR"))
                _pendingBtrStashes = null;
            if (_pendingTransitStashes != null && ApplyTransferStashes(world.TransitController?.TransferItemsController, _pendingTransitStashes, FikaGlobals.TransitTraderName))
                _pendingTransitStashes = null;
            if ((_pendingBtrStashes != null || _pendingTransitStashes != null) && Time.unscaledTime > _stashDeadline)
            {
                _logger.LogError("Transfer stash initialization timed out waiting for map controllers");
                ReconnectError = "Transfer stash initialization timed out waiting for map controllers";
                _pendingBtrStashes = _pendingTransitStashes = null;
            }
            var btr = world.BtrController;
            if (_pendingBtrServices != null && MyPlayer != null)
            {
                MyPlayer.ReceiveTraderServicesData(_pendingBtrServices);
                _pendingBtrServices = null;
            }
            if (_readyBtrView != null && btr?.BtrView != null && btr.BtrView.Pointer == _readyBtrView.Pointer)
            {
                if (_pendingBtrSpawn != null)
                {
                    var spawn = _pendingBtrSpawn;
                    _pendingBtrSpawn = null;
                    GlobalEventsController.CreateEvent<BtrSpawnOnThePathEvent>().Invoke(spawn.Position, spawn.Rotation, spawn.PlayerRaidId);
                    _appliedBtrGeneration = spawn.Generation;
                }
                if (_hasBtrState && _appliedBtrGeneration > 0)
                {
                    _readyBtrView.SyncViewFromServer(ref _pendingBtrState);
                    _hasBtrState = false;
                }
                if (_pendingBtrPassengers != null && ApplyBtrPassengers(world, btr))
                    _pendingBtrPassengers = null;
            }
            if (_pendingRunddans != null && MyPlayer != null &&
                FikaWorldControllers.Find<Main.ClientClasses.ClientRunddansController>(world.RunddansController) is { } events)
            {
                _pendingRunddans.PlayerRaidId = MyPlayer.RaidId;
                events.Native.State(_pendingRunddans);
                // State() updates the visible generator state.  Restore the
                // native durability and absolute game date afterwards so the
                // first FixedUpdate cannot overwrite a late-join snapshot
                // with the default values.
                if (events.Native.Objects is { } objects)
                {
                    foreach (var entry in _pendingRunddansDurability ?? new())
                    {
                        if (!objects.TryGetValue(entry.Key, out var eventObject)) continue;
                        eventObject._durability = entry.Value;
                        if (_pendingRunddansDateTimes?.TryGetValue(entry.Key, out var ticks) == true && ticks != 0)
                            eventObject._dateTime = new Il2CppSystem.DateTime(ticks);
                    }
                }
                _pendingRunddans = null;
                _pendingRunddansDurability = null;
                _pendingRunddansDateTimes = null;
            }
            if (_pendingKolotunState is { } kolotunState && world.KolotunController != null)
            {
                GlobalEventsController.CreateEvent<KolotunStateEvent>().Invoke(kolotunState.IsActive, kolotunState.EventStage);
                _pendingKolotunState = null;
            }
            else if (_pendingKolotunState.HasValue && Time.unscaledTime > _kolotunStateDeadline)
            {
                _logger.LogError("Kolotun state timed out waiting for the map controller");
                ReconnectError = "Kolotun state timed out waiting for the map controller";
                _pendingKolotunState = null;
            }
            if (FikaWorldControllers.Find<Main.ClientClasses.ClientRunddansController>(world.RunddansController) is { } runddans)
            {
                if (_pendingRunddansStarted)
                {
                    if (FikaWorldControllers.Find<Main.ClientClasses.ClientTransitController>(world.TransitController) is { } transit)
                    {
                        transit.Native.EnablePoints(false);
                        transit.UpdateTimers();
                        _pendingRunddansStarted = false;
                    }
                    else if (world.TransitController == null)
                    {
                        // Transit initialization precedes Runddans initialization.
                        // A map with Runddans but no transit controller will never
                        // gain one, so this pending flag must not stall reconnect.
                        _pendingRunddansStarted = false;
                    }
                }
                while (_pendingRunddansMessages.Count > 0)
                    _pendingRunddansMessages.Dequeue().Event?.Invoke();
            }
            if (_pendingEventItems.Count > 0 &&
                FikaWorldControllers.Find<Main.ClientClasses.ClientRunddansController>(world.RunddansController) is { } itemEvents)
            {
                foreach (var pending in new List<KeyValuePair<(int NetId, string ItemId), float>>(_pendingEventItems))
                {
                    if (_coopHandler.Players.TryGetValue(pending.Key.NetId, out var player))
                    {
                        itemEvents.DestroyItem(player, pending.Key.ItemId);
                        _pendingEventItems.Remove(pending.Key);
                    }
                    else if (Time.unscaledTime > pending.Value)
                    {
                        _logger.LogWarning($"Event item removal expired for missing player {pending.Key.NetId}, item {pending.Key.ItemId}");
                        _pendingEventItems.Remove(pending.Key);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Map state initialization failed: {ex}");
            ReconnectError = "Map state initialization failed: " + ex.Message;
            ClearMapState();
        }
    }

    private static bool ApplyTransferStashes(TransferItemsController controller, Stash[] stashes, string name)
    {
        if (controller?.Stash == null) return false;
        foreach (var stash in stashes)
        {
            var exists = false;
            foreach (var current in controller._transferContainers)
                if (current.Id == stash.Id) { exists = true; break; }
            // Reconnect builds a new controller; repeated snapshots within this controller
            // must preserve its live items and owners instead of registering them twice.
            if (!exists) controller.InitTransferContainer(stash, name);
            var grids = new List<Grid>();
            var found = false;
            foreach (var grid in controller.Stash.Grids)
            {
                if (grid.ID == stash.Id)
                {
                    if (found) continue;
                    found = true;
                }
                grids.Add(grid);
            }
            if (!found)
                grids.Add(new Grid(stash.Id, 10, 10, true, false, new Il2CppReferenceArray<ItemFilter>(0), controller.Stash, -1));
            controller.Stash.Grids = grids.ToArray();
        }
        return true;
    }

    private bool ApplyBtrPassengers(GameWorld world, BtrController controller)
    {
        if (controller?.BtrView == null || _pendingBtrPassengers == null) return false;
        var resolved = new List<(Player Player, BTRSide Side, ReconnectPacket.BtrPassengerReconnectState Snapshot)>();
        foreach (var snapshot in _pendingBtrPassengers)
        {
            var player = _coopHandler.HumanPlayers.Find(x => x.RaidId == snapshot.PlayerRaidId);
            if (player == null) return false;
            var side = controller.BtrView.GetBtrSide(snapshot.SideId);
            if (side == null) return false;
            resolved.Add((player, side, snapshot));
        }
        // Reconnect can deliver this snapshot before every observed player is
        // spawned. Resolve the full set first so retries do not repeatedly
        // apply the early passengers while waiting for a later one.
        foreach (var (player, side, snapshot) in resolved)
        {
            if (controller.BtrVehicle?.IsPassenger(player, out _) != true)
            {
                side.AddPassenger(player, snapshot.SlotId);
            }
            player.BtrState = snapshot.State;
            ApplyBtrPassengerRuntimeState(controller.BtrView, side, player, snapshot.State);
        }
        return true;
    }

    /// <summary>
    /// Reconnect snapshots contain the passenger state, but the native BTR
    /// passenger list does not recreate the movement/input side effects that
    /// the normal GoIn/GoOut animation path applies.  Restore those effects
    /// synchronously after adding the passenger so a reconnect cannot leave a
    /// player rendered in the vehicle while still walking or sending movement.
    /// </summary>
    private static void ApplyBtrPassengerRuntimeState(BTRView view, BTRSide side, Player player, EPlayerBtrState state)
    {
        if (player == null) return;
        try
        {
            var inside = state is EPlayerBtrState.Inside or EPlayerBtrState.GoIn or EPlayerBtrState.Approach;
            if (inside)
            {
                player.MovementContext.IsAxesIgnored = true;
                player.InputDirection = new(0, 0);
                if (player.TryCast<FikaPlayer>() is { } fika)
                {
                    fika.PacketSender.SendState = false;
                    if (state is EPlayerBtrState.GoIn or EPlayerBtrState.Approach)
                        fika.HideWeapon();
                }
                if (state == EPlayerBtrState.Inside)
                    player.CharacterController.isEnabled = false;
                view._soundController?.UpdateBtrAudioRoom(EnvironmentType.Indoor, player);
                side.ApplyLocalPassenger();
                return;
            }

            // A snapshot normally contains only active passengers.  Keep the
            // outside branch defensive for an in-flight GoOut snapshot.
            if (state is EPlayerBtrState.GoOut or EPlayerBtrState.Outside)
            {
                if (state == EPlayerBtrState.Outside)
                {
                    side.RemovePassenger(player);
                    player.CharacterController.isEnabled = true;
                    player.MovementContext.IsAxesIgnored = false;
                    if (player.TryCast<FikaPlayer>() is { } fika) fika.PacketSender.SendState = true;
                    view._soundController?.UpdateBtrAudioRoom(EnvironmentType.Outdoor, player);
                }
            }
        }
        catch (System.Exception ex)
        {
            FikaGlobals.LogWarning($"BTR reconnect passenger runtime restore failed for {player.ProfileId}: {ex.Message}");
        }
    }
}
