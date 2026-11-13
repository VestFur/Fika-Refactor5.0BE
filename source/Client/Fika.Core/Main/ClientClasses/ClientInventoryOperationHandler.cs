using EFT.InventoryLogic.Operations;
using System;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.Communication;
using static Fika.Core.Main.ClientClasses.ClientInventoryController;

namespace Fika.Core.Main.ClientClasses;

public sealed class ClientInventoryOperationHandler : IDisposable
{
    public ClientInventoryController InventoryController;
    public EFT.InventoryLogic.Operations.AbstractOperation Operation;
    public Callback Callback;

    public IResult OperationResult;
    public ServerOperationStatus ServerStatus;

    public Callback ExecuteResultDelegate;
    public Callback HandleResultDelegate;
    public Action<ServerOperationStatus> ServerStatusDelegate;
    public ushort CallbackId { get; private set; }
    private bool _awaitingInventoryResync;
    private bool _inventoryResyncApplied;
    private Inventory _pendingAuthoritativeInventory;
    private uint _generation;
    private bool _active;
    private bool _receivedServerStatus;
    private bool _completed;

    /// <summary>Whether this pooled handler is still driving an operation.</summary>
    internal bool IsActive => _active;

    /// <summary>Whether the server has answered with any status yet.</summary>
    internal bool ReceivedServerStatus => _receivedServerStatus;

    /// <summary>
    /// Completes the operation with a local failure when the server never
    /// answered. Keeps a lost acknowledgement from locking the player in the
    /// interaction state forever.
    /// </summary>
    internal void FailWithoutServerStatus(string reason) => FailLocally(reason);

    public void Set(ClientInventoryController inventoryController, EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
        InventoryController = inventoryController;
        Operation = operation;
        Callback = callback;
        CallbackId = operation.Id;
        _active = true;
        _receivedServerStatus = false;
        _completed = false;
        var generation = unchecked(++_generation);
        ExecuteResultDelegate = (Callback)(result => ExecuteResult(generation, result));
        HandleResultDelegate = (Callback)(result => HandleResult(generation, result));
        ServerStatusDelegate = status => ReceiveStatusFromServer(generation, status);
    }

    public static ClientInventoryOperationHandler CreateInstance()
    {
        return new ClientInventoryOperationHandler();
    }

    private ClientInventoryOperationHandler()
    {
    }

    private void ReceiveStatusFromServer(uint generation, ServerOperationStatus serverStatus)
    {
        if (!_active || generation != _generation) return;
        // Reliable delivery may repeat a terminal status during reconnect.
        // Never execute the same native operation again after it has finished.
        if (_receivedServerStatus)
        {
            if (ServerStatus.Status is EOperationStatus.Succeeded or EOperationStatus.Failed) return;
            if (serverStatus.Status == EOperationStatus.Started && ServerStatus.Status == EOperationStatus.Started) return;
        }
        _receivedServerStatus = true;
        ServerStatus = serverStatus;
        switch (serverStatus.Status)
        {
            case EOperationStatus.Started:
                // A throwing native execution must never leave the player locked in
                // the interaction state with a callback that never fires. Fail the
                // operation locally instead and let the caller release its state.
                try
                {
                    Operation.ExecuteWithoutDispose(ExecuteResultDelegate);
                }
                catch (Exception exception)
                {
                    FikaGlobals.LogError($"{InventoryController?.ID} - Client operation threw during local execution: " +
                        $"{Operation?.Id} - {Operation}\r\nError: {exception}");
                    FailLocally(exception.Message);
                }
                return;
            case EOperationStatus.Succeeded:
                if (Operation.TryCast<TransferExfiltrationPaymentOperation>() is { } paidOperation)
                {
                    ConfirmCarExtractPayment(paidOperation);
                }
                // [QuestOp] A quest operation is normally executed locally and never
                // answered by the host, so this line only appears if one was relayed.
                if (QuestOperations.TryDescribe(Operation, out var appliedQuestDetail))
                {
                    FikaGlobals.LogInfo($"[QuestOp] host applied {Operation.GetIl2CppType().Name} ({appliedQuestDetail})");
                }
                InventoryController?.SyncInventoryToProfile();
                    HandleResultDelegate.Invoke(SuccessfulResult.New);
                return;
            case EOperationStatus.Failed:
                FikaGlobals.LogError($"{InventoryController.ID} - Client operation rejected by server: {Operation.Id} - {LogSafe.Ref(Operation)}\r\nReason: {serverStatus.Error}");
                // [CarExtract] The paid exfiltration point interaction shows its
                // "handed over" message from TransferExitItem's return value, long
                // before the host answers. When the host rejects the payment that
                // prediction has to be corrected, otherwise the player believes the
                // driver was paid while the money is still in the inventory.
                if (Operation.TryCast<TransferExfiltrationPaymentOperation>() is { } rejectedPayment)
                {
                    var rejectedPoint = rejectedPayment._result?.PointName;
                    FikaGlobals.LogError($"[CarExtract] the host rejected the payment for '{rejectedPoint}' " +
                        $"(operation {Operation.Id}): {serverStatus.Error}");
                    FikaNotifications.DisplayMessageNotification(
                        $"[CarExtract] The host rejected the payment for '{rejectedPoint}': {serverStatus.Error}",
                        iconType: ENotificationIconType.Alert);
                }
                if (QuestOperations.TryDescribe(Operation, out var rejectedQuestDetail))
                {
                    FikaGlobals.LogError($"[QuestOp] host rejected {Operation.GetIl2CppType().Name} " +
                        $"({rejectedQuestDetail}): {serverStatus.Error}");
                }
                ServerStatus = serverStatus;
                // Terminate may invoke HandleResult synchronously. Mark the
                // resync first so that callback cannot dispose and return
                // this handler to the pool before the snapshot arrives.
                _awaitingInventoryResync = true;
                if (Operation.Status.InProgress()
                    && Operation.TryCast<IServerDependentOperation>() is { } serverDependentOperation)
                {
                    serverDependentOperation.Terminate();
                }
                if (_pendingAuthoritativeInventory != null)
                {
                    var authoritativeInventory = _pendingAuthoritativeInventory;
                    _pendingAuthoritativeInventory = null;
                    ApplyAuthoritativeInventory(authoritativeInventory);
                }
                return;
            default:
                FikaGlobals.LogError("ReceiveStatusFromServer: Status was missing?");
                break;
        }
    }

    /// <summary>
    /// [CarExtract] The host confirmed the payment, so this client has to report
    /// its own player as queued on the exfiltration point. Executing the same
    /// operation locally normally does that; when that execution was refused or
    /// aborted the point stays unmet for the payer, which shows the countdown but
    /// never extracts, so the native handover handling is replayed here.
    /// </summary>
    private void ConfirmCarExtractPayment(TransferExfiltrationPaymentOperation payment)
    {
        try
        {
            var pointName = payment._result?.PointName;
            var player = InventoryController?.Player;
            if (string.IsNullOrEmpty(pointName) || player == null)
            {
                return;
            }

            var point = FikaExfilManager.FindExfiltrationPoint(pointName);
            if (point == null)
            {
                FikaGlobals.LogError($"[CarExtract] the host confirmed a payment for '{pointName}' but this client " +
                    "has no such exfiltration point");
                return;
            }

            // The host took the money from its own copy of this inventory. When the
            // local prediction of the same operation did not apply, the two sides
            // disagree about the stack until the next inventory resync, which only
            // happens on a rejection - so at least make it visible.
            var localStatus = payment.Status;
            if (localStatus.Finished() && localStatus != EOperationStatus.Succeeded)
            {
                FikaGlobals.LogError($"[CarExtract] the local execution of the payment for '{pointName}' ended as " +
                    $"{localStatus} while the host succeeded; the money stack stays out of sync until the next resync");
            }

            if (FikaExfilManager.HasPlayerPaid(point, player))
            {
                FikaGlobals.LogInfo($"[CarExtract] payment confirmed by the host: point '{pointName}' status " +
                    $"{point.Status}, queued {point.QueuedPlayers?.Count ?? 0}");
                return;
            }

            FikaGlobals.LogWarning($"[CarExtract] the host confirmed the payment for '{pointName}' but the local " +
                $"point had not queued {player.ProfileId}; applying the handover locally");
            point.OnItemTransferred(player);
            FikaGlobals.LogInfo($"[CarExtract] local point '{pointName}' is now status {point.Status}, " +
                $"queued {point.QueuedPlayers?.Count ?? 0}");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[CarExtract] failed to confirm the payment locally: {exception}");
        }
    }

    /// <summary>
    /// Completes a still-pending operation with a local failure so the caller's
    /// callback always runs. Used when the native execution throws before the
    /// server can report a terminal status.
    /// </summary>
    private void FailLocally(string error)
    {
        if (!_active || _completed) return;
        _completed = true;
        try
        {
            if (Operation != null && Operation.Status.InProgress()
                && Operation.TryCast<IServerDependentOperation>() is { } serverDependentOperation)
            {
                serverDependentOperation.Terminate();
            }
        }
        catch (Exception terminateException)
        {
                    }

                ServerStatus = new ServerOperationStatus(EOperationStatus.Failed, error);
        OperationResult = new FailedResult(error ?? "The inventory operation failed locally.");
        // Ground loot pickups run inside the native PickUpState/interaction
        // queue. A failed operation has to release that presentation state or
        // the player stays locked: no rotation, no movement, no interaction
        // prompts, for the rest of the raid.
        Fika.Core.Main.Utils.NativeInteractionRelease.Release(InventoryController?.Player, "inventory-operation-failed");
        try
        {
            Operation?.Dispose();
        }
        catch (Exception disposeException)
        {
            FikaGlobals.LogError($"{InventoryController?.ID} - Failed to dispose the failed operation: {disposeException}");
        }

        try
        {
            Callback?.Invoke(OperationResult);
        }
        catch (Exception callbackException)
        {
            FikaGlobals.LogError($"{InventoryController?.ID} - Client operation callback threw: {callbackException}");
        }
        finally
        {
            InventoryController?.ReturnHandler(this);
        }
    }

    private void ExecuteResult(uint generation, IResult executeResult)
    {
        if (!_active || generation != _generation) return;
        if (!executeResult.Succeed)
        {
            FikaGlobals.LogError($"{InventoryController.ID} - Client operation critical failure: {Operation.Id} server status:  - {LogSafe.Ref(Operation)}\r\nError: {executeResult.Error}");
        }
        else
        {
            // The native Execute wrapper registers the items of a move as known; the relayed local
            // execution bypasses it (see MarkOperationItemsKnown).
            InventoryController.MarkOperationItemsKnown(Operation);
        }

        HandleResultDelegate.Invoke(executeResult);
    }

    private void HandleResult(uint generation, IResult result)
    {
        if (!_active || _completed || generation != _generation) return;
        var result2 = OperationResult;
        if (result2?.Failed != true)
        {
            OperationResult = result;
        }
        var serverStatus = ServerStatus.Status;
        if (!serverStatus.Finished())
        {
            return;
        }

        if (_awaitingInventoryResync)
        {
            return;
        }
        var localStatus = Operation.Status;
        if (localStatus.InProgress())
        {
            // A discard is an EFT.InventoryLogic.Operations.ThrowOperation: an async operation
            // that takes the item out of the inventory first and calls GameWorld.ThrowItem at
            // the very end. The host relays the operation to every peer except the source, so
            // this local execution is the only thing that can create the ground object for the
            // dropper. Disposing it here removed the item and never spawned it (the discarded
            // backpack was visible to the host only). Let it finish; the acknowledgement
            // watchdog stays as the backstop.
            if (Operation.TryCast<ThrowOperation>() != null)
            {
                FikaGlobals.LogWarning($"{InventoryController.ID} - The host reported {serverStatus} while the local " +
                    $"discard {Operation.Id} is still running; letting it finish so the ground item is created");
                return;
            }
            if (Operation.TryCast<IServerDependentOperation>() is { } ginterface)
            {
                ginterface.Terminate();
            }
            if (_completed || !Operation.Status.InProgress()) return;
            // The host reported a terminal status while the local execution never
            // finished. Returning here left the caller's callback uninvoked, which
            // locks the player in the interaction state until the raid ends (every
            // later ground item pickup is refused). Release the caller locally.
            FikaGlobals.LogError($"{InventoryController.ID} - Operation {Operation.Id} is still in progress " +
                $"after the server reported {serverStatus}, completing it locally to release the interaction state");
            FailLocally($"The host reported {serverStatus} before the local operation finished.");
            return;
        }
        _completed = true;
        if (serverStatus != localStatus && localStatus.Finished())
        {
            FikaGlobals.LogError($"{InventoryController.ID} - Operation critical failure - status mismatch: {Operation.Id} server status: {serverStatus} client status: {localStatus} - {LogSafe.Ref(Operation)}");
        }

        try
        {
            Operation.Dispose();
        }
        catch (Exception disposeException)
        {
            FikaGlobals.LogError($"{InventoryController.ID} - Failed to dispose the completed operation: {disposeException}");
        }

        try
        {
            Callback?.Invoke(OperationResult);
        }
        catch (Exception callbackException)
        {
            FikaGlobals.LogError($"{InventoryController.ID} - Client operation callback threw: {callbackException}");
        }
        finally
        {
            InventoryController.ReturnHandler(this);
        }
    }

    public void Dispose()
    {
        _active = false;
        _receivedServerStatus = false;
        _completed = false;
        Operation = null;
        Callback = null;
        InventoryController = null;
        OperationResult = null;
        ServerStatus = default;
        CallbackId = default;
        _awaitingInventoryResync = false;
        _inventoryResyncApplied = false;
        _pendingAuthoritativeInventory = null;
        ExecuteResultDelegate = null;
        HandleResultDelegate = null;
        ServerStatusDelegate = null;
    }

    internal void ReceiveAuthoritativeInventory(Inventory inventory)
    {
        if (!_awaitingInventoryResync)
        {
            _pendingAuthoritativeInventory = inventory;
            return;
        }

        ApplyAuthoritativeInventory(inventory);
    }

    private void ApplyAuthoritativeInventory(Inventory inventory)
    {
        Fika.Core.Main.Utils.NativeInteractionRelease.Release(InventoryController?.Player, "authoritative-inventory");
        if (_inventoryResyncApplied)
        {
            return;
        }

        _inventoryResyncApplied = true;
        try
        {
            if (inventory == null)
            {
                // ReplaceInventory(null) would leave the controller with no item
                // graph at all. Release the caller instead of breaking the raid.
                FikaGlobals.LogError($"{InventoryController?.ID} - The authoritative inventory snapshot was empty; " +
                    "keeping the local inventory");
                OperationResult = new FailedResult(ServerStatus.Error ?? "The host sent no inventory snapshot.");
                Callback?.Invoke(OperationResult);
                return;
            }

            Operation?.Dispose();
            // The replacement installs a brand new item graph, so an open loot or
            // inventory screen can keep holding the old items and render empty (the
            // reported black inventory). Force-closing the screens was tried and made it
            // worse: a resync triggered by dragging ammo onto a weapon threw the player
            // out of the inventory, left the drag state dangling, hid every UI element
            // and blocked all input. Report the resync instead of touching the screens.
            FikaGlobals.LogWarning($"{InventoryController.ID} - Applying the authoritative inventory " +
                "(an open screen may need a refresh)");
            InventoryController.ReplaceInventoryFromNetwork(inventory);
            OperationResult = new FailedResult(ServerStatus.Error ?? "The server rejected the inventory operation.");
            Callback?.Invoke(OperationResult);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"{InventoryController.ID} - Failed to apply authoritative inventory: {exception}");
            Callback?.Fail(exception.Message);
        }
        finally
        {
            InventoryController.ReturnHandler(this);
        }
    }

    private string FindOperationItemId()
    {
        try
        {
            if (Operation.TryCast<MoveOperation>() is { } move)
                return move.Item?.Id;
            if (Operation.TryCast<RemoveOperation>() is { } remove)
                return remove.Item?.Id;
        }
        catch
        {
            // Diagnostics must never affect the operation callback.
        }
        return null;
    }
}
