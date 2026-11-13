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
    /// <summary>
    /// Transit events that arrived while this machine had no world to apply them to.
    /// <para>
    /// Each entry carries the map-state generation it was queued in (FikaClient.MapState), because the
    /// queue now survives a world swap instead of being wiped by <c>ClearMapState</c>: a transit reload
    /// tears the world down long before the next one exists, and an event that arrives in that window
    /// belongs to the raid that is being created. Entries of an older generation are dropped at the swap
    /// and at every flush (analysis/transit-reload-sync), so nothing from a previous raid is replayed.
    /// </para>
    /// </summary>
    private readonly Queue<(TransitEventPacket Packet, int Generation)> _pendingTransitEvents = new();

    // ------------------------------------------------------------------ world packet queue
    // (analysis/loading-screen-sync)
    //
    // The world state packets (WorldPacket: ragdolls, grenades, artillery, sync objects, loot sync)
    // are applied to Singleton<GameWorld>.Instance. A transit replaces that world: between the swap
    // and the new world's creation the instance is null, and every such packet was dropped with one
    // error line per packet (36 of them in the 2026-10-11 18:58 client run, right after the transit).
    // The queue below holds the packets of that window as *bytes* - the receive path hands the same
    // WorldPacket instance to every receive (NetPacketProcessor.SubscribeReusable keeps one reference),
    // so the packet object itself can never be retained - and replays them through the same packet
    // processor once the world exists.
    //
    // Bounded on purpose, in three ways:
    //   * armed only for the world swap after a transit (ArmWorldPacketQueue); outside that window the
    //     old behaviour (drop + aggregated error) stays, because a world packet that arrives while no
    //     raid is being loaded belongs to the previous raid and must never be applied to the next one;
    //   * at most MaxPendingWorldPackets entries - a longer burst is truncated, oldest first;
    //   * every entry expires after WorldPacketQueueMaxAgeSeconds; an expired entry is dropped, not
    //     replayed, so stale world objects cannot be resurrected into a fresh world.
    internal const int MaxPendingWorldPackets = 256;
    private const float WorldPacketQueueMaxAgeSeconds = 15f;

    /// <summary>Cap of the transit startup event queue below (same bound as the world packet queue).</summary>
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

    /// <summary>How many InformationPacket responses the host has sent for this session (see OnInformationPacketReceived).</summary>
    public int HostResponseCount { get; private set; }

    // ------------------------------------------------------------------ transit retention counters
    // (analysis/transit-reload-oom)
    //
    // This object survives a transit on purpose (see NetManagerUtils.DestroyNetManager), so every list,
    // queue and cache it owns survives with it. These four counters are what the [MemDiag] line prints,
    // so the next run shows whether a swap left old-raid state behind:
    //   * observedPlayers must be 0 right after the swap (the players of the replaced world are gone and
    //     the list is cleared in CoopHandler.CleanUpForTransit);
    //   * worldQueue is capped at MaxPendingWorldPackets entries of raw bytes, each 15 s old at most;
    //   * transitQueue is capped at MaxPendingTransitEvents entries;
    //   * inventoryOps must drain within a frame - a non-zero value across the swap would mean an
    //     operation of the old raid is about to be executed against the new one.

    /// <summary>Number of players the spawning code currently keeps for network state updates.</summary>
    internal int ObservedPlayerCount => ObservedPlayers?.Count ?? 0;

    /// <summary>Entries the world packet queue of a transit swap holds right now.</summary>
    internal int PendingWorldPacketCount => _pendingWorldPackets.Count;

    /// <summary>Transit startup events waiting for a native transit controller.</summary>
    internal int PendingTransitEventCount => _pendingTransitEvents.Count;

    /// <summary>Queued inventory operations of the raid that is still running.</summary>
    internal int PendingInventoryOperationCount => _inventoryOperations?.Count ?? 0;

    /// <summary>Counts one host response; the loading wait uses the delta to tell a slow host from a dead one.</summary>
    internal void NoteHostResponse()
    {
        HostResponseCount++;
    }

    /// <summary>
    /// Opens the world packet queue for the world swap that follows a transit. Called from
    /// NetManagerUtils.DestroyNetManager on the transit path, i.e. at the moment this client's world
    /// is torn down and the next one is about to be created.
    /// </summary>
    internal void ArmWorldPacketQueue(string reason)
    {
        _pendingWorldPackets.Clear();
        _worldPacketQueueArmed = true;
        LoadSync.Report("FikaClient.ArmWorldPacketQueue",
            "world packet queue armed (" + reason + "); at most " + MaxPendingWorldPackets
            + " packet(s), each expires after " + WorldPacketQueueMaxAgeSeconds + "s => "
            + "world packets of the swap window are replayed once the new world exists instead of being dropped");
    }

    /// <summary>
    /// Keeps one world packet for the next world. Returns false when the queue is not armed (or the
    /// packet does not fit), in which case the caller keeps its current behaviour.
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
    /// Replays the queued world packets once a world exists, drops the expired ones, and says so in one
    /// line. Driven from Update, i.e. once per frame on the main thread.
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
                LoadSync.Report("FikaClient.FlushPendingWorldPackets",
                    "the world packet queue is closed (the world is up, nothing had to be replayed) => "
                    + "the world swap did not lose any world packet");
            }

            return;
        }

        var now = UnityEngine.Time.realtimeSinceStartup;
        var worldReady = LoadSync.WorldReady;
        if (!worldReady && now - _pendingWorldPackets.Peek().QueuedAt < WorldPacketQueueMaxAgeSeconds)
        {
            // Still inside the swap window: keep the entries until the world exists or they expire.
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
        LoadSync.Report("FikaClient.FlushPendingWorldPackets",
            "world packet queue drained: replayed " + replayed + ", dropped " + expired
            + " (world=" + (worldReady ? "up" : "still missing") + ", maxAge=" + WorldPacketQueueMaxAgeSeconds
            + "s) => the replayed packets are applied to this world only, the dropped ones belonged to an older one");
    }

    /// <summary>
    /// The aggregated replacement for the per-packet "GameWorld was null!" error: one line per second
    /// with the count, so a burst stays visible without flooding the game log.
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

        // (analysis/stutter-frame-skip) The budget is read when a client starts: 0 keeps the unbounded drain
        // of every arrived event, a positive value bounds how many of them this frame delivers.
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

        // The host endpoint is committed by the ping handshake (FikaPingingClient.CommitEndpoint).
        // It is the only thing that sets it, so a null here means the handshake never finished.
        // Reading endPoint.Address on that null used to surface as the generic
        // "Object reference not set to an instance of an object." error box, and letting the
        // method continue left the raid starting with a client that was never connected (which
        // is what leaves the Fika loading screen at 0 %). Fail with a reason instead.
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
        

        // (analysis/loading-screen-peer-state) The bounded re-announcement of this machine's loading row and
        // progress (see LoadingScreenUI.ArmPeerStateHeartbeat); 
        LoadingScreenUI.TickPeerStateHeartbeat();
        

        var networkTime = NetworkTimeSync.NetworkTime;
        // (analysis/perf-multiplayer) The co-op-only half of this frame gets its own slot plus the player
        // count that paid for it. This walk exists only on a client: a host iterates its own ObservedPlayers
        // (other clients, one or two) and a solo raid has none at all, so the slot reads 0 there - which is
        // exactly the "what does multiplayer add per frame" number this probe is asked for. The list is
        // taken once into a local: nothing in the walk replaces it, and the loop body is unchanged.
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
    /// (analysis/stutter-frame-skip) A frame that delivered a burst of network events is named once per two
    /// seconds. The delivery of every arrived event of every peer happens inside this frame's
    /// <c>PollEvents</c> call - packet parse and callback included, because this manager has no logic thread
    /// (see <c>LiteNetManager.PollEvents</c>) - and the 2026-10-13 20:01 raid measured the step that contains
    /// it at 76-306 ms in 101 of its 103 in-raid windows. The line turns "a big frame" into "this frame
    /// delivered N events, the worst so far is M". Never throws.
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

    /// <summary>Events one frame may deliver before the burst is worth a line (see <see cref="ReportPollBurst"/>).</summary>
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

        // Keep an independent packet shell while the native controller is
        // being created. The event payload itself is a freshly deserialized
        // IL2CPP object and is consumed exactly once by the controller.
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
        // One stamp per inbound packet, for the [LoadSync] lines ("sinceLastInbound"): on the client this
        // separates "the host's main thread is busy" (transport alive, no packets) from "the host answers".
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
            // This is the normal LAN path. The peer has to be cached as well:
            // SendData only targets ServerConnection and silently drops every
            // client packet (exfiltration requests, inventory operations, pickups)
            // while it is null, which strands the client on the loading screen and
            // freezes it as soon as it interacts with loot.
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
            
            // The host can time out while the local player is still loading. Release the transport even
            // when no player/packet sender has been created yet.
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