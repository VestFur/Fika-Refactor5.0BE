using Fika.Core.Main.BaseClasses;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
        // Every activation is logged before it reaches the native spawner. The
        // IL2CPP wrapper does not implicitly convert a managed method group to
        // Il2CppSystem.Action<T>, so bridge it explicitly and fall back to the
        // native method binding if the trampoline cannot be created.
        Il2CppSystem.Action<BossLocationSpawn> bossAction;
        try
        {
            Action<BossLocationSpawn> bossActionManaged = OnBossWaveActivated;
            bossAction = bossActionManaged;
        }
        catch (Exception exception)
        {
            Logger.LogError($"[BossSpawn] Could not bridge the boss spawn action, activations will not be logged: {exception}");
            bossAction = (Il2CppSystem.Action<BossLocationSpawn>)_botsController.ActivateBotsByWave;
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

    public event Action UpdateByUnity;
    private readonly NativeActionEvent _nativeUpdateByUnity = new();
    public void add_UpdateByUnity(Il2CppSystem.Action handler) => _nativeUpdateByUnity.Add(handler);
    public void remove_UpdateByUnity(Il2CppSystem.Action handler) => _nativeUpdateByUnity.Remove(handler);

    private void InvokeUpdate()
    {
        UpdateByUnity?.Invoke();
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
        bot.HealthController.remove_DiedEvent((Il2CppSystem.Action<EDamageType>)botOwner.OnDied);
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
            return;
        }

        // A peer can disconnect while the remaining players are loading. Keep
        // the original upper bound so late joins do not extend this barrier,
        // but stop waiting for peers that are no longer connected.
        int ExpectedPlayers() => Math.Max(1, Math.Min(initialPlayerCount, _server.PlayerAmount));
        int ExpectedHumans() => Math.Max(0, ExpectedPlayers() - (FikaBackendUtils.IsHeadless ? 1 : 0));
#if DEBUG
        Logger.LogWarning("Server: Waiting for coopHandler.AmountOfHumans < expected players, expected: " + ExpectedHumans());
#endif
        _server.ReadyClients++;
        do
        {
            await Task.Delay(100);
            var expectedHumans = ExpectedHumans();
            _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_WAIT_FOR_OTHER_PLAYERS.Localized(),
                expectedHumans == 0 ? 1f : Math.Min(1f, (float)_coopHandler.AmountOfHumans / expectedHumans));
        } while (_coopHandler.AmountOfHumans < ExpectedHumans());

        InformationPacket packet = new()
        {
            RaidStarted = RaidStarted,
            ReadyPlayers = _server.ReadyClients
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);

#if DEBUG
        Logger.LogWarning("Server: Waiting for server.ReadyClients < expected players, expected: " + ExpectedPlayers());
#endif
        do
        {
            await Task.Delay(100);
            var expectedPlayers = ExpectedPlayers();
            _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_WAIT_FOR_OTHER_PLAYERS.Localized(),
                Math.Min(1f, (float)_server.ReadyClients / expectedPlayers));
        } while (_server.ReadyClients < ExpectedPlayers());

        InformationPacket finalPacket = new()
        {
            RaidStarted = RaidStarted,
            ReadyPlayers = _server.ReadyClients
        };

        _server.SendData(ref finalPacket, DeliveryMethod.ReliableOrdered);
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

        _gameWorld.StartCoroutine(SyncTraps());
        _gameWorld.StartCoroutine(CreateStashes());
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

        var useWaveControl = controllerSettings.BotAmount == EBotAmount.Horde;

        var limits = SetMaxBotsLimit(location);
        if (FikaPlugin.Instance.Settings.NoAI.Value)
        {
            FikaGlobals.LogWarning("No AI enabled - stopping bot spawns");
            controllerSettings.BotAmount = EBotAmount.NoBots;
            limits = 0;
        }

        var numberOfBots = controllerSettings.BotAmount switch
        {
            EBotAmount.AsOnline => _backendSession.BackEndConfig.Config.MaxBotsAliveOnMap,
            EBotAmount.NoBots => 0,
            EBotAmount.Low => 15,
            EBotAmount.Medium => 20,
            EBotAmount.High => 25,
            EBotAmount.Horde => 35,
            _ => 15,
        };

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
    }

    /// <summary>
    /// Counts the boss and escort bots the native spawner actually asked the
    /// host to create, logging each one.
    /// </summary>
    private void LogBossBotCreation(Profile profile)
    {
        var role = profile.Info.Settings.Role;
        LocaleUtils.IsBoss(role, out var bossName);
        if (bossName == null && !role.ToString().StartsWith("follower", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _bossRolesCreated.TryGetValue(role, out var count);
        count++;
        _bossRolesCreated[role] = count;
        Logger.LogInfo($"[BossSpawn] Created boss-wave bot: {role} ({bossName ?? "escort"}) profile={profile.Id}, {role} created so far={count}");
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

        coopGame.GameUi.TimerPanel.SetTime(DateTimeExtensions.UtcNow,
            coopGame.Profile.Info.Side, coopGame.GameTimer.EscapeTimeSeconds(),
            exfilPoints, secretExfilPoints);

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

            preloaderUI.StartBlackScreenShow(2f, 2f, (Il2CppSystem.Action)(() => preloaderUI.FadeBlackScreen(2f, -2f)));

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
            if (coopGame.GameUi.TimerPanel.enabled)
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
                UpdateByUnity -= gameWorld.ServerShellingController.OnUpdate;
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
        yield return new WaitForSeconds(5);

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
