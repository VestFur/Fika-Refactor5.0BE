// © 2026 Lacyway All Rights Reserved

using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.Settings;
using EFT.UI;
using EFT.Vehicle;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Components;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking.Packets;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.Networking.Packets.Communication;
#if DEBUG
using Fika.Core.Networking.Packets.Debug;
#endif
using Fika.Core.Networking.Packets.FirearmController;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.Player;
using Fika.Core.Networking.Packets.Player.Common;
using Fika.Core.Networking.Packets.World;
using Fika.Core.Networking.Pooling;
using Fika.Core.Networking.Snapshotting;
using Fika.Core.Networking.VOIP;
using Fika.Core.UI.Custom;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using static Fika.Core.Networking.NetworkUtils;

namespace Fika.Core.Networking;

/// <summary>
/// Client used to communicate with the <see cref="FikaServer"/>
/// </summary>
public sealed partial class FikaClient : MonoBehaviour, INetEventListener, IFikaNetworkManager
{
    public FikaClient(System.IntPtr pointer) : base(pointer) { }


    public FikaClient() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClient>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public FikaPlayer MyPlayer;
    public int Ping;
    public int ServerFPS;
    public int ReadyClients;
    public bool HostReady;
    public bool HostLoaded;
    public bool HostReceivedLocation;
    public bool ReconnectDone;
    internal string ReconnectError;
    public NetPeer ServerConnection { get; private set; }
    public NetManager NetClient
    {
        get
        {
            return _netClient;
        }
    }
    public NetDataWriter Writer
    {
        get
        {
            return _dataWriter;
        }
    }
    public bool Started
    {
        get
        {
            if (_netClient == null)
            {
                return false;
            }
            return _netClient.IsRunning;
        }
    }
    public int SendRate
    {
        get
        {
            return _sendRate;
        }
    }
    public CoopHandler CoopHandler
    {
        get
        {
            return _coopHandler;
        }
        set
        {
            _coopHandler = value;
        }
    }
    public Stash TemporaryStash { get; set; }
    public bool StrictInventorySync { get; set; }
    public Queue<EFT.InventoryLogic.Operations.AbstractOperation> InventoryOperations
    {
        get
        {
            return _inventoryOperations;
        }
    }

    internal FikaVOIPClient VOIPClient { get; set; }

    public int NetId { get; set; }
    public FikaClientWorld FikaClientWorld { get; set; }
    public ESideType RaidSide { get; set; }
    public bool AllowVOIP { get; set; }
    public List<ObservedPlayer> ObservedPlayers { get; set; }
    public int PlayerAmount { get; set; }

    private NetPacketProcessor _packetProcessor;
    private int _sendRate;
    private NetManager _netClient;
    private CoopHandler _coopHandler;
    private ManualLogSource _logger;
    private NetDataWriter _dataWriter;
    private FikaChatUIScript _fikaChat;
    private string _myProfileId;
    // Bounds how long the client keeps polling for the host's raid loot
    // descriptor before it accepts an empty world.
    private const int MaxEmptyLootDescriptors = 30;
    private int _emptyLootDescriptors;
    private Queue<EFT.InventoryLogic.Operations.AbstractOperation> _inventoryOperations;
    private readonly Queue<TransitEventPacket> _pendingTransitEvents = new();
    private List<int> _missingIds;
    private GenericPacket _genericPacket;
    private DateTime _startTime;
    private Callback _handleInventoryOperationCallback;

    public async Task Init()
    {
        _netClient = new(this)
        {
            UnconnectedMessagesEnabled = true,
            UpdateTime = 50,
            NatPunchEnabled = false,
            AutoRecycle = true,
            IPv6Enabled = FikaPlugin.Instance.Settings.UseIPv6.Value,
            DisconnectTimeout = FikaPlugin.Instance.Settings.ConnectionTimeout.Value * 1000,
            EnableStatistics = true,
            MaxConnectAttempts = 5,
            ReconnectDelay = 1 * 1000,
            ChannelsCount = 2,
            UseNativeSockets = NativeSocket.IsSupported
        };

        _packetProcessor = new();
        _dataWriter = new();
        _logger = Logger.CreateLogSource("Fika.Client");
        _inventoryOperations = new(8);
        _missingIds = [];
        _startTime = DateTime.Now;
        _handleInventoryOperationCallback = (Callback)HandleResult;
        ObservedPlayers = [];
        PlayerAmount = 1;

        Ping = 0;
        ServerFPS = 0;
        ReadyClients = 0;

        TemporaryStash = Singleton<ItemFactory>.Instance.CreateFakeStash(new Il2CppSystem.Nullable<MongoID>());

        NetworkGameSession.Rtt = 0;
        NetworkGameSession.LossPercent = 0;

        _myProfileId = FikaBackendUtils.Profile.ProfileId;
        _genericPacket = new();

        await RegisterPacketsAndTypes();

#if DEBUG
        AddDebugPackets();
#endif

        await NetManagerUtils.CreateCoopHandler();

        NetManagerUtils.ApplySimulationSettings(_netClient);

        if (FikaBackendUtils.IsHostNatPunch)
        {
            _netClient.Start(FikaBackendUtils.LocalPort); // NAT punching has to re-use the same local port
        }
        else
        {
            _netClient.Start();
        }

        var endPoint = FikaBackendUtils.RemoteEndPoint;
        var connectString = FikaProtocol.ConnectionKey(FikaBackendUtils.IsReconnect, FikaBackendUtils.IsScavLateJoin);

        if (endPoint.Address == null)
        {
            Singleton<PreloaderUI>.Instance.ShowErrorScreen("Network Error",
                "Unable to connect to the raid server. IP and/or Port was empty when requesting data!");
        }
        else
        {
            ServerConnection = _netClient.Connect(FikaBackendUtils.RemoteEndPoint, connectString);
        }
    }

    private FikaVoiceSession _voiceSession;

    public Task InitializeVOIP()
    {
        if (_voiceSession?.CanReuse == true) return _voiceSession.InitializeAsync();
        _voiceSession?.Dispose();
        _voiceSession = new FikaVoiceSession(this, FikaBackendUtils.IsHeadless);
        return _voiceSession.InitializeAsync();
    }

    internal void ShutdownVOIP()
    {
        _voiceSession?.Dispose();
        _voiceSession = null;
    }

    private Task RegisterPacketsAndTypes()
    {
        PoolUtils.CreateAll();

        RegisterCustomType(FikaSerializationExtensions.PutRagdollStruct, FikaSerializationExtensions.GetRagdollStruct);
        RegisterCustomType(FikaSerializationExtensions.PutArtilleryStruct, FikaSerializationExtensions.GetArtilleryStruct);
        RegisterCustomType(FikaSerializationExtensions.PutGrenadeStruct, FikaSerializationExtensions.GetGrenadeStruct);
        RegisterCustomType(FikaSerializationExtensions.PutAirplaneDataPacketStruct, FikaSerializationExtensions.GetAirplaneDataPacketStruct);
        RegisterCustomType(FikaSerializationExtensions.PutLootSyncStruct, FikaSerializationExtensions.GetLootSyncStruct);

        RegisterPacket<InformationPacket>(OnInformationPacketReceived);
        RegisterPacket<TextMessagePacket>(OnTextMessagePacketReceived);
        RegisterPacket<QuestConditionPacket>(OnQuestConditionPacketReceived);
        RegisterPacket<QuestItemPacket>(OnQuestItemPacketReceived);
        RegisterPacket<QuestDropItemPacket>(OnQuestDropItemPacketReceived);
        RegisterPacket<HalloweenEventPacket>(OnHalloweenEventPacketReceived);
        RegisterPacket<KolotunStatePacket>(OnKolotunStatePacketReceived);
        RegisterPacket<InteractableInitPacket>(OnInteractableInitPacketReceived);
        RegisterPacket<StatisticsPacket>(OnStatisticsPacketReceived);
        RegisterPacket<WorldLootPacket>(OnWorldLootPacketReceived);
        RegisterPacket<ReconnectPacket>(OnReconnectPacketReceived);
        RegisterPacket<SpawnSyncObjectPacket>(OnSpawnSyncObjectPacketReceived);
        RegisterPacket<BTRInteractionPacket>(OnBTRInteractionPacketReceived);
        RegisterPacket<FlareSuccessPacket>(OnFlareSuccessPacketReceived);
        RegisterPacket<BufferZonePacket>(OnBufferZonePacketReceived);
        RegisterPacket<ResyncInventoryIdPacket>(OnResyncInventoryIdPacketReceived);
        RegisterPacket<InventoryResyncPacket>(OnInventoryResyncPacketReceived);
        RegisterPacket<NetworkSettingsPacket>(OnNetworkSettingsPacketReceived);
        RegisterPacket<SyncTransitControllersPacket>(OnSyncTransitControllersPacketReceived);
        RegisterPacket<TransitEventPacket>(OnTransitEventPacketReceived);
        RegisterPacket<BotStatePacket>(OnBotStatePacketReceived);
        RegisterPacket<LoadingProfilePacket>(OnLoadingProfilePacketReceived);
        RegisterPacket<SideEffectPacket>(OnSideEffectPacketReceived);
        RegisterPacket<RequestPacket>(OnRequestPacketReceived);
        RegisterPacket<InRaidQuestPacket>(OnInraidQuestPacketReceived);
        RegisterPacket<EventControllerEventPacket>(OnEventControllerEventPacketReceived);
        RegisterPacket<EventControllerInteractPacket>(OnEventControllerInteractPacketReceived);
        RegisterPacket<SyncTrapsPacket>(OnSyncTrapsPacketReceived);
        RegisterPacket<StashesPacket>(OnStashesPacketReceived);
        RegisterPacket<MessagePacket>(OnMessagePacketReceived);
        RegisterPacket<LoadingScreenPacket>(OnLoadingScreenPacketReceived);
        RegisterPacket<LoadingScreenPlayersPacket>(OnLoadingScreenPlayersPacketReceived);
        RegisterPacket<SyncEventPacket>(OnSyncEventPacketReceived);
        RegisterPacket<ClearSnapshotterPacket>(OnClearSnapshotterPacketReceived);
        RegisterPacket<ProceedResponsePacket>(OnProceedResponsePacketReceived);
        RegisterPacket<SpawnItemInInventoryPacket>(SpawnItemInInventoryPacketReceived);
        RegisterPacket<PasscodeInitPacket>(OnPasscodeInitPacketReceived);
        RegisterPacket<PasscodeResultPacket>(OnPasscodeResultPacketReceived);
        RegisterPacket<CutsceneStartPacket>(OnCutsceneStartPacketReceived);
        RegisterPacket<CutsceneSkipPacket>(OnCutsceneSkipPacketReceived);
        RegisterPacket<CutsceneEndPacket>(OnCutsceneEndPacketReceived);
        RegisterPacket<CutsceneTimePacket>(OnCutsceneTimePacketReceived);
        RegisterPacket<FinalMissionPacket>(OnFinalMissionPacketReceived);

        RegisterReusable<WorldPacket>(OnWorldPacketReceived);

        RegisterNetReusable<WeaponPacket>(OnWeaponPacketReceived);
        RegisterNetReusable<CommonPlayerPacket>(OnCommonPlayerPacketReceived);
        RegisterNetReusable<GenericPacket>(OnGenericPacketReceived);

        return Task.CompletedTask;
    }

#if DEBUG
    private void AddDebugPackets()
    {
        RegisterPacket<SpawnItemPacket>(OnSpawnItemPacketReceived);
    }
#endif

    public void SetupGameVariables(FikaPlayer fikaPlayer)
    {
        MyPlayer = fikaPlayer;
    }

    public void CreateFikaChat()
    {
        if (FikaPlugin.Instance.Settings.EnableChat.Value)
        {
            _fikaChat = FikaChatUIScript.Create();
        }
    }

    private void Update()
    {
        RaidInteractionSync.Tick();
        _netClient?.PollEvents();
        ApplyPendingRaidInteractions();
        ApplyPendingMapState();
        FlushPendingTransitEvents();

        var networkTime = NetworkTimeSync.NetworkTime;
        for (var i = 0; i < ObservedPlayers.Count; i++)
        {
            ObservedPlayers[i].ManualStateUpdate(networkTime);
        }

        HandleInventoryOperations();

        if (Input.GetKeyDown(FikaPlugin.Instance.Settings.ChatKey.Value.MainKey))
        {
            if (_fikaChat != null)
            {
                _fikaChat.ToggleChat();
            }
        }
    }

    private void QueueTransitEvent(TransitEventPacket packet)
    {
        if (packet == null) return;
        if (_pendingTransitEvents.Count >= 256)
        {
            _logger.LogError("Transit startup event queue exceeded its limit");
            return;
        }

        // Keep an independent packet shell while the native controller is
        // being created. The event payload itself is a freshly deserialized
        // IL2CPP object and is consumed exactly once by the controller.
        _pendingTransitEvents.Enqueue(new TransitEventPacket
        {
            EventType = packet.EventType,
            TransitEvent = packet.TransitEvent,
            TransitId = packet.TransitId,
            PlayerId = packet.PlayerId,
            Destination = packet.Destination,
            EnabledPointIds = packet.EnabledPointIds,
            RegisteredPointId = packet.RegisteredPointId,
            KeyId = packet.KeyId,
            SelectedTime = packet.SelectedTime
        });
    }

    private void FlushPendingTransitEvents()
    {
        var world = Singleton<GameWorld>.Instance;
        if (FikaWorldControllers.Find<Fika.Core.Main.ClientClasses.ClientTransitController>(world?.TransitController) is not { } transit) return;
        while (_pendingTransitEvents.TryDequeue(out var packet))
        {
            if (packet.EventType == TransitEventPacket.ETransitEventType.Init) transit.ReceiveSnapshot(packet);
            else transit.ReceiveEvent(packet);
        }
        transit.ProcessPendingInitialization();
    }

    /// <summary>
    /// Processes queued <see cref="EFT.InventoryLogic.Operations.AbstractOperation"/>
    /// </summary>
    private void HandleInventoryOperations()
    {
        while (_inventoryOperations.Count > 0)
        {
            if (_inventoryOperations.Peek().WaitingForForeignEvents())
            {
                return;
            }

            _inventoryOperations.Dequeue()
                .Execute(_handleInventoryOperationCallback);
        }
    }

    private void OnDestroy()
    {
        ShutdownVOIP();
        ClearRaidInteractions();
        ClearMapState();
        _pendingTransitEvents.Clear();
        _netClient?.Stop();
        _genericPacket.Clear();

        PoolUtils.ReleaseAll();

        if (_fikaChat != null)
        {
            Destroy(_fikaChat);
        }

        FikaEventDispatcher.DispatchEvent(new FikaNetworkManagerDestroyedEvent(this));
    }

    public void SendData<T>(ref T packet, DeliveryMethod deliveryMethod, bool broadcast = false) where T : INetSerializable
    {
        var peer = ServerConnection ?? _netClient?.FirstPeer;
        if (peer == null)
        {
            _logger.LogError($"SendData<{typeof(T).Name}> was dropped: no host connection is cached. " +
                "The client would stall on the next server response.");
            return;
        }

        ServerConnection ??= peer;
        _dataWriter.Reset();
        _dataWriter.Put(broadcast);
        _dataWriter.PutEnum(EPacketType.Serializable);

        _packetProcessor.WriteNetSerializable(_dataWriter, ref packet);
        peer.Send(_dataWriter.AsReadOnlySpan(), deliveryMethod);
    }

    public void SendPlayerState(ref PlayerStateData packet)
    {
        _dataWriter.Reset();
        _dataWriter.Put(true);
        _dataWriter.PutEnum(EPacketType.PlayerState);
        _dataWriter.Put(NetworkTimeSync.NetworkTime);
        _dataWriter.PutUnmanaged(packet);

        _netClient.SendToAll(_dataWriter.AsReadOnlySpan(), DeliveryMethod.Unreliable);
    }

    public void SendGenericPacket(EGenericSubPacketType type, IPoolSubPacket subpacket, bool broadcast = false, NetPeer peerToIgnore = null)
    {
        var packet = _genericPacket;
        packet.Type = type;
        packet.SubPacket = subpacket;
        SendNetReusable(ref packet, DeliveryMethod.ReliableOrdered, broadcast);
    }

    public void SendNetReusable<T>(ref T packet, DeliveryMethod deliveryMethod, bool broadcast = false, NetPeer peerToIgnore = null) where T : INetReusable
    {
        try
        {
            _dataWriter.Reset();
            _dataWriter.Put(broadcast);
            _dataWriter.PutEnum(EPacketType.Serializable);

            _packetProcessor.WriteNetReusable(_dataWriter, ref packet);
            _netClient.SendToAll(_dataWriter.AsReadOnlySpan(), deliveryMethod);
        }
        finally
        {
            packet.Clear();
        }
    }

    public void SendDataToPeer<T>(ref T packet, DeliveryMethod deliveryMethod, NetPeer peer) where T : INetSerializable
    {
        _dataWriter.Reset();
        _dataWriter.Put(false);
        _dataWriter.PutEnum(EPacketType.Serializable);

        _packetProcessor.WriteNetSerializable(_dataWriter, ref packet);
        peer.Send(_dataWriter.AsReadOnlySpan(), deliveryMethod);
    }

    public void SendVOIPData(ArraySegment<byte> data, DeliveryMethod deliveryMethod, NetPeer peer = null)
    {
        var firstPeer = _netClient.FirstPeer;
        if (firstPeer != null)
        {
            _dataWriter.Reset();

            _dataWriter.Put(false);
            _dataWriter.PutEnum(EPacketType.VOIP);
            _dataWriter.Put(data.AsSpan());
            firstPeer.Send(_dataWriter.AsReadOnlySpan(), deliveryMethod);
        }
    }

    /// <summary>
    /// Sends a reusable packet
    /// </summary>
    /// <typeparam name="T">The <see cref="IReusable"/> to send</typeparam>
    /// <param name="packet">The <see cref="INetSerializable"/> to send</param>
    /// <param name="deliveryMethod">The deliverymethod</param>
    /// <remarks>
    /// Reusable will always be of type broadcast when sent from a client
    /// </remarks>
    public void SendReusable<T>(T packet, DeliveryMethod deliveryMethod) where T : class, IReusable, new()
    {
        var peer = _netClient.FirstPeer;
        if (peer != null)
        {
            _dataWriter.Reset();

            _dataWriter.Put(true);
            _dataWriter.PutEnum(EPacketType.Serializable);
            _packetProcessor.Write(_dataWriter, packet);
            peer.Send(_dataWriter.AsReadOnlySpan(), deliveryMethod);
        }

        packet.Flush();
    }

    public void OnPeerConnected(NetPeer peer)
    {
        FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.CONNECTED_TO_SERVER.Localized(), FikaBackendUtils.RemoteEndPoint.Port),
            ENotificationDurationType.Default, ENotificationIconType.Friend);

        var ownProfile = FikaGlobals.GetLiteProfile(FikaBackendUtils.IsScav);
        if (ownProfile == null)
        {
            _logger.LogError("OnPeerConnected: Own profile was null!");
            return;
        }

        NetworkSettingsPacket settingsPacket = new()
        {
            ProfileId = ownProfile.ProfileId
        };
        SendData(ref settingsPacket, DeliveryMethod.ReliableOrdered);

        ownProfile.Info.SetProfileNickname(FikaBackendUtils.PMCName ?? ownProfile.Nickname);
        Dictionary<Profile, bool> profiles = [];
        profiles.Add(ownProfile, false);
        LoadingProfilePacket profilePacket = new()
        {
            Profiles = profiles
        };
        SendData(ref profilePacket, DeliveryMethod.ReliableOrdered);

        FikaEventDispatcher.DispatchEvent(new PeerConnectedEvent(peer, this));
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketErrorCode)
    {
        _logger.LogError("[CLIENT] We received error " + socketErrorCode);
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        switch (reader.GetEnum<EPacketType>())
        {
            case EPacketType.Serializable:
                _packetProcessor.ReadAllPackets(reader, peer);
                break;
            case EPacketType.PlayerState:
                var remoteTime = reader.GetDouble();
                var localTime = NetworkTimeSync.NetworkTime;
                var remaining = reader.GetRemainingBytesSpan();
                var snapshots = MemoryMarshal.Cast<byte, PlayerStateData>(remaining);
                for (var i = 0; i < snapshots.Length; i++)
                {
                    ref readonly var snapshot = ref snapshots[i];
                    if (_coopHandler.Players.TryGetValue(snapshot.NetId, out var player))
                    {
                        var header = new PlayerStateSnapshot(in snapshot, remoteTime, localTime);
                        player.Snapshotter.AddSnapshot(in header);
                    }
                }
                break;
            case EPacketType.BTR:
                var data = reader.GetUnmanaged<ShapshotBTRMessage>();
                QueueBtrState(data);
                break;
            case EPacketType.VOIP:
                if (VOIPClient != null)
                {
                    VOIPClient.NetworkReceivedPacket(reader.GetRemainingBytesSegment().ToNative());
                }
                break;
        }
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        if (messageType == UnconnectedMessageType.BasicMessage && _netClient.ConnectedPeersCount == 0 && reader.GetInt() == 1)
        {
            _logger.LogInfo("[CLIENT] Received discovery response. Connecting to: " + remoteEndPoint);
            // This is the normal LAN path. The peer has to be cached as well:
            // SendData only targets ServerConnection and silently drops every
            // client packet (exfiltration requests, inventory operations, pickups)
            // while it is null, which strands the client on the loading screen and
            // freezes it as soon as it interacts with loot.
            ServerConnection = _netClient.Connect(remoteEndPoint, NetDataWriter.FromString(
                FikaProtocol.ConnectionKey(FikaBackendUtils.IsReconnect, FikaBackendUtils.IsScavLateJoin)).AsReadOnlySpan());
        }
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        Ping = latency;
        NetworkGameSession.Rtt = peer.RoundTripTime;
        NetworkGameSession.LossPercent = (int)NetClient.Statistics.PacketLossPercent;
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {

    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        _logger.LogInfo("[CLIENT] We disconnected because " + disconnectInfo.Reason);
        if (disconnectInfo.Reason is DisconnectReason.Timeout)
        {
            NotificationManager.DisplayWarningNotification(LocaleUtils.LOST_CONNECTION.Localized());
            // The host can time out while the reconnect profile or local
            // player is still loading. Release the transport even when no
            // player/packet sender has been created yet.
            try
            {
                MyPlayer?.PacketSender?.DestroyThis();
            }
            catch (Exception exception)
            {
                _logger.LogError($"Could not stop the timed-out player's packet sender: {exception}");
            }
            finally
            {
                try { Destroy(this); }
                catch (Exception exception)
                {
                    _logger.LogError($"Could not destroy the timed-out client: {exception}");
                }
                // A new client can be created while an old peer's timeout
                // callback is still running. Do not clear that replacement.
                try { FikaSingleton<FikaClient>.TryRelease(this); }
                catch (Exception exception)
                {
                    _logger.LogError($"Could not release the timed-out client singleton: {exception}");
                }
            }
        }

        if (disconnectInfo.Reason is DisconnectReason.ConnectionRejected)
        {
            var reason = disconnectInfo.AdditionalData.GetString();
            if (!string.IsNullOrEmpty(reason))
            {
                NotificationManager.DisplayWarningNotification(reason);
                return;
            }

            _logger.LogError("OnPeerDisconnected: Rejected connection but no reason");
        }

        FikaEventDispatcher.DispatchEvent(new PeerDisconnectedEvent(peer, this));
    }

    public void RegisterPacket<T>(Action<T> handle) where T : INetSerializable, new()
    {
        _packetProcessor.SubscribeNetSerializable(handle);
    }

    public void RegisterPacket<T, TUserData>(Action<T, TUserData> handle) where T : INetSerializable, new()
    {
        _packetProcessor.SubscribeNetSerializable(handle);
    }

    public void RegisterReusable<T>(Action<T> handle) where T : class, IReusable, new()
    {
        _packetProcessor.SubscribeReusable(handle);
    }

    public void RegisterReusable<T, TUserData>(Action<T, TUserData> handle) where T : class, IReusable, new()
    {
        _packetProcessor.SubscribeReusable(handle);
    }

    public void RegisterNetReusable<T>(Action<T> handle) where T : class, INetReusable, new()
    {
        _packetProcessor.SubscribeNetReusable(handle);
    }

    public void RegisterNetReusable<T, TUserData>(Action<T, TUserData> handle) where T : class, INetReusable, new()
    {
        _packetProcessor.SubscribeNetReusable(handle);
    }

    public void RegisterCustomType<T>(Action<NetDataWriter, T> writeDelegate, Func<NetDataReader, T> readDelegate)
    {
        _packetProcessor.RegisterNestedType(writeDelegate, readDelegate);
    }

    public void UnregisterPacket<T>() where T : INetSerializable
    {
        if (!_packetProcessor.RemoveSubscription<T>())
        {
            _logger.LogError($"Failed to remove {typeof(T).Name} from the packet subscription list");
        }
    }

    public void UnregisterNetReusable<T>() where T : INetReusable
    {
        if (!_packetProcessor.RemoveSubscription<T>())
        {
            _logger.LogError($"Failed to remove {typeof(T).Name} from the packet subscription list");
        }
    }

    public void PrintStatistics()
    {
        _logger.LogInfo("..:: Fika Client Session Statistics ::..");
        _logger.LogInfo($"Sent packets: {_netClient.Statistics.PacketsSent}");
        _logger.LogInfo($"Sent data: {FikaGlobals.FormatFileSize(_netClient.Statistics.BytesSent)}");
        _logger.LogInfo($"Received packets: {_netClient.Statistics.PacketsReceived}");
        _logger.LogInfo($"Received data: {FikaGlobals.FormatFileSize(_netClient.Statistics.BytesReceived)}");
        _logger.LogInfo($"Packet loss: {_netClient.Statistics.PacketLossPercent}%");
        _logger.LogInfo($"Time in raid: {_startTime - DateTime.Now:hh\\h\\ mm\\m\\ ss\\s}");
    }

    public NetPeer GetPeerById(int id)
    {
        return (NetPeer)_netClient.GetPeerById(id);
    }

    private void HandleInventoryPacket(InventoryPacket packet, FikaPlayer player)
    {
        if (packet.Descriptor == null)
        {
            _logger.LogError("HandleInventoryPacket::Descriptor was null!");
            return;
        }

        var controller = player.InventoryController;
        if (controller != null)
        {
            try
            {
                if (controller is IOperationHandler networkController)
                {
                    var result = networkController.CreateOperationFromDescriptor(packet.Descriptor);
                    if (!result.Succeeded)
                    {
                        // Interpolating the wrapper as System.Object loses the native
                        // error's overridden ToString and only prints its CLR type.
                        _logger.LogError($"HandleInventoryPacket::Unable to process descriptor from netId {packet.NetId}, " +
                            $"operation: {packet.Descriptor.GetIl2CppType().FullName} #{packet.Descriptor.OperationId}, " +
                            $"descriptor: {packet.Descriptor.ToString()}, error: {result.Error?.ToString()}");
                        return;
                    }

                    _inventoryOperations.Enqueue(result.Value);
                    HandleInventoryOperations();
                }
                else
                {
                    _logger.LogError($"Player {player.Profile.GetCorrectedNickname()} is not allowed to process inventory operations. Operation: {packet.Descriptor.GetType().Name}");
                }
            }
            catch (Exception exception)
            {
                _logger.LogError($"HandleInventoryPacket::Exception thrown: {exception}");
            }
        }
        else
        {
            _logger.LogError("HandleInventoryPacket: inventory was null!");
        }
    }

    private void HandleResult(IResult result)
    {
        if (result.Failed)
        {
            _logger.LogError($"Error in operation: {result.Error}");
        }
    }
}
