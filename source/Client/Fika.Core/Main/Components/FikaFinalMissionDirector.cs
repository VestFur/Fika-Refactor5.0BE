using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using CommonAssets.Scripts.Cutscenes;
using EFT;
using EFT.GlobalEvents;
using EFT.Interactive;
using EFT.Quests;
using EFT.UI;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.ObservedClasses;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Main.Components;

/// <summary>
/// 每个绑定突袭一个任务。主机掌管席位、区域归属与结果；
/// 每名玩家自行播放并保存自己的结局。客户端从不提供退出状态。
/// </summary>
internal static class FikaFinalMissionDirector
{
    /// <summary>从突袭开始到地图的突袭开场剧情场景之间的帧数。</summary>
    private const int IntroDelayFrames = 120;

    // (analysis/terminal-ending-settlement) 个人结局的时钟及其判定。该结局是本机独有的
    // 客户端过场（没有 CutsceneProcess），因此能报告它的只有两个原生信号——
    // 时间线的退出动作与观看器完成——这是它唯一能宣告自己已结束的途径，而
    // 在它开启期间 DeferStop 会拦住该游戏的其他所有停止。下面的常量给这段等待设了上界，
    // 以免丢失一个信号就把突袭冻结在结局画面上。
    /// <summary>结局场景出现之前最多等待多久，超时后导演就自行进入其播放。</summary>
    private const float EndingPlayGraceSeconds = 2f;

    /// <summary>两次尝试抢救结局场景之间的秒数。</summary>
    private const float EndingRescueRetrySeconds = 2f;

    /// <summary>导演在放弃之前最多进入结局播放多少次。</summary>
    private const int EndingRescueMaxAttempts = 3;

    /// <summary>两次采样结局场景之间的秒数。</summary>
    private const float EndingSampleSeconds = 0.25f;

    /// <summary>停止推进的场景可以停留多久，之后才把结局视为已结束。</summary>
    private const float EndingStoppedGraceSeconds = 3f;

    /// <summary>场景结束后、导演定论之前等待原生完成多久。</summary>
    private const float EndingSettleGraceSeconds = 3f;

    /// <summary>这么多秒内结局没有播放过任何一帧：无论如何都要把场景撤下。</summary>
    private const float EndingWithoutSceneSeconds = 30f;

    /// <summary>整个结局的硬性截止时间：超过之后就用主机发来的结果结算突袭。</summary>
    private const float EndingHardDeadlineSeconds = 45f;

    /// <summary>对于不会结束的结局，突袭停止最多可被推迟多久。</summary>
    private const float EndingDeferCapSeconds = 90f;

    /// <summary>对主机尚未回应的完成进行两次确认之间的秒数。</summary>
    private const float EndingCompleteRetrySeconds = 5f;

    /// <summary>正在播放的结局场景两次进度日志之间的秒数。</summary>
    private const float EndingProgressSeconds = 5f;

    /// <summary>一个结局最多写出多少条进度日志。</summary>
    private const int EndingProgressLines = 12;

    /// <summary>
    /// (analysis/terminal-ending-settlement) 整个结局修复的单一开关：跟随本机的个人结局，
    /// 用它的两个原生 id 中任一者接受其完成，通过原生停止把一个已结束（或从未播放）的
    /// 场景收尾，重复一次未被确认的完成，并为 <see cref="DeferStop"/> 对该游戏其他所有
    /// 停止施加的拦截设定上界。设为 <c>false</c> 时恰好保留原有行为：结局照旧启动，
    /// 且只有那两个原生信号能将它结束。它被有意设为 <c>static bool</c> 而非
    /// <c>const</c>——与过场桥接的各个开关一样——因此回滚只需一次赋值
    /// （或由主 agent 接上的一个配置键）。
    /// </summary>
    // (2026-10-18 refactor W3-finalmission) 结局修复层已关闭：周期性 watchdog、完成重试与
    // 场景抢救都由该开关控制；最终任务现在只依赖原生信号运行。
    internal static bool EndingRepair = false;

    private sealed class Ending
    {
        internal FinalMissionPacket Packet;
        internal bool Completed;
    }

    private static readonly Dictionary<string, Ending> Endings = new(StringComparer.Ordinal);
    private static readonly Queue<(FinalMissionPacket Packet, float Received)> Incoming = new();
    private static readonly Dictionary<NetPeer, int> PendingDepartures = new();
    private static readonly Dictionary<string, Dictionary<string, EQuestStatus>> QuestStates = new(StringComparer.Ordinal);
    private static Dictionary<string, EQuestStatus> _sentQuests;
    private static HashSet<string> _relevantQuests;
    private static CoopHandler _handler;
    private static FinallExitZone _zone;
    private static MapTimelinesBank _bank;
    private static bool _timerBankMissingReported;
    private static Collider[] _colliders;
    private static WorldInteractiveObject[] _gates;
    private static bool _discovered, _introStarted, _armed, _departed, _localStarted, _localComplete;
    private static bool _exitAuthorized, _stopping, _localPrepared, _zoneRequested;
    private static int _frames, _nextOutcome;
    private static float _nextState, _nextError, _nextQuestState, _nextQuestRefresh;

    /// <summary>
    /// (perf 2026-10-14) CutsceneBoundary.Tick 驱动五个泵（adopt link / takeover / local playback /
    /// skip overlay / host playback repair），过去每帧由 TickMission 调用一次。方法
    /// 监视测得它平均每次 6.2 ms、约 20 次/秒，在 194 秒的突袭中占 25.5 秒（挂钟时间的 13%）——
    /// 而正是同一条链路让剧情过场出现卡顿。每个泵本就按各自的内部定时器轮询
    /// （playback 0.25 s、adopt link RetrySeconds），因此 10 Hz 的上限对逻辑没有可见影响。
    /// </summary>
    private const float CutsceneTickIntervalSeconds = 0.1f;

    private static float _nextCutsceneTick;

    /// <summary>
    /// (perf 2026-10-14, 实测) 只对 CutsceneBoundary.Tick 限流后，该方法在 184 秒的突袭中
    /// 用时仍有 22,529 ms / 5,151 次调用（单次最差 571.6 ms）——即每次 4.4 ms，占挂钟时间 12%。
    /// 方法体的其余部分是重试/watchdog 工作：剧情开场驱动、最终任务队列排空，以及
    /// 主机侧的 arming 重试。按设计这些全部都是“重试到成功为止”，而
    /// 下面任何地方最快的内部节奏是 0.25 s 的播放泵，所以 10 Hz 不会损失任何东西，还能去掉
    /// 那笔开销的大部分。
    /// </summary>
    private const float MissionTickIntervalSeconds = 0.1f;

    private static float _nextMissionTick;
    private static float _evacuationDuration, _evacuationElapsed;
    private static FinalMissionPacket? _localEnding;
    private static EFinalExitStatus? _localEligibility;
    private static bool _localZoneUi;
    // (analysis/terminal-ending-settlement) 结局链路自身的状态：结局何时交给
    // 本机的观看器、它的场景是否推进过任何一帧、何时停止，以及哪些兜底
    // 已经执行过。每一项都回答了报告中的一个问题——画面停在结局画面上、
    // 什么都不结算——而无需再跑一次突袭来复现它。
    private static float _localEndingAt, _endingStoppedAt, _nextEndingSample, _nextEndingRescue, _nextEndingProgress, _localCompleteAt, _nextCompleteRetry;
    private static double _localEndingSceneTime = -1d;
    private static bool _endingPlayed, _endingVisible, _endingRescued, _endingNoScene, _endingSceneOver, _endingSettledByWatchdog;
    private static int _endingRescueAttempts, _endingProgressWritten;
    private static string _endingScene;
    private static GameWorld World => RaidInteractionSync.World;
    private static IFikaNetworkManager Network => FikaSingleton<IFikaNetworkManager>.Instance;
    private static CoopHandler Handler => _handler ??= Network?.CoopHandler;
    internal static bool Bound => World != null;

    /// <summary>当本机播放自己的结局（其结果已到达并在此处启动）时为 true。</summary>
    internal static bool HasLocalEnding => _localEnding.HasValue && _localStarted;
    internal static bool Owns(FinallExitZone zone) => Bound && (_zone == null || _zone.Pointer == zone.Pointer);

    /// <summary>
    /// 当导演自行运行该区域的时钟时为 true：在主机上完成 arming 之后，或在
    /// 对等端收到主机的状态包之后。它为 false 时，原生区域更新不受干扰，
    /// 因此导演无法接管的区域仍由游戏驱动，而不是无人驱动。
    /// </summary>
    internal static bool DrivesZone(FinallExitZone zone)
    {
        if (!Bound || zone == null) return false;
        if (_zone != null && _zone.Pointer != zone.Pointer) return false;
        // 对等端从不自行驱动任务：席位、时钟与结果都经由主机的
        // FinalMissionPacket(State) 流传入，因此它的原生更新保持被抑制。
        return !FikaBackendUtils.IsServer || _armed || _departed;
    }

    internal static void Reset()
    {
        Endings.Clear();
        Incoming.Clear();
        PendingDepartures.Clear();
        QuestStates.Clear();
        _sentQuests = null;
        _relevantQuests = null;
        _handler = null;
        _zone = null;
        _bank = null;
        _colliders = null;
        _gates = null;
        _localEnding = null;
        _localEligibility = null;
        _localZoneUi = false;
        _discovered = _introStarted = _armed = _departed = _localStarted = _localComplete = false;
        _exitAuthorized = _stopping = _localPrepared = _zoneRequested = false;
        
        _frames = _nextOutcome = 0;
        _nextState = _nextError = _nextQuestState = _nextQuestRefresh = 0;
        _nextCutsceneTick = 0f;
        _evacuationDuration = _evacuationElapsed = 0;
        _localEndingAt = _endingStoppedAt = 0;
        _nextEndingSample = _nextEndingRescue = _nextEndingProgress = _localCompleteAt = _nextCompleteRetry = 0;
        _localEndingSceneTime = 0d;
        _endingPlayed = _endingVisible = _endingRescued = _endingNoScene = _endingSceneOver = false;
        _endingSettledByWatchdog = false;
        _endingRescueAttempts = _endingProgressWritten = 0;
        _endingScene = null;
        
        CutsceneBoundary.Reset();
    }

    internal static void Receive(FinalMissionPacket packet)
    {
        if (Incoming.Count >= 256) throw new InvalidOperationException("Final mission startup queue exceeded its limit");
        Incoming.Enqueue((packet, Time.unscaledTime));
    }

    internal static void Tick()
    {
        if (!Bound || !RaidInteractionSync.Ready || Handler?.MyPlayer == null || !Handler.ShouldSync ||
            Handler.LocalGameInstance is not CoopGame game || game.Status != GameStatus.Started || _stopping) return;
        try { TickMission(game); }
        catch (Exception error)
        {
            // 为重试保留状态，但不要每帧都刷日志。
            if (Time.unscaledTime >= _nextError)
            {
                _nextError = Time.unscaledTime + 5;
                FikaGlobals.LogError($"Final mission update failed: {error}");
            }
        }
    }

    private static void TickMission(CoopGame game)
    {
        if (!_discovered)
        {
            _zone = UnityEngine.Object.FindObjectOfType<FinallExitZone>();
            _bank = UnityEngine.Object.FindObjectOfType<MapTimelinesBank>();
            _discovered = true;
            if (_zone != null)
            {
                _colliders = _zone.GetComponentsInChildren<Collider>(true).ToArray();
                _gates = UnityEngine.Object.FindObjectsOfType<WorldInteractiveObject>(true)
                    .Where(door => door.KeyId == "6866adbe09b973bf45094339").ToArray();
            }
        }
        // (analysis/icebreaker-cutscene) 在 Fika 突袭中，原生过场控制器创建时没有带上它们的 timeline
        // bank；在任何东西读取它之前修复这个链接，这样剧情标记就有一个能够
        // 真正解析其过场的控制器——也让客户端控制器至少能解码出一条 timeline。
        // (perf 2026-10-14) 已还原为原来的每帧调用：10 Hz 限流既没有改善
        // 卡顿，反而挤掉了过场跳过遮罩（下面的某个泵），所以原版节奏胜出。
        CutsceneBoundary.Tick(World);
        AdoptTimelinesBank();
        _frames++;
        // 突袭开场的剧情场景属于地图标记，而不属于最终撤离：在
        // 最终任务网关之前驱动它，这样没有 FinallExitZone 的剧情地图也照样播放它。
        // (perf 2026-10-14) 已还原为原来的每帧方法体：把整个 tick 限流到 10 Hz
        // 并未减少每秒的卡顿，还以同样方式延迟了屏幕上的任务 UI。
        DriveStoryIntro();
        if (World.CutscenesClientController?._timelinesMapBank == null)
        {
            return;
        }
        if (_zone == null || _bank == null)
        {
            return;
        }
        
        SyncQuestStates();
        while (Incoming.TryPeek(out var message))
        {
            var packet = message.Packet;
            if (FikaBackendUtils.IsClient && packet.Message == FinalMissionMessage.State)
                packet.Remaining = Math.Max(0, packet.Remaining - (Time.unscaledTime - message.Received));
            Apply(packet);
            Incoming.Dequeue();
        }
        if (FikaBackendUtils.IsServer)
        {
            if (World.CutscenesServerController?._timelinesMapBank == null) return;
            var players = Participants();
            // 原生区域初始化会请求 arm 该区域，但它可能在突袭
            // 开始之前、或在注册到第一名玩家之前运行；该请求在此重试，使得
            // 任务不会因为某个钩子触发过早而一直处于未 arm 状态。
            if (!_armed && _zoneRequested) TryArm("native-zone-init retry");
            if (!_armed && _introStarted && (_gates.Any(gate => gate != null && gate.DoorState == EDoorState.Open) ||
                (_gates.Length == 0 && players.Any(InZone)))) TryArm("gate-open");
            if (_armed && !_departed)
            {
                // 原生 UI 初始化也会写这些区域字段。把权威时钟
                // 单独保存，这样延迟的 UI 协程
                // 就无法缩短/延长主机的任务计时器。
                _evacuationElapsed += Time.deltaTime;
                _zone.evacuateTimer = _evacuationDuration;
                _zone._evacuateTimerElapsed = _evacuationElapsed;
                var eligible = players.Count(player => InZone(player) && HasRequiredItems(player));
                if (Time.unscaledTime >= _nextState)
                {
                    _nextState = Time.unscaledTime + 1;
                    foreach (var player in players) SendState(player, eligible);
                }
                if (_zone._waitingEvacuateTimeLeft || _evacuationElapsed >= _evacuationDuration)
                    Depart(ConnectedHumans(), eligible);
            }
        }
        else if (_armed && !_departed)
            _zone._evacuateTimerElapsed = Math.Min(_zone.evacuateTimer, _zone._evacuateTimerElapsed + Time.deltaTime);

        if (_localEnding.HasValue && !_localStarted) StartLocalEnding();
        // (2026-10-26 W18, 从上游 3.0.4 恢复) 结局修复层曾被 W3-finalmission 移除
        // 接线：开关留了下来，它守护的行为却没有。现已逐字恢复，仍
        // 由 EndingRepair 守护（配置 'Final Mission: Ending Repair'，默认 true）：
        //   * WatchLocalEnding - 本机自己的结局会被跟随直到真正完成，这样一个丢失的
        //     原生信号就不会把突袭留在结局画面上；
        //   * 完成时执行 PrepareLocal - 结局一完成就准备好本地退出路径；
        //   * RetryCompletion - 丢失的 ExitAuthorized 确认会被重发（主机处理器
        //     对同一结果 id 是幂等的），而不是永远等一个回答。
        if (EndingRepair && _localStarted && !_localComplete) WatchLocalEnding();
        if (_localComplete) PrepareLocal(game);
        if (EndingRepair && _localComplete && !_exitAuthorized)
        {
            var retryNow = Time.unscaledTime;
            if (retryNow >= _nextCompleteRetry)
            {
                _nextCompleteRetry = retryNow + EndingCompleteRetrySeconds;
                RetryCompletion();
            }
        }
        // (analysis/terminal-ending-settlement) 本机的结局从这里开始被跟随：能报告其结束的
        // 那些原生信号是它另一种可能的结束方式，而一个丢失的信号绝不能让
        // 突袭停在结局画面上（见 WatchLocalEnding）。
        if (FikaBackendUtils.IsClient && _localComplete &&
            FikaSingleton<FikaClient>.Instance?.ServerConnection?.ConnectionState != ConnectionState.Connected)
            _exitAuthorized = true; // Save the already-issued host outcome after connection loss.
        if (FikaBackendUtils.IsServer && _departed && (FikaBackendUtils.IsHeadless || _localComplete))
        {
            // 等客户端在自行保存/退出之后离开。仅有一个确认
            // 并不证明主机的可靠响应已经到达。
            foreach (var peer in PendingDepartures.Keys.ToArray())
                if (peer.ConnectionState != ConnectionState.Connected) PendingDepartures.Remove(peer);
            if (PendingDepartures.Count == 0)
            {
                if (FikaBackendUtils.IsHeadless && !_localEnding.HasValue)
                {
                    _localEnding = new FinalMissionPacket { ExitStatus = ExitStatus.Survived };
                    PrepareLocal(game);
                }
                _exitAuthorized = true;
            }
        }
        if (_exitAuthorized && _localEnding is { } ending)
        {
            _stopping = true;
            if (FikaBackendUtils.IsClient && FikaSingleton<FikaClient>.Instance?.ServerConnection?.ConnectionState == ConnectionState.Connected)
                Network.SendGenericPacket(EGenericSubPacketType.ClientExtract, ClientExtract.FromValue(Handler.MyPlayer.NetId), true);
            
            game.Stop(Handler.MyPlayer.ProfileId, ending.ExitStatus, FinallExitZone.FINAL_EXIT_NAME, 0);
        }
    }

    private static List<NetPeer> ConnectedPeers()
    {
        var peers = new List<NetPeer>();
        FikaSingleton<FikaServer>.Instance?.NetServer.GetConnectedPeers(peers);
        return peers.Where(peer => peer.ConnectionState == ConnectionState.Connected).ToList();
    }

    private static FikaPlayer[] ConnectedHumans()
    {
        var peers = ConnectedPeers();
        return Handler.HumanPlayers.Where(player => player != null &&
            !Handler.ExtractedPlayers.Contains(player.NetId) &&
            (player.IsYourPlayer ? !FikaBackendUtils.IsHeadless : peers.Any(peer => peer.Player?.NetId == player.NetId))).ToArray();
    }

    private static FikaPlayer[] Participants() => ConnectedHumans().Where(player => player.HealthController?.IsAlive == true).ToArray();

    private static Il2CppSystem.Collections.Generic.List<int> PlayerIds(IEnumerable<FikaPlayer> players)
    {
        var ids = new Il2CppSystem.Collections.Generic.List<int>();
        foreach (var player in players) ids.Add(player.PlayerId);
        return ids;
    }

    /// <summary>
    /// (2026-10-17) 原生过场控制器创建时没有带上它们的 timeline bank（由 CutsceneBoundary
    /// 创建，并有意不运行私有的原生 Init），而桥接自身的采纳
    /// 在加载过程中的三次尝试后即停止。因此，<see cref="MapTimelinesBank"/> 稍后
    /// 才流入的地图永远得不到该链接，而该地图的原生 <c>StartCutscene</c>——即 Terminal
    /// 抵达场景——每次尝试都抛出 NullReferenceException（2026-10-17 主机日志中 309 次），
    /// 而它已经切换到的那块屏幕一直是黑的。本导演解析到的 bank 在此
    /// 交给两个控制器，操作幂等，且仅在控制器尚无 bank 时执行。
    /// </summary>
    private static void AdoptTimelinesBank()
    {
        if (_bank == null) return;
        try
        {
            var server = World.CutscenesServerController;
            if (server != null && server._timelinesMapBank == null)
            {
                server._timelinesMapBank = _bank;
                FikaGlobals.LogWarning("[CutsceneBank] the server cutscene controller had no timeline bank; linked the map's bank");
            }

            var clientController = World.CutscenesClientController;
            if (clientController != null && clientController._timelinesMapBank == null)
            {
                clientController._timelinesMapBank = _bank;
                FikaGlobals.LogWarning("[CutsceneBank] the client cutscene controller had no timeline bank; linked the map's bank");
            }
        }
        catch (Exception error)
        {
            FikaGlobals.LogWarning("[CutsceneBank] linking the map's timeline bank failed: " + error.Message);
        }
    }

    internal static void TickTimer(StartCutsceneByTimer timer)
    {
        if (!FikaBackendUtils.IsServer || !timer.timerOn || Handler?.ShouldSync != true ||
            Handler.LocalGameInstance is not CoopGame game || game.Status != GameStatus.Started) return;
        timer._timeElapsed += Time.deltaTime;
        if (timer._timeElapsed < timer.time) return;
        var ids = PlayerIds(Participants());
        if (ids.Count == 0 || World.CutscenesServerController == null) return;
        // (2026-10-17) 没有 timeline bank 时，原生 StartCutscene 会在
        // IL2CPP 运行时内部抛出 NullReferenceException（2026-10-17 主机日志中有 309 次），而它被
        // 调用所对应的过场屏幕会一直黑着。一旦地图带上 bank，就由 AdoptTimelinesBank 完成链接；
        // 在此之前，定时器就是不会触发。
        if (World.CutscenesServerController._timelinesMapBank == null)
        {
            if (!_timerBankMissingReported)
            {
                _timerBankMissingReported = true;
                FikaGlobals.LogWarning("[CutsceneBank] the timer-driven cutscene was not started: the server controller has no timeline bank yet");
            }

            return;
        }
        if (World.CutscenesServerController.StartCutscene(timer.cutsceneId, ids, true, true, 0) == null) return;
        timer.timerOn = false;
        timer._timeElapsed = 0;
        timer.gameObject.SetActive(false);
    }

    /// <summary>
    /// 突袭开场的剧情场景。地图用 <see cref="StartCutsceneByStartRaid"/> 标记它，
    /// 而单人驱动只在 Terminal 上遵循该标记；Fika 突袭替换了那个
    /// 驱动，因此凡是携带它的地图都会遵循该标记。仅限主机：timeline bank
    /// 与服务器控制器掌控该流程，每个对等端都会从它那里收到一个
    /// CutsceneStartPacket。
    /// </summary>
    private static void DriveStoryIntro()
    {
        if (_introStarted || !FikaBackendUtils.IsServer) return;
        if (World.CutscenesServerController?._timelinesMapBank == null)
        {
            
            return;
        }
        if (_frames < IntroDelayFrames) return;
        var marker = UnityEngine.Object.FindObjectOfType<StartCutsceneByStartRaid>();
        if (marker == null)
        {
            _introStarted = true;
            
            return;
        }
        if (_bank == null)
        {
            
            return;
        }
        if (_bank.timelines.Count == 0)
        {
            
            return;
        }
        var players = Participants();
        if (players.Length == 0)
        {
            
            return;
        }
        var cutscene = _bank.timelines[0].cutsceneId;
        World.CutscenesServerController._lagTimeCompensation = Math.Max(2,
            Singleton<GlobalConfiguration>.Instance.FinalMissionSettings.LagTimeCompensation);
        var process = World.CutscenesServerController.StartCutscene(cutscene, PlayerIds(players), false, false, 0);
        if (process == null)
        {
            
            return;
        }
        _introStarted = true;
        
    }

    /// <summary>
    /// 由被替换的原生 <see cref="FinallExitZone.InitZoneServer"/> 钩子调用。即便区域
    /// 尚无法 arm，该请求也会被记录下来——地图早在第一名玩家注册之前
    /// 就请求它的区域——<see cref="TickMission"/> 会重试它。仅当导演拥有该区域时
    /// 才返回 true，这样调用者就知道是否要抑制原生方法体。
    /// </summary>
    internal static bool Arm(FinallExitZone zone)
    {
        if (zone != null)
        {
            _zone = zone;
            _zoneRequested = true;
        }
        return TryArm("native-zone-init");
    }

    private static bool TryArm(string source)
    {
        if (_armed) return true;
        if (!FikaBackendUtils.IsServer)
        {
            
            return false;
        }
        if (Handler?.ShouldSync != true)
        {
            
            return false;
        }
        if (_zone is not { } zone)
        {
            
            return false;
        }
        var settings = Singleton<GlobalConfiguration>.Instance.FinalMissionSettings;
        var count = Participants().Length;
        if (count == 0)
        {
            
            return false;
        }
        zone._playersToEvacuateRandom = settings.SeatsRandom;
        zone._minPLayersToEvacuate = settings.MinPlayersToEvac;
        zone._playersToEvacuate = Math.Min(count, Math.Max(settings.MinPlayersToEvac,
            count - (int)UnityEngine.Random.Range(settings.SeatsRandom.x, settings.SeatsRandom.y)));
        var configuredEvacuationTime = Math.Max(0, settings.EvacuateTime);
        // 先写入原生字段：GetEvacuateTimerByEndRaid() 在应用
        // 突袭结束边界时会使用已配置的撤离时长。
        zone.evacuateTimer = configuredEvacuationTime;
        // 最终任务计时器不可超出原生突袭结束时间。
        // GetEvacuateTimerByEndRaid() 是剩余突袭时间，而不是对
        // 所配置最终任务时长的替代。
        var remainingRaidTime = Math.Max(0, zone.GetEvacuateTimerByEndRaid());
        zone.evacuateTimer = Math.Min(configuredEvacuationTime, remainingRaidTime);
        if (settings.FinalCutscenesByQuest != null)
        {
            zone.finalCutscenesVariants = new();
            foreach (var variant in settings.FinalCutscenesByQuest) zone.finalCutscenesVariants.Add(variant);
        }
        zone.BuildCutsceneMapping();
        _evacuationDuration = zone.evacuateTimer;
        _evacuationElapsed = 0;
        zone._evacuateTimerElapsed = 0;
        zone._evacuateInitialized = zone._waitingEvacuateTimeLeft = false;
        zone._zoneActive = true;
        _armed = true;
        FikaGlobals.LogInfo($"Final mission armed: {count} players, {zone._playersToEvacuate} seats, {zone.evacuateTimer}s");
        
        return true;
    }

    private static bool InZone(FikaPlayer player)
    {
        if (_colliders == null) _colliders = _zone.GetComponentsInChildren<Collider>(true).ToArray();
        // ClosestPoint 也能处理旋转的/非盒形触发形状，而不会
        // 引入它们世界空间轴对齐包围盒中的空角。
        return _colliders.Any(collider => collider != null && collider.enabled &&
            (collider.ClosestPoint(player.Position) - player.Position).sqrMagnitude < 0.01f);
    }

    private static bool ActiveQuest(FikaPlayer player, string questId)
    {
        if (string.IsNullOrEmpty(questId)) return true;
        if (!player.IsYourPlayer && QuestStates.TryGetValue(player.ProfileId, out var statuses))
            // 该报告是本地图相关任务的一份完整快照。
            // 缺失的任务表示客户端未接受它；在一次突袭中的任务变更之后，
            // 主机持有的该客户端档案副本可能已经过时。
            return statuses.TryGetValue(questId, out var status) &&
                status is EQuestStatus.Started or EQuestStatus.AvailableForFinish;
        foreach (var quest in player.Profile.QuestsData)
            if (quest.Id == questId) return quest.Status is EQuestStatus.Started or EQuestStatus.AvailableForFinish;
        return false;
    }

    private static void SyncQuestStates()
    {
        if (_relevantQuests == null)
        {
            _relevantQuests = new(StringComparer.Ordinal);
            var variants = Singleton<GlobalConfiguration>.Instance.FinalMissionSettings.FinalCutscenesByQuest;
            if (variants != null)
                foreach (var variant in variants) if (!string.IsNullOrEmpty(variant.questId)) _relevantQuests.Add(variant.questId);
            if (_zone.finalCutscenesVariants != null)
                foreach (var variant in _zone.finalCutscenesVariants) if (!string.IsNullOrEmpty(variant.questId)) _relevantQuests.Add(variant.questId);
            if (_zone.questItemsRequirements != null)
                foreach (var requirement in _zone.questItemsRequirements) if (!string.IsNullOrEmpty(requirement.questId)) _relevantQuests.Add(requirement.questId);
        }
        if (!FikaBackendUtils.IsClient || Time.unscaledTime < _nextQuestState || _departed ||
            FikaSingleton<FikaClient>.Instance?.ServerConnection?.ConnectionState != ConnectionState.Connected) return;
        _nextQuestState = Time.unscaledTime + 1;
        var statuses = new Dictionary<string, EQuestStatus>(StringComparer.Ordinal);
        foreach (var quest in Handler.MyPlayer.Profile.QuestsData)
            if (_relevantQuests.Contains(quest.Id)) statuses[quest.Id] = quest.Status;
        if (Time.unscaledTime < _nextQuestRefresh && _sentQuests != null && _sentQuests.Count == statuses.Count &&
            statuses.All(quest => _sentQuests.TryGetValue(quest.Key, out var status) && status == quest.Value)) return;
        var packet = new FinalMissionQuestPacket { NetId = Handler.MyPlayer.NetId, Statuses = statuses };
        Network.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        _sentQuests = statuses;
        // 可靠包可能在主机的世界/对等端就绪之前到达。
        // 即使没有任务变化，也要周期性重发这份有界快照。
        _nextQuestRefresh = Time.unscaledTime + 5;
    }

    internal static void ReceiveQuestStates(FinalMissionQuestPacket packet, FikaPlayer player)
    {
        if (!FikaBackendUtils.IsServer || player == null || packet.NetId != player.NetId || _departed ||
            _relevantQuests == null || packet.Statuses == null) return;
        if (packet.Statuses.Any(quest => !_relevantQuests.Contains(quest.Key))) return;
        // 在 Fika 中任务进度由客户端掌管。把报告放在作用域内的
        // 表中；绝不要改动被观察的档案，也不要相信客户端提供的结果。
        QuestStates[player.ProfileId] = packet.Statuses;
    }

    private static string VariantFor(FikaPlayer player)
    {
        // 与原生 GetVariantCutsceneId 保持一致：按档案顺序取第一个
        // Started/AvailableForFinish 且 ID 在地图过场映射中的任务。
        foreach (var quest in player.Profile.QuestsData)
            if (ActiveQuest(player, quest.Id) && _zone._cutsceneByQuest.TryGetValue(quest.Id, out var cutscene)) return cutscene;
        // 本次突袭中接受的任务可能不在主机的初始档案里。
        if (!player.IsYourPlayer && QuestStates.TryGetValue(player.ProfileId, out var reported))
            foreach (var quest in reported)
                if ((quest.Value is EQuestStatus.Started or EQuestStatus.AvailableForFinish) &&
                    _zone._cutsceneByQuest.TryGetValue(quest.Key, out var cutscene)) return cutscene;
        return null;
    }

    private static bool HasRequiredItems(FikaPlayer player)
    {
        if (_zone.questItemsRequirements == null) return true;
        foreach (var requirement in _zone.questItemsRequirements)
        {
            if (!ActiveQuest(player, requirement.questId) || string.IsNullOrEmpty(requirement.itemId)) continue;
            var items = player.Profile.Inventory.GetAllItemByTemplate(requirement.itemId);
            if (items?.GetEnumerator()?.MoveNext() == true) continue;
            // 任务拾取物是每名玩家的私有数据，通过
            // QuestSyncPacket 同步，而不通过观察到的装备库存操作。
            if (player.FikaQuests is ObservedQuestController observed && observed.HasCollectedQuestItem(requirement.itemId)) continue;
            return false;
        }
        return true;
    }

    private static EFinalExitStatus Eligibility(FikaPlayer player, int eligible) =>
        !InZone(player) ? EFinalExitStatus.notInZone : !HasRequiredItems(player) ? EFinalExitStatus.noItem :
        eligible > _zone._playersToEvacuate ? EFinalExitStatus.overCount : EFinalExitStatus.available;

    private static void SendState(FikaPlayer player, int eligible, NetPeer peer = null)
    {
        var packet = new FinalMissionPacket { Message = FinalMissionMessage.State, NetId = player.NetId,
            Seats = _zone._playersToEvacuate, EligiblePlayers = eligible,
            Remaining = Math.Max(0, _evacuationDuration - _evacuationElapsed), Eligibility = Eligibility(player, eligible) };
        Send(player, packet, peer);
    }

    private static void Send(FikaPlayer player, FinalMissionPacket packet, NetPeer peer = null)
    {
        if (player.IsYourPlayer) { Receive(packet); return; }
        peer ??= ConnectedPeers().FirstOrDefault(candidate => candidate.Player?.NetId == player.NetId);
        if (peer != null) FikaSingleton<FikaServer>.Instance.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
    }

    private static void Depart(FikaPlayer[] players, int eligible)
    {
        if (!FikaBackendUtils.IsHeadless && !players.Any(player => player.IsYourPlayer))
            players = players.Append(Handler.MyPlayer).ToArray();
        var decisions = new Dictionary<string, Ending>(StringComparer.Ordinal);
        // 先确定所有结果，之后再调用任何本地的原生结局/动作。
        foreach (var player in players)
        {
            var alive = player.HealthController?.IsAlive == true;
            var cutscene = alive && Eligibility(player, eligible) == EFinalExitStatus.available ? VariantFor(player) : null;
            var status = !alive ? ExitStatus.Killed : string.IsNullOrEmpty(cutscene) ? ExitStatus.MissingInAction : EndingStatus(cutscene);
            decisions.Add(player.ProfileId, new Ending { Packet = new FinalMissionPacket {
                Message = FinalMissionMessage.Outcome, NetId = player.NetId, OutcomeId = ++_nextOutcome,
                CutsceneId = cutscene ?? string.Empty, ExitStatus = status } });
        }
        foreach (var decision in decisions) Endings.Add(decision.Key, decision.Value);
        foreach (var peer in ConnectedPeers())
            if (peer.Player != null && decisions.ContainsKey(peer.Player.ProfileId)) PendingDepartures[peer] = peer.Player.NetId;
        _zone._evacuateInitialized = _zone._waitingEvacuateTimeLeft = true;
        _departed = true;
        foreach (var player in players) Send(player, Endings[player.ProfileId].Packet);
    }

    private static ExitStatus EndingStatus(string cutscene)
    {
        foreach (var timeline in _bank.timelines)
        {
            if (timeline.cutsceneId != cutscene) continue;
            foreach (var id in timeline.endActionsId ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(0))
                if (World.CutscenesClientController._actions.TryGetValue(id, out var action) &&
                    action.TryCast<CutsceneActionExitAfterCutscene>() is { } exit) return exit.ExitStatus;
        }
        // 若地图的退出动作缺失，不要凭空造出一个成功的存档。
        throw new InvalidOperationException($"Final cutscene '{cutscene}' has no registered exit action");
    }

    internal static bool SendSnapshot(NetPeer peer)
    {
        if (!_armed || peer.Player == null || _zone == null) return false;
        var player = peer.Player;
        SendState(player, Participants().Count(candidate => InZone(candidate) && HasRequiredItems(candidate)), peer);
        if (Endings.TryGetValue(player.ProfileId, out var ending))
        {
            PendingDepartures[peer] = player.NetId;
            ending.Packet.NetId = player.NetId;
            Send(player, ending.Packet, peer);
            // 重连的游戏需要在退出之前准备好它的本地状态。
            // 重复的 Complete 将在下面收到同样的授权。
        }
        else if (_departed)
        {
            // 飞船离开之后再重连，无法获得新的席位。
            var packet = new FinalMissionPacket { Message = FinalMissionMessage.Outcome, NetId = player.NetId,
                OutcomeId = ++_nextOutcome, CutsceneId = string.Empty,
                ExitStatus = player.HealthController?.IsAlive == true ? ExitStatus.MissingInAction : ExitStatus.Killed };
            Endings.Add(player.ProfileId, new Ending { Packet = packet });
            PendingDepartures[peer] = player.NetId;
            Send(player, packet, peer);
        }
        return _departed;
    }

    private static void Apply(FinalMissionPacket packet)
    {
        if (packet.NetId != Handler.MyPlayer.NetId) return;
        switch (packet.Message)
        {
            case FinalMissionMessage.State:
                if (!_localZoneUi)
                {
                    if (FikaBackendUtils.IsClient)
                    {
                        _zone.InitZoneClient(packet.Seats);
                        _zone.evacuateTimer = packet.Remaining;
                    }
                    // 原生 UI 协程会从这个事件写入 evacuateTimer。
                    // 保留主机的权威总时长与已用时间。
                    var duration = _zone.evacuateTimer;
                    GlobalEventsController.CreateEvent<FinalExitZoneInitEvent>().Invoke(packet.Seats, (int)Math.Ceiling(packet.Remaining));
                    if (FikaBackendUtils.IsServer) _zone.evacuateTimer = duration;
                    _localZoneUi = true;
                }
                _armed = true;
                _zone._playersToEvacuate = packet.Seats;
                if (FikaBackendUtils.IsClient)
                    _zone._evacuateTimerElapsed = Math.Max(0, _zone.evacuateTimer - packet.Remaining);
                if (_localEligibility != packet.Eligibility)
                {
                    GlobalEventsController.CreateEvent<ChangeFinalExitStateEvent>().Invoke(Handler.MyPlayer.PlayerId, packet.Eligibility);
                    _localEligibility = packet.Eligibility;
                }
                var panel = GameUI.Instance?.TimerPanel;
                // locked/condition 的 setter 通过面板自身的协程
                // 为其颜色做动画，因此绝不应驱动已关闭（未激活）的面板。
                if (panel?.FinalExitTimerIsExist() == true &&
                    FikaUiPanels.CanDrive(panel, "FikaFinalMissionDirector.FinalExitZoneText"))
                {
                    panel.SetFinalExitZoneConditionText($"{FinallExitZone.FINAL_EXIT_CONDITION_SEATS_COUNT.Localized()} {packet.Seats} " +
                        $"{FinallExitZone.FINAL_EXIT_CONDITION_SEATS_PLAYERS.Localized()} {packet.EligiblePlayers}");
                    panel.SetFinalExitZoneLockedText(packet.EligiblePlayers > packet.Seats);
                }
                break;
            case FinalMissionMessage.Outcome:
                if (_localEnding?.OutcomeId == packet.OutcomeId) { if (_localComplete) RequestCompletion(); return; }
                if (_localEnding.HasValue) throw new InvalidOperationException("Host changed a final mission outcome");
                _departed = true;
                _localEnding = packet;
                
                break;
            case FinalMissionMessage.ExitAuthorized:
                if (_localComplete && _localEnding?.OutcomeId == packet.OutcomeId) _exitAuthorized = true;
                break;
        }
    }

    private static void StartLocalEnding()
    {
        var ending = _localEnding.Value;
        _localEndingAt = Time.unscaledTime;
        _localEndingSceneTime = 0d;
        if (string.IsNullOrEmpty(ending.CutsceneId))
        {
            _localStarted = true;
            CompleteLocal("the outcome carries no ending scene, so there is nothing to play here");
            return;
        }
        // 原生最终结局是仅客户端的视图（没有服务器端 CutsceneProcess）。
        // 重连时从头播放，这样它的本地任务/动作效果也会执行。
        _localStarted = true;
        try
        {
            GlobalEventsController.CreateEvent<CutSceneStartEvent>().Invoke(ending.CutsceneId, 0,
                new[] { Handler.MyPlayer.PlayerId }, 0, false, true);
        }
        catch { _localStarted = false; throw; }
        // 此调用对应的原生实现是 FinallExitZone.ActivateCutsceneForPlayer，它播放完全相同的
        // 仅客户端事件；
    }

    /// <summary>
    /// 本机自己的观看器播放完了提供给它的那条 timeline（上报它的是原生
    /// <c>CutscenesClientController.TimelineShowed</c>）。结局可能送来两个 id：
    /// 结果包中的过场 id，以及该过场 timeline 条目的<b>场景</b>名——
    /// 该信号所走的原生事件携带的正是这一对（<c>SceneName</c> 加上进程 id，
    /// 而结局并不拥有进程），因此两种答案都被接受，而不是丢弃
    /// 一个确实结束了的场景的完成。
    /// </summary>
    internal static void LocalTimelineComplete(string cutscene)
    {
        if (!_localStarted || _localEnding is not { } ending) return;
        if (string.Equals(ending.CutsceneId, cutscene, StringComparison.Ordinal))
        {
            CompleteLocal("the timeline of the ending was shown on this machine");
            return;
        }
        var scene = EndingRepair ? SceneOfEnding(ending.CutsceneId) : null;
        if (!string.IsNullOrEmpty(scene) && string.Equals(scene, cutscene, StringComparison.Ordinal))
            CompleteLocal("the timeline of the ending was shown on this machine (reported by the scene name '" + cutscene + "')");
    }

    /// <summary>结局的 timeline 条目在地图 timeline bank 中的场景名，或 null。</summary>
    private static string SceneOfEnding(string cutscene)
    {
        if (string.IsNullOrEmpty(cutscene)) return null;
        var controller = World?.CutscenesClientController;
        try
        {
            if (string.Equals(controller?._currentTimelineData?.cutsceneId, cutscene, StringComparison.Ordinal))
                return controller._currentTimelineData.sceneName;
        }
        catch (Exception) { }
        try
        {
            var timelines = _bank?.timelines ?? controller?._timelinesMapBank?.timelines;
            for (var i = 0; i < (timelines?.Count ?? 0); i++)
                if (string.Equals(timelines[i].cutsceneId, cutscene, StringComparison.Ordinal)) return timelines[i].sceneName;
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>
    /// (analysis/terminal-ending-settlement) 结局链路的 watchdog。
    ///
    /// 结局是本机特有的仅客户端过场（<see cref="StartLocalEnding"/>）：没有
    /// CutsceneProcess 驱动它，所以过场桥接中所有基于进程的修复都退让，而能够
    /// 将它报告为结束的两个原生信号是时间线的退出动作（SPT 的 ClientInitActionPatch ->
    /// <see cref="LocalExitAction"/>）与观看器完成（TimelineShowed ->
    /// <see cref="LocalTimelineComplete"/>）。2026-10-13 的那次突袭正是两者都没有
    /// 落地时所发生的情形：结果已宣告，结局画面仍在屏幕上，什么都不结算——因为
    /// 在结局开启期间 <see cref="DeferStop"/> 把该游戏的其他所有停止都挡在外面。
    ///
    /// watchdog 补上了这个漏洞，同时并不替换任何一个信号：它采样结局场景，在场景
    /// 从未启动时进入原生播放，在场景结束后通过原生停止将它结束
    /// （那个会执行 timeline 自身清理的调用），并且在两者都获得机会且已过
    /// 硬性截止时间之后，才由它自己完成结局——于是突袭按主机已经送来的
    /// 退出状态停下。每一步都写一行 [FinalDiag]，下一场突袭正是据此读取的。
    /// </summary>
    private static void WatchLocalEnding()
    {
        if (_localEnding is not { } ending) return;
        var now = Time.unscaledTime;
        var elapsed = now - _localEndingAt;
        var cutscene = ending.CutsceneId;

        if (now >= _nextEndingSample)
        {
            _nextEndingSample = now + EndingSampleSeconds;
            var sample = CutsceneBoundary.SampleSceneOf(cutscene);
            if (!string.IsNullOrEmpty(sample.Scene)) _endingScene = sample.Scene;
            if (sample.Director)
            {
                // 一个仅仅存在的导演并不等于一个播放过的场景：从未启动的场景的
                // 首帧停在时间 0，因此只有正在播放的导演——或时钟越过了上一次
                // 采样点的导演——才算有进展。
                if (sample.Playing || sample.Time > _localEndingSceneTime + 0.05d)
                {
                    if (!_endingPlayed)
                    {
                        _endingPlayed = true;
                        
                    }
                    _endingVisible = sample.Visible;
                    _localEndingSceneTime = sample.Time;
                    _endingStoppedAt = 0f;
                }
                else if (_endingPlayed && _endingStoppedAt <= 0f)
                {
                    _endingStoppedAt = now;
                    
                }
            }
            else if (!_endingNoScene && elapsed >= EndingPlayGraceSeconds)
            {
                _endingNoScene = true;
                
            }
        }

        if (_endingPlayed && now >= _nextEndingProgress && _endingProgressWritten < EndingProgressLines)
        {
            _nextEndingProgress = now + EndingProgressSeconds;
            _endingProgressWritten++;
            
        }

        // 1) 场景从未启动：用剧情侧抢救所用的同一批原生调用进入它的播放。
        if (!_endingPlayed && !_endingRescued && elapsed >= EndingPlayGraceSeconds && now >= _nextEndingRescue &&
            _endingRescueAttempts < EndingRescueMaxAttempts)
        {
            _endingRescueAttempts++;
            _nextEndingRescue = now + EndingRescueRetrySeconds;
            if (_endingRescueAttempts >= EndingRescueMaxAttempts) _endingRescued = true;
            var verdict = CutsceneBoundary.RescueSceneOf(cutscene);
            // 与剧情侧接管所用的规则相同（analysis/cutscene-slowmo）：仅当场景的
            // 导演已停止时，才从这里驱动控制器自身的每帧步进。正在播放的
            // 导演由引擎推进，第二个调用者会让 timeline 跑在两个时钟上。
            var ticked = 0f;
            var after = CutsceneBoundary.SampleSceneOf(cutscene);
            if (after.Director && !after.Playing) ticked = CutsceneBoundary.TickSceneOf();
            
        }

        // 2) 场景已结束：通过原生停止将它结束，那个调用会执行它自身的清理。
        if (_endingPlayed && _endingStoppedAt > 0f && !_endingSceneOver &&
            now - _endingStoppedAt >= EndingStoppedGraceSeconds)
        {
            _endingSceneOver = true;
            var verdict = CutsceneBoundary.StopSceneOf(cutscene);
            
        }

        // 3) 结局什么都没播放过：在原生观看器等待结束之后，仍然把场景撤下，
        //    这样本机的过场摄像机无论如何都无法继续锁住本地玩家。
        if (!_endingPlayed && !_endingSceneOver && elapsed >= EndingWithoutSceneSeconds)
        {
            _endingSceneOver = true;
            var verdict = CutsceneBoundary.StopSceneOf(cutscene);
            
        }

        // 4) 完成本身。此时两个原生信号都已获得充分机会，而突袭无论如何
        //    都要结算：结局开启期间，DeferStop 会拦住该游戏的其他所有停止。
        var settledByScene = _endingSceneOver && _endingStoppedAt > 0f &&
            now - _endingStoppedAt >= EndingStoppedGraceSeconds + EndingSettleGraceSeconds;
        if (settledByScene || elapsed >= EndingHardDeadlineSeconds)
        {
            _endingSettledByWatchdog = true;
            CompleteLocal(settledByScene
                ? "the ending scene was over " + (now - _endingStoppedAt).ToString("0.0")
                    + "s ago and neither native completion of it landed"
                : "the ending did not finish within " + EndingHardDeadlineSeconds.ToString("0.0")
                    + "s; the outcome of the host is settled anyway");
        }
    }

    internal static bool EndLocalOnTimeout()
    {
        if (_localEnding is not { } ending || !_localStarted) return false;
        if (_localComplete) return true;
        // 原生 FinalCutsceneTimer 是地图自身针对不会结束的结局的安全网：它通过
        // 打过补丁的 ImmediateExitFromRaid 调用此处。其方法体与桥接所用的原生停止相同，
        // 而当结局的导演无法解析时，突袭仍必须结算，而不是被
        // 被抑制的原生退出留在开启状态（analysis/terminal-ending-settlement）。
        var verdict = CutsceneBoundary.StopSceneOf(ending.CutsceneId);
        
        // 绝不在未执行原生结束动作/清理的情况下就上报成功。
        return true;
    }

    internal static void LocalExitAction(CutscenesClientController controller, string actionId)
    {
        if (_localStarted && _localEnding is { } ending && controller._cutsceneId == ending.CutsceneId &&
            controller._actions.TryGetValue(actionId, out var action) && action.TryCast<CutsceneActionExitAfterCutscene>() != null)
            CompleteLocal("the exit action '" + actionId + "' of the ending ran on this machine");
    }

    internal static bool SuppressExitAction(string cutsceneId) =>
        _departed && !string.IsNullOrEmpty(cutsceneId) &&
        Endings.Values.Any(ending => string.Equals(ending.Packet.CutsceneId, cutsceneId, StringComparison.Ordinal));

    internal static bool IsLocalFinalExitAction(CutscenesClientController controller, string actionId)
    {
        if (!_localEnding.HasValue || controller == null || controller._actions == null ||
            !controller._actions.TryGetValue(actionId, out var action) ||
            action.TryCast<CutsceneActionExitAfterCutscene>() == null) return false;
        return string.Equals(controller._cutsceneId, _localEnding.Value.CutsceneId, StringComparison.Ordinal);
    }

    private static void CompleteLocal(string reason)
    {
        if (_localComplete) return;
        _localComplete = true;
        _localCompleteAt = Time.unscaledTime;
        
        RequestCompletion();
    }

    /// <summary>
    /// 重复发送主机尚未回应的完成确认。响应通过一个
    /// 可靠包传输，而主机的处理器对同一结果 id 是幂等的——因此响应丢失的代价
    /// 是重发一个包，而不是一场永不结束的突袭。
    /// </summary>
    private static void RetryCompletion()
    {
        if (!_localComplete) return;
        
        RequestCompletion();
    }

    private static void RequestCompletion()
    {
        if (_localEnding is not { } ending) return;
        var packet = new FinalMissionPacket { Message = FinalMissionMessage.Complete,
            NetId = Handler.MyPlayer.NetId, OutcomeId = ending.OutcomeId };
        if (FikaBackendUtils.IsServer) HandleCompletion(packet, Handler.MyPlayer, null);
        else if (FikaSingleton<FikaClient>.Instance?.ServerConnection?.ConnectionState == ConnectionState.Connected)
            Network.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    internal static void HandleCompletion(FinalMissionPacket packet, FikaPlayer player, NetPeer peer)
    {
        if (packet.Message != FinalMissionMessage.Complete || player == null || packet.NetId != player.NetId ||
            !Endings.TryGetValue(player.ProfileId, out var ending) || packet.OutcomeId != ending.Packet.OutcomeId) return;
        ending.Completed = true;
        if (peer != null)
        {
            var response = new FinalMissionPacket { Message = FinalMissionMessage.ExitAuthorized,
                NetId = player.NetId, OutcomeId = ending.Packet.OutcomeId };
            FikaSingleton<FikaServer>.Instance.SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
        }
    }

    internal static void PlayerExtracted(int netId)
    {
        // 客户端的可靠 ClientExtract 在收到 ExitAuthorized 之后执行。
        // 此时它的本地保存已不再需要游戏主机。
        if (!Endings.Values.Any(ending => ending.Packet.NetId == netId && ending.Completed)) return;
        foreach (var pending in PendingDepartures.Where(entry => entry.Value == netId).ToArray())
            PendingDepartures.Remove(pending.Key);
    }

    private static void PrepareLocal(CoopGame game)
    {
        if (_localPrepared) return;
        var player = Handler.MyPlayer;
        game.ExitStatus = _localEnding.Value.ExitStatus;
        game.ExitLocation = FinallExitZone.FINAL_EXIT_NAME;
        game.GameController.LocalTriggerZones.Clear();
        game.GameController.LocalTriggerZones.AddRange(player.TriggerZones.ToArray());
        if (!game.ExtractedPlayers.Contains(player.NetId)) game.ExtractedPlayers.Add(player.NetId);
        if (!Handler.ExtractedPlayers.Contains(player.NetId)) Handler.ExtractedPlayers.Add(player.NetId);
        // 不要使用普通 Extract：它会在过场动画期间传送玩家，并可能
        // 依据 XP/时间把任务结果改写成 Runner。
        player.ActiveHealthController.SetDamageCoeff(0);
        player.ActiveHealthController.DamageMultiplier = 0;
        player.ActiveHealthController.DisableMetabolism();
        player.ActiveHealthController.PauseAllEffects();
        if (player.FikaQuests is Fika.Core.Main.ClientClasses.ClientQuestController clientQuests) clientQuests.ToggleSend(false);
        if (player.FikaQuests is Fika.Core.Main.ClientClasses.ClientSharedQuestController sharedQuests) sharedQuests.ToggleQuestSharing(false);
        game.DiagnosticPlayerOwner?.Stop();
        if (game.GameController is HostGameController host) host.ClearHostAI(player);
        _localPrepared = true;
    }

    // 阻止原生突袭计时到期在对等端仍在
    // 游玩/保存时拆掉主机。上面那条显式授权的停止会绕过这个守卫。
    //
    // (analysis/terminal-ending-settlement) 该拦截是有界的：某个完成信号永不到达的结局
    // 过去会在本会话余下的时间里拦住整个游戏——这个端口的其他每条停止路径都会先查询本
    // 属性——而这正是报告所描述的状态（屏幕停在结局画面上，别的什么都没发生，也无从结算）。
    // 超过 EndingDeferCapSeconds 之后守卫解除，因此即使最坏情况下
    // 游戏也能通过自身路径结束突袭。
    internal static bool DeferStop
    {
        get
        {
            if (_stopping) return false;
            if (EndingRepair && _localEnding.HasValue && _localStarted && !_localComplete &&
                Time.unscaledTime - _localEndingAt >= EndingDeferCapSeconds) return false;
            return _localEnding.HasValue || (FikaBackendUtils.IsServer && _departed);
        }
    }
}
