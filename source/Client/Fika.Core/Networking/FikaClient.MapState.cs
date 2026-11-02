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
    /// 待处理 transit 历史的 map-state 代数（见 <see cref="_mapStateGeneration"/>）。
    /// </summary>
    private int _pendingTransitHistoryGeneration;

    private readonly Dictionary<(int NetId, string ItemId), float> _pendingEventItems = new();

    /// <summary>
    /// 统计本会话的世界代数。它由 <see cref="ClearMapState"/> 递增，即
    /// 每次世界切换一次（transit 重载会在下一个世界存在之前很久就销毁当前世界），而它是
    /// 唯一决定某个已入队的 transit 事件是否仍属于正在创建的
    /// 世界的依据：属于第 N-1 代的条目就是那些在切换窗口内到达的，任何更早的条目
    /// 已经整整存活过一个世界而未被消费，因此会被丢弃（analysis/transit-reload-sync）。
    /// </summary>
    private int _mapStateGeneration;

    /// <summary>自上次世界切换以来入队的 transit 事件，以及它们的类型。</summary>
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
        // 世界切换*就是* transit 重载窗口：transit 会在下一个世界存在之前很久就销毁当前世界，
        // 因此其间到达的 transit 状态属于正在被创建的 raid，
        // 并且必须挺过这次重置。只有可证明比一代更旧的东西（它已经
        // 整整存活过一个世界而未被消费）才会被丢弃，而切换会报告它所看到的情况
        // （analysis/transit-reload-sync；该队列过去在这里被清空，从而把窗口内的每个事件都扔掉
        // ——包括主机对新 raid 的初始化快照）。
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
    /// 世界切换的 transit 事件那一半：窗口内的条目会为新 raid 的
    /// transit 控制器保留，更旧的一切都被丢弃并计数。
    /// </summary>
    /// <returns>被丢弃的条目数量。</returns>
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

    /// <summary>当本代的一个条目仍可能属于当前已就位的世界时为 true。</summary>
    private bool IsTransitEventGenerationCurrent(int generation)
    {
        return generation >= _mapStateGeneration - 1;
    }

    /// <summary>统计一个被丢弃的条目并在诊断文件中命名它（切换行携带总计）。</summary>
    private void DropStaleTransitEvent(TransitEventPacket packet, int generation, string site)
    {
        _transitQueueDroppedThisWindow++;
    }

    /// <summary>每次看到 transit 流量的世界切换写一行：什么到达了、什么被保留、什么被丢弃。</summary>
    private void ReportTransitQueueAtWorldSwap(int generation, int droppedAtSwap)
    {
        var kept = _pendingTransitEvents.Count;
        var history = _pendingTransitHistory == null ? "none" : "kept";
        if (_transitQueueReceivedThisWindow == 0 && kept == 0 && droppedAtSwap == 0 && _pendingTransitHistory == null)
        {
            // 窗口内没有任何东西到达，也没有任何东西在等待：正常的 raid 启动保持静默。
            return;
        }

        var types = new System.Text.StringBuilder();
        foreach (var entry in _transitQueueTypesThisWindow)
        {
            if (types.Length > 0) types.Append(',');
            types.Append(entry.Key).Append('=').Append(entry.Value);
        }


        _transitQueueReceivedThisWindow = 0;
        _transitQueueDroppedThisWindow = 0;
        _transitQueueTypesThisWindow.Clear();
    }

    /// <summary>为下一个世界的切换行统计一个已入队的 transit 事件。</summary>
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
        // 通用封包在 Execute 之后立即返回池中。
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

    /// <summary>
    /// (2026-10-26 W28) 在客户端上显示 BTR 视图。原生路径从其自己的服务器侧代码中显示它，因此
    /// Fika 客户端能收到位置、旋转和声音控制器，但收不到网格：这辆车有声可闻
    /// 却不可见。<c>BTRView._viewVisible</c> 和 <c>ShowView()</c> 都存在于 interop 表面中
    /// （analysis/_re-btr.txt），因此这里恰好请求一次，且只在视图仍然隐藏时请求。
    /// </summary>
    private static void EnsureBtrViewVisible(BTRView view)
    {
        try
        {
            if (view == null) return;
            if (view._viewVisible) return;
            view.ShowView();
            FikaGlobals.LogInfo("[BtrDiag] view shown by Fika (the native client path never calls ShowView)");
        }
        catch (System.Exception exception)
        {
            FikaGlobals.LogError($"BTR view could not be shown: {exception}");
        }
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
            // 状态更新不得抹掉更早的 StartedEvent。即使生成器已停止运行，
            // 主机仍会为该事件锁定 transit。
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
                    // 它在上一个世界之前就已入队且从未被消费：它无法描述
                    // 现在正在运行的 raid（analysis/transit-reload-sync）。
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
                    // (2026-10-26 W28) 客机"只听见 BTR 声音、看不见车"：BTRView 的 ShowView() 是私有方法，
                    // 只有原生主机路径会调它；客机走 InitClient + SyncViewFromServer，位置/音效都对，但
                    // 网格一直停在隐藏态。车一出现在路上就把它显出来，且只在隐藏时动一次。
                    EnsureBtrViewVisible(btr.BtrView);
                }
                if (_hasBtrState && _appliedBtrGeneration > 0)
                {
                    _readyBtrView.SyncViewFromServer(ref _pendingBtrState);
                    EnsureBtrViewVisible(_readyBtrView);
                    _hasBtrState = false;
                }
            }
            if (_pendingRunddans != null && MyPlayer != null &&
                FikaWorldControllers.Find<Main.ClientClasses.ClientRunddansController>(world.RunddansController) is { } events)
            {
                _pendingRunddans.PlayerRaidId = MyPlayer.RaidId;
                events.Native.State(_pendingRunddans);
                // State() 会更新可见的生成器状态。之后恢复
                // 原生耐久度和绝对游戏日期，这样
                // 第一次 FixedUpdate 就无法用默认值覆盖
                // 一个迟到加入的快照。
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
                        // transit 初始化先于 Runddans 初始化。
                        // 一张有 Runddans 但没有 transit 控制器的地图永远不会
                        // 获得一个，因此这个待处理标志不能一直保持设置。
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
            // 迟到加入可能两次投递同一个快照；在此控制器内重复的快照
            // 必须保留其存活物品和所有者，而不是把它们注册两次。
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
