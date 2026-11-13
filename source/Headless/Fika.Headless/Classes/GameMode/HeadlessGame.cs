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

    // A headless raid has no local player and therefore no player raid ID.
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

        var game = NativeHeadlessGameFactory.Create(updateQueue,
            new Il2CppSystem.Nullable<Il2CppSystem.TimeSpan>(sessionTime.ToNative()));
        game._logger = Logger.CreateLogSource(nameof(HeadlessGame));
        game.GameWorld = gameWorld;

        var gameTime = backendDateTime;
        if (timeAndWeather.HourOfDay != -1)
        {
            game._logger.LogInfo($"Using custom time, hour of day: {timeAndWeather.HourOfDay}");
            var currentTime = backendDateTime.StatedGameDateTime;
            Il2CppSystem.DateTime newTime = new(currentTime.Year, currentTime.Month, currentTime.Day, timeAndWeather.HourOfDay,
                currentTime.Minute, currentTime.Second, currentTime.Millisecond);
            gameTime = new(backendDateTime.StatedRealDateTime, newTime, backendDateTime.TimeFactor);
            gameTime.Reset(newTime);
            dateTime = EDateTime.CURR;
        }

        const float num = 1.5f;
        foreach (var wildSpawnWave in location.waves)
        {
            wildSpawnWave.slots_min = (int)(wildSpawnWave.slots_min * num);
            wildSpawnWave.slots_max = (int)(wildSpawnWave.slots_max * num);
        }

        game.BackendSession = backEndSession;
        game._exitCallback = callback;
        game._location = location;
        game._tarkovDateTime = dateTime;
        game.FixedDeltaTime = fixedDeltaTime;
        game.HandleLocationData(location, wavesSettings.BotAmount);
        if (!Singleton<GlobalEventDispatcher>.Instantiated)
        {
            Singleton<GlobalEventDispatcher>.Create(new GlobalEventDispatcher());
        }

        game.GameController = new HeadlessGameController(game, updateQueue, gameWorld, backEndSession, location, wavesSettings, gameTime)
        {
            Location = location
        };
        game.GameDateTime = gameTime;
        game._localRaidSettings = localRaidSettings;
        game.DoWeatherThings(timeAndWeather.IsRandomTime, timeAndWeather.IsRandomWeather);
        WorldInteractiveObject.InteractionShouldBeConfirmed = false;

        float hearingDistance = FikaGlobals.VOIPHandler.PushToTalkSettings.HearingDistance;
        game._voipDistance = (hearingDistance * hearingDistance) + 9;

        ClientHearingTable.Instance = game;

        if (game.GameController.IsServer)
        {
            gameWorld.World.RegisterNetworkInteractionObjects();
        }

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

        FikaSingleton<IFikaGame>.Create(game);
        FikaEventDispatcher.DispatchEvent(new FikaGameCreatedEvent(game));

        game.GameController.RaidSettings = raidSettings;
        game.GameController.ThrownGrenades = [];
        game.gameObject.AddComponent<HeadlessRaidController>();
        game._logger.LogInfo($"[Headless] Raid host game created for {location.Id} (side={localRaidSettings.playerSide}, bots={wavesSettings.BotAmount})");

        return game;
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
        _logger.LogInfo($"[Headless] Stage: raid init started for {_location?.Id} (status=Running)");

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
        // Mirrors CoopGame: an interop TryCast returns null instead of
        // throwing, and the world is not guaranteed to be a ClientGameWorld
        // (a mod, or a create path that did not request a Fika world, can
        // install a plain GameWorld). Never dereference the result directly.
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
        StartCoroutine(FinishRaidSetup(() => taskCompletionSource.SetResult(true)).WrapToIl2Cpp());
        await taskCompletionSource.Task;

        FikaBackendUtils.GroupPlayers.Clear();

        MonoBehaviourSingleton<EnvironmentUI>.Instance?.ShowEnvironment(false);
        MonoBehaviourSingleton<PreloaderUI>.Instance?.SetMenuTaskBarVisibility(false);

        FikaEventDispatcher.DispatchEvent(new FikaRaidStartedEvent(true));
        _logger.LogInfo("[Headless] Stage: raid is live (players may connect)");

        NetManagerUtils.DisableLoadingScreenUI();

        StartCoroutine(GameController.CreateStashes().WrapToIl2Cpp());

        if (GameController.CoopHandler.HumanPlayers.Count > 0)
        {
            var player = GameController.CoopHandler.HumanPlayers[0];
            // A headless host may run without a camera at all (Destroy
            // Graphics, -nographics, or a host that never created one). This
            // is a cosmetic anchoring step for the host's own view, so a
            // missing camera must never fail the raid.
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

        StartCoroutine((GameController as HeadlessGameController).SyncTraps().WrapToIl2Cpp());
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
        // Fail-safe: this tears down cameras, culling and the spatial audio
        // system. It must never run in a process that is not an armed
        // dedicated host, whatever created this game object.
        if (!FikaHeadlessPlugin.IsArmed)
        {
            _logger.LogError("[Headless] Refusing to start: this does not look like a dedicated headless host. " +
                "The graphics and camera teardown was skipped; remove Fika.Headless.dll from a playing install.");
            return;
        }

        // The camera manager only exists when the native map load created it.
        // A dedicated host can legitimately have no camera, so report it and
        // keep the raid alive instead of failing initialization.
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
            // This calls the Dispose() method
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
        // Do nothing
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

        // Set the state before raising events: an event handler may request a
        // second stop while the first one is still disposing players.
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
            // OnGameEnd may destroy this game. Do not access it after invoking
            // the callback, and invoke the callback only for the first stop.
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
                // Spawn mods may mutate the dictionary while players dispose.
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
        // Do nothing
    }
}



