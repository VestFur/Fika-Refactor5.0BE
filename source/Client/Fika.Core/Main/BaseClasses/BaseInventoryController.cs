using Fika.Core.Main.Utils;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using EFT.UI;
using Diz.LanguageExtensions;
using Diz.Utils;

namespace Fika.Core.Main.BaseClasses;

/// <summary>
/// 为 Fika 制作的基础物品栏控制器
/// </summary>
public class BaseInventoryController : Player.PlayerOwnerInventoryController
{
    public BaseInventoryController(System.IntPtr pointer) : base(pointer) { }


    /// <summary>
    /// 严格物品栏同步是否启用
    /// </summary>
    public bool StrictSync { get; }

    protected readonly bool _instantLoad;
    protected readonly bool _fastLoad;

    public BaseInventoryController(Player player, Profile profile, bool examined, bool strictSync)
        : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<BaseInventoryController>(), player, profile, examined, strictSync) { }

    protected BaseInventoryController(IntPtr pointer, Player player, Profile profile, bool examined, bool strictSync) : this(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        NativeConstructor.InvokeBase<Player.PlayerOwnerInventoryController>(this, player, profile, examined);
        _instantLoad = FikaPlugin.Instance.Settings.InstantLoad;
        _fastLoad = !_instantLoad && FikaPlugin.Instance.Settings.FastLoad;
        StrictSync = strictSync;
    }

    public override SearchContentOperation CreateSearchOperation(SearchableItem item)
    {
        // SPT 5 对本地、主机与 bot 的物品栏仍然使用原生的单人搜索
        // 操作。旧的 Mono 移植版把这个基类方法留作守卫，因为每个
        // 具体控制器都重写了它；而在物品搜索初始化期间，IL2CPP
        // 可能合法地通过基类包装来派发，
        // 因此在这里同样必须返回与原生兼容的
        // 操作。
        return new SinglePlayerSearchContentOperation(GetAndIncrementNextOperationId(), this,
            PlayerSearchController, Profile, item);
    }

    /// <summary>应用从 Fika 服务器收到的权威物品栏。</summary>
    internal void ReplaceInventoryFromNetwork(Inventory inventory)
    {
        // 重建后的树中的实例都是新的，所以搜索控制器一个都不认识。只有
        // 替换之前已知的那些物品会在它们的新地址上被重新注册：
        // 玩家尚未搜索过的容器不能因一次重新同步而变成已搜索。
        var knownBeforeReplace = CollectKnownItemIds();
        ReplaceInventory(inventory);
        SyncInventoryToProfile();
        RestoreKnownItemIds(knownBeforeReplace);
    }

    /// <summary>
    /// （2026-10-17）当前树已注册为已知的条目 id。在网络替换之前读取，
    /// 以便同样的物品能在重建后的树中被重新注册。
    /// </summary>
    private System.Collections.Generic.HashSet<string> CollectKnownItemIds()
    {
        var known = new System.Collections.Generic.HashSet<string>();
        try
        {
            var search = PlayerSearchController;
            var inventory = Inventory;
            if (search == null || inventory == null)
            {
                return known;
            }

            var active = search.TryCast<EFT.ActiveSearchController>();
            if (active == null)
            {
                return known;
            }

            var items = inventory.GetPlayerItems(EPlayerItems.NonQuestItems);
            if (items == null)
            {
                return known;
            }

            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                try
                {
                    if (active.IsItemKnown(item))
                    {
                        known.Add(item.Id);
                    }
                }
                catch (Exception)
                {
                    // 一个无法读取的物品不得阻止其它物品被保留。
                }
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[InventoryRepair] {ID} - could not read the known items before a resync: {exception.Message}");
        }

        return known;
    }

    /// <summary>
    /// 把网络替换之前已知的物品在新地址上重新注册。
    /// </summary>
    private void RestoreKnownItemIds(System.Collections.Generic.HashSet<string> known)
    {
        if (known == null || known.Count == 0)
        {
            return;
        }

        try
        {
            var search = PlayerSearchController;
            var inventory = Inventory;
            if (search == null || inventory == null)
            {
                return;
            }

            var items = inventory.GetPlayerItems(EPlayerItems.NonQuestItems);
            if (items == null)
            {
                return;
            }

            foreach (var item in items)
            {
                if (item != null && known.Contains(item.Id))
                {
                    search.SetItemAsKnown(item, false);
                }
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[InventoryRepair] {ID} - could not restore the known items after a resync: {exception.Message}");
        }
    }

    /// <summary>
    /// 让 SPT 战局结束序列化器所使用的 profile 对象与
    /// 本控制器所拥有的物品栏对象保持同步。SPT 5 的 IL2CPP
    /// <c>ReplaceInventory</c> 只更新控制器背后的字段；当一次网络
    /// 重新同步替换了整棵树时，它不会把新对象写回
    /// <see cref="Profile.Inventory"/>。
    /// </summary>
    internal void SyncInventoryToProfile()
    {
        try
        {
            var profile = Profile;
            var inventory = Inventory;
            if (profile == null || inventory == null)
                return;

            if (profile.Inventory?.Pointer != inventory.Pointer)
                profile.Inventory = inventory;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"Failed to synchronize controller inventory to profile: {exception}");
        }
    }


    /// <summary>
    /// 原生物品栏闸门是否处于关闭状态
    /// （<c>EFT.InventoryLogic.ItemController.Locked</c>）。这是唯一一个会拒绝
    /// 整个物品栏的物品动作的标志：
    /// 当它被置位时，
    /// <c>Player.PlayerInventoryController.CheckItemAction</c> 会返回错误，并且每个拖放视图
    /// （<c>EFT.UI.DragAndDrop.GridView/SlotView.CanDrag/CanAccept</c>）以及
    /// <c>Player.SetItemInHands</c>/<c>SetQuickSlotItem</c> 都会经过
    /// <c>Item.CheckAction</c>。因此一道一直关闭的闸门会让每一次
    /// 物品栏拖拽和每一次换武器都失败，而移动却仍然正常。
    /// 唯一的原生写入者是 <c>MovementContext.StateLocksInventory</c>
    /// （由门/靠近与静止状态把 <c>MovementState.Enter</c> 置为 true、
    /// <c>Exit</c> 置为 false）；当那次 Exit 从未执行时，
    /// 再没有别的东西会清除它。
    /// </summary>
    internal bool IsInventoryGateLocked
    {
        get
        {
            try
            {
                return Locked;
            }
            catch (Exception exception)
            {
                FikaGlobals.LogWarning($"[InventoryRepair] {ID} - reading the native inventory lock failed: {exception.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// 未完成的物品事件是否仍把某个物品标记为忙碌。
    /// 当其中一个事件仍被注册时，<c>ItemController.CheckItemAction</c>
    /// 会拒绝该物品（及其父级），而
    /// <c>IsChangingWeapon</c> 还会额外阻止
    /// <c>Player.SetItemInHands</c>/<c>SetQuickSlotItem</c>。
    /// </summary>
    internal bool IsChangingWeaponState
    {
        get
        {
            try
            {
                return IsChangingWeapon;
            }
            catch (Exception exception)
            {
                FikaGlobals.LogWarning($"[InventoryRepair] {ID} - reading IsChangingWeapon failed: {exception.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// 原生闸门最后一次拒绝本移植版所派发操作的时间。只有
    /// 被拒绝的一次尝试才会武装看门狗：从未触碰物品栏的玩家
    /// 永远不会被修复。
    /// </summary>
    internal float LastGateRefusalTime { get; private set; }

    /// <summary>
    /// 记录原生闸门拒绝了玩家所请求的一个动作。
    /// </summary>
    internal void NoteGateRefusal(string detail)
    {
        var repeated = LastGateRefusalTime > 0f && Time.time - LastGateRefusalTime <= 2f;
        LastGateRefusalTime = Time.time;
        if (!repeated)
        {
            FikaGlobals.LogWarning($"[InventoryRepair] {ID} - the native inventory gate refused an action ({detail}); " +
                "watching whether it stays closed");
        }
    }

    /// <summary>最近几秒内是否有动作被该闸门拒绝过。</summary>
    internal bool HadGateRefusalWithin(float seconds)
    {
        return LastGateRefusalTime > 0f && Time.time - LastGateRefusalTime <= seconds;
    }

    /// <summary>
    /// 以与当初锁定它的移动状态会采用的方式解锁物品栏。
    /// 只在玩家空闲时调用它：正在进行的门/靠近/静止交互
    /// 是合法地持有该锁的。
    /// </summary>
    internal bool ReleaseStuckInventoryLock(string reason)
    {
        if (!IsInventoryGateLocked)
        {
            return false;
        }

        try
        {
            // StateLocksInventory 是移动状态所用的原生设值器
            // （Enter => true，Exit => false），因此这里写入 false 正好重复了
            // 那个卡住的状态从未执行过的解锁。
            var movementContext = this.Player?.MovementContext;
            if (movementContext != null)
            {
                movementContext.StateLocksInventory = false;
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[InventoryRepair] {ID} - clearing the lock through the movement state failed: {exception.Message}");
        }

        var unlocked = false;
        try
        {
            if (Locked)
            {
                Locked = false;
            }
            unlocked = !Locked;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[InventoryRepair] {ID} - clearing the item controller lock failed: {exception.Message}");
        }

        FikaGlobals.LogWarning($"[InventoryRepair] {ID} - released the stuck inventory lock " +
            $"(reason={reason}, unlocked={unlocked})");
        return unlocked;
    }

    /// <summary>本控制器已派发但尚未完成的操作。</summary>
    internal virtual int PendingOperationCount => 0;

    /// <summary>
    /// 丢弃没有任何已派发操作所拥有的服务器回调注册。
    /// 只有客户端控制器会有这类注册；主机自己应答自己的操作。
    /// </summary>
    internal virtual int ReleaseStaleServerCallbacks(string reason) => 0;

    /// <summary>有多少服务器回调注册没有活跃处理器所拥有。</summary>
    internal virtual int CountStaleServerCallbacks() => 0;

    /// <summary>
    /// 释放一道在已不可能有谁拥有它时却仍然关闭的物品栏闸门。
    /// 只有 <c>CoopGame</c> 里的看门狗会调用它，且只在一次玩家尝试
    /// 之后、接着出现一个闸门关闭的空闲玩家时调用。
    /// <paramref name="escalate"/> 还会额外丢弃原生运行时自己报告的
    /// 已过期物品事件，正是这些事件让一件物品在一次被中止的操作
    /// 之后一直处于忙碌状态。
    /// </summary>
    internal virtual void RepairBlockedInventory(string reason, bool escalate)
    {
        var staleCallbacks = ReleaseStaleServerCallbacks(reason);
        var lockReleased = ReleaseStuckInventoryLock(reason);
        var outdatedEvents = escalate ? ReleaseOutdatedItemEvents(reason) : 0;

        // 物品栏锁与交互状态由同一批原生流程设置，
        // 因此两者都要释放。
        NativeInteractionRelease.Release(this.Player, "inventory-gate-stuck");

        FikaGlobals.LogWarning($"[InventoryRepair] {ID} - inventory recovery ran (reason={reason}, " +
            $"pendingHandlers={PendingOperationCount}, staleCallbacks={staleCallbacks}, lockReleased={lockReleased}, " +
            $"outdatedEvents={outdatedEvents}, locked={IsInventoryGateLocked}, changingWeapon={IsChangingWeaponState}, " +
            $"state={this.Player?.MovementContext?.CurrentState?.Name})");
    }

    /// <summary>
    /// 移除原生运行时自己认为已过期的物品事件
    /// （<c>ItemEventArgs.IsOutdated()</c>）。一次失败或被中止的操作如果其
    /// Dispose 抛出过异常，就会留下它已注册的 "Begin" 事件，此后针对该物品的
    /// 每一次 CheckItemAction 都会以忙碌错误被拒绝。
    /// </summary>
    internal int ReleaseOutdatedItemEvents(string reason)
    {
        Il2CppSystem.Collections.Generic.List<ItemEventArgs> activeEvents;
        try
        {
            activeEvents = ActiveEvents;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[InventoryRepair] {ID} - reading the active item events failed: {exception.Message}");
            return 0;
        }

        if (activeEvents == null)
        {
            return 0;
        }

        System.Collections.Generic.List<ItemEventArgs> outdated = null;
        var count = activeEvents.Count;
        for (var i = 0; i < count; i++)
        {
            var args = activeEvents[i];
            if (args == null)
            {
                continue;
            }
            try
            {
                if (args.IsOutdated())
                {
                    (outdated ??= []).Add(args);
                }
            }
            catch (Exception exception)
            {
                FikaGlobals.LogWarning($"[InventoryRepair] {ID} - checking an item event for staleness failed: {exception.Message}");
            }
        }

        if (outdated == null)
        {
            return 0;
        }

        var removed = 0;
        foreach (var args in outdated)
        {
            try
            {
                var itemId = args.Item == null ? "null" : args.Item.Id.ToString();
                var eventName = args.GetType().Name;
                var status = args.Status;
                RemoveActiveEvent(args);
                removed++;
                FikaGlobals.LogWarning($"[InventoryRepair] {ID} - dropped the outdated item event {eventName} " +
                    $"(item={itemId}, status={status}, reason={reason})");
            }
            catch (Exception exception)
            {
                FikaGlobals.LogError($"[InventoryRepair] {ID} - dropping an outdated item event failed: {exception}");
            }
        }
        return removed;
    }

    public override Il2CppSystem.Threading.Tasks.Task<IResult> LoadMagazine(Ammo sourceAmmo, Magazine magazine, int loadCount, bool ignoreRestrictions)
    {
        if (_instantLoad)
        {
            if (Singleton<GUISounds>.Instantiated)
            {
                Singleton<GUISounds>.Instance.PlayUILoadSound();
            }

            var gstruct = ignoreRestrictions
                ? magazine.ApplyWithoutRestrictions(this, sourceAmmo, loadCount, true)
                : magazine.Apply(this, sourceAmmo, loadCount, true);

            return TryRunNetworkTransaction(gstruct, null);
        }

        if (_fastLoad)
        {
            return ToNativeTask(MainThreadDispatcher.RunAsync(() => QuickLoadMagazine(sourceAmmo, magazine, loadCount, ignoreRestrictions)));
        }

        return global::Fika.Core.Main.Utils.NativeBaseCalls.Call0(this, sourceAmmo, magazine, loadCount, ignoreRestrictions);
    }

    private async Task<IResult> QuickLoadMagazine(Ammo sourceAmmo, Magazine magazine, int loadCount, bool ignoreRestrictions)
    {
        if (loadCount <= 0)
        {
            return new FailedResult("Can not load 0 bullets.", 0);
        }

        StopProcesses();

        var speedPercentage = 100f - Profile.Skills.MagDrillsLoadSpeed + magazine.LoadUnloadModifier;
        var finalLoadSpeed = Singleton<GlobalConfiguration>.Instance.BaseLoadTime * speedPercentage / 100f;
        var loadPerTick = GetLoadPerTick(magazine);

        var operationResult = ignoreRestrictions
            ? magazine.ApplyWithoutRestrictions(this, sourceAmmo, loadPerTick, true)
            : magazine.Apply(this, sourceAmmo, loadPerTick, true);

        if (operationResult.Failed || !CanExecute(operationResult.Value))
        {
            return operationResult.ToResult();
        }

        var readinessResult = await WaitForProcess().AsManaged();
        if (readinessResult.Failed)
        {
            return readinessResult;
        }

        var native = new LoadMagazineProcess(this, magazine, sourceAmmo, loadCount,
            Profile.Skills.MagDrillsLoadProgression, finalLoadSpeed);
        var process = new CustomAmmoLoader(this, magazine, sourceAmmo, loadCount,
            Profile.Skills.MagDrillsLoadProgression, finalLoadSpeed, loadPerTick, ignoreRestrictions);
        return await RunBatchProcess(native, process);
    }

    public override Il2CppSystem.Threading.Tasks.Task<IResult> UnloadMagazine(Magazine magazine, bool equipmentBlocked)
    {
        if (_instantLoad)
        {
            return UnloadAmmoInstantly(magazine, equipmentBlocked);
        }

        if (_fastLoad)
        {
            return ToNativeTask(MainThreadDispatcher.RunAsync(() => QuickUnloadMagazine(magazine)));
        }

        return global::Fika.Core.Main.Utils.NativeBaseCalls.Call1(this, magazine, equipmentBlocked);
    }

    private async Task<IResult> QuickUnloadMagazine(Magazine magazine)
    {
        StopProcesses();
        var unloadSpeed = 100f - Profile.Skills.MagDrillsUnloadSpeed + magazine.LoadUnloadModifier;
        var unloadOneAmmoSpeed = Singleton<GlobalConfiguration>.Instance.BaseUnloadTime * unloadSpeed / 100f;
        var unloadPerTick = GetLoadPerTick(magazine);

        var awaitClear = await WaitForProcess().AsManaged();
        if (awaitClear.Failed)
        {
            return awaitClear;
        }

        var native = new UnloadMagazineProcess(this, magazine, unloadOneAmmoSpeed, Profile.Skills.MagDrillsLoadProgression);
        var process = new CustomAmmoUnloader(this, magazine, unloadPerTick, unloadOneAmmoSpeed, Profile.Skills.MagDrillsLoadProgression);
        return await RunBatchProcess(native, process);
    }

    /// <summary>
    /// 返回每个 tick 应向 <paramref name="magazine"/> 中装入多少发子弹
    /// </summary>
    /// <param name="magazine">要检查其 <see cref="Magazine.MaxCount"/> 的弹匣</param>
    /// <returns>每个 tick 要装入的子弹数量</returns>
    private static int GetLoadPerTick(Magazine magazine)
    {
        var maxCount = magazine.MaxCount;
        if (maxCount <= 5)
        {
            return 1;
        }

        if (maxCount == 10)
        {
            return 2;
        }

        if (maxCount < 20)
        {
            return 3;
        }

        return 5;
    }

    public override IPlayerSearchController PlayerSearchController { get; }

    private static Il2CppSystem.Threading.Tasks.Task<IResult> ToNativeTask(Task<IResult> task) => NativeTask.FromManaged(task);

    private async Task<IResult> RunBatchProcess(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase native, IFikaMagazineProcess process)
    {
        var handle = native.Cast<IMagazineLoadingProcess>();
        FikaMagazineProcesses.Add(native, process);
        _loadProcess = handle;
        try { return await process.Start(); }
        finally
        {
            FikaMagazineProcesses.Remove(native);
            if (_loadProcess?.Pointer == handle.Pointer) _loadProcess = null;
        }
    }
    private sealed class CustomAmmoLoader : IFikaMagazineProcess
    {
        private readonly InventoryController _inventoryController;
        private readonly Magazine _magazine;
        private readonly Ammo _sourceAmmo;
        private readonly int _totalLoadCount;
        private readonly bool _isElite;
        private readonly float _baseLoadSpeed;
        private readonly int _loadPerTick;
        private readonly bool _ignoreRestrictions;

        private readonly IItemOwner _magazineOwner;
        private readonly IItemOwner _ammoOwner;

        private CancellationTokenSource _cts;
        private float _currentLoadSpeed;

        public bool IsCancelled => _cts?.IsCancellationRequested != false;

        public CustomAmmoLoader(InventoryController inventoryController, Magazine magazine, Ammo sourceAmmo,
            int count, bool elite, float loadOneAmmoSpeed, int loadPerTick, bool ignoreRestrictions)
        {
            _inventoryController = inventoryController;
            _magazine = magazine;
            _sourceAmmo = sourceAmmo;
            _totalLoadCount = count;
            _isElite = elite;
            _baseLoadSpeed = loadOneAmmoSpeed;
            _currentLoadSpeed = loadOneAmmoSpeed;

            _magazineOwner = _magazine.Parent.GetOwner();
            _ammoOwner = _sourceAmmo.Parent.GetOwner();
            _loadPerTick = loadPerTick;
            _ignoreRestrictions = ignoreRestrictions;
        }

        private Task<IResult> _execution;

        public Task<IResult> Start() => _execution ??= RunAsync();

        private async Task<IResult> RunAsync()
        {
            _cts = new CancellationTokenSource();
            var success = false;
            try
            {
                RaiseEvents(CommandStatus.Begin);
                var result = await DoLoadLoopAsync();
                success = result.Succeed;
                return result;
            }
            catch (OperationCanceledException) when (IsCancelled)
            {
                success = true;
                return SuccessfulResult.New;
            }
            finally
            {
                Proceed(success);
                _cts.Dispose();
                _cts = null;
            }
        }

        public void Proceed(bool success = true)
        {
            if (IsCancelled) return;
            _cts.Cancel();
            RaiseEvents(success ? CommandStatus.Succeed : CommandStatus.Failed);
            RefreshIcons(false);
        }
        public void TryProceedForItem(Item item)
        {
            if (_magazine == item || _sourceAmmo == item)
            {
                Proceed(true);
            }
        }

        public void RaiseEvents(CommandStatus status)
        {
            var loadCount = Mathf.CeilToInt((float)_totalLoadCount / _loadPerTick);
            var geventArgs = new LoadMagazineEventArgs(_sourceAmmo, _magazine,
                loadCount, _baseLoadSpeed, status, _inventoryController);
            _magazineOwner.RaiseLoadMagazineEvent(geventArgs);

            if (_magazineOwner != _inventoryController)
            {
                _inventoryController.RaiseLoadMagazineEvent(geventArgs);
            }

            if (_ammoOwner != _magazineOwner)
            {
                _ammoOwner.RaiseLoadMagazineEvent(geventArgs);
            }
        }

        public async Task<IResult> DoLoadLoopAsync()
        {
            var loadedCount = 0;

            while (loadedCount < _totalLoadCount)
            {
                await Task.Delay(Mathf.CeilToInt(_currentLoadSpeed * 1000f), _cts.Token);

                if (IsCancelled)
                {
                    break;
                }

                if (_isElite)
                {
                    var progressModifier = Singleton<GlobalConfiguration>.Instance.LoadTimeSpeedProgress;
                    _currentLoadSpeed = Mathf.Clamp(_currentLoadSpeed - (_baseLoadSpeed * progressModifier / 100f), _baseLoadSpeed * 40f / 100f, 10f);
                }

                var count = Math.Min(_loadPerTick, _totalLoadCount - loadedCount);
                var gstruct = _ignoreRestrictions
                    ? _magazine.ApplyWithoutRestrictions(_inventoryController, _sourceAmmo, count, true)
                    : _magazine.Apply(_inventoryController, _sourceAmmo, count, true);

                if (gstruct.Failed)
                {
                    return gstruct.ToResult();
                }

                var operation = _inventoryController.ConvertOperationResultToOperation(gstruct.Value);
                var executionSource = new TaskCompletionSource<IResult>();

                _inventoryController.Execute(operation, (Callback)executionSource.SetResult);

                var operationResult = await executionSource.Task;

                if (operationResult.Failed)
                {
                    return operationResult;
                }

                PlayLoadSound();
                RefreshIcons(loadedCount == _totalLoadCount - 1);

                if (IsCancelled)
                {
                    break;
                }

                // 循环按每 tick 装入的数量递增，而不是按单个单位递增
                loadedCount += count;
            }
            return SuccessfulResult.New;
        }

        public void RefreshIcons(bool refreshIcon = false)
        {
            if (_sourceAmmo.CurrentAddress != null)
            {
                _sourceAmmo.RaiseRefreshEvent(refreshIcon, true);
            }
            _magazine.RaiseRefreshEvent(refreshIcon, true);
        }

        public void PlayLoadSound()
        {
            if (!Singleton<GUISounds>.Instantiated)
            {
                return;
            }
            Singleton<GUISounds>.Instance.PlayUILoadSound();
        }
    }

    private sealed class CustomAmmoUnloader : IFikaMagazineProcess
    {
        private readonly BaseInventoryController _inventoryController;
        private readonly Magazine _magazine;
        private readonly int _unloadPerTick;
        private readonly float _baseUnloadSpeed;
        private readonly bool _isElite;
        private readonly int _totalAmmoCount;

        private float _currentUnloadSpeed;
        private int _remainingAmmoCount;
        private CancellationTokenSource _cts;
        private Item _currentAmmoItem;
        private Item _targetItem;

        public bool IsCancelled => _cts?.IsCancellationRequested != false;

        public CustomAmmoUnloader(BaseInventoryController baseInventoryController, Magazine magazine, int unloadPerTick, float unloadOneAmmoSpeed, bool isElite)
        {
            _inventoryController = baseInventoryController;
            _magazine = magazine;
            _unloadPerTick = unloadPerTick;
            _baseUnloadSpeed = unloadOneAmmoSpeed;
            _isElite = isElite;

            _currentUnloadSpeed = unloadOneAmmoSpeed;
            foreach (var cartridge in magazine.Cartridges.Items) _totalAmmoCount += cartridge.StackObjectsCount;
            _remainingAmmoCount = _totalAmmoCount;
        }

        private Task<IResult> _execution;

        public Task<IResult> Start() => _execution ??= RunAsync();

        private async Task<IResult> RunAsync()
        {
            if (_remainingAmmoCount == 0) return new AmmoContainerIsEmptyError(_magazine).ToResult();
            _cts = new CancellationTokenSource();
            var success = false;
            try
            {
                var result = await DoUnloadLoopAsync();
                success = result.Succeed;
                return result;
            }
            catch (OperationCanceledException) when (IsCancelled)
            {
                success = true;
                return SuccessfulResult.New;
            }
            finally
            {
                Proceed(success);
                _cts.Dispose();
                _cts = null;
            }
        }

        public void Proceed(bool success)
        {
            if (IsCancelled) return;
            _cts.Cancel();
            if (_currentAmmoItem != null)
                RaiseUnloadEvent(success ? CommandStatus.Succeed : CommandStatus.Failed);
        }
        public void TryProceedForItem(Item item)
        {
            if (_magazine == item || _currentAmmoItem == item || _targetItem == item)
            {
                Proceed(true);
            }
        }

        private void PlayUnloadSound()
        {
            if (!Singleton<GUISounds>.Instantiated)
            {
                return;
            }
            Singleton<GUISounds>.Instance.PlayUIUnloadSound();
        }

        private async Task<IResult> DoUnloadLoopAsync()
        {
            var delayTask = GetDelayTask();

            while (!IsCancelled)
            {
                Item lastCartridge = null;
                foreach (var cartridge in _magazine.Cartridges.Items) lastCartridge = cartridge;
                var ammoItem = lastCartridge?.TryCast<Ammo>();
                if (ammoItem == null)
                {
                    break;
                }

                var equipment = _inventoryController.Inventory.Equipment?.TryCast<CompoundItem>();
                if (equipment == null) return new FailedResult("Equipment was not a CompoundItem", 0);
                var findPlaceResult = ItemManipulator.QuickFindAppropriatePlace(ammoItem, _inventoryController,
                    equipment.ToEnumerable(),
                    ItemManipulator.EMoveItemOrder.UnloadAmmo, true);

                if (findPlaceResult.Failed)
                {
                    return findPlaceResult.ToResult();
                }

                ItemAddress targetAddress = null;
                Item targetItem = null;
                var interactionValue = findPlaceResult.Value;

                if (interactionValue is IToEmptyAddressResult moveOperation)
                {
                    targetAddress = moveOperation.To;
                }
                else if (interactionValue is ITransferOrMergeResult mergeOperation)
                {
                    targetItem = mergeOperation.TargetItem;
                }

                if (targetAddress == null && targetItem == null)
                {
                    break;
                }

                if (_currentAmmoItem != ammoItem || targetItem != _targetItem)
                {
                    if (_currentAmmoItem != null)
                    {
                        RaiseUnloadEvent(CommandStatus.Succeed);
                    }
                    _currentAmmoItem = ammoItem;
                    _targetItem = targetItem;
                    RaiseUnloadEvent(CommandStatus.Begin);
                }

                await delayTask;

                if (IsCancelled)
                {
                    break;
                }

                delayTask = GetDelayTask();

                if (_isElite)
                {
                    var speedModifier = _baseUnloadSpeed * Singleton<GlobalConfiguration>.Instance.LoadTimeSpeedProgress / 100f;
                    _currentUnloadSpeed = Mathf.Clamp(_currentUnloadSpeed - speedModifier, _baseUnloadSpeed * 40f / 100f, 10f);
                }

                var batchSize = Math.Min(_unloadPerTick, Math.Min(_remainingAmmoCount, _currentAmmoItem.StackObjectsCount));
                var applyResult = (_targetItem != null)
                    ? Ammo.ApplyToAmmo(_currentAmmoItem, _targetItem, batchSize, _inventoryController, true)
                    : Ammo.ApplyToAddress(_currentAmmoItem, targetAddress, batchSize, _inventoryController, true);

                if (applyResult.Failed)
                {
                    return applyResult.ToResult();
                }

                IUnloadMagOperationResult operationResult = new UnloadMagOperationResult(applyResult.Value);
                var operation = _inventoryController.ConvertOperationResultToOperation(operationResult)?.TryCast<UnloadMagOperation>()
                    ?? throw new InvalidOperationException("Unload magazine operation could not be converted to the native operation type");

                var executionTcs = new TaskCompletionSource<IResult>();

                _inventoryController.Execute(operation, (Callback)executionTcs.SetResult);

                var executionResult = await executionTcs.Task;
                if (executionResult.Failed)
                {
                    return executionResult;
                }

                _remainingAmmoCount -= batchSize;
                _currentAmmoItem.RaiseRefreshEvent(false, true);
                _magazine.RaiseRefreshEvent(_remainingAmmoCount == 0, true);
                _targetItem?.RaiseRefreshEvent(false, true);

                PlayUnloadSound();
            }

            return SuccessfulResult.New;
        }

        private void RaiseUnloadEvent(CommandStatus status)
        {
            var owner = _magazine.Parent.GetOwner();
            var unloadCount = Mathf.CeilToInt((float)_totalAmmoCount / _unloadPerTick);

            var eventArgs = new UnloadMagazineEventArgs(_currentAmmoItem, _targetItem, _magazine, _totalAmmoCount - _remainingAmmoCount, unloadCount,
                                            _baseUnloadSpeed, status, _inventoryController);

            owner.RaiseUnloadMagazineEvent(eventArgs);
            if (owner != _inventoryController)
            {
                _inventoryController.RaiseUnloadMagazineEvent(eventArgs);
            }
        }

        private Task GetDelayTask()
        {
            return Task.Delay(Mathf.CeilToInt(_currentUnloadSpeed * 1000f), _cts.Token);
        }
    }
}
