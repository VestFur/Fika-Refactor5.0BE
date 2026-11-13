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
    private RunddansStateEvent _pendingRunddans;
    private System.Collections.Generic.Dictionary<int, float> _pendingRunddansDurability;
    private System.Collections.Generic.Dictionary<int, long> _pendingRunddansDateTimes;
    private bool _pendingRunddansStarted;
    private KolotunStatePacket? _pendingKolotunState;
    private float _kolotunStateDeadline;
    private readonly Queue<EventControllerEventPacket> _pendingRunddansMessages = new();
    private SyncTransitControllersPacket? _pendingTransitHistory;

    /// <summary>
    /// Map-state generation of the pending transit history (see <see cref="_mapStateGeneration"/>).
    /// </summary>
    private int _pendingTransitHistoryGeneration;

    private readonly Dictionary<(int NetId, string ItemId), float> _pendingEventItems = new();

    /// <summary>
    /// Counts the world generations of this session. It is raised by <see cref="ClearMapState"/>, i.e. once
    /// per world swap (a transit reload tears the world down long before the next one exists), and it is the
    /// only thing that decides whether a queued transit event still belongs to the world that is being
    /// created: entries of generation N-1 are the ones that arrived in the swap window, anything older has
    /// already survived a whole world without being consumed and is dropped (analysis/transit-reload-sync).
    /// </summary>
    private int _mapStateGeneration;

    /// <summary>Transit events that were queued since the last world swap, and their types.</summary>
    private int _transitQueueReceivedThisWindow;
    private int _transitQueueDroppedThisWindow;
    private readonly Dictionary<string, int> _transitQueueTypesThisWindow = new();
    internal bool HasPendingMapLoads => _pendingBtrStashes != null || _pendingTransitStashes != null ||
        _pendingBtrSpawn != null || _hasBtrState ||
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
        _appliedBtrGeneration = 0;
        _pendingRunddans = null;
        _pendingRunddansDurability = null;
        _pendingRunddansDateTimes = null;
        _pendingRunddansStarted = false;
        _pendingRunddansMessages.Clear();
        _pendingKolotunState = null;
        _kolotunStateDeadline = 0f;
        // A world swap *is* the transit reload window: a transit tears the world down long before the next
        // one exists, so the transit state that arrived in between belongs to the raid that is being created
        // now and must survive this reset. Only what is provably older than one generation (it already
        // survived a whole world without being consumed) is dropped, and the swap reports what it saw
        // (analysis/transit-reload-sync; the queue used to be cleared here, which threw every event of the
        // window away - including the host's initialization snapshot of the new raid).
        _mapStateGeneration++;
        var droppedAtSwap = DropStaleTransitQueue(_mapStateGeneration);
        if (_pendingTransitHistory != null && _pendingTransitHistoryGeneration < _mapStateGeneration - 1)
        {
            droppedAtSwap++;
            _pendingTransitHistory = null;
        }
        ReportTransitQueueAtWorldSwap(_mapStateGeneration, droppedAtSwap);
        _pendingEventItems.Clear();
    }

    /// <summary>
    /// The transit-event half of the world swap: the entries of the window are kept for the new raid's
    /// transit controller, everything older is dropped and counted.
    /// </summary>
    /// <returns>The number of entries that were dropped.</returns>
    private int DropStaleTransitQueue(int generation)
    {
        if (_pendingTransitEvents.Count == 0)
        {
            return 0;
        }

        var kept = new List<(TransitEventPacket Packet, int Generation)>(_pendingTransitEvents.Count);
        var dropped = 0;
        while (_pendingTransitEvents.TryDequeue(out var entry))
        {
            if (entry.Generation < generation - 1)
            {
                dropped++;
                DropStaleTransitEvent(entry.Packet, entry.Generation, "a world swap");
                continue;
            }

            kept.Add(entry);
        }

        foreach (var entry in kept)
        {
            _pendingTransitEvents.Enqueue(entry);
        }

        return dropped;
    }

    /// <summary>True when an entry of this generation can still belong to the world that is now in place.</summary>
    private bool IsTransitEventGenerationCurrent(int generation)
    {
        return generation >= _mapStateGeneration - 1;
    }

    /// <summary>Counts one dropped entry and names it in the diagnostics file (the swap line carries the totals).</summary>
    private void DropStaleTransitEvent(TransitEventPacket packet, int generation, string site)
    {
        _transitQueueDroppedThisWindow++;
        LoadSync.Trace("transit event dropped at " + site + ": type=" + (packet == null ? "null" : packet.EventType.ToString())
            + " queuedInGeneration=" + generation + " currentGeneration=" + _mapStateGeneration
            + " => it already survived a whole world without being consumed");
    }

    /// <summary>One line per world swap that saw transit traffic: what arrived, what was kept, what was dropped.</summary>
    private void ReportTransitQueueAtWorldSwap(int generation, int droppedAtSwap)
    {
        var kept = _pendingTransitEvents.Count;
        var history = _pendingTransitHistory == null ? "none" : "kept";
        if (_transitQueueReceivedThisWindow == 0 && kept == 0 && droppedAtSwap == 0 && _pendingTransitHistory == null)
        {
            // Nothing arrived in the window and nothing is waiting: a normal raid start stays silent.
            return;
        }

        var types = new System.Text.StringBuilder();
        foreach (var entry in _transitQueueTypesThisWindow)
        {
            if (types.Length > 0) types.Append(',');
            types.Append(entry.Key).Append('=').Append(entry.Value);
        }

        LoadSync.Report("FikaClient.ClearMapState",
            "transit event queue at the world swap: arrived=" + _transitQueueReceivedThisWindow + " kept=" + kept
            + " dropped=" + droppedAtSwap + " reason=dropped entries were queued before the previous world; kept ones are the swap window of the raid being created"
            + " types=[" + (types.Length == 0 ? "none" : types.ToString()) + "] history=" + history
            + " generation=" + (generation - 1) + "->" + generation + " deltaDroppedTotal=" + _transitQueueDroppedThisWindow
            + ", " + TransitReloadGate.Describe()
            + " => the transit state that arrived while this machine had no world is handed to the new raid's controller instead of being dropped at the swap");

        _transitQueueReceivedThisWindow = 0;
        _transitQueueDroppedThisWindow = 0;
        _transitQueueTypesThisWindow.Clear();
    }

    /// <summary>Counts one queued transit event for the swap line of the next world.</summary>
    internal void NoteTransitEventQueued(TransitEventPacket.ETransitEventType type)
    {
        _transitQueueReceivedThisWindow++;
        var key = type.ToString();
        _transitQueueTypesThisWindow.TryGetValue(key, out var count);
        _transitQueueTypesThisWindow[key] = count + 1;
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
        _pendingTransitHistoryGeneration = _mapStateGeneration;
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
                if (!IsTransitEventGenerationCurrent(_pendingTransitHistoryGeneration))
                {
                    // It was queued before the previous world and never consumed: it cannot describe the
                    // raid that is running now (analysis/transit-reload-sync).
                    LoadSync.Warn("FikaClient.ApplyPendingMapState",
                        "the pending transit history for " + history.ProfileId + " was dropped: it was queued in map-state generation "
                        + _pendingTransitHistoryGeneration + " and this world is generation " + _mapStateGeneration);
                    _pendingTransitHistory = null;
                }
                else
                {
                    world.TransitController.summonedTransits[history.ProfileId] =
                        new(history.RaidId, history.Count, history.Maps, history.Events);
                    _pendingTransitHistory = null;
                }
            }
            if (_pendingBtrStashes != null && ApplyTransferStashes(world.BtrController?.TransferItemsController, _pendingBtrStashes, "BTR"))
                _pendingBtrStashes = null;
            if (_pendingTransitStashes != null && ApplyTransferStashes(world.TransitController?.TransferItemsController, _pendingTransitStashes, FikaGlobals.TransitTraderName))
                _pendingTransitStashes = null;
            if ((_pendingBtrStashes != null || _pendingTransitStashes != null) && Time.unscaledTime > _stashDeadline)
            {
                _logger.LogError("Transfer stash initialization timed out waiting for map controllers");
                _pendingBtrStashes = _pendingTransitStashes = null;
            }
            var btr = world.BtrController;
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
                        // gain one, so this pending flag must not stay set forever.
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
            // A late join can deliver the same snapshot twice; repeated snapshots within this
            // controller must preserve its live items and owners instead of registering them twice.
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

}
