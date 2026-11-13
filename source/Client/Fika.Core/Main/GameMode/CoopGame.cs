using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Audio.RadioSystem;
using BepInEx.Logging;
using Comfort.Common;
using CommonAssets.Scripts.Game;
using Dissonance.Integrations.MirrorIgnorance;
using Dissonance.Networking.Client;
using Diz.Jobs;
using EFT;
using EFT.Airdrop;
using EFT.AssetsManager;
using EFT.Bots;
using EFT.CameraControl;
using EFT.Communications;
using EFT.EnvironmentEffect;
using EFT.Game.Spawning;
using EFT.HealthSystem;
using EFT.InputSystem;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Insurance;
using EFT.UI.Screens;
using EFT.Utilities;
using EFT.Weather;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Components;
using Fika.Core.Main.Patches.BTR;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using Fika.Core.Networking.Packets.Communication;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.World;
using Fika.Core.UI.Models;
using HarmonyLib;
using JsonType;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using EFT.UI.Matchmaker;

namespace Fika.Core.Main.GameMode;

/// <summary>
/// Coop game used in Fika
/// </summary>
public sealed class CoopGame : BaseLocalGame<EftGamePlayerOwner>, IFikaGame, IClientHearingTable
{
    public CoopGame(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<BaseLocalGame<EftGamePlayerOwner>>(this);
    }


    public CoopGame() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<CoopGame>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    /// <summary>
    /// Invoked when the main player extracts
    /// </summary>
    /// <remarks>Does not run on headless clients as there is no player</remarks>
    public static Action<FikaPlayer> MainPlayerExtracted { get; set; }

    public BaseGameController GameController { get; set; }
    public ExitStatus ExitStatus { get; set; } = ExitStatus.Survived;
    public string ExitLocation { get; set; }
    public List<int> ExtractedPlayers { get; } = [];

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

    private static ManualLogSource _logger;

    private Func<LocalPlayer, EftGamePlayerOwner> _func_1;
    private FikaPlayer _localPlayer;
    private string _pendingReconnectProfileId;
    private ReconnectInventoryState _reconnectInventoryState;
    private ReconnectHandsState _reconnectHands;
    private bool _hasReconnectPosition;
    private bool _hasReconnectHands;
    private bool _reconnectWorldRequested;
    private ushort _lastReconnectFirstOperationId = ushort.MaxValue;
    private readonly RevivalStateClock _reconnectRevivalClock = new();
    private readonly RaidSaveState _deathSave = new();
    private float _voipDistance;
    private Il2CppSystem.Action<EDamageType> _onLocalPlayerDied;
    private float _nextHandsRepairTime;
    private float _nextInteractionRepairTime;
    private float _stuckInteractionSince;
    private int _handsRepairAttempts;
    private bool _handsRepairInFlight;

    // Hands lock watchdog ("operating too fast"). The timers live here so the
    // per-frame plugin hook can keep calling one watchdog per concern.
    private float _stuckHandsSince;
    private float _nextHandsLockRepairTime;
    private int _handsLockRepairs;
    private float _nextInventoryGateCheckTime;
    private float _inventoryGateBlockedSince;
    private int _inventoryGateRepairRounds;

    public EftGamePlayerOwner DiagnosticPlayerOwner => _playerOwner;

    /// <summary>
    /// Creates a <see cref="CoopGame"/>
    /// </summary>
    internal static CoopGame Create(IInputTree inputTree, Profile profile, GameWorld gameWorld, GameDateTime backendDateTime,
        InsuranceCompany insurance, GameUI gameUI, LocationSettings.Location location,
        TimeAndWeatherSettings timeAndWeather, WavesSettings wavesSettings, EDateTime dateTime,
        Callback<ExitStatus, Il2CppSystem.TimeSpan, ClientMetrics> callback, float fixedDeltaTime, EUpdateQueue updateQueue,
        IEftSession backEndSession, TimeSpan sessionTime, ClientMetricsEvents metricsEvents,
        ClientMetricsCollector metricsCollector, LocalRaidSettings localRaidSettings, RaidSettings raidSettings)
    {
        _logger = Logger.CreateLogSource("CoopGame");

        FikaSingleton<IFikaNetworkManager>.Instance.RaidSide = localRaidSettings.playerSide;

        var gameTime = backendDateTime;
        _logger.LogInfo($"Raid time: hourOfDay={timeAndWeather.HourOfDay} " +
            $"({(timeAndWeather.HourOfDay == -1 ? "current world time" : "custom")}), " +
            $"server={FikaBackendUtils.IsServer}, scav={FikaBackendUtils.IsScav}, " +
            $"worldHour={backendDateTime.StatedGameDateTime.Hour}");
        if (timeAndWeather.HourOfDay != -1)
        {
            var currentTime = backendDateTime.StatedGameDateTime;
            Il2CppSystem.DateTime newTime = new(currentTime.Year, currentTime.Month, currentTime.Day, timeAndWeather.HourOfDay,
                currentTime.Minute, currentTime.Second, currentTime.Millisecond);
            gameTime = new(backendDateTime.StatedRealDateTime, newTime, backendDateTime.TimeFactor);
            gameTime.Reset(newTime);
            dateTime = EDateTime.CURR;
        }

        var coopGame = NativeCoopGameFactory.Create(inputTree, profile, gameWorld, gameTime, insurance, gameUI,
            location, timeAndWeather, wavesSettings, dateTime, callback, fixedDeltaTime, updateQueue, backEndSession,
            new Il2CppSystem.Nullable<Il2CppSystem.TimeSpan>(sessionTime.ToNative()), metricsEvents, metricsCollector, localRaidSettings);
        coopGame.GameController = FikaBackendUtils.IsServer ? new HostGameController(coopGame, updateQueue, gameWorld, backEndSession, location, wavesSettings, coopGame.GameDateTime)
            : new ClientGameController(coopGame, updateQueue, gameWorld, backEndSession);
        coopGame.GameController.Location = location;

        float hearingDistance = FikaGlobals.VOIPHandler.PushToTalkSettings.HearingDistance;
        coopGame._voipDistance = (hearingDistance * hearingDistance) + 9;

        ClientHearingTable.Instance = coopGame;

        if (coopGame.GameController.IsServer)
        {
            gameWorld.World.RegisterNetworkInteractionObjects();
        }

        if (timeAndWeather.TimeFlowType != ETimeFlowType.x1)
        {
            var newFlow = timeAndWeather.TimeFlowType.ToTimeFlow();
            coopGame.GameWorld.GameDateTime.TimeFactor = newFlow;
            _logger.LogInfo($"Using custom time flow: {newFlow}");
        }

        if (FikaBackendUtils.CustomRaidSettings.UseCustomWeather && coopGame.GameController.IsServer)
        {
            _logger.LogInfo("Custom weather enabled, initializing curves");
            if (coopGame.GameController is HostGameController hostGameController)
                hostGameController.SetupCustomWeather(timeAndWeather);
        }

        SetupGamePlayerOwnerHandler setupGamePlayerOwnerHandler = new(inputTree, insurance, backEndSession, gameUI, coopGame, location);
        coopGame._func_1 = setupGamePlayerOwnerHandler.HandleSetup;

        FikaSingleton<IFikaGame>.Create(coopGame);
        FikaEventDispatcher.DispatchEvent(new FikaGameCreatedEvent(coopGame));

        var endByExitTrigger = coopGame.GetComponent<EndByExitTrigerScenario>();
        var endByTimerScenario = coopGame.GetComponent<EndByTimerScenario>();

        if (endByExitTrigger != null)
        {
            Destroy(endByExitTrigger);
        }
        if (endByTimerScenario != null)
        {
            Destroy(endByTimerScenario);
        }

        coopGame.GameController.TimeManager = FikaTimeManager.Create(coopGame);
        coopGame.GameController.RaidSettings = raidSettings;
        coopGame.GameController.ThrownGrenades = [];

        return coopGame;
    }

    /// <summary>
    /// Used to create a <see cref="EftGamePlayerOwner"/>
    /// </summary>
    /// <param name="inputTree"></param>
    /// <param name="insurance"></param>
    /// <param name="backEndSession"></param>
    /// <param name="gameUI"></param>
    /// <param name="game"></param>
    /// <param name="location"></param>
    private class SetupGamePlayerOwnerHandler(IInputTree inputTree, InsuranceCompany insurance, IEftSession backEndSession, GameUI gameUI, CoopGame game, LocationSettings.Location location)
    {
        private readonly IInputTree _inputTree = inputTree;
        private readonly InsuranceCompany _insurance = insurance;
        private readonly IEftSession _backEndSession = backEndSession;
        private readonly GameUI _gameUI = gameUI;
        private readonly CoopGame _game = game;
        private readonly LocationSettings.Location _location = location;

        public EftGamePlayerOwner HandleSetup(LocalPlayer player)
        {
            _game.LocalPlayer = player;
            var gamePlayerOwner = EftGamePlayerOwner.Create(player, _inputTree, _insurance, _backEndSession,
                _gameUI, _game.GameDateTime, _location);
            gamePlayerOwner.OnLeave += (Il2CppSystem.Action)_game.LocalLeaveGame;
            return gamePlayerOwner;
        }
    }

    public override void SetMatchmakerStatus(EMatchingStage stage, Il2CppSystem.Nullable<float> progress = default)
    {
        MatchingStatusText = null;
        InvokeMatchingStatusChanged(stage, progress ?? new Il2CppSystem.Nullable<float>());
    }

    internal string MatchingStatusText { get; private set; }

    public void SetMatchmakerStatus(string status, float? progress = null)
    {
        MatchingStatusText = status;
        InvokeMatchingStatusChanged(EMatchingStage.SynchronizationWithOtherPlayers,
            progress.HasValue ? new Il2CppSystem.Nullable<float>(progress.Value) : new Il2CppSystem.Nullable<float>());
    }

    /// <summary>
    /// The countdown deploy screen
    /// </summary>
    public override Il2CppSystem.Collections.IEnumerator InitSessionUI() => InitSessionUIManaged().WrapToIl2Cpp();

    private IEnumerator InitSessionUIManaged()
    {
        yield return GameController.CountdownScreen(Profile, ProfileId);
    }

    public override Il2CppSystem.Threading.Tasks.Task<LocalPlayer> PlayerFactory(GameWorld gameWorld, int playerId, Vector3 position, Quaternion rotation,
        string layerName, string prefix, EPointOfView pointOfView, Profile profile, bool aiControl,
        EUpdateQueue updateQueue, Player.EUpdateMode armsUpdateMode, Player.EUpdateMode bodyUpdateMode,
        CharacterControllerSpawner.Mode characterControllerMode, Il2CppSystem.Func<float> getSensitivity,
        Il2CppSystem.Func<float> getAimingSensitivity, IStatisticsManager statisticsManager, IEftSession session,
        ELocalMode localMode)
    {
        return NativeTask.FromManaged(MainThreadDispatcher.RunAsync(() => CreatePlayerManaged(gameWorld, playerId, position, rotation, layerName, prefix,
            pointOfView, profile, aiControl, updateQueue, armsUpdateMode, bodyUpdateMode, characterControllerMode,
            () => getSensitivity.Invoke(), () => getAimingSensitivity.Invoke(), statisticsManager, session, localMode)));
    }

    private async Task<LocalPlayer> CreatePlayerManaged(GameWorld gameWorld, int playerId, Vector3 position, Quaternion rotation,
        string layerName, string prefix, EPointOfView pointOfView, Profile profile, bool aiControl,
        EUpdateQueue updateQueue, Player.EUpdateMode armsUpdateMode, Player.EUpdateMode bodyUpdateMode,
        CharacterControllerSpawner.Mode characterControllerMode, Func<float> getSensitivity,
        Func<float> getAimingSensitivity, IStatisticsManager statisticsManager, IEftSession session,
        ELocalMode localMode)
    {
        if (!TransitController.IsTransit(profile.Id, out int _) && !FikaBackendUtils.IsReconnect)
        {
            profile.SetSpawnedInSession(false);
        }

        var fikaPlayer = await FikaPlayer.Create(gameWorld, playerId, position, rotation, "Player", "Main_", EPointOfView.FirstPerson,
            profile, false, UpdateQueue, armsUpdateMode, Player.EUpdateMode.Auto,
            AppEnvironment.Config.CharacterController.ClientPlayerMode, getSensitivity, getAimingSensitivity,
            statisticsManager, new ClientViewFilter(), session, playerId, FikaSingleton<IFikaNetworkManager>.Instance.StrictInventorySync,
            FikaBackendUtils.IsReconnect
                ? _reconnectInventoryState ?? throw new InvalidOperationException("Reconnect inventory snapshot is missing")
                : null);
        _reconnectInventoryState = null;

        if (FikaBackendUtils.IsReconnect)
        {
            _reconnectHands?.RestoreLastItem(fikaPlayer);
            fikaPlayer.StageReconnectRevival(_reconnectRevivalClock.Capture());
        }

        fikaPlayer.Location = Location.Id;
        var coopHandler = GameController.CoopHandler;

        if (coopHandler == null)
        {
            _logger.LogError("PlayerFactory: CoopHandler was null!");
            throw new InvalidOperationException("CoopHandler was missing during CoopGame init");
        }

        if (GameController.RaidSettings.MetabolismDisabled)
        {
            fikaPlayer.HealthController.DisableMetabolism();
            FikaNotifications.DisplayMessageNotification(LocaleUtils.METABOLISM_DISABLED.Localized(), iconType: EFT.Communications.ENotificationIconType.Alert);
        }

        coopHandler.Players.Add(fikaPlayer.NetId, fikaPlayer);
        coopHandler.HumanPlayers.Add(fikaPlayer);
        fikaPlayer.SetupMainPlayer();

        PlayerSpawnRequest body = new(FikaBackendUtils.Profile.ProfileId, FikaBackendUtils.GroupId);
        await FikaRequestHandler.UpdatePlayerSpawn(body);

        fikaPlayer.SpawnPoint = GameController.SpawnPoint;

        await NetManagerUtils.SetupGameVariables(fikaPlayer);

        if (!GameController.IsServer && !FikaBackendUtils.IsReconnect)
        {
            AnnounceCharacter(fikaPlayer);
        }
        else
        {
            FikaGlobals.LogInfo($"Not announcing a character: isServer={GameController.IsServer}, " +
                $"isReconnect={FikaBackendUtils.IsReconnect}, scavLateJoin={FikaBackendUtils.IsScavLateJoin}");
        }

        _logger.LogInfo("Adding debug component...");
        GameController.CreateDebugComponent();

        if (FikaBackendUtils.IsReconnect && _hasReconnectPosition)
        {
            fikaPlayer.Teleport(FikaBackendUtils.ReconnectPosition);
            fikaPlayer.MovementContext.Rotation = FikaBackendUtils.ReconnectRotation;
            fikaPlayer.MovementContext.CachedRotation = FikaBackendUtils.ReconnectRotation;
            _hasReconnectPosition = false;
        }

        return fikaPlayer;
    }

    private Action StopFromCancel(Player myPlayer)
    {
        StopFromCancel(myPlayer.ProfileId, ExitStatus.Runner);
        PlayerLeftRequest playerLeftRequest = new(FikaBackendUtils.Profile.ProfileId);
        FikaRequestHandler.RaidLeave(playerLeftRequest);
        return null;
    }

    /// <summary>
    /// Initializes the local player, replaces <see cref="BaseLocalGame{TPlayerOwner}.method_4(BotControllerSettings, string, InventoryController)"/>
    /// </summary>
    public async Task InitPlayer(BotControllerSettings botsSettings)
    {
        Status = GameStatus.Running;
        UnityEngine.Random.InitState((int)DateTimeExtensions.Now.Ticks);

        if (!GameController.IsServer)
        {
        if (GameController is ClientGameController clientGameController)
            await clientGameController.WaitForHostToLoad();
        }

        _logger.LogInfo("Creating CoopHandler");
        await GameController.SetupCoopHandler(this);

        var gameWorld = Singleton<GameWorld>.Instance;
        gameWorld.LocationId = Location.Id;

        _logger.LogInfo($"Initializing Exfils: Id {Location.Id}, Exits: {Location.exits?.Length ?? 0}, SecretExits: {Location.SecretExits?.Length ?? 0}");

        ExfiltrationController.Instance.InitAllExfiltrationPoints(Location._Id, Location.exits, Location.SecretExits,
            !GameController.IsServer, Location.DisabledScavExits);

        _logger.LogInfo($"Location: {Location.Name}");
        var instance = Singleton<GlobalConfiguration>.Instance;

        GameController.InitShellingController(instance, gameWorld, Location);
        GameController.InitHalloweenEvent(instance, gameWorld, Location);
        GameController.InitBTRController(instance, gameWorld, Location);

        if (FikaPlugin.Instance.Settings.EnableTransits)
        {
            GameController.InitializeTransitSystem(gameWorld, instance, Profile, _raidSettings, Location);
        }

        GameController.InitializeRunddans(instance, gameWorld, Location);
        GameController.InitializeKolotun(instance, gameWorld, Location);

        if (GameController.IsServer)
        {
            FikaSingleton<FikaServer>.Instance.RaidInitialized = true;
        }

        gameWorld.ClientBroadcastSyncController = new ClientBroadcastSyncController();

        var config = AppEnvironment.Config;
        if (config.FixedFrameRate > 0f)
        {
            FixedDeltaTime = 1f / config.FixedFrameRate;
        }

        if (FikaBackendUtils.IsReconnect)
        {
            await GetReconnectProfile(ProfileId);
        }

        try
        {
            var player = await CreateLocalPlayer()
                ?? throw new NullReferenceException("InitPlayer: Player was null!");
            _players.Add(player.ProfileId, player);
            _playerOwner = _func_1(player);

            // The stock SPT 5 LocalGame path registers the local player with
            // the native world immediately after PlayerFactory returns. Fika
            // creates the player through its managed path, so that native
            // registration can be skipped even though MainPlayer happens to
            // be populated by another startup callback. Register both sides
            // explicitly before creating the camera/owner input chain. This
            // binds GamePlayerOwner, MainPlayer and the native player list to
            // the same object used by Fika networking.
            RegisterNativePlayerWithWorld(gameWorld, player);

            // The native call is intentionally deferred.  FikaPlayer starts
            // with an empty-hands controller and the SPT 5 native helper can
            // run before the owner/input chain exists, silently leaving the
            // local player in EmptyHandsController forever.  RepairLocalHands
            // retries the same stock selection after the owner and raid are
            // fully started, then falls back to FikaPlayer.Proceed(Weapon).

            PlayerCameraController.Create(_playerOwner.Player);
            CameraManager.Instance.SetOcclusionCullingEnabled(new Il2CppSystem.Nullable<bool>(Location.OcculsionCullingEnabled));
            CameraManager.Instance.IsActive = false;

            FikaSingleton<IFikaNetworkManager>.Instance.CreateFikaChat();
        }
        catch (Exception ex)
        {
            _logger.LogError($"InitPlayer: {ex.Message}");
            throw;
        }

        await GameController.WaitForHostToStart();

        var location = _raidSettings.selectedLocation;
        await GameController.InitializeLoot(location);
        await SpawnLoot(location).AsManaged();

        // NOTE: do NOT call GetQuestLootReady + SpawnLoot again here. SpawnLoot(location) above
        // already routes the descriptor through GetQuestLootReady, which diverts the quest-flagged
        // entries into GameWorld._questLootItems and materialises the REST - so a second pass spawned
        // a duplicate copy of every ordinary ground loot item (the reported ghost entities).
        // The deferred quest entries are materialised by GameWorld_SpawnLoot_QuestLoot_Patch
        // (postfix on GameWorld.SpawnLoot -> ManageQuestLoot), which logs [QuestLoot].

        GameController.CoopHandler.ShouldSync = true;

        if (FikaBackendUtils.IsReconnect)
        {
            await Reconnect();
            foreach (var item in _playerOwner.Player.ActiveHealthController.BodyState)
            {
                if (item.Value.Health.AtMinimum)
                {
                    item.Value.IsDestroyed = true;
                }
            }
        }

        // Calling the inherited IL2CPP virtual wrapper here can re-enter the
        // native BaseLocalGame.Run path. On SPT 5 that path invokes
        // LocalBotsSpawnInitialization and bypasses Fika's host/client bot
        // controller, which leaves the native spawn context incomplete and
        // fails with a NullReferenceException before the lobby can start.
        // Dispatch directly to the managed Fika controller instead.
        await GameController.StartBotSystemsAndCountdown(botsSettings, gameWorld);

        EnsurePlayerOwnerReady();
        RepairLocalHands(force: true);

        if (GameController.IsServer)
        {
            Singleton<IBotGame>.Instance.BotsController.CoversData.Patrols.RestoreLoot(Location.Loot, LocationScene.GetAllObjects<LootableContainer>(false));
            ServerAirdropManager airdropEventClass = new()
            {
                AirdropParameters = Location.airdropParameters
            };
            airdropEventClass.Init(true);
            Singleton<GameWorld>.Instance?.TryCast<ClientGameWorld>()?.ClientSynchronizableObjectLogicProcessor.ServerAirdropManager = airdropEventClass;
            GameWorld.SynchronizableObjectLogicProcessor.AirdropDataSender = FikaSingleton<FikaServer>.Instance;
        }
        await PrepareSession().AsManaged();
        EnsurePlayerOwnerReady();
        _localPlayer.ApplyReconnectRevival();
        if (_reconnectHands != null && !_localPlayer.Downed && _localPlayer.HealthController.IsAlive)
        {
            try
            {
                await _reconnectHands.Restore(_localPlayer);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Reconnect hands restore failed; using normal startup selection: {ex.Message}");
                _hasReconnectHands = false;
            }
        }
        _reconnectHands = null;
        RepairLocalHands(force: true);
        FikaEventDispatcher.DispatchEvent(new GameWorldStartedEvent(GameWorld));
    }

    /// <summary>
    /// Re-enters the stock first-item selection after deployment.  SPT 5's
    /// IL2CPP LocalGame normally performs this after PlayerOwner.Run(); the
    /// managed Fika startup path can reach the raid without that callback.
    /// </summary>
    public void RepairLocalHands(bool force = false)
    {
        // A reconnect snapshot may intentionally contain empty hands. Never
        // let the startup weapon fallback replace that authoritative state.
        if (_hasReconnectHands) return;
        if (_localPlayer == null || _playerOwner == null || Status is not (GameStatus.Running or GameStatus.Started))
            return;
        if (_localPlayer.Downed || _localPlayer.AwaitingReconnectRevival || !_localPlayer.HealthController.IsAlive)
            return;

        if (!force && Time.time < _nextHandsRepairTime)
            return;
        _nextHandsRepairTime = Time.time + 0.5f;

        if (_handsRepairInFlight || _handsRepairAttempts >= 8)
            return;

        if (_localPlayer.HandsController?.TryCast<Player.EmptyHandsController>() == null)
            return;

        _handsRepairAttempts++;
        _logger.LogInfo($"RepairLocalHands attempt {_handsRepairAttempts}: owner={_playerOwner.State}, hands={_localPlayer.HandsController?.GetType().FullName}");
        RestoreFirstAvailableItem("RepairLocalHands");
    }

    /// <summary>
    /// Puts the first usable item back into the local player's hands.  Shared
    /// by the deployment repair (empty hands) and by the hands lock watchdog
    /// (a controller that can no longer fire or move items).  The deployment
    /// path keeps its historic log lines so the existing diagnostics still
    /// match.
    /// </summary>
    private void RestoreFirstAvailableItem(string tag, bool includeMelee = false)
    {
        if (_handsRepairInFlight)
        {
            return;
        }

        _handsRepairInFlight = true;
        try
        {
            var weapon = FindFirstEquippedWeapon();
            if (weapon != null)
            {
                _logger.LogInfo($"{tag} selected weapon {weapon.Id} ({weapon.GetType().FullName})");
                // Calling FikaPlayer.Proceed directly bypasses the native
                // TrySetLastEquippedWeapon path that can be a no-op while
                // the managed owner is being brought up.
                _localPlayer.Proceed(weapon, null, scheduled: false);
                return;
            }

            // A knife controller still satisfies the inventory (its
            // CanExecute returns true), so it is a better fallback than
            // leaving the player without any hands at all.
            if (includeMelee)
            {
                var melee = _localPlayer.Equipment?.GetSlot(EquipmentSlot.Scabbard)?.ContainedItem
                    ?.GetItemComponent<KnifeComponent>();
                if (melee != null)
                {
                    _logger.LogWarning($"{tag} found no firearm, falling back to the melee weapon {melee.Item.Id}");
                    _localPlayer.Proceed(melee, (Callback<IKnifeController>)null, scheduled: false);
                    return;
                }
            }

            _logger.LogWarning($"{tag} found no weapon in FirstPrimaryWeapon/SecondPrimaryWeapon/Holster");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"{tag} failed: {ex.Message}");
        }
        finally
        {
            _handsRepairInFlight = false;
        }
    }

    /// <summary>
    /// Ground loot pickups run inside EFT.PickUpState plus the animator's
    /// interaction queue. When that state is left behind, the player keeps its
    /// restrictions for the rest of the raid (no view rotation, no movement, no
    /// crouch, no interaction prompts) even though the inventory and firing keep
    /// working. Releasing it only from failed operations is not enough, because
    /// the state can outlive an operation that reported success, so watch the
    /// local player and release a pickup/loot state that persists far longer
    /// than the native animation ever does.
    /// </summary>
    public void RepairLocalInteractionState()
    {
        if (_localPlayer == null || _playerOwner == null)
        {
            return;
        }
        if (Status is not (GameStatus.Running or GameStatus.Started))
        {
            return;
        }
        if (_localPlayer.Downed || _localPlayer.AwaitingReconnectRevival || !_localPlayer.HealthController.IsAlive)
        {
            _stuckInteractionSince = 0f;
            _stuckHandsSince = 0f;
            return;
        }

        // The hands lock keeps its own timers; the interaction state below
        // only ticks once per second and must not gate it.
        try
        {
            RepairHandsLock();
        }
        catch (Exception exception)
        {
            // A disposed player or a throwing native accessor must not take
            // the per-frame plugin hook down with it. Reuse the repair
            // cooldown so a persistent failure does not spam the log.
            if (Time.time >= _nextHandsLockRepairTime)
            {
                _nextHandsLockRepairTime = Time.time + 8f;
                _logger.LogWarning($"[HandsRepair] watchdog failed: {exception.Message}");
            }
        }

        if (Time.time < _nextInteractionRepairTime)
        {
            return;
        }
        _nextInteractionRepairTime = Time.time + 1f;

        var movementContext = _localPlayer.MovementContext;
        if (movementContext?.CurrentState == null)
        {
            return;
        }

        // Only EPlayerState.Pickup: a ground item take animates for well under two
        // seconds, while container searching (Loot), planting, door breaching and
        // normal interactions legitimately last for seconds and must not be
        // cancelled here. The reported freeze only ever comes from ground loot.
        // Ground loot (Pickup) locks up after a take animation of well under two
        // seconds; placing a quest item (Plant) legitimately takes longer, so it gets
        // a much larger budget before the state counts as stuck.
        var timeout = movementContext.CurrentState.Name switch
        {
            EPlayerState.Pickup => 5f,
            EPlayerState.Plant => 20f,
            _ => 0f
        };
        if (timeout <= 0f)
        {
            _stuckInteractionSince = 0f;
            return;
        }

        if (_stuckInteractionSince == 0f)
        {
            _stuckInteractionSince = Time.time;
            return;
        }
        if (Time.time - _stuckInteractionSince < timeout)
        {
            return;
        }

        var stuckState = movementContext.CurrentState.Name;
        _stuckInteractionSince = 0f;
        _logger.LogWarning($"Local player stayed in {stuckState} for more than {timeout} seconds; releasing the native interaction state");
        NativeInteractionRelease.Release(_localPlayer, "stuck-pickup-state");
    }

    /// <summary>
    /// The native inventory gate refuses every item action while it is closed:
    /// the drag-and-drop views (<c>GridView</c>/<c>SlotView.CanDrag</c>) and
    /// <c>Player.SetItemInHands</c>/<c>SetQuickSlotItem</c> all go through
    /// <c>Item.CheckAction</c> -> <c>PlayerInventoryController.CheckItemAction</c>.
    /// <c>ItemController.Locked</c> is only ever written by
    /// <c>MovementContext.StateLocksInventory</c> (a movement state's Enter sets
    /// it, its Exit clears it) and an unfinished item event keeps single items
    /// busy, so a state whose Exit never ran leaves the inventory dead for the
    /// rest of the raid while movement keeps working.
    /// <para>
    /// The watchdog is deliberately narrow: it only acts when the player is
    /// trying to use the inventory (the inventory screen is open, or an
    /// operation was just refused by the gate) AND the gate is still closed
    /// while the player is idle for several seconds. A door, approach or
    /// stationary interaction owns the lock legitimately and never leaves the
    /// player idle, so it is never touched.
    /// </para>
    /// </summary>
    public void RepairLocalInventoryState()
    {
        // The gate has to stay closed this long before it counts as stuck: every
        // legitimate holder (a hands switch, a door approach, a magazine load)
        // releases it well inside this budget.
        const float GateTimeoutSeconds = 6f;
        const float RefusalWindowSeconds = 60f;

        if (_localPlayer == null || _playerOwner == null)
        {
            return;
        }
        if (Status is not (GameStatus.Running or GameStatus.Started)
            || _localPlayer.Downed || _localPlayer.AwaitingReconnectRevival || !_localPlayer.HealthController.IsAlive)
        {
            ResetInventoryGateWatch();
            return;
        }
        if (Time.time < _nextInventoryGateCheckTime)
        {
            return;
        }
        _nextInventoryGateCheckTime = Time.time + 1f;

        if (_localPlayer.InventoryController?.TryCast<BaseInventoryController>() is not { } inventoryController)
        {
            return;
        }

        // A consumable or usable item in hands legitimately holds the same gate
        // for several seconds (meds, food, a range finder); never touch that.
        if (HasConsumableInHands())
        {
            ResetInventoryGateWatch();
            return;
        }

        // A server callback registration without a handler is unambiguous: a live
        // operation always owns its registration, and both the table and the
        // registration are only touched on this thread. FikaPlayer.WaitingForCallback
        // counts those registrations, and the client hands controllers answer
        // CanPressTrigger/CanStartReload with false while it is true, so one leaked
        // registration keeps the local player from shooting for the rest of the raid.
        var staleCallbacks = inventoryController.CountStaleServerCallbacks();
        if (staleCallbacks > 0)
        {
            inventoryController.RepairBlockedInventory(
                $"waiting for {staleCallbacks} server callback(s) that no handler owns", escalate: false);
            ResetInventoryGateWatch();
            return;
        }

        var attempted = HasInventoryScreenOpen() || inventoryController.HadGateRefusalWithin(RefusalWindowSeconds);
        var blocked = inventoryController.IsInventoryGateLocked || inventoryController.IsChangingWeaponState;
        var idle = _localPlayer.MovementContext?.CurrentState?.Name is EPlayerState.Idle;
        if (!attempted || !blocked || !idle)
        {
            ResetInventoryGateWatch();
            return;
        }

        if (_inventoryGateBlockedSince == 0f)
        {
            _inventoryGateBlockedSince = Time.time;
            return;
        }
        if (Time.time - _inventoryGateBlockedSince < GateTimeoutSeconds)
        {
            return;
        }

        // Keep the timestamp so a repair that did not help runs again after the
        // same timeout instead of once per frame.
        _inventoryGateBlockedSince = Time.time;
        _inventoryGateRepairRounds++;
        inventoryController.RepairBlockedInventory(
            $"gate closed for {GateTimeoutSeconds}s while idle (locked={inventoryController.IsInventoryGateLocked}, " +
            $"changingWeapon={inventoryController.IsChangingWeaponState}, " +
            $"pendingHandlers={inventoryController.PendingOperationCount})",
            escalate: _inventoryGateRepairRounds > 1);
    }

    private void ResetInventoryGateWatch()
    {
        _inventoryGateBlockedSince = 0f;
        _inventoryGateRepairRounds = 0;
    }

    /// <summary>
    /// Whether the player is using a consumable or another usable item. Those
    /// hands controllers hold the same process/inventory gate for seconds while
    /// they run, so the gate watchdog must not treat them as stuck.
    /// </summary>
    private bool HasConsumableInHands()
    {
        try
        {
            var hands = _localPlayer.HandsController;
            if (hands == null)
            {
                return false;
            }
            return hands.TryCast<Player.MedsController>() != null
                || hands.TryCast<Player.UsableItemController>() != null;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[InventoryRepair] reading the hands controller failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>Whether the player has the inventory screen open right now.</summary>
    private static bool HasInventoryScreenOpen()
    {
        try
        {
            return EftScreenManager.Instance?.CheckCurrentScreen(EEftScreenType.Inventory) == true;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[InventoryRepair] reading the current screen failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// "Operating too fast" hands lock.  Every hands switch is a
    /// <c>EFT.Player.Process</c> gated by <c>Player.ProcessStatus</c>: while that
    /// status is not <c>None</c> the inventory controller reports itself locked
    /// and the next <c>Proceed</c> is skipped by the native scheduler - including
    /// the switch that would return from a quick knife to the previous weapon.
    /// A double-tapped quick knife can therefore leave the transitional
    /// controller in hands for good: it rejects every inventory operation
    /// (<c>QuickKnifeKickController.CanExecute</c> is false) and, with no firearm,
    /// nothing can fire.  Movement does not depend on the hands, which is exactly
    /// what the player sees.  Watch it and release it, but only long after any
    /// legitimate switch could still be running.
    /// </summary>
    private void RepairHandsLock()
    {
        // A native switch (drop, spawn and the host confirmation) finishes well
        // inside a second, so these budgets never interrupt a legitimate one.
        const float HandsLockTimeout = 4f;
        const float TransitionalHandsTimeout = 6f;
        const float ProceedCallbackTimeout = 5f;
        const float RepairCooldown = 8f;

        var hands = _localPlayer.HandsController;
        var handsMissing = hands == null || hands.Pointer == IntPtr.Zero;
        var handsDestroyed = !handsMissing && hands.Destroyed;
        var handsTransitional = !handsMissing && !handsDestroyed && IsTransitionalHandsController(hands);
        var processStatus = _localPlayer.ProcessStatus;
        var gateHeld = processStatus != Player.EProcessStatus.None;
        // A registered proceed callback is what WaitingForCallback reports,
        // and FikaClientFirearmController refuses to fire while it is set.
        var waitingForCallback = _localPlayer.PendingProceedCallbacks > 0;

        float timeout;
        if (gateHeld || handsMissing || handsDestroyed)
        {
            timeout = HandsLockTimeout;
        }
        else if (handsTransitional)
        {
            // The quick knife, quick grenade and quick use controllers own the
            // hands only until their animation hands the previous item back.
            timeout = TransitionalHandsTimeout;
        }
        else if (waitingForCallback)
        {
            // Healthy hands, but a hands switch the host never answered keeps
            // CanPressTrigger/CanStartReload false on the firearm controller.
            timeout = ProceedCallbackTimeout;
        }
        else
        {
            _stuckHandsSince = 0f;
            return;
        }

        if (_stuckHandsSince == 0f)
        {
            _stuckHandsSince = Time.time;
            return;
        }

        var stuckFor = Time.time - _stuckHandsSince;
        if (stuckFor < timeout || Time.time < _nextHandsLockRepairTime)
        {
            return;
        }

        _stuckHandsSince = 0f;
        _nextHandsLockRepairTime = Time.time + RepairCooldown;
        _handsLockRepairs++;

        var inventoryController = _localPlayer.InventoryController;
        var playerInventoryController = inventoryController?.TryCast<Player.PlayerInventoryController>();
        var handsName = handsMissing ? "null" : hands.GetType().Name;
        _logger.LogWarning($"[HandsRepair] attempt {_handsLockRepairs}: the local hands stayed locked for " +
            $"{stuckFor:F1}s (process={processStatus}, hands={handsName}" +
            $"{(handsDestroyed ? ", destroyed" : string.Empty)}{(handsTransitional ? ", transitional" : string.Empty)}, " +
            $"inventoryLocked={playerInventoryController?.Locked}, " +
            $"inventoryBlocked={inventoryController?.IsInventoryBlocked()}, " +
            $"pendingProceedCallbacks={_localPlayer.PendingProceedCallbacks}); releasing");

        // 1. The player level switch gate. None is what every completed native
        //    switch leaves behind; while it is set, the native Process.Proceed
        //    scheduler skips every later hands switch.
        TryHandsRepairStep("process status", () =>
        {
            if (_localPlayer.ProcessStatus != Player.EProcessStatus.None)
            {
                _localPlayer.ProcessStatus = Player.EProcessStatus.None;
            }
        });

        // 2. The same gate is what refuses item actions:
        //    PlayerInventoryController.CheckItemAction returns the "inventory
        //    blocked" error while IsInventoryBlocked() - which forwards to
        //    Locked - is true.  Writing the property is the native unlock.
        TryHandsRepairStep("inventory lock", () =>
        {
            if (playerInventoryController != null && playerInventoryController.Locked)
            {
                playerInventoryController.Locked = false;
            }
        });

        // 3. An answer from the host that never arrived keeps its callback
        //    registered and WaitingForCallback true, which
        //    FikaClientFirearmController turns into CanPressTrigger/
        //    CanStartReload == false.
        TryHandsRepairStep("callback expiry", () =>
        {
            var expired = _localPlayer.ExpireStaleProceedCallbacks(ProceedCallbackTimeout);
            if (expired > 0)
            {
                _logger.LogWarning($"[HandsRepair] dropped {expired} proceed callback(s) the host never answered");
            }
        });

        // 4. Hands that are gone, destroyed or still holding a single use
        //    controller can neither fire nor move items, so put a usable item
        //    back. Reuse the native interaction release first: it is what clears
        //    a held animator interaction queue, and it never touches items.
        if (handsMissing || handsDestroyed || handsTransitional)
        {
            TryHandsRepairStep("interaction release", () => NativeInteractionRelease.Release(_localPlayer, "hands-lock"));
            RestoreFirstAvailableItem("[HandsRepair]", includeMelee: true);
        }
    }

    /// <summary>
    /// Controllers that exist only to hand the previous item back: the quick
    /// knife kick, the quick grenade and the quick use item.  Holding one for
    /// longer than its animation means the switch back was lost.
    /// </summary>
    private static bool IsTransitionalHandsController(Player.AbstractHandsController hands)
    {
        // The native class is not visible to a CLR type test; TryCast sees it.
        return hands.TryCast<Player.QuickKnifeKickController>() != null
            || hands.TryCast<Player.QuickGrenadeThrowHandsController>() != null
            || hands.TryCast<Player.QuickUseItemController>() != null;
    }

    private void TryHandsRepairStep(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _logger.LogWarning($"[HandsRepair] {step} failed: {exception.Message}");
        }
    }

    private Weapon FindFirstEquippedWeapon()
    {
        foreach (var slotType in FikaGlobals.WeaponSlots)
        {
            var item = _localPlayer.Equipment.GetSlot(slotType)?.ContainedItem;
            var weapon = item?.TryCast<Weapon>();
            if (weapon != null && _localPlayer.IsItemCanBeEquipped(weapon))
                return weapon;
        }
        return null;
    }

    internal bool AcceptsReconnectPacket(ReconnectPacket packet)
    {
        // GetReconnectProfile clears Profile while waiting for the first
        // response, so ProfileId is not a reliable filter in that phase.
        if (_pendingReconnectProfileId != null)
            return (packet.Type is ReconnectPacket.EReconnectDataType.OwnCharacter or
                ReconnectPacket.EReconnectDataType.Failed) &&
                packet.ProfileId == _pendingReconnectProfileId;
        return _reconnectWorldRequested &&
            packet.Type != ReconnectPacket.EReconnectDataType.OwnCharacter &&
            packet.ProfileId == ProfileId;
    }

    internal void ReceiveReconnectProfile(ReconnectPacket packet)
    {
        // Scope the snapshot to this game and this request. A late/duplicate
        // response must not replace the profile of an already-created player.
        if (_pendingReconnectProfileId == null) return;
        if (packet.Profile == null || packet.Profile.Id != _pendingReconnectProfileId)
            throw new InvalidOperationException("Reconnect response belongs to a different profile");
        // Retries can produce two responses for the same request. A response whose inventory
        // position is older than the one already accepted must not overwrite the snapshot.
        if (packet.FirstOperationId < _lastReconnectFirstOperationId)
        {
            FikaGlobals.LogWarning($"Reconnect: ignoring a stale own-character response " +
                $"(operation {packet.FirstOperationId} < accepted {_lastReconnectFirstOperationId})");
            return;
        }

        _lastReconnectFirstOperationId = packet.FirstOperationId;
        var inventoryState = new ReconnectInventoryState(packet.Profile.Id, packet.ControllerId, packet.FirstOperationId);
        packet.Profile.Health = packet.ProfileHealthClass;
        _reconnectInventoryState = inventoryState;
        _reconnectHands = new ReconnectHandsState(packet);
        _hasReconnectHands = packet.ControllerType != EHandsControllerType.None;
        _reconnectRevivalClock.Restore(packet.RevivalState);
        FikaBackendUtils.ReconnectPosition = packet.PlayerPosition;
        FikaBackendUtils.ReconnectRotation = packet.PlayerRotation;
        _hasReconnectPosition = true;
        _pendingReconnectProfileId = null;
        // Publish last: GetReconnectProfile waits for this before creating the player.
        Profile = packet.Profile;
    }

    internal void ReceiveReconnectRevival(ReconnectPacket packet)
    {
        if (!FikaBackendUtils.IsReconnect || packet.ProfileId != ProfileId || _localPlayer == null ||
            !_localPlayer.AwaitingReconnectRevival) return;
        _localPlayer.StageReconnectRevival(packet.RevivalState);
    }

    private async Task GetReconnectProfile(string profileId)
    {
        _pendingReconnectProfileId = profileId;
        _reconnectInventoryState = null;
        _reconnectHands = null;
        _hasReconnectHands = false;
        _hasReconnectPosition = false;
        _reconnectWorldRequested = false;
        // Stale-response detection must not survive into the next reconnect request.
        _lastReconnectFirstOperationId = ushort.MaxValue;
        Profile = null;

        ReconnectPacket reconnectPacket = new()
        {
            IsRequest = true,
            InitialRequest = true,
            ProfileId = profileId
        };
        var client = FikaSingleton<FikaClient>.Instance;
        client.ReconnectError = null;
        client.SendData(ref reconnectPacket, DeliveryMethod.ReliableOrdered);

        // The host can still be registering the raid when this request lands (fresh raid,
        // transit, host restart), and one refused request must not end the session. Retry the
        // whole handshake a bounded number of times before giving up with the host's reason.
        const int reconnectAttempts = 3;
        const double attemptTimeoutSeconds = 45.0;
        for (var attempt = 1; attempt <= reconnectAttempts; attempt++)
        {
            var attemptStartedAt = Time.unscaledTimeAsDouble;

            do
            {
                await Task.Delay(250);
                if (client.ReconnectError != null) break;
                if (Time.unscaledTimeAsDouble - attemptStartedAt > attemptTimeoutSeconds) break;
            } while (Profile == null);

            if (Profile != null)
            {
                if (attempt > 1)
                {
                    FikaGlobals.LogInfo($"Reconnect profile received on attempt {attempt}/{reconnectAttempts}");
                }
                break;
            }

            var failure = client.ReconnectError
                ?? $"Host did not return the reconnect profile within {attemptTimeoutSeconds:0}s";
            if (attempt >= reconnectAttempts)
            {
                throw new InvalidOperationException(failure);
            }

            FikaGlobals.LogWarning($"Reconnect attempt {attempt}/{reconnectAttempts} failed ({failure}); retrying in 3 s");
            await Task.Delay(3000);
            client.ReconnectError = null;
            Profile = null;
            _pendingReconnectProfileId = profileId;
            reconnectPacket = new ReconnectPacket
            {
                IsRequest = true,
                InitialRequest = true,
                ProfileId = profileId
            };
            client.SendData(ref reconnectPacket, DeliveryMethod.ReliableOrdered);
        }

        await Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid, ObjectsFactory.AssemblyType.Local,
            Profile.GetAllPrefabPaths(true).AsManaged().ToNativeList(), JobYieldPriority.General,
            null, new Il2CppSystem.Threading.CancellationToken()).AsManaged();
    }

    private async Task Reconnect()
    {
        SetMatchmakerStatus(LocaleUtils.UI_RECONNECTING.Localized());

        ReconnectPacket reconnectPacket = new()
        {
            IsRequest = true,
            ProfileId = ProfileId
        };
        var client = FikaSingleton<FikaClient>.Instance;
        client.ReconnectDone = false;
        client.ReconnectError = null;
        _reconnectWorldRequested = true;
        var requestedAt = Time.unscaledTimeAsDouble;
        client.SendData(ref reconnectPacket, DeliveryMethod.ReliableOrdered);

        do
        {
            await Task.Delay(1000);
            if (client.ReconnectError != null) throw new InvalidOperationException(client.ReconnectError);
            if (Time.unscaledTimeAsDouble - requestedAt > 120) throw new TimeoutException("Host did not finish the reconnect snapshot");
        } while (!client.ReconnectDone);

        var worldSpawns = client.FikaClientWorld?.WorldSpawns;
        while (worldSpawns is { HasPending: true } || client.HasPendingMapLoads)
        {
            await Task.Delay(100);
            if (client.ReconnectError != null) throw new InvalidOperationException(client.ReconnectError);
            if (Time.unscaledTimeAsDouble - requestedAt > 240) throw new TimeoutException("World objects did not finish loading during reconnect");
        }
        if (worldSpawns?.Failure != null) throw new InvalidOperationException(worldSpawns.Failure);
        if (client.ReconnectError != null) throw new InvalidOperationException(client.ReconnectError);

        var packet = new ClearSnapshotterPacket
        {
            NetId = client.NetId
        };
        client.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
    }



    /// <summary>
    /// Creates the local player
    /// </summary>
    /// <returns>A <see cref="Player"/></returns>
    private async Task<LocalPlayer> CreateLocalPlayer()
    {
        Status = GameStatus.Running;

        var num = FikaSingleton<IFikaNetworkManager>.Instance.NetId;

        var eupdateMode = Player.EUpdateMode.Auto;
        if (AppEnvironment.Config.UseHandsFastAnimator)
        {
            eupdateMode = Player.EUpdateMode.Manual;
        }

        if (GameController.IsServer)
        {
            GameController.CreateSpawnSystem(Profile);
        }
        else
        {
            await GameController.ReceiveSpawnPoint(Profile);
            if (string.IsNullOrEmpty(GameController.InfiltrationPoint))
            {
                _logger.LogError("InfiltrationPoint was null after retrieving it from the server!");
                GameController.CreateSpawnSystem(Profile);
            }

            var clientGameController = (ClientGameController)GameController;
            await clientGameController.InitInteractables();
            await clientGameController.InitExfils();
        }

        GameController.ExfilManager = gameObject.AddComponent<FikaExfilManager>();

        // Signal flare exits only react to a flare fired on the machine that owns
        // it; this component relays the activation to the host and back so a
        // client's flare actually unlocks the exit for its squad.
        if (gameObject.GetComponent<FikaFlareExfilManager>() == null)
        {
            gameObject.AddComponent<FikaFlareExfilManager>();
        }

        // SPT consumes the entry requirement while creating the initial raid
        // request.  A reconnect and a transit raid already carry the
        // authoritative profile/transition state; consuming the requirement
        // here again removes another key charge (and, for a consumed key,
        // makes the next map fail its access check).
        var isTransitRaid = FikaBackendUtils.IsTransit || TransitController.IsTransit(Profile.Id, out int _);
        if (!FikaBackendUtils.IsReconnect && !isTransitRaid &&
            Profile.Side != EPlayerSide.Savage && Profile.Info.Level >= Location.MinPlayerLvlAccessKeys)
        {
            var access = Location.GetAccessRequirements(EGameMode.Pve,
                FikaBackendUtils.IsTransit ? JsonType.LocationSettings.Location.EAccessUsageType.Transit : JsonType.LocationSettings.Location.EAccessUsageType.Menu);
            var items = Profile.Inventory.GetPlayerItems(EPlayerItems.Equipment);
            if (access != null && access.Count > 0 && items != null)
            {
                // SPT 5 supports required item counts and remaining key charges.
                // Let the game's utility consume the selected requirement.
                if (!LocationAccessRequirementsUtility.TryConsumeAccessRequirement(items, access, null))
                    _logger.LogError($"Unable to consume entry requirement for {Location.Id}");
            }
        }

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

        IStatisticsManager statisticsManager = new ClientStatisticsManager();

        var spawnPos = GameController.GetSpawnPosition();
        var spawnRot = GameController.GetSpawnRotation();

        LocalPlayer myPlayer;
        try
        {
            if (Profile.Side != EPlayerSide.Savage && !FikaBackendUtils.IsReconnect)
            {
                GenerateNewDogTagId();
            }
            else if (Profile.Side == EPlayerSide.Savage && FikaBackendUtils.IsHeadless)
            {
                Profile.Info.Nickname = Profile.Info.Nickname.Insert(0, "headless_");
            }

            myPlayer = await CreatePlayerManaged(GameWorld, num, spawnPos, spawnRot, "Player", "", EPointOfView.FirstPerson,
                    Profile, false, UpdateQueue, eupdateMode, Player.EUpdateMode.Auto,
                    AppEnvironment.Config.CharacterController.ClientPlayerMode,
                    FikaGlobals.GetLocalPlayerSensitivity, FikaGlobals.GetLocalPlayerAimingSensitivity, statisticsManager,
                    _backEnd, (_raidSettings?.mode) ?? ELocalMode.TRAINING);
        }
        catch (Exception ex)
        {
            _logger.LogError($"CreateLocalPlayer: {ex.Message}");
            throw;
        }

        myPlayer.OnEpInteraction += NativeDelegates.Bind<Il2CppSystem.Action<ExfiltrationPoint, bool>>(this, nameof(OnEpInteraction));

        _localPlayer = myPlayer.TryCast<FikaPlayer>()
            ?? throw new InvalidOperationException("Local player factory did not return a FikaPlayer");
        GameController.SetLocalPlayer(_localPlayer);

        _logger.LogInfo("Local player created");
        return myPlayer;
    }

    /// <summary>
    /// Tells the host about our character. A player scav walking into a raid that
    /// already runs can send this before its network connection is up, in which
    /// case the host never learns about the character, has no player for it and
    /// rejects every proceed request. Re-announcing is therefore allowed.
    /// </summary>
    internal static void AnnounceCharacter(FikaPlayer fikaPlayer)
    {
        if (fikaPlayer == null || FikaSingleton<FikaClient>.Instance == null) return;

        var packet = SendCharacterPacket.FromValue(new PlayerInfoPacket()
        {
            Profile = fikaPlayer.Profile,
            ControllerId = fikaPlayer.InventoryController.CurrentId,
            FirstOperationId = fikaPlayer.InventoryController.NextOperationId
        }, fikaPlayer.HealthController.IsAlive, false, fikaPlayer.Transform.position, fikaPlayer.NetId);

        if (fikaPlayer.ActiveHealthController != null)
        {
            packet.PlayerInfoPacket.HealthByteArray = fikaPlayer.ActiveHealthController.SerializeState();
        }

        if (fikaPlayer.HandsController != null)
        {
            packet.PlayerInfoPacket.ControllerType = HandsControllerTypeConvert.FromController(fikaPlayer.HandsController);
            packet.PlayerInfoPacket.ItemId = fikaPlayer.HandsController.Item?.Id;
            packet.PlayerInfoPacket.IsStationary = fikaPlayer.MovementContext.IsStationaryWeaponInHands;
        }

        FikaGlobals.LogInfo($"Announcing our character to the host: netId={fikaPlayer.NetId}, " +
            $"profile={fikaPlayer.Profile.ProfileId}, scavLateJoin={FikaBackendUtils.IsScavLateJoin}");
        FikaSingleton<FikaClient>.Instance.SendGenericPacket(EGenericSubPacketType.SendCharacter, packet, true);
    }

    /// <summary>
    /// Re-announces the local character when the host reports that it has no
    /// player bound for us yet.
    /// </summary>
    internal static void ReannounceLocalCharacter()
    {
        // Proceed responses arrive on the network thread, but the packet writer and
        // the packet processor are shared with the main thread, so the announcement
        // has to be built on the Unity thread.
        MainThreadDispatcher.Post(() =>
        {
            if (FikaSingleton<IFikaGame>.Instance is not CoopGame coopGame || coopGame._localPlayer == null) return;
            if (coopGame.GameController.IsServer || FikaBackendUtils.IsReconnect) return;
            AnnounceCharacter(coopGame._localPlayer);
        });
    }

    /// <summary>
    /// Temporary workaround since SPT does not generate a unique ID >3.11 for the dogtag
    /// </summary>
    private void GenerateNewDogTagId()
    {
        var dogTag = Profile.Inventory.Equipment.GetSlot(EquipmentSlot.Dogtag).ContainedItem;
        if (dogTag != null)
        {
            dogTag._Id_k__BackingField = MongoID.Generate(true);
#if DEBUG
            _logger.LogWarning("Generated new ID for DogTag");
#endif
        }
        else
        {
            _logger.LogError("Could not find DogTag when generating new ID!");
        }
    }

    /// <summary>
    /// Sets the status of the game on the backend
    /// </summary>
    /// <param name="myPlayer"></param>
    /// <param name="status"></param>
    /// <returns></returns>
    private async Task SetStatus(LocalPlayer myPlayer, LobbyEntry.ELobbyStatus status)
    {
        SetStatusModel statusBody = new(myPlayer.ProfileId, status);
        await FikaRequestHandler.UpdateSetStatus(statusBody);
        _logger.LogInfo("Setting game status to: " + status.ToString());
    }

    /// <summary>
    /// Bot System Starter -> Countdown
    /// </summary>
    /// <param name="controllerSettings"></param>
    /// <param name="spawnSystem"></param>
    /// <returns></returns>
    public override Il2CppSystem.Threading.Tasks.Task LocalBotsControllerInitialization(BotControllerSettings controllerSettings, ISpawnSystem spawnSystem)
    {
        return NativeTask.FromManaged(MainThreadDispatcher.RunAsync(() => GameController.StartBotSystemsAndCountdown(controllerSettings, GameWorld)));
    }

    public override Il2CppSystem.Collections.IEnumerator SessionRun(Il2CppSystem.Action runCallback) => SessionRunManaged(runCallback).WrapToIl2Cpp();

    private IEnumerator SessionRunManaged(Il2CppSystem.Action runCallback)
    {
        yield return GameController.FinishRaidSetup();
        yield return global::Fika.Core.Main.Utils.NativeBaseCalls.Call69(this, runCallback);
    }

    public override void Spawn()
    {
        if (LocalPlayer.ActiveHealthController?.TryCast<ClientHealthController>() is { } coopClientHealthController)
        {
            coopClientHealthController.Start();
        }
        _onLocalPlayerDied ??= (Il2CppSystem.Action<EDamageType>)HealthController_DiedEvent;
        _playerOwner.Player.HealthController.remove_DiedEvent(_onLocalPlayerDied);
        _playerOwner.Player.HealthController.add_DiedEvent(_onLocalPlayerDied);
        _playerOwner.Init();
#if DEBUG
        FikaGlobals.LogWarning("Forcing god mode on DEBUG build, use 'god f' console command to disable");
        _playerOwner.Player.ActiveHealthController.SetDamageCoeff(0f);
#endif
    }

    /// <summary>
    /// Sets up <see cref="OfflineHealthController"/> events and all <see cref="ExfiltrationPoint"/>s
    /// </summary>
    public override void SessionRun()
    {
        GameController.SetupEventsAndExfils(_playerOwner.Player);
        SessionStartTime = DateTimeExtensions.Now;
    }

    public override void FixedUpdate()
    {
        // Do nothing
    }

    /// <summary>
    /// Updates a <see cref="ExfiltrationPoint"/> from the server
    /// </summary>
    /// <param name="point"></param>
    /// <param name="enable"></param>
    public void UpdateExfilPointFromServer(ExfiltrationPoint point, bool enable)
    {
        if (GameController.ExfilManager != null)
        {
            GameController.ExfilManager.UpdateExfilPointFromServer(point, enable);
            return;
        }

        _logger.LogError("CoopGame::UpdateExfilPointFromServer: ExfilManager was null!");
    }

    public override void Dispose()
    {
        ClientHearingTable.Instance = null;
        foreach (var player in _players.Values)
        {
            try
            {
                if (player != null)
                {
                    player.Dispose();
                    AssetPoolObject.ReturnToPool(player.gameObject, true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
            }
        }
        _players.Clear();
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call70(this);
    }

    /// <summary>
    /// When the local player successfully extracts, enable freecam, notify other players about the extract
    /// </summary>
    /// <param name="player">The local player to start the Coroutine on</param>
    /// <param name="exfiltrationPoint">The exfiltration point that was used to extract</param>
    /// <param name="transitPoint">The transit point that was used to transit</param>
    /// <returns></returns>
    public void Extract(FikaPlayer player, ExfiltrationPoint exfiltrationPoint, TransitPoint transitPoint = null)
    {
        GameController.Extract(player, exfiltrationPoint, transitPoint);
        MainPlayerExtracted?.Invoke(player);
    }

    /// <summary>
    /// Triggers when the main player dies
    /// </summary>
    /// <param name="damageType"></param>
    private void HealthController_DiedEvent(EDamageType damageType)
    {
        MainThreadDispatcher.RunAsync(HandleLocalPlayerDeath).Forget();
    }

    /// <summary>
    /// Ensures the native player owner has entered its running state after the
    /// Fika deployment/countdown path.  SPT's normal LocalGame sequence calls
    /// this through the base session runner; Fika's managed startup can reach
    /// the raid with a valid player but without the owner registered in the
    /// input tree, which breaks ESC/TAB and leaves hands/UI unbound.
    /// </summary>
    internal void EnsurePlayerOwnerReady()
    {
        if (_playerOwner == null || _playerOwner.Player == null)
        {
            return;
        }

        // Use the exact owner created for this session. Its lifecycle state,
        // rather than presence in the scene, determines whether Init ran.
        if (_playerOwner.State == EFT.PlayerOwner.EState.None)
        {
            _playerOwner.Init();
        }

        // Fika creates the local owner from the managed startup path.  SPT's
        // native LocalGame normally registers the owner and creates both input
        // translators as part of PlayerOwner.Run(); the managed path can reach
        // the raid after that native step was skipped.  Restore those pieces
        // explicitly and idempotently.
        if (_playerOwner.PlayerInputTranslator == null || _playerOwner.HandsInputTranslator == null)
        {
            _playerOwner.CreateInputTranslators();
        }

        if (_playerOwner.State != EFT.PlayerOwner.EState.Started)
        {
            _playerOwner.Run();
        }

        if (_playerOwner.InputTree != null)
        {
            // InputTree.Add gives the owner priority over UIInputRoot. While
            // inventory/pause is open its Common gate returns BlockAll and
            // prevents the UI from receiving the key that should close it.
            // Keep gameplay below the UI, including owners already registered
            // by Run, and let the native tree dispatch each command once.
            if (_playerOwner.InputTree.Contains(_playerOwner))
                _playerOwner.InputTree.Remove(_playerOwner);
            _playerOwner.InputTree.AddWithLowPriority(_playerOwner);
        }

        if (_playerOwner.BattleUIScreenController == null)
        {
            _playerOwner.InitBattleUIScreen();
        }

        if (EftScreenManager.Instance?.CheckCurrentScreen(EEftScreenType.BattleUI) != true)
        {
            _playerOwner.ShowBattleUIScreen();
        }

        // Do this after ShowBattleUIScreen: screen transitions own the Common
        // input gate and can overwrite an earlier reset. This is a startup
        // handoff only; native input handles all subsequent menu transitions.
        if (EftScreenManager.Instance?.CheckCurrentScreen(EEftScreenType.BattleUI) == true &&
            (!FikaSingleton<FreeCamera.FreeCameraController>.Instantiated ||
             !FikaSingleton<FreeCamera.FreeCameraController>.Instance.IsScriptActive))
        {
            GamePlayerOwner.SetIgnoreInput(false, EIgnoreInputSource.Common);
        }
    }

    private static void RegisterNativePlayerWithWorld(GameWorld gameWorld, Player player)
    {
        if (gameWorld == null || player == null)
        {
            return;
        }

        try
        {
            // RegisterGameOwner does more than assign MainPlayer: the native
            // ClientLocalGameWorld override also binds the local owner/input
            // hooks. MainPlayer may already be populated by PlayerFactory, so
            // do not use it as a proxy for whether this registration ran.
            gameWorld.RegisterGameOwner(player);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"RegisterGameOwner failed: {ex.Message}");
        }

        try
        {
            var registered = gameWorld.RegisteredPlayers;
            var alreadyRegistered = false;
            if (registered != null)
            {
                for (var i = 0; i < registered.Count; i++)
                {
                    var existing = registered[i];
                    if (existing != null && existing.Pointer == player.Pointer)
                    {
                        alreadyRegistered = true;
                        break;
                    }
                }
            }
            if (!alreadyRegistered)
            {
                gameWorld.RegisterPlayer(player);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"RegisterPlayer failed: {ex.Message}");
        }
    }

    private async Task HandleLocalPlayerDeath()
    {
        var player = _playerOwner.Player;
        if (player.TryCast<FikaPlayer>()?.FikaQuests is ClientSharedQuestController sharedQuestController)
        {
            sharedQuestController.ToggleQuestSharing(false);
        }
        if (GameController.TimeManager != null)
        {
            Destroy(GameController.TimeManager);
        }
        if (GameUi.TimerPanel != null && GameUi.TimerPanel.enabled)
        {
            GameUi.TimerPanel.Close();
        }

        // Spawn is replaced above; only our death callback was registered.
        player.HealthController.remove_DiedEvent(_onLocalPlayerDied);

        PlayerOwner.Stop();
        ExitStatus = ExitStatus.Killed;
        ExitLocation = string.Empty;

        if (FikaPlugin.Instance.Settings.ForceSaveOnDeath)
        {
            if (player.TryCast<FikaPlayer>() is { } fikaPlayer)
                await _deathSave.SaveOnce(() => SavePlayer(fikaPlayer, ExitStatus, string.Empty, true));
        }
    }

    /// <summary>
    /// Stops the local <see cref="CoopGame"/>
    /// </summary>
    public override void Stop(string profileId, ExitStatus exitStatus, string exitName, float delay = 0f)
    {
        if (FikaFinalMissionDirector.DeferStop) return;
        if (Status == GameStatus.Stopping || Status == GameStatus.Stopped) return;
        var localPlayer = Singleton<GameWorld>.Instance?.MainPlayer?.TryCast<FikaPlayer>();
        // A client inventory resync can replace the controller's inventory
        // without updating the profile serialized by SPT's raid-end flow.
        // Keep the Scav transfer screen and the natural raid save on the same
        // inventory tree before taking the result-screen descriptor snapshot.
        if (localPlayer?.InventoryController?.TryCast<ClientInventoryController>() is { } clientInventoryController)
            clientInventoryController.SyncInventoryToProfile();

        // Capture directly so native injected-vtable calls and managed calls
        // both run this step before any raid-end handlers change the profile.
        Main.Patches.Overrides.RaidEndProfile.Capture(Profile);
        FikaEventDispatcher.DispatchEvent(new FikaGameEndedEvent(GameController.IsServer, exitStatus, exitName));

        if (exitStatus < ExitStatus.Transit)
        {
            FikaBackendUtils.IsTransit = false;
        }

        if (FikaBackendUtils.IsTransit)
        {
            var data = FikaBackendUtils.TransitData;
            data.transitionType = ELocationTransition.Common;
            data.transitionCount++;
            data.visitedLocations = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(data.visitedLocations.Append(Location.Id).ToArray());
            FikaBackendUtils.TransitData = data;
        }
        else
        {
            FikaBackendUtils.ResetTransitData();
        }

#if DEBUG
        _logger.LogDebug("Stop");
#endif

        ToggleDebug(false);

        GameController.DestroyDebugComponent();

        var myPlayer = localPlayer
            ?? throw new InvalidOperationException("Main player was unavailable while stopping the raid");
        myPlayer.PacketSender.DestroyThis();

        if (myPlayer.Side != EPlayerSide.Savage && myPlayer.Equipment.GetSlot(EquipmentSlot.Dogtag).ContainedItem != null)
        {
            var result = ItemManipulator.Remove(myPlayer.Equipment.GetSlot(EquipmentSlot.Dogtag)
                .ContainedItem, myPlayer.InventoryController, false);
            if (result.Error != null)
            {
                _logger.LogError("Error removing dog tag!");
            }
        }

        if (!myPlayer.ActiveHealthController.IsAlive && exitStatus == ExitStatus.Survived)
        {
            exitStatus = ExitStatus.Killed;
        }

        var sessionTime = NativeNullable<Il2CppSystem.TimeSpan>.Read(GameTimer, "<SessionTime>k__BackingField");
        if (!ExtractedPlayers.Contains(myPlayer.NetId) && sessionTime.HasValue && GameTimer.PastTime >= sessionTime.Value)
        {
            exitStatus = ExitStatus.MissingInAction;
        }

        if (GameController.IsServer)
        {
            if (GameController is HostGameController hostGameController)
                hostGameController.StopBotsSystem(false);
        }

        var errors = 0;

        if (GameController.CoopHandler != null)
        {
            // Create a copy to prevent errors when the dictionary is being modified (which happens when using spawn mods)
            foreach (var player in (FikaPlayer[])[.. GameController.CoopHandler.Players.Values])
            {
                if (player == null || player.IsYourPlayer)
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
                    _logger.LogError($"There was an error disposing of player [{player.Profile.GetCorrectedNickname()}]: {ex}");
                    errors++;
                }
            }
        }
        else
        {
            _logger.LogError("Could not find CoopHandler!");
        }

        if (errors > 0)
        {
            NotificationManager.DisplayWarningNotification($"Failed to dispose of {errors} players/bots. Please check your log files and restart the client after this raid.");
            _logger.LogError($"Failed to dispose of {errors} players/bots.");
        }

        if (!FikaBackendUtils.IsTransit)
        {
            Destroy(GameController.CoopHandler);
        }

        ExitManager stopManager = new()
        {
            baseLocalGame_0 = this,
            profileId = profileId,
            exitStatus = exitStatus,
            exitName = exitName,
            delay = delay
        };

        var gameUI = GameUI.Instance;

        GameController.ExfilManager.Stop();

        Status = GameStatus.Stopping;
        GameTimer.TryStop();
        if (gameUI.TimerPanel.isActiveAndEnabled)
        {
            gameUI.TimerPanel.Close();
        }
        if (EnvironmentManager.Instance != null)
        {
            EnvironmentManager.Instance.Stop();
        }
        MonoBehaviourSingleton<PreloaderUI>.Instance.StartBlackScreenShow(1f, 1f, (Il2CppSystem.Action)stopManager.ExitOverride);
        AppEnvironment.Config.UseSpiritPlayer = false;
    }

    /// <summary>
    /// Saves your own <see cref="FikaPlayer"/> to the server
    /// </summary>
    private async Task SavePlayer(FikaPlayer player, ExitStatus exitStatus, string exitName, bool fromDeath)
    {
        // SPT 5's native GameEnd resets this before saving and sets it after
        // LocalRaidEnded completes. Early saving bypasses that method, but
        // SessionResultShowOperation still waits for its completion flag.
        TarkovApplication.Exist(out var application);
        application?.ResetGameOverSaveStatus();

        if (fromDeath)
        {
            //Since we're bypassing saving on exiting, run this now.
            player.Profile.EftStats.LastPlayerState = null;
            player.StatisticsManager.EndStatisticsSession(exitStatus, PastTime);
            player.CheckAndResetControllers(exitStatus, PastTime, Location.Id, exitName);
        }

        var playTimeDuration = DateTimeExtensions.Now - SessionStartTime;

        if (player.InventoryController?.TryCast<ClientInventoryController>() is { } clientInventoryController)
        {
            clientInventoryController.SyncInventoryToProfile();
            clientInventoryController.LogInventorySnapshot("before-profile-descriptor");
        }

        SessionResult parameters = new()
        {
            profile = new ProfileDescriptor(Profile, FikaGlobals.SearchControllerSerializer).ToUnparsedData(),
            result = exitStatus,
            killerId = _playerOwner.Player.KillerId,
            killerAid = _playerOwner.Player.KillerAccountId,
            exitName = exitName,
            inSession = true,
            favorite = Profile.Info.Side == EPlayerSide.Savage,
            playTime = (int)playTimeDuration.ToManaged().Duration().TotalSeconds,
            ProfileId = Profile.Id
        };

        if (player.InventoryController?.TryCast<ClientInventoryController>() is { } clientInventoryControllerAfterDescriptor)
        {
            clientInventoryControllerAfterDescriptor.LogInventorySnapshot("after-profile-descriptor");
        }

        var sentItems = GetOwnSentItems(player.ProfileId).ToDictionary(pair => pair.Key,
            pair => new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<FlatItem>(pair.Value)).ToNativeDictionary();
        await _backEnd.LocalRaidEnded(_raidSettings, parameters, GetLostInsuredItems(), sentItems).AsManaged();
        if (application != null) application.GameOverSaveStatusReceived = true;
    }

    /// <summary>
    /// Retrieves the sent item containers associated with the specified profile from the player's own stash and transit
    /// stash, if available.
    /// </summary>
    /// <param name="profileId">The profile id to get the items from.</param>
    /// <returns>A dictionary mapping stash names to arrays of flat item data for each matching sent item container. The
    /// dictionary is empty if no matching containers are found.</returns>
    public Dictionary<string, FlatItem[]> GetOwnSentItems(string profileId)
    {
        var instance = Singleton<GameWorld>.Instance;
        Dictionary<string, FlatItem[]> dictionary = [];
        var btrController = instance.BtrController;
        if ((btrController?.TransferItemsController.Stash) != null)
        {
            var stash = btrController.TransferItemsController.Stash;
            var stashName = stash.Id + "_btr";
            foreach (var item in stash.Containers)
            {
                if (item.ID == profileId && !dictionary.ContainsKey(stashName))
                {
                    dictionary.Add(stashName, Singleton<ItemFactory>.Instance.TreeToFlatItems(item.Items));
                    break;
                }
            }

        }

        if (EFT.TransitController.Exist(out EFT.TransitController controller))
        {
            bool flag;
            if (controller == null)
            {
                flag = null != null;
            }
            else
            {
                var transferItemsController = controller.TransferItemsController;
                flag = (transferItemsController?.Stash) != null;
            }
            if (flag)
            {
                var stash2 = controller.TransferItemsController.Stash;
                var stashName = stash2.Id + "_transit";
                foreach (var item in stash2.Containers)
                {
                    if (item.ID == profileId && !dictionary.ContainsKey(stashName))
                    {
                        dictionary.Add(stashName, Singleton<ItemFactory>.Instance.TreeToFlatItems(item.Items));
                        break;
                    }
                }
            }
        }

        return dictionary;
    }

    /// <summary>
    /// Stops the local <see cref="CoopGame"/> when waiting for other players
    /// </summary>
    public void StopFromCancel(string profileId, ExitStatus exitStatus)
    {
        Main.Patches.Overrides.RaidEndProfile.Clear();
        if (exitStatus < ExitStatus.Transit)
        {
            FikaBackendUtils.IsTransit = false;
        }

        _logger.LogWarning("Game init was cancelled!");

        var myPlayer = Singleton<GameWorld>.Instance?.MainPlayer?.TryCast<FikaPlayer>();
        if (myPlayer == null)
        {
            _logger.LogWarning("Game init cancellation had no Fika main player");
            return;
        }
        myPlayer.PacketSender.DestroyThis();

        if (myPlayer.Side != EPlayerSide.Savage)
        {
            if (myPlayer.Equipment.GetSlot(EquipmentSlot.Dogtag).ContainedItem != null)
            {
                var result = ItemManipulator.Remove(myPlayer.Equipment.GetSlot(EquipmentSlot.Dogtag).ContainedItem,
                    myPlayer.InventoryController, false);
                if (result.Error != null)
                {
                    _logger.LogWarning("StopFromError: Error removing dog tag!");
                }
            }
        }

        PlayerLeftRequest body = new(FikaBackendUtils.Profile.ProfileId);
        FikaRequestHandler.RaidLeave(body);

        if (GameController.CoopHandler != null)
        {
            foreach (var player in (FikaPlayer[])[.. GameController.CoopHandler.Players.Values])
            {
                if (player == null)
                {
                    continue;
                }

                player.Dispose();
                AssetPoolObject.ReturnToPool(player.gameObject, true);
            }
            GameController.CoopHandler.Players.Clear();
        }
        else
        {
            _logger.LogError("Stop: Could not find CoopHandler!");
        }

        Destroy(GameController.CoopHandler);

        if (GameController.IsServer)
        {
            if (GameController is HostGameController hostGameController)
                hostGameController.StopBotsSystem(true);
        }


        const string exitName = null;
        const float delay = 0f;

        CancelExitManager stopManager = new()
        {
            baseLocalGame_0 = this,
            profileId = profileId,
            exitStatus = exitStatus,
            exitName = exitName,
            delay = delay
        };

        var gameUI = GameUI.Instance;

        if (GameController.ExfilManager != null)
        {
            GameController.ExfilManager.Stop();
        }

        Status = GameStatus.Stopping;
        if (GameTimer != null)
        {
            GameTimer.TryStop();
        }
        if (gameUI.TimerPanel.enabled)
        {
            gameUI.TimerPanel.Close();
        }

        if (EnvironmentManager.Instance != null)
        {
            EnvironmentManager.Instance.Stop();
        }
        MonoBehaviourSingleton<PreloaderUI>.Instance.StartBlackScreenShow(1f, 1f, (Il2CppSystem.Action)stopManager.ExitOverride);
        AppEnvironment.Config.UseSpiritPlayer = false;
    }

    /// <summary>
    /// Toggles the <see cref="DebugUI"/> menu
    /// </summary>
    public void ToggleDebug(bool enabled)
    {
        GameController.ToggleDebug(enabled);
    }

    /// <summary>
    /// Tells the server that we have left the raid
    /// </summary>
    public override ClientMetrics CollectMetrics()
    {
        if (!FikaBackendUtils.IsTransit)
        {
            try
            {
                PlayerLeftRequest body = new(FikaBackendUtils.Profile.ProfileId);
                FikaRequestHandler.RaidLeave(body);
            }
            catch (Exception ex)
            {
                _logger.LogError("Unable to send RaidLeave request to server: " + ex.Message);
            }
        }
        _metricsCollector.Stop();
        return new();
    }

    /// <summary>
    /// Cleans up after the <see cref="CoopGame"/> stops
    /// </summary>
    public override void CleanUp()
    {
        foreach (var player in _players.Values)
        {
            try
            {
                if (player != null)
                {
                    player.Dispose();
                    AssetPoolObject.ReturnToPool(player.gameObject, true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex);
            }
        }
        _players.Clear();
        GameController.CleanUp();
        FikaBackendUtils.CleanUpVariables();
        BTRSide_Patches.Passengers.Clear();
    }

    private class ExitManager
    {
        public CoopGame baseLocalGame_0;
        public string profileId;
        public ExitStatus exitStatus;
        public string exitName;
        public float delay;
        private bool _exitStarted;

        public void ExitOverride()
        {
            if (_exitStarted) return;
            _exitStarted = true;
            MainThreadDispatcher.RunAsync(ExitAsync).Forget();
        }

        private async Task ExitAsync()
        {
            try { await baseLocalGame_0._deathSave.Completion; }
            catch (Exception exception)
            {
                _logger.LogWarning("Early death save did not complete successfully; continuing with normal raid-end saving: " + exception);
            }

            baseLocalGame_0.GameUi.TimerPanel.Close();

            if (baseLocalGame_0._playerOwner != null)
            {
                baseLocalGame_0._playerOwner.Stop();
            }

            EftScreenManager.Instance.CloseAllScreensForced();

            //If we haven't saved, run the original method and stop running here.
            if (!baseLocalGame_0._deathSave.HasSaved)
            {
                baseLocalGame_0._playerOwner.Player.TriggerZones.Clear();
                baseLocalGame_0._playerOwner.Player.TriggerZones.AddRange(baseLocalGame_0.GameController.LocalTriggerZones.ToNativeList());
                await baseLocalGame_0.GameEnd(profileId, exitStatus, exitName, delay).AsManaged();
                return;
            }

            //Most of this is from method_14, minus the saving player part.
            await baseLocalGame_0._playerOwner.Player.OnGameSessionEnd(exitStatus, baseLocalGame_0.PastTime, baseLocalGame_0.Location.Id, exitName).AsManaged();
            baseLocalGame_0.CleanUp();
            MonoBehaviourSingleton<BetterAudio>.Instance?.FadeOutVolumeAfterRaid();

            var duration = DateTimeExtensions.Now - baseLocalGame_0.SessionStartTime;
            StaticManager.Instance.WaitSeconds(delay, (Il2CppSystem.Action)(System.Action)(() =>
            {
                baseLocalGame_0.Status = GameStatus.Stopped;
                baseLocalGame_0._callback.Invoke(new Result<ExitStatus, Il2CppSystem.TimeSpan, ClientMetrics>(exitStatus, duration, baseLocalGame_0.CollectMetrics()));
            }));
        }
    }

    /// <summary>
    /// Used to manage the stopping of the <see cref="CoopGame"/> gracefully when cancelling
    /// </summary>
    private class CancelExitManager
    {
        public CoopGame baseLocalGame_0;
        public string profileId;
        public ExitStatus exitStatus;
        public string exitName;
        public float delay;
        public void ExitOverride()
        {
            var instance = EftScreenManager.Instance;
            if (instance != null && instance.CheckCurrentScreen(EEftScreenType.Reconnect))
            {
                instance.CloseAllScreensForced();
            }
            if (baseLocalGame_0 != null)
            {
                baseLocalGame_0.CleanUp();
                baseLocalGame_0.Status = GameStatus.Stopped;
            }
            if (MonoBehaviourSingleton<BetterAudio>.Instance != null)
            {
                MonoBehaviourSingleton<BetterAudio>.Instance.FadeOutVolumeAfterRaid();
            }
            baseLocalGame_0.GameEnd(profileId, exitStatus, exitName, delay).HandleExceptions();
        }
    }

    public bool IsHeard()
    {
        if (Status != GameStatus.Started)
        {
            return false;
        }
        if (_localPlayer == null)
        {
            return true;
        }
        var flag = VoiceClient.IsTalkDetected();
        _localPlayer.TalkDateTime = flag ? DateTimeExtensions.UtcNow.ToManaged() : default;
        bool flag2;
        bool flag3;
        if (_players.Count == 1)
        {
            flag2 = true;
            flag3 = true;
        }
        else
        {
            flag2 = false;
            flag3 = false;
            var position = _localPlayer.Position;
            foreach (var humanPlayer in GameController.CoopHandler.HumanPlayers)
            {
                if (humanPlayer.IsYourPlayer)
                {
                    continue;
                }

                var valueTuple = humanPlayer.IsHeard(ref position, _voipDistance);
                var item = valueTuple.Item1;
                var item2 = valueTuple.Item2;
                flag2 = flag2 || item;
                flag3 = flag3 || item2;
                if (flag2 && flag3)
                {
                    break;
                }
            }
        }
        VoiceClient.Blocked = !flag3;
        return flag2;
    }

    public void ReportAbuse()
    {
        _logger.LogInfo("NO");
    }
}

