using Audio.RadioSystem;
using Audio.SpatialSystem;
using BepInEx.Logging;
using Comfort.Common;
using CommonAssets.Scripts.Game;
using Dissonance.Networking.Client;
using Diz.Jobs;
using EFT;
using EFT.Airdrop;
using EFT.AssetsManager;
using EFT.Bots;
using EFT.CameraControl;
using EFT.EnvironmentEffect;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.Settings;
using EFT.UI;
using EFT.UI.Screens;
using EFT.Utilities;
using EFT.Weather;
using Fika.Core;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Patches.BTR;
using Fika.Core.Main.Patches.Overrides;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using JsonType;
using Koenigz.PerfectCulling;
using Koenigz.PerfectCulling.EFT;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine.LowLevel;

namespace Fika.Headless.Classes.GameMode;

public class HeadlessGame : AbstractGame, IFikaGame, IClientHearingTable
{
    public HeadlessGame(IntPtr pointer) : base(pointer)
    {
        ClassInjector.InvokeBaseConstructor<AbstractGame>(this);
    }

    public HeadlessGame() : this(ClassInjector.DerivedConstructorPointer<HeadlessGame>())
    {
        ClassInjector.DerivedConstructorBody(this);
    }

    public override string LocationObjectId
    {
        get
        {
            return _location?._Id ?? string.Empty;
        }
    }

    public override string LocationId
    {
        get
        {
            return _location?.Id ?? string.Empty;
        }
    }

    public override GameUI GameUi
    {
        get
        {
            return null;
        }
    }

    public override string ProfileId
    {
        get
        {
            return string.Empty;
        }
    }

    // 无头 raid 没有本地玩家，因此也没有玩家 raid ID。
    public override int RaidID => -1;

    public List<int> ExtractedPlayers { get; } = [];

    public void SetMatchmakerStatus(string status, float? progress = null)
    {
        InvokeMatchingStatusChanged(EFT.UI.Matchmaker.EMatchingStage.SynchronizationWithOtherPlayers,
            progress.HasValue ? new Il2CppSystem.Nullable<float>(progress.Value) : new Il2CppSystem.Nullable<float>());
    }

    public ExitStatus ExitStatus { get; set; } = ExitStatus.Survived;

    public string ExitLocation { get; set; }

    public ESeason Season
    {
        get
        {
            return GameController.Season;
        }
        set
        {
            GameController.Season = value;
        }
    }

    public SeasonsSettings SeasonsSettings
    {
        get
        {
            return GameController.SeasonsSettings;
        }

        set
        {
            GameController.SeasonsSettings = value;
        }
    }

    private ManualLogSource _logger { get; set; }

    public IEftSession BackendSession { get; set; }

    public BaseGameController GameController { get; set; }
    public GameDateTime GameDateTime
    {
        get
        {
            return (GameController as HeadlessGameController).GameDateTime;
        }
        set
        {
            (GameController as HeadlessGameController).GameDateTime = value;
        }
    }
    public GameWorld GameWorld { get; private set; }

    private LocalRaidSettings _localRaidSettings;
    private Callback<ExitStatus, Il2CppSystem.TimeSpan, ClientMetrics> _exitCallback;
    private bool _disposed;
    private LocationSettings.Location _location;
    private EDateTime _tarkovDateTime;
    private DateTime _dateTime;
    private float _voipDistance;
    private readonly Dictionary<string, DateTime> _factoryTimes = new()
    {
        {
            "factory4_day",
            new DateTime(2016, 8, 4, 15, 28, 0, DateTimeKind.Utc)
        },
        {
            "factory4_night",
            new DateTime(2016, 8, 4, 3, 28, 0, DateTimeKind.Utc)
        }
    };

    public static HeadlessGame Create(GameWorld gameWorld, GameDateTime backendDateTime,
        LocationSettings.Location location, TimeAndWeatherSettings timeAndWeather, WavesSettings wavesSettings,
        EDateTime dateTime, Callback<ExitStatus, Il2CppSystem.TimeSpan, ClientMetrics> callback, float fixedDeltaTime,
        EUpdateQueue updateQueue, IEftSession backEndSession, TimeSpan sessionTime, LocalRaidSettings localRaidSettings,
        RaidSettings raidSettings)
    {
        FikaSingleton<IFikaNetworkManager>.Instance.RaidSide = localRaidSettings.playerSide;

        TraceStep("create: native headless factory");
        var game = NativeHeadlessGameFactory.Create(updateQueue,
            new Il2CppSystem.Nullable<Il2CppSystem.TimeSpan>(sessionTime.ToNative()));
        game._logger = Logger.CreateLogSource(nameof(HeadlessGame));
        game.GameWorld = gameWorld;
        TraceStep("create: native game ready");

        var gameTime = backendDateTime;

        // (analysis/map-fixed-time) 与 CoopGame.Create 所运行的同一代码块，位于专用主机自身的 raid
        // 路径上：地图可以在原生 matchmaker 面板中钉住自己的时钟
        // （EFT.UI.Matchmaker.LocationConditionsPanel，每张这类地图一个静态的 "<map>FixedTime" 值），而
        // Fika raid 从不运行那个面板。'Coop / Honor Map Fixed Time'（默认开启）就是那个开关，而
        // 重新加入或 transit 无论如何都会保留正在运行的时钟，因为再次钉住它会让时钟倒退。
        //
        // 给任何阅读无头日志的人一个提示：DoWeatherThings（在本方法后面被调用）会
        // 第二次推导时钟——在设置 IsRandomTime 时它会抽取一个随机小时并再次丢弃固定时间。
        // 判定行会打印 isRandomTime，因此那种情况是可见的而非静默的，而把
        // 'Honor Map Fixed Time' 关掉仍然是这条路径的一键回滚。
        var fixedHour = 0;
        var fixedMinute = 0;
        var fixedSource = "none";
        var honorFixedTime = FikaPlugin.Instance.Settings.HonorMapFixedTime.Value
            && !FikaBackendUtils.IsScavLateJoin && !FikaBackendUtils.IsTransit
            && MapFixedTime.TryGet(location.Id, out fixedHour, out fixedMinute, out fixedSource);
        var effectiveHourOfDay = honorFixedTime ? fixedHour : timeAndWeather.HourOfDay;
        var fixedField = honorFixedTime
            ? fixedHour.ToString("D2") + ":" + fixedMinute.ToString("D2") + " (" + fixedSource + ")"
            : "none";
        // 随机时间会在 DoWeatherThings 内部重新抽取小时并静默丢弃固定时间，因此它恰好
        // 只在那个采用了固定时间的 raid 上被跳过（见下面的调用与 analysis/map-fixed-time §6.4）。
        var skippedRandom = honorFixedTime && timeAndWeather.IsRandomTime;
        game._logger.LogInfo($"Raid time: hourOfDay={effectiveHourOfDay} " +
            $"({(honorFixedTime ? "map fixed time" : effectiveHourOfDay == -1 ? "current world time" : "custom")}), " +
            $"server={FikaBackendUtils.IsServer}, scav={FikaBackendUtils.IsScav}, " +
            $"worldHour={backendDateTime.StatedGameDateTime.Hour}, " +
            $"fixed={fixedField}, playerHourOfDay={timeAndWeather.HourOfDay}, " +
            $"isRandomTime={timeAndWeather.IsRandomTime}, skippedRandom={skippedRandom}, headless=True");
        if (honorFixedTime)
        {
            game._logger.LogInfo($"[MapFixedTime] {location.Id} carries a fixed clock time: using {fixedField} and ignoring " +
                $"the player's hourOfDay={timeAndWeather.HourOfDay} (headless host)");
        }
        if (effectiveHourOfDay != -1)
        {
            game._logger.LogInfo($"Using custom time, hour of day: {effectiveHourOfDay}");
            var currentTime = backendDateTime.StatedGameDateTime;
            Il2CppSystem.DateTime newTime = new(currentTime.Year, currentTime.Month, currentTime.Day, effectiveHourOfDay,
                currentTime.Minute, currentTime.Second, currentTime.Millisecond);
            gameTime = new(backendDateTime.StatedRealDateTime, newTime, backendDateTime.TimeFactor);
            gameTime.Reset(newTime);
            dateTime = EDateTime.CURR;
        }

        TraceStep("create: wave scaling");
        const float num = 1.5f;
        foreach (var wildSpawnWave in location.waves)
        {
            wildSpawnWave.slots_min = (int)(wildSpawnWave.slots_min * num);
            wildSpawnWave.slots_max = (int)(wildSpawnWave.slots_max * num);
        }

        TraceStep("create: wave scaling ok");
        game.BackendSession = backEndSession;
        game._exitCallback = callback;
        game._location = location;
        game._tarkovDateTime = dateTime;
        game.FixedDeltaTime = fixedDeltaTime;
        TraceStep("create: handle location data");
        game.HandleLocationData(location, wavesSettings.BotAmount);
        TraceStep("create: handle location data ok");
        if (!Singleton<GlobalEventDispatcher>.Instantiated)
        {
            Singleton<GlobalEventDispatcher>.Create(new GlobalEventDispatcher());
        }

        TraceStep("create: game controller ctor");
        game.GameController = new HeadlessGameController(game, updateQueue, gameWorld, backEndSession, location, wavesSettings, gameTime)
        {
            Location = location
        };
        TraceStep("create: game controller ok");
        game.GameDateTime = gameTime;
        game._localRaidSettings = localRaidSettings;
        TraceStep("create: weather");
        // (analysis/map-fixed-time §6.4) 优先级：地图的固定时间优先于随机时间，且仅在那时优先。
        // 当本次 raid 没有采用固定时间时（honorFixedTime == false，即该地图没有固定时间、开关
        // 关闭，或这是一次重新加入/transit），该参数恰好就是 timeAndWeather.IsRandomTime——即原始
        // 语义，未被改动。
        game.DoWeatherThings(timeAndWeather.IsRandomTime && !honorFixedTime, timeAndWeather.IsRandomWeather);
        TraceStep("create: weather ok");
        WorldInteractiveObject.InteractionShouldBeConfirmed = false;

        TraceStep("create: voip distance");
        // 专用主机没有自己的 VOIP 实例；该值只用于提供给
        // game._voipDistance，因此缺少处理器绝不能中止 raid 启动。
        float hearingDistance = 0f;
        if (FikaGlobals.VOIPHandler is { } voipHandler)
        {
            hearingDistance = voipHandler.PushToTalkSettings.HearingDistance;
        }
        else
        {
            game._logger.LogWarning("[Headless] No VOIP handler is instantiated; using a hearing distance of 0");
        }
        game._voipDistance = (hearingDistance * hearingDistance) + 9;

        TraceStep("create: hearing table");
        ClientHearingTable.Instance = game;

        if (game.GameController.IsServer)
        {
            gameWorld.World.RegisterNetworkInteractionObjects();
        }

        TraceStep("create: network interaction objects ok");

        if (timeAndWeather.TimeFlowType != ETimeFlowType.x1)
        {
            var newFlow = timeAndWeather.TimeFlowType.ToTimeFlow();
            gameWorld.GameDateTime.TimeFactor = newFlow;
            game._logger.LogInfo($"Using custom time flow: {newFlow}");
        }

        if (FikaBackendUtils.CustomRaidSettings.UseCustomWeather && game.GameController.IsServer)
        {
            game._logger.LogInfo("Custom weather enabled, initializing curves");
            (game.GameController as HostGameController).SetupCustomWeather(timeAndWeather);
        }

        TraceStep("create: game singleton");
        FikaSingleton<IFikaGame>.Create(game);
        FikaEventDispatcher.DispatchEvent(new FikaGameCreatedEvent(game));

        TraceStep("create: raid controller component");
        game.GameController.RaidSettings = raidSettings;
        game.GameController.ThrownGrenades = [];
        game.gameObject.AddComponent<HeadlessRaidController>();
        game._logger.LogInfo($"[Headless] Raid host game created (locationPtr={(location is null ? "null" : "0x" + location.Pointer.ToInt64().ToString("X"))}, side={localRaidSettings.playerSide}, bots={wavesSettings.BotAmount})");

        return game;
    }

    /// <summary>
    /// Raid 启动追踪。原生帧或异步帧内部的失败只报告
    /// 入口方法，因此 <see cref="Create"/> 中每条有风险的语句都会记录其名称：
    /// 失败前的最后一行就点出了抛异常的那条语句。
    /// </summary>
    private static void TraceStep(string step)
    {
        FikaHeadlessPlugin.FikaHeadlessLogger.LogInfo($"[Headless] Stage(trace): {step}");
    }

    private void HandleLocationData(LocationSettings.Location location, EBotAmount botAmount)
    {
        location.OldSpawn = location.OfflineOldSpawn;
        location.NewSpawn = location.OfflineNewSpawn;
        var num = 1f;
        switch (botAmount)
        {
            case EBotAmount.NoBots:
            case EBotAmount.Low:
                num = Singleton<GlobalConfiguration>.Instance != null ? Singleton<GlobalConfiguration>.Instance.WAVE_COEF_LOW : BotInternalSettingsController.Core.WAVE_COEF_LOW;
                break;
            case EBotAmount.Medium:
                num = Singleton<GlobalConfiguration>.Instance != null ? Singleton<GlobalConfiguration>.Instance.WAVE_COEF_MID : BotInternalSettingsController.Core.WAVE_COEF_MID;
                break;
            case EBotAmount.High:
                num = Singleton<GlobalConfiguration>.Instance != null ? Singleton<GlobalConfiguration>.Instance.WAVE_COEF_HIGH : BotInternalSettingsController.Core.WAVE_COEF_HIGH;
                break;
            case EBotAmount.Horde:
                num = Singleton<GlobalConfiguration>.Instance != null ? Singleton<GlobalConfiguration>.Instance.WAVE_COEF_HORDE : BotInternalSettingsController.Core.WAVE_COEF_HORDE;
                break;
        }

        location.BotMax = (int)(location.BotMax * num);
    }

    private void DoWeatherThings(bool isRandomTime, bool isRandomWeather)
    {
        System.Random random = new();
        if (isRandomTime)
        {
            _dateTime = new DateTime(2016, 4, 30, random.Next(1, 24), random.Next(1, 59), 0, DateTimeKind.Utc);
        }
        else if (!_factoryTimes.TryGetValue(_location.Id, out _dateTime))
        {
            var calculated = GameDateTime.Calculate().ToManaged();
            _dateTime = _tarkovDateTime == EDateTime.CURR ? calculated : calculated.AddHours(12.0);
        }
        GameDateTime = new GameDateTime(GameDateTime.StatedRealDateTime, _dateTime.ToNative(), GameDateTime.TimeFactor, GameDateTime.Debug);
        GameWorld.GameDateTime = GameDateTime;
        if (WeatherController.Instance != null || MonoBehaviourSingleton<TODSkySimple>.Instance != null)
        {
            TODSkyProvider.Instance.CurrentTime.GameDateTime = GameDateTime;
            var randomTestWeatherNodes = WeatherNode.GetRandomTestWeatherNodes(600, 12);
            if (!isRandomWeather)
            {
                var time = randomTestWeatherNodes[0].Time;
                randomTestWeatherNodes[0] = BackendSession.Weather;
                randomTestWeatherNodes[0].Time = time;
            }
            if (WeatherController.Instance != null)
            {
                WeatherController.Instance.SetWeatherNodes(randomTestWeatherNodes);
            }
        }
    }

    public async Task Init(BotControllerSettings botsSettings)
    {
        _logger.LogInfo("Unloading unused resources");
        var unload = Resources.UnloadUnusedAssets();
        while (!unload.isDone)
        {
            await Task.Yield();
        }

        Status = GameStatus.Running;
        UnityEngine.Random.InitState((int)DateTimeExtensions.Now.Ticks);
        _logger.LogInfo($"[Headless] Stage: raid init started (locationPtr={(_location is null ? "null" : "0x" + _location.Pointer.ToInt64().ToString("X"))}, status=Running)");

        await GameController.SetupCoopHandler(this);
        var gameWorld = Singleton<GameWorld>.Instance;
        gameWorld.LocationId = _location.Id;
        _logger.LogInfo("[Headless] Stage: coop handler ready");
        ExfiltrationController.Instance.InitAllExfiltrationPoints(_location._Id, _location.exits, _location.SecretExits,
        !GameController.IsServer, _location.DisabledScavExits);

        _logger.LogInfo($"[Headless] Stage: exfiltration points initialized, location: {_location.Name}");
        var instance = Singleton<GlobalConfiguration>.Instance;

        GameController.InitShellingController(instance, gameWorld, _location);
        GameController.InitHalloweenEvent(instance, gameWorld, _location);
        GameController.InitBTRController(instance, gameWorld, _location);

        if ((FikaBackendUtils.IsHeadless || FikaBackendUtils.IsHeadlessGame) && FikaPlugin.Instance.Settings.EnableTransits)
        {
            GameController.InitializeTransitSystem(gameWorld, instance, null, _localRaidSettings, _location);
        }

        GameController.InitializeRunddans(instance, gameWorld, _location);
        GameController.InitializeKolotun(instance, gameWorld, _location);

        FikaSingleton<FikaServer>.Instance.RaidInitialized = true;

        gameWorld.ClientBroadcastSyncController = new ClientBroadcastSyncController();

        var config = AppEnvironment.Config;
        if (config.FixedFrameRate > 0f)
        {
            FixedDeltaTime = 1f / config.FixedFrameRate;
        }

        GameController.CreateSpawnSystem(null);
        _logger.LogInfo("[Headless] Stage: spawn system created");

        if (FikaSingleton<IFikaNetworkManager>.Instance.AllowVOIP)
        {
            _logger.LogInfo("VOIP enabled, initializing...");
            try
            {
                await FikaSingleton<IFikaNetworkManager>.Instance.InitializeVOIP();
            }
            catch (Exception ex)
            {
                _logger.LogError($"There was an error initializing the VOIP module: {ex.Message}");
            }
        }

        _logger.LogInfo("[Headless] Stage: waiting for the requesting player to start the raid");
        await GameController.WaitForHostToStart();
        _logger.LogInfo("[Headless] Stage: raid start requested by the requester");

        var location = _localRaidSettings.selectedLocation;
        await GameController.InitializeLoot(location);
        await LoadLoot(location);
        _logger.LogInfo("[Headless] Stage: loot spawned");

        GameController.CoopHandler.ShouldSync = true;
        await StartBotSystemsAndCountdown(botsSettings);
        _logger.LogInfo("[Headless] Stage: bots and countdown started");

        (GameController as HeadlessGameController).BotsController.CoversData.Patrols.RestoreLoot(location.Loot,
            LocationScene.GetAllObjects<LootableContainer>(false));
        ServerAirdropManager airdropEventClass = new()
        {
            AirdropParameters = _location.airdropParameters
        };
        airdropEventClass.Init(true);
        // 与 CoopGame 一致：interop 的 TryCast 返回 null 而不是
        // 抛异常，而且世界并不保证是一个 ClientGameWorld
        // （一个 mod，或一条未请求 Fika 世界的创建路径，可以
        // 装入一个普通的 GameWorld）。绝不要直接解引用该结果。
        var clientGameWorld = Singleton<GameWorld>.Instance?.TryCast<ClientGameWorld>();
        if (clientGameWorld != null && clientGameWorld.ClientSynchronizableObjectLogicProcessor != null)
        {
            clientGameWorld.ClientSynchronizableObjectLogicProcessor.ServerAirdropManager = airdropEventClass;
        }
        else
        {
            _logger.LogWarning("[Headless] World is not a ClientGameWorld (or its synchronizable processor is missing); airdrops stay host-local");
        }
        GameWorld.SynchronizableObjectLogicProcessor.AirdropDataSender = FikaSingleton<FikaServer>.Instance;

        var timeBeforeDeployLocal = Singleton<GlobalConfiguration>.Instance.TimeBeforeDeployLocal;
#if DEBUG
        timeBeforeDeployLocal = 3;
#endif

        await RunMemoryCleanup();
        await (GameController as HeadlessGameController).WaitForHeadlessInit(timeBeforeDeployLocal);

        _logger.LogInfo("[Headless] Stage: host ready, deploying players");

        TaskCompletionSource taskCompletionSource = new();
        // 经由托管/引擎值边界路由：一个 yield 出本移植托管
        // WaitSeconds 的例程绝不能把它交给引擎的枚举器包装器（analysis/red-errors-coroutines，E1）。
        StartCoroutine(Fika.Core.EngineCoroutines.Wrap(FinishRaidSetup(() => taskCompletionSource.SetResult(true)), "Fika.Headless.FinishRaidSetup"));
        await taskCompletionSource.Task;

        FikaBackendUtils.GroupPlayers.Clear();

        MonoBehaviourSingleton<EnvironmentUI>.Instance?.ShowEnvironment(false);
        MonoBehaviourSingleton<PreloaderUI>.Instance?.SetMenuTaskBarVisibility(false);

        FikaEventDispatcher.DispatchEvent(new FikaRaidStartedEvent(true));
        _logger.LogInfo("[Headless] Stage: raid is live (players may connect)");

        NetManagerUtils.DisableLoadingScreenUI();

        StartCoroutine(Fika.Core.EngineCoroutines.Wrap(GameController.CreateStashes(), "Fika.Headless.CreateStashes"));

        if (GameController.CoopHandler.HumanPlayers.Count > 0)
        {
            var player = GameController.CoopHandler.HumanPlayers[0];
            // 无头主机完全可能在没有相机的情况下运行（Destroy
            // Graphics、-nographics，或一个从未创建过相机的主机）。这
            // 是为主机自身视角所做的一个装饰性锚定步骤，因此
            // 缺少相机绝不能导致 raid 失败。
            var cameraTransform = CameraManager.Instance != null ? CameraManager.Instance.Camera?.transform : null;
            if (cameraTransform != null)
            {
                cameraTransform.SetParent(player.gameObject.transform, false);
                cameraTransform.localPosition = new(0f, 1.7f, 0f);
                cameraTransform.rotation = Quaternion.identity;
            }
            else
            {
                _logger.LogWarning("[Headless] No camera available to anchor on the first human player; continuing without one");
            }
        }

        StartCoroutine(Fika.Core.EngineCoroutines.Wrap((GameController as HeadlessGameController).SyncTraps(), "Fika.Headless.SyncTraps"));
    }

    private Task RunMemoryCleanup()
    {
        _logger.LogInfo("Running memory cleanup and asset unloading");

        InGameMemoryManagement.RunHeapPreAllocation();
        InGameMemoryManagement.Collect(true);
        InGameMemoryManagement.EmptyWorkingSet();

        InGameMemoryManagement.GCEnabled = false;
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        InitializeCameraAndUnloadAssets();

        return Task.CompletedTask;
    }

    private void InitializeCameraAndUnloadAssets()
    {
        // 故障安全：这会拆除相机、剔除和空间音频
        // 系统。它绝不能运行在一个并非已武装的
        // 专用主机的进程里，无论这个游戏对象由谁创建。
        if (!FikaHeadlessPlugin.IsArmed)
        {
            _logger.LogError("[Headless] Refusing to start: this does not look like a dedicated headless host. " +
                "The graphics and camera teardown was skipped; remove Fika.Headless.dll from a playing install.");
            return;
        }

        // 相机管理器只在原生地图加载创建了它时才存在。
        // 专用主机完全可能确实没有相机，因此报告它并
        // 保持 raid 存活，而不是让初始化失败。
        var cameraManager = CameraManager.Instance;
        if (cameraManager == null)
        {
            _logger.LogWarning("[Headless] CameraManager is not instantiated; skipping camera setup (headless rendering is disabled)");
        }
        else
        {
            if (Singleton<LevelSettings>.Instantiated)
            {
                cameraManager.SetCameraFromSettings(Singleton<LevelSettings>.Instance);
            }
            else
            {
                _logger.LogWarning("[Headless] LevelSettings is not instantiated; skipping camera settings");
            }
            cameraManager.IsActive = true;
        }

        var instance = PerfectCullingAdaptiveGrid.Instance;
        if (instance != null)
        {
            GameObject.Destroy(instance);
        }

        var cameraInstance = PerfectCullingCrossSceneSampler.Instance;
        if (cameraInstance != null)
        {
            var pCCS = cameraInstance.GetComponent<PerfectCullingCrossSceneSampler>();
            if (pCCS != null)
            {
                GameObject.Destroy(pCCS);
            }

            var pCC = cameraInstance.GetComponent<PerfectCullingCamera>();
            if (pCC != null)
            {
                GameObject.Destroy(pCC);
            }
        }

        if (Singleton<SpatialAudioSystem>.Instantiated)
        {
            var spatialAudioSystem = Singleton<SpatialAudioSystem>.Instance;
            var audioRoomStorage = spatialAudioSystem._audioRoomStorage;
            if (audioRoomStorage != null)
            {
                _logger.LogInfo($"SpatialAudio: Destroying {audioRoomStorage._orderedConnections.Count} rooms");
                foreach (var connection in audioRoomStorage._orderedConnections)
                {
                    foreach (var rooms in connection.Value)
                    {
                        foreach (var portal in rooms.GetPortals())
                        {
                            GameObject.Destroy(portal.Cast<MonoBehaviour>());
                        }
                    }

                    if (connection.Key != null)
                    {
                        GameObject.Destroy(connection.Key.Cast<MonoBehaviour>());
                    }
                }
            }
            // 这里调用 Dispose() 方法
            GameObject.Destroy(spatialAudioSystem);
        }
    }

    private IEnumerator FinishRaidSetup(Action complete)
    {
        yield return GameController.FinishRaidSetup();
        yield return FinishHeadlessRaidSetup(complete);
    }

    private IEnumerator FinishHeadlessRaidSetup(Action complete)
    {
        yield return new WaitForSeconds(Singleton<GlobalConfiguration>.Instance.TimeBeforeDeployLocal);
        (GameController as HeadlessGameController).ActivateBots();
        GameController.SetupEventsAndExfils(null);
        complete?.Invoke();
    }

    private async Task StartBotSystemsAndCountdown(BotControllerSettings botsSettings)
    {
        await GameController.StartBotSystemsAndCountdown(botsSettings, GameWorld);
    }

    private async Task LoadLoot(LocationSettings.Location location)
    {
        if (AppEnvironment.Config.NoLootForLocalGame)
        {
            foreach (var lootItemPositionClass in location.Loot.AsManaged()
                .Where(new Func<JsonLootItem, bool>(IsLootItemContainer))
                .ToList()
                )
            {
                var lootContainerItemClass = lootItemPositionClass.Item.TryCast<LootContainer>();
                var grids = lootContainerItemClass.Grids;
                for (var i = 0; i < grids.Length; i++)
                {
                    grids[i].RemoveAll();
                }
                var slots = lootContainerItemClass.Slots;
                for (var i = 0; i < slots.Length; i++)
                {
                    slots[i].RemoveItem(false);
                }
            }
        }

        Item[] array = [.. location.Loot.AsManaged().Select(ItemFromPositionClass)];
        ResourceKey[] array2 = [.. array.Select(item => item.TryCast<ContainerCollection>()).Where(item => item != null)
            .ToNativeList().GetAllItemsFromCollections().AsManaged()
            .Concat(array
                .Where(IsItemSpecialContainer)
            )
            .SelectMany(GetResourceKeys)];
        if (array2.Length != 0)
        {
            var playerLoop = PlayerLoop.GetCurrentPlayerLoop();
            var streamingEntry = FindPlayerLoopEntry(playerLoop, "UpdateTextureStreamingManager", out var streamingIndex);
            var streamingUpdate = IntPtr.Zero;
            if (streamingEntry != null)
            {
                streamingUpdate = SetPlayerLoopUpdate(streamingEntry, streamingIndex, IntPtr.Zero);
                PlayerLoop.SetPlayerLoop(playerLoop);
            }

            LoadingScreenUI.Instance?.UpdateAndBroadcast(50f);
            // (analysis/transit-reload-sync) FikaHostGameWorld 对齐锚点的无头孪生实现：一个已经
            // 结束自身 raid 的客户端会在创建关卡之前等待这个信号，因此它必须在
            // 本主机真正开始加载新地图的那一刻发出。只有仍在继续的 transit 才会宣告
            // （客户端自身的有界兜底会覆盖一个从不宣告的主机），且该调用从不抛异常。
            if (FikaBackendUtils.IsTransit)
            {
                TransitReloadGate.AnnounceHostReloadStart(FikaSingleton<FikaServer>.Instance,
                    _location != null ? _location.Id : "unknown");
            }

            try
            {
                await Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid,
                    ObjectsFactory.AssemblyType.Local, array2.ToNativeList(), JobYieldPriority.General,
                    null, new Il2CppSystem.Threading.CancellationToken()).AsManaged();
            }
            finally
            {
                if (streamingEntry != null)
                {
                    SetPlayerLoopUpdate(streamingEntry, streamingIndex, streamingUpdate);
                    PlayerLoop.SetPlayerLoop(playerLoop);
                }
            }
            LoadingScreenUI.Instance?.UpdateAndBroadcast(75f);
        }
        var questLoot = GameWorld.GetQuestLootReady(location.Loot);
        GameWorld.SpawnLoot(questLoot, true);
    }

    private static Il2CppReferenceArray<PlayerLoopSystem> FindPlayerLoopEntry(PlayerLoopSystem root, string typeName, out int index)
    {
        index = -1;
        foreach (var system in root.subSystemList ?? new Il2CppReferenceArray<PlayerLoopSystem>(0))
        {
            var entries = system.subSystemList;
            if (entries == null)
            {
                continue;
            }

            for (var i = 0; i < entries.Length; i++)
            {
                if (entries[i].type?.Name == typeName)
                {
                    index = i;
                    return entries;
                }
            }
        }

        return null;
    }

    private static IntPtr SetPlayerLoopUpdate(Il2CppReferenceArray<PlayerLoopSystem> entries, int index, IntPtr update)
    {
        var entry = entries[index];
        var previous = entry.updateFunction;
        entry.updateFunction = update;
        entries[index] = entry;
        return previous;
    }

    private IEnumerable<ResourceKey> GetResourceKeys(Item item)
    {
        return item.Template.AllResources.AsManaged();
    }

    private bool IsItemSpecialContainer(Item item)
    {
        return item.TryCast<ContainerCollection>() == null;
    }

    public bool IsLootItemContainer(JsonLootItem x)
    {
        return x.Item.TryCast<LootContainer>() != null;
    }

    public Item ItemFromPositionClass(JsonLootItem x)
    {
        return x.Item;
    }

    public bool IsHeard()
    {
        return false;
    }

    public void ReportAbuse()
    {
        // 什么都不做
    }

    public void RepairLocalHands(bool force = false) { }

    public void RepairLocalInteractionState() { }

    public void RepairLocalInventoryState() { }

    public override void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ClientHearingTable.Instance = null;
        try
        {
            (GameController as HeadlessGameController)?.HeadlessExfilManager?.Stop();
        }
        finally
        {
            NativeBaseCalls.Call70(this);
        }
    }

    public void Stop(string profileId, ExitStatus exitStatus, string exitName, float delay = 0)
    {
        if (Status is GameStatus.Stopping or GameStatus.Stopped)
        {
            return;
        }

        // 在触发事件之前设置状态：一个事件处理器可能在第一次停止仍在
        // 处置玩家时请求第二次停止。
        Status = GameStatus.Stopping;
        _logger.LogInfo($"[Headless] Shutdown: raid stopping (exitStatus={exitStatus}, exitName={exitName ?? "<none>"})");

        var raidDuration = GameTimer != null ? GameTimer.PastTime : new Il2CppSystem.TimeSpan();

        try
        {
            StopRaid(profileId, exitStatus, exitName);
        }
        finally
        {
            Status = GameStatus.Stopped;
            _logger.LogInfo("[Headless] Shutdown: raid stopped, handing control back to the menu lifecycle");
            // OnGameEnd 可能销毁这个游戏。在调用回调之后不要再访问它，
            // 并且只对第一次停止调用该回调。
            _exitCallback?.Invoke(new Result<ExitStatus, Il2CppSystem.TimeSpan, ClientMetrics>(
                exitStatus, raidDuration, null));
        }
    }

    private void StopRaid(string profileId, ExitStatus exitStatus, string exitName)
    {
        StopStep("dispatch game-ended event", () =>
            FikaEventDispatcher.DispatchEvent(new FikaGameEndedEvent(GameController.IsServer, exitStatus, exitName)));

        if (exitStatus < ExitStatus.Transit)
        {
            FikaBackendUtils.IsTransit = false;
        }

        StopStep("update transit state", () =>
        {
            if (FikaBackendUtils.IsTransit)
            {
                var data = FikaBackendUtils.TransitData;
                if (data != null)
                {
                    data.transitionType = ELocationTransition.Common;
                    data.transitionCount++;
                    data.visitedLocations = new Il2CppStringArray([.. data.visitedLocations ?? (IEnumerable<string>)[], GameController.Location.Id]);
                    FikaBackendUtils.TransitData = data;
                }
                else
                {
                    _logger.LogWarning("Stop: Transit was requested without transit data");
                }
            }
            else
            {
                FikaBackendUtils.ResetTransitData();
            }
        });

        _logger.LogDebug("Stop");

        StopStep("destroy debug component", GameController.DestroyDebugComponent);
        StopStep("stop exfiltration manager", () =>
            (GameController as HeadlessGameController)?.HeadlessExfilManager?.Stop());
        StopStep("stop bot system", () => (GameController as HostGameController)?.StopBotsSystem(false));

        StopStep("dispose raid players", () =>
        {
            if (GameController.CoopHandler != null)
            {
                // 生成 mod 可能在玩家处置过程中修改该字典。
                FikaPlayer[] players = [.. GameController.CoopHandler.Players.Values];
                foreach (var player in players)
                {
                    if (player == null)
                    {
                        continue;
                    }

                    try
                    {
                        player.Dispose();
                        AssetPoolObject.ReturnToPool(player.gameObject, true);
                    }
                    catch (Exception ex)
                    {
                        FikaHeadlessPlugin.PlayerDisposeError = true;
                        _logger.LogError($"Stop: There was an error while disposing player: {ex}");
                    }
                }
            }
            else
            {
                _logger.LogError("Stop: Could not find CoopHandler!");
            }
        });

        if (!FikaBackendUtils.IsTransit)
        {
            StopStep("destroy coop handler", () => Destroy(GameController.CoopHandler));
        }

        StopStep("stop game timer", () => GameTimer?.TryStop());
        StopStep("close optional game timer panel", () =>
        {
            var timerPanel = GameUI.Instance?.TimerPanel;
            if (timerPanel != null && timerPanel.isActiveAndEnabled)
                timerPanel.Close();
        });
        StopStep("stop environment manager", () => EnvironmentManager.Instance?.Stop());
        AppEnvironment.Config.UseSpiritPlayer = false;

        StopStep("close optional screens", () => EftScreenManager.Instance?.CloseAllScreensForced());
        if (!FikaBackendUtils.IsTransit)
        {
            StopStep("send raid-leave request", () =>
            {
                var leavingProfileId = FikaBackendUtils.Profile?.ProfileId ?? profileId;
                if (string.IsNullOrEmpty(leavingProfileId))
                {
                    _logger.LogWarning("Stop: No profile ID was available for the raid-leave request");
                    return;
                }
                FikaRequestHandler.RaidLeave(new PlayerLeftRequest(leavingProfileId));
            });
        }

        StopStep("clean up raid controller", CleanUp);
        StopStep("restore optional UI event system", () => UIEventSystem.Instance?.Enable());
    }

    private void StopStep(string description, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Stop: Failed to {description}: {ex}");
        }
    }

    private void CleanUp()
    {
        GameController.CleanUp();
        FikaBackendUtils.CleanUpVariables();
        BTRSide_Patches.Passengers.Clear();
    }

    public override void FixedUpdate()
    {
        // 什么都不做
    }
}



