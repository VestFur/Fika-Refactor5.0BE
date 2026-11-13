using Fika.Core.Main.BaseClasses;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BitPacking;
using Comfort.Common;
using CommonAssets.Scripts.Game;
using CommonAssets.Scripts.Game.LabyrinthEvent;
using EFT;
using EFT.AssetsManager;
using EFT.Bots;
using EFT.Communications;
using EFT.Counters;
using EFT.Game.Spawning;
using EFT.GlobalEvents;
using EFT.Interactive;
using EFT.Interactive.SecretExfiltrations;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Screens;
using EFT.Weather;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Components;
using Fika.Core.Main.FreeCamera;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.ObservedClasses;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.Networking.Packets.Communication;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.World;
using Fika.Core.UI.Models;
using JsonType;
using static JsonType.LocationSettings;

namespace Fika.Core.Main.GameMode;

public class HostGameController : BaseGameController
{
    private readonly HostBotGameBridge _nativeBotGame;


    public HostGameController(IFikaGame game, EUpdateQueue updateQueue, GameWorld gameWorld, IEftSession session,
        LocationSettings.Location location, WavesSettings wavesSettings, GameDateTime gameDateTime)
        : base(game, updateQueue, gameWorld, session)
    {
        _nativeBotGame = new HostBotGameBridge(this);
        _botsController = new();
        _botStateManager = BotStateManager.Create(_abstractGame, FikaSingleton<FikaServer>.Instance, this);
        _gameDateTime = gameDateTime;

        // Native parity: the offline raid preparation EFT runs before the bot
        // scenarios exist (see PrepareLocationForOfflineRaid).
        PrepareLocationForOfflineRaid(location, wavesSettings);

        if (game is not AbstractGame abstractGame)
        {
            throw new NullReferenceException("Missing AbstractGame");
        }

        // Non Waves Scenario setup
        _nonWavesSpawnScenario = NonWavesSpawnScenario.Create(abstractGame, location, _botsController);
        _nonWavesSpawnScenario.ImplementWaveSettings(wavesSettings);

        // Waves Scenario setup
        var waves = LocalGame.ModifySettings(wavesSettings, location.waves);
        _wavesSpawnScenario = WavesSpawnScenario.Create(abstractGame.gameObject, waves, (Il2CppSystem.Func<SpawnWave, Il2CppSystem.Threading.Tasks.Task>)_botsController.ActivateBotsByWave, location);

        // Boss Scenario setup
        var bossSpawns = LocalGame.ModifyBossWaveSettings(ELocalMode.PVE_OFFLINE, wavesSettings, location.BossLocationSpawn);
        bossSpawns = ApplyBossWaveCollapse(bossSpawns);
        // Every activation is logged before it reaches the native spawner. The
        // IL2CPP wrapper does not implicitly convert a managed method group to
        // System.Action<T>, so bridge it explicitly and fall back to the
        // native method binding if the trampoline cannot be created.
        System.Action<BossLocationSpawn> bossAction;
        try
        {
            Action<BossLocationSpawn> bossActionManaged = OnBossWaveActivated;
            bossAction = bossActionManaged;
        }
        catch (Exception exception)
        {
            Logger.LogError($"[BossSpawn] Could not bridge the boss spawn action, activations will not be logged: {exception}");
            bossAction = (System.Action<BossLocationSpawn>)_botsController.ActivateBotsByWave;
        }
        _bossSpawnScenario = BossSpawnScenario.Create(bossSpawns, bossAction);
        LogBossWaves("received", bossSpawns);

        _server = FikaSingleton<FikaServer>.Instance;
    }

    public byte[] LootData { get; set; }

    protected readonly BotStateManager _botStateManager;
    protected readonly NonWavesSpawnScenario _nonWavesSpawnScenario;
    protected readonly BotsController _botsController;
    protected readonly WavesSpawnScenario _wavesSpawnScenario;
    protected readonly BossSpawnScenario _bossSpawnScenario;
    // A peer can acknowledge each bot profile only once. The snapshot is
    // captured before sending, so a later joiner cannot hold this spawn up.
    protected readonly Dictionary<int, HashSet<NetPeer>> _botQueue = [];
    private readonly object _botQueueGate = new();
    /// <summary>
    /// Telemetry for the [BossSpawn] lines: how many bots of every boss-wave
    /// role (boss or escort) the host actually created during this raid.
    /// </summary>
    private readonly Dictionary<WildSpawnType, int> _bossRolesCreated = [];
    /// <summary>
    /// Role histogram of every bot created this raid plus the one-shot flag of the
    /// [ZoneDiag] ambient line (see <see cref="LogAmbientBotCreation"/>).
    /// </summary>
    private readonly Dictionary<WildSpawnType, int> _botRolesCreated = [];
    private bool _ambientScavLogged;
    protected readonly FikaServer _server;
    protected GameDateTime _gameDateTime;

    /// <summary>
    /// How long in seconds until a bot is force spawned if not every client could load it
    /// </summary>
    private readonly float _botTimeout = 120f;
    /// <summary>
    /// The <see cref="Task.Delay(int)"/> in every loop during the <see cref="_botTimeout"/>
    /// </summary>
    private readonly float _botTimeoutDelay = 0.25f;

    private readonly NetDataWriter _writer = new();

    /// <summary>
    /// Peer ids this host already released through the pre-raid barrier backstop. A peer is disconnected
    /// at most once per raid (idempotent): the set is checked when the peers are collected, so a second
    /// backstop run - or a peer id that reappears - cannot produce a second disconnect for the same peer.
    /// </summary>
    private readonly HashSet<int> _gateReleasedPeers = [];

    public GameStatus Status
    {
        get
        {
            return _abstractGame.Status;
        }
    }

    public GameDateTime GameDateTime
    {
        get
        {
            return _gameDateTime;
        }
        set
        {
            _gameDateTime = value;
        }
    }

    public BotsController BotsController
    {
        get
        {
            return _botsController;
        }
    }

    public IWeatherCurve WeatherCurve
    {
        get
        {
            return WeatherController.Instance.WeatherCurve;
        }
    }

    public BossSpawnScenario BossSpawnScenario
    {
        get
        {
            return _bossSpawnScenario;
        }
    }

    private readonly NativeActionEvent _nativeUpdateByUnity = new();
    /// <summary>Fika's own subscribers (artillery fire control) use a managed delegate.</summary>
    public void add_UpdateByUnity(System.Action handler) => _nativeUpdateByUnity.Add(handler);
    public void remove_UpdateByUnity(System.Action handler) => _nativeUpdateByUnity.Remove(handler);
    /// <summary>
    /// The game subscribes to <c>IBotGame.UpdateByUnity</c> with its own native
    /// delegate, so the injected bridge forwards that ABI here.
    /// </summary>
    public void add_UpdateByUnity(Il2CppSystem.Action handler) => _nativeUpdateByUnity.Add(handler);
    public void remove_UpdateByUnity(Il2CppSystem.Action handler) => _nativeUpdateByUnity.Remove(handler);

    private void InvokeUpdate()
    {
        _nativeUpdateByUnity.Invoke();
    }

    public Action Update
    {
        get
        {
            return InvokeUpdate;
        }
    }

    public override Task ReceiveSpawnPoint(Profile profile)
    {
        throw new NotImplementedException("Should not be called as a host");
    }

    public Il2CppSystem.Threading.Tasks.Task BotDespawn(BotOwner bot)
    {
        var getPlayer = bot.GetPlayer;
        _botsController.BotDied(bot);
        _botsController.DestroyInfo(getPlayer);
        AssetPoolObject.ReturnToPool(bot.gameObject, true);
        return NativeTask.FromManaged(Task.CompletedTask);
    }

    public override IEnumerator WaitForHostInit(int timeBeforeDeployLocal)
    {
        if (_fikaGame is not AbstractGame abstractGame)
        {
            throw new NullReferenceException("AbstractGame was missing");
        }

        _server.HostReady = true;

        var startTime = DateTimeExtensions.UtcNow.AddSeconds((double)timeBeforeDeployLocal);
        GameTime = startTime.ToManaged();
        _server.GameStartTime = startTime.ToManaged();
        var endOfFrame = new WaitForEndOfFrame();
        var waitStarted = Time.realtimeSinceStartup;
        const float timerReadyTimeout = 30f;
        Il2CppSystem.Nullable<Il2CppSystem.TimeSpan> nativeSessionTime;
        do
        {
            nativeSessionTime = NativeNullable<Il2CppSystem.TimeSpan>.Read(abstractGame.GameTimer, "<SessionTime>k__BackingField");
            SessionTime = nativeSessionTime.ToManaged();
            if (!SessionTime.HasValue)
            {
                if (Time.realtimeSinceStartup - waitStarted >= timerReadyTimeout)
                {
                    Logger.LogError("Host timer SessionTime did not become available before the initialization timeout.");
                    _server.HostReady = false;
                    yield break;
                }
                yield return endOfFrame;
            }
        }
        while (!SessionTime.HasValue);

        if (GameTime is not { } gameTime || SessionTime is not { } sessionTime)
        {
            yield break;
        }

        InformationPacket packet = new()
        {
            RaidStarted = RaidStarted,
            ReadyPlayers = _server.ReadyClients,
            HostReady = _server.HostReady,
            GameTime = gameTime,
            SessionTime = sessionTime,
            GameDateTime = GameDateTime
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);

        // The loot descriptor used to be dropped right here, after the first
        // information broadcast. That made the host answer "descriptor not ready"
        // for every later request, so a client whose request lost the race - and
        // any player scav joining a running raid - received no world loot at all.
        // The descriptor is ~135 KB and Location.Loot stays alive anyway, so it
        // is kept for the whole raid.

        yield break;
    }

    #region Bots
    /// <summary>
    /// Used to spawn a bot for the host
    /// </summary>
    /// <param name="profile"><see cref="Profile"/> to spawn</param>
    /// <param name="position">The <see cref="Vector3"/> position to spawn on</param>
    /// <returns></returns>
    private async Task<LocalPlayer> CreateBot(GameWorld gameWorld, Profile profile, Vector3 position)
    {
#if DEBUG
        Logger.LogWarning($"Creating bot {profile.Info.Settings.Role} at {position}");
#endif

        if (_coopHandler == null)
        {
            Logger.LogError($"{nameof(CreateBot)}: CoopHandler was null");
            return null;
        }

        var netId = 1000;
        FikaBot fikaBot;
        if (Bots.ContainsKey(profile.Id))
        {
            return null;
        }

        netId = _server.PopNetId();

        profile.SetSpawnedInSession(profile.Info.Side == EPlayerSide.Savage || FikaPlugin.Instance.Settings.PMCFoundInRaid);
        if (profile.Info.Side != EPlayerSide.Savage)
        {
            var backpack = profile.Inventory.Equipment.GetSlot(EquipmentSlot.Backpack).ContainedItem;
            if (backpack != null)
            {
                foreach (var item in backpack.GetAllItems())
                {
                    if (item != backpack)
                    {
                        item.SpawnedInSession = true;
                    }
                }
            }

            var armband = profile.Inventory.Equipment.GetSlot(EquipmentSlot.ArmBand).ContainedItem;
            if (armband != null)
            {
                armband.SpawnedInSession = false;
            }

            var melee = profile.Inventory.Equipment.GetSlot(EquipmentSlot.Scabbard).ContainedItem;
            if (melee != null)
            {
                melee.SpawnedInSession = false;
            }
        }

        var mongoId = MongoID.Generate(true);
        const ushort nextOperationId = 0;
        var packet = SendCharacterPacket.FromValue(new()
        {
            Profile = profile,
            ControllerId = mongoId,
            FirstOperationId = nextOperationId,
            IsZombie = profile.Info.Settings.UseSimpleAnimator
        }, true, true, position, netId);
        packet.PlayerInfoPacket.HealthByteArray = profile.Health.SerializeHealthInfo();
        var awaitingBotProfile = new List<NetPeer>();
        _server.NetServer.GetConnectedPeers(awaitingBotProfile);
        if (awaitingBotProfile.Count > 0)
        {
            lock (_botQueueGate)
                _botQueue.Add(netId, new HashSet<NetPeer>(awaitingBotProfile));
        }
        try
        {
            _server.SendGenericPacket(EGenericSubPacketType.SendCharacter, packet, true);

            if (awaitingBotProfile.Count > 0)
                await WaitForPlayersToLoadBotProfile(netId);
        }
        finally
        {
            lock (_botQueueGate)
                _botQueue.Remove(netId);
        }

        fikaBot = await FikaBot.CreateBot(_gameWorld, netId, position, Quaternion.identity, "Player",
           "Bot_", EPointOfView.ThirdPerson, profile, true, _updateQueue, Player.EUpdateMode.Auto,
           Player.EUpdateMode.Auto, AppEnvironment.Config.CharacterController.BotPlayerMode,
           FikaGlobals.GetOtherPlayerSensitivity, FikaGlobals.GetOtherPlayerSensitivity,
           ObservedViewFilter.Default, mongoId, nextOperationId);

        fikaBot.Location = Location.Id;
        Bots.Add(fikaBot.ProfileId, fikaBot);

        if (FikaPlugin.Instance.Settings.DisableBotMetabolism.Value)
        {
            fikaBot.HealthController.DisableMetabolism();
        }
        _coopHandler.Players.Add(fikaBot.NetId, fikaBot);
        // Counted only once the bot is fully registered on the host.
        LogBossBotCreation(profile);
        LogAmbientBotCreation(profile);

        

        if (profile.Info.Settings.Role != WildSpawnType.shooterBTR)
        {
            _botStateManager.AddBot(fikaBot);
        }

        var spawnPacket = SpawnAI.FromValue(netId, position);
        _server.SendGenericPacket(EGenericSubPacketType.SpawnAI, spawnPacket);

        return fikaBot;
    }

    /// <summary>
    /// Acknowledges one peer loading a bot profile for <see cref="WaitForPlayersToLoadBotProfile(int)"/>
    /// </summary>
    /// <param name="netId">The bot ID</param>
    /// <param name="peer">The acknowledging peer</param>
    public void IncreaseLoadedPlayers(int netId, NetPeer peer)
    {
        lock (_botQueueGate)
        {
            if (_botQueue.TryGetValue(netId, out var awaitingPeers))
            {
                if (!awaitingPeers.Remove(peer))
                    Logger.LogDebug($"IncreaseLoadedPlayers::Ignoring duplicate or unexpected acknowledgement for bot netId {netId}");
                return;
            }
        }

        if (CoopHandler?.Players.TryGetValue(netId, out var existing) == true &&
            existing?.AIData?.IsAI == true)
        {
            // Reconnect replays acknowledgements for bots already on the host.
            Logger.LogDebug($"IncreaseLoadedPlayers::Ignoring late acknowledgement for existing bot netId {netId}");
            return;
        }

        Logger.LogError($"IncreaseLoadedPlayers::Could not find netId {netId}!");
    }

    /// <summary>
    /// <see cref="Task"/> used to ensure that all players loads a bot before it spawns
    /// </summary>
    /// <param name="netId">The NetId to spawn</param>
    /// <returns></returns>
    private async Task WaitForPlayersToLoadBotProfile(int netId)
    {
        var elapsedSeconds = 0f;
        try
        {
            while (true)
            {
                var waiting = false;
                lock (_botQueueGate)
                {
                    if (_botQueue.TryGetValue(netId, out var awaitingPeers))
                    {
                        awaitingPeers.RemoveWhere(peer => peer.ConnectionState != ConnectionState.Connected);
                        waiting = awaitingPeers.Count > 0;
                    }
                }
                if (!waiting)
                    break;

                if (elapsedSeconds >= _botTimeout)
                {
                    Logger.LogWarning($"WaitForPlayersToLoadBotProfile::Took too long for every player to load the bot with id {netId}, force spawning!");
                    return;
                }

                await Task.Delay((int)(_botTimeoutDelay * 1000)); // multiply 0.X * 1000 to get milliseconds
                elapsedSeconds += _botTimeoutDelay;
            }

            if (elapsedSeconds > 30f)
                Logger.LogWarning($"WaitForPlayersToLoadBotProfile::Bot [{netId}] took an abnormal amount of time to load: {elapsedSeconds:F1}s");
        }
        finally
        {
            lock (_botQueueGate)
                _botQueue.Remove(netId);
        }
    }

    /// <summary>
    /// Despawns a bot
    /// </summary>
    /// <param name="coopHandler"></param>
    /// <param name="bot">The bot to despawn</param>
    internal void DespawnBot(CoopHandler coopHandler, Player bot)
    {
        var botOwner = bot.AIData.BotOwner;

        _botsController.Bots.Remove(botOwner);
        bot.HealthController.remove_DiedEvent((System.Action<EDamageType>)botOwner.OnDied);
        BotDespawn(botOwner);
        if (botOwner != null)
        {
            botOwner.Dispose();
        }

        var fikaPlayer = bot.TryCast<FikaPlayer>();
        if (fikaPlayer == null) return;
        coopHandler.Players.Remove(fikaPlayer.NetId);
        Bots.Remove(bot.ProfileId);
    }
    #endregion

    /// <summary>
    /// Sets up a custom weather curve
    /// </summary>
    /// <param name="timeAndWeather">Struct with custom settings</param>
    public void SetupCustomWeather(TimeAndWeatherSettings timeAndWeather)
    {
        if (WeatherController.Instance == null)
        {
            return;
        }

        var dateTime = DateTimeExtensions.StartOfDay();
        var dateTime2 = dateTime.AddDays(1);

        var weather = WeatherNode.CreateDefault();
        var weather2 = WeatherNode.CreateDefault();
        weather.Cloudness = weather2.Cloudness = timeAndWeather.CloudinessType.ToValue();
        weather.Rain = weather2.Rain = timeAndWeather.RainType.ToValue();
        weather.Wind = weather2.Wind = timeAndWeather.WindType.ToValue();
        weather.ScaterringFogDensity = weather2.ScaterringFogDensity = timeAndWeather.FogType.ToValue();
        weather.Time = dateTime.Ticks;
        weather2.Time = dateTime2.Ticks;
        WeatherNode[] weatherClasses = [weather, weather2];
        WeatherClasses = weatherClasses;
        WeatherController.Instance.SetWeatherNodes(Array.ConvertAll(weatherClasses, CloneNode));
    }

    public override async Task StartBotSystemsAndCountdown(BotControllerSettings controllerSettings, GameWorld gameWorld)
    {
        if (!FikaBackendUtils.IsHeadless)
            LoadingScreenUI.Instance.UpdateAndBroadcast(80f);

        if (Location.Id == "laboratory")
        {
            Logger.LogInfo("Location is 'Laboratory', skipping weather generation");
            Season = ESeason.Summer;
            FikaBackendUtils.CustomRaidSettings.UseCustomWeather = false;
        }
        else
        {
            await GenerateWeathers();
        }

        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_FINISHING_RAID_INIT.Localized());

        gameWorld.RegisterRestrictableZones();
        gameWorld.RegisterBorderZones();

        await InitializeBotsSystem(Location, controllerSettings, gameWorld);

#if DEBUG
        Logger.LogWarning("Starting " + nameof(BaseGameController.WaitForOtherPlayersToLoad));
#endif
        await WaitForOtherPlayersToLoad();

        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_FINISHING_RAID_INIT.Localized());
        Logger.LogInfo("All players are loaded, continuing...");

        await StartBotsSystem(Location);

        // Add FreeCamController to GameWorld GameObject
        if (!FikaBackendUtils.IsHeadless)
        {
            var freeCamController = gameWorld.gameObject.AddComponent<FreeCameraController>();
            FikaSingleton<FreeCameraController>.Create(freeCamController);
        }

        await SetupRaidCode();

        if (!FikaBackendUtils.IsHeadless)
            LoadingScreenUI.Instance.UpdateAndBroadcast(85f);

        Singleton<GlobalConfiguration>.Instance.TimeBeforeDeployLocal = Math.Max(Singleton<GlobalConfiguration>.Instance.TimeBeforeDeployLocal, 3);
    }

    public override async Task WaitForHostToStart()
    {
        await base.WaitForHostToStart();

        GameObject startButton = null;
        if (!FikaBackendUtils.IsHeadless)
        {
            startButton = CreateStartButton() ?? throw new NullReferenceException("Start button could not be created!");
        }

        if (FikaPlugin.Instance.Settings.DevMode.Value)
        {
            Logger.LogWarning("DevMode is enabled, skipping wait...");
            if (!FikaBackendUtils.IsHeadless)
                FikaNotifications.DisplayMessageNotification("DevMode enabled, starting automatically...", iconType: EFT.Communications.ENotificationIconType.Note);
            RaidStarted = true;
        }

        while (!RaidStarted)
        {
            await Task.Yield();
        }

        Logger.LogInfo("Raid has been started...");

        if (FikaPlugin.Instance.Settings.UseNATPunching.Value)
        {
            _server.StopNatIntroduceRoutine();
        }

        if (startButton != null)
        {
            GameObject.Destroy(startButton);
        }

        InformationPacket continuePacket = new()
        {
            RaidStarted = RaidStarted,
            AmountOfPeers = _server.NetServer.ConnectedPeersCount + 1
        };
        _server.SendData(ref continuePacket, DeliveryMethod.ReliableOrdered);
        SetStatusModel status = new(FikaBackendUtils.GroupId, LobbyEntry.ELobbyStatus.IN_GAME);
        await FikaRequestHandler.UpdateSetStatus(status);
    }

    public override async Task WaitForOtherPlayersToLoad()
    {
        var initialPlayerCount = Math.Max(1, _server.PlayerAmount);
        if (initialPlayerCount <= 1)
        {
            _server.ReadyClients++;
            LoadSync.Report("HostGameController.WaitForOtherPlayersToLoad",
                "stage=enter role=host playerAmount=1 => no peer to wait for, the barrier releases immediately");
            return;
        }

        LoadSync.Report("HostGameController.WaitForOtherPlayersToLoad",
            "stage=enter role=host playerAmount=" + _server.PlayerAmount
            + " connectedPeers=" + _server.NetServer.ConnectedPeersCount
            + " => the barrier waits for the character announcement of every expected peer (timeout "
            + LoadSync.PeerLoadTimeoutSeconds.ToString("0") + "s)");

        // A peer can disconnect while the remaining players are loading. Keep
        // the original upper bound so late joins do not extend this barrier,
        // but stop waiting for peers that are no longer connected.
        int ExpectedPlayers() => Math.Max(1, Math.Min(initialPlayerCount, _server.PlayerAmount));
        int ExpectedHumans() => Math.Max(0, ExpectedPlayers() - (FikaBackendUtils.IsHeadless ? 1 : 0));
#if DEBUG
        Logger.LogWarning("Server: Waiting for coopHandler.AmountOfHumans < expected players, expected: " + ExpectedHumans());
#endif
        _server.ReadyClients++;

        // Judgement line (analysis/loading-screen-sync): the host's pre-raid barrier waits for the
        // *characters* of the peers, i.e. for each peer's SendCharacter announcement - not for its
        // loading progress and not for a packet the peer may have failed to apply. A peer whose raid
        // start failed and left the session never announces a character, which is why this wait is now
        // bounded and names the missing peer count in every line.
        var barrierStartedAt = UnityEngine.Time.realtimeSinceStartup;
        var nextBarrierReport = 0f;
        var timedOut = false;
        do
        {
            await Task.Delay(100);
            var expectedHumans = ExpectedHumans();
            _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_WAIT_FOR_OTHER_PLAYERS.Localized(),
                expectedHumans == 0 ? 1f : Math.Min(1f, (float)_coopHandler.AmountOfHumans / expectedHumans));

            var elapsed = UnityEngine.Time.realtimeSinceStartup - barrierStartedAt;
            if (elapsed >= nextBarrierReport)
            {
                nextBarrierReport = elapsed + 5f;
                LoadSync.Report("HostGameController.WaitForOtherPlayersToLoad",
                    DescribeBarrier(expectedHumans, expectedPlayers: ExpectedPlayers(), elapsed) + " => waiting for the missing character(s)");
            }

            if (elapsed >= LoadSync.PeerLoadTimeoutSeconds)
            {
                timedOut = true;
                break;
            }
        } while (_coopHandler.AmountOfHumans < ExpectedHumans());

        if (timedOut)
        {
            var barrierState = DescribeBarrier(ExpectedHumans(), ExpectedPlayers(),
                UnityEngine.Time.realtimeSinceStartup - barrierStartedAt);

            // The backstop really fired: this is the one place where a peer is disconnected (see
            // DisconnectPeersWithoutCharacter). The normal release above never disconnects anybody.
            var released = DisconnectPeersWithoutCharacter(barrierState);
            LoadSync.Warn("HostGameController.WaitForOtherPlayersToLoad",
                barrierState + " => gave up after " + LoadSync.PeerLoadTimeoutSeconds.ToString("0")
                + "s and starts the raid without the missing character(s); "
                + (released == 0
                    ? "no peer had to be disconnected (the missing character belongs to a peer that is already gone)"
                    : "the gate was released without " + released + " peer(s)"));
        }
        else
        {
            LoadSync.Report("HostGameController.WaitForOtherPlayersToLoad",
                DescribeBarrier(ExpectedHumans(), ExpectedPlayers(), UnityEngine.Time.realtimeSinceStartup - barrierStartedAt)
                + " => all expected human characters are in this world; releasing the barrier");
        }

        InformationPacket packet = new()
        {
            RaidStarted = RaidStarted,
            ReadyPlayers = _server.ReadyClients
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);

#if DEBUG
        Logger.LogWarning("Server: Waiting for server.ReadyClients < expected players, expected: " + ExpectedPlayers());
#endif
        var readyStartedAt = UnityEngine.Time.realtimeSinceStartup;
        var nextReadyReport = 0f;
        var readyTimedOut = false;
        do
        {
            await Task.Delay(100);
            var expectedPlayers = ExpectedPlayers();
            _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_WAIT_FOR_OTHER_PLAYERS.Localized(),
                Math.Min(1f, (float)_server.ReadyClients / expectedPlayers));

            var elapsed = UnityEngine.Time.realtimeSinceStartup - readyStartedAt;
            if (elapsed >= nextReadyReport)
            {
                nextReadyReport = elapsed + 5f;
                LoadSync.Report("HostGameController.WaitForOtherPlayersToLoad",
                    "stage=ready role=host readyClients=" + _server.ReadyClients + "/" + expectedPlayers
                    + " playerAmount=" + _server.PlayerAmount + " connectedPeers=" + _server.NetServer.ConnectedPeersCount
                    + " elapsed=" + elapsed.ToString("0") + "s => waiting for the ready message of a peer"
                    + " (the client sends it after its own character is spawned)");
            }

            if (elapsed >= LoadSync.PeerLoadTimeoutSeconds)
            {
                readyTimedOut = true;
                break;
            }
        } while (_server.ReadyClients < ExpectedPlayers());

        if (readyTimedOut)
        {
            LoadSync.Warn("HostGameController.WaitForOtherPlayersToLoad",
                "stage=ready role=host readyClients=" + _server.ReadyClients + "/" + ExpectedPlayers()
                + " => gave up after " + LoadSync.PeerLoadTimeoutSeconds.ToString("0") + "s; the raid starts with the players that reported ready");
        }

        InformationPacket finalPacket = new()
        {
            RaidStarted = RaidStarted,
            ReadyPlayers = _server.ReadyClients
        };

        _server.SendData(ref finalPacket, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Disconnects the peers that are still connected and have never announced a character, at the moment
    /// the pre-raid barrier gives up. Returns how many peers were actually disconnected.
    ///
    /// Why (analysis/loading-screen-sync): the barrier waits for every expected peer's *character*, and a
    /// peer whose raid start failed stays connected while it will never announce one - in the 2026-10-11
    /// 18:58 round that held the host on its loading screen from the client's failed start until that
    /// client's process was closed. Releasing the gate while such a peer stays connected also keeps it in
    /// the session: it receives raid traffic and its own session flags do not match the raid any more
    /// (that is what produced "Received CharacterSyncPacket as server" on a client that was on the main
    /// menu). Disconnecting it makes both sides consistent: the host stops expecting it (the standard
    /// path decrements PlayerAmount, so ExpectedPlayers shrinks) and the peer sees a closed session.
    ///
    /// The rules this follows:
    ///   * only at the moment the backstop fires - the normal release path never disconnects anybody;
    ///   * only peers with no player (a peer that announced its character between the last poll and this
    ///     call has Player != null and is left alone; a peer that already disconnected is not enumerated
    ///     by NetServer at all, which is the "already gone" case);
    ///   * idempotent: <see cref="_gateReleasedPeers"/> holds every peer id this backstop released, so a
    ///     peer is disconnected at most once per raid;
    ///   * through the standard entry only - NetPeer.Disconnect() -> NetManager.DisconnectPeer -> the
    ///     disconnect packet plus the local disconnect event, i.e. the very same cleanup the host already
    ///     runs for a peer that left on its own (FikaServer.OnPeerDisconnected: ready/assigned-net-id/
    ///     profile/scav bookkeeping, RaidInteractionSync.OnDisconnected, the ClientDisconnected broadcast,
    ///     PlayerAmount-- with the UpdateBackendData broadcast, the reconnect timer, the headless handling
    ///     and the PeerDisconnectedEvent). Nothing in that path is changed by this method.
    /// </summary>
    private int DisconnectPeersWithoutCharacter(string barrierState)
    {
        var waiting = new List<Fika.Core.Networking.LiteNetLib.NetPeer>();
        try
        {
            foreach (var raw in _server.NetServer)
            {
                if (raw is not Fika.Core.Networking.LiteNetLib.NetPeer peer || peer.Player != null)
                {
                    continue;
                }

                if (!_gateReleasedPeers.Add(peer.Id))
                {
                    continue;
                }

                waiting.Add(peer);
            }
        }
        catch (Exception exception)
        {
            LoadSync.Warn("HostGameController.WaitForOtherPlayersToLoad",
                "the peers of the barrier could not be enumerated (" + exception.GetType().Name + "): " + exception.Message
                + " => the gate is released and every peer keeps its connection");
            return 0;
        }

        if (waiting.Count == 0)
        {
            return 0;
        }

        var released = 0;
        foreach (var peer in waiting)
        {
            var name = peer.Tag as string ?? ("peer#" + peer.Id);
            LoadSync.Warn("HostGameController.WaitForOtherPlayersToLoad",
                "releasing the gate: disconnecting peer=" + name + " after " + LoadSync.PeerLoadTimeoutSeconds.ToString("0")
                + "s without a character announcement (" + barrierState + ")");
            try
            {
                peer.Disconnect();
                released++;
                LoadSync.Warn("HostGameController.WaitForOtherPlayersToLoad",
                    "-> the gate was released without peer=" + name + " (id=" + peer.Id
                    + "): the connection is closed through the standard disconnect path, so the host's "
                    + "OnPeerDisconnected cleanup runs for it exactly as for a peer that left on its own");
            }
            catch (Exception exception)
            {
                // Give the id back: the peer was not released, so a later backstop (or a later raid) may try again.
                _gateReleasedPeers.Remove(peer.Id);
                LoadSync.Warn("HostGameController.WaitForOtherPlayersToLoad",
                    "-> peer=" + name + " (id=" + peer.Id + ") could not be disconnected: "
                    + exception.GetType().Name + ": " + exception.Message
                    + " => the gate is released anyway; that peer keeps its connection and the session keeps it in PlayerAmount");
            }
        }

        return released;
    }

    /// <summary>
    /// The state of the pre-raid barrier as one line, for the [LoadSync] reports: what the host expects,
    /// what it has, who is still missing and what would release the wait.
    /// </summary>
    private string DescribeBarrier(int expectedHumans, int expectedPlayers, float elapsed)
    {
        var humans = _coopHandler == null ? -1 : _coopHandler.AmountOfHumans;
        var missing = Math.Max(0, expectedHumans - humans);
        return "stage=characters role=host humans=" + humans + "/" + expectedHumans
            + " missing=" + missing + " waitingFor=[" + DescribePeersWithoutPlayer() + "]"
            + " here=[" + DescribeSpawnedHumans() + "]"
            + " playerAmount=" + _server.PlayerAmount + "/" + expectedPlayers
            + " connectedPeers=" + _server.NetServer.ConnectedPeersCount
            + " elapsed=" + elapsed.ToString("0") + "s, " + LoadSync.DescribePeerTraffic()
            + " (" + LoadSync.DescribeScreen() + ")";
    }

    /// <summary>
    /// The nicknames of the connected peers that have no player in this world yet - the "who am I waiting
    /// for" half of a barrier line. A peer's nickname is the Tag the connection was accepted with
    /// (FikaServer.OnPeerDisconnected reads it the same way).
    /// </summary>
    private string DescribePeersWithoutPlayer()
    {
        var names = new System.Text.StringBuilder();
        try
        {
            foreach (var raw in _server.NetServer)
            {
                if (raw is not Fika.Core.Networking.LiteNetLib.NetPeer peer || peer.Player != null)
                {
                    continue;
                }

                if (names.Length > 0)
                {
                    names.Append(',');
                }

                names.Append(peer.Tag as string ?? ("peer#" + peer.Id));
            }
        }
        catch (Exception exception)
        {
            return "unreadable (" + exception.GetType().Name + ")";
        }

        return names.Length == 0 ? "none" : names.ToString();
    }

    /// <summary>The nicknames of the human players this host has spawned in this raid.</summary>
    private string DescribeSpawnedHumans()
    {
        var players = _coopHandler?.HumanPlayers;
        if (players == null || players.Count == 0)
        {
            return "none";
        }

        var names = new System.Text.StringBuilder();
        for (var i = 0; i < players.Count; i++)
        {
            if (i > 0)
            {
                names.Append(',');
            }

            try
            {
                names.Append(players[i].Profile.Nickname);
            }
            catch (Exception)
            {
                names.Append('?');
            }
        }

        return names.ToString();
    }

    public override async Task GenerateWeathers()
    {
        if (WeatherController.Instance != null)
        {
            _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_INIT_WEATHER.Localized());
            Logger.LogInfo("Generating and initializing weather...");
            var weather = await _backendSession.WeatherRequest().AsManaged();
            Season = _backendSession.Season;
            SeasonsSettings = _backendSession.SeasonsSettings;
            if (!FikaBackendUtils.CustomRaidSettings.UseCustomWeather)
            {
                WeatherClasses = weather.Weathers;
                // SetWeatherNodes smooths fog in place. Preserve the original
                // nodes for clients, which apply their own smoothing.
                WeatherController.Instance.SetWeatherNodes(Array.ConvertAll(WeatherClasses, CloneNode));
            }
        }
    }

    private static WeatherNode CloneNode(WeatherNode node)
    {
        return new WeatherNode()
        {
            Time = node.Time,
            Cloudness = node.Cloudness,
            Wind = node.Wind,
            WindDirection = node.WindDirection,
            Turbulence = node.Turbulence,
            Rain = node.Rain,
            RainRandomness = node.RainRandomness,
            ScaterringFogDensity = node.ScaterringFogDensity,
            ScaterringFogHeight = node.ScaterringFogHeight,
            GlobalFogDensity = node.GlobalFogDensity,
            GlobalFogHeight = node.GlobalFogHeight,
            Temperature = node.Temperature,
            AtmospherePressure = node.AtmospherePressure,
            MainWindPosition = node.MainWindPosition,
            MainWindDirection = node.MainWindDirection,
            TopWindPosition = node.TopWindPosition,
            TopWindDirection = node.TopWindDirection,
            LyingWater = node.LyingWater
        };
    }

    public override IEnumerator CountdownScreen(Profile profile, string profileId)
    {
        yield return base.CountdownScreen(profile, profileId);
        _localPlayer.PacketSender.Init();

        // Both of these used to be engine coroutines started from inside the countdown routine (i.e. from
        // inside another coroutine's MoveNext - the shape that faulted during the host raid start, see
        // analysis/host-raid-coroutine). They run on the Update driven pump now.
        FikaRoutinePump.Instance.Start(SyncTraps(), "SyncTraps");
        FikaRoutinePump.Instance.Start(CreateStashes(), "CreateStashes");
    }

    public override void CreateSpawnSystem(Profile profile)
    {
        _spawnPoints = SpawnPointsCollection.CreateFromScene(new Il2CppSystem.Nullable<Il2CppSystem.DateTime>(DateTimeExtensions.LocalDateTimeFromUnixTime(Location.UnixDateTime)),
                                Location.SpawnPointParams);
        var spawnSafeDistance = (Location.SpawnSafeDistanceMeters > 0) ? Location.SpawnSafeDistanceMeters : 100;
        SpawnSystemSettings settings = new(Location.MinDistToFreePoint, Location.MaxDistToFreePoint, Location.MaxBotPerZone, spawnSafeDistance, Location.NoGroupSpawn, Location.OneTimeSpawn);
        SpawnSystem = SpawnSystemFactory.CreateSpawnSystem(settings, (Il2CppSystem.Func<float>)FikaGlobals.GetApplicationTime, Singleton<GameWorld>.Instance, _botsController, _spawnPoints);
        _spawnPoint = SpawnSystem.SelectSpawnPoint(ESpawnCategory.Player, profile.Info.Side, null, null, null, null, profile.Id);
        InfiltrationPoint = string.IsNullOrEmpty(_spawnPoint.Infiltration) ? "MissingInfiltration" : _spawnPoint.Infiltration;
    }

    public async Task InitializeBotsSystem(LocationSettings.Location location,
        BotControllerSettings controllerSettings, GameWorld gameWorld)
    {
        BotProfileClient botsPresets = new(_backendSession, _wavesSpawnScenario.SpawnWaves,
                _bossSpawnScenario.BossSpawnWaves, _nonWavesSpawnScenario.NonWaves, false);
        List<CountTypeBotWave> waveInfos = [];
        var halloween = location.Events.Halloween2024;
        if (halloween?.InfectionPercentage > 0)
        {
            waveInfos.AddRange(BotHalloweenWithZombies.GetProfilesOnStart().AsManaged());
        }
        await botsPresets.TryLoadBotsProfilesOnStart(waveInfos.ToNativeList()).AsManaged();
        BotCreatorClient botCreator = new(_nativeBotGame, botsPresets,
            (Il2CppSystem.Func<GameWorld, Profile, Vector3, Il2CppSystem.Threading.Tasks.Task<LocalPlayer>>)
            (Func<GameWorld, Profile, Vector3, Il2CppSystem.Threading.Tasks.Task<LocalPlayer>>)
            ((world, profile, position) => NativeTask.FromManaged(MainThreadDispatcher.RunAsync(() => CreateBot(world, profile, position)))));
        BotZone[] botZones = [.. LocationScene.GetAllObjects<BotZone>(false)];

        // The zones the raid data names versus the zones the scene provides - the only
        // place that difference is visible (the native spawner skips such a wave
        // silently).
        LogZoneDiagnostics(location, botZones);

        var useWaveControl = controllerSettings.BotAmount == EBotAmount.Horde;

        var limits = SetMaxBotsLimit(location);
        if (FikaPlugin.Instance.Settings.NoAI.Value)
        {
            FikaGlobals.LogWarning("No AI enabled - stopping bot spawns");
            controllerSettings.BotAmount = EBotAmount.NoBots;
            limits = 0;
        }

        // One mapping for both budgets: the spawner's MaxBots and the ambient cap
        // PrepareLocationForOfflineRaid falls back to (see ResolveBotBudget).
        var numberOfBots = ResolveBotBudget(controllerSettings.BotAmount);

        _botsController.SetSettings(limits > 0 ? limits : numberOfBots, _backendSession.BackEndConfig.BotPresets, _backendSession.BackEndConfig.BotWeaponScatterings);
        _botsController.Init(_nativeBotGame, botCreator, botZones, SpawnSystem, _wavesSpawnScenario.BotLocationModifier,
            controllerSettings.IsEnabled, controllerSettings.IsScavWars, useWaveControl, false,
            _bossSpawnScenario.HaveSectants, gameWorld, location.OpenZones, location.Events);
        if (controllerSettings.ExcludedBosses != null)
        {
            _botsController.BotSpawner.SetBlockedRoles(controllerSettings.ExcludedBosses);
            Logger.LogInfo($"[BossSpawn] Blocked roles (server excludedBosses): {DescribeExcludedBosses(controllerSettings.ExcludedBosses)}");
        }
        else
        {
            Logger.LogInfo("[BossSpawn] Blocked roles (server excludedBosses): <null> - nothing blocked");
        }
        _botStateManager.AssignBotsController(_botsController);

        if (!FikaBackendUtils.IsHeadless && FikaSingleton<IFikaGame>.Instance is CoopGame coopGame)
        {
            _botsController.AddActivePLayer(coopGame.LocalPlayer);
        }
    }

    /// <summary>
    /// Made for modders to set custom limits in their mods
    /// </summary>
    /// <param name="location">The map to get the max bots for</param>
    /// <returns>Max bots if any</returns>
    protected int SetMaxBotsLimit(LocationSettings.Location location)
    {
        return 0;
    }

    public async Task StartBotsSystem(LocationSettings.Location location)
    {
#if DEBUG
        Logger.LogWarning("Server: Starting scenarios of bots");
#endif
        if (location.OldSpawn && _wavesSpawnScenario.SpawnWaves != null && _wavesSpawnScenario.SpawnWaves.Length != 0)
        {
            Logger.LogInfo("Running old spawn system. Waves: " + _wavesSpawnScenario.SpawnWaves.Length);
            if (_wavesSpawnScenario != null)
            {
                await _wavesSpawnScenario.Run(EBotsSpawnMode.BeforeGameStarted).AsManaged();
                await _wavesSpawnScenario.Run(EBotsSpawnMode.AfterGameStarted).AsManaged();
            }
        }

        if (location.NewSpawn)
        {
            Logger.LogInfo("Running new spawn system.");
            if (_nonWavesSpawnScenario != null)
            {
                _nonWavesSpawnScenario.Run();
            }
        }

        // Native parity: EFT.LocalGame.LocalBotsControllerInitialization calls
        // bossSpawnScenario.Run(BotSpawner.GetPmcZones()) - the 2-arg call, whose
        // default mode is EBotsSpawnMode.Anyway (Fika 2.4.3 did the same).
        // AfterGameStarted only arms a timer for waves whose Time > 0 and never
        // activates a Time <= 0 wave; every boss wave SPT sends uses Time = -1
        // (Lighthouse: Zryachiy/Knight/Partisan/Gluhar, and the Rogues whenever
        // rogueLighthouseSpawnTimeSettings.enabled is false), so that mode
        // silently dropped the complete boss list.
        _bossSpawnScenario.Run(_botsController.BotSpawner.GetPmcZones(), _botsController.BotSpawner.GetSeason01Zones(), EBotsSpawnMode.Anyway);
        LogBossScenarioState("after Run");
        _botsController.EventsController.SpawnAction();
    }

    /// <summary>
    /// Logs every wave activation before it is handed to the native spawner.
    /// </summary>
    private void OnBossWaveActivated(BossLocationSpawn wave)
    {
        Logger.LogInfo($"[BossSpawn] Activating wave: {DescribeBossWave(wave)}");
        LogBossWaveRequest(wave);
        try
        {
            _botsController.ActivateBotsByWave(wave);
        }
        catch (Exception exception)
        {
            // Never let a managed exception cross back into native timer or
            // event code; report it instead so the wave can be investigated.
            Logger.LogError($"[BossSpawn] Wave activation failed for '{wave?.BossName}': {exception}");
        }
    }

    /// <summary>
    /// Logs the boss wave list exactly as received from the server (after
    /// <see cref="LocalGame.ModifyBossWaveSettings"/>).
    /// </summary>
    private void LogBossWaves(string stage, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BossLocationSpawn> waves)
    {
        // (analysis/perf-diagnostics-cleanup) Boss/zone telemetry of this controller: the master switch
        // ('Coop / Diagnostics', default off) closes it. The 2026-10-13 00:40 raid wrote 102 [BossSpawn] lines
        // (one per boss-wave bot created) and 4 [ZoneDiag] lines - the largest in-raid family of that log.
        // Nothing in this method changes what the raid does; it only describes it.
        
        var length = waves?.Length ?? 0;
        Logger.LogInfo($"[BossSpawn] Boss wave list {stage}: {length} wave(s)");
        for (var index = 0; index < length; index++)
        {
            Logger.LogInfo($"[BossSpawn]   wave[{index}] {DescribeBossWave(waves[index])}");
        }
    }

    /// <summary>
    /// Logs which waves the scenario armed or activated and how many timers it
    /// holds for the timed (Time &gt; 0) waves.
    /// </summary>
    private void LogBossScenarioState(string stage)
    {
        var waves = _bossSpawnScenario.BossSpawnWaves;
        var length = waves?.Length ?? 0;
        var activated = 0;
        for (var index = 0; index < length; index++)
        {
            var wave = waves[index];
            if (wave == null)
            {
                continue;
            }
            if (wave.Activated)
            {
                activated++;
            }
            Logger.LogInfo($"[BossSpawn] {stage}: {DescribeBossWave(wave)}");
        }
        Logger.LogInfo($"[BossSpawn] {stage}: {activated}/{length} wave(s) activated, {_bossSpawnScenario.Timers.Count} timer(s) armed");
        LogIdleBossTriggers();
    }

    /// <summary>
    /// One line per boss wave activation stating what the wave asks the native
    /// spawner for. Icebreaker's boss groups are gated behind triggered events
    /// (wedges1-4), so "the boss never spawns" splits into two very different
    /// cases that this line plus <see cref="LogIdleBossTriggers"/> tell apart:
    /// the wave activated but the boss body is missing, or the wave was never
    /// activated at all.
    /// </summary>
    private void LogBossWaveRequest(BossLocationSpawn wave)
    {
        
        if (wave == null)
        {
            return;
        }

                LogIdleBossTriggers();
    }

    /// <summary>
    /// Lists the botEvent triggers that still have waves waiting. A map whose boss
    /// group only spawns on an event (Icebreaker: wedges1-4, Terminal: TB1-4) leaves
    /// exactly one trace when the event never arrives - an armed wave and an
    /// "idle" entry here. Without it, "no boss in the room" is indistinguishable
    /// from "our port dropped the boss".
    /// </summary>
    private void LogIdleBossTriggers()
    {
        
        var waves = _bossSpawnScenario?.BossSpawnWaves;
        var length = waves?.Length ?? 0;
        if (length == 0)
        {
            return;
        }

        Dictionary<string, int> idle = [];
        for (var index = 0; index < length; index++)
        {
            var wave = waves[index];
            if (wave == null || wave.Activated)
            {
                continue;
            }

            if (wave.TriggerType != SpawnTriggerType.botEvent || string.IsNullOrEmpty(wave.TriggerId))
            {
                continue;
            }

            idle.TryGetValue(wave.TriggerId, out var count);
            idle[wave.TriggerId] = count + 1;
        }

        if (idle.Count == 0)
        {
                        return;
        }

        List<string> parts = [];
        foreach (var pair in idle)
        {
            parts.Add($"{pair.Key}({pair.Value} wave(s))");
        }

        parts.Sort(StringComparer.Ordinal);
            }

    /// <summary>
    /// Counts the boss and escort bots the native spawner actually asked the
    /// host to create, logging each one.
    /// </summary>
    private void LogBossBotCreation(Profile profile)
    {
        
        var settings = profile?.Info?.Settings;
        if (settings == null)
        {
            return;
        }

        var role = settings.Role;
        LocaleUtils.IsBoss(role, out var bossName);
        var isFollower = role.ToString().StartsWith("follower", StringComparison.OrdinalIgnoreCase);
        _bossRolesCreated.TryGetValue(role, out var count);
        count++;
        _bossRolesCreated[role] = count;
        // Deliberately role-agnostic: the previous predicate skipped every role that is
        // neither in LocaleUtils' table nor named "follower*", so the bots of the
        // post-0.14 map bosses (bossWedge, bossBullyBlackDiv, ...) were created in the
        // raid but never appeared here - the log then reads exactly like "the bridge
        // dropped the boss and kept the escorts".
        Logger.LogInfo($"[BossSpawn] Created boss-wave bot: {role} " +
            $"({bossName ?? (isFollower ? "escort" : "unlisted role")}) difficulty={settings.BotDifficulty} " +
            $"profile={profile.Id}, {role} created so far={count}");
    }

    /// <summary>
    /// Reports - and, with <see cref="CollapseBossWavesPerTrigger"/>, removes - the bossWedge
    /// waves that share one trigger. Several entries under one trigger is how the map data
    /// expresses "this event owns these positions", and the native scenario activates every
    /// one of them (<c>BossSpawnScenario.Run</c> hands each activated wave to
    /// <c>ActivateBotsByWave</c>; nothing de-duplicates them). Only <c>bossWedge</c> waves take
    /// part in a group (<see cref="BossTriggerKey"/>); every other role is handed to the
    /// scenario exactly as shipped.
    ///
    /// The user's expectation is one boss in the room, so the collapse runs by default and
    /// keeps the group member with the largest escort count (the squad leader; ties go to
    /// the first entry of the data). With <see cref="CollapseBossWavesPerTrigger"/> off the
    /// shipped list stays byte-for-byte effective, which is how the native behaviour (three
    /// Wedge bodies per event on Icebreaker) can be restored without a database change.
    /// </summary>
    private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BossLocationSpawn> ApplyBossWaveCollapse(
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BossLocationSpawn> waves)
    {
        var length = waves?.Length ?? 0;

        // Only the bossWedge family forms groups (see BossTriggerKey): every other wave -
        // another role, or a trigger-less/timer boss - is its own group, so the collapse
        // cannot silence it.
        Dictionary<string, int> groups = new(StringComparer.Ordinal);
        Dictionary<string, int> keepers = new(StringComparer.Ordinal);
        for (var index = 0; index < length; index++)
        {
            var wave = waves[index];
            if (wave == null)
            {
                continue;
            }

            var key = BossTriggerKey(wave);
            groups.TryGetValue(key, out var count);
            groups[key] = count + 1;
            // The user wants "a boss with its escorts", so the wave that asks the native
            // spawner for the most followers is the one that survives; an equal escort
            // count keeps the earlier entry of the data.
            if (!keepers.TryGetValue(key, out var keeper) || wave.EscortCount > waves[keeper].EscortCount)
            {
                keepers[key] = index;
            }
        }

        var duplicated = 0;
        var shared = 0;
        SortedSet<string> pairs = new(StringComparer.Ordinal);
        foreach (var pair in groups)
        {
            if (pair.Value <= 1)
            {
                continue;
            }

            shared++;
            duplicated += pair.Value - 1;
            pairs.Add($"{pair.Key} x{pair.Value} keep=index{keepers[pair.Key]}");
        }

        if (!CollapseBossWavesPerTrigger)
        {
            

            return waves;
        }

        HashSet<string> losers = new(StringComparer.Ordinal);
        foreach (var pair in keepers)
        {
            losers.Add(pair.Key);
        }

        List<BossLocationSpawn> kept = new(length);
        var disabled = 0;
        for (var index = 0; index < length; index++)
        {
            var wave = waves[index];
            if (wave == null)
            {
                kept.Add(null);
                continue;
            }

            var key = BossTriggerKey(wave);
            // keepers only exist for duplicated groups, so a trigger-less wave (own key,
            // never in keepers) is always kept.
            if (!losers.Contains(key) || keepers[key] == index)
            {
                kept.Add(wave);
                continue;
            }

            // The waves that lose stay in the list (the scenario still subscribes them)
            // but are marked as already activated, and both the event callback and the
            // timer callback skip a wave in that state, so they can never reach the
            // native spawner.
            wave.Activated = true;
            disabled++;
        }

        var result = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BossLocationSpawn>(kept.Count);
        for (var index = 0; index < kept.Count; index++)
        {
            result[index] = kept[index];
        }

        

        return result;
    }

    /// <summary>
    /// Groups the waves the way the map gates them: the trigger kind plus its id. A wave
    /// without an id is its own group (an index-suffixed key), so two timed bosses of the
    /// same role never collapse into one another.
    ///
    /// Only <see cref="WildSpawnType.bossWedge"/> participates: the user's rule is "do not
    /// touch the shipped spawning except the boss", and the Icebreaker data also lists
    /// duplicated triggers for pmcBotBlackDiv/bossBullyBlackDiv (hides1-3, stern0-3) whose
    /// stacking is the data's business. Those waves get a key of their own - one group each -
    /// so the collapse can never silence them.
    /// </summary>
    private static string BossTriggerKey(BossLocationSpawn wave)
    {
        if (wave.BossType != WildSpawnType.bossWedge || string.IsNullOrEmpty(wave.TriggerId))
        {
            return $"{wave.TriggerType}:index:{wave.GetHashCode()}";
        }

        return $"{wave.TriggerType}:{wave.TriggerId}";
    }

    /// <summary>
    /// Native parity for the offline raid preparation EFT performs before any bot
    /// scenario exists - <c>EFT.BaseLocalGame.smethod_1</c> (BaseLocalGame.cs:508-530):
    /// it copies the Offline* spawn switches onto the live ones and scales the map's
    /// ambient bot cap with the raid's bot amount. The headless host already ports it
    /// (Fika.Headless/Classes/GameMode/HeadlessGame.cs:330-353), the coop host never
    /// did, so a location that ships <c>NewSpawn=false</c> with
    /// <c>OfflineNewSpawn=true</c> never started its non-wave spawn scenario. On
    /// Icebreaker the non-wave scenario is the only zone-valid spawn system (its
    /// <c>waves</c> still name zones of the Interchange), so that map stayed empty
    /// apart from the boss waves.
    /// </summary>
    private void PrepareLocationForOfflineRaid(LocationSettings.Location location, WavesSettings wavesSettings)
    {
        var oldSpawn = location.OldSpawn;
        var newSpawn = location.NewSpawn;
        var rawBotMax = location.BotMax;
        var coefficient = ResolveWaveCoefficient(wavesSettings.BotAmount);
        var ambientCap = (int)(rawBotMax * coefficient);
        var fallback = false;

        if (MirrorOfflineSpawnFlags)
        {
            location.OldSpawn = location.OfflineOldSpawn;
            location.NewSpawn = location.OfflineNewSpawn;
        }

        // A map that configures an ambient window (BotStop/BotSpawnTimeOn*) yet ships
        // cap 0 expects the server to send the cap: SPT does that from
        // bot.json/maxBotCap (PostDbLoadService.AdjustLocationBotValues). The fallback
        // below stands in for a missing server cap, but the user's rule for Icebreaker
        // is "no ambient scavengers" (2026-10-13), so it stays off and the map's own
        // BotMax = 0 decides - the non-wave scenario runs with a budget of zero.
        if (EnableAmbientBotCapFallback && ambientCap <= 0 && location.BotStop > 0)
        {
            ambientCap = Math.Min(ResolveBotBudget(wavesSettings.BotAmount), FallbackAmbientBotCap);
            fallback = true;
        }

        location.BotMax = ambientCap;

        // (analysis/perf-diagnostics-cleanup) A [ZoneDiag] probe line inside a functional method: with the
        // master switch off the interpolation is not built; the assignments above are untouched.
        
    }

    /// <summary>
    /// Ambient cap used when a location ships cap 0 although it configures an ambient
    /// spawn window. 18 is SPT's own <c>bot.json -> maxBotCap.default</c> (the value
    /// the installs that still ship that table use for every map without a specific
    /// entry), and it is still bounded by the raid's own bot budget.
    /// </summary>
    private const int FallbackAmbientBotCap = 18;

    /// <summary>
    /// Off by default since 2026-10-13: the user does not want ambient scavengers on
    /// Icebreaker, so the map's data decides again (<c>BotMax = 0</c> -&gt; the non-wave
    /// scenario computes a budget of zero and spawns nobody). Turn it on to give a map
    /// that configures an ambient window yet ships cap 0 a fallback cap instead.
    /// </summary>
    internal static bool EnableAmbientBotCapFallback = false;

    /// <summary>
    /// Mirrors <c>OfflineOldSpawn/OfflineNewSpawn</c> onto the live spawn switches and
    /// scales the ambient cap with the raid's bot amount, exactly like
    /// <c>EFT.BaseLocalGame.smethod_1</c> and the headless host do. On its own it does
    /// not spawn a single bot: the non-wave scenario additionally needs
    /// <c>BotMax &gt; 0</c> (see <see cref="EnableAmbientBotCapFallback"/>). Turn it off
    /// to keep the shipped flags untouched.
    /// </summary>
    internal static bool MirrorOfflineSpawnFlags = true;

    /// <summary>
    /// The user request of 2026-10-13: one map data set may list several boss waves for the
    /// same trigger (Icebreaker's <c>wedges1-4</c> ship three each, in three different
    /// rooms), and the native scenario activates every one of them, so a single event puts
    /// three Wedge bodies on the ship. On (default) exactly one wave per trigger spawns -
    /// the one with the most escorts, ties going to the first entry of the data - and the
    /// others are marked <c>Activated</c> so they can never fire. Off restores the native
    /// behaviour (every entry of the shipped list) without touching the database.
    /// </summary>
    /// <summary>
    /// (analysis/boss-wedge-vanilla) Retired as a user setting: the collapse kept one boss wave per trigger
    /// point, but in live co-op raids the boss then failed to spawn at all, so the vanilla mechanic is used
    /// again. The field is kept (false) so <see cref="ApplyBossWaveCollapse"/> still reports the shipped wave
    /// list, and the old configuration key is dropped from the file by FikaConfig.RetireRemovedEntries.
    /// </summary>
    internal static bool CollapseBossWavesPerTrigger = false;

    /// <summary>
    /// EFT's WAVE_COEF_* switch of <c>BaseLocalGame.smethod_1</c>, read exactly the way
    /// the headless port reads it.
    /// </summary>
    private static float ResolveWaveCoefficient(EBotAmount botAmount)
    {
        var global = Singleton<GlobalConfiguration>.Instance;
        return botAmount switch
        {
            EBotAmount.NoBots or EBotAmount.Low =>
                global != null ? global.WAVE_COEF_LOW : BotInternalSettingsController.Core.WAVE_COEF_LOW,
            EBotAmount.Medium =>
                global != null ? global.WAVE_COEF_MID : BotInternalSettingsController.Core.WAVE_COEF_MID,
            EBotAmount.High =>
                global != null ? global.WAVE_COEF_HIGH : BotInternalSettingsController.Core.WAVE_COEF_HIGH,
            EBotAmount.Horde =>
                global != null ? global.WAVE_COEF_HORDE : BotInternalSettingsController.Core.WAVE_COEF_HORDE,
            _ => 1f,
        };
    }

    /// <summary>
    /// The raid's bot budget: the spawner's MaxBots and the ambient cap fallback of
    /// <see cref="PrepareLocationForOfflineRaid"/> share this mapping, so the ambient
    /// spawner can never aim above the raid's own limit.
    /// </summary>
    private int ResolveBotBudget(EBotAmount botAmount)
    {
        return botAmount switch
        {
            EBotAmount.AsOnline => _backendSession.BackEndConfig.Config.MaxBotsAliveOnMap,
            EBotAmount.NoBots => 0,
            EBotAmount.Low => 15,
            EBotAmount.Medium => 20,
            EBotAmount.High => 25,
            EBotAmount.Horde => 35,
            _ => 15,
        };
    }

    /// <summary>
    /// One line about the zone names the raid data references versus the zones the
    /// scene really provides. A location can ship wave entries that name another map's
    /// zones (Icebreaker's <c>waves</c> and <c>OpenZones</c> still carry the Interchange
    /// set) and the native spawner returns silently for such a wave
    /// (EFT.BotSpawner.ActivateBotsByWave: <c>botZone == null</c> -&gt; no spawn), which
    /// no log could show before.
    /// </summary>
    private void LogZoneDiagnostics(LocationSettings.Location location, BotZone[] botZones)
    {
        
        HashSet<string> present = new(StringComparer.Ordinal);
        for (var index = 0; index < botZones.Length; index++)
        {
            var zone = botZones[index];
            if (zone != null && !string.IsNullOrEmpty(zone.name))
            {
                present.Add(zone.name);
            }
        }

        var waves = location.waves;
        var waveCount = waves?.Length ?? 0;
        var wavesWithZone = 0;
        SortedSet<string> missingWaveZones = new(StringComparer.Ordinal);
        for (var index = 0; index < waveCount; index++)
        {
            var name = waves[index]?.SpawnPoints;
            if (string.IsNullOrEmpty(name) || present.Contains(name))
            {
                wavesWithZone++;
                continue;
            }

            missingWaveZones.Add(name);
        }

        SortedSet<string> missingOpenZones = new(StringComparer.Ordinal);
        var openZones = location.OpenZones;
        if (!string.IsNullOrEmpty(openZones))
        {
            foreach (var name in openZones.Split(','))
            {
                if (!string.IsNullOrEmpty(name) && !present.Contains(name))
                {
                    missingOpenZones.Add(name);
                }
            }
        }

        var ambient = location.NewSpawn && location.BotMax > 0;
            }

    /// <summary>
    /// One-shot proof that the map's non-wave (ambient) spawn scenario is alive: the
    /// first scavenger-role bot the host creates is logged once per raid. On a map
    /// whose only zone-valid spawn system is the non-wave one (Icebreaker) this line
    /// appears shortly after the raid start - or the ambient scenario never ran.
    /// </summary>
    private void LogAmbientBotCreation(Profile profile)
    {
        
        var settings = profile?.Info?.Settings;
        if (settings == null)
        {
            return;
        }

        var role = settings.Role;
        _botRolesCreated.TryGetValue(role, out var count);
        count++;
        _botRolesCreated[role] = count;
        if (_ambientScavLogged || !IsAmbientScavRole(role))
        {
            return;
        }

        _ambientScavLogged = true;
            }

    /// <summary>
    /// The roles the ambient spawner picks from (<c>NonWavesSpawnScenario</c> weights
    /// <c>assault</c> and <c>marksman</c>; their event variants share the bucket).
    /// </summary>
    private static bool IsAmbientScavRole(WildSpawnType role)
    {
        return role is WildSpawnType.assault or WildSpawnType.assaultGroup or WildSpawnType.marksman
            or WildSpawnType.cursedAssault or WildSpawnType.crazyAssaultEvent or WildSpawnType.infectedAssault;
    }

    private static string DescribeBossWave(BossLocationSpawn wave)
    {
        if (wave == null)
        {
            return "<null>";
        }
        LocaleUtils.IsBoss(wave.BossType, out var bossName);
        return $"{wave.BossName} ({wave.BossType}{(bossName == null ? string.Empty : $"/{bossName}")}) " +
            $"time={wave.Time} delay={wave.Delay} trigger={wave.TriggerType} triggerId='{wave.TriggerId}' " +
            $"bornZone='{wave.BornZone}' chance={wave.BossChance} shallSpawn={wave.ShallSpawn} activated={wave.Activated} " +
            $"escorts={wave.EscortType}x{wave.EscortCount}";
    }

    private static string DescribeExcludedBosses(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray excludedBosses)
    {
        if (excludedBosses == null)
        {
            return "<null>";
        }
        if (excludedBosses.Length == 0)
        {
            return "<none>";
        }
        var names = new List<string>(excludedBosses.Length);
        for (var index = 0; index < excludedBosses.Length; index++)
        {
            names.Add(excludedBosses[index]);
        }
        return string.Join(", ", names);
    }

    public override void SetupEventsAndExfils(Player player)
    {
        if (_fikaGame is not CoopGame coopGame)
        {
            // HeadlessGame does not have a local profile, GameUI or player
            // owner. Its HeadlessGameController calls this hook after the
            // world and bots are ready, so only perform the game-wide part of
            // the native startup here.
            _abstractGame.GameTimer.Start(GameTime.ToNative(), SessionTime.ToNative());
            Logger.LogInfo("[SERVER] Headless raid started at spawn point: " + (_spawnPoint?.Id ?? "unknown"));
            if (Location.EventTrapsData != null)
            {
                LabyrinthSyncableTraps.InitLabyrinthSyncableTraps(Location.EventTrapsData);
                _gameWorld.SyncModule = new();
            }

            _abstractGame.Status = GameStatus.Started;
            _botsController.Bots.CheckActivation();
            ConsoleScreen.ApplyStartCommands();
            return;
        }

        coopGame.GameTimer.Start(GameTime.ToNative(), SessionTime.ToNative());
        coopGame.Spawn();

        var skills = coopGame.Profile.Skills.Skills;
        var skillsLength = skills.Length;
        for (var i = 0; i < skillsLength; i++)
        {
            skills[i].SetPointsEarnedInSession(0f, false);
        }

        coopGame.Profile.Info.EntryPoint = InfiltrationPoint;
        Logger.LogInfo("[SERVER] SpawnPoint: " + _spawnPoint.Id + ", InfiltrationPoint: " + InfiltrationPoint);

        var exfilController = ExfiltrationController.Instance;
        var isScav = player.Side is EPlayerSide.Savage;
        ExfiltrationPoint[] exfilPoints;
        SecretExfiltrationPoint[] secretExfilPoints;
        exfilController.InitSecretExfils(player);

        if (isScav)
        {
            exfilController.ScavExfiltrationClaim(player.Position, player.ProfileId, player.Profile.FenceInfo.AvailableExitsCount);
            var mask = exfilController.GetScavExfiltrationMask(player.ProfileId);
            exfilPoints = exfilController.ScavExfiltrationClaim(mask, player.ProfileId);
            secretExfilPoints = exfilController.GetScavSecretExits();
        }
        else
        {
            exfilPoints = exfilController.EligiblePoints(coopGame.Profile);
            secretExfilPoints = exfilController.SecretEligiblePoints();
        }

        // The game's ExtractionTimersPanel starts its slide/colour coroutines from inside SetTime,
        // and Unity refuses that on the closed (inactive) panel. Drive it only while it is live.
        if (FikaUiPanels.CanDrive(coopGame.GameUi.TimerPanel, "HostGameController.Spawn.TimerPanel.SetTime"))
        {
            coopGame.GameUi.TimerPanel.SetTime(DateTimeExtensions.UtcNow,
                coopGame.Profile.Info.Side, coopGame.GameTimer.EscapeTimeSeconds(),
                exfilPoints, secretExfilPoints);
        }

        if (FikaWorldControllers.Find<FikaHostTransitController>(Singleton<GameWorld>.Instance.TransitController) is { } transitController)
        {
            transitController.Init();
            foreach (var activePlayer in CoopHandler.HumanPlayers)
            {
                var packet = transitController.CreateInitialization(activePlayer);
                if (packet != null) _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            }
        }

        if (Location.EventTrapsData != null)
        {
            Logger.LogInfo("Loading trap data");
            LabyrinthSyncableTraps.InitLabyrinthSyncableTraps(Location.EventTrapsData);
            _gameWorld.SyncModule = new();
        }

        ExfilManager.Run(exfilPoints, secretExfilPoints);
        coopGame.Status = GameStatus.Started;
        _botsController.Bots.CheckActivation();

        ConsoleScreen.ApplyStartCommands();
    }

    /// <summary>
    /// When the local player successfully extracts, enable freecam, notify other players about the extract
    /// </summary>
    /// <param name="player">The local player to start the Coroutine on</param>
    /// <param name="exfiltrationPoint">The exfiltration point that was used to extract</param>
    /// <param name="transitPoint">The transit point that was used to transit</param>
    /// <returns></returns>
    public override void Extract(FikaPlayer player, ExfiltrationPoint exfiltrationPoint, TransitPoint transitPoint = null)
    {
        if (_fikaGame is not CoopGame coopGame)
        {
            // Headless hosts have no local UI/player owner. Transit extracts
            // are finalized by the headless game itself; keep the shared
            // player bookkeeping and notify peers without touching CoopGame
            // UI or camera state.
            if (_coopHandler == null) throw new NullReferenceException("Extract: CoopHandler was null!");
            if (_fikaGame.ExtractedPlayers.Contains(player.NetId)) return;
            _fikaGame.ExtractedPlayers.Add(player.NetId);
            _coopHandler.ExtractedPlayers.Add(player.NetId);
            _coopHandler.Players.Remove(player.NetId);
            try
            {
                _server.SendGenericPacket(EGenericSubPacketType.ClientExtract,
                    ClientExtract.FromValue(player.NetId), true);
                ClearHostAI(player);
            }
            catch { }
            return;
        }

        if (coopGame.ExtractedPlayers.Contains(player.NetId)) return;

        var preloaderUI = Singleton<PreloaderUI>.Instance;
        _localTriggerZones = [.. player.TriggerZones];

        player.ClientMovementContext.SetGravity(false);
        var position = player.Position;
        position.y += 500f;
        player.Teleport(position);

        if (coopGame.ExitStatus == ExitStatus.MissingInAction)
        {
            FikaNotifications.DisplayMessageNotification(LocaleUtils.PLAYER_MIA.Localized(), iconType: ENotificationIconType.Alert, textColor: new Il2CppSystem.Nullable<Color>(Color.red));
        }

        if (player.FikaQuests is ClientQuestController clientQuestController)
        {
            clientQuestController.ToggleSend(false);
        }

        if (player.FikaQuests is ClientSharedQuestController sharedQuestController)
        {
            sharedQuestController.ToggleQuestSharing(false);
        }

        // SPT folds queued healing, energy and hydration experience into the
        // profile before evaluating the raid result.
        player.StatisticsManager?.TryCast<HealthStatisticsManager>()?.ConsumeExperience();

#if !DEBUG
        var matchEndConfig = Singleton<GlobalConfiguration>.Instance.Experience.MatchEnd;
        if (player.Profile.EftStats.SessionCounters.GetAllInt(NativeCounters.ExperienceTags()) < matchEndConfig.SurvivedExpRequirement && coopGame.PastTime < matchEndConfig.SurvivedTimeRequirement)
        {
            coopGame.ExitStatus = ExitStatus.Runner;
        }
#endif

        if (exfiltrationPoint != null)
        {
            exfiltrationPoint.Disable();

            if (exfiltrationPoint.HasRequirements && exfiltrationPoint.TransferItemRequirement?.Met(player, exfiltrationPoint) == true && player.IsYourPlayer)
            {
                // Seems to already be handled by SPT so we only add it visibly
                player.Profile.EftStats.SessionCounters.AddDouble(0.2, NativeCounters.ExitStanding());
            }
        }

        if (player.Side == EPlayerSide.Savage)
        {
            // Seems to already be handled by SPT so we only add it visibly
            player.Profile.EftStats.SessionCounters.AddDouble(0.01d, NativeCounters.ExitStanding());
        }

        var transitController = Singleton<GameWorld>.Instance.TransitController;
        if (transitController != null && transitPoint != null)
        {
            if (transitController.alreadyTransits.TryGetValue(player.ProfileId, out var data))
            {
                coopGame.ExitStatus = ExitStatus.Transit;
                coopGame.ExitLocation = transitPoint.parameters.name;
                FikaBackendUtils.IsTransit = true;
            }
            if (FikaWorldControllers.Find<FikaHostTransitController>(transitController) is { } hostController)
            {
                _server.SendGenericPacket(EGenericSubPacketType.UpdateBackendData,
                    UpdateBackendData.FromValue(hostController.AliveTransitPlayers), true);
            }
        }

        if (_coopHandler != null)
        {
            try // This is to allow clients to extract if they lose connection
            {
                _server.SendGenericPacket(EGenericSubPacketType.ClientExtract,
                    ClientExtract.FromValue(player.NetId), true);
                ClearHostAI(player);
            }
            catch
            {

            }

            var fikaPlayer = player;
            coopGame.ExtractedPlayers.Add(fikaPlayer.NetId);
            _coopHandler.ExtractedPlayers.Add(fikaPlayer.NetId);
            _coopHandler.Players.Remove(fikaPlayer.NetId);

            preloaderUI.StartBlackScreenShow(2f, 2f, (System.Action)(() => preloaderUI.FadeBlackScreen(2f, -2f)));

            player.ActiveHealthController.SetDamageCoeff(0f);
            player.ActiveHealthController.DamageMultiplier = 0f;
            player.ActiveHealthController.DisableMetabolism();
            player.ActiveHealthController.PauseAllEffects();

            _extractRoutine = coopGame.StartCoroutine(ExtractRoutine(player, coopGame));

            // Prevents players from looting after extracting
            EftScreenManager.Instance.CloseAllScreensForced();

            // Detroys session timer
            if (TimeManager != null)
            {
                UnityEngine.Object.Destroy(TimeManager);
            }
            if (FikaUiPanels.CanDrive(coopGame.GameUi.TimerPanel, "HostGameController.Extract.TimerPanel.Close"))
            {
                coopGame.GameUi.TimerPanel.Close();
            }
        }
        else
        {
            throw new NullReferenceException("Extract: CoopHandler was null!");
        }
    }

    public void ClearHostAI(Player player)
    {
        if (_botsController != null)
        {
            _botsController.DestroyInfo(player);
        }
    }

    public override void CleanUp()
    {
        base.CleanUp();
        lock (_botQueueGate)
            _botQueue.Clear();

        if (!FikaBackendUtils.IsHeadless)
        {
            var fikaPlayer = Singleton<GameWorld>.Instance?.MainPlayer?.TryCast<FikaPlayer>();
            if (fikaPlayer?.PacketSender != null)
            {
                fikaPlayer.PacketSender.DestroyThis();
            }
        }

        if (!FikaBackendUtils.IsTransit)
        {
            NetManagerUtils.StopPinger();
        }
        else
        {
            _server.HostReady = false;
        }

        _btrSpawn?.Invoke();
        _btrSpawn = null;
    }

    public override Task InitializeLoot(LocationSettings.Location location)
    {
        if (FikaPlugin.Instance.Settings.NoLoot.Value)
        {
            if (!FikaBackendUtils.IsHeadless)
                NotificationManager.DisplayWarningNotification("NoLoot enabled, no loot will spawn and any stationary weapon WILL NOT WORK!");
            location.Loot = new JsonType.LootData();
        }

        var lootDescriptor = ItemBinarySerializer.SerializeLootData(FilterLootForHostProfile(location.Loot),
            FikaGlobals.SearchControllerSerializer);
        _writer.PutEFTLootDataDescriptor(lootDescriptor);
        LootData = _writer.CopyData();
        _writer.Reset();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Drops loot entries the host itself cannot spawn because they are bound to
    /// other profiles (battle pass documents, profile specific quest loot).
    /// Clients apply their own profile filter on top of this descriptor, so the
    /// result can never contain an item the host does not have - otherwise the
    /// host rejects the client's pickup descriptor and the client is forced
    /// through a full inventory resync while it is still interacting.
    /// </summary>
    private JsonType.LootData FilterLootForHostProfile(JsonType.LootData loot)
    {
        try
        {
            var hostProfile = FikaBackendUtils.Profile;
            if (loot == null || hostProfile == null) return loot;

            var profileId = hostProfile.Id.ToString();
            var kept = new Il2CppSystem.Collections.Generic.List<JsonLootItem>();
            var removed = 0;
            foreach (var entry in loot)
            {
                if (entry == null) continue;
                var validProfiles = entry.ValidProfiles;
                if (validProfiles == null || validProfiles.Length == 0 || ContainsProfile(validProfiles, profileId))
                {
                    kept.Add(entry);
                    continue;
                }

                removed++;
            }

            if (removed == 0) return loot;

            FikaGlobals.LogInfo($"Loot descriptor bound to the host profile: kept {kept.Count}, removed {removed}");
            return new JsonType.LootData(kept);
        }
        catch (Exception exception)
        {
            // Never let this optimisation cost the raid its loot.
            FikaGlobals.LogError($"Failed to filter the loot descriptor for the host profile: {exception}");
            return loot;
        }
    }

    private static bool ContainsProfile(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<MongoID> validProfiles,
        string profileId)
    {
        for (var i = 0; i < validProfiles.Length; i++)
        {
            if (validProfiles[i].ToString() == profileId) return true;
        }

        return false;
    }

    public byte[] GetHostLootItems()
    {
        // Only the descriptor built from the raid's own loot data is
        // authoritative. Serializing the live world LootList while the host is
        // still spawning (or before InitializeLoot ran) produced a truncated
        // world for early clients, so an unready host now answers with an empty
        // payload and the client polls again. The loot itself is spawned from
        // Location.Loot by SpawnLoot, so InitializeLoot always describes the
        // complete world.
        if (LootData != null && LootData.Length > 0)
        {
            return LootData;
        }

        FikaGlobals.LogWarning("Loot descriptor requested before the host built it, answering empty");
        return [];
    }

    private JsonLootItem SerializeLootItem(LootItem lootItem, GameWorld gameWorld)
    {
        short num = -1;
        if (gameWorld.Platforms.Length != 0 && lootItem.Platform != null)
        {
            num = (short)Array.IndexOf(gameWorld.Platforms, lootItem.Platform);
        }

        // SPT 5's binary loot format has a dedicated JsonCorpse descriptor.
        // Returning a plain JsonLootItem here loses the corpse's body
        // customization and ragdoll pose, which makes a late-joining client
        // see only an empty loot item (and prevents dogtag/corpse inventory
        // from being restored by the native loot spawner).
        if (lootItem.TryCast<Corpse>() is { } corpse)
        {
            var bones = num > -1 ? corpse.TransformSyncsRelativeToPlatform : corpse.TransformSyncs;
            if (bones == null)
            {
                bones = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<TransformSync>(0);
            }

            return new JsonCorpse
            {
                Position = (num > -1) ? lootItem.transform.localPosition : lootItem.transform.position,
                Rotation = (num > -1) ? lootItem.transform.localRotation.eulerAngles : lootItem.transform.rotation.eulerAngles,
                Item = lootItem.ItemOwner.RootItem,
                ValidProfiles = lootItem.ValidProfiles,
                Id = lootItem.StaticId,
                IsContainer = lootItem.StaticId != null,
                Shift = lootItem.Shift,
                PlatformId = num,
                Customization = corpse.Customization ?? new BodyCustomization(),
                Side = corpse.Side,
                Bones = bones,
                ProfileID = corpse.PlayerProfileID,
                // SPT 5 removed IsZombieCorpse from the native Corpse API.
                // The regular player corpse path is the only one Fika can
                // author here; native zombie corpses keep the safe default.
                IsZombieCorpse = false
            };
        }

        var transform = lootItem.transform;
        return new JsonLootItem
        {
            Position = (num > -1) ? transform.localPosition : transform.position,
            Rotation = (num > -1) ? transform.localRotation.eulerAngles : transform.rotation.eulerAngles,
            Item = lootItem.ItemOwner.RootItem,
            ValidProfiles = lootItem.ValidProfiles,
            Id = lootItem.StaticId,
            IsContainer = lootItem.StaticId != null,
            randomRotation = lootItem.RandomRotation,
            Shift = lootItem.Shift,
            PlatformId = num
        };
    }

    private int LootCompare(JsonLootItem a, JsonLootItem b)
    {
        return string.CompareOrdinal(a.Id, b.Id);
    }

    public void StopBotsSystem(bool fromCancel)
    {
        if (!fromCancel)
        {
            if (_botStateManager != null)
            {
                _botStateManager.UnassignBotsController();
                GameObject.Destroy(_botStateManager);
            }
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld.ServerShellingController != null)
            {
                remove_UpdateByUnity(gameWorld.ServerShellingController.OnUpdate);
            }
            if (_botsController != null)
            {
                _botsController.StopGettingInfo();
                if (!FikaBackendUtils.IsHeadless)
                {
                    _botsController.DestroyInfo(_localPlayer);
                }
            }
        }

        StopBossSpawnTimers(_bossSpawnScenario);
        if (_nonWavesSpawnScenario != null)
        {
            _nonWavesSpawnScenario.Stop();
        }
        if (_wavesSpawnScenario != null)
        {
            _wavesSpawnScenario.Stop();
        }
    }

    internal static void StopBossSpawnTimers(BossSpawnScenario scenario)
    {
        if (scenario == null) return;

        // SPT 5 retains elapsed timers in the scenario, while TimerManager has
        // already stopped and detached them. Native Stop assumes every entry
        // is active; prune completed entries before its normal cancellation.
        var timers = scenario.Timers;
        for (var index = timers.Count - 1; index >= 0; index--)
        {
            if (!timers[index].IsActive) timers.RemoveAt(index);
        }
        scenario.Stop();
    }

    public IEnumerator SyncTraps()
    {
        yield return new WaitSeconds(5f);

        if (Location.EventTrapsData == null)
        {
            yield break;
        }

        if (_gameWorld.SyncModule == null)
        {
            Logger.LogError("SyncModule was null when trying to sync trap data!");
            yield break;
        }

        BitWriterStream writer = new(new byte[2048]);
        _gameWorld.SyncModule.Serialize(writer);

        SyncTrapsPacket packet = new()
        {
            Data = new byte[writer.BytesWritten]
        };
        Buffer.BlockCopy(writer.Buffer, 0, packet.Data, 0, writer.BytesWritten);

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    private bool IsBarbedWireTrap(TrapSyncable trap)
    {
        return trap.TrapType is ETrapType.BarbedWire;
    }

    private bool IsTrapDoorTrap(TrapSyncable trap)
    {
        return trap.TrapType is ETrapType.TrapDoor;
    }

    internal void RemoveBot(FikaBot fikaBot)
    {
        if (_botStateManager != null)
        {
            _botStateManager.RemoveBot(fikaBot);
        }
    }
}
