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
/// Base inventory controller made for Fika
/// </summary>
public class BaseInventoryController : Player.PlayerOwnerInventoryController
{
    public BaseInventoryController(System.IntPtr pointer) : base(pointer) { }


    /// <summary>
    /// Whether strict inventory syncing is active
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
        // SPT 5 still uses the native single-player search operation for
        // local, host and bot inventories.  The old Mono port left this base
        // method as a guard because every concrete controller overrode it;
        // IL2CPP can legitimately dispatch through the base wrapper during
        // item-search setup, so returning the native-compatible operation is
        // required here as well.
        return new SinglePlayerSearchContentOperation(GetAndIncrementNextOperationId(), this,
            PlayerSearchController, Profile, item);
    }

    /// <summary>Applies an authoritative inventory received from the Fika server.</summary>
    internal void ReplaceInventoryFromNetwork(Inventory inventory)
    {
        ReplaceInventory(inventory);
        SyncInventoryToProfile();
    }

    /// <summary>
    /// Keeps the profile object used by SPT's raid-end serializer in step with
    /// the inventory object owned by this controller.  SPT 5's IL2CPP
    /// <c>ReplaceInventory</c> updates the controller backing field only; it
    /// does not write the new object back through <see cref="Profile.Inventory"/>
    /// when a network resync replaces the tree.
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
    /// Whether the native inventory gate is closed
    /// (<c>EFT.InventoryLogic.ItemController.Locked</c>). This is the only flag
    /// that refuses item actions for the whole inventory:
    /// <c>Player.PlayerInventoryController.CheckItemAction</c> returns an error
    /// while it is set, and every drag-and-drop view
    /// (<c>EFT.UI.DragAndDrop.GridView/SlotView.CanDrag/CanAccept</c>) plus
    /// <c>Player.SetItemInHands</c>/<c>SetQuickSlotItem</c> go through
    /// <c>Item.CheckAction</c>. A gate that stays closed therefore makes every
    /// inventory drag and every weapon switch fail while movement keeps working.
    /// The only native writer is <c>MovementContext.StateLocksInventory</c>
    /// (set from <c>MovementState.Enter</c> = true and <c>Exit</c> = false by the
    /// door/approach and stationary states); when that Exit never runs, nothing
    /// else ever clears it.
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
    /// Whether an unfinished item event still marks an item as busy.
    /// <c>ItemController.CheckItemAction</c> refuses an item (and its parents)
    /// while one of these events is registered, and
    /// <c>IsChangingWeapon</c> additionally blocks
    /// <c>Player.SetItemInHands</c>/<c>SetQuickSlotItem</c>.
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
    /// When the native gate last refused an operation the port dispatched. Only
    /// a refused attempt arms the watchdog: a player who never touched the
    /// inventory is never repaired.
    /// </summary>
    internal float LastGateRefusalTime { get; private set; }

    /// <summary>
    /// Records that the native gate refused an action the player asked for.
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

    /// <summary>Whether an action was refused by the gate within the last seconds.</summary>
    internal bool HadGateRefusalWithin(float seconds)
    {
        return LastGateRefusalTime > 0f && Time.time - LastGateRefusalTime <= seconds;
    }

    /// <summary>
    /// Unlocks the inventory the same way the movement state that locked it
    /// would have unlocked it. Only call this while the player is idle: a
    /// running door/approach/stationary interaction owns the lock legitimately.
    /// </summary>
    internal bool ReleaseStuckInventoryLock(string reason)
    {
        if (!IsInventoryGateLocked)
        {
            return false;
        }

        try
        {
            // StateLocksInventory is the native setter the movement states use
            // (Enter => true, Exit => false), so writing false here repeats
            // exactly the unlock the stuck state never performed.
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

    /// <summary>Operations this controller dispatched and has not finished yet.</summary>
    internal virtual int PendingOperationCount => 0;

    /// <summary>
    /// Drops server callback registrations that no dispatched operation owns.
    /// Only the client controller has any; the host answers its own operations.
    /// </summary>
    internal virtual int ReleaseStaleServerCallbacks(string reason) => 0;

    /// <summary>How many server callback registrations no live handler owns.</summary>
    internal virtual int CountStaleServerCallbacks() => 0;

    /// <summary>
    /// Releases an inventory gate that stayed closed although nothing can still
    /// own it. Only the watchdog in <c>CoopGame</c> calls this, and only after a
    /// player attempt followed by an idle player with a closed gate.
    /// <paramref name="escalate"/> additionally drops the item events the native
    /// runtime itself reports as outdated, which is what keeps a single item
    /// busy after an aborted operation.
    /// </summary>
    internal virtual void RepairBlockedInventory(string reason, bool escalate)
    {
        var staleCallbacks = ReleaseStaleServerCallbacks(reason);
        var lockReleased = ReleaseStuckInventoryLock(reason);
        var outdatedEvents = escalate ? ReleaseOutdatedItemEvents(reason) : 0;

        // The inventory lock and the interaction state are set by the same
        // native flows, so release both.
        NativeInteractionRelease.Release(this.Player, "inventory-gate-stuck");

        FikaGlobals.LogWarning($"[InventoryRepair] {ID} - inventory recovery ran (reason={reason}, " +
            $"pendingHandlers={PendingOperationCount}, staleCallbacks={staleCallbacks}, lockReleased={lockReleased}, " +
            $"outdatedEvents={outdatedEvents}, locked={IsInventoryGateLocked}, changingWeapon={IsChangingWeaponState}, " +
            $"state={this.Player?.MovementContext?.CurrentState?.Name})");
    }

    /// <summary>
    /// Removes the item events the native runtime itself considers outdated
    /// (<c>ItemEventArgs.IsOutdated()</c>). A failed or aborted operation whose
    /// Dispose threw leaves its "Begin" event registered, and every later
    /// CheckItemAction for that item is then refused with a busy error.
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
    /// Returns how many bullets should be loaded per tick into the <paramref name="magazine"/>
    /// </summary>
    /// <param name="magazine">The magazine to check the <see cref="Magazine.MaxCount"/> on</param>
    /// <returns>The amount of bullets to load per tick</returns>
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

                // increment loop by the amount loaded per tick instead of single units
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
