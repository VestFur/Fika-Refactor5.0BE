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
/// 用于与 <see cref="FikaServer"/> 通信的客户端
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
    // 限制客户端为主机的 raid 战利品描述符持续轮询的时长上限，
    // 超过之后它就接受一个空 world。
    private const int MaxEmptyLootDescriptors = 30;
    private int _emptyLootDescriptors;
    private Queue<EFT.InventoryLogic.Operations.AbstractOperation> _inventoryOperations;
    /// <summary>
    /// 在本机没有可用于施加的 world 期间到达的 transit 事件。
    /// <para>
    /// 每个条目都携带它入队时所属的地图状态代次（FikaClient.MapState），因为该
    /// 队列现在会在 world 换挡中存活，而不再被 <c>ClearMapState</c> 清空：一次 transit 重载
    /// 会在下一个 world 存在之前很久就把 world 拆掉，而在该窗口内到达的事件
    /// 属于正在被创建的那场 raid。更早代次的条目会在换挡时以及每次 flush 时被丢弃
    /// （analysis/transit-reload-sync），因此上一场 raid 的任何东西都不会被重放。
    /// </para>
    /// </summary>
    private readonly Queue<(TransitEventPacket Packet, int Generation)> _pendingTransitEvents = new();

    // ------------------------------------------------------------------ world 报文队列
    // (analysis/loading-screen-sync)
    //
    // world 状态报文（WorldPacket：布娃娃、手雷、火炮、同步对象、战利品同步）
    // 会被施加到 Singleton<GameWorld>.Instance 上。transit 会替换那个 world：在换挡
    // 与新 world 创建之间该实例为 null，而每个这样的报文都会被丢弃，并每个报文
    // 一条错误行（2026-10-11 18:58 那次客户端运行中有 36 条，就在 transit 之后）。
    // 下面的队列把该窗口内的报文以*字节*形式保存 —— 接收路径每次接收都交给它同一个
    // WorldPacket 实例（NetPacketProcessor.SubscribeReusable 只保留一个引用），
    // 所以报文对象本身绝不可能被保留 —— 并在 world 存在之后，通过同一个报文
    // 处理器重放它们。
    //
    // 刻意从三个方面加了上限：
    //   * 只在 transit 之后的那次 world 换挡中布防（ArmWorldPacketQueue）；在该窗口之外
    //     仍保留旧行为（丢弃 + 聚合错误），因为在没有 raid 正在加载时到达的 world 报文
    //     属于上一场 raid，绝不能被施加到下一场上；
    //   * 最多 MaxPendingWorldPackets 个条目 —— 更长的突发会被截断，先丢最旧的；
    //   * 每个条目在 WorldPacketQueueMaxAgeSeconds 后过期；过期条目会被丢弃，而不是
    //     被重放，因此陈旧的 world 对象无法被复活到新的 world 里。
    internal const int MaxPendingWorldPackets = 256;
    private const float WorldPacketQueueMaxAgeSeconds = 15f;

    /// <summary>下面 transit 启动事件队列的上限（与 world 报文队列相同的上限）。</summary>
    internal const int MaxPendingTransitEvents = 256;

    private sealed class PendingWorldPacket
    {
        internal byte[] Payload;
        internal float QueuedAt;
        internal string Origin;
    }

    private readonly Queue<PendingWorldPacket> _pendingWorldPackets = new();
    private bool _worldPacketQueueArmed;
    private int _worldPacketsDropped;
    private float _worldPacketDropReportedAt;

    /// <summary>本会话中主机已发送了多少个 InformationPacket 应答（见 OnInformationPacketReceived）。</summary>
    public int HostResponseCount { get; private set; }

    // ------------------------------------------------------------------ transit 保留计数
    // (analysis/transit-reload-oom)
    //
    // 本对象会有意地在 transit 中存活下来（见 NetManagerUtils.DestroyNetManager），因此它拥有的每个列表、
    // 队列与缓存都随之存活。这四项计数器就是 [MemDiag] 行所打印的内容，
    // 因此下一次运行能显示一次换挡是否留下了旧 raid 的状态：
    //   * 换挡刚结束时 observedPlayers 必须为 0（被替换 world 的玩家都已不在，
    //     该列表在 CoopHandler.CleanUpForTransit 中被清空）；
    //   * worldQueue 上限为 MaxPendingWorldPackets 个原始字节条目，每个至多已存在 15 秒；
    //   * transitQueue 上限为 MaxPendingTransitEvents 个条目；
    //   * inventoryOps 必须在一帧内排空 —— 跨越换挡仍非零就意味着一个
    //     旧 raid 的操作即将被施加到新 raid 上。

    /// <summary>生成代码当前为网络状态更新而保留的玩家数量。</summary>
    internal int ObservedPlayerCount => ObservedPlayers?.Count ?? 0;

    /// <summary>一次 transit 换挡的 world 报文队列此刻持有的条目数。</summary>
    internal int PendingWorldPacketCount => _pendingWorldPackets.Count;

    /// <summary>正在等待本机 transit 控制器的 transit 启动事件数。</summary>
    internal int PendingTransitEventCount => _pendingTransitEvents.Count;

    /// <summary>仍在运行的那场 raid 中已排队的库存操作数。</summary>
    internal int PendingInventoryOperationCount => _inventoryOperations?.Count ?? 0;

    /// <summary>计一次主机应答；加载等待用其增量来区分慢主机与死掉的主机。</summary>
    internal void NoteHostResponse()
    {
        HostResponseCount++;
    }

    /// <summary>
    /// 为 transit 之后的那次 world 换挡打开 world 报文队列。调用点位于
    /// transit 路径上的 NetManagerUtils.DestroyNetManager，也就是本客户端的 world
    /// 被拆除、下一个 world 即将被创建的那一刻。
    /// </summary>
    internal void ArmWorldPacketQueue(string reason)
    {
        _pendingWorldPackets.Clear();
        _worldPacketQueueArmed = true;
    }

    /// <summary>
    /// 为下一个 world 保存一个 world 报文。当队列未布防（或该报文放不下）时返回 false，
    /// 此时调用方保持它当前的行为。
    /// </summary>
    internal bool TryQueueWorldPacket(ref WorldPacket packet, string origin)
    {
        if (!_worldPacketQueueArmed)
        {
            return false;
        }

        try
        {
            if (packet is null)
            {
                return true;
            }

            var writer = new NetDataWriter();
            _packetProcessor.Write(writer, packet);
            var payload = writer.CopyData();
            if (payload == null || payload.Length == 0)
            {
                return false;
            }

            while (_pendingWorldPackets.Count >= MaxPendingWorldPackets)
            {
                var evicted = _pendingWorldPackets.Dequeue();
                _worldPacketsDropped++;
                LoadSync.Warn("FikaClient.TryQueueWorldPacket",
                    "the world packet queue is full (" + MaxPendingWorldPackets + "): dropped the oldest entry of "
                    + evicted.Origin + " => the world it carried is lost, raise the cap only with a reason");
            }

            _pendingWorldPackets.Enqueue(new PendingWorldPacket
            {
                Payload = payload,
                QueuedAt = UnityEngine.Time.realtimeSinceStartup,
                Origin = origin
            });
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError($"Could not queue a world packet ({origin}): {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 在 world 存在之后重放已排队的 world 报文，丢弃已过期的条目，并用一行日志
    /// 说明此事。由 Update 驱动，即主线程每帧一次。
    /// </summary>
    private void FlushPendingWorldPackets()
    {
        if (_pendingWorldPackets.Count == 0)
        {
            if (!LoadSync.WorldReady)
            {
                return;
            }

            if (_worldPacketQueueArmed)
            {
                _worldPacketQueueArmed = false;
            }

            return;
        }

        var now = UnityEngine.Time.realtimeSinceStartup;
        var worldReady = LoadSync.WorldReady;
        if (!worldReady && now - _pendingWorldPackets.Peek().QueuedAt < WorldPacketQueueMaxAgeSeconds)
        {
            // 仍在换挡窗口内：保留这些条目，直到 world 存在或它们过期。
            return;
        }

        var replayed = 0;
        var expired = 0;
        while (_pendingWorldPackets.Count > 0)
        {
            var entry = _pendingWorldPackets.Dequeue();
            if (!worldReady || now - entry.QueuedAt > WorldPacketQueueMaxAgeSeconds)
            {
                expired++;
                continue;
            }

            try
            {
                _packetProcessor.ReadPacket(new NetDataReader(entry.Payload), null);
                replayed++;
            }
            catch (Exception exception)
            {
                _logger.LogError($"Could not replay a queued world packet ({entry.Origin}): {exception.Message}");
            }
        }

        _worldPacketQueueArmed = false;
    }

    /// <summary>
    /// 对逐报文 "GameWorld was null!" 错误的聚合替代：每秒一行，
    /// 带上计数，这样突发仍然可见，又不会刷爆游戏日志。
    /// </summary>
    internal void ReportDroppedWorldPacket(string origin)
    {
        _worldPacketsDropped++;
        var now = UnityEngine.Time.realtimeSinceStartup;
        if (now - _worldPacketDropReportedAt < 1f)
        {
            return;
        }

        _worldPacketDropReportedAt = now;
        LoadSync.Warn("FikaClient.ReportDroppedWorldPacket",
            "world packet dropped without a world: " + _worldPacketsDropped + " packet(s) so far, last origin="
            + origin + ", queueArmed=" + _worldPacketQueueArmed + " => "
            + (FikaBackendUtils.IsTransit
                ? "this is a transit swap: the packets belong to the raid that is being replaced"
                : "no raid is being loaded, the packets belong to a previous raid and must not be applied"));
    }
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

        // (analysis/stutter-frame-skip) 预算在客户端启动时读取：0 表示保持无上限地排空
        // 每个已到达事件，正值则表示本帧最多投递多少个。
        _netClient.MaxEventsPerPoll = FikaPlugin.Instance.Settings.NetPollBudget.Value;
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
        var connectString = FikaProtocol.ConnectionKey(FikaBackendUtils.IsScavLateJoin);

        // 主机端点由 ping 握手提交（FikaPingingClient.CommitEndpoint）。
        // 它是唯一设置该端点的地方，因此这里为 null 就意味着握手从未完成。
        // 在那个 null 上读 endPoint.Address 以前会表现为那个通用的
        // "Object reference not set to an instance of an object." 错误对话框，而让该方法
        // 继续执行会让 raid 在一个从未连接上的客户端下启动（这正是
        // 让 Fika 加载界面停在 0 % 的原因）。改为带原因地失败。
        if (endPoint == null)
        {
            
            throw new InvalidOperationException(
                "The host address was never resolved (the ping handshake did not complete), so the raid cannot be joined.");
        }

        if (endPoint.Address == null)
        {
            
            throw new InvalidOperationException("The host address was empty when the raid was joined.");
        }

        
        ServerConnection = _netClient.Connect(endPoint, connectString);
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
        RegisterPacket<SpawnSyncObjectPacket>(OnSpawnSyncObjectPacketReceived);
        RegisterPacket<BTRInteractionPacket>(OnBTRInteractionPacketReceived);
        RegisterPacket<FlareSuccessPacket>(OnFlareSuccessPacketReceived);
        RegisterPacket<BufferZonePacket>(OnBufferZonePacketReceived);
        RegisterPacket<SentryAccessPacket>(OnSentryAccessPacketReceived);
        RegisterPacket<ResyncInventoryIdPacket>(OnResyncInventoryIdPacketReceived);
        RegisterPacket<InventoryResyncPacket>(OnInventoryResyncPacketReceived);
        RegisterPacket<NetworkSettingsPacket>(OnNetworkSettingsPacketReceived);
        RegisterPacket<SyncTransitControllersPacket>(OnSyncTransitControllersPacketReceived);
        TransitZonePresence.RegisterOnClient(this);
        FlareExfilActivationPacket.RegisterOnClient(this);
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
        
        ReportPollBurst();

        
        ApplyPendingRaidInteractions();
        

        
        ApplyPendingMapState();
        

        
        FlushPendingTransitEvents();
        

        
        FlushPendingWorldPackets();
        

        // (analysis/loading-screen-peer-state) 本机加载行与进度的有界重新宣告
        // （见 LoadingScreenUI.ArmPeerStateHeartbeat）；
        LoadingScreenUI.TickPeerStateHeartbeat();
        

        var networkTime = NetworkTimeSync.NetworkTime;
        // (analysis/perf-multiplayer) 本帧仅合作模式才有的那一半拥有自己的槽位，外加为其付账的
        // 玩家数量。这段遍历只存在于客户端上：主机遍历它自己的 ObservedPlayers
        // （其他客户端，一两个），而单人 raid 一个都没有，因此那里的槽位显示 0 —— 这正是
        // 本探针所要回答的"多人到底每帧多花了多少"那个数字。该列表
        // 只取一次到本地变量里：遍历过程中没有任何东西替换它，循环体也保持不变。
        var observed = ObservedPlayers;
        var observedCount = observed.Count;
        
        
        for (var i = 0; i < observedCount; i++)
        {
            observed[i].ManualStateUpdate(networkTime);
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

    /// <summary>
    /// (analysis/stutter-frame-skip) 投递了一波网络事件的帧会每两秒被点名一次。
    /// 每个 peer 的每个已到达事件的投递都发生在本帧的
    /// <c>PollEvents</c> 调用内 —— 包含报文解析与回调，因为本管理器没有逻辑线程
    /// （见 <c>LiteNetManager.PollEvents</c>）—— 而 2026-10-13 20:01 那场 raid 测得包含它的
    /// 那一步在其 103 个 raid 内窗口中的 101 个里为 76-306 ms。该行把"一个大帧"变成"本帧
    /// 投递了 N 个事件，迄今最差为 M"。绝不抛异常。
    /// </summary>
    private void ReportPollBurst()
    {
        try
        {
            var client = _netClient;
            if (client == null)
            {
                return;
            }

            var delivered = client.LastPollEventCount;
            if (delivered < PollBurstReportThreshold)
            {
                return;
            }

            _pollBursts++;
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextPollBurstReport)
            {
                return;
            }

            _nextPollBurstReport = now + 2f;
            FikaGlobals.LogWarning("[NetPoll] one frame delivered " + delivered + " network event(s) in a row"
                + " (bursts=" + _pollBursts + ", max=" + client.MaxPollEventCount
                + ", deferredTotal=" + client.DeferredEventCount + ", budget=" + client.MaxEventsPerPoll
                + ") => every arrived event of every peer is parsed and applied inside that frame;"
                + " a budget above 0 spreads the next burst over the following frames");
        }
        catch (Exception)
        {
        }
    }

    /// <summary>一帧可以投递多少个事件之后这波突发就值得记一行（见 <see cref="ReportPollBurst"/>）。</summary>
    private const int PollBurstReportThreshold = 48;

    private float _nextPollBurstReport;
    private int _pollBursts;

    private void QueueTransitEvent(TransitEventPacket packet)
    {
        if (packet == null) return;
        if (_pendingTransitEvents.Count >= MaxPendingTransitEvents)
        {
            _logger.LogError("Transit startup event queue exceeded its limit");
            return;
        }

        // 在本机控制器被创建期间，保留一个独立的报文外壳。
        // 事件载荷本身是一个刚被反序列化出来的
        // IL2CPP 对象，并被控制器恰好消费一次。
        NoteTransitEventQueued(packet.EventType);
        _pendingTransitEvents.Enqueue((new TransitEventPacket
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
        }, _mapStateGeneration));
    }

    private void FlushPendingTransitEvents()
    {
        var world = Singleton<GameWorld>.Instance;
        if (FikaWorldControllers.Find<Fika.Core.Main.ClientClasses.ClientTransitController>(world?.TransitController) is not { } transit) return;
        while (_pendingTransitEvents.TryDequeue(out var entry))
        {
            if (!IsTransitEventGenerationCurrent(entry.Generation))
            {
                DropStaleTransitEvent(entry.Packet, entry.Generation, "the flush of the new raid");
                continue;
            }

            var packet = entry.Packet;
            if (packet.EventType == TransitEventPacket.ETransitEventType.Init) transit.ReceiveSnapshot(packet);
            else transit.ReceiveEvent(packet);
        }
        transit.ProcessPendingInitialization();
    }

    /// <summary>
    /// 处理已排队的 <see cref="EFT.InventoryLogic.Operations.AbstractOperation"/>
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
        _pendingWorldPackets.Clear();
        _worldPacketQueueArmed = false;
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
    /// 发送一个可复用报文
    /// </summary>
    /// <typeparam name="T">要发送的 <see cref="IReusable"/></typeparam>
    /// <param name="packet">要发送的 <see cref="INetSerializable"/></param>
    /// <param name="deliveryMethod">投递方式</param>
    /// <remarks>
    /// 从客户端发送时，Reusable 的类型始终是 broadcast
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
        // 每个入站报文一个时间戳，供 [LoadSync] 行使用（"sinceLastInbound"）：在客户端上它
        // 把"主机主线程正忙"（传输层存活、没有报文）与"主机在应答"区分开来。
        LoadSync.NotePeerPacket("client receive");

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
            // 这是正常的 LAN 路径。peer 同样必须被缓存：
            // SendData 只面向 ServerConnection，并会在它为 null 时静默丢弃每个
            // 客户端报文（撤离请求、库存操作、拾取），
            // 这会把客户端困在加载界面上，并在它
            // 一旦与战利品交互时卡死。
            ServerConnection = _netClient.Connect(remoteEndPoint, NetDataWriter.FromString(
                FikaProtocol.ConnectionKey(FikaBackendUtils.IsScavLateJoin)).AsReadOnlySpan());
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
            
            // 主机可能在本地玩家仍在加载时超时。即使还没有创建任何玩家/报文发送器，
            // 也要释放传输层。
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
                // 在旧 peer 的超时回调仍在运行时，可能已经创建了一个
                // 新客户端。不要清掉那个替代者。
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
                        // 把该包装器按 System.Object 插值会丢掉本机
                        // 错误被重写的 ToString，只打印出它的 CLR 类型。
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