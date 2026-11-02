using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using Dissonance;
using Dissonance.Extensions;
using Dissonance.Integrations.MirrorIgnorance;
using Dissonance.Networking;
using Fika.Core.Main.Utils;
using Il2CppInterop.Runtime.Injection;
using NativeNetwork = Dissonance.Networking.BaseCommsNetwork<Dissonance.Integrations.MirrorIgnorance.MirrorIgnoranceServer, Dissonance.Integrations.MirrorIgnorance.MirrorIgnoranceClient, Dissonance.Integrations.MirrorIgnorance.MirrorConn, Dissonance.Unit, Dissonance.Unit>;

namespace Fika.Core.Networking.VOIP;

/// <summary>
/// 把 Fika 传输层绑定到游戏那套经 AOT 编译的 Dissonance 专用实现上。
/// 编解码器、频道、房间与语音状态仍由原生 Dissonance 拥有。
/// </summary>
public sealed class FikaCommsNetwork : MonoBehaviour
{
    private static readonly Dictionary<IntPtr, FikaCommsNetwork> Networks = new();
    private static readonly MethodInfo NativeUpdate = typeof(NativeNetwork).GetMethod(nameof(NativeNetwork.Update));
    private readonly Queue<Il2CppSystem.ArraySegment<byte>> _loopbackQueue = new();
    public MirrorIgnoranceCommsNetwork Native { get; private set; }
    public FikaVOIPClient Client { get; private set; }
    public FikaVOIPServer Server { get; private set; }
    internal IFikaNetworkManager Owner { get; private set; }
    internal bool IsActive { get; private set; }
    private bool _headless;
    private IntPtr _nativePointer;

    public FikaCommsNetwork(IntPtr pointer) : base(pointer) { }
    public FikaCommsNetwork() : this(ClassInjector.DerivedConstructorPointer<FikaCommsNetwork>()) => ClassInjector.DerivedConstructorBody(this);

    internal static FikaCommsNetwork Attach(MirrorIgnoranceCommsNetwork native, IFikaNetworkManager owner, bool headless)
    {
        if (Networks.TryGetValue(native.Pointer, out var existing))
        {
            if (existing.IsActive && ReferenceEquals(existing.Owner, owner)) return existing;
            throw new InvalidOperationException("The voice scene is already owned by another raid transport.");
        }
        var component = native.gameObject.AddComponent<FikaCommsNetwork>();
        component.Native = native;
        component._nativePointer = native.Pointer;
        component.Owner = owner;
        component._headless = headless;
        component.IsActive = true;
        Networks.Add(native.Pointer, component);
        return component;
    }

    internal static FikaCommsNetwork Find(MirrorIgnoranceCommsNetwork native) =>
        native != null && Networks.TryGetValue(native.Pointer, out var result) ? result : null;

    internal void SetClient(MirrorIgnoranceClient native)
    {
        if (!IsActive) return;
        Client?.Dispose();
        _loopbackQueue.Clear();
        Client = new FikaVOIPClient(this, native);
        if (Owner is FikaClient client) client.VOIPClient = Client;
        else if (Owner is FikaServer server) server.VOIPClient = Client;
    }

    internal void SetServer(MirrorIgnoranceServer native)
    {
        if (!IsActive) return;
        Server?.Dispose();
        Server = new FikaVOIPServer(this, native);
        if (Owner is FikaServer server) server.VOIPServer = Server;
    }

    internal void ClientDisconnected(MirrorIgnoranceClient native)
    {
        if (Client?.Native.Pointer != native.Pointer) return;
        Client.Dispose();
        _loopbackQueue.Clear();
        if (Owner is FikaClient client && ReferenceEquals(client.VOIPClient, Client)) client.VOIPClient = null;
        if (Owner is FikaServer server && ReferenceEquals(server.VOIPClient, Client)) server.VOIPClient = null;
        Client = null;
    }

    internal void ServerDisconnected(MirrorIgnoranceServer native)
    {
        if (Server?.Native.Pointer != native.Pointer) return;
        Server.Dispose();
        _loopbackQueue.Clear();
        if (Owner is FikaServer server && ReferenceEquals(server.VOIPServer, Server)) server.VOIPServer = null;
        Server = null;
    }

    public bool PreprocessPacketToClient(Il2CppSystem.ArraySegment<byte> packet, FikaVOIPPeer peer)
    {
        if (!IsActive) return true;
        if (!peer.Peer.IsLocal) return false;
        // 原生 Dissonance 会在这次调用之后立即回收它的发送缓冲区。
        if (Client != null) _loopbackQueue.Enqueue(packet.ToManaged().ToNative());
        return true;
    }

    public bool PreprocessPacketToServer(Il2CppSystem.ArraySegment<byte> packet)
    {
        if (!IsActive) return true;
        if (Server == null) return false;
        Server.NetworkReceivedPacket(new FikaVOIPPeer(new LocalPeer()), packet);
        return true;
    }

    internal void Tick()
    {
        if (!IsActive || Native == null) return;
        if (Native.IsInitialized)
        {
            if (Owner is FikaClient && !Native.Mode.IsClientEnabled()) Native.RunAsClient(Unit.None);
            else if (Owner is FikaServer && !Native.Mode.IsServerEnabled())
            {
                if (_headless) Native.RunAsDedicatedServer(Unit.None);
                else Native.RunAsHost(Unit.None, Unit.None);
            }
        }
        else if (Native.Mode != NetworkMode.None)
        {
            Native.Stop();
            _loopbackQueue.Clear();
        }

        Server?.RemoveDisconnectedPeers();
        var pending = _loopbackQueue.Count;
        for (var i = 0; i < pending && _loopbackQueue.Count > 0 && IsActive; i++)
        {
            var packet = _loopbackQueue.Dequeue();
            Client?.NetworkReceivedPacket(packet);
        }
        NativeMethod.InvokeVoid(Native, NativeUpdate);
    }

    internal void Shutdown()
    {
        if (!IsActive) return;
        IsActive = false;
        try
        {
            if (Native != null)
            {
                Native.enabled = false;
                var comms = Native.GetComponent<DissonanceComms>();
                if (comms != null)
                {
                    comms.CancelInvoke(nameof(DissonanceComms.Initialize));
                    comms.IsMuted = true;
                    comms.MuteAllRemoteVoices = true;
                }
                // 在原生 Disconnect 调用它的虚方法期间保持网桥处于注册状态；
                // 否则这些调用会穿透到 Mirror。
                Native.Stop();
            }
        }
        catch (Exception exception) { FikaGlobals.LogError($"VOIP shutdown: {exception}"); }
        finally
        {
            _loopbackQueue.Clear();
            Client?.Dispose();
            Server?.Dispose();
            if (Owner is FikaClient client && ReferenceEquals(client.VOIPClient, Client)) client.VOIPClient = null;
            if (Owner is FikaServer server)
            {
                if (ReferenceEquals(server.VOIPClient, Client)) server.VOIPClient = null;
                if (ReferenceEquals(server.VOIPServer, Server)) server.VOIPServer = null;
            }
            Client = null;
            Server = null;
            Owner = null;
        }
    }

    private void OnDestroy()
    {
        Shutdown();
        Networks.Remove(_nativePointer);
    }
}
