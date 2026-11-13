// © 2026 Lacyway All Rights Reserved

using Diz.LanguageExtensions;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.ObservedClasses;

public sealed class ObservedInventoryController : Player.PlayerInventoryController, IOperationHandler
{

    public ObservedInventoryController(System.IntPtr pointer) : base(pointer) { }



    private readonly FikaPlayer _fikaPlayer;

    public override bool HasDiscardLimits
    {
        get
        {
            return false;
        }
    }

    public override IPlayerSearchController PlayerSearchController { get; }

    public ObservedInventoryController(Player player, Profile profile, bool examined, MongoID firstId, ushort firstOperationId, bool aiControl)
        : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedInventoryController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Fika.Core.Main.Utils.NativeConstructor.InvokeBase<Player.PlayerInventoryController>(this, player, profile, examined);
        IdSource = firstId;
        _nextOperationId = firstOperationId;
        PlayerSearchController = new ObservedPlayerSearchController();
        _fikaPlayer = player.TryCast<FikaPlayer>()
            ?? throw new System.InvalidOperationException("Observed inventory controller received a non-Fika player");
    }

    public override void AddDiscardLimits(Item rootItem, Il2CppSystem.Collections.Generic.IEnumerable<ItemsCount> destroyedItems)
    {
        // Do nothing
    }

    public override Il2CppSystem.Collections.Generic.IEnumerable<ItemsCount> GetItemsOverDiscardLimit(Item item)
    {
        return new Il2CppSystem.Collections.Generic.List<ItemsCount>();
    }

    public override bool HasDiscardLimit(Item item, out int limit)
    {
        limit = 0;
        return false;
    }

    public override Option<bool> TryThrowItem(Item item, Callback callback = null, bool silent = false)
    {
        ThrowItem(item, false, callback);
        return true;
    }

    public override Option CheckItemAction(Item item, ItemAddress location)
    {
        if (item.CurrentAddress == null)
        {
            return new Option();
        }
        if (!item.CheckForLockable(out var lockableComponent))
        {
            return new ItemManipulator.ContainerLockedError(lockableComponent);
        }
        if (location != null && !location.Container.ParentItem.CheckForLockable(out lockableComponent))
        {
            return new ItemManipulator.ContainerLockedError(lockableComponent);
        }
        var flag = false;
        if (item.TryCast<CompoundItem>() is { } compoundItem)
        {
            foreach (var item2 in compoundItem.GetAllItems())
            {
                foreach (var geventArgs in ActiveEvents)
                {
                    // this block is redundant during this check and isn't really meant to be used as it triggers a deny if player drags currently equipped item out of a slot
                    /*if (item2 == geventArgs.Item)
                    {
                        FikaGlobals.LogError($"{item2.LocalizedShortName()} was same as queued process: {geventArgs.Item.LocalizedShortName()}");
                        flag = true;
                    }*/
                    if (geventArgs.Location != null && geventArgs.Location.Container.ParentItem == item2)
                    {
#if DEBUG
                        FikaGlobals.LogError($"{item2.LocalizedShortName()} was parentItem of: {geventArgs.Item.LocalizedShortName()}");
#endif
                        flag = true;
                    }
                    if (location != null && geventArgs.Location != null && CheckLocation(item2, location, geventArgs.Item, geventArgs.Location))
                    {
#if DEBUG
                        FikaGlobals.LogError($"{item2.LocalizedShortName()} failed to process on CheckLocation");
#endif
                        flag = true;
                    }
                    if (flag)
                    {
#if DEBUG
                        FikaGlobals.LogError($"Flag hit, gevent was {geventArgs.GetType().Name}");
#endif
                        return new PlayerIsBusyError(item, ParentItem.GetRootItem());
                    }
                }
            }
        }
        foreach (var geventArgs2 in ActiveEvents)
        {
            if (geventArgs2.TryCast<LoadMagazineEventArgs>() is { } geventArgs3 && (geventArgs3.TargetItem == item || geventArgs3.Item == item))
            {
#if DEBUG
                FikaGlobals.LogError($"{item.LocalizedShortName()} was in a GEventArgs7");
#endif
                flag = true;
            }
            if (geventArgs2.TryCast<UnloadMagazineEventArgs>() is { } geventArgs4 && (geventArgs4.FromItem == item || geventArgs4.Item == item || geventArgs4.TargetItem == item))
            {
#if DEBUG
                FikaGlobals.LogError($"{item.LocalizedShortName()} was in a GEventArgs8");
#endif
                flag = true;
            }
            if (geventArgs2.TryCast<AddItemEventArgs>() is { } geventArgs5 && item == geventArgs5.To.Container.ParentItem)
            {
#if DEBUG
                FikaGlobals.LogError($"{item.LocalizedShortName()} was in a GEventArgs2");
#endif
                flag = true;
            }
            if (geventArgs2.TryCast<RemoveItemEventArgs>() is { } geventArgs6)
            {
                if (item.Parent.Container.ParentItem == geventArgs6.Item)
                {
#if DEBUG
                    FikaGlobals.LogError($"{item.LocalizedShortName()} was in a GEventArgs3 and ParentItem was event Item");
#endif
                    flag = true;
                }
                if (Equals(geventArgs6.From, location))
                {
#if DEBUG
                    FikaGlobals.LogError($"{item.LocalizedShortName()} was in a GEventArgs6 and From was same as Location");
#endif
                    flag = true;
                }
            }
            if (geventArgs2.TryCast<InOutHandsProcessEventArgs>() is { } inOutHandsProcess)
            {
                if (item.GetAllParentItemsAndSelf(false).AsManaged().Any(parent => parent == inOutHandsProcess.Item))
                {
#if DEBUG
                    FikaGlobals.LogError($"{item.LocalizedShortName()} failed to pass GetAllParentItemsAndSelf");
#endif
                    flag = true;
                }
                if (location?.Container.ParentItem.GetAllParentItemsAndSelf(false).AsManaged().Any(parent => parent == inOutHandsProcess.Item) == true)
                {
#if DEBUG
                    FikaGlobals.LogError($"{item.LocalizedShortName()} location failed to pass GetAllParentItemsAndSelf");
#endif
                    flag = true;
                }
            }
            if (item == geventArgs2.Item)
            {
#if DEBUG
                FikaGlobals.LogError($"{item.LocalizedShortName()} item was same as GEventArgs2.Item");
#endif
                flag = true;
            }
            if (location != null && geventArgs2.Location != null && CheckLocation(item, location, geventArgs2.Item, geventArgs2.Location))
            {
#if DEBUG
                FikaGlobals.LogError($"{item.LocalizedShortName()} failed to process CheckLocation v2");
#endif
                flag = true;
            }
            if (flag)
            {
                return new PlayerIsBusyError(item, ParentItem.GetRootItem());
            }
        }
        if (!CheckRestrictions(item, location))
        {
            return new Option();
        }
        return new ItemRestrictionsError(item, location);
    }

    public override bool CheckOverLimit(Il2CppSystem.Collections.Generic.IEnumerable<Item> items, ItemAddress to, bool useItemCountInEquipment, out ItemManipulator.CountLimitError error)
    {
        error = null;
        return true;
    }

    public override bool IsLimitedAtAddress(Item item, ItemAddress address, out int limit)
    {
        return IsLimitedAtAddress(item.TemplateId, address, out limit);
    }

    public override bool IsLimitedAtAddress(string templateId, ItemAddress address, out int limit)
    {
        limit = -1;
        return false;
    }

    public override void StrictCheckMagazine(Magazine magazine, bool status, int skill = 0, bool notify = false, bool useOperation = true)
    {
        // Do nothing
    }

    public override void OnAmmoLoadedCall(int count)
    {
        // Do nothing
    }

    public override void OnAmmoUnloadedCall(int count)
    {
        // Do nothing
    }

    public override void OnMagazineCheckCall()
    {
        // Do nothing
    }

    public override bool IsInventoryBlocked()
    {
        return false;
    }

    public override bool CanExecute(EFT.InventoryLogic.Operations.AbstractOperation operation)
    {
        return true;
    }

    public override SearchContentOperation CreateSearchOperation(SearchableItem item)
    {
        // Observed corpses and remote loot use the same native search
        // operation as local/host inventories. Returning null here leaves the
        // IL2CPP operation pipeline without a descriptor, so opening a remote
        // corpse cannot progress to search results or subsequent loot moves.
        return new SinglePlayerSearchContentOperation(GetAndIncrementNextOperationId(), this,
            PlayerSearchController, Profile, item);
    }

    public override void InProcess(ItemController executor, Item item, ItemAddress to, bool succeed, IInventoryOperation operation, Callback callback)
    {
        if (!succeed)
        {
            callback.Succeed();
            return;
        }
        if (!executor.CheckTransferOwners(item, to, out var error))
        {
            callback.Fail(error.ToString());
            return;
        }
        HandleInProcess(item, to, operation, callback);
        _fikaPlayer.StatisticsManager.OnGrabLoot(item);
    }

    private void HandleInProcess(Item item, ItemAddress to, IInventoryOperation operation, Callback callback)
    {
        Player.__c__DisplayClass1637_0 handler = new()
        {
            __4__this = _fikaPlayer,
            callback = callback
        };

        if (!_fikaPlayer.HealthController.IsAlive)
        {
            handler.callback.Succeed();
            return;
        }

        if ((item.Parent != to || operation.TryCast<FoldOperation>() != null)
            && _fikaPlayer.HandsController.CanExecute(operation))
        {
            _fikaPlayer._setInHandsCallback = callback;
            RaiseInOutProcessEvents(new InOutHandsProcessEventArgs(_fikaPlayer.HandsController.Item, CommandStatus.Begin, this));
            _fikaPlayer.HandsController.Execute(operation, (Callback)handler._TrySetInHands_b__2);
            return;
        }

        if (operation.TryCast<FoldOperation>() != null && !_fikaPlayer.HandsController.CanExecute(operation))
        {
            handler.callback.Fail("Can't perform operation");
            return;
        }

        handler.callback.Succeed();
    }

    public override void GetTraderServicesDataFromServer(string traderId)
    {
        // Do nothing
    }

    public void SetNewID(MongoID newId)
    {
        // SPT 5's CurrentId/IDatabaseIdGenerator use native IdSource. A CLR
        // shadow of the old _currentId field never updates that sequence.
        IdSource = newId;
    }

    OperationCreationResult IOperationHandler.CreateOperationFromDescriptor(InventoryOperationDescriptor descriptor)
    {
        UpdateOperationId(descriptor);
        return descriptor.ToInventoryOperation(_fikaPlayer);
    }
}
