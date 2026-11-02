using EFT.Communications;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BepInEx.Logging;
using Comfort.Common;
using Diz.Jobs;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.ObservedClasses;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;

namespace Fika.Core.Main.Components;

/// <summary>
/// CoopHandler 负责 raid 中的大部分生成逻辑，例如其他玩家或 AI。它还负责本地玩家的撤离。
/// </summary>
public class CoopHandler : MonoBehaviour
{
    public CoopHandler(System.IntPtr pointer) : base(pointer) { }


    public CoopHandler() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<CoopHandler>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    #region Fields/Properties
    /// <summary>
    /// 本地 <see cref="CoopGame"/> 实例的引用
    /// </summary>
    public IFikaGame LocalGameInstance { get; internal set; }
    /// <summary>
    /// raid 会话的 ID
    /// </summary>
    public string ServerId { get; internal set; }
    /// <summary>
    /// 本地 <see cref="FikaPlayer"/> 玩家的引用
    /// </summary>
    public FikaPlayer MyPlayer { get; internal set; }
    /// <summary>
    /// <see cref="CoopHandler"/> 是否应在 <see cref="Update"/> 期间同步并生成 profile
    /// </summary>
    public bool ShouldSync { get; set; }
    /// <summary>
    /// 键 = <see cref="FikaPlayer.NetId"/>、值 = <see cref="FikaPlayer"/> 的字典
    /// </summary>
    public Dictionary<int, FikaPlayer> Players { get; internal set; }
    /// <summary>
    /// 以 <see cref="FikaPlayer"/> 形式表示的所有人类玩家
    /// </summary>
    public List<FikaPlayer> HumanPlayers { get; internal set; }
    /// <summary>
    /// 人类玩家的数量
    /// </summary>
    public int AmountOfHumans
    {
        get
        {
            return HumanPlayers.Count;
        }
    }
    /// <summary>
    /// 已撤离的 <see cref="FikaPlayer.NetId"/> 列表
    /// </summary>
    public List<int> ExtractedPlayers { get; internal set; }
    /// <summary>
    /// 返回当前的 <see cref="EQuitState"/>
    /// </summary>
    public EQuitState QuitState
    {
        get
        {
            // 出错？
            if (LocalGameInstance == null || MyPlayer == null)
            {
                return EQuitState.None;
            }

            // 我们还活着吗
            if (!MyPlayer.HealthController.IsAlive)
            {
                return EQuitState.Dead;
            }

            // 我们已经撤离了吗
            if (LocalGameInstance.ExtractedPlayers.Contains(MyPlayer.NetId))
            {
                return EQuitState.Extracted;
            }

            return EQuitState.None;
        }
    }

    private ManualLogSource _logger;
    /// <summary>
    /// 按 <see cref="FikaPlayer.NetId"/> 索引的所有已排队玩家的列表
    /// </summary>
    private List<int> _queuedPlayers;
    private readonly Dictionary<int, SpawnObject> _pendingPlayerStates = [];
    /// <summary>
    /// 包含所有待生成玩家的 <see cref="SpawnObject"/> 队列
    /// </summary>
    private Queue<SpawnObject> _spawnQueue;
    private bool _isClient;
    private float _charSyncCounter;
    private bool _requestQuitGame;
    /// <summary>在世界存在之前到达、并被保留在生成队列中的 profile（见 QueueProfile）。</summary>
    private int _profilesQueuedBeforeWorld;
    #endregion

    public static bool TryGetCoopHandler(out CoopHandler coopHandler)
    {
        coopHandler = null;
        var networkManager = FikaSingleton<IFikaNetworkManager>.Instance;
        if (networkManager != null)
        {
            coopHandler = networkManager.CoopHandler;
            return true;
        }

        return false;
    }

    public static string GetServerId()
    {
        var networkManager = FikaSingleton<IFikaNetworkManager>.Instance;
        if (networkManager != null && networkManager.CoopHandler != null)
        {
            return networkManager.CoopHandler.ServerId;
        }

        return FikaBackendUtils.GroupId;
    }

    public void CleanUpForTransit()
    {
        // (analysis/transit-reload-oom) 本组件 —— 以及拥有它的网络管理器 —— 会有意
        // 在 transit 中存活下来，因此这里持有的每个引用都会随之存活。其中有两个直接指向
        // 正在被替换的那场 raid，它们会在下面被丢弃，而不是被带进下一场：
        // 旧 world 的 observed 玩家，以及旧 world 的本地玩家。
        var network = FikaSingleton<IFikaNetworkManager>.Instance;
        var observed = network?.ObservedPlayers;
        var droppedPlayers = observed?.Count ?? 0;
        if (droppedPlayers > 0)
        {
            // 该列表每帧由存活的网络管理器（FikaClient.Update）驱动，因此一个
            // 陈旧条目不只是被钉住的 Player 对象 —— 它是一个正在被驱动的、已销毁的玩家。
            // 新的 world 会重新加入它生成的每一个玩家（CoopHandler.SpawnObservedPlayer）。
            observed.Clear();
        }

        ShouldSync = false;
        Players.Clear();
        HumanPlayers.Clear();
        ExtractedPlayers.Clear();
        _queuedPlayers.Clear();
        _pendingPlayerStates.Clear();
        _spawnQueue.Clear();
        LocalGameInstance = null;
        _requestQuitGame = false;
        MyPlayer = null;
        _charSyncCounter = 0f;
        var queuedBeforeWorld = _profilesQueuedBeforeWorld;
        _profilesQueuedBeforeWorld = 0;
        if (_isClient)
        {
            var client = FikaSingleton<FikaClient>.Instance;
            if (client != null)
            {
                client.FikaClientWorld = null;
                // 新 raid 创建它的玩家时，NetManagerUtils.SetupGameVariables 会重新设置这两者；
                // 换挡窗口内所有读取它们的代码都受 LocalGameInstance/world 门控。
                client.MyPlayer = null;
            }
        }

    }

    /// <summary>
    /// 检查所有人类玩家是否都已死亡/倒地
    /// </summary>
    /// <returns>若所有人都已死亡则为 <see langword="true"/>；否则为 <see langword="false"/></returns>
    public bool AreAllHumanPlayersDead()
    {
        var deadPlayers = 0;
        for (var i = 0; i < HumanPlayers.Count; i++)
        {
            var player = HumanPlayers[i];
            if (player.IsYourPlayer)
            {
                continue;
            }

            if (!player.HealthController.IsAlive || player.Downed)
            {
                deadPlayers++;
            }
        }

        return deadPlayers >= (AmountOfHumans - 1);
    }

    protected void Awake()
    {
        _logger = Logger.CreateLogSource("CoopHandler");
        _spawnQueue = new(50);
        _queuedPlayers = [];
        Players = [];
        HumanPlayers = [];
        ExtractedPlayers = [];
        ShouldSync = false;
        _charSyncCounter = 0f;
        _isClient = FikaBackendUtils.IsClient;
    }

    /// <summary>
    /// (analysis/perf-round2) co-op handler 的逐帧工作是帧开销探针的一个槽位。
    /// </summary>
    protected void Update()
    {
        
        try
        {
            UpdateMeasured();
        }
        finally
        {
            
        }
    }

    private void UpdateMeasured()
    {
        if (LocalGameInstance == null)
        {
            return;
        }

        if (ShouldSync)
        {
            if (_spawnQueue.Count > 0)
            {
                var spawn = _spawnQueue.Dequeue();
                _logger.LogInfo($"Spawning queued player: netId={spawn.NetId}, profile={spawn.Profile?.ProfileId}, isAI={spawn.IsAI}");
                if (spawn.QueuedBeforeWorld)
                {
                    // 该 profile 等待了 world：记录等了多久，这样将来某次运行可以显示
                    // world 未就绪的窗口是否仍然长到值得关注。
                    spawn.QueuedBeforeWorld = false;
                }
                MainThreadDispatcher.RunAsync(() => SpawnPlayer(spawn).AsTask()).Forget();
            }

            if (!_isClient)
            {
                foreach (var human in HumanPlayers)
                {
                    if (human.TryCast<ObservedPlayer>() is not { } observed || !observed.Downed || observed.HasDied ||
                        observed.CaptureRevivalState().BleedoutRemaining != 0) continue;
                    var death = new EFT.HealthSystem.SyncHealthPacket
                    {
                        SyncType = EFT.HealthSystem.SyncHealthPacket.ESyncType.IsAlive
                    };
                    var data = death.Data;
                    var alive = data.IsAlive;
                    alive.IsAlive = false;
                    alive.DamageType = EDamageType.Undefined;
                    data.IsAlive = alive;
                    death.Data = data;
                    observed.SetupCorpseSyncPacket(death);
                    observed.NetworkHealthController.IsAlive = true;
                    observed.NetworkHealthController.HandleSyncPacket(death);
                }
                _charSyncCounter += Time.unscaledDeltaTime;
                var waitTime = LocalGameInstance.GameController.GameInstance.Status == GameStatus.Started ? 15 : 5;
                if (_charSyncCounter > waitTime)
                {
                    _charSyncCounter = 0f;
                    if (Players == null)
                    {
                        return;
                    }

                    SyncPlayersWithClients();
                }
            }
        }

        FikaFinalMissionDirector.Tick();
        ProcessQuitting();
    }

    private void SyncPlayersWithClients()
    {
        FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.CharacterSync,
            CharacterSyncPacket.FromValue(Players), true);
    }

    protected void OnDestroy()
    {
        _pendingPlayerStates.Clear();
        Players.Clear();
        HumanPlayers.Clear();
    }

    public void ReInitInteractables()
    {
        Singleton<GameWorld>.Instance.World.RegisterNetworkInteractionObjects(null);
    }

    /// <summary>
    /// 负责处理退出当前活动游戏会话的各种方式
    /// </summary>
    private void ProcessQuitting()
    {
        if (FikaPlugin.Instance.Settings.ExtractKey.Value.IsDown())
        {
            RequestQuit();
        }
    }

    /// <summary>使用与已配置热键相同的状态检查与 peer 检查来请求撤离。</summary>
    public void RequestQuit()
    {
        if (FikaFinalMissionDirector.DeferStop) return;
        var quitState = QuitState;
        if (quitState == EQuitState.None || _requestQuitGame)
        {
            return;
        }

        ConsoleScreen.Log("Attempting to extract!");
        _logger.LogInfo("Attempting to extract!");

        _requestQuitGame = true;
        var fikaGame = LocalGameInstance;

        var isPlayerAlive = MyPlayer.ActiveHealthController.IsAlive;
        var exitLocation = isPlayerAlive ? fikaGame.ExitLocation : null;

        // 客户端逻辑
        if (_isClient)
        {
            fikaGame.Stop(MyPlayer.ProfileId, fikaGame.ExitStatus, exitLocation, 0);
            return;
        }

        // 主机逻辑
        var server = FikaSingleton<FikaServer>.Instance;
        var peers = server.NetServer.ConnectedPeersCount;

        if (fikaGame.ExitStatus == ExitStatus.Transit && HumanPlayers.Count <= 1)
        {
            fikaGame.Stop(MyPlayer.ProfileId, fikaGame.ExitStatus, exitLocation, 0);
            return;
        }

        if (peers > 0)
        {
            NotificationManager.DisplayWarningNotification(LocaleUtils.HOST_CANNOT_EXTRACT.Localized());
            _requestQuitGame = false;
            return;
        }

        var recentDisconnect = server.TimeSinceLastPeerDisconnected > DateTime.Now.AddSeconds(-5);
        if (server.HasHadPeer && recentDisconnect)
        {
            NotificationManager.DisplayWarningNotification(LocaleUtils.HOST_WAIT_5_SECONDS.Localized());
            _requestQuitGame = false;
            return;
        }

        fikaGame.Stop(MyPlayer.ProfileId, fikaGame.ExitStatus, exitLocation, 0);
    }

    private async ValueTask SpawnPlayer(SpawnObject spawnObject)
    {
        try
        {
            await SpawnPlayerCore(spawnObject);
            _logger.LogInfo($"Spawned queued player: netId={spawnObject.NetId}, profile={spawnObject.Profile?.ProfileId}");
        }
        catch (Exception exception)
        {
            _logger.LogError($"Failed to spawn queued player netId={spawnObject.NetId}: {exception}");
            throw;
        }
        finally
        {
            if (_pendingPlayerStates.TryGetValue(spawnObject.NetId, out var current) && ReferenceEquals(current, spawnObject))
            {
                _queuedPlayers.Remove(spawnObject.NetId);
                _pendingPlayerStates.Remove(spawnObject.NetId);
            }
        }
    }

    /// <summary>
    /// raid 进入运行状态后，一次玩家生成会为其 prefab 池等待多久。
    /// </summary>
    private const int PrefabLoadTimeoutMs = 120000;

    internal bool QueueSpawningPlayerState(Fika.Core.Networking.Packets.Player.Common.CommonPlayerPacket packet)
    {
        if (!_pendingPlayerStates.TryGetValue(packet.NetId, out var spawn)) return false;
        // 在接收时更新计时器，这样 bundle 加载无法延长流血倒计时
        // 或救援暂停。它们的视觉状态在玩家创建之后再应用。
        if (packet.SubPacket is DownedSyncPacket downed)
        {
            if (!spawn.DeathPending) spawn.RevivalClock.Restore(downed.State);
            return true;
        }
        if (packet.SubPacket is RevivingPlayerPacket reviving)
        {
            if (!spawn.DeathPending) spawn.RevivalClock.SetReviving(reviving.Reviving, reviving.Nickname, FikaPlugin.Instance.Settings.ReviveConfig.ReviveTime);
            return true;
        }
        if (packet.SubPacket is RevivedPlayerPacket)
        {
            var state = spawn.RevivalClock.Capture();
            if (!spawn.DeathPending && state.Downed && state.BleedoutRemaining != 0) spawn.RevivalClock.SetDowned(false, 0);
            return true;
        }
        if (packet.SubPacket is HealthSyncPacket health &&
            health.Packet.SyncType == EFT.HealthSystem.SyncHealthPacket.ESyncType.IsAlive && !health.Packet.Data.IsAlive.IsAlive)
            spawn.DeathPending = true;
        // 报文实例是池化的。在本机 prefab/双手创建让出期间，请保留序列化后的字节，
        // 绝不要保留对被回收子报文的引用。
        var writer = new NetDataWriter();
        packet.Serialize(writer);
        spawn.PendingPackets.Add(writer.CopyData());
        return true;
    }

    private async ValueTask SpawnPlayerCore(SpawnObject spawnObject)
    {
        var spawningWorld = Singleton<GameWorld>.Instance;
        var spawningGame = LocalGameInstance;
        if (spawnObject.Profile == null)
        {
            _logger.LogError("SpawnPlayer: Profile was null!");
            _queuedPlayers.Remove(spawnObject.NetId);
            return;
        }

        foreach (IPlayer player in Singleton<GameWorld>.Instance.AllPlayersEverExisted)
        {
            if (player.ProfileId == spawnObject.Profile.ProfileId)
            {
                return;
            }
        }

        var allPrefabPaths = new Il2CppSystem.Collections.Generic.List<ResourceKey>();
        foreach (var path in spawnObject.Profile.GetAllPrefabPaths(!spawnObject.IsAI))
            allPrefabPaths.Add(path);
        if (allPrefabPaths.Count == 0)
        {
            _logger.LogError($"SpawnPlayer::{spawnObject.Profile.Info.Nickname}::PrefabPaths are empty!");
            return;
        }

        await JobScheduler.Yield().AsManaged();
        // 中途走进一场已经在运行的 raid 的玩家，不能去等已配置的（低）加载优先级：
        // raid 再也不会进入空闲状态，因此那个 bundle 任务
        // 永远不会被拾起，生成会永远挂住。
        var raidAlreadyRunning = FikaSingleton<IFikaGame>.Instance?.GameController?.RaidStarted == true;
        // 在一场运行中的 raid 里，"Local" 程序集类型永远不会完成它的池任务，
        // 这就是为什么后加入者的 prefab（例如玩家 scav 的躯体，
        // wild_body.bundle）一直未被加载，生成会以
        // "wild_body.bundle is not loaded" 失败。raid 中期的加载使用 online 程序集
        // 类型和一般优先级，与世界对象生成器完全一致。
        var assemblyType = raidAlreadyRunning
            ? ObjectsFactory.AssemblyType.Online
            : ObjectsFactory.AssemblyType.Local;
        var priority = raidAlreadyRunning
            ? JobYieldPriority.General
            : FikaPlugin.Instance.Settings.LoadPriority.Value.ToLoadPriorty();
        _logger.LogInfo($"SpawnPlayerCore:: loading {allPrefabPaths.Count} prefabs for netId={spawnObject.NetId} " +
            $"(raidStarted={raidAlreadyRunning})");
        
        try
        {
            var loadTask = Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid,
                assemblyType, allPrefabPaths, priority,
                null, new Il2CppSystem.Threading.CancellationToken()).AsManaged();
            // 绝不要在无上限的情况下等待池任务。一个挂起的任务会让整个生成 —— 以及随之而来的
            // peer.Player 绑定 —— 无法完成，
            // 而那个客户端的每个玩法报文（库存操作、
            // 拾取、收益）随后都会被主机拒绝，而玩家
            // 体验到的就是客户端在拾起东西的那一刻卡死。
            // 如果加载没有及时完成，生成会带着已经驻留的内容
            // 继续下去；失败会被记录，并在客户端再次宣告其角色时
            // 重试。
            var finished = await Task.WhenAny(loadTask, Task.Delay(PrefabLoadTimeoutMs));
            if (finished != loadTask)
            {
                _logger.LogWarning($"SpawnPlayerCore:: the prefab load for netId={spawnObject.NetId} did not " +
                    $"finish within {PrefabLoadTimeoutMs} ms, continuing with the resident prefabs");
            }
            else
            {
                await loadTask;
            }

            _logger.LogInfo($"SpawnPlayerCore:: prefabs ready for netId={spawnObject.NetId}");
        }
        catch (OperationCanceledException)
        {
            _logger.LogError($"SpawnPlayer::{spawnObject.Profile.Info.Nickname}::Load Cancelled");
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError($"SpawnPlayer::{spawnObject.Profile.Info.Nickname}::Load Failed: {ex}");
            return;
        }
        await JobScheduler.Yield().AsManaged();

        if (spawningWorld == null || Singleton<GameWorld>.Instance?.Pointer != spawningWorld.Pointer ||
            LocalGameInstance != spawningGame) return;
        _logger.LogInfo($"SpawnPlayerCore:: creating the observed player for netId={spawnObject.NetId}");
        var otherPlayer = await SpawnObservedPlayer(spawnObject);
        _logger.LogInfo($"SpawnPlayerCore:: observed player for netId={spawnObject.NetId} is {(otherPlayer == null ? "null" : "ready")}");
        
        if (otherPlayer == null) return;

        var revival = spawnObject.RevivalClock.Capture();
        if (revival.Downed)
        {
            otherPlayer.ToggleDowned(true);
            if (revival.RevivePauseRemaining > 0)
                otherPlayer.ToggleRevive(true, revival.ReviverNickname);
        }
        else if (!revival.IsAlive)
        {
            otherPlayer.OnDead(EDamageType.Undefined);
            otherPlayer.NetworkHealthController.IsAlive = false;
        }
        otherPlayer.RevivalClock.Restore(revival);
        for (var i = 0; i < spawnObject.PendingPackets.Count; i++)
        {
            var pending = new Fika.Core.Networking.Packets.Player.Common.CommonPlayerPacket();
            try
            {
                pending.Deserialize(new NetDataReader(spawnObject.PendingPackets[i]));
                pending.Execute(otherPlayer);
            }
            finally { pending.Clear(); }
        }
        spawnObject.PendingPackets.Clear();

        if (FikaBackendUtils.IsServer)
        {
            if (LocalGameInstance != null)
            {
                var botController = (FikaSingleton<IFikaGame>.Instance.GameController as HostGameController)?.BotsController;
                if (botController != null)
                {
                    // 启动协程，因为 botController 有时可能需要一段时间才能启动……
#if DEBUG
                    _logger.LogInfo("Starting AddClientToBotEnemies routine.");
#endif
                    Fika.Core.Il2CppCoroutineExtensions.StartCoroutine(this, AddClientToBotEnemies(botController, otherPlayer));
                }
                else
                {
                    _logger.LogError("botController was null when trying to add player to enemies!");
                }
            }
            else
            {
                _logger.LogError("LocalGameInstance was null when trying to add player to enemies!");
            }
        }

        _queuedPlayers.Remove(spawnObject.NetId);
    }

    /// <summary>
    /// 一次最多允许多少个 profile 在生成队列中等待。一旦 ShouldSync 被设置，队列每帧消费一个 profile，
    /// 因此这只对突发情况有意义 —— 而超出该上限的突发会被上报，
    /// 而不是无上限地增长。
    /// </summary>
    private const int MaxQueuedProfiles = 256;

    /// <summary>
    /// 把一个 profile（玩家或 AI）排入生成队列。
    ///
    /// world 可能确实还不存在：transit 会替换 world 对象，而一个已经
    /// 完成自身加载的 peer，可能会在本机的 world 仍在创建时宣告它的角色 ——
    /// 而客户端的 world 比主机的 raid 启动要*更晚*创建。这个方法以前
    /// 会用 "QueueProfile::GameWorld was null, not queuing" 丢掉这样的 profile（在
    /// 2026-10-11 18:58 那次客户端运行中有 10 次）：该 peer 的角色从未被生成，之后每条
    /// 针对它的命令都失败了，例如主机的 AI 传送命令
    /// （"Could not find 20..29 to teleport"，LogOutput.log:2727-2736，共 10 行）。
    ///
    /// 现在改为保留该 profile。生成泵只在 ShouldSync 被设置时运行，而
    /// CoopGame.InitPlayer 是在 world 存在之后才设置它，因此排队的 profile 恰好在
    /// 能够生成时才被生成，绝不会更早。只有在 world 缺失期间才会跳过依赖 world 的重复检查；
    /// SpawnPlayerCore 会在生成时针对 AllPlayersEverExisted 再检查一次。
    /// </summary>
    public void QueueProfile(Profile profile, byte[] healthByteArray, Vector3 position, int netId, bool isAlive, bool isAI, MongoID firstId, ushort firstOperationId, bool isZombie, MongoID? itemId,
        EHandsControllerType controllerType = EHandsControllerType.None, RevivalState? revivalState = null, bool isStationary = false)
    {
        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld != null)
        {
            foreach (IPlayer player in gameWorld.AllPlayersEverExisted)
            {
                if (player.ProfileId == profile.ProfileId)
                {
                    _logger.LogInfo($"QueueProfile::Skipped {profile.ProfileId} (netId {netId}), the profile already exists in the world");
                    return;
                }
            }
        }
        else
        {
            _profilesQueuedBeforeWorld++;
        }

        if (_queuedPlayers.Contains(netId) && !Players.ContainsKey(netId))
        {
            if (_pendingPlayerStates.ContainsKey(netId))
            {
                _logger.LogInfo($"QueueProfile::Skipped netId {netId} for {profile.ProfileId}, it is already queued");
                return;
            }

            // (2026-10-17) 该 net id 被标记为已排队，但并没有挂起的生成对象对应它：这正是
            // 在应用某个宣告的过程中队列被清空时，一次 world 换挡留下的痕迹。把陈旧
            // 标记当作"已排队"会让该 peer 在 raid 余下的时间里一直没有玩家（每个
            // proceed 请求都被回答 "unbound player"），因此这个标记被
            // 丢弃，profile 会被重新排队。
            _queuedPlayers.Remove(netId);
            LoadSync.Warn("CoopHandler.QueueProfile",
                "netId " + netId + " was marked as queued without a pending spawn (" + profile.ProfileId
                + "): the stale mark is cleared and the profile is queued again");
        }

        if (_spawnQueue.Count >= MaxQueuedProfiles)
        {
            // 把 net id 还回去，否则 CheckIds（CharacterSync 的应答）会认为这个
            // 玩家正在路上，从而再也不向主机索要该角色。
            _queuedPlayers.Remove(netId);
            LoadSync.Warn("CoopHandler.QueueProfile",
                "the spawn queue is full (" + MaxQueuedProfiles + "): netId=" + netId + ", profile=" + profile.ProfileId
                + " is dropped and un-queued so the host's character list can re-request it");
            return;
        }

        if (!_queuedPlayers.Contains(netId))
        {
            _queuedPlayers.Add(netId);
        }
#if DEBUG
        _logger.LogInfo($"Queueing profile: {profile.Nickname}, {profile.ProfileId}");
#endif
        SpawnObject spawnObject = new(profile, position, isAlive, isAI, netId, firstId, firstOperationId, isZombie);
        spawnObject.QueuedBeforeWorld = gameWorld == null;
        spawnObject.QueuedAt = UnityEngine.Time.realtimeSinceStartup;
        spawnObject.RevivalClock.Restore(revivalState ?? new RevivalState { IsAlive = isAlive, BleedoutRemaining = -1f });
        spawnObject.IsStationary = isStationary;
        _pendingPlayerStates[netId] = spawnObject;
        if (controllerType != EHandsControllerType.None)
        {
            spawnObject.ControllerType = controllerType;
            if (itemId is not null)
            {
                spawnObject.ItemId = itemId;
            }
        }
        if (healthByteArray != null)
        {
            spawnObject.HealthBytes = healthByteArray;
        }
        _spawnQueue.Enqueue(spawnObject);
    }

    private async ValueTask<ObservedPlayer> SpawnObservedPlayer(SpawnObject spawnObject)
    {
        var isAi = spawnObject.IsAI;
        var profile = spawnObject.Profile;
        var position = spawnObject.Position;
        var netId = spawnObject.NetId;
        var firstId = spawnObject.CurrentId;
        var firstOperationId = spawnObject.FirstOperationId;
        var healthBytes = spawnObject.HealthBytes;
        var isZombie = spawnObject.IsZombie;

        // 处理玩家上的空字节
        if (!isAi && (spawnObject.HealthBytes == null || spawnObject.HealthBytes.Length == 0))
        {
            healthBytes = profile.Health.SerializeHealthInfo();
        }

        var gameWorld = Singleton<GameWorld>.Instance;

        if (isAi) // spawn underground until everyone has loaded the AI
        {
            position = new(0f, -5000f, 0f);
        }

        profile.SetSpawnedInSession((profile.Info.Side == EPlayerSide.Savage || FikaPlugin.Instance.Settings.PMCFoundInRaid) && isAi);
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

        var otherPlayer = await ObservedPlayer.CreateObservedPlayer(gameWorld, netId, position, Quaternion.identity, "Player",
            isAi ? "Bot_" : $"Player_{profile.Nickname}_", EPointOfView.ThirdPerson, profile, healthBytes, isAi,
            EUpdateQueue.Update, Player.EUpdateMode.Manual, Player.EUpdateMode.Auto,
            AppEnvironment.Config.CharacterController.ObservedPlayerMode,
            FikaGlobals.GetOtherPlayerSensitivity, FikaGlobals.GetOtherPlayerSensitivity, ObservedViewFilter.Default,
            firstId, firstOperationId, isZombie);

        if (otherPlayer == null)
        {
            return null;
        }

        FikaSingleton<IFikaNetworkManager>.Instance.ObservedPlayers.Add(otherPlayer);
#if DEBUG
        _logger.LogInfo($"SpawnObservedPlayer: {profile.GetCorrectedNickname()} spawning with NetId {netId} at [{position.x:F1}, {position.y:F1}, {position.z:F1}]");
#endif

        if (!Players.ContainsKey(netId))
        {
            Players.Add(netId, otherPlayer);
        }
        else
        {
            _logger.LogError($"Trying to add {otherPlayer.Profile.Nickname} to list of players but it was already there!");
        }

        if (!isAi)
        {
            HumanPlayers.Add(otherPlayer);
        }

        foreach (var player in Players.Values)
        {
            if (player.TryCast<ObservedPlayer>() == null)
            {
                continue;
            }

            var playerCollider = otherPlayer.GetCharacterControllerCommon().GetCollider();
            var otherCollider = player.GetCharacterControllerCommon().GetCollider();

            if (playerCollider != null && otherCollider != null)
            {
                PhysicsExtensions.IgnoreCollision(playerCollider, otherCollider);
            }
        }

        otherPlayer.InitObservedPlayer();

        var controllerType = spawnObject.ControllerType;
        var itemId = spawnObject.ItemId;
        var isStationary = spawnObject.IsStationary;
        if (controllerType != EHandsControllerType.None)
        {
            if (controllerType != EHandsControllerType.Empty && (itemId is null || itemId.Equals(new MongoID())))
            {
                _logger.LogError($"CreateLocalPlayer: ControllerType was not Empty but itemId was default! ControllerType: {controllerType}");
            }
            else
            {
                otherPlayer.SpawnHandsController(controllerType, itemId, isStationary);
            }
        }
        return otherPlayer;
    }

    private IEnumerator AddClientToBotEnemies(BotsController botController, LocalPlayer playerToAdd)
    {
        var coopGame = LocalGameInstance;
        _logger.LogInfo($"AddClientToBotEnemies: {playerToAdd.Profile.GetCorrectedNickname()}");
        while (coopGame.GameController.GameInstance.Status != GameStatus.Running && !botController.IsEnable)
        {
            yield return null;
        }

        while (botController.BotSpawner == null)
        {
            yield return null;
        }

#if DEBUG
        _logger.LogInfo($"Adding Client {playerToAdd.Profile.GetCorrectedNickname()} to enemy list");
#endif
        botController.AddActivePLayer(playerToAdd);

        var found = false;

        for (var i = 0; i < botController.BotSpawner.PlayersCount; i++)
        {
            if (botController.BotSpawner.GetPlayer(i) == playerToAdd)
            {
                found = true;
                break;
            }
        }

        if (found)
        {
#if DEBUG
            _logger.LogInfo($"Verified that {playerToAdd.Profile.GetCorrectedNickname()} was added to the enemy list.");
#endif
            yield break;
        }

        _logger.LogError($"Failed to add {playerToAdd.Profile.GetCorrectedNickname()} to the enemy list.");
    }

    public void CheckIds(List<int> playerIds, List<int> missingIds)
    {
        foreach (var netId in playerIds)
        {
            if (!_queuedPlayers.Contains(netId) && !Players.ContainsKey(netId))
            {
                missingIds.Add(netId);
            }
        }
    }

    /// <summary>
    /// 你的角色或游戏为退出（Quit）所处的状态。
    /// </summary>
    public enum EQuitState
    {
        None = -1,
        Dead,
        Extracted
    }

    public sealed class SpawnObject(Profile profile, Vector3 position, bool isAlive, bool isAI, int netId, MongoID currentId, ushort firstOperationId, bool isZombie)
    {
        public Profile Profile = profile;
        public Vector3 Position = position;
        public bool IsAlive = isAlive;
        public bool IsAI = isAI;
        public int NetId = netId;
        public MongoID CurrentId = currentId;
        public ushort FirstOperationId = firstOperationId;
        public EHandsControllerType ControllerType;
        public MongoID ItemId;
        public bool IsStationary;
        public byte[] HealthBytes;
        public bool IsZombie = isZombie;
        internal readonly RevivalStateClock RevivalClock = new();
        internal readonly List<byte[]> PendingPackets = [];
        internal bool DeathPending;
        /// <summary>当该 profile 在游戏世界存在之前就被排队时为 true（见 QueueProfile）。</summary>
        internal bool QueuedBeforeWorld;
        /// <summary>它被排队的时刻，供生成泵写出的 [LoadSync] 行使用。</summary>
        internal float QueuedAt;
    }
}

