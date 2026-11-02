using Fika.Core.Main.BaseClasses;
using CommonAssets.Scripts.ArtilleryShelling.Client;
using CommonAssets.Scripts.Game;
using EFT.Interactive.SecretExfiltrations;
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

        // （analysis/session-log-archive）游戏模式类写出它们的判断行（主机控制器的
        // [ZoneDiag]、[BossDiag]）时用的就是这个源。把它提供给镜像会让这些行也进入
        // Fika-diagnostics.log，而那是下一次启动后仍然存在的文件；
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

    // 天气
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

    // 突袭数据
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

    // 生成点
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
    // SPT 5 的 SubscribeOnEvent 返回一个取消注册回调的
    // Action。把退订句柄与类型化回调本身分开保存。
    protected Il2CppSystem.Action _btrServicePurchase;
    protected CoopHandler _coopHandler;
    protected FikaPlayer _localPlayer;
    protected EUpdateQueue _updateQueue;
    protected GameWorld _gameWorld;
    protected IEftSession _backendSession;
    protected Coroutine _extractRoutine;
    // VOIP 修复过去是一个引擎协程。它从倒计时例程内部启动，也就是从另一个协程的 MoveNext
    // 内部启动——正是那种出错的形状（analysis/host-raid-coroutine）
    // ——所以现在它跑在由 Update 驱动的泵上，并通过泵句柄停止。
    private FikaRoutinePump.RoutineHandle _voipAudioFixRoutine;
    // 突袭内 HUD 恢复（analysis/inraid-ui）：在倒计时主体被跳过之后把
    // HUD 层级重新打开的例程的泵句柄。保留它是为了让第二次调用无法叠加出第二份。
    private FikaRoutinePump.RoutineHandle _inRaidHudRestoreRoutine;
    // 开场倒计时（analysis/countdown-restore）：后处理重申例程（第
    // 4/4 步）的泵句柄，也就是由倒计时恢复路径启动的第 5/8 条语句的主体。
    // 保留它是为了让第二次调用无法叠加出第二份，也让 CleanUp 能停掉它。
    private FikaRoutinePump.RoutineHandle _countdownReassertRoutine;
    private readonly List<GameObject> _mutedVoiceSources = [];

    public void SetLocalPlayer(FikaPlayer player)
    {
        _localPlayer = player;
        _coopHandler.MyPlayer = player;
    }

    /// <summary>
    /// <see cref="Task"/> 过去用于等待主机启动突袭
    /// </summary>
    /// <returns></returns>
    public virtual Task WaitForHostToStart()
    {
        Logger.LogInfo("Starting task to wait for host to start the raid.");
        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_WAIT_FOR_HOST_START_RAID.Localized());

        return Task.CompletedTask;
    }

    /// <summary>
    /// 这会创建一个 "custom" 返回按钮，以便在卡住时能退出来
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
        WaitSeconds waitForSeconds = new(0.5f);
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
    /// 这个 task 确保所有玩家都已加入并加载完成，然后再继续
    /// </summary>
    public abstract Task WaitForOtherPlayersToLoad();

    /// <summary>
    /// 对突袭设置做最后几处修改
    /// </summary>
    public virtual IEnumerator FinishRaidSetup()
    {
        // 第 3 轮（analysis/countdown-bisect-round3）：13:45
        // 那次运行在这一步里把 Time.frameCount 冻结在 17849 达 3.1 秒——泵写下了它的
        // tick 标签，然后主线程在那一帧之后什么也没写——而且没有任何标签指明是哪条语句。
        // 因此这个例程里每条可能真正耗时的语句，都在运行之前写下自己的标签，并在运行之后写下
        // RETURNED 标签。仅用于诊断：没有开关，不改变行为，每条语句每次突袭写两次。

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
        // season 语句自带逐帧等待，所以 RETURNED 标签跟在循环之后：
        // 一条停在上一个标签上的轨迹，说明该例程当时正在 season 的工作内部。
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

        var timeBeforeDeployLocal = Singleton<GlobalConfiguration>.Instance.TimeBeforeDeployLocal;
#if DEBUG
        timeBeforeDeployLocal = 3;
#endif
        yield return WaitForHostInit(timeBeforeDeployLocal);

        NetManagerUtils.DisableLoadingScreenUI();

        // ---------------------------------------------------------------------------------------
        // 二分测试面（第 1 轮：analysis/countdown-bisect，
        // 第 2 轮：analysis/countdown-bisect-round2）。
        // 第 1 轮把三条装饰性语句放到一个开关之后并关闭发货；11:17
        // 那次运行在这三条全关的情况下仍死在这个主体里，而轨迹没有指明语句，因为它的持久化器 14
        // 秒前就停了。因此第 2 轮连*保留*的语句也一并二分：它们每一条都有自己的标签和自己的
        // 开关（全部以开启状态发货），标签立即写入崩溃轨迹——本线程上一次 WriteFile，
        // 经标签句柄——而总开关 'Skip Countdown Body'（以开启状态发货）
        // 会跳过它们全部并直接跳到部署等待，那才是无论哪条语句是元凶都能开始的突袭。
        // ---------------------------------------------------------------------------------------

        var dateTime = DateTimeExtensions.Now.AddSeconds(timeBeforeDeployLocal);
        var bisect = CountdownBisect.Read();
        // 开场倒计时（analysis/countdown-restore）
        // 是一次独立的读取：它决定下面是否把玩家看到的倒计时放回去，而总开关仍然在跳过主体。
        // 它单独写在自己的一行上，这样下一次崩溃就可以对照当时真正生效的开关来读。
        var restore = CountdownRestore.Read();
        // 当恢复路径确实把叠层放上去时，由下面的总开关分支置位：
        // 只有那时，部署等待之后的收尾才有东西可以撤下。
        var countdownOverlayShown = false;
        
        

        // 第 389 行——装饰性，开关 'Show Final Countdown Screen'（默认关闭）。
        // interop 确实暴露了这次调用所需的东西：控制器的构造函数 (EFT.Profile,
        // Il2CppSystem.DateTime)，以及其泛型基类 ScreenController
        // 上的 ShowScreen(EFT.UI.Screens.EScreenState)，
        // 其中 EScreenState.Root = 2。因此这次调用形式上是完整的，
        // 并完全按上游所写保留——它默认关闭只是因为它是该主体里的第一个托管调用点，
        // 而它的调用目标是一个双重嵌套的泛型 interop 类型，正是那种会让 JIT/调用点决策出错的形状。
        if (!bisect.SkipBody && bisect.Screen)
        {
            new MatchmakerFinalCountdown.FinalCountdownScreenController(profile, dateTime).ShowScreen(EScreenState.Root);
        }

        // 第 390-394 行：突袭开始的音频淡入。尽管二分测试曾把它称为装饰性，
        // 它并不是。在总开关跳过这个主体期间，BetterAudio 从未淡回来，
        // 于是除玩家自身之外的一切声音都停留在菜单的已淡出音量（2026-10-10 报告：
        // "除了玩家，一切都是安静的"）。它现在无条件运行；两个二分开关都不再控制它。
        if (MonoBehaviourSingleton<AmbientAudioSystem>.Instantiated)
        {
            MonoBehaviourSingleton<AmbientAudioSystem>.Instance.Initialize();
        }

        MonoBehaviourSingleton<BetterAudio>.Instance.FadeInVolumeBeforeRaid(timeBeforeDeployLocal);

        // 第 395-396 行——装饰性，开关 'Raid Start Menu Music'（默认关闭）。
        if (!bisect.SkipBody && bisect.MenuMusic)
        {
            Singleton<GUISounds>.Instance.BackgroundMusicActive = false;
            Singleton<GUISounds>.Instance.StopMenuBackgroundMusicWithDelay(timeBeforeDeployLocal);
        }

        // 总开关：这个主体里不碰任何原生对象。如果轨迹的最后一个标签是下面的
        // SKIP 行，那么元凶根本不在这个主体里——接下
        // 来的候选是部署等待，以及它挂起期间运行的其它东西。
        if (bisect.SkipBody)
        {
            

            // ---------------------------------------------------------------------------------------
            // 开场倒计时（analysis/countdown-restore）。
            //
            // 总开关正是移除玩家所见倒计时的原因：它开启时，
            // 这个例程直接跳到部署等待，于是等待发生时没有任何倒计时画面，
            // 玩家从一个从未倒数过任何东西的界面里部署出去。这个块把倒计时放回去，
            // 而*不*关闭总开关：它运行第 389 条语句（原版
            // FinalCountdownScreenController 叠层），
            // 下面的部署等待就是那个叠层的时钟，等待之后的收尾再把它关闭并恢复它留下的东西。
            //
            // 为什么它可以运行：这个例程已不再是嫌疑对象。最近几次突袭的轨迹显示了整个流程——进入主体、
            // 总开关、等待、主体完成——而让总开关变得必要的那个 fail fast，
            // 是在*本例行停在等待中*时由一个从原生到托管的回调进入 Fika
            // 自己的已观察玩家覆写而抛出的（analysis/countdown-bisect-round3
            // 第 1.2 节），不是由这个主体的某条语句抛出的。这个块运行的一切都遵
            // 守移植版崩溃所带来的三条规则：不对引擎的 WaitForSeconds
            // 做托管读取（等待用的是托管的 WaitSeconds），不从托管代码启动引擎协程，
            // 不对注入方法做引擎反射——这条路径的两个调用点都是托管 → 原生，
            // 作用在*原生*对象上（倒计时画面控制器、EFT 画面管理器）。
            //
            // 主体运行时这里什么都不跑：总开关关闭时，
            // 上面的语句在各自开关下自行显示倒计时，而这条路径不碍事。
            // ---------------------------------------------------------------------------------------
            countdownOverlayShown = CountdownRestore.ShowIfEnabled(restore, profile, dateTime);
        }
        else
        {
            // 保留语句 1/8：突袭需要游戏 UI（原生层级激活）。
            if (bisect.GameUi)
            {
                _abstractGame.GameUi.gameObject.SetActive(true);
            }
            else
            {
            }

            // 保留语句 2/8：撤离计时的档案绑定——一次对原生 MonoBehaviour setter
            // 的托管调用，也就是移植版在 analysis/ui-log-noise 里不得不保护的那个面板。
            if (bisect.TimerPanel)
            {
                _abstractGame.GameUi.TimerPanel.ProfileId = profileId;
            }
            else
            {
            }
        }

        // 保留语句 3/8：部署等待。正是它的完成写出了 'step 2/3
        // done'。它在总开关开启和关闭时都运行：跳过路径保留部署时序，
        // 因此会话序列、原生会话协程以及下游的一切都保持同样的进度安排。
        if (bisect.DeployWait)
        {
            // 第 3 轮：这个等待两端都被括起来。13:45 的轨迹停在上面的那行，
            // 其中没有任何东西说明进程是在本例行停在等待中时死的，还是在等待之后的第一条语句里死的——这
            // 两个标签让这一点变得可读，而泵也围绕同一个等待写下自己的 'pump wait: ...'
            // 对（FikaRoutinePump.Routine.TraceWaitEntered/Returned）。
            yield return new WaitSeconds(timeBeforeDeployLocal);
        }


        // ---------------------------------------------------------------------------------------------
        // 倒计时的收尾（analysis/countdown-restore），第 2/4-4/4 步。
        //
        // 只有当上面的恢复路径确实把叠层放上去时它才运行：
        // 总开关关闭的突袭改为运行上游语句，而叠层开关关闭的突袭
        // 没有东西可以撤下——轨迹会写出是这两种情况中的哪一种，
        // 而不是留下一个沉默的空洞。每一步都有自己的标签和自己的开关，
        // 所以停在这里的轨迹就指明了是哪一步死的，而那一步的开关就是把它再去掉的那一行。
        // ---------------------------------------------------------------------------------------------
        if (countdownOverlayShown)
        {

            // 第 2/4 步——再次把叠层撤下。它有单独的开关："叠层关不掉"
            // 和 "突袭视图没有恢复" 是两种不同的故障，绝不能共用同一个标签。
            CountdownRestore.CloseIfEnabled(restore);

            // 第 3/4 步——倒计时留下的那个突袭视图：就是原版尾部运行的同一条第
            // 4/8 条语句，其所有者已重新就绪，这里复用它而不是重新推导。
            // 它故意在关闭之后运行：关闭画面正是恢复随后必须善后的那件事。
            if (restore.ViewRestore)
            {
                if (FikaBoundaries.Run(CountdownRestore.BoundaryViewRestore, () => RestoreRaidViewAfterCountdown()))
                {
                }
            }
            else
            {
            }

            // 第 4/4 步——原生画面在关闭后的一帧仍可能施加它的后处理（第 5/8
            // 条语句存在的原因）。它启动在 Update 泵上：从本例行内部把一个托管枚举器交给引擎，
            // 正是那种出错的形状（analysis/host-raid-coroutine）。
            if (restore.Reassert)
            {
                if (FikaBoundaries.Run(CountdownRestore.BoundaryReassert, () =>
                {
                    _countdownReassertRoutine = FikaRoutinePump.Instance.Start(RestoreRaidViewAfterCountdownRoutine(),
                        "RestoreRaidViewAfterCountdown (countdown restore)");
                }))
                {
                }
            }
            else
            {
            }

        }
        else
        {
        }

        // 第 3 轮闸门：等待之后的块（第 4/8-8/8 条语句）现在有了自己的总开关，
        // 因此发货默认值在进入会话序列时把整条倒计时尾巴都短路掉了。当 'Skip
        // Post Wait Block' 开启（发货默认值）或 'Skip
        // Countdown Body' 开启时它被跳过；轨迹会写出是哪一个做的，
        // 这样一次成功开始的突袭绝不会被误认为是一次尾巴语句运行过的突袭。
        var runPostWait = !bisect.SkipBody && !bisect.SkipPostWait;
        if (!runPostWait)
        {
        }
        else
        {
        }

        if (runPostWait)
        {
            // 保留语句 4/8：当 Fika 绕过原生的 LocalGame
            // 启动协程时，SPT 5 可能让最终倒计时的后处理/画面状态保持
            // 激活——一次实时突袭，背景永久模糊。在倒计时结束后恢复突袭视图，
            // 但不触碰之后突袭中的伤害/死亡模糊之类的玩法效果。
            if (bisect.RestoreRaidView)
            {
                RestoreRaidViewAfterCountdown();
            }
            else
            {
            }

            // 保留语句 5/8：原生倒计时画面在关闭后的一帧仍可能施加它的摄像机后处理。
            // 重新主张清除状态若干帧，使第一人称视图无法永久保持模糊。由
            // Update 泵驱动而非引擎：从本例行内部把一个托管枚举器交给引擎，
            // 正是主机突袭开始期间出错的那种形状（
            // analysis/host-raid-coroutine）。
            if (bisect.RestoreRoutine)
            {
                FikaBoundaries.Run(CountdownBisect.BoundaryRestoreRoutine, () =>
                {
                    FikaRoutinePump.Instance.Start(RestoreRaidViewAfterCountdownRoutine(), "RestoreRaidViewAfterCountdown");
                });
            }
            else
            {
            }

            // 保留语句 6/8：转场控制器。
            if (bisect.SyncTransits)
            {
                SyncTransitControllers(profileId);
            }
            else
            {
            }

            // 保留语句 7/8：突袭已开始事件。放在它自己的边界内：
            // 抛异常的订阅者绝不能展开回泵里（它会被报告为一次 [Boundary] 失败）。
            if (bisect.RaidStartedEvent)
            {
                FikaBoundaries.Run(CountdownBisect.BoundaryRaidStartedEvent, () =>
                {
                    FikaEventDispatcher.DispatchEvent(new FikaRaidStartedEvent(FikaBackendUtils.IsServer));
                });
            }
            else
            {
            }

            // 保留语句 8/8：VOIP 设备修复（Dissonance 原生代码），在泵上启动。
            if (bisect.VoipFix)
            {
                FikaBoundaries.Run(CountdownBisect.BoundaryVoipFix, () =>
                {
                    if (FikaSingleton<IFikaNetworkManager>.Instance.AllowVOIP && !FikaBackendUtils.IsHeadless)
                    {
                        _voipAudioFixRoutine = FikaRoutinePump.Instance.Start(FixVOIPAudioDevice(), "FixVOIPAudioDevice");
                    }
                });
            }
            else
            {
            }

        }

        // ---------------------------------------------------------------------------------------------
        // 突袭内 HUD 恢复（analysis/inraid-ui）。
        //
        // 第 1/8 和 2/8 条语句正是把突袭 HUD 层级打开的那两条语句。
        // 总开关开启时它们不运行，于是 HUD 在突袭里根本不会出现：GameUI 根保持非激活，
        // 撤离计时面板（EFT.UI.ExtractionTimersPanel，其 GameObject
        // 为 'TimerPanel'）和战斗 UI 画面（EFT.UI.BattleUIScreen，
        // 由 CoopGame.EnsurePlayerOwnerReady
        // 创建）也随之保持非激活。症状正是 2026-10-10 报告的那个：
        // 计时键什么也不做——没有撤离点列表、没有剩余时间、没有撤离倒计时。
        //
        // 这个块在不重新启用任何一条被跳过语句的情况下恢复 HUD：
        //   * 它只在主体确实被跳过时运行（bisect.SkipBody），所以总开关关闭的
        //     突袭完全保持上游行为；
        //   * 它作为泵例程运行，即它的第一步在一帧之后执行，在会话序列之后，
        //     用的是倒计时本身所用的同一个 Update tick；
        //   * 它里面的每个引擎调用都在各自的 FikaBoundaries.Run/try-catch 内，所以
        //     失败的步骤会在日志里被指明，突袭继续；
        //   * 它复用被跳过语句所做的那两次调用，此外别无其它——它不运行装饰性
        //     语句、等待之后的语句或 VOIP 修复。
        //
        // 它在简单重跑 1/8 和 2/8 之上新增的唯一行为是 SetTime 重推：面板的撤离列表在生成时填充，
        // 而面板关闭期间那次驱动会被跳过（HostGameController.cs:991，
        // 21:35 那次突袭里有 4 行 [PanelGuard]），
        // 所以只激活层级会显示一个空面板。SetTime 与生成路径所做的是同一次调用。
        // ---------------------------------------------------------------------------------------------
        if (bisect.SkipBody && ReadRestoreHudFlag())
        {
            try
            {
                _inRaidHudRestoreRoutine = FikaRoutinePump.Instance.Start(RestoreInRaidHudRoutine(),
                    "RestoreInRaidHud");
                
            }
            catch (Exception exception)
            {
                FikaBoundaries.Fail(CountdownBisect.BoundaryHudRestore, exception);
            }
        }
        else if (!bisect.SkipBody)
        {
            
        }
        else
        {
            
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

                // 在 SPT 5 中，倒计时使用的是 CameraManager
                // 的 InventoryBlur 组件。调用
                // Blur(false) 会启动一次原生过渡，
                // 而在 Fika 绕过 LocalGame
                // 正常启动协程的情况下这并不可靠。还要直接禁用该组件并清除它的值。
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
                    // CoopGame 在构造本地玩家时会禁用
                    // CameraManager。原版 LocalGame
                    // 路径会在最终倒计时之前把它重新打开，但
                    // Fika 是经由另一条路径抵达倒计时的。
                    // 在这里把该管理器及其摄像机一并重新启用，
                    // 好让 Unity 真正提交世界渲染。

                    // 原生倒计时可能把玩家摄像机留在临时的
                    // LockCameraState 里。
                    // 此时 Fika 已经生成了本地玩家，
                    // 所以在下一次渲染之前显式恢复正常的第一人称状态。
                    var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
                    if (mainPlayer != null)
                    {
                        mainPlayer.PointOfView = EPointOfView.FirstPerson;
                        if (mainPlayer.PlayerBody != null)
                        {
                            mainPlayer.PlayerBody.PointOfView.Value = EPointOfView.FirstPerson;
                        }
                    }

                    // 当 Fika 的倒计时取代原版
                    // LocalGame 启动协程时，SPT 5
                    // 可能让原生死亡/淡出组件停留在启用状态。
                    // 把它连同 FastBlur 一起清除，
                    // 以便恢复第一人称渲染，同时不触碰之后突袭中的伤害效果。
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

                    // 清除由最终倒计时填充的效果累加器（灰度、
                    // 去饱和、径向模糊等），
                    // 同时不永久禁用玩法效果。
                    // 突袭中真正开始一个生命/装备效果时，
                    // 原生控制器会重新启用这些组件。
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

                    // [NvgDiag] 夜视与热成像是由摄像机效果本身渲染的：
                    // BSG.CameraEffects.NightVision
                    // / ThermalVision 在 OnPreCull
                    // 里 blit 它们的材质并清除它，在 OnDisable
                    // 里清除它们的命令缓冲区和 _NightVisionOn
                    // 着色器全局量。按下设备键只会对已经在运行的组件调用
                    // Switch(bool)，而效果内部没有任何东西会把它重新启用。
                    // 因此在这里禁用它们就杀掉了余下整个突袭的夜视：
                    // 按键和头戴状态仍有反应，画面却再也没变。
                    // 请把它们强制置入干净的 "off" 状态，而不是把组件关掉。
                    ResetVisionEffect(cameraManager.NightVision, "NightVision");
                    ResetVisionEffect(cameraManager.ThermalVision, "ThermalVision");

                    var zBlur = cameraManager.NightVisionZBlur;
                    if (zBlur != null)
                    {
                        if (zBlur.IsApplied)
                        {
                            zBlur.Apply(false);
                        }

                        if (!zBlur.enabled)
                        {
                            // 夜视效果通过它的 Construct(NightVisionZBlur)
                            // 字段驱动这个模糊；一旦设备被打开，
                            // 被禁用的组件会静默地把它丢掉。
                            zBlur.enabled = true;
                            Logger.LogWarning($"[NvgRepair] NightVisionZBlur was disabled at raid start; re-enabled it");
                        }
                    }

                    // 菜单/倒计时摄像机被拆除之后，少数
                    // SPT 5 后处理组件仍可能保持启用。
                    // 在 IL2CPP 的 Fika 突袭里，
                    // 它们的原生材质并不总是已初始化，于是即便 FPS
                    // 摄像机本身是激活的，该组件仍会写入一整帧纯黑。
                    // 这些是菜单/倒计时的呈现效果。
                    // 夜视与热成像效果已经不在这个列表里：它们是玩法效果，
                    // EFT 只会对它自己创建的那个组件调用
                    // Switch(bool) 来重新施加，
                    // 所以把它们关掉就等于整个突袭都禁用了它们——见
                    // analysis/night-vision/REPORT.md。
                    foreach (var effect in camera.GetComponents<MonoBehaviour>())
                    {
                        if (effect == null)
                        {
                            continue;
                        }

                        var effectName = effect.GetType().Name;
                        // 视觉效果（NightVision / ThermalVision
                        // / NightVisionZBlur）被刻意排除在外：
                        // 它们必须在整个突袭期间保持启用，见下面的 ResetVisionEffect。
                        if (effectName is "CC_Vintage" or "DesaturateEffect" or
                            "PostprocessGrayscale" or "DistortCameraFX")
                        {
                            effect.enabled = false;
                        }
                    }

                    // 对某些 IL2CPP 代理组件来说
                    // GetComponents<MonoBehaviour>()
                    // 并不完整。作为兜底，按 Unity 组件名解析同样的效果。
                    foreach (var effectName in new[]
                    {
                        "CC_Vintage", "DesaturateEffect",
                        "PostprocessGrayscale", "DistortCameraFX"
                    })
                    {
                        var component = camera.GetComponent(effectName);
                        if (component is Behaviour behaviour)
                        {
                            behaviour.enabled = false;
                        }
                    }
                }

                // 清除原生倒计时预制体留在摄
                // 像机上的任何重复后处理组件。
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

            // 原生最终倒计时关闭之后，SPT 5 的菜单场景仍可能存活。
            // 它的 MainMenuCamera 深度远高于
            // FPS 摄像机，会在突袭摄像机渲染之后清空帧，
            // 导致完全黑屏的突袭视图。本地玩家生成之后
            // Fika 不再需要任何菜单摄像机，
            // 所以在这里禁用每一个残留实例（并且在每一个重试帧上也这么做）。
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

    /// <summary>
    /// 把一个原生视觉摄像机效果（<c>BSG.CameraEffects.NightVision</c>
    /// 或 <c>ThermalVision</c>）置入干净的
    /// "off" 状态，<b>而不</b>禁用该组件。
    /// <para>
    /// 两个效果都在 <c>OnPreCull</c> 中渲染，而 Unity
    /// 只在行为启用时才调用它。它们的 <c>OnDisable</c>
    /// 会清除夜视材质与 <c>_NightVisionOn</c> 着色器全局量，
    /// 而两个效果都不会再把自己重新启用——设备键只对运行中的组件调用
    /// <c>Switch(bool)</c>。因此在这里把组件关掉（就像
    /// 倒计时清理以前所做的那样）正是让夜视在余下整个突袭中不可用的原因。
    /// 一个已启用且 <c>On == false</c> 的效果什么也不绘制：
    /// 它的 OnPreCull 在把 0 写入着色器全局量之后便返回，
    /// 所以这次清理当初针对的残留菜单黑帧无法再通过这条路径回来。
    /// </para>
    /// </summary>
    private void ResetVisionEffect(Behaviour effect, string label)
    {
        if (effect == null)
        {
            return;
        }

        try
        {
            if (!effect.enabled)
            {
                effect.enabled = true;
                Logger.LogWarning($"[NvgRepair] {label} was disabled at raid start; re-enabled it " +
                    "so the headwear device key can drive it");
            }

            // 摄像机效果位于 BSG.CameraEffects（夜视）
            // 和全局命名空间（热成像）中；否则本文件的
            // EFT.InventoryLogic 导入会把这些名字解析成物品类。
            if (effect.TryCast<BSG.CameraEffects.NightVision>() is { } nightVision)
            {
                if (nightVision.InProcessSwitching)
                {
                    nightVision.FastForwardSwitch();
                }

                // （2026-10-17）戴着该设备且已把它打开的玩家会保持开启：
                // 在这里强行关闭效果会让头戴设备与画面在余下整个突袭中互相矛盾。
                if (IsDeviceSwitchedOn(nightVision: true))
                {
                    return;
                }

                if (nightVision.On)
                {
                    nightVision.Switch(false);
                }

                return;
            }

            if (effect.TryCast<global::ThermalVision>() is { } thermal)
            {
                if (thermal.InProcessSwitching)
                {
                    thermal.FastForwardSwitch();
                }

                if (IsDeviceSwitchedOn(nightVision: false))
                {
                    return;
                }

                if (thermal.On)
                {
                    thermal.Switch(false);
                }
            }
        }
        catch (Exception exception)
        {
            Logger.LogError($"[NvgRepair] resetting {label} failed: {exception.Message}");
        }
    }

    /// <summary>
    /// 本地玩家的夜视（或热成像）头戴设备是否已打开。
    /// 通过玩家自身的观察者读取，也就是设备键写入的同一个来源。
    /// 任何失败都回答 false，即这次清理保持这道守卫出现之前的行为。
    /// </summary>
    private static bool IsDeviceSwitchedOn(bool nightVision)
    {
        try
        {
            var player = GamePlayerOwner.MyPlayer;
            if (player == null)
            {
                return false;
            }

            if (nightVision)
            {
                var component = player.NightVisionObserver?.Component;
                return component != null && component.Togglable.On;
            }

            var thermal = player.ThermalVisionObserver?.Component;
            return thermal != null && thermal.Togglable.On;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private IEnumerator RestoreRaidViewAfterCountdownRoutine()
    {
        // 在原生画面的 close/LateUpdate
        // 回调之后也要运行。SPT 5 可能在倒计时画面关闭
        // 之后一帧或多帧才完成玩家所有者的设置。在一个短的、
        // 有界的窗口内持续重申请本地所有者、第一人称视觉与摄像机，
        // 使迟到的原生回调无法把玩家留在未绑定状态。
        for (var i = 0; i < 120; i++)
        {
            yield return null;
            // 一旦某个菜单打开，或者玩家离开了第一人称玩法，
            // 它的输入与摄像机状态就归原生 UI 所有。不要关闭它、
            // 不要重新显示 BattleUI、也不要撤销它的模糊/光标状态。
            if (EftScreenManager.Instance?.CheckCurrentScreen(EEftScreenType.BattleUI) != true ||
                _localPlayer?.HealthController?.IsAlive != true ||
                (FikaSingleton<FreeCameraController>.Instantiated &&
                 FikaSingleton<FreeCameraController>.Instance.IsScriptActive))
                yield break;
            RestoreRaidViewAfterCountdown(initializeOwner: false);
        }
    }

    /// <summary>
    /// 'Countdown: Restore In-Raid HUD' 开关（配置项，
    /// 默认开启）。缺少条目即视为开启：突袭 HUD 是玩家看到的东西，而该元素自身会报告并继续。
    /// </summary>
    private static bool ReadRestoreHudFlag()
    {
        try
        {
            var settings = FikaPlugin.Instance?.Settings;
            if (settings?.CountdownRestoreHud is null)
            {
                return true;
            }

            return settings.CountdownRestoreHud.Value;
        }
        catch (Exception exception)
        {
            
            return true;
        }
    }

    /// <summary>
    /// 在总开关跳过了倒计时主体之后恢复突袭内 HUD（analysis/inraid-ui）。
    ///
    /// 把 HUD 层级打开的那两条语句——1/8 <c>GameUi.gameObject.SetActive(true)</c>
    /// 与 2/8 <c>GameUi.TimerPanel.ProfileId =
    /// profileId</c>——在 'Skip Countdown Body' 开启时被跳过，
    /// 于是整个层级（GameUI 根、<c>EFT.UI.ExtractionTimersPanel</c>，
    /// 以及由 <see cref="CoopGame.EnsurePlayerOwnerReady"/> 创建的
    /// <c>EFT.UI.BattleUIScreen</c>）在整个突袭期间都保持非激活：计时键什么也不显示，且任何引擎协程都不许在这些面板上运行。
    ///
    /// 这个例程在 Update 泵上重复那两条语句以及面板生成时的
    /// <c>SetTime</c> 驱动，每帧一步，每一步都在
    /// <see cref="FikaBoundaries.Run"/> 内，
    /// 这样失败会在日志里被指明，而不是展开回会话序列。倒计时主体的其它部分都不运行。
    /// </summary>
    private IEnumerator RestoreInRaidHudRoutine()
    {
        try
        {
            var gameUi = _abstractGame?.GameUi;
            if (gameUi == null)
            {
                
                yield break;
            }

            // 先让部署帧落定，再打开层级。
            // 主体被跳过意味着这个例程是在倒计时主体返回的那一帧启动的，
            // 而那也正是会话序列进入它最后一步的同一帧；
            // 两帧可以让层级激活避开那一步的时间窗口。
            yield return null;
            yield return null;

            // ---- 1/8（等价）：激活 HUD 层级 -------------------------------------------------
            var gameUiObject = FikaUiPanels.DescribeObject(gameUi, "the game UI reference could not be read");
            try
            {
                if (gameUi.gameObject.activeSelf)
                {
                    
                }
                else
                {
                    // 这就是倒计时的第 1/8 条语句，那一次把整个 HUD 打开的调用。
                    // 游戏代码里它那五个受保护的兄弟调用只有在它能够运行时才会被加上：
                    // 见 FikaBoundaries.Run。
                    gameUi.gameObject.SetActive(true);
                    
                }
            }
            catch (Exception exception)
            {
                
            }

            // ---- 2/8（等价）：绑定撤离计时面板并填充它 ------------------------------------------
            var timerPanel = gameUi.TimerPanel;
            if (timerPanel == null)
            {
                
                yield break;
            }

            var profileId = _abstractGame.ProfileId;
            if (!string.IsNullOrEmpty(profileId))
            {
                FikaBoundaries.Run("HudRestore: bind the extraction timer panel (countdown statement 2/8)",
                    () => timerPanel.ProfileId = profileId);
            }

            // 面板的撤离列表在生成时填充，而面板关闭期间那次驱动会被跳
            // 过（HostGameController.Spawn ->
            // FikaUiPanels.CanDrive）。因此只激活层级会显示一个空面板，
            // 所以这里重复生成路径所做的那次 SetTime——现在面板已经活了。
            if (_abstractGame is CoopGame coopGame && !FikaBackendUtils.IsHeadless)
            {
                FikaBoundaries.Run("HudRestore: re-push the extraction list into the panel (SetTime, as at spawn)",
                    () =>
                    {
                        // 该档案有权使用的列表，解析方式与生成路径完全一致（
                        // HostGameController.Spawn）：scav
                        // 集合来自 scav 声明，其余人来自档案的可用点位。
                        var exfilController = ExfiltrationController.Instance;
                        if (exfilController == null)
                        {
                            
                            return;
                        }

                        exfilController.InitSecretExfils(_localPlayer);
                        ExfiltrationPoint[] exfilPoints;
                        SecretExfiltrationPoint[] secretExfilPoints;
                        if (coopGame.Profile?.Info?.Side == EPlayerSide.Savage)
                        {
                            exfilController.ScavExfiltrationClaim(_localPlayer.Position, coopGame.ProfileId,
                                coopGame.Profile.FenceInfo.AvailableExitsCount);
                            var mask = exfilController.GetScavExfiltrationMask(coopGame.ProfileId);
                            exfilPoints = exfilController.ScavExfiltrationClaim(mask, _localPlayer.ProfileId);
                            secretExfilPoints = exfilController.GetScavSecretExits();
                        }
                        else
                        {
                            exfilPoints = exfilController.EligiblePoints(coopGame.Profile);
                            secretExfilPoints = exfilController.SecretEligiblePoints();
                        }

                        // 与 HostGameController.Spawn 相同的调用、
                        // 相同的参数——面板现在活了，所以它在 SetTime 内部启动的滑动/颜色协程可以运行。
                        timerPanel.SetTime(DateTimeExtensions.UtcNow, coopGame.Profile.Info.Side,
                            coopGame.GameTimer.EscapeTimeSeconds(), exfilPoints, secretExfilPoints);
                    });
            }

            // ---- 显示突袭时间的那块面板：它由战斗 UI 画面揭示，也就是原版所有者路径所做的那次调用。
            // 当画面已是当前画面时是幂等的。 -------------------
            if (_localPlayer?.HealthController?.IsAlive == true)
            {
                FikaBoundaries.Run("HudRestore: re-assert the extraction timer panel (Reveal)",
                    () => timerPanel.Reveal());
            }

            // ---- 回读结果：HUD 现在要么已经生效，要么日志写明了为什么没有 ----------------
            yield return null;

            var gameUiLive = false;
            var timerPanelLive = false;
            try
            {
                gameUiLive = gameUi.gameObject.activeInHierarchy;
            }
            catch (Exception)
            {
            }

            try
            {
                timerPanelLive = timerPanel.gameObject.activeInHierarchy;
            }
            catch (Exception)
            {
            }

            
        }
        finally
        {
            // 无论例程是完成还是死掉，句柄都会被清除：下一次突袭会启动一个新的。
            _inRaidHudRestoreRoutine = null;
        }
    }

    private IEnumerator FixVOIPAudioDevice()
    {
        // Todo: 找出根本原因并优雅地修复...
        var comms = DissonanceComms.Instance;
        if (comms == null)
        {
            _voipAudioFixRoutine = null;
            yield break;
        }
        comms.IsMuted = false;
        yield return new WaitSeconds(1f);
        // 玩家可能在这段延迟期间开始按键通话。不要在 VOIP
        // 控制器已进入 Talking 之后静音原生采集。
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

        yield return new WaitSeconds(2f);

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

    /// <summary>
    /// （analysis/cutscene-native-registration）世界控制器的过场对，
    /// 在原生 <c>BaseLocalGame&lt;TPlayerOwner&gt;.method_4</c>
    /// 创建这整个家族的地方创建并注册：也就是本移植版的突袭启动协程（<c>CoopGame.InitPlayer</c>）。创建本身位于
    /// <see cref="Fika.Core.Main.Components.CutsceneBoundary.CreateCutsceneControllers"/>
    /// ——回滚路径（world-factory 创建）调用的同一个方法——因此始终
    /// 只有一个地方创建它们；原生逐帧钩子上的注册在同一次调用里完成，并且是幂等的。
    /// </summary>
    public void InitCutsceneControllers(GameWorld gameWorld) =>
        Fika.Core.Main.Components.CutsceneBoundary.InitCutsceneControllers(gameWorld);

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
                        // IL2CPP 包装不会隐式把托管方法组转换成
                        // System.Action<T>；请显式创建桥接。
                        Action<BtrSpawnOnThePathEvent> spawnManaged = OnBtrSpawn;
                        Il2CppSystem.Action<BtrSpawnOnThePathEvent> spawn = spawnManaged;
                        Action<BtrServicePurchaseEvent> purchaseManaged = OnBtrServicePurchase;
                        Il2CppSystem.Action<BtrServicePurchaseEvent> purchase = purchaseManaged;
                        _btrSpawn = GlobalEventsController.Instance.SubscribeOnEvent<BtrSpawnOnThePathEvent>(spawn);
                        _btrServicePurchase = GlobalEventsController.Instance.SubscribeOnEvent<BtrServicePurchaseEvent>(purchase);
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
    /// 初始化转场系统 TODO: 添加无头变体
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
    /// 用于确保没有任何兴奋剂或 mod 重置 DamageCoeff
    /// </summary>
    /// <param name="player">要在其上运行协程的 <see cref="FikaPlayer"/></param>
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
    /// 切换 <see cref="DebugUI"/> 菜单
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
            FikaRoutinePump.Instance.Stop(_voipAudioFixRoutine, "the game controller is being cleaned up");
            _voipAudioFixRoutine = null;
        }

        // HUD 恢复只在倒计时之后的头几帧运行；在这里停掉它，
        // 可以覆盖在那段窗口内就结束的突袭。它完成时会清除自己的句柄。
        if (_inRaidHudRestoreRoutine != null)
        {
            FikaRoutinePump.Instance.Stop(_inRaidHudRestoreRoutine, "the game controller is being cleaned up");
            _inRaidHudRestoreRoutine = null;
        }

        // 倒计时的重申请在部署等待之后只运行有界帧数；在这里停掉它，
        // 可以覆盖在那段窗口内就结束的突袭。例程完成时会从泵中移除，
        // 而对一个例程已不再运行的句柄调用 Stop() 是文档化的空操作。
        if (_countdownReassertRoutine != null)
        {
            FikaRoutinePump.Instance.Stop(_countdownReassertRoutine, "the game controller is being cleaned up");
            _countdownReassertRoutine = null;
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

