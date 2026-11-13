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
        // 这个池化处理器早先使用时的原生完成回调，
        // 不得在处理器被复用后完成较新的操作。
        if (generation != _generation || Completed) return;
        Completed = true;
        Succeeded = result.Succeed;
        var controller = InventoryController;
        try
        {
            if (!result.Succeed)
            {
                FikaGlobals.LogError($"[{Time.frameCount}][{InventoryController.Name}] {InventoryController.ID} - Local operation failed: {Operation.Id} - {LogSafe.Ref(Operation)}\r\nError: {result.Error}");
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

    // Execute 可能同步完成。保持操作与回调存活，
    // 直到其网络描述符已发送（或已被显式丢弃）。
    public void FinishDispatch()
    {
        _dispatching = false;
        if (Completed)
            FinishCompletedOperation(InventoryController);
    }

    public void CancelDispatch()
    {
        // 在 Execute 抛出异常或操作被拒绝之后，原生回调仍可能到达。
        // 它不得触碰这个池化实例。
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
