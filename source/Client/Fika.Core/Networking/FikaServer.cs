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
/// 用于同步所有 <see cref="FikaClient"/> 的服务器
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
    /// 用于 <see cref="DeliveryMethod.Unreliable"/> 批处理的最大 MTU
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
    /// <summary>在本场战局已经开打之后才加入、以玩家 scav 身份入场的对等端。</summary>
    internal readonly HashSet<NetPeer> _scavLateJoinPeers = [];
    private HashSet<NetPeer> _readyPeers;
    private GenericPacket _genericPacket;
    private DateTime _startTime;
    private InventoryOperationHandlerPool _inventoryOperationHandlerPool;

    internal FikaVOIPServer VOIPServer { get; set; }
    internal FikaVOIPClient VOIPClient { get; set; }

    public async Task Init()
    {
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

        MaxMTU = NetConstants.PossibleMtu[0] - NetConstants.HeaderSize; // 假设为最小 MTU + 不可靠头部大小

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

        // 只有非空白的地址才允许进入这个列表。上游曾用 [_externalIp] 初始化它，随后又用
        // [_externalIp, ""] "修复"空的情况，而这正是把一条 null/空白条目放进
        // 已发布记录的原因：主机报告 "IPs: "（没有任何可用地址），而实际上可达的
        // 地址就摆在旁边没被读取。外部地址保持在首位，并且每个
        // 合法的本地地址仍会被追加（上游行为，未作改动：局域网列表始终
        // 会被发布，而 NAT 打洞是同一条记录上的一个独立标志）。
        List<string> ipAddresses = string.IsNullOrWhiteSpace(_externalIp) ? [] : [_externalIp];
        for (var i = 0; i < FikaPlugin.Instance.LocalIPs.Length; i++)
        {
            var ip = FikaPlugin.Instance.LocalIPs[i];
            if (string.IsNullOrWhiteSpace(ip) || ipAddresses.Contains(ip))
            {
                continue;
            }
            if (!useIPv6 && IPAddress.TryParse(ip, out var parsedAddress)
                && parsedAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                continue;
            }
            if (ValidateIP(ip))
            {
                ipAddresses.Add(ip);
            }
        }

        // 现在可达，而且所言非虚：外部查询失败了，也没有任何本地地址
        // 通过 ValidateIP（禁用 IPv6 的纯 IPv6 主机，或者只有 loopback / APIPA /
        // 链路本地地址）。旧的守卫永远不会触发，因为列表用一个元素就已被初始化，
        // 所以 Count 始终至少为 1——而它的方法体又把本应修复的空白条目
        // 重新放了进来。无论哪种情况，记录都会在无空白条目的前提下被发布，而启用了 NAT
        // 打洞的战局仍然可以通过 NAT 介绍加入。
        if (ipAddresses.Count == 0)
        {
            FikaNotifications.DisplayMessageNotification(LocaleUtils.NO_VALID_IP.Localized(),
                iconType: EFT.Communications.ENotificationIconType.Alert);
        }

        SetHostRequest body = new([.. ipAddresses], _port, useNATPunching,
            FikaPlugin.Instance.Settings.UseFikaNATPunchServer.Value, FikaBackendUtils.IsHeadless);
        LogHostAddressPublication(body, ipAddresses);
        try
        {
            FikaRequestHandler.UpdateSetHost(body);
        }
        catch (Exception exception)
        {
            // 当这个请求失败时，该房间在服务器上始终不会被发布，所以每个加入者都会看到
            // "the host has not published an address on that server"，直到房间结束。这里留一行
            // 点明原因，然后让失败原样继续传给调用方。
            var reason = exception.GetBaseException().Message;
            _logger.LogWarning($"[HostAddress] publishing failed for serverId={body.ServerId} on {FikaRequestHandler.ServerHost}: "
                + $"{reason}; other players will not be able to join this raid");
            
            throw;
        }

        if (!FikaBackendUtils.IsHeadless)
        {
            _raidAdminUIScript = RaidAdminUIScript.Create(this, _netServer);
        }
    }

    /// <summary>
    /// 报告本主机即将为其战局发布的地址（每次战局开始一行）。
    ///
    /// 加入者只有在已发布记录带有可用地址与端口时才能连接，而加入方
    /// 没有办法说出记录为空的原因：它只能说 "the host has not
    /// published an address on that server"。这一行正是解释它的地方，因此它会点明产生该记录的
    /// 那些值，以及为它们提供输入的那些设置。
    /// </summary>
    private void LogHostAddressPublication(SetHostRequest body, List<string> ipAddresses)
    {
        var usable = 0;
        for (var i = 0; i < ipAddresses.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(ipAddresses[i]))
            {
                usable++;
            }
        }

        var values = $"ips=[{DescribeAddressList(ipAddresses)}], port={body.Port}, natPunch={body.NatPunch}, "
            + $"useFikaNatPunchServer={body.UseFikaNatPunchServer}, isHeadless={body.IsHeadless}";

        if (usable == 0 || body.Port == 0)
        {
            _logger.LogWarning($"[HostAddress] publishing no usable address ({values}); externalIp={_externalIp ?? "null"}, "
                + $"wanIp={FikaPlugin.Instance.WanIP}, forceIp='{FikaPlugin.Instance.Settings.ForceIP.Value}', "
                + $"upnp={FikaPlugin.Instance.Settings.UseUPnP.Value}: other players will not be able to join this raid");
            
            return;
        }

        _logger.LogInfo($"[HostAddress] publishing {values}");
    }

    private static string DescribeAddressList(List<string> addresses)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < addresses.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(string.IsNullOrWhiteSpace(addresses[i]) ? "<blank>" : addresses[i]);
        }

        return builder.ToString();
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
        RegisterPacket<BTRInteractionPacket, NetPeer>(OnBTRInteractionPacketReceived);
        RegisterPacket<ResyncInventoryIdPacket, NetPeer>(OnResyncInventoryIdPacketReceived);
        RegisterPacket<SyncTransitControllersPacket, NetPeer>(OnSyncTransitControllersPacketReceived);
        TransitZonePresence.RegisterOnServer(this);
        FlareExfilActivationPacket.RegisterOnServer(this);
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
        RegisterPacket<SentryAccessPacket, NetPeer>(OnSentryAccessPacketReceived);

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

        // （analysis/loading-screen-peer-state）本机加载行与进度的有界重新
        // 通告。它从这里被泵动，因为引擎只会进入 LoadingScreenUI 的 Awake/OnDestroy，
        // 而且除非一次中转重新武装了它，否则它什么也不做。
        LoadingScreenUI.TickPeerStateHeartbeat();

        var networkTime = NetworkTimeSync.NetworkTime;
        for (var i = 0; i < ObservedPlayers.Count; i++)
        {
            ObservedPlayers[i].ManualStateUpdate(networkTime);
        }

        // （2026-10-17）对等端 -> 玩家的绑定是每个请求处理器所信任的东西；这里会为
        // 丢失绑定的对等端每秒恢复一次（见 RelinkUnboundPeers）。
        RelinkUnboundPeers();

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


    private float _peerRelinkCounter;
    private readonly List<NetPeer> _relinkPeers = [];

    /// <summary>
    /// （2026-10-17）为每个绑定缺失、或指向本场战局已不再持有对象的已连接对等端
    /// 重新建立 <see cref="NetPeer.Player"/>。每个请求处理器（proceed、inventory、
    /// player state）都信任那一个字段，而它只在一个地方被写入——信息
    /// 交换。因此，在一次中转之后不再发送信息包的对等端，或其角色
    /// 通告被丢弃的对等端，会在剩余的战局中一直保持未绑定：它的换武器被
    /// 回滚为空手，它的物品移动从不被执行，它的 ObservedPlayer 在本主机上
    /// 也永不移动。该绑定只会被写入 CoopHandler.Players 中存在于该对等端
    /// 所分配 net id 之下的玩家，这里不做任何解绑，且该调用是幂等的。
    /// </summary>
    private void RelinkUnboundPeers()
    {
        try
        {
            _peerRelinkCounter += Time.unscaledDeltaTime;
            if (_peerRelinkCounter < 1f)
            {
                return;
            }

            _peerRelinkCounter = 0f;
            var coopHandler = _coopHandler;
            if (_netServer == null || coopHandler?.Players == null || _assignedPeerNetIds == null)
            {
                return;
            }

            _relinkPeers.Clear();
            _netServer.GetConnectedPeers(_relinkPeers);
            for (var i = 0; i < _relinkPeers.Count; i++)
            {
                var peer = _relinkPeers[i];
                if (!_assignedPeerNetIds.TryGetValue(peer, out var netId) || netId == 0)
                {
                    continue;
                }

                if (!coopHandler.Players.TryGetValue(netId, out var player) || player == null)
                {
                    continue;
                }

                if (_assignedPeerProfiles != null && _assignedPeerProfiles.TryGetValue(peer, out var profile)
                    && player.ProfileId != profile)
                {
                    continue;
                }

                var bound = peer.Player;
                if (bound != null && bound.Pointer == player.Pointer)
                {
                    continue;
                }

                peer.Player = player;
                _logger.LogWarning($"[PeerRelink] bound peer {peer.Id} to player {netId} ({player.ProfileId}); " +
                    $"previous={(bound == null ? "none" : bound.NetId + "/" + bound.ProfileId)}");
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning("[PeerRelink] failed: " + exception.Message);
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
        // 不要清空 packet：inventoryPacket 是接收路径的对象。
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

        // 强制更新
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
                resp.Put(started ? "fika.inprogress" : "fika.hello");
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
                // SPT 会在其战局开始时重新生成玩家 scav 的 profile，所以
                // 这个对等端通告的 profile id 与它连接时所用的那个
                // 不同。记住它并重新绑定，而不是拒绝该角色，
                // 那会让主机没有可供其使用的玩家。
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
        // 已断开的客户端不再满足战前准备屏障。
        if (_readyPeers.Remove(peer) && ReadyClients > 0) ReadyClients--;
        _scavLateJoinPeers.Remove(peer);

        // 该对等端彻底消失了：它的身份分配也随之而去，因此之后的任何数据包
        // 都不会被归属到一个已不存在的连接上。
        _assignedPeerProfiles.Remove(peer);
        _assignedPeerNetIds.Remove(peer);
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

        if (FikaBackendUtils.IsHeadless && _netServer.ConnectedPeersCount == 0)
        {
            // 最后一个客户端已经离开，也没什么可回来的了：无头会话随之停止。
            // （那五分钟的"等待重连"保活属于已被移除的功能。）
            AsyncWorker.RunInMainTread((System.Action)DisconnectHeadless);
        }

        FikaEventDispatcher.DispatchEvent(new PeerDisconnectedEvent(peer, this));
    }

    private void DisconnectHeadless()
    {
        if (_netServer.ConnectedPeersCount != 0)
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
        // 每个入站数据包一个时间戳，用于 [LoadSync] 屏障行（"sinceLastInbound"）：主机的
        // 屏障会等待一次角色通告，而它能把"该对等端是静默的"与
        // "该对等端在发包但尚未通告角色"区分开。
        LoadSync.NotePeerPacket("host receive");

        var broadcast = reader.GetByte() == 1;
        var payloadStart = reader.Position;
        var packetType = reader.GetEnum<EPacketType>();
        switch (packetType)
        {
            case EPacketType.Serializable:
                // 逐条检查一批中的每个消息。对等端无法通过设置 broadcast=true
                // 就把一个最终任务请求变成主机结果。
                while (reader.AvailableBytes > 0)
                {
                    var messageStart = reader.Position;
                    var id = reader.PeekUShort();
                    var relay = broadcast && id != NetPacketProcessor.PacketId<FinalMissionPacket>() &&
                        id != NetPacketProcessor.PacketId<FinalMissionQuestPacket>() &&
                        id != NetPacketProcessor.PacketId<InRaidQuestPacket>() &&
                        id != NetPacketProcessor.PacketId<TextMessagePacket>() &&
                        // 加载 UI 更新只有在主机检查过该对等端所分配的
                        // 玩家 ID 之后才会被主机重新广播。
                        id != NetPacketProcessor.PacketId<LoadingScreenPlayersPacket>() &&
                        id != NetPacketProcessor.PacketId<LoadingScreenPacket>() &&
                        // 客户端只报告某个被观察的 bot 已加载。
                        // Bot 的生命周期变更由主机发起。
                        id != NetPacketProcessor.PacketId<BotStatePacket>() &&
                        // 主机计算并广播权威的交互结果。
                        // 客户端不得在其回调拒绝之前转发一个
                        // 伪造的 IsResponse 数据包。
                        id != NetPacketProcessor.PacketId<BTRInteractionPacket>();
                    if (id == NetPacketProcessor.PacketId<WorldPacket>() && peer.Player == null)
                        // 主机回调会丢弃未绑定的战利品更新。也不要把这些
                        // 原始字节转发给其它客户端。
                        relay = false;
                    if (id == NetPacketProcessor.PacketId<QuestConditionPacket>() ||
                        id == NetPacketProcessor.PacketId<QuestItemPacket>() ||
                        id == NetPacketProcessor.PacketId<QuestDropItemPacket>())
                        // 主机会校验发送方的 NetId，应用该事件，
                        // 然后转发规范化后的数据包。
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
                            // 物品栏变更只有在原生操作报告成功之后
                            // 才会由主机转发。
                            relay = false;
                        else if (subType == EGenericSubPacketType.DialogEntry)
                            // 对话入口数据包由主机回调在校验发送方
                            // 之后应用并转发。
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
                    // 客户端状态数据报只包含一个快照。只转发
                    // 已解析的帧：尾部字节绝不能在接受端客户端上变成额外的
                    // 未经校验的玩家快照。
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

        // 命中和复活针对受影响的玩家，而该玩家可能与发送方
        // 不同。其它所有玩家动作必须以发送方为目标。
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

    // NAT 打洞
    public void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
    {
        // 什么也不做
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

        // todo: 正确的连接方式
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
    /// 获取一个池化的处理器
    /// </summary>
    /// <returns>一个池化的处理器</returns>
    public InventoryOperationHandler GetHandler()
    {
        return _inventoryOperationHandlerPool.Get();
    }

    /// <summary>
    /// 归还一个池化的处理器
    /// </summary>
    /// <param name="handler">要归还的处理器</param>
    public void ReturnHandler(InventoryOperationHandler handler)
    {
        _inventoryOperationHandlerPool.ReturnHandler(handler);
    }
}