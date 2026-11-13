using Fika.Core.Main.BaseClasses;
using CommonAssets.Scripts.ArtilleryShelling.Client;
using EFT.InventoryLogic;
using EFT.Vehicle;
using EFT.Weather;
using JsonType;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Audio.AmbientSubsystem;
using BepInEx.Logging;
using Comfort.Common;
using CommonAssets.Scripts.Audio.RadioSystem;
using Dissonance;
using EFT;
using EFT.Bots;
using EFT.CameraControl;
using EFT.Game.Spawning;
using EFT.GlobalEvents;
using EFT.Interactive;
using EFT.UI;
using EFT.UI.Matchmaker;
using EFT.UI.Screens;
using Fika.Core.Bundles;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Components;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.FreeCamera;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.Communication;
using Fika.Core.Networking.Packets.World;
using HarmonyLib;
using UnityEngine.Events;
using static JsonType.LocationSettings;
using ClientTransitController = Fika.Core.Main.ClientClasses.ClientTransitController;
using ClientRunddansController = Fika.Core.Main.ClientClasses.ClientRunddansController;

namespace Fika.Core.Main.GameMode;

public abstract class BaseGameController
{
    protected BaseGameController(IFikaGame game, EUpdateQueue updateQueue, GameWorld gameWorld, IEftSession session)
    {
        _fikaGame = game;
        _abstractGame = game as AbstractGame
            ?? throw new ArgumentException("Fika game must derive from AbstractGame", nameof(game));
        _updateQueue = updateQueue;
        _gameWorld = gameWorld;
        _backendSession = session;
        IsServer = FikaBackendUtils.IsServer;

        Logger = BepInEx.Logging.Logger.CreateLogSource(GetType().Name);
    }

    public ManualLogSource Logger { get; set; }

    public AbstractGame GameInstance
    {
        get
        {
            return _abstractGame;
        }
    }

    protected IFikaGame _fikaGame;
    protected AbstractGame _abstractGame;

    public bool IsServer { get; }
    public bool RaidStarted { get; internal set; }
    public FikaTimeManager TimeManager { get; set; }

    // Weather
    public ESeason Season
    {
        get
        {
            return _season;
        }
        set
        {
            _season = value;
            Logger.LogInfo($"Setting Season to: {value}");
            WeatherReady = true;
        }
    }
    public bool WeatherReady { get; internal set; }
    public WeatherNode[] WeatherClasses { get; set; }
    public SeasonsSettings SeasonsSettings { get; set; }
    public FikaExfilManager ExfilManager { get; set; }

    // Raid data
    public List<ThrowWeap> ThrownGrenades { get; set; }
    public RaidSettings RaidSettings { get; set; }
    public LootData LootItems { get; set; } = new LootData();
    public LocationSettings.Location Location { get; set; }
    public Dictionary<string, Player> Bots = [];
    public CoopHandler CoopHandler
    {
        get
        {
            return _coopHandler;
        }
    }
    public DateTime? GameTime { get; set; }
    public TimeSpan? SessionTime { get; set; }
    public List<string> LocalTriggerZones
    {
        get
        {
            return _localTriggerZones;
        }
    }
    protected List<string> _localTriggerZones = [];

    // Spawns
    public Vector3 ClientSpawnPosition { get; set; }
    public Quaternion ClientSpawnRotation { get; set; }
    public ISpawnSystem SpawnSystem { get; set; }
    public string InfiltrationPoint { get; set; }
    public ISpawnPoint SpawnPoint
    {
        get
        {
            return _spawnPoint;
        }
    }

    private FikaHalloweenEventManager _halloweenEventManager;
    private FikaKolotunEventManager _kolotunEventManager;
    private DebugUI _debugUi;
    private ESeason _season;

    protected SpawnPointsCollection _spawnPoints;
    protected ISpawnPoint _spawnPoint;
    protected Il2CppSystem.Action _btrSpawn;
    // SPT 5's SubscribeOnEvent returns an Action that unregisters the callback.
    // Keep the unsubscribe handle separately from the typed callback itself.
    protected Il2CppSystem.Action _btrServicePurchase;
    protected CoopHandler _coopHandler;
    protected FikaPlayer _localPlayer;
    protected EUpdateQueue _updateQueue;
    protected GameWorld _gameWorld;
    protected IEftSession _backendSession;
    protected Coroutine _extractRoutine;
    private Coroutine _voipAudioFixRoutine;
    private readonly List<GameObject> _mutedVoiceSources = [];

    public void SetLocalPlayer(FikaPlayer player)
    {
        _localPlayer = player;
        _coopHandler.MyPlayer = player;
    }

    /// <summary>
    /// <see cref="Task"/> used to wait for host to start the raid
    /// </summary>
    /// <returns></returns>
    public virtual Task WaitForHostToStart()
    {
        Logger.LogInfo("Starting task to wait for host to start the raid.");
        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_WAIT_FOR_HOST_START_RAID.Localized());

        return Task.CompletedTask;
    }

    /// <summary>
    /// This creates a "custom" Back button so that we can back out if we get stuck
    /// </summary>
    protected GameObject CreateStartButton()
    {
        if (MenuUI.Instantiated)
        {
            var menuUI = MenuUI.Instance;
            var backButton = menuUI.MatchmakerTimeHasCome._cancelButton;
            var customButton = GameObject.Instantiate(backButton.gameObject, backButton.gameObject.transform.parent);
            customButton.gameObject.name = "FikaStartButton";
            customButton.gameObject.SetActive(true);
            var backButtonComponent = customButton.GetComponent<DefaultUIButton>();
            backButtonComponent.SetHeaderText(LocaleUtils.UI_START_RAID.Localized(), 32);
            backButtonComponent.SetEnabledTooltip(LocaleUtils.UI_START_RAID_DESCRIPTION.Localized());
            UnityEvent newEvent = new();
            newEvent.AddListener((UnityAction)(Action)(() =>
            {
                if (IsServer)
                {
                    RaidStarted = true;
                    return;
                }

                var fikaClient = FikaSingleton<FikaClient>.Instance ?? throw new NullReferenceException("CreateStartButton::FikaClient was null!");
                InformationPacket packet = new()
                {
                    RequestStart = true
                };
                fikaClient.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            }));
            backButtonComponent.OnClick = newEvent;

            Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.QuestStarted);

            return customButton;
        }

        return null;
    }

    public abstract IEnumerator WaitForHostInit(int timeBeforeDeployLocal);

    public Vector3 GetSpawnPosition()
    {
        return IsServer ? _spawnPoint.Position : ClientSpawnPosition;
    }

    public Quaternion GetSpawnRotation()
    {
        return IsServer ? _spawnPoint.Rotation : ClientSpawnRotation;
    }

    public virtual void CreateDebugComponent()
    {
        var asset = InternalBundleLoader.Instance.GetFikaAsset(InternalBundleLoader.EFikaAsset.DebugUI);
        var debugObject = GameObject.Instantiate(asset);
        _debugUi = debugObject.GetComponent<DebugUI>();
        debugObject.SetActive(false);
    }

    public Task SetupCoopHandler(IFikaGame fikaGame)
    {
        if (CoopHandler.TryGetCoopHandler(out var coopHandler))
        {
            _coopHandler = coopHandler;
            _coopHandler.LocalGameInstance = fikaGame;
            if (IsServer && FikaBackendUtils.IsTransit)
            {
                coopHandler.ReInitInteractables();
            }

            return Task.CompletedTask;
        }
        else
        {
            throw new NullReferenceException("CoopHandler was missing!");
        }
    }

    public IEnumerator CreateStashes()
    {
        WaitForSeconds waitForSeconds = new(0.5f);
        if (_gameWorld.TransitController != null)
        {
            while (_gameWorld.TransitController.TransferItemsController == null)
            {
                yield return waitForSeconds;
            }

            while (_gameWorld.TransitController.TransferItemsController.Stash == null)
            {
                yield return waitForSeconds;
            }
        }

        if (_gameWorld.BtrController != null)
        {
            while (_gameWorld.BtrController.TransferItemsController == null)
            {
                yield return waitForSeconds;
            }

            while (_gameWorld.BtrController.TransferItemsController.Stash == null)
            {
                yield return waitForSeconds;
            }
        }

        if (_coopHandler != null)
        {
            for (var i = 0; i < _coopHandler.HumanPlayers.Count; i++)
            {
                var player = _coopHandler.HumanPlayers[i];
                try
                {
                    if (_gameWorld.BtrController != null)
                    {
                        _gameWorld.BtrController.TransferItemsController.InitPlayerStash(player);
                    }

                    if (_gameWorld.TransitController != null)
                    {
                        _gameWorld.TransitController.TransferItemsController.InitPlayerStash(player);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Could not initialize transfer stash on {player.Profile.Nickname}: {ex.Message}");
                }
            }

            FikaSingleton<FikaServer>.Instance.SendStashes();
        }
        else
        {
            Logger.LogError("Could not find CoopHandler when trying to initialize player stashes for TransferItemsController!");
        }
    }

    /// <summary>
    /// This task ensures that all players are joined and loaded before continuing
    /// </summary>
    public abstract Task WaitForOtherPlayersToLoad();

    /// <summary>
    /// Runs a few last changes to the raid setup
    /// </summary>
    public virtual IEnumerator FinishRaidSetup()
    {
        if (!FikaBackendUtils.IsHeadless)
            LoadingScreenUI.Instance.UpdateAndBroadcast(90f);

        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_FINISHING_RAID_INIT.Localized());

        WaitForEndOfFrame endOfFrame = new();
        if (!FikaBackendUtils.IsHeadless)
        {
            var musicTask = Singleton<GUISounds>.Instance.FadeBackgroundMusicAsync(false, Il2CppSystem.Threading.CancellationToken.None);
            while (!musicTask.IsCompleted)
                yield return endOfFrame;
        }

        AudioUtils.ResetAudioBuffer();

        _gameWorld.TriggersModule = _abstractGame.gameObject.AddComponent<LocalFikaTriggersModule>();
        _gameWorld.FillLampControllers();
        if (Location.Id == "laboratory")
        {
            Season = ESeason.Summer;
        }
        WeatherReady = true;
        FikaBackendUtils.CustomRaidSettings.UseCustomWeather = false;

        SeasonsController seasonController = new();
        _gameWorld.SeasonsController = seasonController;

        if (!FikaBackendUtils.IsHeadless)
            LoadingScreenUI.Instance.UpdateAndBroadcast(100f);

#if DEBUG
        Logger.LogWarning($"Running season handler for season: {Season}");
#endif
        var runSeason = seasonController.Run(Season, SeasonsSettings, FikaBackendUtils.IsHeadless);
        while (!runSeason.IsCompleted)
        {
            yield return endOfFrame;
        }

        if (MonoBehaviourSingleton<RadioBroadcastController>.Instantiated)
        {
            MonoBehaviourSingleton<RadioBroadcastController>.Instance.StartBroadcast();
        }
    }

    public abstract Task GenerateWeathers();

    public virtual IEnumerator CountdownScreen(Profile profile, string profileId)
    {
        FikaBackendUtils.GroupPlayers.Clear();

        var timeBeforeDeployLocal = FikaBackendUtils.IsReconnect ? 3 : Singleton<GlobalConfiguration>.Instance.TimeBeforeDeployLocal;
#if DEBUG
        timeBeforeDeployLocal = 3;
#endif
        yield return WaitForHostInit(timeBeforeDeployLocal);

        NetManagerUtils.DisableLoadingScreenUI();

        var dateTime = DateTimeExtensions.Now.AddSeconds(timeBeforeDeployLocal);
        new MatchmakerFinalCountdown.FinalCountdownScreenController(profile, dateTime).ShowScreen(EScreenState.Root);
        if (MonoBehaviourSingleton<AmbientAudioSystem>.Instantiated)
        {
            MonoBehaviourSingleton<AmbientAudioSystem>.Instance.Initialize();
        }
        MonoBehaviourSingleton<BetterAudio>.Instance.FadeInVolumeBeforeRaid(timeBeforeDeployLocal);
        Singleton<GUISounds>.Instance.BackgroundMusicActive = false;
        Singleton<GUISounds>.Instance.StopMenuBackgroundMusicWithDelay(timeBeforeDeployLocal);
        _abstractGame.GameUi.gameObject.SetActive(true);
        _abstractGame.GameUi.TimerPanel.ProfileId = profileId;
        yield return new WaitForSeconds(timeBeforeDeployLocal);

        // SPT 5 can leave the final-countdown post-processing/screen state
        // active when Fika bypasses the native LocalGame startup coroutine.
        // The result is a live raid with a permanently blurred background.
        // Restore the raid view after the countdown has elapsed, without
        // touching gameplay effects such as damage/death blur later in raid.
        RestoreRaidViewAfterCountdown();
        // The native countdown screen can apply its camera post-processing one
        // frame after the screen is closed. Re-assert the clear state for a few
        // frames so the first-person view cannot remain permanently blurred.
        _abstractGame.StartCoroutine(RestoreRaidViewAfterCountdownRoutine());

        SyncTransitControllers(profileId);
        FikaEventDispatcher.DispatchEvent(new FikaRaidStartedEvent(FikaBackendUtils.IsServer));

        if (FikaSingleton<IFikaNetworkManager>.Instance.AllowVOIP && !FikaBackendUtils.IsHeadless)
        {
            _voipAudioFixRoutine = _abstractGame.StartCoroutine(FixVOIPAudioDevice());
        }
    }

    private void RestoreRaidViewAfterCountdown(bool initializeOwner = true)
    {
        try
        {
            if (initializeOwner && EftScreenManager.Instance?.CheckCurrentScreen(EEftScreenType.FinalCountdown) == true)
                EftScreenManager.Instance.CloseAllScreensForced();
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Could not close the final countdown screen: {ex.Message}");
        }

        try
        {
            var cameraManager = CameraManager.Instance;
            if (cameraManager != null)
            {
                cameraManager.IsActive = true;
                cameraManager.Blur(false, 0f);
                cameraManager.SetZBlur(false);

                // In SPT 5 the countdown uses CameraManager's InventoryBlur
                // component. Calling Blur(false) starts a native transition,
                // which is not reliable when Fika bypasses LocalGame's normal
                // startup coroutine. Disable the component and clear its value
                // directly as well.
                var inventoryBlur = cameraManager._blur;
                if (inventoryBlur != null)
                {
                    inventoryBlur.Disable();
                    inventoryBlur._value = 0f;
                    inventoryBlur.enabled = false;
                }

                var camera = cameraManager.Camera;
                if (camera != null)
                {
                    camera.enabled = true;
                    camera.gameObject.SetActive(true);
                    // CoopGame disables CameraManager while constructing the
                    // local player.  The stock LocalGame path turns it back on
                    // before the final countdown, but Fika reaches the
                    // countdown through a different path.  Re-enable both
                    // the manager and its camera here so Unity actually
                    // submits the world render.

                    // The native countdown can leave the player camera in a
                    // temporary LockCameraState.  Fika has already spawned
                    // the local player at this point, so explicitly restore
                    // the normal first-person state before the next render.
                    var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
                    if (mainPlayer != null)
                    {
                        mainPlayer.PointOfView = EPointOfView.FirstPerson;
                        if (mainPlayer.PlayerBody != null)
                        {
                            mainPlayer.PlayerBody.PointOfView.Value = EPointOfView.FirstPerson;
                        }
                    }

                    // SPT 5 may leave the native death/fade component in its
                    // enabled state when the Fika countdown replaces the
                    // stock LocalGame startup coroutine.  Clear it together
                    // with FastBlur so the first-person render is restored,
                    // while leaving later in-raid damage effects untouched.
                    var deathFade = camera.GetComponent<DeathFade>();
                    if (deathFade != null)
                    {
                        deathFade.DisableEffectInstant();
                        deathFade._isDead = false;
                        deathFade._fadeValue = 0f;
                        deathFade.enabled = false;
                    }

                    var fastBlur = camera.GetComponent<FastBlur>();
                    if (fastBlur != null)
                    {
                        fastBlur.Reset();
                        fastBlur.enabled = false;
                    }

                    // Clear effect accumulators populated by the final
                    // countdown (grayscale, desaturation, radial blur, etc.)
                    // without disabling gameplay effects permanently.  The
                    // native controller will re-enable these components when
                    // a real health/equipment effect starts during the raid.
                    cameraManager.EffectsController?.ResetEffectsInstant();

                    var grayscale = camera.GetComponent<PostprocessGrayscale>();
                    if (grayscale != null)
                    {
                        grayscale.EffectAmount = 0f;
                    }

                    var distort = camera.GetComponent<DistortCameraFX>();
                    if (distort != null)
                    {
                        distort.Distortion = 0f;
                    }

                    var zBlur = cameraManager.NightVisionZBlur;
                    if (zBlur != null && zBlur.IsApplied)
                    {
                        zBlur.Apply(false);
                    }

                    // A few SPT 5 post-process components can remain enabled
                    // after the menu/countdown camera is torn down.  In an
                    // IL2CPP Fika raid their native materials are not always
                    // initialized, and the component then writes a solid
                    // black frame even though the FPS camera itself is active.
                    // These are menu/countdown presentation effects; gameplay
                    // effects are re-applied by EFT when actually needed.
                    foreach (var effect in camera.GetComponents<MonoBehaviour>())
                    {
                        if (effect == null)
                        {
                            continue;
                        }

                        var effectName = effect.GetType().Name;
                        if (effectName is "ThermalVision" or "NightVision" or "CC_Vintage" or
                            "DesaturateEffect" or "PostprocessGrayscale" or "DistortCameraFX" or
                            "NightVisionZBlur")
                        {
                            effect.enabled = false;
                        }
                    }

                    // GetComponents<MonoBehaviour>() is incomplete for some
                    // IL2CPP proxy components.  Resolve the same effects by
                    // Unity component name as a fallback.
                    foreach (var effectName in new[]
                    {
                        "ThermalVision", "NightVision", "CC_Vintage",
                        "DesaturateEffect", "PostprocessGrayscale",
                        "DistortCameraFX", "NightVisionZBlur"
                    })
                    {
                        var component = camera.GetComponent(effectName);
                        if (component is Behaviour behaviour)
                        {
                            behaviour.enabled = false;
                        }
                    }
                }

                // Clear any duplicate post-processing components left on the
                // camera by the native countdown prefab.
                if (cameraManager.Camera != null)
                {
                    foreach (var blur in cameraManager.Camera.GetComponents<InventoryBlur>())
                    {
                        blur.Disable();
                        blur._value = 0f;
                        blur.enabled = false;
                    }
                }
            }

            if (Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null)
            {
                var player = Singleton<GameWorld>.Instance.MainPlayer;

                if (initializeOwner && _abstractGame is CoopGame coopGame)
                {
                    coopGame.EnsurePlayerOwnerReady();
                }

                player.PointOfView = EPointOfView.FirstPerson;
                if (player.PlayerBody != null)
                {
                    player.PlayerBody.PointOfView.Value = EPointOfView.FirstPerson;
                }
                player.GetComponent<PlayerCameraController>()?.UpdatePointOfView();

                if (FikaSingleton<FreeCameraController>.Instantiated)
                {
                    FikaSingleton<FreeCameraController>.Instance.ForceFirstPersonView();
                }
            }

            // The SPT 5 menu scene can remain alive after the native final
            // countdown closes.  Its MainMenuCamera has a much higher depth
            // than the FPS camera and clears the frame after the raid camera
            // renders, resulting in a completely black raid view.  Fika does
            // not need any menu camera once the local player has spawned, so
            // disable every leftover instance here (and on each retry frame).
            foreach (var menuCamera in UnityEngine.Object.FindObjectsOfType<Camera>())
            {
                if (menuCamera == null || !menuCamera.name.Contains("MainMenuCamera", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                menuCamera.enabled = false;
                menuCamera.gameObject.SetActive(false);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Could not restore the raid camera: {ex.Message}");
        }

        try
        {
            var preloader = MonoBehaviourSingleton<PreloaderUI>.Instance;
            if (preloader != null)
            {
                preloader.SetBlackImageAlpha(0f);
                if (preloader._overlapBlackImage != null)
                {
                    preloader._overlapBlackImage.gameObject.SetActive(false);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Could not clear the preloader overlay: {ex.Message}");
        }
    }

    private IEnumerator RestoreRaidViewAfterCountdownRoutine()
    {
        // Run after the native screen's close/LateUpdate callbacks too.
        // SPT 5 may finish the player-owner setup one or more frames after
        // the countdown screen closes.  Keep reasserting the local owner,
        // first-person visuals and camera for a short bounded window so late
        // native callbacks cannot leave the player unbound.
        for (var i = 0; i < 120; i++)
        {
            yield return null;
            // Once a menu opens or the player leaves first-person gameplay,
            // its input and camera state belongs to the native UI. Do not
            // close it, reshow BattleUI, or undo its blur/cursor state.
            if (EftScreenManager.Instance?.CheckCurrentScreen(EEftScreenType.BattleUI) != true ||
                _localPlayer?.HealthController?.IsAlive != true ||
                (FikaSingleton<FreeCameraController>.Instantiated &&
                 FikaSingleton<FreeCameraController>.Instance.IsScriptActive))
                yield break;
            RestoreRaidViewAfterCountdown(initializeOwner: false);
        }
    }

    private IEnumerator FixVOIPAudioDevice()
    {
        // Todo: Find root causes and fix elegantly...
        var comms = DissonanceComms.Instance;
        if (comms == null)
        {
            _voipAudioFixRoutine = null;
            yield break;
        }
        comms.IsMuted = false;
        yield return new WaitForSeconds(1);
        // The user can start push-to-talk during this delay. Do not mute the
        // native capture after the VOIP controller has entered Talking.
        if (comms != null && !(_localPlayer?.IsVoipTalking ?? false))
            comms.IsMuted = true;

        if (_coopHandler == null)
        {
            _voipAudioFixRoutine = null;
            yield break;
        }

        _mutedVoiceSources.Clear();
        for (var i = 0; i < _coopHandler.HumanPlayers.Count; i++)
        {
            var player = _coopHandler.HumanPlayers[i];
            if (player.IsYourPlayer)
            {
                continue;
            }
            if (player.VoipAudioSource == null)
            {
                Logger.LogError($"FixVOIPAudioDevice: VoipAudioSource was null for {player.Profile.Nickname}");
                continue;
            }
            var source = player.VoipAudioSource.gameObject;
            if (source.activeSelf)
            {
                source.SetActive(false);
                _mutedVoiceSources.Add(source);
            }
        }

        yield return new WaitForSeconds(2);

        foreach (var source in _mutedVoiceSources)
            if (source != null) source.SetActive(true);
        _mutedVoiceSources.Clear();
        _voipAudioFixRoutine = null;
    }

    private void SyncTransitControllers(string profileId)
    {
        var transitController = Singleton<GameWorld>.Instance.TransitController;
        if (transitController == null)
        {
            if (FikaPlugin.Instance.Settings.EnableTransits)
            {
                Logger.LogError("SyncTransitControllers: TransitController was null!");
            }
            return;
        }

        if (transitController.summonedTransits.TryGetValue(profileId, out var transitData))
        {
            SyncTransitControllersPacket packet = new()
            {
                ProfileId = profileId,
                RaidId = transitData.raidId,
                Count = transitData.count,
                Maps = transitData.maps,
                Events = transitData.events
            };

            FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            return;
        }

        Logger.LogError("SyncTransitControllers: Could not find TransitData in Summonedtransits!");
    }

    public abstract Task ReceiveSpawnPoint(Profile profile);

    public abstract void CreateSpawnSystem(Profile profile);

    public void InitShellingController(GlobalConfiguration instance, GameWorld gameWorld, LocationSettings.Location location)
    {
        if (instance != null && instance.ArtilleryShelling != null && instance.ArtilleryShelling.ArtilleryMapsConfigs?.ContainsKey(location.Id) == true)
        {
            if (IsServer)
            {
                gameWorld.ServerShellingController = new ArtilleryShellingControllerServer();
            }
            gameWorld.ClientShellingController = new ArtilleryShellingControllerClient(IsServer);
        }
    }

    public void InitHalloweenEvent(GlobalConfiguration instance, GameWorld gameWorld, LocationSettings.Location location)
    {
        if (instance != null && instance.EventSettings.EventActive && !instance.EventSettings.LocationsToIgnore.Contains(location.Id))
        {
#if DEBUG
            Logger.LogWarning("Spawning halloween prefabs");
#endif
            gameWorld.HalloweenEventController = new HalloweenEventController();
            var gameObject = (GameObject)Resources.Load("Prefabs/HALLOWEEN_CONTROLLER");
            if (gameObject != null)
            {
                _abstractGame.transform.InstantiatePrefab(gameObject);
            }
            else
            {
                Logger.LogError("InitHalloweenEvent: Error loading Halloween assets!");
            }

            if (IsServer)
            {
                _halloweenEventManager = gameWorld.gameObject.GetOrAddComponent<FikaHalloweenEventManager>();
            }
        }
    }

    public void InitBTRController(GlobalConfiguration instance, GameWorld gameWorld, LocationSettings.Location location)
    {
        if (FikaPlugin.Instance.Settings.UseBTR)
        {
            if (instance != null)
            {
                if (instance.BTRSettings.LocationsWithBTR.Contains(location.Id))
                {
                    Logger.LogInfo("Loading BTR data...");
#if DEBUG
                    Logger.LogWarning("Spawning BTR controller and setting spawn chance to 100%");
                    var settings = Singleton<GlobalConfiguration>.Instance.BTRLocalSettings;
                    var mapSettings = settings.ServerMapBTRSettings.First(x => x.Value.MapID == gameWorld.LocationId);
                    var btrSettings = mapSettings.Value;
                    btrSettings.ChanceSpawn = 100;
                    btrSettings.SpawnPeriod = new(5, 10);
                    btrSettings.MoveSpeed = 32f;
                    btrSettings.PauseDurationRange = new(595, 600);
                    settings.ServerMapBTRSettings[mapSettings.Key] = btrSettings;
#endif
                    gameWorld.BtrController = new BtrController(gameWorld);
                    if (IsServer)
                    {
                        // The IL2CPP wrapper does not implicitly convert a managed method
                        // group to Il2CppSystem.Action<T>; create the bridge explicitly.
                        Action<BtrSpawnOnThePathEvent> spawnManaged = OnBtrSpawn;
                        Il2CppSystem.Action<BtrSpawnOnThePathEvent> spawn = spawnManaged;
                        Action<BtrServicePurchaseEvent> purchaseManaged = OnBtrServicePurchase;
                        Il2CppSystem.Action<BtrServicePurchaseEvent> purchase = purchaseManaged;
                        _btrSpawn = GlobalEventsController.Instance.SubscribeOnEvent(spawn);
                        _btrServicePurchase = GlobalEventsController.Instance.SubscribeOnEvent(purchase);
                    }
                }
            }
            else
            {
                Logger.LogError("InitBTRController::GlobalConfiguration was missing when initializing BTR!");
            }
        }
    }

    private void OnBtrSpawn(BtrSpawnOnThePathEvent spawnEvent)
    {
        Logger.LogInfo("BTR spawned, notifying clients");
        FikaSingleton<FikaServer>.Instance.BroadcastBtrSpawn(spawnEvent);
    }

    private void OnBtrServicePurchase(BtrServicePurchaseEvent purchaseEvent)
    {
        if (!IsServer || purchaseEvent?.PlayerRaidIds == null || purchaseEvent.PlayerRaidIds.Length == 0) return;
        var writer = NetworkUtils.EventDataWriter;
        writer.Reset();
        purchaseEvent.Serialize(ref writer);
        writer.Flush();
        var data = new byte[writer.BytesWritten];
        Array.Copy(writer.Buffer, data, data.Length);
        SyncEventPacket packet = new() { Type = 2, Data = data };
        FikaSingleton<FikaServer>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
    }

    /// <summary>
    /// Initializes the transit system TODO: Add headless variant
    /// </summary>
    /// <param name="gameWorld"></param>
    /// <param name="instance"></param>
    /// <param name="profile"></param>
    /// <param name="localRaidSettings"></param>
    /// <param name="location"></param>
    public virtual void InitializeTransitSystem(GameWorld gameWorld, GlobalConfiguration instance, Profile profile,
        LocalRaidSettings localRaidSettings, LocationSettings.Location location)
    {
        bool transitActive;
        if (instance == null)
        {
            transitActive = false;
        }
        else
        {
            var transitSettings = instance.transitSettings;
            transitActive = transitSettings != null && transitSettings.active;
        }
        if (transitActive)
        {
            gameWorld.TransitController = IsServer
                ? new FikaHostTransitController(instance.transitSettings, location.transitParameters, profile, localRaidSettings).Native
                : new ClientTransitController(instance.transitSettings, location.transitParameters, profile, localRaidSettings).Native;
        }
        else
        {
            Logger.LogInfo("Transits are disabled");
            EFT.TransitController.DisableTransitPoints();
        }
    }

    public void InitializeRunddans(GlobalConfiguration instance, GameWorld gameWorld, LocationSettings.Location location)
    {
        bool runddansActive;
        if (instance == null)
        {
            runddansActive = false;
        }
        else
        {
            var runddansSettings = instance.runddansSettings;
            runddansActive = runddansSettings != null && runddansSettings.active;
        }
        if (runddansActive)
        {
            gameWorld.RunddansController = IsServer ? new HostRunddansController(instance.runddansSettings, location).Native
                : new ClientRunddansController(instance.runddansSettings, location).Native;
            Logger.LogInfo("Created RunddansController");
        }
        else
        {
            RunddansController.ToggleEventEnvironment(false);
        }
    }

    public void InitializeKolotun(GlobalConfiguration instance, GameWorld gameWorld, LocationSettings.Location location)
    {
        var settings = instance?.kolotunSettings;
        var locationSettings = _backendSession?.TryCast<ClientBackendSession>()?.LocationSettings;
        if (settings?.active == true && locationSettings != null &&
            KolotunController.IsWorkOnLocation(location, locationSettings))
        {
            gameWorld.KolotunController = IsServer
                ? new LocalKolotunController(settings, location, locationSettings)
                : new NetworkKolotunController(settings, location, locationSettings);
            if (IsServer)
                _kolotunEventManager = gameWorld.gameObject.GetOrAddComponent<FikaKolotunEventManager>();
            Logger.LogInfo("Created KolotunController");
        }
        else
        {
            KolotunController.ToggleEventEnvironment(false);
            KolotunController.DisableEventObjects();
            if (settings?.active == true && locationSettings == null)
                Logger.LogError("Kolotun is active but location settings are unavailable");
        }
    }

    public abstract Task InitializeLoot(LocationSettings.Location location);

    public Task SetupRaidCode()
    {
        var raidCode = FikaBackendUtils.RaidCode;
        if (!string.IsNullOrEmpty(raidCode))
        {
            if (!FikaBackendUtils.IsHeadless)
                MonoBehaviourSingleton<PreloaderUI>.Instance.SetSessionId(raidCode);

            Logger.LogInfo($"MatchingType: {FikaBackendUtils.ClientType}, Raid Code: {raidCode}");
        }

        return Task.CompletedTask;
    }

    public abstract void SetupEventsAndExfils(Player player);

    public abstract void Extract(FikaPlayer player, ExfiltrationPoint exfiltrationPoint, TransitPoint transitPoint = null);

    /// <summary>
    /// Used to make sure no stims or mods reset the DamageCoeff
    /// </summary>
    /// <param name="player">The <see cref="FikaPlayer"/> to run the coroutine on</param>
    /// <returns></returns>
    protected IEnumerator ExtractRoutine(FikaPlayer player, CoopGame coopGame)
    {
        WaitForEndOfFrame waitForEndOfFrame = new();
        while (coopGame.Status != GameStatus.Stopping)
        {
            if (player != null && player.ActiveHealthController != null)
            {
                if (player.ActiveHealthController.DamageCoeff != 0)
                {
                    player.ActiveHealthController.SetDamageCoeff(0);
                }
            }
            else
            {
                yield break;
            }
            yield return waitForEndOfFrame;
        }
    }

    /// <summary>
    /// Toggles the <see cref="DebugUI"/> menu
    /// </summary>
    /// <param name="enabled"></param>
    public void ToggleDebug(bool enabled)
    {
        if (_debugUi != null)
        {
            _debugUi.gameObject.SetActive(enabled);
        }
    }

    public void DestroyDebugComponent()
    {
        if (_debugUi != null)
        {
            ToggleDebug(false);
            GameObject.Destroy(_debugUi.gameObject);
            _debugUi = null;
        }
    }

    public virtual void CleanUp()
    {
        if (_voipAudioFixRoutine != null)
        {
            _abstractGame.StopCoroutine(_voipAudioFixRoutine);
            _voipAudioFixRoutine = null;
        }
        foreach (var source in _mutedVoiceSources)
            if (source != null) source.SetActive(true);
        _mutedVoiceSources.Clear();
        ThrownGrenades?.Clear();

        if (_btrSpawn != null)
        {
            _btrSpawn?.Invoke();
            _btrSpawn = null;
        }
        if (_btrServicePurchase != null)
        {
            _btrServicePurchase?.Invoke();
            _btrServicePurchase = null;
        }

        if (_extractRoutine != null)
        {
            _abstractGame.StopCoroutine(_extractRoutine);
        }

        if (!FikaBackendUtils.IsTransit)
        {
            FikaBackendUtils.CustomRaidSettings.Reset();
        }
    }

    public abstract Task StartBotSystemsAndCountdown(BotControllerSettings controllerSettings, GameWorld gameWorld);

    public void SetClientTime(DateTime gameTime, TimeSpan sessionTime, GameDateTime gameDateTime)
    {
        GameTime = gameTime;
        SessionTime = sessionTime;
        if (_abstractGame is CoopGame coopGame)
        {
            Logger.LogInfo($"Received date from server, was [{coopGame.GameDateTime.Calculate().ToManaged():G}] - new [{gameDateTime.Calculate().ToManaged():G}]");
            coopGame.GameDateTime = gameDateTime;
            coopGame.GameWorld.GameDateTime = gameDateTime;
        }
    }
}

