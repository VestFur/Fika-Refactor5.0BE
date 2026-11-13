// © 2026 Lacyway All Rights Reserved

using BepInEx.Logging;
using Comfort.Common;
using Diz.Utils;
using EFT;
using EFT.Airdrop;
using EFT.Communications;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.Settings;
using EFT.SynchronizableObjects;
using EFT.Vehicle;
using Fika.Core.Main.Components;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Packets;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.Networking.VOIP;
using Fika.Core.UI.Custom;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
#if DEBUG
using Fika.Core.Networking.Packets.Debug;
# endif
using static Fika.Core.Networking.NetworkUtils;
using Fika.Core.Networking.Packets.Player;
using Fika.Core.Networking.Packets.World;
using Fika.Core.Networking.Packets.FirearmController;
using Fika.Core.Networking.Packets.Communication;
using Fika.Core.Networking.Pooling;
using Fika.Core.Networking.Open.Nat;
using Fika.Core.Networking.Open.Nat.Enums;
using Fika.Core.Networking.Models;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Player.Common;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Snapshotting;

namespace Fika.Core.Networking;

/// <summary>
/// Server used to synchronize all <see cref="FikaClient"/>s
/// </summary>
public sealed partial class FikaServer : MonoBehaviour, INetEventListener, INatPunchListener, IAirdropDataSender, IFikaNetworkManager
{
    public FikaServer(System.IntPtr pointer) : base(pointer) { }


    public FikaServer() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaServer>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public int ReadyClients;
    public DateTime TimeSinceLastPeerDisconnected;
    // Invalidates delayed empty-peer shutdowns after a reconnect or a newer
    // disconnect, so an old wait cannot stop a later raid.
    private int _headlessWaitGeneration;
    public bool HasHadPeer;
    public bool RaidInitialized;
    public bool HostReady;
    public bool LocationReceived;
    public FikaHostWorld FikaHostWorld { get; set; }
    public bool Started
    {
        get
        {
            return _netServer?.IsRunning == true;
        }
    }
    public DateTime? GameStartTime
    {
        get
        {
            if (_gameStartTime == null)
            {
                _gameStartTime = DateTimeExtensions.UtcNow.ToManaged();
            }
            return _gameStartTime;
        }
        set
        {
            _gameStartTime = value;
        }
    }
    public NetManager NetServer
    {
        get
        {
            return _netServer;
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
    /// <summary>
    /// Max MTU used for <see cref="DeliveryMethod.Unreliable"/> batching
    /// </summary>
    public int MaxMTU { get; private set; }

    public int NetId { get; set; }
    public ESideType RaidSide { get; set; }
    public bool AllowVOIP { get; set; }
    public List<ObservedPlayer> ObservedPlayers { get; set; }
    public int PlayerAmount { get; set; }

    private int _sendRate;
    private NetPacketProcessor _packetProcessor;
    private FikaPlayer _hostPlayer;
    private string _externalIp;
    private NetManager _netServer;
    private DateTime? _gameStartTime;
    private NetDataWriter _dataWriter;
    private ushort _port;
    private CoopHandler _coopHandler;
    private ManualLogSource _logger;
    private int _currentNetId;
    private FikaChatUIScript _fikaChat;
    private RaidAdminUIScript _raidAdminUIScript;
    private CancellationTokenSource _cts;
    private float _statisticsCounter;
    private float _sendThreshold;
    private Dictionary<Profile, bool> _visualProfiles;
    private Dictionary<string, int> _cachedConnections;
    private Dictionary<NetPeer, int> _assignedPeerNetIds;
    private Dictionary<NetPeer, string> _assignedPeerProfiles;
    /// <summary>Peers that entered a raid that was already running, as player scavs.</summary>
    internal readonly HashSet<NetPeer> _scavLateJoinPeers = [];
    private HashSet<NetPeer> _readyPeers;
    private GenericPacket _genericPacket;
    private DateTime _startTime;
    private InventoryOperationHandlerPool _inventoryOperationHandlerPool;

    internal FikaVOIPServer VOIPServer { get; set; }
    internal FikaVOIPClient VOIPClient { get; set; }

    public async Task Init()
    {
        // A new host network manager may be created for a subsequent Transit
        // raid while an old empty-peer delay is still awaiting its timeout.
        // Advance the generation before any asynchronous setup so that delay
        // can never stop this new raid.
        System.Threading.Interlocked.Increment(ref _headlessWaitGeneration);
        _netServer = new(this)
        {
            BroadcastReceiveEnabled = true,
            UnconnectedMessagesEnabled = true,
            UpdateTime = 50,
            AutoRecycle = true,
            DisconnectTimeout = FikaPlugin.Instance.Settings.ConnectionTimeout.Value * 1000,
            EnableStatistics = true,
            NatPunchEnabled = true,
            IPv6Enabled = FikaPlugin.Instance.Settings.UseIPv6.Value,
            ChannelsCount = 2,
            UseNativeSockets = NativeSocket.IsSupported
        };

        AllowVOIP = FikaPlugin.Instance.Settings.AllowVOIP.Value;

        _packetProcessor = new();
        _dataWriter = new();
        _externalIp = NetUtils.GetLocalIp(LocalAddrType.IPv4);
        _statisticsCounter = 0f;
        _sendThreshold = 2f;
        _cachedConnections = [];
        _assignedPeerNetIds = [];
        _assignedPeerProfiles = [];
        _readyPeers = [];
        _logger = Logger.CreateLogSource("Fika.Server");
        _startTime = DateTime.Now;
        _inventoryOperationHandlerPool = new InventoryOperationHandlerPool(8, InventoryOperationHandler.CreateInstance);
        BotInventoryOperationHandlerPool.Create();
        ObservedPlayers = [];
        PlayerAmount = 1;
        StrictInventorySync = FikaPlugin.Instance.Settings.StrictInventorySync.Value;

        ResetReadyClients();

        TemporaryStash = Singleton<ItemFactory>.Instance.CreateFakeStash(new Il2CppSystem.Nullable<MongoID>());

        MaxMTU = NetConstants.PossibleMtu[0] - NetConstants.HeaderSize; // we assume minimum MTU + unreliable header size

        TimeSinceLastPeerDisconnected = DateTime.Now.AddDays(1);

        _visualProfiles = [];
        if (!FikaBackendUtils.IsHeadless)
        {
            var ownProfile = FikaGlobals.GetLiteProfile(FikaBackendUtils.IsScav);
            if (ownProfile != null)
            {
                _visualProfiles.Add(ownProfile, true);
            }
            else
            {
                _logger.LogError("Init: Own profile was null!");
            }
        }

        _sendRate = FikaPlugin.Instance.Settings.SendRate.Value.ToNumber();
        _logger.LogInfo($"Starting server with SendRate: {_sendRate}");
        _port = FikaPlugin.Instance.Settings.UDPPort.Value;

        NetworkGameSession.Rtt = 0;
        NetworkGameSession.LossPercent = 0;

        _currentNetId = 2;
        NetId = 1;
        _genericPacket = new()
        {
            NetId = NetId
        };

        await RegisterPacketsAndTypes();
        var pmcName = FikaBackendUtils.IsHeadless ? "Headless" : FikaBackendUtils.PMCName;
        LoadingScreenUI.Instance.AddPlayer(NetId, pmcName);

#if DEBUG
        AddDebugPackets();
#endif
        await NetManagerUtils.CreateCoopHandler();

        var portMapped = false;

        if (FikaPlugin.Instance.Settings.UseUPnP.Value)
        {
            try
            {
                NatDiscoverer discoverer = new();
                CancellationTokenSource cts = new(10000);
                var device = await discoverer.DiscoverDeviceAsync(PortMapper.Upnp | PortMapper.Pmp, cts);
                var extIp = await device.GetExternalIPAsync();
                _externalIp = extIp.MapToIPv4().ToString();

                await device.CreatePortMapAsync(new Mapping(Protocol.Udp, _port, _port, 300, "Fika UDP"));

                _logger.LogInfo("Port Mapping Succeeded");
                portMapped = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Port Mapping Failed: {ex.Message}");
            }
        }
        else if (FikaPlugin.Instance.Settings.ForceIP.Value != "")
        {
            _externalIp = FikaPlugin.Instance.Settings.ForceIP.Value;
        }
        else
        {
            if (FikaPlugin.Instance.WanIP == null)
            {
                FikaNotifications.DisplayMessageNotification("No WAN IP could be found, external players will not be able to join",
                    iconType: EFT.Communications.ENotificationIconType.Alert, textColor: new Il2CppSystem.Nullable<Color>(Color.red));
            }

            else
            {
                _externalIp = FikaPlugin.Instance.WanIP.ToString();
            }
        }

        var useNATPunching = !portMapped && FikaPlugin.Instance.Settings.UseNATPunching.Value &&
            (FikaPlugin.Instance.NatPunchServerEnable || FikaPlugin.Instance.Settings.UseFikaNATPunchServer.Value);

        if (useNATPunching)
        {
            _logger.LogInfo("NAT Punching Enabled");

            _netServer.NatPunchModule.UnsyncedEvents = true;
            _netServer.NatPunchModule.Init(this);
            _netServer.Start();

            _cts = new CancellationTokenSource();

            var natPunchServerIP = FikaPlugin.Instance.NatPunchServerIP;
            var natPunchServerPort = FikaPlugin.Instance.NatPunchServerPort;

            if (FikaPlugin.Instance.Settings.UseFikaNATPunchServer.Value)
            {
                natPunchServerIP = FikaPlugin.FikaNATPunchMasterServer;
                natPunchServerPort = FikaPlugin.FikaNATPunchMasterPort;
            }

            var token = $"Server:{FikaBackendUtils.ServerGuid}";
            var natIntroduceTask = Task.Run(() => NatIntroduceTask(natPunchServerIP, natPunchServerPort, token, _cts.Token));
        }
        else
        {
            if (FikaPlugin.Instance.Settings.ForceBindIP.Value != "Disabled")
            {
                if (!IPAddress.TryParse(FikaPlugin.Instance.Settings.ForceBindIP.Value, out var bindAddress))
                {
                    var error = $"{FikaPlugin.Instance.Settings.ForceBindIP.Value} could not be parsed into a valid IP, raid will not start";
                    NotificationManager.DisplayWarningNotification(
                        error,
                        EFT.Communications.ENotificationDurationType.Long);

                    throw new ParseException(error);
                }

                switch (bindAddress.AddressFamily)
                {
                    case AddressFamily.InterNetwork: // IPv4
                        _logger.LogInfo("Hosting on IPv4");
                        _netServer.Start(bindAddress, IPAddress.IPv6Any, _port);
                        break;
                    case AddressFamily.InterNetworkV6: // IPv6
                        _logger.LogInfo("Hosting on IPv4 and IPv6 (IPv6 bind address)");
                        _netServer.Start(IPAddress.Any, bindAddress, _port);
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported address family: {bindAddress.AddressFamily}");
                }
            }
            else
            {
                _logger.LogInfo(FikaPlugin.Instance.Settings.UseIPv6.Value
                    ? "Hosting on IPv4 and IPv6 (dual stack)"
                    : "Hosting on IPv4 only (IPv6 disabled)");
                _netServer.Start(_port);
            }
        }

        NetManagerUtils.ApplySimulationSettings(_netServer);

        _logger.LogInfo("Started Fika Server");

        FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.SERVER_STARTED.Localized(), _port),
            EFT.Communications.ENotificationDurationType.Default, EFT.Communications.ENotificationIconType.EntryPoint);

        var useIPv6 = FikaPlugin.Instance.Settings.UseIPv6.Value;
        List<string> ipAddresses = [_externalIp];
        for (var i = 0; i < FikaPlugin.Instance.LocalIPs.Length; i++)
        {
            var ip = FikaPlugin.Instance.LocalIPs[i];
            if (!useIPv6 && IPAddress.TryParse(ip, out var parsedAddress)
                && parsedAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                continue;
            }
            if (ValidateIP(ip) && !ipAddresses.Contains(ip))
            {
                ipAddresses.Add(ip);
            }
        }

        if (ipAddresses.Count < 1)
        {
            ipAddresses = [_externalIp, ""];
            FikaNotifications.DisplayMessageNotification(LocaleUtils.NO_VALID_IP.Localized(),
                iconType: EFT.Communications.ENotificationIconType.Alert);
        }

        SetHostRequest body = new([.. ipAddresses], _port, useNATPunching,
            FikaPlugin.Instance.Settings.UseFikaNATPunchServer.Value, FikaBackendUtils.IsHeadless);
        FikaRequestHandler.UpdateSetHost(body);

        if (!FikaBackendUtils.IsHeadless)
        {
            _raidAdminUIScript = RaidAdminUIScript.Create(this, _netServer);
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

        RegisterPacket<InformationPacket, NetPeer>(OnInformationPacketReceived);
        RegisterPacket<TextMessagePacket, NetPeer>(OnTextMessagePacketReceived);
        RegisterPacket<QuestConditionPacket, NetPeer>(OnQuestConditionPacketReceived);
        RegisterPacket<QuestItemPacket, NetPeer>(OnQuestItemPacketReceived);
        RegisterPacket<QuestDropItemPacket, NetPeer>(OnQuestDropItemPacketReceived);
        RegisterPacket<InteractableInitPacket, NetPeer>(OnInteractableInitPacketReceived);
        RegisterPacket<WorldLootPacket, NetPeer>(OnWorldLootPacketReceived);
        RegisterPacket<ReconnectPacket, NetPeer>(OnReconnectPacketReceived);
        RegisterPacket<BTRInteractionPacket, NetPeer>(OnBTRInteractionPacketReceived);
        RegisterPacket<ResyncInventoryIdPacket, NetPeer>(OnResyncInventoryIdPacketReceived);
        RegisterPacket<SyncTransitControllersPacket, NetPeer>(OnSyncTransitControllersPacketReceived);
        RegisterPacket<TransitInteractPacket, NetPeer>(OnTransitInteractPacketReceived);
        RegisterPacket<BotStatePacket, NetPeer>(OnBotStatePacketReceived);
        RegisterPacket<LoadingProfilePacket, NetPeer>(OnLoadingProfilePacketReceived);
        RegisterPacket<SideEffectPacket, NetPeer>(OnSideEffectPacketReceived);
        RegisterPacket<RequestPacket, NetPeer>(OnRequestPacketReceived);
        RegisterPacket<NetworkSettingsPacket, NetPeer>(OnNetworkSettingsPacketReceived);
        RegisterPacket<InRaidQuestPacket, NetPeer>(OnInraidQuestPacketReceived);
        RegisterPacket<EventControllerInteractPacket, NetPeer>(OnEventControllerInteractPacketReceived);
        RegisterPacket<LoadingScreenPacket, NetPeer>(OnLoadingScreenPacketReceived);
        RegisterPacket<LoadingScreenPlayersPacket, NetPeer>(OnLoadingScreenPlayersPacketReceived);
        RegisterPacket<ClearSnapshotterPacket, NetPeer>(OnClearSnapshotterPacketReceived);
        RegisterPacket<ProceedRequestPacket, NetPeer>(OnProceedRequestPacketReceived);
        RegisterPacket<KnifeHitPacket, NetPeer>(OnKnifeHitPacketReceived);
        RegisterPacket<QuestSyncPacket, NetPeer>(OnQuestSyncPacketReceived);
        RegisterPacket<SpawnItemInInventoryPacket, NetPeer>(SpawnItemInInventoryPacketReceived);
        RegisterPacket<PasscodeInitPacket, NetPeer>(OnPasscodeInitPacketReceived);
        RegisterPacket<PasscodeAttemptPacket, NetPeer>(OnPasscodeAttemptPacketReceived);
        RegisterPacket<CutsceneSkipIntentionPacket, NetPeer>(OnCutsceneSkipIntentionPacketReceived);
        RegisterPacket<CutsceneViewCompletePacket, NetPeer>(OnCutsceneViewCompletePacketReceived);
        RegisterPacket<FinalMissionPacket, NetPeer>(OnFinalMissionPacketReceived);
        RegisterPacket<FinalMissionQuestPacket, NetPeer>(OnFinalMissionQuestPacketReceived);

        RegisterReusable<WorldPacket, NetPeer>(OnWorldPacketReceived);

        RegisterNetReusable<WeaponPacket, NetPeer>(OnWeaponPacketReceived);
        RegisterNetReusable<CommonPlayerPacket, NetPeer>(OnCommonPlayerPacketReceived);
        RegisterNetReusable<GenericPacket, NetPeer>(OnGenericPacketReceived);

        return Task.CompletedTask;
    }

#if DEBUG
    private void AddDebugPackets()
    {
        RegisterPacket<SpawnItemPacket, NetPeer>(OnSpawnItemPacketReceived);
        RegisterPacket<CommandPacket, NetPeer>(OnCommandPacketReceived);
    }
#endif

    public void SendAirdropContainerData(EAirdropType containerType, Item item, int ObjectId)
    {
        MainThreadDispatcher.RunAsync(() => SendAirdropWhenReady(containerType, item, ObjectId)).Forget();
    }

    public void SendFlareSuccessEvent(string profileId, bool canSendAirdrop)
    {
        FlareSuccessPacket packet = new(profileId, canSendAirdrop);
        SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    public int PopNetId()
    {
        var netId = _currentNetId;
        _currentNetId++;

        return netId;
    }

    public void SetupGameVariables(FikaPlayer fikaPlayer)
    {
        _hostPlayer = fikaPlayer;
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
        _netServer?.PollEvents();

        var networkTime = NetworkTimeSync.NetworkTime;
        for (var i = 0; i < ObservedPlayers.Count; i++)
        {
            ObservedPlayers[i].ManualStateUpdate(networkTime);
        }

        _statisticsCounter += Time.unscaledDeltaTime;
        if (_statisticsCounter > _sendThreshold)
        {
            _statisticsCounter -= _sendThreshold;
            SendStatisticsPacket();
        }

        if (!FikaBackendUtils.IsHeadless && Input.GetKeyDown(FikaPlugin.Instance.Settings.ChatKey.Value.MainKey))
        {
            if (_fikaChat != null)
            {
                _fikaChat.ToggleChat();
            }
        }
    }

    private void SendStatisticsPacket()
    {
        if (_netServer == null)
        {
            return;
        }

        var fps = Mathf.CeilToInt(1f / Time.unscaledDeltaTime);
        StatisticsPacket packet = new(fps);

        SendData(ref packet, DeliveryMethod.Unreliable);
    }

    private void OnDestroy()
    {
        System.Threading.Interlocked.Increment(ref _headlessWaitGeneration);
        ShutdownVOIP();
        _netServer?.Stop();
        _genericPacket.Clear();

        PoolUtils.ReleaseAll();

        if (_fikaChat != null)
        {
            Destroy(_fikaChat);
        }
        if (_raidAdminUIScript != null)
        {
            Destroy(_raidAdminUIScript);
        }

        BotInventoryOperationHandlerPool.Clear();

        FikaEventDispatcher.DispatchEvent(new FikaNetworkManagerDestroyedEvent(this));
    }

    public void SendData<T>(ref T packet, DeliveryMethod deliveryMethod, bool broadcast = false) where T : INetSerializable
    {
        _dataWriter.Reset();
        _dataWriter.PutEnum(EPacketType.Serializable);

        _packetProcessor.WriteNetSerializable(_dataWriter, ref packet);
        _netServer.SendToAll(_dataWriter.AsReadOnlySpan(), deliveryMethod);
    }

    public void SendGenericPacket(EGenericSubPacketType type, IPoolSubPacket subpacket, bool broadcast = false, NetPeer peerToIgnore = null)
    {
        var packet = _genericPacket;
        packet.Type = type;
        packet.SubPacket = subpacket;
        SendNetReusable(ref packet, DeliveryMethod.ReliableOrdered, broadcast, peerToIgnore);
    }

    public void SendGenericPacketToPeer(EGenericSubPacketType type, IPoolSubPacket subpacket, NetPeer peer)
    {
        var packet = _genericPacket;
        packet.Type = type;
        packet.SubPacket = subpacket;
        SendNetReusableToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
    }

    internal byte[] CreateInventoryRelayPayload(InventoryPacket inventoryPacket)
    {
        var packet = new GenericPacket
        {
            NetId = NetId,
            Type = EGenericSubPacketType.InventoryOperation,
            SubPacket = inventoryPacket
        };
        var writer = new NetDataWriter();
        writer.PutEnum(EPacketType.Serializable);
        _packetProcessor.WriteNetReusable(writer, ref packet);
        // Do not clear packet: inventoryPacket is the receive path's object.
        return writer.CopyData();
    }

    internal void RelayInventoryOperation(byte[] payload, NetPeer source)
    {
        if (payload == null) return;
        _netServer.SendToAll(payload, DeliveryMethod.ReliableOrdered, source);
    }

    public void SendNetReusable<T>(ref T packet, DeliveryMethod deliveryMethod, bool broadcast = false, NetPeer peerToIgnore = null) where T : INetReusable
    {
        try
        {
            _dataWriter.Reset();
            _dataWriter.PutEnum(EPacketType.Serializable);

            _packetProcessor.WriteNetReusable(_dataWriter, ref packet);
            _netServer.SendToAll(_dataWriter.AsReadOnlySpan(), deliveryMethod, peerToIgnore);
        }
        finally
        {
            packet.Clear();
        }
    }

    public void SendNetReusableToPeer<T>(ref T packet, DeliveryMethod deliveryMethod, NetPeer peer) where T : INetReusable
    {
        try
        {
            _dataWriter.Reset();
            _dataWriter.PutEnum(EPacketType.Serializable);

            _packetProcessor.WriteNetReusable(_dataWriter, ref packet);
            peer.Send(_dataWriter.AsReadOnlySpan(), deliveryMethod);
        }
        finally
        {
            packet.Clear();
        }
    }

    public void SendData<T>(ref T packet, DeliveryMethod deliveryMethod, NetPeer peerToIgnore) where T : INetSerializable
    {
        _dataWriter.Reset();
        _dataWriter.PutEnum(EPacketType.Serializable);

        _packetProcessor.WriteNetSerializable(_dataWriter, ref packet);
        _netServer.SendToAll(_dataWriter.AsReadOnlySpan(), deliveryMethod, peerToIgnore);
    }

    public void SendPlayerState(ref PlayerStateData packet)
    {
        _dataWriter.Reset();
        _dataWriter.PutEnum(EPacketType.PlayerState);
        _dataWriter.Put(NetworkTimeSync.NetworkTime);
        _dataWriter.PutUnmanaged(packet);

        _netServer.SendToAll(_dataWriter.AsReadOnlySpan(), DeliveryMethod.Unreliable);
    }

    public void BatchSendStates(NetDataWriter writer)
    {
        _netServer.SendToAll(writer.AsReadOnlySpan(), DeliveryMethod.Unreliable);
    }

    public void SendDataToPeer<T>(ref T packet, DeliveryMethod deliveryMethod, NetPeer peer) where T : INetSerializable
    {
        _dataWriter.Reset();
        _dataWriter.PutEnum(EPacketType.Serializable);

        _packetProcessor.WriteNetSerializable(_dataWriter, ref packet);
        peer.Send(_dataWriter.AsReadOnlySpan(), deliveryMethod);
    }

    public void SendInventoryResyncToPeer(int netId, ushort callbackId, NetPeer peer)
    {
        if (_coopHandler?.Players.TryGetValue(netId, out var player) != true || player?.InventoryController == null)
        {
            _logger.LogError($"Unable to create inventory resync for net id {netId}: player or inventory was null.");
            return;
        }

        InventoryResyncPacket packet = new()
        {
            NetId = netId,
            CallbackId = callbackId,
            Descriptor = new InventoryDescriptor(player.InventoryController.Inventory,
                FikaGlobals.SearchControllerSerializer)
        };
        SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
    }

    public void SendReusableToAll<T>(T packet, DeliveryMethod deliveryMethod, NetPeer peerToExlude = null) where T : class, IReusable, new()
    {
        _dataWriter.Reset();

        _dataWriter.PutEnum(EPacketType.Serializable);
        _packetProcessor.Write(_dataWriter, packet);
        if (peerToExlude != null)
        {
            _netServer.SendToAll(_dataWriter.AsReadOnlySpan(), deliveryMethod, peerToExlude);
        }
        else
        {
            _netServer.SendToAll(_dataWriter.AsReadOnlySpan(), deliveryMethod);
        }

        packet.Flush();
    }

    public void SendVOIPData(ArraySegment<byte> data, DeliveryMethod deliveryMethod, NetPeer peer = null)
    {
        if (peer == null)
        {
            _logger.LogError("SendVOIPData: peer was null!");
            return;
        }

        _dataWriter.Reset();
        _dataWriter.PutEnum(EPacketType.VOIP);
        _dataWriter.Put(data.AsSpan());
        peer.Send(_dataWriter.AsReadOnlySpan(), deliveryMethod);
    }

    public void SendBTRPacket(ref ShapshotBTRMessage data)
    {
        _dataWriter.Reset();
        _dataWriter.PutEnum(EPacketType.BTR);
        _dataWriter.PutUnmanaged(data);

        _netServer.SendToAll(_dataWriter.AsReadOnlySpan(), DeliveryMethod.Unreliable);
    }

    public void SendStashes()
    {
        if (Singleton<GameWorld>.Instantiated)
        {
            StashesPacket stashesPacket = new();
            var gameWorld = Singleton<GameWorld>.Instance;

            if (gameWorld.BtrController?.TransferItemsController != null)
            {
                stashesPacket.HasBTR = true;
                var length = gameWorld.BtrController.TransferItemsController._transferContainers.Count;
                stashesPacket.BTRStashes = new Stash[length];
                for (var i = 0; i < length; i++)
                {
                    stashesPacket.BTRStashes[i] = gameWorld.BtrController.TransferItemsController._transferContainers[i];
                }
            }

            if (gameWorld.TransitController?.TransferItemsController != null)
            {
                stashesPacket.HasTransit = true;
                var length = gameWorld.TransitController.TransferItemsController._transferContainers.Count;
                stashesPacket.TransitStashes = new Stash[length];
                for (var i = 0; i < length; i++)
                {
                    stashesPacket.TransitStashes[i] = gameWorld.TransitController.TransferItemsController._transferContainers[i];
                }
            }

            SendData(ref stashesPacket, DeliveryMethod.ReliableOrdered, true);
        }
    }

    public void OnPeerConnected(NetPeer peer)
    {
        System.Threading.Interlocked.Increment(ref _headlessWaitGeneration);
        FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.PEER_CONNECTED.Localized(), peer.Port),
            iconType: ENotificationIconType.Friend);
        _logger.LogInfo($"Connection established with {peer.Address}:{peer.Port}, id: {peer.Id}");

        HasHadPeer = true;
        PlayerAmount++;

        FikaEventDispatcher.DispatchEvent(new PeerConnectedEvent(peer, this));

        SendGenericPacket(EGenericSubPacketType.UpdateBackendData,
            UpdateBackendData.FromValue(PlayerAmount), true);

        var loadingPacket = LoadingScreenUI.Instance.GetPlayersPacket();
        SendDataToPeer(ref loadingPacket, DeliveryMethod.ReliableUnordered, peer);

        // force update
        var updatePackets = LoadingScreenUI.Instance.GetLatestStates();
        for (var i = 0; i < updatePackets.Length; i++)
        {
            var updatePacket = updatePackets[i];
            SendDataToPeer(ref updatePacket, DeliveryMethod.ReliableUnordered, peer);
        }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketErrorCode)
    {
        _logger.LogError("[SERVER] error " + socketErrorCode);
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        var started = false;
        if (_coopHandler != null && _coopHandler.LocalGameInstance != null && FikaSingleton<IFikaGame>.Instance.GameController.RaidStarted)
        {
            started = true;
        }

        if (messageType == UnconnectedMessageType.Broadcast)
        {
            _logger.LogInfo("[SERVER] Received discovery request. Send discovery response");
            NetDataWriter resp = new();
            resp.Put(1);
            _netServer.SendUnconnectedMessage(resp.AsReadOnlySpan(), remoteEndPoint);

            return;
        }

        if (reader.TryGetString(out var data))
        {
            NetDataWriter resp = new();
            var guid = FikaBackendUtils.ServerGuid.ToString();
            if (data == guid)
            {
                var reconnect = reader.GetBool();
                resp.Put(started && !reconnect ? "fika.inprogress" : "fika.hello");
                _netServer.SendUnconnectedMessage(resp.AsReadOnlySpan(), remoteEndPoint);
            }
            else
            {
                _logger.LogError($"PingingRequest::Data was not as expected: {data}");
                resp.Put("fika.reject");
                _netServer.SendUnconnectedMessage(resp.AsReadOnlySpan(), remoteEndPoint);
            }
        }
        else
        {
            _logger.LogError("PingingRequest: Could not parse string");
        }
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {

    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        var raidStarted = _coopHandler != null && _coopHandler.LocalGameInstance != null &&
            FikaSingleton<IFikaGame>.Instance.GameController.RaidStarted;
        if (FikaProtocol.TryReadConnection(request.Data, raidStarted, out var rejection, out var scavLateJoin))
        {
            var accepted = request.Accept();
            if (scavLateJoin && accepted != null)
            {
                // SPT regenerates the player scav profile when its raid starts, so
                // this peer announces a different profile id than the one it
                // connected with. Remember it and rebind instead of rejecting the
                // character, which would leave the host without a player for it.
                _scavLateJoinPeers.Add(accepted);
                _logger.LogInfo($"Accepted a player scav into the running raid ({accepted.Address})");
            }

            return;
        }
        _dataWriter.Reset();
        _dataWriter.Put(rejection);
        request.Reject(_dataWriter);
    }
    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        // A disconnected client no longer satisfies the pre-raid ready barrier.
        if (_readyPeers.Remove(peer) && ReadyClients > 0) ReadyClients--;
        _assignedPeerNetIds.Remove(peer);
        _assignedPeerProfiles.Remove(peer);
        _scavLateJoinPeers.Remove(peer);
        RaidInteractionSync.OnDisconnected(peer.Player);
        _logger.LogInfo($"Peer disconnected {peer.Port}, info: {disconnectInfo.Reason}");
        if (disconnectInfo.Reason != DisconnectReason.RemoteConnectionClose)
        {
            FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.PEER_DISCONNECTED.Localized(), [peer.Port, disconnectInfo.Reason]),
                    iconType: EFT.Communications.ENotificationIconType.Alert);
        }

        if (peer.Tag is string nickname)
        {
            var disconnectPacket = ClientDisconnected.FromValue(nickname);
            disconnectPacket.Execute();
            SendGenericPacket(EGenericSubPacketType.ClientDisconnected,
                disconnectPacket, true, peer);
        }

        PlayerAmount--;
        SendGenericPacket(EGenericSubPacketType.UpdateBackendData,
            UpdateBackendData.FromValue(PlayerAmount), true);

        if (_netServer.ConnectedPeersCount == 0)
        {
            TimeSinceLastPeerDisconnected = DateTime.Now;
        }

        if (FikaBackendUtils.IsHeadless)
        {
            if (_netServer.ConnectedPeersCount == 0)
            {
                var waitGeneration = System.Threading.Interlocked.Increment(ref _headlessWaitGeneration);
                if (disconnectInfo.Reason != DisconnectReason.RemoteConnectionClose)
                {
                    _ = Task.Run(() => WaitBeforeStopping(waitGeneration));
                }
                else
                {
                    AsyncWorker.RunInMainTread((Il2CppSystem.Action)(() => DisconnectHeadless(waitGeneration)));
                }
            }
        }

        FikaEventDispatcher.DispatchEvent(new PeerDisconnectedEvent(peer, this));
    }

    private async Task WaitBeforeStopping(int waitGeneration)
    {
        for (var minutes = 0; minutes < 5; minutes++)
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            if (waitGeneration != System.Threading.Interlocked.CompareExchange(ref _headlessWaitGeneration, 0, 0) ||
                _netServer.ConnectedPeersCount != 0)
                return;
            _logger.LogInfo($"Waited {minutes + 1} minutes for reconnect...");
        }

        if (waitGeneration == System.Threading.Interlocked.CompareExchange(ref _headlessWaitGeneration, 0, 0) &&
            _netServer.ConnectedPeersCount == 0)
        {
            _logger.LogInfo("No reconnect was made, stopping session");
            // Connection state can change between queueing and execution on
            // the Unity thread, so validate the same wait generation there.
            AsyncWorker.RunInMainTread((Il2CppSystem.Action)(() => DisconnectHeadless(waitGeneration)));
        }
    }

    private void DisconnectHeadless(int waitGeneration)
    {
        if (waitGeneration != System.Threading.Interlocked.CompareExchange(ref _headlessWaitGeneration, 0, 0) ||
            _netServer.ConnectedPeersCount != 0)
            return;

        if (!FikaSingleton<IFikaGame>.Instantiated)
        {
            _logger.LogError("Headless is trying to disconnect when there is no FikaGame started, stopping client...");
            Application.Quit();
            return;
        }

        FikaSingleton<IFikaGame>.Instance.Stop(null, ExitStatus.Survived, "");
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        var broadcast = reader.GetByte() == 1;
        var payloadStart = reader.Position;
        var packetType = reader.GetEnum<EPacketType>();
        switch (packetType)
        {
            case EPacketType.Serializable:
                // Inspect each message in a batch. A peer cannot turn a final
                // mission request into a host outcome by setting broadcast=true.
                while (reader.AvailableBytes > 0)
                {
                    var messageStart = reader.Position;
                    var id = reader.PeekUShort();
                    var relay = broadcast && id != NetPacketProcessor.PacketId<FinalMissionPacket>() &&
                        id != NetPacketProcessor.PacketId<FinalMissionQuestPacket>() &&
                        id != NetPacketProcessor.PacketId<InRaidQuestPacket>() &&
                        id != NetPacketProcessor.PacketId<TextMessagePacket>() &&
                        // Loading UI updates are rebroadcast by the host only
                        // after checking the peer's assigned player ID.
                        id != NetPacketProcessor.PacketId<LoadingScreenPlayersPacket>() &&
                        id != NetPacketProcessor.PacketId<LoadingScreenPacket>() &&
                        // Clients only report that an observed bot has loaded.
                        // Bot lifecycle changes originate on the host.
                        id != NetPacketProcessor.PacketId<BotStatePacket>() &&
                        // The host computes and broadcasts the authoritative
                        // interaction result. A client must not relay a forged
                        // IsResponse packet before its callback rejects it.
                        id != NetPacketProcessor.PacketId<BTRInteractionPacket>();
                    if (id == NetPacketProcessor.PacketId<WorldPacket>() && peer.Player == null)
                        // The host callback drops unbound loot updates. Do not
                        // relay those raw bytes to the other clients either.
                        relay = false;
                    if (id == NetPacketProcessor.PacketId<QuestConditionPacket>() ||
                        id == NetPacketProcessor.PacketId<QuestItemPacket>() ||
                        id == NetPacketProcessor.PacketId<QuestDropItemPacket>())
                        // The host validates the sender's NetId, applies the
                        // event, then forwards the canonicalized packet.
                        relay = false;
                    if (id == NetPacketProcessor.PacketId<WeaponPacket>())
                    {
                        reader.GetUShort();
                        relay = relay && peer.Player != null && reader.GetInt() == peer.Player.NetId;
                        reader.SetPosition(messageStart);
                    }
                    if (id == NetPacketProcessor.PacketId<ClearSnapshotterPacket>())
                    {
                        reader.GetUShort();
                        relay = relay && peer.Player != null && reader.GetInt() == peer.Player.NetId;
                        reader.SetPosition(messageStart);
                    }
                    if (id == NetPacketProcessor.PacketId<GenericPacket>())
                    {
                        reader.GetUShort();
                        var netId = reader.GetInt();
                        var subType = reader.GetEnum<EGenericSubPacketType>();
                        if (!_assignedPeerNetIds.TryGetValue(peer, out var assignedNetId) || netId != assignedNetId ||
                            IsHostOnlyGenericPacket(subType) || subType == EGenericSubPacketType.SendCharacter)
                            relay = false;
                        else if (subType == EGenericSubPacketType.ClientExtract)
                            relay = relay && peer.Player != null && reader.GetInt() == peer.Player.NetId;
                        else if (subType == EGenericSubPacketType.MuffledState)
                            relay = relay && peer.Player != null && reader.GetInt() == peer.Player.NetId;
                        else if (subType == EGenericSubPacketType.InventoryOperation)
                            // Inventory mutations are relayed by the host only
                            // after the native operation reports success.
                            relay = false;
                        else if (subType == EGenericSubPacketType.DialogEntry)
                            // Dialog entry packets are applied and forwarded by
                            // the host callback after validating the sender.
                            relay = false;
                        else if (subType == EGenericSubPacketType.FlareShootReport)
                            // Flare reports are validated and re-broadcast as the
                            // host's canonical activation by the host callback.
                            relay = false;
                        reader.SetPosition(messageStart);
                    }
                    if (id == NetPacketProcessor.PacketId<CommonPlayerPacket>())
                    {
                        reader.GetUShort();
                        var netId = reader.GetInt();
                        var subType = reader.GetEnum<ECommonSubPacketType>();
                        relay = relay && CanPeerSendCommonPlayerPacket(peer, netId, subType);
                        reader.SetPosition(messageStart);
                    }
                    _packetProcessor.ReadPacket(reader, peer);
                    if (!relay) continue;
                    _dataWriter.Reset();
                    _dataWriter.PutEnum(EPacketType.Serializable);
                    _dataWriter.Put(reader.RawData.AsSpan(messageStart, reader.Position - messageStart));
                    _netServer.SendToAll(_dataWriter.AsReadOnlySpan(), deliveryMethod, peer);
                }
                break;
            case EPacketType.PlayerState:
                var remoteTime = reader.GetDouble();
                var localTime = NetworkTimeSync.NetworkTime;
                var snapshot = reader.GetUnmanaged<PlayerStateData>();
                if (peer.Player == null || snapshot.NetId != peer.Player.NetId)
                {
                    break;
                }

                if (!_coopHandler.Players.TryGetValue(snapshot.NetId, out var player)) break;
                var header = new PlayerStateSnapshot(in snapshot, remoteTime, localTime);
                player.Snapshotter.AddSnapshot(in header);
                if (broadcast)
                {
                    // Client state datagrams contain one snapshot. Relay only
                    // the parsed frame: trailing bytes must not become extra
                    // unverified player snapshots on receiving clients.
                    _netServer.SendToAll(reader.RawData.AsSpan(payloadStart, reader.Position - payloadStart), deliveryMethod, peer);
                }
                break;
            case EPacketType.VOIP:
                if (VOIPServer != null)
                {
                    VOIPServer.NetworkReceivedPacket(new(new RemotePeer(peer)),
                        reader.GetRemainingBytesSegment().ToNative());
                }
                break;
        }
    }

    private static bool IsHostOnlyGenericPacket(EGenericSubPacketType type) => type is
        EGenericSubPacketType.ClientConnected or
        EGenericSubPacketType.ClientDisconnected or
        EGenericSubPacketType.UpdateBackendData or
        EGenericSubPacketType.BorderZone or
        EGenericSubPacketType.Mine or
        EGenericSubPacketType.SpawnBTR or
        EGenericSubPacketType.CharacterSync or
        EGenericSubPacketType.OperationCallback or
        EGenericSubPacketType.SyncableItem or
        EGenericSubPacketType.SpawnAI;

    private bool CanPeerSendCommonPlayerPacket(NetPeer peer, int netId, ECommonSubPacketType type)
    {
        if (peer.Player == null) return false;
        if (peer.Player.NetId == netId) return true;

        // Hits and revives address the affected player, who can differ from
        // the sender. All other player actions must target the sender.
        return type is ECommonSubPacketType.Damage or ECommonSubPacketType.ArmorDamage or
            ECommonSubPacketType.RevivingPlayer or ECommonSubPacketType.RevivedPlayer;
    }

    internal void ResetReadyClients()
    {
        ReadyClients = 0;
        _readyPeers?.Clear();
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
        _logger.LogInfo("..:: Fika Server Session Statistics ::..");
        _logger.LogInfo($"Sent packets: {_netServer.Statistics.PacketsSent}");
        _logger.LogInfo($"Sent data: {FikaGlobals.FormatFileSize(_netServer.Statistics.BytesSent)}");
        _logger.LogInfo($"Received packets: {_netServer.Statistics.PacketsReceived}");
        _logger.LogInfo($"Received data: {FikaGlobals.FormatFileSize(_netServer.Statistics.BytesReceived)}");
        _logger.LogInfo($"Packet loss: {_netServer.Statistics.PacketLossPercent}%");
        _logger.LogInfo($"Time in raid: {_startTime - DateTime.Now:hh\\h\\ mm\\m\\ ss\\s}");
    }

    public void ToggleAdminUI()
    {
        if (_raidAdminUIScript != null)
        {
            _raidAdminUIScript.Toggle();
        }
    }

    // NAT Punching
    public void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
    {
        // Do nothing
    }

    public void OnNatIntroductionSuccess(IPEndPoint targetEndPoint, NatAddressType type, string token)
    {
        /*_logger.LogInfo($"Received NAT introduction from master server for [{targetEndPoint}]: {token}");
        var message = token.Split(":")[1];
        if (!Guid.TryParse(message, out var id))
        {
            Console.WriteLine($"Could not parse token: {message}");
        }

        if (id != FikaBackendUtils.ServerGuid)
        {
            Console.WriteLine($"Incorrect GUID: {id}");
        }

        _logger.LogInfo($"Received endpoint {targetEndPoint} from the NAT punching server.");*/

        // todo: proper connect
        //_netServer.Connect(targetEndPoint, id.ToString());
    }

    public NetPeer GetPeerById(int id)
    {
        return (NetPeer)_netServer.GetPeerById(id);
    }

    public void StopNatIntroduceRoutine()
    {
        _cts?.Cancel();
    }

    private async Task NatIntroduceTask(string natPunchServerIP, int natPunchServerPort, string token, CancellationToken ct = default)
    {
        _logger.LogInfo("Send NAT Introduce Request task started.");

        while (!ct.IsCancellationRequested)
        {
            _netServer.NatPunchModule.SendNatIntroduceRequest(natPunchServerIP, natPunchServerPort, token);

            await Task.Delay(TimeSpan.FromSeconds(15));
        }

        _logger.LogInfo("Send NAT Introduce Request task stopped.");
    }

    /// <summary>
    /// Gets a pooled handler
    /// </summary>
    /// <returns>A pooled handler</returns>
    public InventoryOperationHandler GetHandler()
    {
        return _inventoryOperationHandlerPool.Get();
    }

    /// <summary>
    /// Returns a pooled handler
    /// </summary>
    /// <param name="handler">The handler to return</param>
    public void ReturnHandler(InventoryOperationHandler handler)
    {
        _inventoryOperationHandlerPool.ReturnHandler(handler);
    }
}
