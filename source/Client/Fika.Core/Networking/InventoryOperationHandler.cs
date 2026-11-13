using Diz.LanguageExtensions;
using System;
using Comfort.Common;
using EFT;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.Communication;
#if DEBUG
#endif
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
#if DEBUG
#endif

namespace Fika.Core.Networking;

public sealed class InventoryOperationHandler : IDisposable
{
    public void Set(OperationCreationResult operationResult, ushort operationId, int netId, NetPeer peer, FikaServer server)
    {
        OperationResult = operationResult;
        _operationId = operationId;
        _netId = netId;
        _peer = peer;
        _server = server;
        Completed = false;
        TerminalStatusReported = false;
        _dispatching = true;
        var generation = unchecked(++_generation);
        HandleResultDelegate = (Callback)(result => HandleResult(generation, result));
    }

    private InventoryOperationHandler()
    {
    }

    public static InventoryOperationHandler CreateInstance()
    {
        return new InventoryOperationHandler();
    }

    public Callback HandleResultDelegate;
    public OperationCreationResult OperationResult;

    private ushort _operationId;
    private int _netId;
    private NetPeer _peer;
    private FikaServer _server;
    private byte[] _relayPayload;
    private uint _generation;
    private bool _dispatching;
    public bool Completed { get; private set; }
    public bool TerminalStatusReported { get; private set; }

    public void SetRelayPayload(byte[] payload)
    {
        _relayPayload = payload;
    }

    private void HandleResult(uint generation, IResult result)
    {
        if (generation != _generation || Completed) return;
        Completed = true;
        try
        {
            if (!result.Succeed)
            {
                FikaGlobals.LogError($"Error in operation: {result.Error ?? "An unknown error has occured"}");
                _server.SendGenericPacketToPeer(EGenericSubPacketType.OperationCallback,
                            OperationCallbackPacket.FromValue(_netId, _operationId, EOperationStatus.Failed,
                            result.Error ?? "An unknown error has occured"), _peer);
                TerminalStatusReported = true;

                _server.SendInventoryResyncToPeer(_netId, _operationId, _peer);

                ResyncInventoryIdPacket resyncPacket = new(_netId);
                _server.SendDataToPeer(ref resyncPacket, DeliveryMethod.ReliableOrdered, _peer);

                return;
            }

            // 接收数据包与原生描述符属于数据包处理器。
            // 对于异步的原生完成，只保留序列化后的字节；
            // 数据包对象可能已经被回收进池。
            try
            {
                _server.RelayInventoryOperation(_relayPayload, _peer);
            }
            catch (Exception exception)
            {
                FikaGlobals.LogError($"Failed to relay accepted inventory operation {_operationId}: {exception}");
            }

            _server.SendGenericPacketToPeer(EGenericSubPacketType.OperationCallback,
                            OperationCallbackPacket.FromValue(_netId, _operationId, EOperationStatus.Succeeded), _peer);
            TerminalStatusReported = true;
        }
        finally
        {
            // Execute 可能同步调用该委托。调用方在其 Execute 栈
            // 展开之前仍然需要这个处理器。
            if (!_dispatching)
                _server.ReturnHandler(this);
        }
    }

    public void FinishDispatch()
    {
        if (!_dispatching) return;
        _dispatching = false;
        if (Completed)
            _server.ReturnHandler(this);
    }

    public void Cancel()
    {
        if (Completed) return;
        unchecked { ++_generation; }
        Completed = true;
        // 保持处理器存活，直到原生 Execute 栈展开完毕。
        // 在同步情形下 FinishDispatch 会把它归还到池中。
        if (!_dispatching)
            _server.ReturnHandler(this);
    }

    public void Dispose()
    {
        OperationResult = default;
        _operationId = default;
        _netId = default;
        _peer = null;
        _server = null;
        _relayPayload = null;
        HandleResultDelegate = null;
        _dispatching = false;
    }
}
