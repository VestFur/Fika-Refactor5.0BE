using System;
using System.Collections.Generic;
using Dissonance.Integrations.MirrorIgnorance;
using Fika.Core.Main.Utils;

namespace Fika.Core.Networking.VOIP;

/// <summary>把稳定的原生语音连接身份映射到 Fika 对等端。</summary>
public sealed class FikaVOIPServer(FikaCommsNetwork network, MirrorIgnoranceServer native) : IDisposable
{
    private readonly Dictionary<NetPeer, MirrorConn> _remote = new();
    private readonly Dictionary<IntPtr, FikaVOIPPeer> _peers = new();
    private MirrorConn _local;
    private bool _disposed;
    internal MirrorIgnoranceServer Native { get; } = native;

    public void NetworkReceivedPacket(FikaVOIPPeer peer, Il2CppSystem.ArraySegment<byte> packet)
    {
        if (_disposed || !network.IsActive || packet.Count == 0) return;
        MirrorConn connection;
        if (peer.Peer.IsLocal)
        {
            _local ??= CreateConnection(-1, "loopback", 0, peer);
            connection = _local;
        }
        else if (peer.Peer is RemotePeer remote)
        {
            if (remote.Peer.ConnectionState != ConnectionState.Connected) return;
            if (!_remote.TryGetValue(remote.Peer, out connection))
            {
                connection = CreateConnection(remote.Peer.Id, remote.Peer.Address.ToString(), remote.Peer.Port, peer);
                _remote.Add(remote.Peer, connection);
            }
        }
        else throw new ArgumentException("Unsupported Fika voice peer", nameof(peer));
        Native.NetworkReceivedPacket(connection, packet);
    }

    private MirrorConn CreateConnection(int id, string address, int port, FikaVOIPPeer peer)
    {
        var identity = new EFT.Network.NetworkConnection(0, id, address, port);
        identity._IsConnected_k__BackingField = true;
        var connection = new MirrorConn(identity);
        _peers.Add(identity.Pointer, peer);
        return connection;
    }

    internal void Send(MirrorConn connection, Il2CppSystem.ArraySegment<byte> packet, DeliveryMethod delivery)
    {
        if (_disposed || !network.IsActive || packet.Count == 0) return;
        if (connection?.Connection == null || !_peers.TryGetValue(connection.Connection.Pointer, out var peer))
        {
            // Dissonance 正在完成一次语音更新时，连接可能已被移除。
            // 与上游 Fika 一样，丢弃这次过期的发送，而不是
            // 中止原生的语音循环。
            FikaGlobals.LogError("FikaVOIPServer: no peer for this connection");
            return;
        }
        if (!network.PreprocessPacketToClient(packet, peer) && peer.Peer is RemotePeer remote &&
            remote.Peer.ConnectionState == ConnectionState.Connected)
            network.Owner.SendVOIPData(packet.ToManaged(), delivery, remote.Peer);
    }

    internal void RemoveDisconnectedPeers()
    {
        if (_disposed) return;
        List<NetPeer> removed = new();
        foreach (var pair in _remote)
        {
            if (pair.Key.ConnectionState == ConnectionState.Connected) continue;
            removed.Add(pair.Key);
        }
        foreach (var peer in removed)
        {
            if (!_remote.Remove(peer, out var connection)) continue;
            connection.Connection._IsConnected_k__BackingField = false;
            try { Native.ClientDisconnected(connection); }
            finally { _peers.Remove(connection.Connection.Pointer); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var connection in _remote.Values) connection.Connection._IsConnected_k__BackingField = false;
        if (_local != null) _local.Connection._IsConnected_k__BackingField = false;
        _remote.Clear();
        _peers.Clear();
        _local = null;
    }
}
