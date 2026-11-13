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

        // 与原版对齐：EFT 在 bot 场景存在之前执行的离线战局准备
        // （见 PrepareLocationForOfflineRaid）。
        PrepareLocationForOfflineRaid(location, wavesSettings);

        if (game is not AbstractGame abstractGame)
        {
            throw new NullReferenceException("Missing AbstractGame");
        }

        // 非波次场景设置
        _nonWavesSpawnScenario = NonWavesSpawnScenario.Create(abstractGame, location, _botsController);
        _nonWavesSpawnScenario.ImplementWaveSettings(wavesSettings);

        // 波次场景设置
        var waves = LocalGame.ModifySettings(wavesSettings, location.waves);
        _wavesSpawnScenario = WavesSpawnScenario.Create(abstractGame.gameObject, waves,
            (Il2CppSystem.Func<SpawnWave, Il2CppSystem.Threading.Tasks.Task>)
            (Func<SpawnWave, Il2CppSystem.Threading.Tasks.Task>)
            (wave => _botsController.ActivateBotsByWave(wave)), location);

        // Boss 场景设置
        var bossSpawns = LocalGame.ModifyBossWaveSettings(ELocalMode.PVE_OFFLINE, wavesSettings, location.BossLocationSpawn);
        bossSpawns = ApplyBossWaveCollapse(bossSpawns);
        // 每次激活在到达原版生成器之前都会被记录。
        // IL2CPP 包装器不会把托管方法组隐式转换为
        // System.Action<T>，所以要显式桥接它，并在无法创建
        // trampoline 时回退到原版方法绑定。
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
    // 每个对端对每个 bot 档案只能确认一次。快照在发送之前
    // 捕获，因此后来的加入者无法拖住这次生成。
    protected readonly Dictionary<int, HashSet<NetPeer>> _botQueue = [];
    private readonly object _botQueueGate = new();
    /// <summary>
    /// [BossSpawn] 行的遥测：本场战局中主机实际创建了每个 boss 波次
    /// 角色（boss 或 escort）的多少个 bot。
    /// </summary>
    private readonly Dictionary<WildSpawnType, int> _bossRolesCreated = [];
    /// <summary>
    /// 本场战局创建的每个 bot 的角色直方图，外加 [ZoneDiag] 环境行的
    /// 一次性标志（见 <see cref="LogAmbientBotCreation"/>）。
    /// </summary>
    private readonly Dictionary<WildSpawnType, int> _botRolesCreated = [];
    private bool _ambientScavLogged;
    protected readonly FikaServer _server;
    protected GameDateTime _gameDateTime;

    /// <summary>
    /// 若并非每个客户端都能加载某个 bot，多少秒后强制生成它
    /// </summary>
    private readonly float _botTimeout = 120f;
    /// <summary>
    /// <see cref="_botTimeout"/> 期间每个循环中的 <see cref="Task.Delay(int)"/>
    /// </summary>
    private readonly float _botTimeoutDelay = 0.25f;

    private readonly NetDataWriter _writer = new();

    /// <summary>
    /// 本主机已通过战前闸门后备逻辑释放过的对端 id。每场战局中一个对端最多
    /// 被断开一次（幂等）：收集对端时会检查该集合，因此第二次后备运行 - 或
    /// 重新出现的对端 id - 都无法对同一对端产生第二次断开。
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
    /// <summary>Fika 自己的订阅者（火炮火控）使用托管委托。</summary>
    public void add_UpdateByUnity(System.Action handler) => _nativeUpdateByUnity.Add(handler);
    public void remove_UpdateByUnity(System.Action handler) => _nativeUpdateByUnity.Remove(handler);
    /// <summary>
    /// 游戏用自己的原生委托订阅 <c>IBotGame.UpdateByUnity</c>，
    /// 因此注入的桥接把该 ABI 转发到这里。
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

        // 战利品描述符过去就在此处、第一次信息广播之后被丢弃。
        // 这使主机对之后每次请求都答复 "descriptor not ready"，
        // 于是输掉竞争的客户端 - 以及加入进行中战局的玩家 scav -
        // 完全拿不到世界战利品。该描述符约 135 KB，而
        // Location.Loot 本来也一直存活，所以它现在保留
        // 整场战局。

        yield break;
    }

    #region Bots
    /// <summary>
    /// 用于为主机生成一个 bot
    /// </summary>
    /// <param name="profile">要生成的 <see cref="Profile"/></param>
    /// <param name="position">生成所用的 <see cref="Vector3"/> 位置</param>
    /// <returns></returns>
    /// <summary>逐个 bot 创建链的尾部（来自上游 3.0.4）。</summary>
    private Task _botCreateTail = Task.CompletedTask;

    /// <summary>
    /// 逐个 bot 创建链的尾部（来自上游 3.0.4）。
    /// <para>
    /// 一次 boss 波次突发会在一帧内把多个档案交给 <see cref="CreateBot"/>（Interchange
    /// 用 EBotsSpawnMode.Anyway 激活 12 个波次中的 10 个）。并发运行这些创建会在 Bots、联机
    /// 处理器、网络 id 计数器和逐 bot 确认队列上产生竞争，而且观察到一场 Interchange 战局
    /// 在这种突发的第十个 bot 前后死在原版 Player.Create 里面。现在每次创建都会等待前一个
    /// 完成。
    /// </para>
    /// </summary>
    private async Task<LocalPlayer> CreateBot(GameWorld gameWorld, Profile profile, Vector3 position)
    {
        var previous = _botCreateTail;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _botCreateTail = gate.Task;

        // 已完成的任务会同步延续，所以第一个 bot 不付代价，之后每一个都在前一个
        // 之后立即于主线程开始。
        await previous;
        try
        {
            return await CreateBotCore(gameWorld, profile, position);
        }
        finally
        {
            gate.SetResult(true);
        }
    }

    private async Task<LocalPlayer> CreateBotCore(GameWorld gameWorld, Profile profile, Vector3 position)
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
        // 只有 bot 在主机上完全注册之后才计数。
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
    /// 确认一个对端为 <see cref="WaitForPlayersToLoadBotProfile(int)"/> 加载了某个 bot 档案
    /// </summary>
    /// <param name="netId">bot 的 ID</param>
    /// <param name="peer">进行确认的对端</param>
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
            // 重连会重放主机上已有 bot 的确认。
            Logger.LogDebug($"IncreaseLoadedPlayers::Ignoring late acknowledgement for existing bot netId {netId}");
            return;
        }

        Logger.LogError($"IncreaseLoadedPlayers::Could not find netId {netId}!");
    }

    /// <summary>
    /// 用于确保所有玩家都加载某个 bot 后它才生成的 <see cref="Task"/>
    /// </summary>
    /// <param name="netId">要生成的 NetId</param>
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

                await Task.Delay((int)(_botTimeoutDelay * 1000)); // 乘 0.X * 1000 得到毫秒
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
    /// 反生成一个 bot
    /// </summary>
    /// <param name="coopHandler"></param>
    /// <param name="bot">要反生成的 bot</param>
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
    /// 设置自定义天气曲线
    /// </summary>
    /// <param name="timeAndWeather">带自定义设置的结构体</param>
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

        // 把 FreeCamController 添加到 GameWorld 的 GameObject
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


        // 对端可能在其余玩家加载时断开。保留最初的上界，
        // 这样迟到的加入不会延长这道屏障，
        // 但不再等待已经断开连接的对端。
        int ExpectedPlayers() => Math.Max(1, Math.Min(initialPlayerCount, _server.PlayerAmount));
        int ExpectedHumans() => Math.Max(0, ExpectedPlayers() - (FikaBackendUtils.IsHeadless ? 1 : 0));
#if DEBUG
        Logger.LogWarning("Server: Waiting for coopHandler.AmountOfHumans < expected players, expected: " + ExpectedHumans());
#endif
        _server.ReadyClients++;

        // 判定行（analysis/loading-screen-sync）：主机的战前屏障等待对端的
        // *角色*，即每个对端的 SendCharacter 通告 - 而不是它的
        // 加载进度，也不是它可能未能应用的数据包。战局启动失败并离开
        // 会话的对端永远不会通告角色，这就是该等待现在有界、并在每一行里
        // 点名缺失对端数量的原因。
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

            // 后备逻辑确实触发了：这是唯一会断开对端的地方（见
            // DisconnectPeersWithoutCharacter）。上面正常的放行从不主动断开任何人。
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
    /// 在对端的战前屏障放弃的那一刻，断开仍连接且从未通告过角色的对端。
    /// 返回实际断开的对端数量。
    ///
    /// 原因（analysis/loading-screen-sync）：屏障等待每个预期对端的*角色*，而一个
    /// 战局启动失败的对端会保持连接，且永远不会通告角色 - 在 2026-10-11 18:58 那一轮，
    /// 这使主机停留在加载界面上，从客户端启动失败一直等到该客户端
    /// 进程被关闭。在此类对端仍保持连接时放行闸门，也会把它留在
    /// 会话中：它收到战局流量，而它自己的会话标志已与战局不再匹配
    /// （这正是那个处于主菜单的客户端收到 "Received CharacterSyncPacket as server" 的原因）。断开它
    /// 使双方一致：主机不再预期它（标准路径会
    /// 递减 PlayerAmount，因此 ExpectedPlayers 缩小），而对端则看到会话已关闭。
    ///
    /// 本方法遵循的规则：
    ///   * 只在后备逻辑触发的那一刻 - 正常的放行路径从不主动断开任何人；
    ///   * 只断开没有玩家的对端（在上次轮询与本次调用之间通告了角色的
    ///     对端 Player != null，不会被触碰；已经断开的对端根本不会被
    ///     NetServer 枚举，这就是"已经走了"的情况）；
    ///   * 幂等：<see cref="_gateReleasedPeers"/> 保存本后备逻辑释放过的每个对端 id，因此
    ///     每场战局中一个对端最多被断开一次；
    ///   * 只走标准入口 - NetPeer.Disconnect() -> NetManager.DisconnectPeer ->
    ///     断开数据包加本地断开事件，即主机为自行离开的对端
    ///     已经在跑的那套清理（FikaServer.OnPeerDisconnected：ready/assigned-net-id/
    ///     profile/scav 记账、RaidInteractionSync.OnDisconnected、ClientDisconnected 广播、
    ///     PlayerAmount-- 连同 UpdateBackendData 广播、重连计时器、无头处理
    ///     以及 PeerDisconnectedEvent）。这条路径中没有任何东西被本方法改动。
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
                // 把这个 id 还回去：该对端并未被释放，后来的后备（或后来的战局）可以再试。
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
    /// 战前屏障的状态，一行呈现，供 [LoadSync] 报告使用：主机预期多少、
    /// 已有多少、还缺谁，以及什么会放行该等待。
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
    /// 已连接但在此世界中尚无玩家的对端昵称 - 屏障行中"我在等谁"
    /// 的那一半。对端的昵称是接受连接时使用的 Tag
    /// （FikaServer.OnPeerDisconnected 也以同样方式读取它）。
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

    /// <summary>本主机在本场战局中已生成的人类玩家昵称。</summary>
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
                // SetWeatherNodes 会就地平滑雾效。为客户端保留原始
                // 节点，它们会自己再做平滑。
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

        // 这两者过去都是从倒计时例程内部启动的引擎协程（即在另一个协程的
        // MoveNext 内部 - 正是主机战局启动时出错的那种形态，见
        // analysis/host-raid-coroutine）。它们现在跑在 Update 驱动的泵上。
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

        // 战局数据点名的区域与场景实际提供的区域之差 - 这个差异
        // 唯一可见的地方（原版生成器会静默跳过这样的
        // 波次）。
        LogZoneDiagnostics(location, botZones);

        var useWaveControl = controllerSettings.BotAmount == EBotAmount.Horde;

        var limits = SetMaxBotsLimit(location);
        if (FikaPlugin.Instance.Settings.NoAI.Value)
        {
            FikaGlobals.LogWarning("No AI enabled - stopping bot spawns");
            controllerSettings.BotAmount = EBotAmount.NoBots;
            limits = 0;
        }

        // 两个预算共用一套映射：生成器的 MaxBots 和 PrepareLocationForOfflineRaid
        // 回退到的环境上限（见 ResolveBotBudget）。
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
    /// 供 mod 作者在各自 mod 中设置自定义上限
    /// </summary>
    /// <param name="location">要获取最大 bot 数的地图</param>
    /// <returns>最大 bot 数（若有）</returns>
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

        // 与原版对齐：EFT.LocalGame.LocalBotsControllerInitialization 调用
        // bossSpawnScenario.Run(BotSpawner.GetPmcZones()) - 那个两参数调用，其
        // 默认模式是 EBotsSpawnMode.Anyway（Fika 2.4.3 也是如此）。
        // AfterGameStarted 只为 Time > 0 的波次武装计时器，从不
        // 激活 Time <= 0 的波次；SPT 发送的每个 boss 波次都用 Time = -1
        // （Lighthouse：Zryachiy/Knight/Partisan/Gluhar，以及
        // rogueLighthouseSpawnTimeSettings.enabled 为 false 时的 Rogues），所以那种模式
        // 静默丢掉了整份 boss 列表。
        _bossSpawnScenario.Run(_botsController.BotSpawner.GetPmcZones(), _botsController.BotSpawner.GetSeason01Zones(), EBotsSpawnMode.Anyway);
        LogBossScenarioState("after Run");
        _botsController.EventsController.SpawnAction();
    }

    /// <summary>
    /// 在每次波次激活被交给原版生成器之前记录它。
    /// </summary>
    private void OnBossWaveActivated(BossLocationSpawn wave)
    {
        Logger.LogInfo($"[BossSpawn] Activating wave: {DescribeBossWave(wave)}");
        LogBossWaveRequest(wave);

        // （2026-10-26 W17，从上游 3.0.4 恢复）每 50 毫秒一次激活，避免一次波次突发
        // （Interchange 在一帧内激活 12 个波次中的 10 个）在同一帧里堆积同样多的 bot 创建。
        // CreateBot 本来就会串行化它们；这能避免帧本身卡住，而
        // 派发器把延续保持在 Unity 主线程上，这是原版激活所要求的。
        _ = MainThreadDispatcher.RunAsync(async () =>
        {
            try
            {
                await Task.Delay(BossWaveActivationSpacingMilliseconds);
            }
            catch (Exception)
            {
                // 被取消的等待仍然必须激活下面的波次。
            }

            try
            {
                _botsController.ActivateBotsByWave(wave);
            }
            catch (Exception exception)
            {
                // 绝不让托管异常回到原生计时器或
                // 事件代码；改为上报它，以便追查该波次。
                Logger.LogError($"[BossSpawn] Wave activation failed for '{wave?.BossName}': {exception}");
            }
        });
    }

    /// <summary>
    /// 两次 boss 波次激活之间的间隔，见 <see cref="OnBossWaveActivated"/>。
    /// </summary>
    private const int BossWaveActivationSpacingMilliseconds = 50;

    /// <summary>
    /// 原样记录从服务器收到的 boss 波次列表（在
    /// <see cref="LocalGame.ModifyBossWaveSettings"/> 之后）。
    /// </summary>
    private void LogBossWaves(string stage, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BossLocationSpawn> waves)
    {
        // (analysis/perf-diagnostics-cleanup) 本控制器的 boss/区域遥测：总开关
        // （'Coop / Diagnostics'，默认关闭）会关掉它。2026-10-13 00:40 的战局写了 102 行 [BossSpawn]
        // （每创建一个 boss 波次 bot 一行）和 4 行 [ZoneDiag] - 是那份日志中最大的战局内日志族。
        // 本方法中没有任何东西改变战局行为；它只是描述战局。
        
        var length = waves?.Length ?? 0;
        Logger.LogInfo($"[BossSpawn] Boss wave list {stage}: {length} wave(s)");
        for (var index = 0; index < length; index++)
        {
            Logger.LogInfo($"[BossSpawn]   wave[{index}] {DescribeBossWave(waves[index])}");
        }
    }

    /// <summary>
    /// 记录场景武装或激活了哪些波次，以及它为定时波次（Time &gt; 0）
    /// 持有多少个计时器。
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
    /// 每次 boss 波次激活一行，说明该波次向原版生成器要求什么。
    /// Icebreaker 的 boss 组被触发事件（wedges1-4）门控，所以
    /// "boss 永不生成"分成两种截然不同的情况，这一行加上
    /// <see cref="LogIdleBossTriggers"/> 可以区分它们：
    /// 波次已激活但 boss 实体缺失，或者波次根本从未
    /// 激活过。
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
    /// 列出仍有波次在等待的 botEvent 触发器。boss 组只在事件上生成的
    /// 地图（Icebreaker：wedges1-4，Terminal：TB1-4），在事件始终不到达时会
    /// 恰好留下一条痕迹 - 一个已武装的波次和这里的一条
    /// "idle" 条目。没有它，"房间里没有 boss" 就无法与
    /// "本移植丢了 boss" 区分开来。
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
    /// 统计原版生成器实际要求主机创建的 boss 与 escort bot，
    /// 并逐个记录。
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
        // 刻意与角色无关：先前的判断条件会跳过既不在 LocaleUtils 表中、也不叫
        // "follower*" 的每个角色，所以 0.14 之后的地图 boss（bossWedge、
        // bossBullyBlackDiv，……）的 bot 在战局中被创建了，
        // 却从未出现在这里 - 于是日志读起来完全是"桥接丢掉了 boss、
        // 只留下了 escort"。
        Logger.LogInfo($"[BossSpawn] Created boss-wave bot: {role} " +
            $"({bossName ?? (isFollower ? "escort" : "unlisted role")}) difficulty={settings.BotDifficulty} " +
            $"profile={profile.Id}, {role} created so far={count}");
    }

    /// <summary>
    /// 上报 - 并在 <see cref="CollapseBossWavesPerTrigger"/> 开启时移除 - 共用同一个触发器的
    /// bossWedge 波次。一个触发器下挂多个条目，是地图数据表达"这个事件拥有这些位置"的方式，
    /// 而原版场景会激活其中每一个（<c>BossSpawnScenario.Run</c> 把每个已激活的波次交给
    /// <c>ActivateBotsByWave</c>；没有任何东西去重）。只有 <c>bossWedge</c> 波次参与
    /// 分组（<see cref="BossTriggerKey"/>）；其他所有角色都按发布原样交给
    /// 场景，内容完全
    /// 按发布原样。
    ///
    /// 用户的期望是房间里只有一个 boss，所以折叠默认执行，并
    /// 保留 escort 数量最大的那个组成员（即小队长；数量相同时取
    /// 数据中的第一个条目）。<see cref="CollapseBossWavesPerTrigger"/> 关闭时
    /// 发布列表逐字节保持不变，这就是在不改数据库的情况下恢复原版行为
    /// （Icebreaker 上每个事件三个 Wedge 实体）的办法。
    /// </summary>
    private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BossLocationSpawn> ApplyBossWaveCollapse(
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BossLocationSpawn> waves)
    {
        var length = waves?.Length ?? 0;

        // 只有 bossWedge 家族会形成分组（见 BossTriggerKey）：其他任何波次 -
        // 另一个角色，或无触发器/计时的 boss - 都自成一个分组，所以折叠
        // 无法让它静默。
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
            // 用户要的是"一个 boss 带着它的 escort"，所以向原版生成器要求
            // 最多随从的那个波次得以存活；escort 数量相同时保留
            // 数据中靠前的条目。
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
            // keepers 只为重复的分组存在，因此无触发器的波次（自己的键，
            // 从不在 keepers 中）总是被保留。
            if (!losers.Contains(key) || keepers[key] == index)
            {
                kept.Add(wave);
                continue;
            }

            // 落败的波次留在列表中（场景仍会订阅它们），
            // 但被标记为已激活，而事件回调与
            // 计时器回调都会跳过处于该状态的波次，所以它们永远无法到达
            // 原版生成器。
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
    /// 按地图门控波次的方式对它们分组：触发器种类加上它的 id。没有 id 的波次
    /// 自成一个分组（带索引后缀的键），所以同一角色的两个定时 boss
    /// 永远不会互相折叠。
    ///
    /// 只有 <see cref="WildSpawnType.bossWedge"/> 参与：用户的规则是"除 boss 之外
    /// 不要碰发布的生成配置"，而 Icebreaker 数据还为
    /// pmcBotBlackDiv/bossBullyBlackDiv 列出了重复的触发器（hides1-3、stern0-3），它们的
    /// 叠加是数据自己的事。那些波次各自获得自己的键 - 各成一个分组 -
    /// 所以折叠永远无法让它们静默。
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
    /// 与 EFT 在任何 bot 场景存在之前执行的离线战局准备对齐 -
    /// <c>EFT.BaseLocalGame.smethod_1</c>（BaseLocalGame.cs:508-530）：
    /// 它把 Offline* 生成开关复制到实时开关上，并按战局的 bot 数量
    /// 缩放地图的环境 bot 上限。无头主机早已移植了它
    /// （Fika.Headless/Classes/GameMode/HeadlessGame.cs:330-353），联机主机从未
    /// 做过，所以发布 <c>NewSpawn=false</c> 而
    /// <c>OfflineNewSpawn=true</c> 的地点从未启动它的非波次
    /// 生成场景。在 Icebreaker 上，非波次场景是唯一区域有效的
    /// 生成系统（它的 <c>waves</c> 仍然点名 Interchange 的区域），所以那张地图除了
    /// boss 波次之外一直是空的。
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

        // 配置了环境窗口（BotStop/BotSpawnTimeOn*）却发布上限
        // 0 的地图，期望服务器下发上限：SPT 从
        // bot.json/maxBotCap 那样做（PostDbLoadService.AdjustLocationBotValues）。下面的
        // 兜底代替缺失的服务器上限，但用户对 Icebreaker 的规则
        // 是"不要环境 scavenger"（2026-10-13），所以它保持关闭，由地图自己的
        // BotMax = 0 决定 - 非波次场景以零预算运行。
        if (EnableAmbientBotCapFallback && ambientCap <= 0 && location.BotStop > 0)
        {
            ambientCap = Math.Min(ResolveBotBudget(wavesSettings.BotAmount), FallbackAmbientBotCap);
            fallback = true;
        }

        location.BotMax = ambientCap;

        // (analysis/perf-diagnostics-cleanup) 功能方法里的一条 [ZoneDiag] 探针行：总开关
        // 关闭时不会构建插值；上面的赋值不受影响。
        
    }

    /// <summary>
    /// 当地点配置了环境生成窗口却发布上限 0 时使用的
    /// 环境上限。18 是 SPT 自己的 <c>bot.json -> maxBotCap.default</c>（仍然
    /// 发布该表的安装对它没有专门条目的每张地图都使用这个值），且它
    /// 仍受战局自己的 bot 预算约束。
    /// </summary>
    private const int FallbackAmbientBotCap = 18;

    /// <summary>
    /// 自 2026-10-13 起默认关闭：用户不希望 Icebreaker 上有
    /// 环境 scavenger，所以又由地图数据决定（<c>BotMax = 0</c> -&gt; 非波次
    /// 场景算出零预算，不生成任何人）。把它打开，则改为给配置了环境窗口
    /// 却发布上限 0 的地图一个兜底上限。
    /// </summary>
    internal static bool EnableAmbientBotCapFallback = false;

    /// <summary>
    /// 把 <c>OfflineOldSpawn/OfflineNewSpawn</c> 镜像到实时生成开关上，并按战局的
    /// bot 数量缩放环境上限，与 <c>EFT.BaseLocalGame.smethod_1</c> 和无头主机
    /// 的做法完全一致。它自己不会生成哪怕一个 bot：非波次场景
    /// 另外还需要 <c>BotMax &gt; 0</c>（见
    /// <see cref="EnableAmbientBotCapFallback"/>）。把它关掉可让发布标志
    /// 保持不动。
    /// </summary>
    internal static bool MirrorOfflineSpawnFlags = true;

    /// <summary>
    /// 2026-10-13 的用户要求：一份地图数据可能为同一个触发器列出多个
    /// boss 波次（Icebreaker 的 <c>wedges1-4</c> 每个发布三个，分布在三个不同的
    /// 房间），而原版场景会激活其中每一个，于是一个事件就在船上放下
    /// 三个 Wedge 实体。开启（默认）时每个触发器只生成一个波次 -
    /// escort 最多的那个，数量相同时取数据中的第一个条目 - 其余的
    /// 被标记为 <c>Activated</c>，从而永不触发。关闭则在不改数据库的情况下
    /// 恢复原版行为（发布列表的每个条目）。
    /// </summary>
    /// <summary>
    /// (analysis/boss-wedge-vanilla) 作为用户设置已退役：折叠让每个触发点只保留一个
    /// boss 波次，但在实际联机战局中 boss 因此完全不再生成，所以重新使用原版
    /// 机制。字段保留（false），使 <see cref="ApplyBossWaveCollapse"/> 仍然上报发布的
    /// 波次列表，而旧配置键由 FikaConfig.RetireRemovedEntries 从文件中删除。
    /// </summary>
    internal static bool CollapseBossWavesPerTrigger = false;

    /// <summary>
    /// EFT 的 <c>BaseLocalGame.smethod_1</c> 中的 WAVE_COEF_* 开关，完全按
    /// 无头移植的读取方式读取。
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
    /// 战局的 bot 预算：生成器的 MaxBots 与
    /// <see cref="PrepareLocationForOfflineRaid"/> 的环境上限兜底共用这套映射，因此
    /// 环境生成器永远不会瞄准超过战局自身上限的数量。
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
    /// 一行说明战局数据引用的区域名
    /// 与场景实际提供的区域之间的对照。一个地点可能发布点名另一张地图
    /// 区域的波次条目（Icebreaker 的 <c>waves</c> 和 <c>OpenZones</c> 仍带着 Interchange
    /// 的那一套），而原版生成器对这样的波次会静默返回
    /// （EFT.BotSpawner.ActivateBotsByWave：<c>botZone == null</c> -&gt; 不生成），
    /// 这一点此前没有任何日志能显示。
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
    /// 一次性证明地图的非波次（环境）生成场景是活的：主机创建的第一个
    /// scavenger 角色 bot 每场战局记录一次。在唯一区域有效的生成
    /// 系统就是非波次系统的地图（Icebreaker）上，这一行会在战局开始后不久
    /// 出现 - 否则就是环境场景从未运行。
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
    /// 环境生成器挑选的角色（<c>NonWavesSpawnScenario</c> 对 <c>assault</c> 和
    /// <c>marksman</c> 加权；它们的事件变体共用同一个桶）。
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
            // HeadlessGame 没有本地档案、GameUI 或玩家
            // 所有者。它的 HeadlessGameController 在世界和 bot 就绪后
            // 调用这个钩子，所以这里只执行原版启动中
            // 与整局相关的那部分。
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

        // 游戏的 ExtractionTimersPanel 在 SetTime 内部启动它的滑动/颜色协程，
        // 而 Unity 在关闭（不活动）的面板上拒绝这样做。只在它活着时驱动它。
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
    /// 当本地玩家成功撤离时，启用自由视角，并把撤离告知其他玩家
    /// </summary>
    /// <param name="player">要在其上启动协程的本地玩家</param>
    /// <param name="exfiltrationPoint">用于撤离的撤离点</param>
    /// <param name="transitPoint">用于转移的转移点</param>
    /// <returns></returns>
    public override void Extract(FikaPlayer player, ExfiltrationPoint exfiltrationPoint, TransitPoint transitPoint = null)
    {
        if (_fikaGame is not CoopGame coopGame)
        {
            // 无头主机没有本地 UI/玩家所有者。转移撤离
            // 由无头游戏自己完成；保留共享的玩家记账
            // 并通知对端，而不触碰 CoopGame 的 UI
            // 或摄像机状态。
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

        // SPT 在评估战局结果之前，把排队的治疗、能量和水分经验
        // 折算进档案。
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
                // 看起来已由 SPT 处理，所以这里只做可见的添加
                player.Profile.EftStats.SessionCounters.AddDouble(0.2, NativeCounters.ExitStanding());
            }
        }

        if (player.Side == EPlayerSide.Savage)
        {
            // 看起来已由 SPT 处理，所以这里只做可见的添加
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
            try // 这样客户端在断线时也能撤离
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

            // 防止玩家在撤离后继续搜刮
            EftScreenManager.Instance.CloseAllScreensForced();

            // 销毁会话计时器
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

        // （2026-10-19，方案 A）Terminal：在构建描述符之前把每位玩家的装备放进
        // 军械库容器，这样容器到达每台机器时就已装好装备。
        try
        {
            // （2026-10-19）以战局自身的位置 id 为门槛，而不是菜单选择：转移期间菜单
            // 仍持有上一张地图，而向非 Terminal 的战局注入军械库会
            // 触碰不属于它的战利品表。
            if (TerminalArmory.Applies(location?.Id))
            {
                TerminalArmory.Inject(location, FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler);
            }
        }
        catch (Exception exception)
        {
            // （2026-10-19）战利品初始化绝不能因为 terminal 军械库而失败：这里抛异常
            // 会让每台机器都卡在 "loading loot pool"。
            FikaGlobals.LogError($"[TerminalArmory] skipped, the raid keeps its normal loot: {exception}");
        }

        var lootDescriptor = ItemBinarySerializer.SerializeLootData(FilterLootForHostProfile(location.Loot),
            FikaGlobals.SearchControllerSerializer);
        _writer.PutEFTLootDataDescriptor(lootDescriptor);
        LootData = _writer.CopyData();
        _writer.Reset();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 丢弃主机自己无法生成的战利品条目，因为它们绑定在其他档案上
    /// （战斗通行证文件、档案专属任务战利品）。
    /// 客户端会在这个描述符之上再套用自己的档案过滤，所以结果
    /// 永远不会包含主机没有的物品 - 否则主机
    /// 会拒绝客户端的拾取描述符，而客户端在仍在交互时
    /// 被迫经历一次完整的背包重同步。
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
            // 绝不让这个优化让战局失去战利品。
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
        // 只有根据战局自身战利品数据构建的描述符才是
        // 权威的。在主机仍在生成时（或 InitializeLoot 运行之前）序列化
        // 实时的世界 LootList，会为早到的客户端产出残缺的
        // 世界，所以现在未就绪的主机用空负载
        // 答复，客户端再轮询一次。战利品本身由 SpawnLoot 从
        // Location.Loot 生成，所以 InitializeLoot 描述的总是
        // 完整的世界。
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

        // SPT 5 的二进制战利品格式有专门的 JsonCorpse 描述符。
        // 这里返回普通的 JsonLootItem 会丢掉尸体的外观
        // 定制和布娃娃姿态，使迟到的客户端
        // 只看到一个空战利品条目（并且阻止 dogtag/尸体背包
        // 被原版战利品生成器恢复）。
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
                // SPT 5 从原版 Corpse API 中移除了 IsZombieCorpse。
                // 常规玩家尸体路径是 Fika 在这里唯一能
                // 编写的路径；原版僵尸尸体保留安全的默认值。
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

        // SPT 5 在场景中保留已耗尽的计时器，而 TimerManager 早已
        // 停止并分离了它们。原版 Stop 假定每个条目都是活动的；
        // 在它正常取消之前先剪除已完成的条目。
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
