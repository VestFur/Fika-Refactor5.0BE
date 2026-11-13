using System;
using Comfort.Common;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Main.HostClasses;

public sealed class HostInventoryOperationHandler : IDisposable
{
    public HostInventoryController InventoryController;
    public EFT.InventoryLogic.Operations.AbstractOperation Operation;
    public Callback Callback;

    public Callback HandleResultDelegate;
    public bool Completed { get; private set; }
    public bool Succeeded { get; private set; }
    private bool _dispatching;
    private InventoryPacket _pendingPacket;
    private uint _generation;

    public static HostInventoryOperationHandler CreateInstance()
    {
        return new HostInventoryOperationHandler();
    }

    private HostInventoryOperationHandler()
    {
    }

    public void Set(HostInventoryController inventoryController, EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
        InventoryController = inventoryController;
        Operation = operation;
        Callback = callback;
        Completed = false;
        Succeeded = false;
        _dispatching = true;
        var generation = unchecked(++_generation);
        HandleResultDelegate = (Callback)(result => HandleResult(generation, result));
    }

    private void HandleResult(uint generation, IResult result)
    {
        // A native completion from an earlier use of this pooled handler must
        // not complete the newer operation after the handler has been reused.
        if (generation != _generation || Completed) return;
        Completed = true;
        Succeeded = result.Succeed;
        var controller = InventoryController;
        try
        {
            if (!result.Succeed)
            {
                FikaGlobals.LogError($"[{Time.frameCount}][{InventoryController.Name}] {InventoryController.ID} - Local operation failed: {Operation.Id} - {Operation}\r\nError: {result.Error}");
            }
            Callback?.Invoke(result);
        }
        finally
        {
            if (!_dispatching)
                FinishCompletedOperation(controller);
        }
    }

    public void SetPendingPacket(InventoryPacket packet)
    {
        _pendingPacket = packet;
    }

    // Execute may complete synchronously. Keep the operation and callback alive
    // until its network descriptor has been sent (or explicitly discarded).
    public void FinishDispatch()
    {
        _dispatching = false;
        if (Completed)
            FinishCompletedOperation(InventoryController);
    }

    public void CancelDispatch()
    {
        // A native callback can still arrive after Execute throws or the
        // operation is rejected. It must not touch this pooled instance.
        unchecked { ++_generation; }
        Completed = true;
        _dispatching = false;
        ReturnPendingPacket();
        InventoryController.ReturnHandler(this);
    }

    private void FinishCompletedOperation(HostInventoryController controller)
    {
        try
        {
            if (Succeeded && _pendingPacket != null)
            {
                var packet = _pendingPacket;
                _pendingPacket = null;
                try
                {
                    controller.FikaPlayer.PacketSender.NetworkManager.SendGenericPacket(
                        EGenericSubPacketType.InventoryOperation, packet, true);
                }
                catch (Exception exception)
                {
                    FikaGlobals.LogError($"Failed to relay host inventory operation: {exception}");
                }
            }
        }
        finally
        {
            ReturnPendingPacket();
            controller.ReturnHandler(this);
        }
    }

    private void ReturnPendingPacket()
    {
        if (_pendingPacket == null) return;
        GenericSubPacketPoolManager.Instance.ReturnPacket(EGenericSubPacketType.InventoryOperation, _pendingPacket);
        _pendingPacket = null;
    }

    public void Dispose()
    {
        InventoryController = null;
        Operation = null;
        Callback = null;
        HandleResultDelegate = null;
        _dispatching = false;
        _pendingPacket = null;
    }
}
