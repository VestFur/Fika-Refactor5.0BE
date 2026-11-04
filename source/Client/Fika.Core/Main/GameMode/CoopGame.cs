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
using Fika.Core.Main.Patches.World;
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
/// Fika 中使用的合作游戏
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
    /// 主玩家撤离时触发
    /// </summary>
    /// <remarks>在无头客机上不会运行，因为没有玩家</remarks>
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
    private readonly RaidSaveState _deathSave = new();
    private float _voipDistance;
    private System.Action<EDamageType> _onLocalPlayerDied;
    private float _nextHandsRepairTime;
    private float _nextInteractionRepairTime;
    private float _stuckInteractionSince;
    /// <summary>
    /// Update 驱动 session 启动例程的句柄。该例程本身位于
    /// <see cref="Fika.Core.Main.Utils.SessionPump"/>——一个普通的托管静态类：这个注入类的私有方法
    /// 会被注册到 il2cpp（自己的 trampoline thunk + 共享的托管
    /// invoker），而正是这种构造让 2026-10-10 的主机 raid 发生 fail fast。该字段保持
    /// internal 只是为了让那个辅助类能持有句柄（analysis/delegate-targets）。
    /// </summary>
    internal FikaRoutinePump.RoutineHandle _sessionRoutine;
    /// <summary>由 <see cref="SessionRun"/> 的 override 设置，原生 session 协程会调用它。</summary>
    internal bool _sessionRunReached;
    private int _handsRepairAttempts;
    private bool _handsRepairInFlight;
    private bool _nativeHandsSelectionNoted;
    private bool _handsProcessCompletedNoted;

    // 手部锁定看门狗（"operating too fast"）。计时器放在这里，是为了让
    // 逐帧的插件钩子能对每个关注点各调用一个看门狗。
    private float _stuckHandsSince;
    private float _nextHandsLockRepairTime;
    private int _handsLockRepairs;
    private float _nextInventoryGateCheckTime;
    private float _inventoryGateBlockedSince;
    private int _inventoryGateRepairRounds;

    public EftGamePlayerOwner DiagnosticPlayerOwner => _playerOwner;

    /// <summary>
    /// 创建一个 <see cref="CoopGame"/>
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

        // （analysis/map-fixed-time）有些地图会固定自己的时钟。原生匹配面板
        // （EFT.UI.Matchmaker.LocationConditionsPanel）为每张这样的地图携带一个静态的 "<map>FixedTime"
        // 值（Icebreaker），并把这个值直接喂给 raid 时间选择器，所以正式游戏
        // 在那里不给玩家任何时间选项。Fika 的 raid 从不运行那个面板，于是 raid 是按
        // raid 设置里原有的值启动的——在本移植上通常是 -1（"跟随世界时钟"）
        // 也就是 Fika raid 设置界面留下的那个值。改用地图自己的时间。
        //
        // 转图被有意排除：它延续的是一个时钟已经越过固定时间的 raid，
        // 再次固定会让那台机器的时钟倒退。
        // 'Coop / Honor Map Fixed Time' 关闭 = 玩家自己的设置，也就是这段代码存在之前
        // 的行为（翻一个开关即回到旧路径）。
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
        _logger.LogInfo($"Raid time: hourOfDay={effectiveHourOfDay} " +
            $"({(honorFixedTime ? "map fixed time" : effectiveHourOfDay == -1 ? "current world time" : "custom")}), " +
            $"server={FikaBackendUtils.IsServer}, scav={FikaBackendUtils.IsScav}, " +
            $"worldHour={backendDateTime.StatedGameDateTime.Hour}, " +
            $"fixed={fixedField}, playerHourOfDay={timeAndWeather.HourOfDay}, " +
            $"isRandomTime={timeAndWeather.IsRandomTime}");
        if (honorFixedTime)
        {
            _logger.LogInfo($"[MapFixedTime] {location.Id} carries a fixed clock time: using {fixedField} and ignoring " +
                $"the player's hourOfDay={timeAndWeather.HourOfDay}");
        }
        if (effectiveHourOfDay != -1)
        {
            var currentTime = backendDateTime.StatedGameDateTime;
            Il2CppSystem.DateTime newTime = new(currentTime.Year, currentTime.Month, currentTime.Day, effectiveHourOfDay,
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
    /// 用于创建一个 <see cref="EftGamePlayerOwner"/>
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
            gamePlayerOwner.OnLeave += (System.Action)_game.LocalLeaveGame;
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
    /// 倒计时部署界面
    /// </summary>
    /// <remarks>
    /// 只有原生 <see cref="SessionRun(Il2CppSystem.Action)"/> 协程会调用它，而泵在调用到它之前
    /// 就已经跑完了倒计时（泵自己启动那个协程，见第 3 步）。因此返回的是
    /// 一个已经跑完的*原生*枚举器，而不是本方法过去交给引擎的托管包装
    /// （<c>InitSessionUIManaged().WrapToIl2Cpp()</c>）：正是释放这样一个包装时，引擎在主机 raid
    /// 启动期间本方法调用方运行后的一帧崩溃了
    /// （analysis/host-raid-coroutine）。
    /// </remarks>
    public override Il2CppSystem.Collections.IEnumerator InitSessionUI()
    {
        
        return NativeRoutines.Completed();
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
        if (!TransitController.IsTransit(profile.Id, out int _))
        {
            profile.SetSpawnedInSession(false);
        }

        var fikaPlayer = await FikaPlayer.Create(gameWorld, playerId, position, rotation, "Player", "Main_", EPointOfView.FirstPerson,
            profile, false, UpdateQueue, armsUpdateMode, Player.EUpdateMode.Auto,
            AppEnvironment.Config.CharacterController.ClientPlayerMode, getSensitivity, getAimingSensitivity,
            statisticsManager, new ClientViewFilter(), session, playerId, FikaSingleton<IFikaNetworkManager>.Instance.StrictInventorySync);

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

        if (!GameController.IsServer)
        {
            AnnounceCharacter(fikaPlayer);
        }
        else
        {
            FikaGlobals.LogInfo($"Not announcing a character: isServer={GameController.IsServer}, " +
                $"scavLateJoin={FikaBackendUtils.IsScavLateJoin}");
        }

        _logger.LogInfo("Adding debug component...");
        GameController.CreateDebugComponent();

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
    /// 初始化本地玩家，替换 <see cref="BaseLocalGame{TPlayerOwner}.method_4(BotControllerSettings, string, InventoryController)"/>
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

        // （analysis/cutscene-native-registration）这份列表里本移植缺失的最后一项。原生
        // method_4 创建了这一整族控制器，而每个控制器都在同一处注册自己的逐帧
        // 调用者；两个过场控制器却是由世界工厂创建、从未在任何地方注册，
        // 这就是从来没有东西 tick 它们的原因（gate:ticks engine=0）。在原生构建所做的
        // 那个位置创建并注册它们。
        try
        {
            GameController.InitCutsceneControllers(gameWorld);
        }
        catch (Exception error)
        {
            _logger.LogError("InitCutsceneControllers failed: " + error.GetType().Name + ": " + error.Message);
        }

        var config = AppEnvironment.Config;
        if (config.FixedFrameRate > 0f)
        {
            FixedDeltaTime = 1f / config.FixedFrameRate;
        }

        
        try
        {
            var player = await CreateLocalPlayer()
                ?? throw new NullReferenceException("InitPlayer: Player was null!");
            _players.Add(player.ProfileId, player);
            _playerOwner = _func_1(player);
            

            // 原版 SPT 5 的 LocalGame 路径把本地玩家注册到
            // 原生世界里，时机是 PlayerFactory 返回之后立刻。Fika
            // 是通过自己的托管路径创建玩家的，所以那次原生
            // 注册可以被跳过，即使 MainPlayer 恰好被
            // 另一个启动回调填好。在创建相机/owner 输入链之前，
            // 把两侧都显式注册好。这样就把
            // GamePlayerOwner、MainPlayer 与原生玩家列表
            // 绑定到 Fika 网络所使用的同一个对象上。
            RegisterNativePlayerWithWorld(gameWorld, player);

            // 这次原生调用是有意延迟的。FikaPlayer 启动时
            // 持有的是一个空手控制器，而 SPT 5 的原生辅助方法可能
            // 在 owner/输入链存在之前就运行，从而让本地玩家
            // 静默地永久停在 EmptyHandsController。RepairLocalHands
            // 会在 owner 与 raid 完全启动之后重试同一套原版选择，
            // 然后回退到 FikaPlayer.Proceed(Weapon)。

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

        // 注意：不要在这里再次调用 GetQuestLootReady + SpawnLoot。上面的 SpawnLoot(location)
        // 已经把该描述符过了一遍 GetQuestLootReady，后者会把带任务标记的条目
        // 分流进 GameWorld._questLootItems，并把剩下的实例化——所以第二遍会给每一个
        // 普通地面战利品物品再刷出重复的一份（即被报告的那些幽灵实体）。
        // 被延迟的任务条目由 GameWorld_SpawnLoot_QuestLoot_Patch
        // （GameWorld.SpawnLoot -> ManageQuestLoot 的 postfix）实例化，它会打 [QuestLoot]。

        GameController.CoopHandler.ShouldSync = true;

        // 在这里调用继承来的 IL2CPP 虚方法包装，可能会重入
        // 原生的 BaseLocalGame.Run 路径。在 SPT 5 上那条路径会调用
        // LocalBotsSpawnInitialization，并绕过 Fika 的主机/客机 bot
        // 控制器，从而让原生刷怪上下文不完整，并以
        // NullReferenceException 失败，大厅根本起不来。
        // 改为直接派发到 Fika 的托管控制器。
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
        RepairLocalHands(force: true);
        EnsureRaidCameraActive();
        FikaEventDispatcher.DispatchEvent(new GameWorldStartedEvent(GameWorld));
    }

    /// <summary>
    /// （2026-10-17）当一次 raid 启动把相机管理器关掉时，重新激活它。本类
    /// 自己会把它关掉（InitPlayer 中的 CameraManager.Instance.IsActive = false），而唯一
    /// 把它重新打开的地方是倒计时拆除流程。当转图重载的覆盖层从未出现时，那次拆除
    /// 不会运行，于是下一张地图加载后机器就停在黑帧上
    /// （Shoreline -> Terminal 转图后主机侧的已报告现象）。只有非激活的管理器
    /// 会被碰，所以正常的 raid 启动——它本来就已经是激活的——完全保持原样。
    /// </summary>
    private static void EnsureRaidCameraActive()
    {
        try
        {
            if (!CameraManager.Exist)
            {
                return;
            }

            var cameraManager = CameraManager.Instance;
            if (cameraManager == null || cameraManager.IsActive)
            {
                return;
            }

            cameraManager.IsActive = true;
            cameraManager.Blur(false, 0f);
            cameraManager.SetZBlur(false);
            var blur = cameraManager._blur;
            if (blur != null)
            {
                blur.Disable();
                blur._value = 0f;
                blur.enabled = false;
            }

            FikaGlobals.LogWarning("[RaidCamera] the camera manager was inactive after the raid start; re-activated it");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning("[RaidCamera] could not re-activate the camera manager: " + exception.Message);
        }
    }

    /// <summary>
    /// 部署之后重新进入原版的首件物品选择。SPT 5 的
    /// IL2CPP LocalGame 通常在 PlayerOwner.Run() 之后执行它；而 Fika 的
    /// 托管启动路径可能在没有那次回调的情况下就到达 raid。
    /// </summary>
    public void RepairLocalHands(bool force = false)
    {
        if (_localPlayer == null || _playerOwner == null || Status is not (GameStatus.Running or GameStatus.Started))
            return;
        if (_localPlayer.Downed || !_localPlayer.HealthController.IsAlive)
            return;

        // 本次修复所复刻的那次原生选择由 PlayerOwner.Run() 运行：
        // 它的 vmethod_0 会在手上控制器是 EmptyHandsController 时调用
        // Player.SetFirstAvailableItem，而它所用的装备闸门
        // （Player.IsItemCanBeEquipped = 可及位置 + CheckAction）
        // 在 owner 运行起来之前无法满足。在
        // 2026-10-10 的主机 raid 中，每一个早期探测仍然消耗了八次尝试中的一次
        // （LogOutput.log 7481..7496 = 倒计时期间的第 1..8 次尝试），
        // 所以 "Raid has been started" 之后的那两次强制修复被
        // 尝试预算丢掉，raid 于是空手开局。改为等待 owner，
        // 而不是去数那些不可能成功的探测；预算留给
        // 那些能成功的重试。
        if (_playerOwner.State != EFT.PlayerOwner.EState.Started)
        {
            return;
        }

        if (_localPlayer.HandsController?.TryCast<Player.EmptyHandsController>() == null)
        {
            NoteNativeHandsSelection();
            NoteNativeHandsProcessCompleted();
            return;
        }

        if (!force && Time.time < _nextHandsRepairTime)
            return;
        _nextHandsRepairTime = Time.time + 0.5f;

        if (_handsRepairInFlight || _handsRepairAttempts >= 8)
            return;

        _handsRepairAttempts++;
        _logger.LogInfo($"RepairLocalHands attempt {_handsRepairAttempts}: owner={_playerOwner.State}, hands={_localPlayer.HandsController?.GetType().FullName}");
        RestoreFirstAvailableItem("RepairLocalHands");
    }

    /// <summary>
    /// 每次 raid 一行：本地玩家拥有的不再是启动时的那个
    /// EmptyHandsController 之后打印。原版启动路径是
    /// <c>PlayerOwner.Run()</c> -&gt; <c>Player.SetFirstAvailableItem</c>，所以这行
    /// 日志证明原生选择确实到达了武器：下一条日志要么带着它（原生路径
    /// 已完成），要么带着 RepairLocalHands 的警告（安全网
    /// 不得不运行）。
    /// </summary>
    private void NoteNativeHandsSelection()
    {
        if (_nativeHandsSelectionNoted)
        {
            return;
        }

        _nativeHandsSelectionNoted = true;
        try
        {
            var hands = _localPlayer.HandsController;
            _logger.LogInfo($"[HandsRepair] native hands selection completed: hands={hands?.GetType().FullName}, " +
                $"item={hands?.Item?.Id}, process={_localPlayer.ProcessStatus}, " +
                $"operation={DescribeHandsOperation(hands)}");
        }
        catch (Exception exception)
        {
            _logger.LogWarning($"[HandsRepair] could not describe the selected hands controller: {exception.Message}");
        }
    }

    /// <summary>
    /// 每次 raid 一行：一旦原生手部切换可以被证明完成——手上是真实控制器，
    /// 且玩家层级的进程闸门回到 None。2026-10-10 09:45 那次 raid 从未到达
    /// 该状态——它的进程从第一次切换起一直停在 Scheduled，直到 raid 结束——
    /// 所以这行日志（连同那些 "stayed locked" 警告）才是切换现在能完成的证据。
    /// </summary>
    private void NoteNativeHandsProcessCompleted()
    {
        if (_handsProcessCompletedNoted || _localPlayer.ProcessStatus != Player.EProcessStatus.None)
        {
            return;
        }

        _handsProcessCompletedNoted = true;
        var hands = _localPlayer.HandsController;
        _logger.LogInfo($"[HandsRepair] hands process completed: hands={hands?.GetType().FullName}, " +
            $"item={hands?.Item?.Id}, process={_localPlayer.ProcessStatus}, " +
            $"operation={DescribeHandsOperation(hands)}");
    }

    /// <summary>
    /// 手部操作正是原生 <c>ItemHandsController.ManualUpdate</c> 解引用的那个字段
    /// （<c>CurrentHandsOperation.Update(deltaTime)</c>），而它只由控制器的 <c>Spawn</c> 中的
    /// <c>InitiateOperation</c> 赋值。在真实控制器已设置时操作却为 null，正是
    /// 2026-10-10 09:45 那次 raid 产生 30 万行 NullReference 洪流的那个状态
    /// （<c>ItemHandsController.ManualUpdate</c> &lt;-
    /// <c>FirearmController.ManualUpdate</c> &lt;- <c>Player.ArmsUpdate</c>）：操作工厂
    /// 字典从未被构建，所以 spawn 在第一个操作被设置之前就中止了。
    /// </summary>
    private static string DescribeHandsOperation(Player.AbstractHandsController hands)
    {
        try
        {
            var operation = hands?.TryCast<Player.ItemHandsController>()?.CurrentHandsOperation;
            return operation == null ? "<null>" : operation.GetIl2CppType().Name;
        }
        catch (Exception exception)
        {
            return "unreadable (" + exception.GetType().Name + ")";
        }
    }

    /// <summary>
    /// 把第一件可用物品放回本地玩家手中。它由
    /// 部署修复（空手）与手部锁定看门狗（控制器再也无法开火
    /// 或移动物品）共用。部署路径
    /// 保留它历史悠久的日志行，这样现有的诊断仍然
    /// 能对上。
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
            // 每一次手部切换都是一个受 Player.ProcessStatus 控制的
            // EFT.Player.Process：当它不是 None 时，原生调度器会跳过
            // 下一次 Proceed（RepairHandsLock 释放的就是同一道闸门）。
            // 一次其 DropCurrentController 从未完成的切换会把它留着，
            // 于是修复会被静默跳过。
            if (_localPlayer.ProcessStatus != Player.EProcessStatus.None)
            {
                _logger.LogWarning($"{tag} clearing the hands process status {_localPlayer.ProcessStatus} left by an unfinished switch");
                _localPlayer.ProcessStatus = Player.EProcessStatus.None;
            }

            var weapon = FindFirstEquippedWeapon(out var source);
            if (weapon != null)
            {
                _logger.LogInfo($"{tag} selected weapon {weapon.Id} ({weapon.GetType().FullName}) from {source}");
                // 直接调用 FikaPlayer.Proceed 会绕过原生的
                // TrySetLastEquippedWeapon 路径，而该路径在托管 owner 尚未
                // 就绪时可能变成空操作。
                _localPlayer.Proceed(weapon, null, scheduled: false);
                return;
            }

            // 小刀控制器仍然能满足背包需求（它的 CanExecute 返回 true），
            // 所以它比让玩家完全没有手部要好，是一个更好的
            // 兜底。
            if (includeMelee)
            {
                var melee = FindEquipment(out _)?.GetSlot(EquipmentSlot.Scabbard)?.ContainedItem
                    ?.GetItemComponent<KnifeComponent>();
                if (melee != null)
                {
                    _logger.LogWarning($"{tag} found no firearm, falling back to the melee weapon {melee.Item.Id}");
                    _localPlayer.Proceed(melee, (Callback<IKnifeController>)null, scheduled: false);
                    return;
                }
            }

            // 保留历史上的措辞（现有诊断按它匹配），并追加
            // 逐槽位的真相：该槽位是空的、装的是非武器，还是装着一把被
            // 原生闸门拒绝的武器。
            _logger.LogWarning($"{tag} found no weapon in FirstPrimaryWeapon/SecondPrimaryWeapon/Holster [{DescribeWeaponSlots()}]");
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
    /// 地面战利品拾取运行在 EFT.PickUpState 加上 animator 的
    /// 交互队列之中。当这个状态被留在身后时，玩家会在整个 raid 剩下的
    /// 时间里保留它的限制（无法转视角、无法移动、无法
    /// 蹲下、没有交互提示），尽管背包与开火仍然
    /// 正常。只在失败的操作里释放它并不够，因为
    /// 该状态可以在一个报告成功的操作之后继续存在，所以要监视
    /// 本地玩家，并释放一个持续时间远超原生动画的
    /// 拾取/战利品状态。
    /// </summary>
    public void RepairLocalInteractionState()
    {
        // [LootDiag]（analysis/pickup-auto-fallback，Z4）：仅做逐帧的拾取路径记账——
        // raid 运行期间每 60 秒一次模式心跳，一旦停止就重置一次，这样开关
        // 绝不会活到下一次 raid。它必须在下面的 null 守卫之前运行：
        // 两次 raid 之间本地玩家已经没了，而开关仍必须被清掉。
        GroundLootPickup.TickPickupModeWatchdog(_localPlayer,
            Status is GameStatus.Running or GameStatus.Started);

        if (_localPlayer == null || _playerOwner == null)
        {
            return;
        }
        if (Status is not (GameStatus.Running or GameStatus.Started))
        {
            return;
        }
        if (_localPlayer.Downed || !_localPlayer.HealthController.IsAlive)
        {
            _stuckInteractionSince = 0f;
            _stuckHandsSince = 0f;
            return;
        }

        // 手部锁定有自己的计时器；下面的交互状态
        // 只每秒 tick 一次，不得对它形成闸门。
        try
        {
            RepairHandsLock();
        }
        catch (Exception exception)
        {
            // 一个已释放的玩家或一个抛异常的原生访问器，都不能把
            // 逐帧的插件钩子一起带下去。复用修复的
            // 冷却时间，这样持续失败不会刷爆日志。
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

        // [InvDiag]（2026-10-27，T3）下面的状态切换看不到的两个交互锁定信号：
        // MovementContext.CanInteract 被持有，以及 animator 的交互计划被拒。NativeInteractionRelease
        // 已经把两者都当作"卡住"，但从没有人轮询过它们——过去只监视 Pickup/Plant 状态名，
        // 于是一次丢失的地面战利品拾取让计划被拒之后，整个 raid 里之后每一次拾取都被拒绝。
        NativeInteractionRelease.WatchLostInteractionSchedule(_localPlayer,
            _localPlayer.InventoryController?.TryCast<BaseInventoryController>()?.PendingOperationCount ?? 0, Time.time);

        // 只针对 EPlayerState.Pickup：一次地面物品拾取的动画远不到两
        // 秒，而搜索容器（Loot）、放置、破门以及
        // 正常交互合法地会持续数秒，绝不能在这里被
        // 取消。所报告的冻结只来自地面战利品。
        // 地面战利品（Pickup）在一次远不到两秒的拾取动画之后就锁定；
        // 而放置任务物品（Plant）合法地耗时更长，所以在它算作卡住之前
        // 会拿到大得多的预算。
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
        var heldFor = Time.time - _stuckInteractionSince;
        _stuckInteractionSince = 0f;
        // [LootDiag]（analysis/pickup-auto-fallback，Z4）：在下面的释放改写原生拾取状态机
        // 之前先给它拍个快照，然后把本机的地面战利品拾取切到
        // Fika 的旁路（每次 raid 一次，且只针对真正的 EPlayerState.Pickup 卡住——见
        // GroundLootPickup.NoteStuckPickup）。释放逻辑完全保持原样。
        GroundLootPickup.DiagnoseStuckState(movementContext, stuckState, heldFor);
        GroundLootPickup.NoteStuckPickup(_localPlayer, stuckState, heldFor);
        // 当 take-item 动画事件从未触发时，原生拾取续接（MovementContext.PickupAction）就丢失了。
        // 在释放该状态之前把它运行一次。
        global::Fika.Core.Main.Utils.PickupContinuationRepair.TryRepair(_localPlayer, movementContext, stuckState, heldFor);
        _logger.LogWarning($"Local player stayed in {stuckState} for more than {timeout} seconds; releasing the native interaction state");
        NativeInteractionRelease.Release(_localPlayer, "stuck-pickup-state");
    }

    /// <summary>
    /// 原生背包闸门在关闭期间会拒绝每一个物品操作：
    /// 拖放视图（<c>GridView</c>/<c>SlotView.CanDrag</c>）以及
    /// <c>Player.SetItemInHands</c>/<c>SetQuickSlotItem</c> 全都经过
    /// <c>Item.CheckAction</c> -> <c>PlayerInventoryController.CheckItemAction</c>。
    /// <c>ItemController.Locked</c> 只由
    /// <c>MovementContext.StateLocksInventory</c> 写入（某个移动状态的 Enter 把它置位，
    /// 它的 Exit 把它清掉），而一个未完成的物品事件会让单个物品
    /// 保持忙碌，所以一个 Exit 从未运行过的状态，会让背包在整个 raid
    /// 剩下的时间里都是死的，而移动仍然正常。
    /// <para>
    /// 这个看门狗被刻意做得很窄：只有当玩家正
    /// 试图使用背包（背包界面已打开，或某个操作
    /// 刚刚被闸门拒绝）并且玩家已空闲数秒而闸门
    /// 仍然关闭时，它才会动手。门、接近或
    /// 原地交互合法地持有该锁，并且绝不会让玩家
    /// 处于空闲，所以它永远不会被碰。
    /// </para>
    /// </summary>
    public void RepairLocalInventoryState()
    {
        // 闸门必须持续关闭这么久才算卡住：每一个
        // 合法的持有者（一次手部切换、一次接近门、一次上弹）
        // 都在这份预算之内就释放了。
        const float GateTimeoutSeconds = 6f;
        const float RefusalWindowSeconds = 60f;

        if (_localPlayer == null || _playerOwner == null)
        {
            return;
        }
        if (Status is not (GameStatus.Running or GameStatus.Started)
            || _localPlayer.Downed || !_localPlayer.HealthController.IsAlive)
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

        // 手中持有消耗品或可用物品时，合法地会持有同一道闸门
        // 数秒（药、食物、测距仪）；绝不要碰这种状态。
        if (HasConsumableInHands())
        {
            ResetInventoryGateWatch();
            return;
        }

        // 没有处理器的服务器回调注册是明确的：一个活着的
        // 操作总是拥有自己的注册，而注册表和
        // 注册都只在这个线程上被碰。FikaPlayer.WaitingForCallback
        // 会统计这些注册，而客机手部控制器在它为 true 时
        // 对 CanPressTrigger/CanStartReload 回答 false，于是一个泄漏的
        // 注册会让本地玩家在整个 raid 剩下的时间里无法开枪。
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

        // 保留时间戳，这样没起作用的修复会在同一个
        // 超时之后再次运行，而不是每帧一次。
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
    /// 玩家是否正在使用消耗品或其他可用物品。这些
    /// 手部控制器在运行时会持有同一道进程/背包闸门数秒，
    /// 所以闸门看门狗不得把它们当作卡住。
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

    /// <summary>玩家此刻是否打开了背包界面。</summary>
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
    /// "Operating too fast" 手部锁定。每一次手部切换都是一个
    /// 受 <c>Player.ProcessStatus</c> 控制的 <c>EFT.Player.Process</c>：当该
    /// 状态不是 <c>None</c> 时，背包控制器会报告自己已锁定，
    /// 下一次 <c>Proceed</c> 会被原生调度器跳过——包括那次
    /// 本该从快速小刀切回上一把武器的切换。
    /// 因此双击快速小刀可能把过渡
    /// 控制器永久留在手上：它拒绝每一个背包操作
    /// （<c>QuickKnifeKickController.CanExecute</c> 为 false），而且没有枪械时，
    /// 什么都开不了火。移动不依赖手部，而这正是玩家
    /// 所见到的现象。监视它并释放它，但只能在任何
    /// 合法切换都可能已经结束后、且要很久之后。
    /// </summary>
    private void RepairHandsLock()
    {
        // 一次原生切换（丢弃、生成与主机确认）会远在
        // 一秒之内完成，所以这些预算绝不会打断合法的切换。
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
        // 一个已注册的 proceed 回调正是 WaitingForCallback 所报告的东西，
        // 而 FikaClientFirearmController 在它被置位时拒绝开火。
        var waitingForCallback = _localPlayer.PendingProceedCallbacks > 0;

        float timeout;
        if (gateHeld || handsMissing || handsDestroyed)
        {
            timeout = HandsLockTimeout;
        }
        else if (handsTransitional)
        {
            // 快速小刀、快速手雷与快速使用控制器只
            // 在它们的动画把上一个物品交回来的那段时间里占着手。
            timeout = TransitionalHandsTimeout;
        }
        else if (waitingForCallback)
        {
            // 手部是好的，但一次主机从未应答的手部切换会让
            // 枪械控制器上的 CanPressTrigger/CanStartReload 保持 false。
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
            $"operation={DescribeHandsOperation(hands)}, " +
            $"inventoryLocked={playerInventoryController?.Locked}, " +
            $"inventoryBlocked={inventoryController?.IsInventoryBlocked()}, " +
            $"pendingProceedCallbacks={_localPlayer.PendingProceedCallbacks}); releasing");

        // 1. 玩家层级的切换闸门。每一个完成的原生切换
        //    留下的都是 None；当它被置位时，原生 Process.Proceed
        //    调度器会跳过之后每一次手部切换。
        TryHandsRepairStep("process status", () =>
        {
            if (_localPlayer.ProcessStatus != Player.EProcessStatus.None)
            {
                _localPlayer.ProcessStatus = Player.EProcessStatus.None;
            }
        });

        // 2. 同一道闸门也是拒绝物品操作的原因：
        //    在 IsInventoryBlocked()（它转发到
        //    Locked）为 true 时，PlayerInventoryController.CheckItemAction
        //    会返回 "inventory blocked" 错误。写这个属性就是原生解锁。
        TryHandsRepairStep("inventory lock", () =>
        {
            if (playerInventoryController != null && playerInventoryController.Locked)
            {
                playerInventoryController.Locked = false;
            }
        });

        // 3. 一个主机从未送达的回答会让它的回调保持
        //    注册状态、WaitingForCallback 保持 true，
        //    FikaClientFirearmController 会把它们变成 CanPressTrigger/
        //    CanStartReload == false。
        TryHandsRepairStep("callback expiry", () =>
        {
            var expired = _localPlayer.ExpireStaleProceedCallbacks(ProceedCallbackTimeout);
            if (expired > 0)
            {
                _logger.LogWarning($"[HandsRepair] dropped {expired} proceed callback(s) the host never answered");
            }
        });

        // 4. 已经消失、被销毁，或仍握着一个一次性使用
        //    控制器的手，既不能开火也不能移动物品，所以把一个可用物品
        //    放回去。先复用原生交互释放：它用于清除
        //    一个被持有的 animator 交互队列，而且它从不碰物品。
        if (handsMissing || handsDestroyed || handsTransitional)
        {
            TryHandsRepairStep("interaction release", () => NativeInteractionRelease.Release(_localPlayer, "hands-lock"));
            RestoreFirstAvailableItem("[HandsRepair]", includeMelee: true);
        }
    }

    /// <summary>
    /// 只为了让上一个物品交回来而存在的控制器：快速
    /// 小刀踢、快速手雷与快速使用物品。比它的动画
    /// 持有一个更久，意味着切回去的那次切换丢失了。
    /// </summary>
    private static bool IsTransitionalHandsController(Player.AbstractHandsController hands)
    {
        // 原生类对 CLR 类型检查不可见；TryCast 能看到它。
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

    private Weapon FindFirstEquippedWeapon(out string source)
    {
        var equipment = FindEquipment(out source);
        foreach (var slotType in FikaGlobals.WeaponSlots)
        {
            var item = equipment?.GetSlot(slotType)?.ContainedItem;
            var weapon = item?.TryCast<Weapon>();
            if (weapon != null && _localPlayer.IsItemCanBeEquipped(weapon))
                return weapon;
        }
        return null;
    }

    /// <summary>
    /// 引擎自己读取的那份装备。<c>Player.SetFirstAvailableItem</c>
    /// 会遍历 <c>InventoryController.Inventory.Equipment</c>；在原版玩家上
    /// 它与 <c>Player.Equipment</c> 是同一个对象，但 Fika 会通过网络替换掉
    /// 背包树，只有控制器能保证
    /// 指向活着的那一份，所以优先用控制器，取不到再退回
    /// 玩家。调用方会记录是哪一面给出的答案，因为读错
    /// 树的那次修复什么也选不出来。
    /// </summary>
    private InventoryEquipment FindEquipment(out string source)
    {
        try
        {
            var fromController = _localPlayer.InventoryController?.Inventory?.Equipment;
            if (fromController != null)
            {
                source = "InventoryController.Inventory";
                return fromController;
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[HandsRepair] reading InventoryController.Inventory failed: {exception.Message}");
        }

        source = "Player.Equipment";
        return _localPlayer.Equipment;
    }

    /// <summary>
    /// 修复刚刚读取的那份装备的逐槽位状态。只在没有
    /// 选出武器时才打印，这样下一次日志就能把 "槽位里没有武器"
    /// 与 "有武器，但原生闸门拒绝了它" 区分开——旧日志做不到，
    /// 而在 raid 空手开局时，这两种情况被报成同一条警告。
    /// </summary>
    private string DescribeWeaponSlots()
    {
        var equipment = FindEquipment(out var source);
        var description = new System.Text.StringBuilder("source=").Append(source);
        if (equipment == null)
        {
            return description.Append(", equipment=null").ToString();
        }

        foreach (var slotType in FikaGlobals.WeaponSlots)
        {
            description.Append(", ").Append(slotType).Append('=');
            try
            {
                var item = equipment.GetSlot(slotType)?.ContainedItem;
                if (item == null)
                {
                    description.Append("empty");
                    continue;
                }

                description.Append(item.Id).Append(" (").Append(item.GetIl2CppType().FullName).Append(", ");
                var weapon = item.TryCast<Weapon>();
                description.Append(weapon == null
                    ? "not-a-weapon"
                    : (_localPlayer.IsItemCanBeEquipped(weapon) ? "selectable" : "gate-refused"));
                description.Append(')');
            }
            catch (Exception exception)
            {
                description.Append("read-failed: ").Append(exception.Message);
            }
        }

        return description.ToString();
    }

    /// <summary>
    /// 创建本地玩家
    /// </summary>
    /// <returns>一个 <see cref="Player"/></returns>
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

        // SPT 在创建初始 raid 请求时会消耗入场
        // 需求。转图的 raid 已经携带了权威的
        // profile/transition 状态；在这里再消耗一次需求
        // 会再扣掉一次钥匙耐久（而且对于一把已消耗的钥匙，
        // 还会让下一张地图无法通过准入检查）。
        var isTransitRaid = FikaBackendUtils.IsTransit || TransitController.IsTransit(Profile.Id, out int _);
        if (!isTransitRaid &&
            Profile.Side != EPlayerSide.Savage && Profile.Info.Level >= Location.MinPlayerLvlAccessKeys)
        {
            var access = Location.GetAccessRequirements(EGameMode.Pve,
                FikaBackendUtils.IsTransit ? JsonType.LocationSettings.Location.EAccessUsageType.Transit : JsonType.LocationSettings.Location.EAccessUsageType.Menu);
            var items = Profile.Inventory.GetPlayerItems(EPlayerItems.Equipment);
            if (access != null && access.Count > 0 && items != null)
            {
                // SPT 5 支持所需物品数量与剩余钥匙耐久。
                // 让游戏的工具类去消耗选中的需求。
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
            if (Profile.Side != EPlayerSide.Savage)
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

        // 在创建本地玩家期间运行，也就是在 raid 启动内部。这里如果有
        // delegate 绑定抛异常，整个加入流程就会中止（Nullable<float> 匹配器
        // delegate 杀掉一次 raid 正是这么发生的），所以订阅退化成"主机不会被告知撤离
        // 交互"。
        if (NativeDelegates.TryBindManaged<System.Action<ExfiltrationPoint, bool>>(this, nameof(OnEpInteraction),
                "CoopGame.OnEpInteraction", out var onEpInteraction))
        {
            myPlayer.OnEpInteraction += onEpInteraction;
        }

        _localPlayer = myPlayer.TryCast<FikaPlayer>()
            ?? throw new InvalidOperationException("Local player factory did not return a FikaPlayer");
        GameController.SetLocalPlayer(_localPlayer);

        _logger.LogInfo("Local player created");
        return myPlayer;
    }

    /// <summary>
    /// 把我们的角色告诉主机。一个走进已在运行的 raid 的 player scav
    /// 可能在自己的网络连接建立之前就发这条消息，那样主机
    /// 永远不会知道这个角色，为它找不到玩家，并
    /// 拒绝每一个 proceed 请求。因此允许重复宣告。
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
    /// 当主机报告它还没有为我们绑定玩家时，重新宣告
    /// 本地角色。
    /// </summary>
    internal static void ReannounceLocalCharacter()
    {
        // Proceed 应答在网络线程上到达，但 packet 写入器与
        // packet 处理器是与主线程共享的，所以这条宣告
        // 必须在 Unity 线程上构建。
        MainThreadDispatcher.Post(() =>
        {
            if (FikaSingleton<IFikaGame>.Instance is not CoopGame coopGame || coopGame._localPlayer == null) return;
            if (coopGame.GameController.IsServer) return;
            AnnounceCharacter(coopGame._localPlayer);
        });
    }

    /// <summary>
    /// 临时绕行方案：SPT 在 >3.11 上不会为狗牌生成唯一 ID
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
    /// 在后端设置游戏状态
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
    /// Bot 系统启动器 -> 倒计时
    /// </summary>
    /// <param name="controllerSettings"></param>
    /// <param name="spawnSystem"></param>
    /// <returns></returns>
    public override Il2CppSystem.Threading.Tasks.Task LocalBotsControllerInitialization(BotControllerSettings controllerSettings, ISpawnSystem spawnSystem)
    {
        return NativeTask.FromManaged(MainThreadDispatcher.RunAsync(() => GameController.StartBotSystemsAndCountdown(controllerSettings, GameWorld)));
    }

    // 原生回调被原样转发：SessionRun 的 interop 参数是 Il2CppSystem.Action，
    // 而托管闭包无法被 marshal 进原生 delegate 槽位。
    //
    // 引擎从原生 PrepareSession（StartCoroutine(SessionRun(complete))）调用它，并通过它的
    // 协程调度器驱动返回的东西。在这里返回托管枚举器
    // （SessionRunManaged(...).WrapToIl2Cpp()，3.0.0 的形态）正是主机 raid 启动期间
    // 出故障的隐患（analysis/host-raid-coroutine），所以序列在 Update 驱动的泵上运行，而引擎
    // 收到的是一个已经跑完的*原生*枚举器。
    public override Il2CppSystem.Collections.IEnumerator SessionRun(Il2CppSystem.Action runCallback)
    {
        // 序列本身不是本类的成员（见 SessionPump）：这个注入方法
        // 由引擎通过 override 的 vtable 槽位调用（这是原生入口，属于
        // 合法方向），而它绝不能把任何*注入*方法交给原生侧。
        SessionPump.Start(this, runCallback ?? FikaGlobals.EmptyNativeAction);
        return NativeRoutines.Completed();
    }

    // session 启动序列（FinishRaidSetup -> 倒计时界面 -> 原生 session 协程）
    // 以前作为私有方法 StartSessionPump/SessionSequence 放在这里。它现在位于
    // Fika.Core.Main.Utils.SessionPump，一个普通的托管静态类：Il2CppInterop 会把这个注入类的
    // 每一个已声明方法（包括私有方法）注册到 il2cpp，并为其中每一个构建一个原生
    // trampoline thunk 加一个共享的托管 invoker。invoker 收尾时 calli 进入
    // 这样一个 thunk，正是让 2026-10-10 主机 raid 发生 fail fast 的原因，而那次崩溃里点名的 thunk
    // 恰恰就是本类的 StartSessionPump（analysis/run-2026-10-10-1553/scan-thunk-result.txt，
    // analysis/delegate-targets/REPORT.md）。引擎自己的入口点留在这里：
    // 上面的 SessionRun(Il2CppSystem.Action) override，以及下面无参的 SessionRun()/FixedUpdate()
    // override。

    /// <summary>三个步骤的 journal 标签；用常量是为了让 journal 从不为它们分配。</summary>
    internal const string BoundarySequenceStep1 = "SessionSequence step 1/3 FinishRaidSetup (pump)";

    /// <summary>倒计时步骤的 journal 标签——10:37 那次崩溃就死在这一步里。</summary>
    internal const string BoundarySequenceStep2 = "SessionSequence step 2/3 CountdownScreen (pump)";

    /// <summary>证明倒计时主体已返回的那一步的 journal 标签（立即写入）。</summary>
    internal const string BoundarySequenceStep2Done = "SessionSequence step 2/3 done CountdownScreen returned (pump)";

    /// <summary>原生协程步骤的 journal 标签。</summary>
    internal const string BoundarySequenceStep3 = "SessionSequence step 3/3 native session coroutine (pump)";

    public override void Spawn()
    {
        if (LocalPlayer.ActiveHealthController?.TryCast<ClientHealthController>() is { } coopClientHealthController)
        {
            coopClientHealthController.Start();
        }
        _onLocalPlayerDied ??= (System.Action<EDamageType>)HealthController_DiedEvent;
        _playerOwner.Player.HealthController.remove_DiedEvent(_onLocalPlayerDied);
        _playerOwner.Player.HealthController.add_DiedEvent(_onLocalPlayerDied);
        _playerOwner.Init();
#if DEBUG
        FikaGlobals.LogWarning("Forcing god mode on DEBUG build, use 'god f' console command to disable");
        _playerOwner.Player.ActiveHealthController.SetDamageCoeff(0f);
#endif
    }

    /// <summary>
    /// 接好 <see cref="OfflineHealthController"/> 事件以及所有 <see cref="ExfiltrationPoint"/>
    /// </summary>
    public override void SessionRun()
    {
        // 泵启动的原生 session 协程（SessionSequence 的第 3 步）在它的
        // InitSessionUI 步骤之后调用这里；泵通过监视该标志来证明原生序列确实跑过。
        _sessionRunReached = true;
        

        // il2cpp -> 托管：这里抛异常会向上展开进原生协程，而上面没有任何托管栈帧，
        // 也就是说它会变成运行时的问题而不是一行日志。它被捕获、只上报一次
        // 并被吞掉，这样原生协程仍能到达释放 PrepareSession 的那个 run 回调。
        FikaBoundaries.Run("CoopGame.SessionRun -> GameController.SetupEventsAndExfils",
            () => GameController.SetupEventsAndExfils(_playerOwner.Player));
        SessionStartTime = DateTimeExtensions.Now;
    }

    public override void FixedUpdate()
    {
        // 什么都不做，只是给 Update 驱动的泵一个场景内的第二次 tick：session 启动与
        // 倒计时路径不再是引擎协程，所以如果插件行为真的停止 tick，它们
        // 会静默卡住。FikaRoutinePump.Tick 有帧守卫——一帧中只有第一个 tick 会运行。
        // il2cpp 直接调用它，所以它自成一个边界，并像插件 Update 那样被保护。
        FikaBoundaries.Run(BoundaryFixedUpdate, static () => FikaRoutinePump.Instance.Tick());
    }

    /// <summary>原生 InitSessionUI callout 的 journal 标签。</summary>
    internal const string BoundaryInitSessionUI = "CoopGame.InitSessionUI (native coroutine -> managed)";

    /// <summary>原生 SessionRun callout 的 journal 标签。</summary>
    internal const string BoundarySessionRun = "CoopGame.SessionRun (native coroutine -> managed)";

    /// <summary>FixedUpdate callout 的 journal 标签。</summary>
    internal const string BoundaryFixedUpdate = "CoopGame.FixedUpdate (il2cpp -> managed)";

    /// <summary>
    /// 从服务器更新一个 <see cref="ExfiltrationPoint"/>
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
        if (_sessionRoutine != null)
        {
            FikaRoutinePump.Instance.Stop(_sessionRoutine, "the game is being disposed");
            _sessionRoutine = null;
        }

        // （analysis/transit-crash-unload）一次 raid 的游戏正在被释放：从这里起，那个 raid 的
        // 世界对本进程中的每个持有者都不再存在。在这里也一并丢弃跨 raid 持有者
        // （幂等——世界组件与 DestroyNetManager 调用的都是同一个方法），可以覆盖那些
        // 不走世界拆除就到达 Dispose 的路径，例如被取消的 raid 启动。
        Fika.Core.Main.Utils.WorldLifecycle.MarkWorldTornDown("CoopGame.Dispose");

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
    /// 当本地玩家成功撤离时，开启自由视角，并把撤离告诉其他玩家
    /// </summary>
    /// <param name="player">要在其上启动协程的本地玩家</param>
    /// <param name="exfiltrationPoint">用于撤离的撤离点</param>
    /// <param name="transitPoint">用于转图的转图点</param>
    /// <returns></returns>
    public void Extract(FikaPlayer player, ExfiltrationPoint exfiltrationPoint, TransitPoint transitPoint = null)
    {
        GameController.Extract(player, exfiltrationPoint, transitPoint);
        MainPlayerExtracted?.Invoke(player);
    }

    /// <summary>
    /// 主玩家死亡时触发
    /// </summary>
    /// <param name="damageType"></param>
    private void HealthController_DiedEvent(EDamageType damageType)
    {
        MainThreadDispatcher.RunAsync(HandleLocalPlayerDeath).Forget();
    }

    /// <summary>
    /// 确保原生玩家 owner 在 Fika 的部署/倒计时路径之后
    /// 已进入它的运行状态。SPT 正常的 LocalGame 序列会通过
    /// 基础 session runner 调用这里；Fika 的托管启动路径可能在
    /// 玩家有效、但 owner 未注册到输入树的情况下进入 raid，
    /// 这会让 ESC/TAB 失效，并使手部/UI 未绑定。
    /// </summary>
    internal void EnsurePlayerOwnerReady()
    {
        if (_playerOwner == null || _playerOwner.Player == null)
        {
            return;
        }

        // 使用为本次 session 创建的那个 owner。决定 Init 是否运行的是
        // 它的生命周期状态，而不是它是否在场景里。
        if (_playerOwner.State == EFT.PlayerOwner.EState.None)
        {
            _playerOwner.Init();
        }

        // Fika 从托管启动路径创建本地 owner。SPT 的
        // 原生 LocalGame 通常在 PlayerOwner.Run() 中注册 owner 并创建两个输入
        // 转换器；而托管路径可能在原生这一步被跳过后
        // 才进入 raid。显式且幂等地把这些
        // 组件恢复回来。
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
            // InputTree.Add 会让 owner 的优先级高于 UIInputRoot。当
            // 背包/暂停界面打开时，它的 Common 闸门会返回 BlockAll，
            // 阻止 UI 收到那个本该关闭它的按键。
            // 让游戏层保持在 UI 之下，包括 Run 已经注册过的
            // owner，并让原生树把每个命令只派发一次。
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

        // 这一步要在 ShowBattleUIScreen 之后做：界面切换拥有 Common
        // 输入闸门，可能覆盖掉更早的一次重置。这只是启动期的
        // 交接；之后所有的菜单切换都由原生输入处理。
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
            // RegisterGameOwner 做的事不止是赋值 MainPlayer：原生
            // ClientLocalGameWorld 的 override 还会绑定本地 owner/输入
            // 钩子。MainPlayer 可能已经被 PlayerFactory 填好了，所以
            // 不要把它当作这次注册是否运行过的代理指标。
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
        if (FikaUiPanels.CanDrive(GameUi.TimerPanel, "CoopGame.HandleLocalPlayerDeath.TimerPanel.Close"))
        {
            GameUi.TimerPanel.Close();
        }

        // Spawn 已在上面被替换；我们只注册了自己的死亡回调。
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
    /// 停止本地的 <see cref="CoopGame"/>
    /// </summary>
    public override void Stop(string profileId, ExitStatus exitStatus, string exitName, float delay = 0f)
    {
        if (FikaFinalMissionDirector.DeferStop) return;
        if (Status == GameStatus.Stopping || Status == GameStatus.Stopped) return;
        var localPlayer = Singleton<GameWorld>.Instance?.MainPlayer?.TryCast<FikaPlayer>();
        // 一次客机背包重新同步可能会替换掉控制器的背包，
        // 却不更新 SPT 的 raid 结束流程所序列化的 profile。
        // 在拍取结果界面描述符快照之前，把 Scav 转移界面与自然的 raid 存档
        // 保持在同一个背包树上。
        if (localPlayer?.InventoryController?.TryCast<ClientInventoryController>() is { } clientInventoryController)
            clientInventoryController.SyncInventoryToProfile();

        // 直接捕获，这样原生的注入 vtable 调用与托管调用
        // 都会在任何 raid 结束处理器改动 profile 之前运行这一步。
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
            // 先建副本，避免字典正在被修改时出错（使用刷怪 mod 时会发生这种情况）
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
        if (FikaUiPanels.CanDrive(gameUI.TimerPanel, "CoopGame.Stop.TimerPanel.Close"))
        {
            gameUI.TimerPanel.Close();
        }
        if (EnvironmentManager.Instance != null)
        {
            EnvironmentManager.Instance.Stop();
        }
        MonoBehaviourSingleton<PreloaderUI>.Instance.StartBlackScreenShow(1f, 1f, (System.Action)stopManager.ExitOverride);
        AppEnvironment.Config.UseSpiritPlayer = false;
    }

    /// <summary>
    /// 把你自己的 <see cref="FikaPlayer"/> 保存到服务器
    /// </summary>
    private async Task SavePlayer(FikaPlayer player, ExitStatus exitStatus, string exitName, bool fromDeath)
    {
        // SPT 5 的原生 GameEnd 在保存前重置它，并在
        // LocalRaidEnded 完成后把它置位。提前保存会绕过那个方法，但
        // SessionResultShowOperation 仍会等它的完成标志。
        TarkovApplication.Exist(out var application);
        application?.ResetGameOverSaveStatus();

        if (fromDeath)
        {
            //因为退出时我们绕过了保存，就在这里现在跑一次。
            player.Profile.EftStats.LastPlayerState = null;
            player.StatisticsManager.EndStatisticsSession(exitStatus, PastTime);
            player.CheckAndResetControllers(exitStatus, PastTime, Location.Id, exitName);
        }

        var playTimeDuration = DateTimeExtensions.Now - SessionStartTime;

        if (player.InventoryController?.TryCast<ClientInventoryController>() is { } clientInventoryController)
        {
            clientInventoryController.SyncInventoryToProfile();
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
        }

        var sentItems = GetOwnSentItems(player.ProfileId).ToDictionary(pair => pair.Key,
            pair => new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<FlatItem>(pair.Value)).ToNativeDictionary();
        await _backEnd.LocalRaidEnded(_raidSettings, parameters, GetLostInsuredItems(), sentItems).AsManaged();
        if (application != null) application.GameOverSaveStatusReceived = true;
    }

    /// <summary>
    /// 从玩家自己的仓库与转图仓库中取出与指定 profile 关联的已发送物品容器
    /// （如果存在）。
    /// </summary>
    /// <param name="profileId">要从中取物品的 profile id。</param>
    /// <returns>把仓库名映射到每个匹配的已发送物品容器的扁平物品数据数组的字典。
    /// 没有匹配的容器时该字典为空。</returns>
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
    /// 在等待其他玩家时停止本地的 <see cref="CoopGame"/>
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
        if (FikaUiPanels.CanDrive(gameUI.TimerPanel, "CoopGame.StopFromCancel.TimerPanel.Close"))
        {
            gameUI.TimerPanel.Close();
        }

        if (EnvironmentManager.Instance != null)
        {
            EnvironmentManager.Instance.Stop();
        }
        MonoBehaviourSingleton<PreloaderUI>.Instance.StartBlackScreenShow(1f, 1f, (System.Action)stopManager.ExitOverride);
        AppEnvironment.Config.UseSpiritPlayer = false;
    }

    /// <summary>
    /// 切换 <see cref="DebugUI"/> 菜单
    /// </summary>
    public void ToggleDebug(bool enabled)
    {
        GameController.ToggleDebug(enabled);
    }

    /// <summary>
    /// 告诉服务器我们已经离开 raid
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
    /// <see cref="CoopGame"/> 停止之后做清理
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
        // AP（analysis/nonfika-raid-inertness）：我们的 raid 到这里就结束了，所以在下一次 raid
        // （无论由谁启动）被准备之前，raid 类型闸门先关上。
        Fika.Core.Main.Utils.FikaRaidGate.Disarm("CoopGame.CleanUp");
        FikaBackendUtils.CleanUpVariables();
        BTRSide_Patches.Passengers.Clear();
        // 池化的主机端操作不得存活到下一场 raid 里在那里超时。
        global::Fika.Core.Main.HostClasses.HostOperationWatchdog.Reset();
        // （analysis/transit-crash-unload）raid 的最后一站：这个世界的任何东西都不许再可达。
        Fika.Core.Main.Utils.WorldLifecycle.MarkWorldTornDown("CoopGame.CleanUp");
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

            if (FikaUiPanels.CanDrive(baseLocalGame_0.GameUi.TimerPanel, "CoopGame.ExitAsync.TimerPanel.Close"))
            {
                baseLocalGame_0.GameUi.TimerPanel.Close();
            }

            if (baseLocalGame_0._playerOwner != null)
            {
                baseLocalGame_0._playerOwner.Stop();
            }

            EftScreenManager.Instance.CloseAllScreensForced();

            //如果我们还没保存，就运行原方法并在此停止。
            if (!baseLocalGame_0._deathSave.HasSaved)
            {
                baseLocalGame_0._playerOwner.Player.TriggerZones.Clear();
                baseLocalGame_0._playerOwner.Player.TriggerZones.AddRange(baseLocalGame_0.GameController.LocalTriggerZones.ToNativeList());
                await baseLocalGame_0.GameEnd(profileId, exitStatus, exitName, delay).AsManaged();
                return;
            }

            //这里大部分来自 method_14，除去保存玩家的那部分。
            await baseLocalGame_0._playerOwner.Player.OnGameSessionEnd(exitStatus, baseLocalGame_0.PastTime, baseLocalGame_0.Location.Id, exitName).AsManaged();
            baseLocalGame_0.CleanUp();
            MonoBehaviourSingleton<BetterAudio>.Instance?.FadeOutVolumeAfterRaid();

            var duration = DateTimeExtensions.Now - baseLocalGame_0.SessionStartTime;
            StaticManager.Instance.WaitSeconds(delay, (System.Action)(System.Action)(() =>
            {
                baseLocalGame_0.Status = GameStatus.Stopped;
                baseLocalGame_0._callback.Invoke(new Result<ExitStatus, Il2CppSystem.TimeSpan, ClientMetrics>(exitStatus, duration, baseLocalGame_0.CollectMetrics()));
            }));
        }
    }

    /// <summary>
    /// 用于在取消时优雅地管理 <see cref="CoopGame"/> 的停止
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

