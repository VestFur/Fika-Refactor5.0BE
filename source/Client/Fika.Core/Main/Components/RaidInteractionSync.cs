using System;
using System.Collections.Generic;
using System.Linq;
using CommonAssets.Scripts.Cutscenes;
using EFT;
using EFT.GlobalEvents;
using EFT.Interactive;
using EFT.Passcodes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Main.Components;

/// <summary>持有一次 raid 的原生事件订阅；传输 ID 始终是 Fika NetId。</summary>
internal static class RaidInteractionSync
{
    internal static GameWorld World { get; private set; }
    internal static bool Ready { get; private set; }
    private static readonly List<Il2CppSystem.Action> Unsubscribe = new();
    private static readonly Dictionary<int, CutsceneStartPacket> Cutscenes = new();
    private static readonly HashSet<(int Process, int Player)> Completed = new();
    private static readonly Dictionary<int, HashSet<int>> Votes = new();
    private static readonly HashSet<NetPeer> PendingInit = new();
    private static readonly HashSet<int> DisconnectedViewers = new();
    private static bool _requested;
    private static IntPtr _configuredPasscodes;
    /// <summary>主机侧过场看门狗的节流（analysis/rally-gate-deadlock）。</summary>
    private static float _nextWatchdog;

    // ---- BB（analysis/icebreaker-keypad-door）：本次 raid 的密码状态 -------------------------
    // 两个可观测量决定键盘在本 raid 的客机上是否可用：(a) 主机究竟有没有应答本机这次的密码
    // 请求（它的快照携带每个 peer 做墙面提示和面板所需的 hints），以及 (b) 本机是否应用了
    // 那份快照。本轮的修复就挂在这两点上，而它们中任何一点的状态变化都会写一行
    // [KeypadDiag]。
    private static bool _hostPasscodeReady;
    private static bool _passcodeInitApplied;
    private static int _passcodeInitRequests;
    private static float _nextPasscodeInitRequest;
    private static int _passcodeRelays;
    private static int _lastAttemptTerminal = int.MinValue;
    private static string _lastAttemptInput;
    private static long _lastAttemptSentAt;

    // BO（analysis/icebreaker-keypad-2）：r32 客机日志里的两个事实，是一个完全不带密码控制器的
    // 世界（因此原生菜单无法提供输入密码的入口），以及一份 hints 为零的主机快照。所以控制器
    // 会在一段很短的宽限窗口内被接管；若原生侧始终不创建，就在这里按它的网络形态创建出来。
    // 主机自己的快照在仍为空时会被重新询问一次。
    private static int _lastSnapshotHints = -1;
    private static int _emptySnapshotAsks;
    private static float _startedAt;
    private static bool _loggedGraceWait;
    private static float _nextControllerFailureLine;

    /// <summary>原生 raid 管线为客机创建的东西，都在第一个 ready 帧之后才出现。</summary>
    private const float PasscodeControllerGraceSeconds = 12f;

    /// <summary>主机尚未应答时，客机重复发送密码请求的间隔。</summary>
    private const float PasscodeInitRequestSeconds = 3f;

    /// <summary>空的主机快照会被重新询问多少次（主机可能稍后才生成它的 hints）。</summary>
    private const int PasscodeEmptySnapshotAsks = 3;

    /// <summary>在把沉默记为永久之前，值得发送多少次重复请求。</summary>
    private const int PasscodeInitRequestLimit = 40;

    /// <summary>在该窗口内、终端与密码都相同的两次尝试视为同一次尝试。</summary>
    private const long PasscodeRelayDedupMillis = 3000;

    /// <summary>本次 raid 中主机至少应答过一次本机的密码请求。</summary>
    internal static bool HostPasscodeReady => _hostPasscodeReady;

    /// <summary>本机已应用主机快照（其 hint 集合现在已在本地控制器中）。</summary>
    internal static bool PasscodeInitApplied => _passcodeInitApplied;

    private static IFikaNetworkManager Manager => FikaSingleton<IFikaNetworkManager>.Instance;

    internal static void Bind(GameWorld world)
    {
        if (World?.Pointer == world.Pointer) return;
        if (World != null) Release(World);
        World = world;
        DialogEntryOccupancy.Bind(world.RaidDialogEntryController);
        Unsubscribe.Add(GlobalEventsController.Instance.SubscribeOnEvent<PasscodeAttemptEvent>(
            (Action<PasscodeAttemptEvent>)OnPasscodeAttempt));
        Unsubscribe.Add(GlobalEventsController.Instance.SubscribeOnEvent<CutsceneSkipIntentionEventFromClient>(
            (Action<CutsceneSkipIntentionEventFromClient>)OnSkipIntention));
        Unsubscribe.Add(GlobalEventsController.Instance.SubscribeOnEvent<SendEndCutsceneOnServerEvent>(
            (Action<SendEndCutsceneOnServerEvent>)OnViewComplete));
        Unsubscribe.Add(GlobalEventsController.Instance.SubscribeOnEvent<CorrectCutsceneTimerEvent>(
            (Action<CorrectCutsceneTimerEvent>)OnCutsceneTime));
    }

    internal static void LevelReady(GameWorld world)
    {
        if (World?.Pointer != world.Pointer) return;
        ConfigureControllers();
        Ready = true;
    }

    private static void ConfigureControllers()
    {
        var world = World;
        // BO（analysis/icebreaker-keypad-2）：宽限时间戳取在这个世界的第一个 ready 帧；下面每一行
        // 控制器日志都会报告本机为等原生控制器等了多久。
        if (_startedAt <= 0f) _startedAt = UnityEngine.Time.unscaledTime;
        // BU（analysis/icebreaker-keypad-3）：本移植围绕主机码表构建的本地控制器，正是客机
        // 想要的终态——它用游戏自己的代码自行校验本 raid 的密码。它绝不能被
        // 换回网络形态，而下面的分支对原生本地控制器做的正是这件事（那份码表只属于
        // 本机自己）。
        var localController = FikaBackendUtils.IsClient
            ? world.PasscodeController?.TryCast<ClientLocalPasscodeController>()
            : null;
        if (localController != null && localController.Pointer != _configuredPasscodes
            && KeypadCodeSync.OwnsLocalController(localController))
        {
            _configuredPasscodes = localController.Pointer;
            _requested = false;
        }
        else if (localController != null && localController.Pointer != _configuredPasscodes)
        {
            var local = localController;
            var settings = local._passcodeLocationSettings;
            
            local.Dispose();
            world.PasscodeController = new ClientNetworkPasscodeController(settings);
            _configuredPasscodes = world.PasscodeController.Pointer;
            _requested = false;
        }
        else if (FikaBackendUtils.IsClient && world.PasscodeController == null)
        {
            // BO（analysis/icebreaker-keypad-2）：r32 客机日志证明过这个状态，而报告必须说明它是在什么
            // 时候被观察到的。'waited=' 是本世界在本帧的年龄；只要它还低于宽限窗口，
            // 原生管线就可能仍会创建控制器，此时什么都不写。
            var waited = UnityEngine.Time.unscaledTime - _startedAt;
            if (waited < PasscodeControllerGraceSeconds)
            {
                if (!_loggedGraceWait)
                {
                    _loggedGraceWait = true;
                    
                }
            }
            else
            {
                // 失败每帧都会重试（加载较晚的场景仍必须被捕获），但只记录一次，之后每个窗口最多
                // 一次，这样永久损坏的状态仍然可读。
                if (!_loggedGraceWait || waited >= _nextControllerFailureLine)
                {
                    _loggedGraceWait = true;
                    _nextControllerFailureLine = waited + 10f;
                    
                }
            }
        }
        // Fika 通过自己的连接承载离线客机的原生事件。
        // 原生客机不得在本地执行单人的跳过/完成。
        if (world.CutscenesClientController != null) world.CutscenesClientController._offlineMode = false;
        if (world.CutscenesServerController != null) world.CutscenesServerController._offlineMode = false;
    }

    // ---- BO（analysis/icebreaker-keypad-2）：不携带密码控制器的客机世界 --------------

    /// <summary>
    /// 所报告缺陷的第二处缺口：在本移植的客机上，没有人创建这个世界的密码
    /// 控制器——客机的原生 raid 管线在那一步之前就停下了，而本类安装的网络
    /// 控制器只会基于原生本地控制器来构建（见 <see cref="ConfigureControllers"/>）。
    /// 没有控制器，本机的交互派发器就无处可问，于是键盘完全不提供
    /// 输入密码的入口（"cannot interact"），而且游戏自己的提交守卫反正会拒绝每一个密码。
    /// 这里就地创建该控制器，形态与原生流程中主机侧 peer 上的完全一致：
    ///   * entries 是本机自身地点场景真正携带的密码 id（与主机读取的是同一份 base.json；
    ///     两次安装已做过比对，携带完全相同的四个 icebreaker 条目）——这些 id
    ///     并不决定密码是否正确，决定的是主机的校验（见 <see cref="TryRelayPasscodeAttempt"/>）；
    ///   * terminals 来自本机自身的地点场景，而主机读取的正是同一个场景；
    ///   * hints 来自主机的快照，这是唯一有意义的那一组（客机绝不能自行生成
    ///     密码——两台机器从同一张表里抽出不同密码，正是 BB 拒绝制造的那种 desync）。
    /// 这些在主机上、在 Fika raid 之外的机器上、或在原生控制器已存在时都不会运行：
    /// 它正是为 r32 客机日志所证明的那个状态准备的兜底。
    /// </summary>
    private static bool AdoptOwnController(GameWorld world, float waited)
    {
        // （2026-10-18 refactor W3-interaction）控制器接管的启发式已被移除：桥接层只
        // 创建并链接它需要的控制器（见 CutsceneBoundary），不再接管外来的控制器。
        return false;
    }

    /// <summary>
    /// 为不得不自行构建控制器的客机准备的设置对象：本机地点场景真正携带的每个密码 id
    /// 对应一条 entries。list id 对客机所做的任何事情都不需要——
    /// 本 raid 的密码属于主机——这条 entries 存在的唯一目的，是让本
    /// 地点的原生守卫得到满足（它会向地点设置询问自己将要打开的那个 id）。
    /// </summary>
    private static Il2CppReferenceArray<PasscodeEntry> ByIdToEntries(IEnumerable<string> ids)
    {
        var entries = new List<PasscodeEntry>();
        foreach (var id in ids)
        {
            if (string.IsNullOrEmpty(id)) continue;
            entries.Add(new PasscodeEntry { PasscodeId = id, PasscodeListId = string.Empty });
        }

        return new Il2CppReferenceArray<PasscodeEntry>(entries.ToArray());
    }

    /// <summary>本机场景中持有多少个密码终端，供没有控制器的机器打日志使用。</summary>

    internal static void Release(GameWorld world)
    {
        if (World?.Pointer != world.Pointer) return;
        DropRaidState();
        World = null;
        Ready = _requested = false;
        _configuredPasscodes = IntPtr.Zero;
    }

    /// <summary>两个入口（世界重绑与世界拆除）共用一份实现。</summary>
    private static void DropRaidState()
    {
        _immunityCleared = false;
        TerminalArmory.Reset();
        FikaFinalMissionDirector.Reset();
        FikaLocalCutsceneTrigger.Reset();
        foreach (var unsubscribe in Unsubscribe) unsubscribe.Invoke();
        Unsubscribe.Clear();
        DialogEntryOccupancy.Reset();
        Cutscenes.Clear();
        Completed.Clear();
        Votes.Clear();
        PendingInit.Clear();
        DisconnectedViewers.Clear();
        FikaSingleton<FikaClient>.Instance?.ClearRaidInteractions();
        FikaSingleton<FikaClient>.Instance?.DropRaidScopedInteractionCache();
        // BB（analysis/icebreaker-keypad-door）：密码就绪状态与中转台账属于刚刚结束的
        // 那个世界——重连或转图都不得继承它们中的任何一个。
        _hostPasscodeReady = false;
        _passcodeInitApplied = false;
        _passcodeInitRequests = 0;
        _nextPasscodeInitRequest = 0f;
        _passcodeRelays = 0;
        _lastAttemptTerminal = int.MinValue;
        _lastAttemptInput = null;
        _lastAttemptSentAt = 0L;
        // BO（analysis/icebreaker-keypad-2）：控制器宽限窗口、快照台账与重新询问
        // 计数器属于刚刚结束的那个世界——重连或转图都不得继承它们。
        _startedAt = 0f;
        _loggedGraceWait = false;
        _nextControllerFailureLine = 0f;
        _lastSnapshotHints = -1;
        _emptySnapshotAsks = 0;
        _hostPasscodeEntries = null;
        // BU（analysis/icebreaker-keypad-3）：本 raid 的码表与围绕它构建的控制器，属于
        // 刚刚结束的那个世界——重连或转图会重新抽取一份。
        KeypadCodeSync.Reset();
        
        // BJ（analysis/terminal-rauf-card）：终端的哨戒通行台账也属于刚结束的
        // 那个世界——第二次 raid 必须写自己的日志行。
        FikaSentryAccess.Reset();
    }

    /// <summary>
    /// 丢弃刚刚结束的那个原生世界的所有跨 raid 持有者，并报告释放了多少个 raid 作用域
    /// 条目。由世界生命周期的拆除流程调用（见
    /// <see cref="Fika.Core.Main.Utils.FikaWorldControllers"/>）：本类持有静态状态——事件
    /// 订阅、过场表、密码快照队列——而刚刚结束的那个世界的原生对象
    /// 绝不能活到下一次 raid。
    /// </summary>
    internal static int ForceResetForWorldTeardown(string reason)
    {
        var dropped = Cutscenes.Count + Votes.Count + Completed.Count + DisconnectedViewers.Count
            + PendingInit.Count + Unsubscribe.Count;
        if (World == null && dropped == 0)
        {
            // 本类的任何东西都不再存活：拆除发生在 raid 被绑定之前。
            FikaLocalCutsceneTrigger.Reset();
            return 0;
        }

        // 两个入口都汇聚到这里：重绑会自行释放前一个世界，拆除则终结
        // 当前的世界。无论走哪条路径，已结束世界的原生对象的持有者都不会存活。
        DropRaidState();
        World = null;
        Ready = _requested = false;
        _configuredPasscodes = IntPtr.Zero;
        FikaGlobals.LogInfo("world teardown (" + reason + "): dropped " + dropped
            + " raid-scoped raid-interaction entry(ies); the cutscene controller link of that world is released with it",
            "RaidInteractionSync");
        return dropped;
    }

    /// <summary>
    /// （2026-10-21）安全网已武装：本机自己的观看者跑完了时间轴
    /// （<c>CutscenesClientController.TimelineShowed</c> 的 postfix）。释放会在稍后于
    /// <see cref="Tick"/> 中执行——见 <see cref="_pendingImmunityReleaseAt"/>——这样主路径（原生
    /// <c>EndCutscene</c> 的 postfix）保持优先，而这一次在已经执行过时就是空操作。
    /// </summary>
    internal static void NoteLocalTimelineShown(int processId)
    {
        _pendingImmunityReleaseAt = UnityEngine.Time.unscaledTime + ImmunityReleaseDelay;
        FikaGlobals.LogInfo($"[SyncDiag] immunity-release armed pid={processId} in={ImmunityReleaseDelay}s",
            "RaidInteractionSync");
    }

    internal static void Tick()
    {
        if (World == null) return;
        // BU（analysis/icebreaker-keypad-3）：本 raid 的密码表，由游戏在主机上生成，并在每台
        // 客机上原生应用。它先于 ConfigureControllers 运行，这样收到码表的客机在该方法
        // 做替换决策时，已经持有自己的原生本地控制器。
        KeypadCodeSync.Tick(World);
        if (Ready) ConfigureControllers();
        DialogEntryOccupancy.Tick();
        // （analysis/rally-gate-deadlock）本 raid 的集结门槛，以及两条脱离观众无法完成的过场的
        // 出路：仍在场景内的观众投票，以及从未进入过它的观众的完成计数。两者都在
        // 主机侧，且都只在真正动手时打日志。
        // （analysis/cutscene-client-native-trigger）本地原生触发器的客机侧决策：本机自身链路
        // 请求、而主机从未派发的过场，就在这里原生播放。
        FikaLocalCutsceneTrigger.Tick(World);
        // （2026-10-19）原生 OnUpdate 在此运行时上没有托管调用方；没有这个泵，Fika 启动的进程
        // 只会切换 UI（字幕），永远不播放时间轴（没有画面）。
        CutsceneBoundary.TickPlayback(World);
        // （2026-10-19）Terminal：raid 开始后清空本地玩家的装备；这些装备已经在
        // 军械库容器里，所以这一步才是把注入变成原版没收效果的关键。
        // （2026-10-21）这次一次性释放只有在本机确实有本地玩家可释放时才会消耗。
        // 以前它会被第一个 raid tick 烧掉，而那个 tick 可能早于本地玩家存在——
        // 于是整个 raid 剩下的时间里释放再也不会运行。重试是节流的：这次扫描会遍历
        // 场景中的所有免疫注册表，这不是逐帧 tick 该做的事。
        if (!_immunityCleared && UnityEngine.Time.unscaledTime >= _nextImmunitySweep)
        {
            _nextImmunitySweep = UnityEngine.Time.unscaledTime + 1f;
            if (ReleaseLocalSniperImmunity("raid start")) _immunityCleared = true;
        }

        // （2026-10-21）过场结束释放的安全网：本机看到自己的时间轴结束了，而一个
        // 已原生结束的进程此时已经释放过。再释放一次只是每个注册表一次 HashSet 移除。
        if (_pendingImmunityReleaseAt > 0f && UnityEngine.Time.unscaledTime >= _pendingImmunityReleaseAt)
        {
            _pendingImmunityReleaseAt = 0f;
            ReleaseLocalSniperImmunity("timeline shown");
        }

        // (2026-10-27 W48 / N2) 主机卡在过场里的兜底：等不到的观众确认不能无限期挂着。
        CutsceneStallWatchdog();
        // 主机的过场进程消失后没有客机本地触发器的 EndCutscene 收口，单独兜底恢复伤害状态。
        HostCutsceneRestoreWatchdog();
        if (Manager?.CoopHandler?.MyPlayer is { } armoryPlayer && TerminalArmory.Applies(World?.LocationId))
        {
            TerminalArmory.Tick(World, armoryPlayer);
        }
        SkipWatchdog();
        if (!Ready || Manager?.CoopHandler?.MyPlayer == null || !Manager.CoopHandler.ShouldSync) return;
        if (FikaBackendUtils.IsClient && Manager.CoopHandler.MyPlayer != null)
        {
            if (!_requested)
            {
                _requested = true;
                SendPasscodeInitRequest("first");
            }
            else if (KeypadDiag.RepairEnabled && !KeypadCodeSync.Ready && _passcodeInitRequests < PasscodeInitRequestLimit)
            {
                // BB（analysis/icebreaker-keypad-door）：旧形态只向主机问一次。如果主机在那一刻
                // 还没准备好（或它的回答丢失了），这台客机整个 raid 都拿不到
                // 密码的 hint 集合——这正是游戏以 "Tried to enter code on
                // location without passcodes" 回应的那种状态。重复请求被节流、有上限，每一次都是一行
                // [KeypadDiag] 日志，因此沉默的主机在能拿到日志的那台机器上是可见的。
                var now = UnityEngine.Time.unscaledTime;
                if (now >= _nextPasscodeInitRequest)
                {
                    _nextPasscodeInitRequest = now + PasscodeInitRequestSeconds;
                    SendPasscodeInitRequest(_passcodeInitRequests + 1 >= PasscodeInitRequestLimit ? "retry-final" : "retry");
                }
            }
        }
        if (FikaBackendUtils.IsServer)
        {
            // (2026-10-26 W34) 空集合时 ToArray 也会产生一次分配，而这行每帧都在主机上跑。
            if (PendingInit.Count == 0) return;
            foreach (var peer in PendingInit.ToArray())
            {
                if (peer.ConnectionState != ConnectionState.Connected) PendingInit.Remove(peer);
                else if (peer.Player != null) { PendingInit.Remove(peer); SendSnapshot(peer); }
            }
        }
    }

    /// <summary>
    /// BB（analysis/icebreaker-keypad-door）：一次发往主机的密码请求。第一次以及每一次重复
    /// 都会写一行日志，因此从不回答的主机可以从本机自己的日志中被证明。
    /// </summary>
    private static void SendPasscodeInitRequest(string source)
    {
        try
        {
            _passcodeInitRequests++;
            var request = new PasscodeInitPacket(true);
            Manager.SendData(ref request, DeliveryMethod.ReliableOrdered);
            
        }
        catch (Exception exception)
        {
            
        }
    }

    /// <summary>
    /// BB（analysis/icebreaker-keypad-door）：主机应答了本机的密码请求。快照正是
    /// 把密码的 hint 集合带到这台机器上的东西，所以这行日志是客机侧证明主机密码层仍然
    /// 活着的证据（它也是下面兜底中转的前提条件）。
    /// </summary>
    internal static void NoteHostPasscodeSnapshot(int hintDoors)
    {
        // BO（analysis/icebreaker-keypad-2）：r32 客机日志证明了 hints=0 的快照。空快照是
        // 主机密码层已应答的证明，但不是它已经持有本 raid 的 hints 的证明：主机
        // 在自己的时间线上生成它们，在那之前发出的快照携带的是空集合。因此保留 hint
        // 数量，并且在集合仍为空期间重新询问主机——客机无法自行制造
        // 那些 hints，否则就等于自己编造密码。
        var first = !_hostPasscodeReady;
        _hostPasscodeReady = true;
        var changed = first || _lastSnapshotHints != hintDoors;
        _lastSnapshotHints = hintDoors;
        if (hintDoors == 0 && _emptySnapshotAsks < PasscodeEmptySnapshotAsks)
        {
            _emptySnapshotAsks++;
            _nextPasscodeInitRequest = 0f;
        }

        if (!changed) return;
        
    }

    /// <summary>本机应用了快照：它的 hints 已作为原生 init 事件抛出。</summary>
    internal static void NotePasscodeInitApplied(int hintDoors)
    {
        _passcodeInitApplied = true;
        
    }

    /// <summary>
    /// 已经走上线路的那些尝试的台账（Fika 自己的原生事件路径会记录
    /// 它发出的那些），这样兜底中转绝不会把同一次尝试发两次。
    /// </summary>
    internal static void NotePasscodeAttemptSent(int terminalNetId, string input)
    {
        _lastAttemptTerminal = terminalNetId;
        _lastAttemptInput = input;
        _lastAttemptSentAt = Environment.TickCount64;
    }

    private static bool RecentlySentAttempt(int terminalNetId, string input) =>
        _lastAttemptTerminal == terminalNetId && string.Equals(_lastAttemptInput, input, StringComparison.Ordinal)
        && Environment.TickCount64 - _lastAttemptSentAt < PasscodeRelayDedupMillis;

    /// <summary>
    /// BB（analysis/icebreaker-keypad-door）：对所报告缺陷的修复。当本机的控制器自身
    /// 不持有任何密码时，游戏在它自己的 <c>ClientPasscodeController.SubmitPasscode</c> 里以
    /// "Tried to enter code on location without passcodes" 拒绝客机的密码——对 Fika 安装在
    /// 每台客机上的网络控制器而言这是设计使然（只有主机的集合算数）。于是这次尝试的任何部分
    /// 都到不了主机，所以尽管主机知道正确密码，门也永远不开。本方法把
    /// 完全相同的这次尝试，通过原生事件路径所用的同一个 packet 交给主机，于是主机
    /// 用自己的密码校验它，并通过原生结果事件作答。
    /// 在主机从未应答过本机期间它拒绝动作（此时中转只会把一次可读的
    /// 拒绝变成无声的等待），并且它会去重，因为原生面板会重复提交被拒绝的提交。
    /// </summary>
    internal static bool TryRelayPasscodeAttempt(Player player, PasscodeTerminal terminal, string input, out string reason)
    {
        reason = null;
        try
        {
            if (!KeypadDiag.RepairEnabled)
            {
                reason = "the keypad repair is switched off (KeypadDiag.RepairEnabled=false)";
                return false;
            }

            if (!Ready || World == null)
            {
                reason = "this machine's raid-interaction layer is not bound";
                return false;
            }

            if (!FikaBackendUtils.IsClient)
            {
                reason = "the local player is not a client of this raid";
                return false;
            }

            if (player == null || terminal == null)
            {
                reason = "no player or terminal on this submit";
                return false;
            }

            var local = LocalPlayer;
            if (local == null || local.Pointer != player.Pointer)
            {
                reason = "the attempt did not come from this machine's own player";
                return false;
            }

            if (!_hostPasscodeReady)
            {
                reason = "the host never answered this client's passcode request (no snapshot, no hint set)";
                return false;
            }

            if (string.IsNullOrEmpty(input))
            {
                reason = "the panel submitted an empty code";
                return false;
            }

            if (RecentlySentAttempt(terminal.NetId, input))
            {
                reason = "this attempt is already on its way to the host";
                return false;
            }

            if (Manager == null)
            {
                reason = "this machine has no network manager";
                return false;
            }

            var packet = new PasscodeAttemptPacket { NetId = local.NetId, PasscodeId = terminal.PasscodeId,
                TerminalNetId = terminal.NetId, Input = input };
            Manager.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            NotePasscodeAttemptSent(terminal.NetId, input);
            _passcodeRelays++;
            
            return true;
        }
        catch (Exception exception)
        {
            reason = "the relay threw " + exception.GetType().Name;
            return false;
        }
    }

    /// <summary>
    /// BU（analysis/icebreaker-keypad-3）：自行校验密码的客机，把一次成功的尝试上报给
    /// 主机。主机随后在自己那份键盘上跑完全相同的原生校验，并把结果
    /// 通告给每个 peer，于是一台机器上输入成功打开的门，在所有人那里都打开。Fika 只承载
    /// 事实，不做判断——而被拒绝的密码根本不需要消息，因为没有任何其他机器会因此改变。
    /// </summary>
    internal static bool TrySyncLocalSuccess(Player player, PasscodeTerminal terminal, string input, out string reason)
    {
        reason = null;
        try
        {
            if (!KeypadDiag.RepairEnabled)
            {
                reason = "the keypad repair is switched off (KeypadDiag.RepairEnabled=false)";
                return false;
            }

            if (!Ready || World == null)
            {
                reason = "this machine's raid-interaction layer is not bound";
                return false;
            }

            if (!FikaBackendUtils.IsClient || Manager == null)
            {
                reason = "this machine is not a client of this raid";
                return false;
            }

            if (player == null || terminal == null || string.IsNullOrEmpty(input))
            {
                reason = "the attempt carries no player, terminal or code";
                return false;
            }

            if (RecentlySentAttempt(terminal.NetId, input))
            {
                reason = "this attempt is already on its way to the host";
                return false;
            }

            var packet = new PasscodeAttemptPacket { NetId = LocalPlayerId, PasscodeId = terminal.PasscodeId,
                TerminalNetId = terminal.NetId, Input = input };
            Manager.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            NotePasscodeAttemptSent(terminal.NetId, input);
            _passcodeRelays++;
            
            return true;
        }
        catch (Exception exception)
        {
            reason = "the local-success sync threw " + exception.GetType().Name;
            return false;
        }
    }

    /// <summary>本机自己的玩家，在它存在之前为 null（无头主机没有玩家）。</summary>
    internal static FikaPlayer LocalPlayer => Manager?.CoopHandler?.MyPlayer;

    /// <summary>本机自己玩家的原生 PlayerId，没有时返回 -1。</summary>
    internal static int LocalPlayerId => LocalPlayer?.PlayerId ?? -1;

    /// <summary>
    /// 当本机自己的玩家已经上报它观看过 <paramref name="processId"/> 时为 true
    /// （即观看者在其时间轴结束时发送的确认）。
    /// </summary>
    internal static bool HasLocalViewCompleted(int processId) =>
        LocalPlayer is { } player && Completed.Contains((processId, player.NetId));

    internal static int ToRaidId(int netId) => netId == 0 ? 0 :
        Manager?.CoopHandler?.Players != null && Manager.CoopHandler.Players.TryGetValue(netId, out var player)
            ? player.RaidId : netId;

    internal static int ToNetId(int raidId)
    {
        if (raidId == 0) return 0;
        if (Manager?.CoopHandler?.Players != null)
            foreach (var player in Manager.CoopHandler.Players.Values)
                if (player.RaidId == raidId) return player.NetId;
        return raidId;
    }

    internal static int ToPlayerId(int netId) =>
        Manager?.CoopHandler?.Players != null && Manager.CoopHandler.Players.TryGetValue(netId, out var player)
            ? player.PlayerId : netId;

    private static int PlayerIdToNetId(int playerId)
    {
        if (Manager?.CoopHandler?.Players != null)
            foreach (var player in Manager.CoopHandler.Players.Values)
                if (player.PlayerId == playerId) return player.NetId;
        return playerId;
    }

    /// <summary>
    /// （analysis/cutscene-client-dispatch）多一个观看者的进程座位表。该表是普通
    /// 数组，所以复制而非就地追加——主机已经发出的 packet 不得在后续读取者
    /// 眼下发生变化。
    /// </summary>
    private static int[] AppendViewer(int[] netIds, int netId)
    {
        if (netIds == null) return new[] { netId };
        for (var i = 0; i < netIds.Length; i++) if (netIds[i] == netId) return netIds;
        var extended = new int[netIds.Length + 1];
        System.Array.Copy(netIds, extended, netIds.Length);
        extended[netIds.Length] = netId;
        return extended;
    }

    /// <summary>
    /// BU（analysis/icebreaker-keypad-3）：主机的码表刚刚就绪（或刚从原生
    /// 控制器那里接管过来）。本 raid 的每个 peer 立刻拿到那份快照，这样客机不必
    /// 等到下一次重试才构建它的原生控制器。
    /// </summary>
    internal static void PushPasscodeSnapshot()
    {
        try
        {
            if (!FikaBackendUtils.IsServer || World == null) return;
            var server = FikaSingleton<FikaServer>.Instance;
            if (server == null) return;
            var peers = new List<NetPeer>();
            server.NetServer.GetConnectedPeers(peers);
            var sent = 0;
            foreach (var peer in peers)
            {
                if (peer.ConnectionState != ConnectionState.Connected || peer.Player == null) continue;
                SendSnapshot(peer);
                sent++;
            }

            
        }
        catch (Exception)
        {
            // 抛异常的推送不得影响 raid：本类的请求/应答路径仍然作为兜底。
        }
    }

    internal static void RequestSnapshot(NetPeer peer)
    {
        if (!Ready || peer.Player == null) PendingInit.Add(peer);
        else SendSnapshot(peer);
    }

    internal static void SendSnapshot(NetPeer peer)
    {
        var server = FikaSingleton<FikaServer>.Instance;
        if (World == null || server == null || peer.Player == null) return;
        DisconnectedViewers.Remove(peer.Player.NetId);
        var init = new PasscodeInitPacket(false) { Hints = new() };
        if (World.PasscodeController?.TryCast<ClientLocalPasscodeController>() is { } local)
            foreach (var entry in local._generatedHints)
                init.Hints.Add(entry.Key, entry.Value.ToArray());
        // BO（analysis/icebreaker-keypad-2）：该主机地点携带的密码 id 随快照一起
        // 传输（id -> list id）。它们是地点密码设置中唯一一部分客机无法
        // 自行证明的东西，而不得不自建控制器的 peer 会把它们当作自己的 entries 集合。
        init.Entries = PasscodeEntries(World);
        // BU（analysis/icebreaker-keypad-3）：本 raid 的码表本身。主机是唯一
        // 抽取它的机器（用游戏自己的 PasscodeGenerator）；客机把它应用到原生本地控制器上，
        // 绝不自行抽取——两台机器两张码表，正是让每个密码都错掉的原因。
        init.Codes = KeypadCodeSync.HostCodes();
        server.SendDataToPeer(ref init, DeliveryMethod.ReliableOrdered, peer);
        // 已分配的最终结局在重连时优先于更早的共享场景。
        if (FikaFinalMissionDirector.SendSnapshot(peer)) return;
        foreach (var start in Cutscenes.Values.ToArray())
        {
            if (!start.NetIds.Contains(peer.Player.NetId) || Completed.Contains((start.ProcessId, peer.Player.NetId))) continue;
            var snapshot = start;
            if (World.CutscenesServerController?._cutsceneProcesses.TryGetValue(start.ProcessId, out var process) == true)
                snapshot.InitTime = Math.Max(start.InitTime, process.afterStartTimeElapsed);
            server.SendDataToPeer(ref snapshot, DeliveryMethod.ReliableOrdered, peer);
            if (Votes.TryGetValue(start.ProcessId, out var votes))
            {
                var eligible = start.NetIds.Where(id => !Completed.Contains((start.ProcessId, id)) && IsActiveViewer(id)).ToArray();
                var vote = new CutsceneSkipPacket { ProcessId = start.ProcessId,
                    PlayersCount = eligible.Length, SkippingNetIds = votes.Where(eligible.Contains).ToArray() };
                server.SendDataToPeer(ref vote, DeliveryMethod.ReliableOrdered, peer);
            }
        }
    }

    /// <summary>
    /// BO（analysis/icebreaker-keypad-2）：主机最后一份快照携带的密码 id。在还没有任何
    /// 快照到达时为 null——此时 <see cref="AdoptOwnController"/> 的兜底改用本机场景的 id，
    /// 而它们是同一批 id（两台机器加载同一份地点文件）。
    /// </summary>
    internal static void NoteHostPasscodeEntries(Dictionary<string, string> entries) => _hostPasscodeEntries = entries;

    private static Dictionary<string, string> _hostPasscodeEntries;

    /// <summary>
    /// BO（analysis/icebreaker-keypad-2）：一个地点的密码 id，转成适合线路传输的普通字符串映射。
    /// 原生集合位于 <c>Location.PasscodeLocationSettings.Entries</c>，主机通过它自己的密码控制器
    /// 构建时所用的那个设置对象来访问它——本 raid 每台机器持有的都是同一个对象，
    /// 因为两者加载同一份 SPT 地点文件。Null（或空映射）意味着主机的控制器
    /// 还没起来；此时 peer 保留自己场景的 id，而那些正是同一批 id。
    /// </summary>
    private static Dictionary<string, string> PasscodeEntries(GameWorld world)
    {
        try
        {
            var settings = world?.PasscodeController?._passcodeLocationSettings;
            var entries = settings?.Entries;
            if (entries == null) return null;
            var map = new Dictionary<string, string>(entries.Length);
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.PasscodeId)) continue;
                map[entry.PasscodeId] = entry.PasscodeListId ?? string.Empty;
            }

            return map;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void OnPasscodeAttempt(PasscodeAttemptEvent attempt)
    {
        if (!Ready || !FikaBackendUtils.IsClient || Manager?.CoopHandler?.MyPlayer is not { } player) return;
        var packet = new PasscodeAttemptPacket { NetId = player.NetId, PasscodeId = attempt.PasscodeId,
            TerminalNetId = attempt.TerminalNetId, Input = attempt.Input };
        Manager.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        // BB（analysis/icebreaker-keypad-door）：原生事件路径自己已经发出了这次尝试，所以提交补丁
        // 的兜底中转不得再发一次。
        NotePasscodeAttemptSent(attempt.TerminalNetId, attempt.Input);
        
    }

    internal static void BroadcastPasscodeResult(Player player, PasscodeTerminal terminal, bool success)
    {
        if (!Ready || !FikaBackendUtils.IsServer || player == null || terminal == null) return;
        var result = new PasscodeResultPacket { NetId = ToNetId(player.RaidId), PasscodeId = terminal.PasscodeId,
            TerminalNetId = terminal.NetId, Successful = success };
        Manager.SendData(ref result, DeliveryMethod.ReliableOrdered);
    }


    /// <summary>
    /// （2026-10-18）过场的原生观看者表还携带着本 session 并不拥有的座位（机器人、
    /// 其他小队的玩家）。游戏显示的那个计数器（"waiting for players 2/8"）就是由它构建的，
    /// 而进程在所有座位确认之前无法结束，于是一个无人拥有的座位会把 raid 卡住。在原生
    /// 进程创建之前，把这张表改写成 session 真正拥有的玩家。
    /// </summary>
    internal static void FilterViewersToSession(Il2CppSystem.Collections.Generic.List<int> viewers)
    {
        try
        {
            if (viewers == null || viewers.Count == 0)
            {
                return;
            }

            for (var i = viewers.Count - 1; i >= 0; i--)
            {
                var raidId = viewers[i];
                if (raidId <= 0 || PlayerIdToNetId(raidId) <= 0)
                {
                    viewers.RemoveAt(i);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    internal static void StartCutscene(CutsceneProcess process, bool forceMode, float initTime)
    {
        if (!FikaBackendUtils.IsServer || World == null || process == null || Cutscenes.ContainsKey(process.processId)) return;
        // 在主机过场的进程表可能消失之前先做标记。这样即使进程在某个 Tick 观察到它之前
        // 就结束了，HostCutsceneRestoreWatchdog 也能恢复伤害状态。
        _hostCutsceneSeen = true;
        _hostCutsceneGoneAt = 0f;
        _hostCutsceneRestored = false;
        // 集结触发器启动的进程属于每一个参与者：世界在创建该进程时没有计入的 peer，
        // 否则就会被留在主机身处其中的过场之外，而它的
        // 跳过投票永远无法凑齐（analysis/rally-gate-deadlock）。
        // （analysis/cutscene-client-dispatch）同一修复中不依赖窗口的那一半：上面的集结窗口由
        // TriggerRallyZone.EmitTrigger 武装，而 Icebreaker 触发器实际是通过 SPTushonka 自己的
        // watcher（TriggersEmitter.Emit）发出的，所以这里运行时窗口并未被武装。观看者表才是
        // 广播所携带、也是 peer 的原生客机控制器用来过滤的对象，所以一个
        // 不在表里的 peer 永远无法进入场景——这正是 "the client was not
        // addressed" 分支里唯一的代码层缺口（analysis/cutscene-client-dispatch，第 3 节）。
        var start = new CutsceneStartPacket { ProcessId = process.processId, CutsceneId = process.currentCutsceneId,
            InitTime = initTime, FixedTime = process.fixedTimeOn, ForceMode = forceMode,
            // （2026-10-18）只寻址 session 中确实存在的观看者：原生观看者表还
            // 携带主机无法映射的 id（机器人、其他小队的玩家），而带 0 的 packet
            // 会让客机把自己从一个本该播放的场景里过滤掉。
            NetIds = process.playersInCurrentView.ToArray()
                .Select(PlayerIdToNetId)
                .Where(netId => netId > 0)
                .ToArray() };
        Cutscenes[start.ProcessId] = start;
        // (2026-10-27 W48 / N2) 记下广播时刻与进程对象，供主机侧的"卡死看门狗"使用。
        BroadcastAt[start.ProcessId] = UnityEngine.Time.unscaledTime;
        if (process != null) BroadcastProcesses[start.ProcessId] = process;
        Votes[start.ProcessId] = new();
        Manager?.SendData(ref start, DeliveryMethod.ReliableOrdered);
        
        // （analysis/cutscene-client-dispatch）唯一一行写明广播所携带座位表的日志——
        // 这是客机侧日志无法呈现的事实，也是 peer 进入或不进入场景的原因。每个
        // 进程一行； 
    }

    /// <summary>本 session 是否已把这个进程交给 peers（raid 的广播）。</summary>
    internal static bool IsCutsceneBroadcast(int processId) => Cutscenes.ContainsKey(processId);

    /// <summary>
    /// （2026-10-21）地图触发器入口：<c>CutscenesServerController.StartCutsceneWithTime</c> 会自行
    /// 创建一个进程，但不经过 session 广播所挂靠的 <c>StartCutscene</c> postfix，于是由地点触发器
    /// 启动的过场只留在主机上。新进程是该控制器自身表（<c>_cutsceneProcesses</c>，interop :94638）中
    /// 具有该过场 id 的最新未广播条目，它被交给 <see cref="StartCutscene"/> 的
    /// 那唯一一处广播——相同的 packet 形状，相同的座位映射。
    /// </summary>
    internal static void StartCutsceneFromWithTime(CutscenesServerController controller, string cutsceneId,
        bool forceMode)
    {
        if (!FikaBackendUtils.IsServer || controller == null || string.IsNullOrEmpty(cutsceneId))
        {
            return;
        }

        try
        {
            var processes = controller._cutsceneProcesses;
            if (processes == null)
            {
                return;
            }

            CutsceneProcess newest = null;
            foreach (var entry in processes)
            {
                var process = entry.Value;
                if (process == null || process.processId <= 0)
                {
                    continue;
                }
                if (!string.Equals(process.currentCutsceneId, cutsceneId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (IsCutsceneBroadcast(process.processId))
                {
                    continue;
                }
                if (newest == null || process.processId > newest.processId)
                {
                    newest = process;
                }
            }

            if (newest == null)
            {
                return;
            }

            // StartCutsceneWithTime 本身不携带初始时间；客机需要的偏移量就是该进程的
            // 固定时间标志，packet 会从进程自身读取它。
            StartCutscene(newest, forceMode, 0f);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// （analysis/cutscene-client-native-trigger）一个<b>本机自己创建的</b>过场进程的派发部分——
    /// 也就是 <see cref="FikaLocalCutsceneTrigger"/> 中被批准的本地原生启动刚刚
    /// 构建出来的那个过场。在主机上这一半是 <see cref="StartCutscene"/> 的广播；在客机上原生那一半（
    /// 离线模式把 <c>CutSceneStartEvent</c> 交给本地客机控制器）已被
    /// <see cref="ConfigureControllers"/> 关掉，所以桥接层抛出的正是主机 packet 路径抛出的那个事件。
    /// 进程 id 是本机自己的，因此本地观看者的完成、原生 <c>EndCutscene</c>，以及
    /// 随之而来的时间轴结束动作（地图的那扇门），都在本机自己的控制器上运行。
    /// </summary>
    internal static void PlayLocalNativeProcess(CutsceneProcess process)
    {
        if (process == null) return;
        var cutscene = process.currentCutsceneId;
        if (string.IsNullOrEmpty(cutscene)) return;
        int[] viewerIds;
        try
        {
            var viewers = process.playersInCurrentView;
            viewerIds = viewers == null ? null : ((IEnumerable<int>)viewers).ToArray();
        }
        catch (Exception)
        {
            viewerIds = null;
        }
        FikaLocalCutsceneTrigger.NoteLocalProcess(process);
        var local = LocalPlayerId;
        if (viewerIds == null || local <= 0 || !viewerIds.Contains(local))
        {
            
            return;
        }
        // （analysis/cutscene-slowmo）本机已经在播放的场景绝不会再启动第二次。第二个
        // CutSceneStartEvent 会再次运行控制器的 start 处理器——那就是第二个时间轴
        // 入口，带它自己的音频起点，于是画面跳回开头而声音保持
        // 原来的位置，也就是本轮修复的正是那种不同步播放。这个守卫是可读状态
        // （控制器自己的进程 id 与过场 id），不是启发式。
        var controller = World?.CutscenesClientController;
        var running = 0;
        var runningScene = (string)null;
        try
        {
            if (controller != null)
            {
                running = controller._cutsceneProcessId;
                runningScene = controller._cutsceneId;
            }
        }
        catch (Exception) { running = 0; }
        if (running != 0 && string.Equals(runningScene, cutscene, StringComparison.Ordinal))
        {
            
            return;
        }
        GlobalEventsController.CreateEvent<CutSceneStartEvent>().Invoke(cutscene, process.processId, viewerIds, 0f, true, false);
        
    }

    /// <summary>
    /// （analysis/cutscene-slowmo）进程的固定时间标志，供本地触发器的日志使用：它是
    /// 主机 packet 作为 <c>FixedTime</c> 携带的字段（由 <c>process.fixedTimeOn</c> 填充），所以在
    /// 本地路径里写出它，能让两条路径在一次 grep 中直接可比。
    /// </summary>
    private static string FixedTime(CutsceneProcess process)
    {
        try { return process.fixedTimeOn ? "true" : "false"; }
        catch (Exception) { return "?"; }
    }

    /// <summary>
    /// （2026-10-21）一个过场进程在本机上的结束。调用方是原生
    /// <c>CutscenesServerController.EndCutscene</c> 的 postfix，所以它在两种角色上都会运行，也是释放
    /// 最早可以发生的时机：下面所有与角色相关的逻辑，对本机从未注册过的进程（客机自己的
    /// 进程、session 表已丢失的主机进程）都会提前返回，而这些提前返回以前
    /// 恰恰就是免疫释放被丢掉的地方。因此释放被放在它们之上。
    /// </summary>
    internal static void EndCutscene(int processId, Il2CppSystem.Collections.Generic.List<int> players, bool skip)
    {
        // 原生调用所命名的进程参与者（raid id），加上本机自己的玩家。
        // 两步都是幂等的：每台机器每个进程一行，绝不产生第二次可见效果。
        var viewers = ReleaseSniperImmunity(players, "pid=" + processId);
        var local = ReleaseLocalSniperImmunity("cutscene end pid=" + processId);
        FikaGlobals.LogInfo($"[SyncDiag] cutscene-immunity-release pid={processId} viewers={CountOf(players)} " +
            $"viewerHits={viewers} localSwept={(local ? "yes" : "no")} " +
            $"role={(FikaBackendUtils.IsServer ? "host" : "client")}", "RaidInteractionSync");

        // (2026-10-27 W48 / TARGETS N4) 过场开始时原生会把本机的伤害系数压到 0（用户实测：CG 触发前能受伤、
        // 触发后无敌，且与任何第三方模组无关）。免疫集合那条路经实测是空转（registries=0 / wasImmune=0），
        // 所以恢复必须挂在"过场结束"这个唯一收口上，并且对本机无条件执行。
        RestoreLocalDamageAfterCutscene("cutscene end pid=" + processId);

        if (!FikaBackendUtils.IsServer) return;

        if (!Cutscenes.Remove(processId, out _)) return;
        Votes.Remove(processId);
        Completed.RemoveWhere(x => x.Process == processId);
        if (skip)
        {
            var skipped = new CutsceneSkipPacket { ProcessId = processId, Skip = true };
            Manager.SendData(ref skipped, DeliveryMethod.ReliableOrdered);
        }
        var end = new CutsceneEndPacket { ProcessId = processId };
        Manager.SendData(ref end, DeliveryMethod.ReliableOrdered);
    }

    private static int CountOf(Il2CppSystem.Collections.Generic.List<int> list)
    {
        try { return list == null ? 0 : list.Count; } catch (Exception) { return 0; }
    }

    /// <summary>
    /// (2026-10-27 W48 / N4) 把本机的伤害保护状态恢复成正常值。只在"过场已经结束、玩家还活着、还没撤离"
    /// 时动手；DamageCoeff 和 DamageMultiplier 都要恢复，并解除原生过场暂停的 effects。每次动作一行日志。
    /// 返回是否真的改过。
    /// </summary>
    internal static bool RestoreLocalDamageAfterCutscene(string source)
    {
        try
        {
            var player = LocalPlayer;
            if (player?.ActiveHealthController == null) return false;
            if (player.Downed || player.HasDied) return false;
            if (player.HealthController != null && !player.HealthController.IsAlive) return false;
            if (Manager?.CoopHandler?.ExtractedPlayers != null &&
                Manager.CoopHandler.ExtractedPlayers.Contains(player.NetId)) return false;
            // 转图会替换关卡；在那里恢复会作用在已经正在关闭的世界上。
            if (FikaBackendUtils.IsTransit) return false;

            var health = player.ActiveHealthController;
            var coeff = health.DamageCoeff;
            var multiplier = health.DamageMultiplier;
            if (coeff > 0.999f && multiplier > 0.999f) return false;

            try
            {
                player.UnpauseAllEffectsOnPlayer();
            }
            catch (Exception exception)
            {
                FikaGlobals.LogWarning($"[CgDiag] cutscene-restore unpause threw ({source}): " +
                    $"{exception.GetType().Name}: {exception.Message}", "RaidInteractionSync");
            }

            if (coeff <= 0.999f)
            {
                health.SetDamageCoeff(1f);
            }

            if (multiplier <= 0.999f)
            {
                health.DamageMultiplier = 1f;
            }

            FikaGlobals.LogInfo($"[CgDiag] cutscene-restore ({source}) role=" +
                $"{(FikaBackendUtils.IsServer ? "host" : "client")} " +
                $"coeff={coeff:F2}->{health.DamageCoeff:F2} " +
                $"mult={multiplier:F2}->{health.DamageMultiplier:F2}", "RaidInteractionSync");
            return true;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[CgDiag] cutscene-restore threw ({source}): " +
                $"{exception.GetType().Name}: {exception.Message}", "RaidInteractionSync");
            return false;
        }
    }

    /// <summary>(2026-10-27 W48 / N2) 广播出去的过场进程开始时间，用于"主机被卡在过场里"的看门狗。</summary>
    private static readonly Dictionary<int, float> BroadcastAt = new();

    // ---- 主机侧恢复看门狗 ------------------------------------------------------------
    // 客机从 FikaLocalCutsceneTrigger 拿到原生 EndCutscene，因此有一个可靠的
    // 恢复钩子。主机走的是原生进程生命周期：当该进程在没有走到
    // EndCutscene 的情况下消失（或它的对象在 CutsceneStallWatchdog 里已经没了），就只有主机
    // 把 DamageCoeff/DamageMultiplier 留在过场的取值上。监视主机的进程表，并在最后一个
    // 进程消失之后恢复一次。
    private const float HostCutsceneRestoreDelaySeconds = 2f;
    private static bool _hostCutsceneSeen;
    private static float _hostCutsceneGoneAt;
    private static bool _hostCutsceneRestored;

    private static bool AnyCutsceneProcessActive()
    {
        try
        {
            var world = World;
            if (world == null) return false;
            if (world.CutscenesClientController != null && world.CutscenesClientController._cutsceneProcessId > 0)
            {
                return true;
            }
            var processes = world.CutscenesServerController?._cutsceneProcesses;
            return processes != null && processes.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void HostCutsceneRestoreWatchdog()
    {
        if (!FikaBackendUtils.IsServer || FikaBackendUtils.IsHeadless) return;

        var now = UnityEngine.Time.unscaledTime;
        if (AnyCutsceneProcessActive())
        {
            _hostCutsceneSeen = true;
            _hostCutsceneGoneAt = 0f;
            _hostCutsceneRestored = false;
            return;
        }

        if (!_hostCutsceneSeen || _hostCutsceneRestored) return;

        if (_hostCutsceneGoneAt <= 0f)
        {
            _hostCutsceneGoneAt = now;
            return;
        }

        if (now - _hostCutsceneGoneAt < HostCutsceneRestoreDelaySeconds) return;

        _hostCutsceneRestored = true;
        RestoreLocalDamageAfterCutscene("host-process-gone");
    }

    /// <summary>(2026-10-27 W48 / N2) 进程序对象，看门狗强制收尾时要用它调原生 EndCutscene。</summary>
    private static readonly Dictionary<int, CutsceneProcess> BroadcastProcesses = new();

    /// <summary>
    /// 主机最多等这么久（秒）就让一个"没有人能推进"的过场进程收尾。用户复现：客机在场时主机黑屏、CG 不触发，
    /// 客机退出后主机立刻正常 —— 说明主机在等一个永远不回来的观众确认。超时后按 skip 收尾，至少把黑屏与
    /// "过场中免伤"解开，玩家还能继续玩。
    /// </summary>
    internal static float CutsceneStallSeconds = 25f;

    private static float _nextCutsceneStallSweep;

    /// <summary>(2026-10-27 W48 / N2) 主机侧的过场卡死看门狗，1 Hz。</summary>
    private static void CutsceneStallWatchdog()
    {
        if (!FikaBackendUtils.IsServer) return;
        if (BroadcastAt.Count == 0) return;
        var now = UnityEngine.Time.unscaledTime;
        if (now < _nextCutsceneStallSweep) return;
        _nextCutsceneStallSweep = now + 1f;

        List<int> stale = null;
        foreach (var entry in BroadcastAt)
        {
            if (now - entry.Value >= CutsceneStallSeconds)
            {
                (stale ??= new List<int>()).Add(entry.Key);
            }
        }

        if (stale == null) return;
        var world = World;
        var server = world == null ? null : world.CutscenesServerController;
        for (var i = 0; i < stale.Count; i++)
        {
            var processId = stale[i];
            BroadcastAt.Remove(processId);
            var process = BroadcastProcesses.TryGetValue(processId, out var p) ? p : null;
            BroadcastProcesses.Remove(processId);
            var waited = now - (process == null ? now : 0f);
            var ended = "skipped";
            try
            {
                if (server != null && process != null)
                {
                    var viewers = new Il2CppSystem.Collections.Generic.List<int>();
                    if (LocalPlayerId > 0) viewers.Add(LocalPlayerId);
                    if (Cutscenes.TryGetValue(processId, out var packet) && !string.IsNullOrEmpty(packet.CutsceneId))
                    {
                        server.EndCutscene(packet.CutsceneId, processId, process, viewers, true);
                        ended = "native";
                    }
                }
            }
            catch (Exception exception)
            {
                ended = "threw:" + exception.GetType().Name;
            }

            FikaGlobals.LogInfo($"[CgDiag] cutscene-stall-watchdog pid={processId} waited>={CutsceneStallSeconds:F0}s " +
                $"end={ended} - a viewer never acknowledged; shut the process down so the black screen and the " +
                "cutscene immunity cannot hold the raid", "RaidInteractionSync");
            // EndCutscene 通常会通过它的 postfix 执行恢复，但当进程对象
            // 已经不在了（ended == "skipped"）时那次调用永远不会发生。此处无条件恢复；
            // RestoreLocalDamageAfterCutscene 是幂等的。
            RestoreLocalDamageAfterCutscene("stall-watchdog:" + processId);
        }
    }

    /// <summary>本次 raid 的 raid-start 释放真正运行过之后为 true（见 <see cref="Tick"/>）。</summary>
    private static bool _immunityCleared;

    /// <summary>一次性 raid-start 释放的节流（见 <see cref="Tick"/>）。</summary>
    private static float _nextImmunitySweep;

    /// <summary>
    /// （2026-10-21）安全网何时触发：由 <see cref="NoteLocalTimelineShown"/> 设置，也就是由
    /// <c>CutscenesClientController.TimelineShowed</c> 的 postfix 设置——这是本机在两种角色上都必定
    /// 运行的唯一一个过场结束事件，因为它正是本地观看者自己刚刚跑完的那条时间轴。释放本身在
    /// <see cref="Tick"/> 中执行，晚几秒，所以它绝不会落在报告结束的那个原生回调
    /// 内部。0 表示 "没有任何待处理"。
    /// </summary>
    private static float _pendingImmunityReleaseAt;

    /// <summary>本地时间轴结束到安全网释放之间的秒数（见 <see cref="_pendingImmunityReleaseAt"/>）。</summary>
    private const float ImmunityReleaseDelay = 2f;

    /// <summary>
    /// （2026-10-19）Terminal 的转图区域位于一个狙击/边界区内，那里的特效和声音会在这台机器上播放，
    /// 而伤害却被本地玩家仍持有的、某个原生免疫容器中的条目拒绝——
    /// 就是那份"有攻击特效却无伤害"的报告。一个从未被释放的过场条目（或一条
    /// 从另一台机器带过来的区域条目）在每次 raid 中清理一次，时机是 raid 上线
    /// 之后几秒，这样该区域要么造成伤害，要么保持安静，而不是徒有其表地开火。
    /// </summary>
    /// <returns>扫描确实运行时为 true，也就是本机有本地玩家可释放。真正持有该条目的
    /// 注册表数量是日志的一部分，因此 "nothing to release" 仍然可见。</returns>
    internal static bool ReleaseLocalSniperImmunity(string source)
    {
        try
        {
            var raidId = LocalPlayer?.RaidId ?? 0;
            if (raidId <= 0) return false;
            var registries = UnityEngine.Object.FindObjectsOfType<PlayersWithImmuneToSniperFire>(true);
            if (registries == null) return false;
            var removed = 0;
            for (var i = 0; i < registries.Length; i++)
            {
                try
                {
                    if (registries[i].IsPlayerImmuneForFire(raidId)) removed++;
                    registries[i].RemoveImmunePlayer(raidId);
                }
                catch (Exception) { }
            }
            FikaGlobals.LogInfo($"[TransitDiag] cleared local sniper immunity ({source}) registries={registries.Length} " +
                $"wasImmune={removed}");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// （2026-10-21）释放原生结束所命名的、作为进程观看者的那些 raid id。这些是原生 raid id
    /// （与 <c>Player.RaidId</c> 携带的同一个值），不是 session NetId——本方法以前依赖的 NetId
    /// 映射从未匹配到主机自己的玩家，这就是过场之后主机仍保持免疫的原因。
    /// </summary>
    private static int ReleaseSniperImmunity(Il2CppSystem.Collections.Generic.List<int> playersId, string source)
    {
        try
        {
            if (playersId == null || playersId.Count == 0) return 0;
            var registries = UnityEngine.Object.FindObjectsOfType<PlayersWithImmuneToSniperFire>(true);
            if (registries == null) return 0;

            var released = 0;
            for (var i = 0; i < playersId.Count; i++)
            {
                var raidId = playersId[i];
                if (raidId <= 0) continue;
                for (var r = 0; r < registries.Length; r++)
                {
                    try
                    {
                        if (registries[r].IsPlayerImmuneForFire(raidId)) released++;
                        registries[r].RemoveImmunePlayer(raidId);
                    }
                    catch (Exception) { }
                }
            }

            if (released > 0)
            {
                FikaGlobals.LogInfo($"[TransitDiag] cleared cutscene viewer immunity ({source}) viewers=" +
                    $"{playersId.Count} released={released}", "RaidInteractionSync");
            }

            return released;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void OnSkipIntention(CutsceneSkipIntentionEventFromClient intention)
    {
        if (Manager?.CoopHandler?.MyPlayer is not { } player) return;
        var packet = new CutsceneSkipIntentionPacket { NetId = player.NetId, ProcessId = intention.ProcessId,
            CutsceneId = intention.CutsceneId, WantsToSkip = intention.WantsToSkip };
        if (FikaBackendUtils.IsServer) HandleSkip(packet, player);
        else Manager.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    private static void OnCutsceneTime(CorrectCutsceneTimerEvent correction)
    {
        if (!FikaBackendUtils.IsServer || World == null) return;
        var packet = new CutsceneTimePacket { ProcessId = correction.processId,
            CutsceneId = correction.cutsceneId, ElapsedTime = correction.elapsedTime };
        Manager?.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    private static void OnViewComplete(SendEndCutsceneOnServerEvent completed)
    {
        if (Manager?.CoopHandler?.MyPlayer is not { } player) return;
        var packet = new CutsceneViewCompletePacket { NetId = player.NetId, ProcessId = completed.CutsceneID };
        if (FikaBackendUtils.IsServer) HandleComplete(packet, player);
        else Manager.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    internal static void HandleSkip(CutsceneSkipIntentionPacket packet, FikaPlayer player)
    {
        if (!FikaBackendUtils.IsServer || !Cutscenes.TryGetValue(packet.ProcessId, out var start) ||
            packet.CutsceneId != start.CutsceneId ||
            Completed.Contains((packet.ProcessId, player.NetId)) || !Votes.TryGetValue(packet.ProcessId, out var votes)) return;
        // 与 HandleComplete 相同的座位修补：发出过场信号的 peer 显然就在
        // 场景内（analysis/cutscene-client-dispatch），所以没把它列进去的表在这里被补全，而不是
        // 把这次投票丢掉——而丢掉投票正是 analysis/rally-gate-deadlock 中让主机无法动弹的原因。
        if (!start.NetIds.Contains(player.NetId))
        {
            start.NetIds = AppendViewer(start.NetIds, player.NetId);
            Cutscenes[packet.ProcessId] = start;
            
        }
        // 一次投票就是观看者确实在过场内的证明；WantsToSkip=false 是同一条
        // 消息，也是 peer 刚进入场景时发送的东西（见 AnnounceLocalCutsceneStart）。
        if (packet.WantsToSkip) votes.Add(player.NetId);
        else votes.Remove(player.NetId);
        BroadcastVotes(start, votes);
    }

    private static void BroadcastVotes(CutsceneStartPacket start, HashSet<int> votes)
    {
        var eligible = DecidingViewers(start, votes, "vote");
        var skipping = votes.Where(eligible.Contains).ToArray();
        var update = new CutsceneSkipPacket { ProcessId = start.ProcessId,
            PlayersCount = eligible.Length, SkippingNetIds = skipping };
        Manager.SendData(ref update, DeliveryMethod.ReliableOrdered);
        GlobalEventsController.CreateEvent<CutsceneSkipIntentionEventFromServer>().Invoke(
            start.ProcessId, update.PlayersCount, update.SkippingNetIds.Select(ToPlayerId).ToArray());
        EndIfEveryViewerVoted(start, eligible, votes, "vote");
    }

    /// <summary>
    /// 其投票决定某个进程是否跳过的那些观看者（analysis/rally-gate-deadlock）。一直到
    /// 静默观看者超时之前，就是广播中的每一个观看者——这是原生规则。超时之后，从未发过
    /// 任何信号的观看者会被剔除：从未进入过场的 peer 永远无法投票，而
    /// 等它，正是让主机手握一份无法凑齐的投票而无法动弹的原因。本地观看者
    /// 与每一个确实投过票的观看者始终留在集合中，所以决策永远不会低于那些
    /// 显然就在场景内的玩家。
    /// </summary>
    private static int[] DecidingViewers(CutsceneStartPacket start, HashSet<int> votes, string source)
    {
        var all = start.NetIds.Where(id => !Completed.Contains((start.ProcessId, id)) && IsActiveViewer(id)).ToArray();
        if (all.Length == 0) return all;
        var local = LocalPlayer?.NetId ?? -1;
        var deciding = all.Where(id => votes.Contains(id) || id == local).ToArray();
        if (deciding.Length == 0) return all;
        
        return deciding;
    }

    /// <summary>
    /// 原生跳过：每个决策观看者都投了票，于是主机通过原生
    /// <c>EndCutscene(skip: true)</c> 结束该进程——这次调用同时运行时间轴的结束动作（地图的那扇门）。
    /// </summary>
    private static void EndIfEveryViewerVoted(CutsceneStartPacket start, int[] eligible, HashSet<int> votes, string source)
    {
        if (eligible == null || eligible.Length == 0 || !eligible.All(votes.Contains)) return;
        if (World?.CutscenesServerController?._cutsceneProcesses.TryGetValue(start.ProcessId, out var process) != true) return;
        
        World.CutscenesServerController.EndCutscene(start.CutsceneId, start.ProcessId, process, process.playersInCurrentView, true);
    }

    /// <summary>
    /// 无人能完成的过场的出路（analysis/rally-gate-deadlock）。在有进程进行时，它每秒在
    /// 主机上运行一次，只做两件事，且两件都只在静默观看者
    /// 超时之后才做：它让仍在场景内的观看者的投票来决定（从未进入过的
    /// 观看者不再持有投票权），并且把从未进入过的观看者的完成计数收口，
    /// 这样原生结束——以及随之而来的时间轴结束动作——就能运行，而不是
    /// 等一个根本不在过场里的 peer。超时尚未
    /// 过去时什么都不会发生，对发过任何信号的观看者也什么都不会发生。
    /// </summary>
    private static void SkipWatchdog()
    {
        if (World == null || !FikaBackendUtils.IsServer || Cutscenes.Count == 0) return;
        var now = UnityEngine.Time.unscaledTime;
        if (now < _nextWatchdog) return;
        _nextWatchdog = now + 1f;
        foreach (var entry in Cutscenes.ToArray())
        {
            var start = entry.Value;
            if (Votes.TryGetValue(start.ProcessId, out var votes) && votes.Count > 0)
                EndIfEveryViewerVoted(start, DecidingViewers(start, votes, "timeout"), votes, "timeout");
            CloseSilentViewers(start);
        }
    }

    /// <summary>
    /// 通过真实确认所用的那个原生调用（<c>PlayerViewCutsceneComplete</c>），把从未进入过场的观看者
    /// 计为已完成，这样进程自己的计数器得以填满，它的
    /// 原生结束就能运行。只有在静默观看者超时过去之后、只有它确实完全没有发过
    /// 任何信号时，观看者才会被收口，且一个进程绝不重复两次。
    /// </summary>
    private static void CloseSilentViewers(CutsceneStartPacket start)
    {
        var server = World?.CutscenesServerController;
        if (server?._cutsceneProcesses == null ||
            !server._cutsceneProcesses.TryGetValue(start.ProcessId, out var process) || process == null) return;
        var viewers = process.playersInCurrentView;
        if (viewers == null || viewers.Count == 0) return;
        var local = LocalPlayer?.NetId ?? -1;
        for (var i = 0; i < viewers.Count; i++)
        {
            int playerId;
            try { playerId = viewers[i]; }
            catch (Exception) { continue; }
            var netId = PlayerIdToNetId(playerId);
            if (netId == local || Completed.Contains((start.ProcessId, netId))) continue;
            if (!Completed.Add((start.ProcessId, netId))) continue;
            
            server.PlayerViewCutsceneComplete(start.CutsceneId, start.ProcessId, playerId);
        }
    }

    /// <summary>
    /// 本机自己的观看者进入了过场：这是唯一能把 "已经在场景内且安静" 与
    /// "从未进入过" 区分开的信号。主机在本地记录它，peer 则把它作为原生跳过意图事件发送，带
    /// <c>wantsToSkip: false</c>——用的是已有 packet，而不是新加一个，这就是本次修复
    /// 没有改动任何线路协议内容的原因。
    /// </summary>
    internal static void AnnounceLocalCutsceneStart(string cutsceneId, int processId)
    {
        if (processId == 0 || string.IsNullOrEmpty(cutsceneId)) return;
        var local = LocalPlayer?.NetId ?? -1;
        if (FikaBackendUtils.IsServer || local < 0) return;
        GlobalEventsController.CreateEvent<CutsceneSkipIntentionEventFromClient>().Invoke(processId, cutsceneId, false);
    }

    internal static void HandleComplete(CutsceneViewCompletePacket packet, FikaPlayer player)
    {
        if (!FikaBackendUtils.IsServer || !Cutscenes.TryGetValue(packet.ProcessId, out var start)) return;
        // （analysis/cutscene-client-dispatch）真正看过的观看者就是观看者。一个进程的座位表是
        // 由本地玩家列表构建的，所以 peer 可能不在其中却仍在播放该场景
        // （本次修复的客机一半：FikaClient.RaidInteractions.cs 会为自己重新抛出 start）。丢掉
        // 该 peer 的完成，会让主机的进程一直开着，而再没有别的东西可等。只接受
        // 本 raid 的参与者，且只在座位修补开启时才接受。
        if (!start.NetIds.Contains(player.NetId))
        {
            start.NetIds = AppendViewer(start.NetIds, player.NetId);
            Cutscenes[packet.ProcessId] = start;
            
        }
        if (!Completed.Add((packet.ProcessId, player.NetId))) return;
        World?.CutscenesServerController?.PlayerViewCutsceneComplete(start.CutsceneId, packet.ProcessId, player.PlayerId);
        
        if (Votes.TryGetValue(packet.ProcessId, out var votes))
        {
            votes.Remove(player.NetId);
            BroadcastVotes(start, votes);
        }
    }


    /// <summary>
    /// 已结束的过场本该在结尾运行什么，以及该动作的门现在是否开着
    /// （analysis/cutscene-play-and-end）。剧情过场打开的那道门，是结束动作确实运行过的
    /// 唯一可见证据，所以这行日志携带时间轴的动作 id、其中有多少注册在
    /// 控制器中、其中有多少会开门，以及原生结束之后紧接着读取的门状态。
    /// </summary>

    private static CutsceneBaseAction FindAction(CutscenesServerController controller, string actionId)
    {
        if (controller?._actions == null || string.IsNullOrEmpty(actionId)) return null;
        return controller._actions.TryGetValue(actionId, out var action) ? action : null;
    }

    private static CutsceneBaseAction FindAction(CutscenesClientController controller, string actionId)
    {
        if (controller?._actions == null || string.IsNullOrEmpty(actionId)) return null;
        return controller._actions.TryGetValue(actionId, out var action) ? action : null;
    }

    /// <summary>该动作命名的每一扇门的实时状态，以便把 "doors=1" 与 "doors=1 open" 区分开。</summary>
    private static string DoorStates(List<string> doorIds)
    {
        if (doorIds.Count == 0) return string.Empty;
        var objects = UnityEngine.Object.FindObjectsOfType<WorldInteractiveObject>(true);
        var text = new System.Text.StringBuilder(" doorState=[");
        var written = 0;
        foreach (var id in doorIds)
            foreach (var door in objects)
            {
                if (door == null || !string.Equals(door.Id, id, StringComparison.Ordinal)) continue;
                if (written++ > 0) text.Append(", ");
                text.Append(id).Append('=').Append(door.DoorState);
            }
        if (written == 0) text.Append("no live object for ").Append(doorIds.Count).Append(" id(s)");
        return text.Append(']').ToString();
    }

    internal static void OnDisconnected(FikaPlayer player)
    {
        if (player == null) return;
        DisconnectedViewers.Add(player.NetId);
        DialogEntryOccupancy.ReleaseAllFor(player.NetId);
        // 断线不等于已完成观看：重连之后仍必须续上。
        foreach (var entry in Votes.ToArray())
        {
            entry.Value.Remove(player.NetId);
            if (Cutscenes.TryGetValue(entry.Key, out var start) && start.NetIds.Contains(player.NetId))
                BroadcastVotes(start, entry.Value);
        }
    }

    private static bool IsActiveViewer(int netId)
    {
        if (DisconnectedViewers.Contains(netId)) return false;
        if (Manager?.CoopHandler?.Players == null || !Manager.CoopHandler.Players.TryGetValue(netId, out var player) ||
            player.HealthController?.IsAlive != true || Manager.CoopHandler.ExtractedPlayers.Contains(netId)) return false;
        if (player.IsYourPlayer) return !FikaBackendUtils.IsHeadless;
        var peers = new List<NetPeer>();
        FikaSingleton<FikaServer>.Instance?.NetServer.GetConnectedPeers(peers);
        return peers.Any(peer => peer.ConnectionState == ConnectionState.Connected && peer.Player?.NetId == netId);
    }
}