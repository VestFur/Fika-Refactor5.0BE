using Comfort.Common;
using Dissonance.Integrations.MirrorIgnorance;

namespace Fika.Core.Networking.VOIP;

/// <summary>围绕既有 native 语音客户端的托管传输适配器。</summary>
public sealed class FikaVOIPClient(FikaCommsNetwork network, MirrorIgnoranceClient native) : System.IDisposable
{
    internal MirrorIgnoranceClient Native { get; } = native;
    private bool _disposed;

    public void NetworkReceivedPacket(Il2CppSystem.ArraySegment<byte> packet)
    {
        if (!_disposed && network.IsActive && packet.Count > 0) Native.NetworkReceivedPacket(packet);
    }

    internal void Send(Il2CppSystem.ArraySegment<byte> packet, DeliveryMethod delivery)
    {
        if (_disposed || !network.IsActive || packet.Count == 0) return;
        if (!network.PreprocessPacketToServer(packet))
            network.Owner.SendVOIPData(packet.ToManaged(), delivery);
    }

    public void Dispose() => _disposed = true;
}
