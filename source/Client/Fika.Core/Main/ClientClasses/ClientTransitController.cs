using Comfort.Common;
using Fika.Core.Main.BaseClasses;
using EFT.UI;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using JsonType;
using Il2CppSystem.Collections.Generic;
using EFT;
using EFT.GlobalEvents;
using EFT.Interactive;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.Patches.TransitPresence;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.Communication;

namespace Fika.Core.Main.ClientClasses;

public class ClientTransitController : FikaWorldController<NetworkTransitController>
{
    private readonly System.Action<TransitPoint, Player> _onPlayerEnter;
    private readonly System.Action<TransitPoint, Player> _onPlayerExit;
    private readonly System.Collections.Generic.Dictionary<int, TransitInitEvent> _pendingInits = new();
    private readonly System.Collections.Generic.Dictionary<int, TransitEventPacket> _pendingSnapshots = new();
    /// <summary>
    /// 本客户端发送给主机的最后一条运输区消息，使主机对一次进入或一次离开
    /// 恰好只收到一次，而不是每个触发事件都收到一次。
    /// </summary>
    private readonly System.Collections.Generic.Dictionary<string, int> _zonePresenceSent = new();

    private bool _ready;
    private bool _initializationPending;
    /// <summary>本客户端为其抬升原生运输交互提示的据点，或 -1。</summary>
    private int _interactionPoint = -1;
    /// <summary>主机最后播报的倒计时所属的据点，或 -1。</summary>
    private int _countdownPointId = -1;
    /// <summary>本客户端为自身玩家收到的最后一个倒计时值，或 -1。</summary>
    private int _countdownRemaining = -1;
    /// <summary>
    /// 原生 <c>Timers</c> 调用最后一次运行时所针对的据点与计时器界面。一个组计时器数据包
    /// 若其值与这些完全相同，就不会再次应用：原生应用器最终落在
    /// <c>LocationTransitTimerPanel.Display()</c> 加 <c>Show(seconds)</c>，而 <c>Show</c> 会重新启动
    /// 面板自身的协程（<c>BattleUITimerPanel.StopActiveCoroutines</c> /
    /// <c>ShowCountdownPanel</c>）——因此每一次冗余调用都会重播那段动画。主机会
    /// 按设计把起始值发送两次（它自己的起始行加上同一秒的原生
    /// <c>Timers</c> 跳动），没有这份缓存时两者会在同一帧内作为两次应用
    /// 抵达这里（第 8 轮：LogOutput.log:2637 与 2639，两者都是 <c>mine=20</c>）。
    /// </summary>
    private int _appliedTimerSurfacePointId = -1;
    private Il2CppSystem.Collections.Generic.Dictionary<int, ushort> _appliedTimerSurface;
    /// <summary>
    /// 承载当前倒计时、以及它所属据点的那个界面。一个界面
    /// 就够了，因为面板不是数值显示器：<c>Show(seconds)</c> 启动的倒计时
    /// 会自己逐秒递减——它启动的协程的 4.0 IL：<c>time -= Time.deltaTime</c>
    /// 每帧一次，标签由该值格式化，面板在归零时自行关闭——并且
    /// 每次调用都会再次启动那个倒计时以及面板的淡入（<c>Co_ShowInfoPanel</c> 会把
    /// canvas 的 alpha 先设为 0 再淡入）。第 9 轮就是「按值」情形的证据：
    /// <c>LogOutput.log:2922-2942</c> 对每个播报的秒都启动一次面板，这正是
    /// 用户报告的闪烁。因此该界面每个倒计时只启动一次，之后便不再动它；原生
    /// <c>Timers</c> 应用器只有在该倒计时确实重新开始、或该界面已落后于
    /// 播报值时才会再次运行。
    /// </summary>
    private CountdownSurface _countdownSurface;
    /// <summary>运行中的界面所属的据点，或 -1。</summary>
    private int _countdownSurfacePointId = -1;
    /// <summary>运行中的界面启动时所用的秒数；面板从此值往下走。</summary>
    private float _countdownSurfaceSeconds = -1f;
    /// <summary>运行中的界面启动时的未缩放时间，以便预测它自身的值。</summary>
    private float _countdownSurfaceStartedAt;
    /// <summary>因运行中的界面已自行递减而未被应用的播报值。</summary>
    private int _countdownTicksSuppressed;
    /// <summary>并非某个倒计时第一次的界面启动次数，每次各上报一次。</summary>
    private int _countdownSurfaceRestarts;
    /// <summary>最近一次重新启动已消失界面的时刻，因此这种事每秒最多发生一次。</summary>
    private float _countdownSurfaceReshowAt = -1f;
    /// <summary>
    /// 运行中界面的标签可以静止多久（缩放时间），超过后本客户端就不再
    /// 相信面板会自行倒计时。4.0 版本会（它的协程每帧减去
    /// <c>Time.deltaTime</c>），5.0 也仍然带着 <c>Co_Countdown</c>、
    /// <c>_countdownCoroutine</c> 和 "Transition in {0:F1}" 字面量——但 5.0 的原生代码
    /// 在这里无法反汇编，所以这个假设是在运行中的游戏上检验的，而不是被相信的。
    /// </summary>
    private const float CountdownLabelStallSeconds = 2.5f;
    /// <summary>运行中界面上一次查看时的标签，以及读取它时的缩放时间。</summary>
    private string _countdownSurfaceLabel;
    private float _countdownSurfaceLabelAt;
    /// <summary>已上报的停滞界面次数，使该行每个倒计时只写一次。</summary>
    private int _countdownSurfaceStalls;
    /// <summary>
    /// 运行中的界面没有推动它的标签时置位：此构建不会自行倒计时，因此
    /// 在本次倒计时的剩余时间里每个播报值都会被重新应用。真正会走倒计时的面板
    /// 永远不会触发它；没有它，不会走倒计时的面板就会僵在第一个值上。
    /// </summary>
    private bool _countdownSurfaceStalled;

    /// <summary>
    /// (analysis/transit-terminal-and-countdown) 本机因主机的播报未包含它而隐藏了其倒计时的
    /// 据点（主机暂停了倒计时，或本机在任何地方都没有登记）。
    /// 因此下一条包含本机的播报就是一次恢复，并按恢复上报。
    /// </summary>
    private int _countdownPausedPointId = -1;

    /// <summary>运行中的倒计时由哪种界面承载。</summary>
    private enum CountdownSurface
    {
        /// <summary>本次倒计时尚未启动任何东西。</summary>
        None = 0,
        /// <summary>原生应用器启动了面板——正常情形。</summary>
        Native = 1,
        /// <summary>原生应用器没有打开界面，因此本客户端自己启动了一次。</summary>
        Own = 2
    }
    /// <summary>被跳过的冗余原生应用次数，以及其中已上报的数量。</summary>
    private int _countdownRepeatsSkipped;
    private int _countdownRepeatsLogged;
    /// <summary>顺序无关的比较每个会话只在第一个组计时器上验证一次。</summary>
    private bool _timerComparisonChecked;
    /// <summary>当前交互尝试的可见点刷新次数；一个隐藏点之后可能变为可见。</summary>
    private int _interactionVisibilityRefreshes;
    /// <summary>尚未完成原生步骤的一次按压的开始时刻，或 -1。</summary>
    private float _pressStartedAt = -1f;
    private int _pressPointId = -1;
    /// <summary>每次按压只上报一次停滞。</summary>
    private bool _pressStallReported;
    /// <summary>本次战局中未产生任何主机请求的按压次数；判定行以它为键。</summary>
    private int _pressStalls;
    /// <summary>
    /// (analysis/client-transit-terminal) 本次战局中因原生按压步骤没有产生请求而发出的
    /// 兜底交互请求次数；以 <see cref="true"/> 为上限。
    /// </summary>
    private int _pressFallbacks;
    /// <summary>一旦运行中的按压被兜底请求应答即为 true，因此每次按压只发送一次。</summary>
    private bool _pressFallbackSent;
    /// <summary>待处理初始化的每次挂起写一行，恢复时再写一行。</summary>
    private bool _pendingInitReported;
    private readonly System.Collections.Generic.Queue<(TransitEventPacket Packet, float ReceivedAt)> _pendingEvents = new();

    public ClientTransitController(TransitGlobalSettings settings, Il2CppReferenceArray<LocationSettings.Location.TransitParameters> parameters, Profile profile, LocalRaidSettings localRaidSettings)
        : base(new NetworkTransitController(settings, parameters))
    {
        _onPlayerEnter = (System.Action<TransitPoint, Player>)OnClientPlayerEnter;
        _onPlayerExit = (System.Action<TransitPoint, Player>)OnClientPlayerExit;
        Native.OnPlayerEnter += _onPlayerEnter;
        // EFT.TransitController.OnPlayerExit 是两种控制器共用的
        // 基类区域退出回调；客户端战局的 NetworkTransitController 继承它。
        Native.OnPlayerExit += _onPlayerExit;
        Native.summonedTransits[profile.Id] = new Transit(localRaidSettings.transition.transitionRaidId,
            localRaidSettings.transition.transitionCount, FikaTransitHistory.AppendLocation(localRaidSettings),
            localRaidSettings.transitionType.HasFlagNoBox(ELocationTransition.Event));
        Native.TransferItemsController.InitItemControllerServer(FikaGlobals.TransitTraderId, FikaGlobals.TransitTraderName);
        foreach (var point in Native.pointsById.Values) point.gameObject.SetActive(true);
    }

    internal void OnInitEvent(TransitInitEvent data)
    {
        var localPlayer = GamePlayerOwner.MyPlayer ?? FikaSingleton<Networking.FikaClient>.Instance?.MyPlayer;
        if (localPlayer != null && localPlayer.RaidId != data.PlayerRaidId) return;
        // 原生 Init 可能在计时器面板 / PlayerOwner 出现之前到达。
        if (GamePlayerOwner.MyPlayer == null || !_ready ||
            !NetworkTransitController.IsTargetPlayer(data.PlayerRaidId, out var player))
        {
            _pendingInits[data.PlayerRaidId] = data;
            return;
        }
        _pendingInits.Remove(data.PlayerRaidId);
        Native._completedQuestRequirementMetByPointId = data.CompletedQuestRequirementMetByPointId ?? new();
        if (Native.summonedTransits.TryGetValue(player.ProfileId, out var transit))
        {
            transit.count = data.TransitionCount;
            transit.events = data.EventPlayer;
        }
        var list = Native.GetTransitPoints(data.Points, player.Side);
        Native.SetTimers(list, player, false);
        Native.HandleExits(list, player);
    }

    internal void ReceiveSnapshot(TransitEventPacket packet)
    {
        if (packet.TransitEvent?.TryCast<TransitInitEvent>() is not { } data) return;
        // (analysis/transit-point-sync) 一次初始化的 Enabled 列表就是主机的据点状态
        // （TransitPoint_SetEnabled_Patch 在 TransitPoint.Enabled 每次变化时重发它）。它在下面
        // 的按玩家过滤之前应用，因为该状态广播并不针对任何玩家
        // （TransitEventPacket.AnyPlayerRaidId）：否则本机的据点会停留在战局
        // 起始快照上，也就是所报告的「据点无法使用」状态。
        ApplyPointState(packet.EnabledPointIds);
        if (data.PlayerRaidId == TransitEventPacket.AnyPlayerRaidId) return;
        var localPlayer = GamePlayerOwner.MyPlayer ?? FikaSingleton<Networking.FikaClient>.Instance?.MyPlayer;
        if (localPlayer != null && localPlayer.RaidId != data.PlayerRaidId) return;
        // TransitEventPacket 会被 LiteNetLib 复用：保留独立的字段。
        _pendingSnapshots[data.PlayerRaidId] = new TransitEventPacket
        {
            TransitEvent = data, EnabledPointIds = packet.EnabledPointIds,
            RegisteredPointId = packet.RegisteredPointId, KeyId = packet.KeyId, SelectedTime = packet.SelectedTime
        };
        if (_ready) ApplySnapshot();
    }

    /// <summary>
    /// 把主机初始化的启用列表应用到本机的每一个据点，双向生效：
    /// 在列表中的据点被打开，不在列表中的据点被关闭。该列表是主机的
    /// 完整状态（<c>TransitPointStateBroadcast</c> 发送 <c>pointsById</c> 的每一项），因此
    /// 本地标志可以精确跟随它；<see cref="SetPointEnabled"/> 执行每一次单独写入，
    /// 也是这里唯一改变该标志的地方。
    /// </summary>
    private void ApplyPointState(int[] enabledPointIds)
    {
        if (enabledPointIds == null) return;
        try
        {
            var points = Native.pointsById;
            if (points == null) return;
            var enabled = new System.Collections.Generic.HashSet<int>(enabledPointIds);
            foreach (var entry in points)
            {
                SetPointEnabled(entry.Key, enabled.Contains(entry.Key));
            }
        }
        catch (System.Exception exception)
        {
            FikaGlobals.LogWarning($"[SyncDiag] point-sync-failed where=client-apply " +
                $"error={exception.GetType().Name}: {exception.Message}");
        }
    }

    private void ApplySnapshot()
    {
        var player = GamePlayerOwner.MyPlayer;
        if (player == null || !_pendingSnapshots.TryGetValue(player.RaidId, out var snapshot)) return;
        var data = snapshot.TransitEvent.Cast<TransitInitEvent>();
        if (!NetworkTransitController.IsTargetPlayer(data.PlayerRaidId, out _)) return;
        _pendingSnapshots.Remove(player.RaidId);
        OnInitEvent(data);
        ApplyPointState(snapshot.EnabledPointIds);
        if (snapshot.RegisteredPointId >= 0 && Native.pointsById.ContainsKey(snapshot.RegisteredPointId))
        {
            Native.transitPlayers[player.ProfileId] = player.RaidId;
            Native.profileKeys[player.ProfileId] = snapshot.KeyId;
            Native.profileTime[player.ProfileId] = snapshot.SelectedTime;
            InteractPacket = new() { hasInteraction = true, pointId = snapshot.RegisteredPointId,
                keyId = snapshot.KeyId, time = snapshot.SelectedTime };
            Native.DisableExits(player);
            
            ClearLocalInteraction("the host already registered this player", false);
        }
        UpdateTimers();
    }

    public InteractWithTransitPacket InteractPacket { get; set; }

    internal void ReceiveEvent(TransitEventPacket packet)
    {
        var localPlayer = GamePlayerOwner.MyPlayer;
        if (!_ready || localPlayer == null ||
            !NetworkTransitController.IsTargetPlayer(localPlayer.RaidId, out _))
        {
            if (_pendingEvents.Count >= 256)
                throw new System.InvalidOperationException("Transit startup event queue exceeded its limit");
            _pendingEvents.Enqueue((new TransitEventPacket
            {
                EventType = packet.EventType, TransitEvent = packet.TransitEvent,
                TransitId = packet.TransitId, PlayerId = packet.PlayerId, Destination = packet.Destination
            }, Time.unscaledTime));
            return;
        }
        if (packet.EventType == TransitEventPacket.ETransitEventType.Extract)
        {
            HandleClientExtract(packet.TransitId, packet.PlayerId, packet.Destination);
            return;
        }

        // 主机每个 tick 都发送组计时器，但 Fika 构造的事件从未经过
        // 游戏的全局事件总线，因此下面的 SyncEventFromServer.Invoke() 触达不到任何订阅者，
        // 客户端也就永远显示不出运输倒计时。这里显式分派原生重写方法。
        if (packet.TransitEvent?.TryCast<TransitGroupTimerEvent>() is { } groupTimer)
        {
            HandleGroupTimer(groupTimer);
            return;
        }
        if (packet.TransitEvent?.TryCast<TransitGroupSizeEvent>() is { } groupSize)
        {
            try
            {
                
                Native.Sizes(groupSize.PointId, groupSize.Sizes);
            }
            catch (System.Exception exception)
            {
                            }

            return;
        }

        // TransitInteractionEvent 在 SPT 5 中有两个私有的原生订阅者：
        // NetworkTransitController.Interact 更新交互/据点状态，
        // 而 ManageAntiLeaveFlag 更新正常退出的锁定。仅调用
        // SyncEventFromServer.Invoke() 对 Fika 创建的数据包是不够的，
        // 因为它们没有经过游戏的全局事件总线。
        // 这里直接调用原生处理函数，使 Confirm/Hide 与 InactivePoint
        // 产生与官方网络事件相同的效果。
        if (packet.TransitEvent?.TryCast<TransitInteractionEvent>() is { } interaction)
        {
            HandleInteractionEvent(interaction, packet);
            Native.Interact(interaction);
            Native.ManageAntiLeaveFlag(interaction);
            return;
        }

        if (packet.TransitEvent?.TryCast<TransitMessagesEvent>() is { } messages)
        {
            // 原生处理函数（NetworkTransitController.Message）只显示警告；本机的
            // 恢复逻辑是 Fika 自己的那一半并先运行，因此它的日志行会紧挨着
            // 玩家看到的通知出现。
            HandleMessagesEvent(messages);
            packet.TransitEvent.Invoke();
            return;
        }

        packet.TransitEvent?.Invoke();
    }

    /// <summary>
    /// 主机为某个据点播报的倒计时，以及客户端应用它的方式。这里发生三件事，
    /// 三件都会被上报：
    /// <list type="number">
    /// <item>原生的 <c>NetworkTransitController.Timers(pointId, timers)</c> 被运行，正是
    /// <c>TransitGroupTimerEvent</c> 的原生处理函数本会调用的东西（Fika 构造的事件
    /// 通过全局事件总线触达不到任何原生订阅者）；</item>
    /// <item>检查该原生调用是否真的把计时器面板放上了屏幕，若没有则从这里驱动面板——
    /// 用的正是主机和原生本地控制器所采用的 <c>Display()</c> 然后 <c>Show(seconds)</c> 顺序，
    /// 并在它前面加上 <c>FikaUiPanels.CanDrive</c>；</item>
    /// <item>记录本机自己 raid id 对应的值，这是下一轮的判断依据：
    /// 一个没有 <c>self</c> 条目的字典，是本客户端任何计时器面板都无法使用的字典
    /// （第 7 轮：客户端显示了组规模，却完全没有倒计时）。</item>
    /// </list>
    /// </summary>
    private void HandleGroupTimer(TransitGroupTimerEvent groupTimer)
    {
        var pointId = groupTimer.PointId;
        var timers = groupTimer.Timers;
        FikaGlobals.LogWarning($"[TransitDiag] client group-timer point={pointId} timers={(timers == null ? "null" : timers.Count.ToString())}");
        var player = LocalPlayer;
        var own = -1;
        try
        {
            if (player != null && timers != null && timers.ContainsKey(player.RaidId)) own = timers[player.RaidId];
        }
        catch (System.Exception exception)
        {
                    }

        var nativeResult = "applied";
        // 正好在本次跳动运行了原生应用器时为 true——只有那次调用会启动面板。
        var applied = false;
        try
        {
            // 原生应用器最终落在面板的 Display() 与 Show(seconds)；Show 会重新启动
            // 面板自身的协程，因此对同一个值的第二次应用会重播那段
            // 动画——也就是报告的「每个 tick 闪烁一次」。主机会按设计把起始值
            // 发送两次（它自己的起始行加上同一秒的原生 Timers 跳动，
            // 第 8 轮的 LogOutput.log:2637/2639），因此这里跳过相等的值。已应用的
            // 界面按据点缓存，并随倒计时一起失效。
            //
            // 该比较以 raid id 为键（TransitStartGate.SameTimers），并且刻意不以
            // DescribeCountdownTimers 打印的文本为键：第 8 轮那两行带着相同的倒计时，却有两种
            // 不同的键顺序（"1=20 2=20" 与 "2=20 1=20"），所以任何基于枚举顺序的比较
            // 都会把它们判为不同，并把同一个值应用两次——而这正是这个守卫
            // 存在的理由。
            if (!_timerComparisonChecked)
            {
                _timerComparisonChecked = true;
                var comparisonOk = TransitStartGate.SelfCheckSameTimers(out var comparisonDetail);
                
            }

            var unchanged = _appliedTimerSurface != null &&
                _appliedTimerSurfacePointId == pointId &&
                TransitStartGate.SameTimers(_appliedTimerSurface, timers);
            // 已经在屏幕上的倒计时会自己逐秒递减（见字段
            // _countdownSurface 的注释），因此主机的每秒值不会再被应用：原生
            // 应用器会把面板的倒计时以及它的淡入从头再启动一次。
            // 只有播报的倒计时确实重新开始（值变大了）时才会再次应用原生值，
            // 或者当该界面偏离该值超过容差时——那是丢包，
            // 而不是正常的那一秒。
            var secondsLeft = TransitStartGate.SurfacedSecondsLeft(pointId, _countdownSurfacePointId,
                _countdownSurfaceSeconds, _countdownSurfaceStartedAt);
            var stalled = _countdownSurfaceStalled;
            var action = "apply";
            var reason = "the first value of this countdown";
            if (unchanged)
            {
                action = "unchanged";
            }
            else if (stalled && secondsLeft >= 0f && own >= 0)
            {
                reason = "the surface did not move its label, so this build does not count down by itself";
            }
            else if (secondsLeft >= 0f && own >= 0)
            {
                if (own > _countdownSurfaceSeconds + 0.5f)
                {
                    reason = $"the countdown restarted (the surface runs from {_countdownSurfaceSeconds:0.0}s)";
                }
                else if (System.Math.Abs(own - secondsLeft) > TransitStartGate.PanelToleranceSeconds)
                {
                    reason = $"the surface was {System.Math.Abs(own - secondsLeft):0.0}s off what the host announces";
                }
                else
                {
                    action = "suppressed";
                }
            }

            if (action == "unchanged")
            {
                _countdownRepeatsSkipped++;
                nativeResult = "unchanged (the same values were already applied)";
            }
            else if (action == "suppressed")
            {
                _countdownTicksSuppressed++;
                nativeResult = $"suppressed (the panel walks its own countdown, it is at {secondsLeft:0.0}s)";
                _appliedTimerSurfacePointId = pointId;
                _appliedTimerSurface = timers;
            }
            else
            {
                if (stalled) nativeResult = "applied (the surface does not count down by itself, every value is applied)";
                Native.Timers(pointId, timers);
                applied = true;
                _appliedTimerSurfacePointId = pointId;
                _appliedTimerSurface = timers;
                if (secondsLeft >= 0f && !stalled)
                {
                    _countdownSurfaceRestarts++;
                    
                }
            }
        }
        catch (System.Exception exception)
        {
            nativeResult = "error:" + exception.GetType().Name;
                    }

        // (analysis/transit-terminal-and-countdown) 一个组计时器字典若没有本机的条目，
        // 就说明主机的倒计时并未承载此玩家：要么主机暂停了它（一名已登记的
        // 队友离开了该据点——本轮规则），要么本机已不再持有该据点的
        // 任何登记。本机的界面绝不应为一场并未进行的转移继续倒计时，因此
        // 在这里关闭它；下一条包含本机的播报会从冻结的值重新启动它，
        // 并按同一次倒计时的恢复来上报。
        if (own < 0)
        {
            var wasShowing = _countdownPointId == pointId || _countdownSurfacePointId == pointId;
            _countdownPointId = pointId;
            _countdownRemaining = -1;
            _countdownPausedPointId = pointId;
            if (wasShowing)
            {
                HideLocalCountdown(pointId,
                    "the host's countdown does not carry this machine (it was paused, or this machine is registered nowhere)",
                    "group timer without an entry for this machine; panel " + DescribePanelState());
            }

            

            return;
        }

        if (_countdownPausedPointId == pointId)
        {
            _countdownPausedPointId = -1;
            
        }

        _countdownPointId = pointId;
        _countdownRemaining = own;

        // 另一个据点的界面绝不会沿用：它的值预测只为
        // 它被启动时对应的据点查询，因此新据点总会在下面启动一个新界面。
        var panel = DriveCountdownPanel(pointId, own, applied);

        // (analysis/terminal-transit-mechanism) 「为什么显示 / 不显示倒计时」的客户端侧判断，
        // 在该判断每次变化时写入，且无需任何开关即可读取：面板状态、
        // 我们的界面、主机播报的值，以及标签显示的值。
        ReportCountdownSurface(pointId, own, panel);
        if (nativeResult != "unchanged (the same values were already applied)")
        {
            
        }
        else if (_countdownRepeatsLogged < 3)
        {
            _countdownRepeatsLogged++;
            
        }
    }

    /// <summary>
    /// (analysis/terminal-transit-mechanism) 客户端侧对「本机为什么显示运输
    /// 倒计时，或者为什么不显示」的回答。该回答每次变化一行，绝不每个 tick 一行，而且它不在
    /// 'Coop / Diagnostics' 之后——2026-10-13 的几轮就是关着该开关跑的，这正是
    /// 在主机的播报明显到达的情况下却无法从日志里判定倒计时问题的原因。
    /// <para>
    /// 该行携带判定它所需的四个事实：面板自身的状态及其激活链（一个
    /// 处于惰性层级中的元素永远无法被驱动）、由哪种界面承载该倒计时、
    /// 主机为本机播报的值，以及面板标签此刻显示的值（两者都为 -1 / none 表示
    /// 倒计时确实不在屏幕上）。<paramref name="panelResult"/> 就是本次 tick 的驱动
    /// 所返回的同一个标签，因此一次拒绝会自己说明自己。
    /// </para>
    /// </summary>
    private void ReportCountdownSurface(int pointId, int own, string panelResult)
    {
        try
        {
            var signature = pointId + "|" + own + "|" + _countdownSurface + "|" + _countdownSurfacePointId + "|" +
                DescribePanelState() + "|" + panelResult;
            

            
        }
        catch (System.Exception exception)
        {
                    }
    }

    /// <summary>
    /// 在驱动之前把本机的运输倒计时面板带到在线状态，前提是该行为已开启。
    /// 开关是 <see cref="true"/>；而调用本身，
    /// <see cref="FikaUiPanels.TryActivateForDrive"/>，在惰性处于面板之上时会拒绝强制任何
    /// 东西，改为上报那条链。
    /// </summary>
    private static bool TryBringCountdownPanelOnline(EFT.UI.LocationTransitTimerPanel panel, string site)
    {
        return FikaUiPanels.TryActivateForDrive(panel, "ClientTransitController.TransitTimer." + site);
    }

    /// <summary>运输倒计时面板及其激活链，用于判定行。只读。</summary>
    private static string DescribePanelState()
    {
        try
        {
            var gameUi = MonoBehaviourSingleton<GameUI>.Instance;
            if (gameUi == null) return "no GameUI";
            var panel = gameUi.LocationTransitTimerPanel;
            if (panel == null) return "no timer panel";
            return FikaUiPanels.DescribeChain(panel);
        }
        catch (System.Exception exception)
        {
            return "unreadable:" + exception.GetType().Name;
        }
    }

    /// <summary>运行中的计时器界面所显示的文本，或 'none'。只读，绝不抛异常。</summary>
    private static string DescribePanelLabel()
    {
        try
        {
            var gameUi = MonoBehaviourSingleton<GameUI>.Instance;
            var panel = gameUi == null ? null : gameUi.LocationTransitTimerPanel;
            if (panel == null) return "none";
            var label = TryReadCountdownLabel(panel);
            return string.IsNullOrEmpty(label) ? "empty" : "'" + label + "'";
        }
        catch (System.Exception)
        {
            return "unreadable";
        }
    }

    /// <summary>
    /// 上报本次 tick 的倒计时界面，并在它尚未运行时启动它。
    /// <para>
    /// <c>LocationTransitTimerPanel.Show(seconds)</c> 不是数值显示器——它会启动一个
    /// 自己逐秒递减的倒计时，并重新启动面板的淡入，因此对一个已经在屏幕上的
    /// 值再启动一次就是一次闪烁（<c>LogOutput.log:2922-2942</c> 就是每个播报的秒
    /// 启动一次）。因此该界面每个倒计时只启动一次，之后便不再动它。
    /// <paramref name="nativeRan"/> 正好在本次 tick 真的把值应用给原生
    /// 控制器时为 true，而那也正是最终落在 <c>Display()</c> + <c>Show(seconds)</c> 的调用；只有当
    /// 那次调用把面板留在关闭状态时，才会在这里按同样的顺序发出同一对调用。一个在
    /// 倒计时运行期间消失的界面会用剩下的秒数再启动一次，每秒
    /// 最多一次——那是状态变化，绝不是每个 tick 的驱动。
    /// </para>
    /// <para>
    /// (analysis/terminal-transit-mechanism) 这是倒计时同步的<b>那个</b>客户端一半，也是
    /// 用户在主机会倒计时时却从未看到客户端倒计时的原因：运输计时器面板不是
    /// 本移植在被跳过的倒计时体之后运行的 HUD 恢复的一部分（那次恢复只覆盖
    /// 提取计时器面板和战斗 UI 屏幕），因此 'LocationTransitTimerPanel' 整场战局
    /// 都保持未激活。对未激活的元素驱动倒计时只能被拒绝——引擎不会在它上面启动
    /// 协程，而 <see cref="FikaUiPanels.CanDrive"/> 正确地拒绝发出一次——这就使得
    /// 主机的播报不断到达、被计数，却始终没被绘制出来。因此面板先被
    /// 带入在线状态（<see cref="FikaUiPanels.TryActivateForDrive"/>，每个倒计时一次），然后
    /// 才被驱动。
    /// </para>
    /// </summary>
    private string DriveCountdownPanel(int pointId, int seconds, bool nativeRan)
    {
        if (seconds < 0) return "no entry for this machine";
        try
        {
            var gameUi = MonoBehaviourSingleton<GameUI>.Instance;
            if (gameUi == null) return "no GameUI";
            var panel = gameUi.LocationTransitTimerPanel;
            if (panel == null) return "no timer panel";

            // (analysis/terminal-transit-mechanism) 一个 HUD 恢复未覆盖此面板的客户端，
            // 会把倒计时挂在未激活的元素上：在该状态下，原生应用和下面本地的那一对调用
            // 都会被引擎拒绝。这里每个倒计时把面板打开一次——这是引擎接受
            // <c>Display()</c>/<c>Show()</c> 所启动协程所需的唯一调用——而当它做不到时
            // （未激活的祖先不会被强制激活）会上报原因。
            if (_countdownSurfacePointId != pointId)
            {
                TryBringCountdownPanelOnline(panel, "start." + pointId);
            }

            var onScreen = panel.gameObject != null && panel.gameObject.activeInHierarchy;

            if (nativeRan)
            {
                // 原生应用器运行了（它最终落在 Display + Show）：面板已用该值
                // 启动，并自行往下走。本次倒计时不再发出别的调用。
                if (onScreen)
                {
                    StartCountdownSurface(CountdownSurface.Native, pointId, seconds);
                    return $"native (started once with {seconds}s, the panel walks it down)";
                }

                // 原生应用器没有把界面放上屏幕。这里先请求控制器自己的公告
                // （原生本地控制器也这么做），然后按同样的顺序发出与原生应用器相同的
                // Display() + Show(seconds) 那一对调用。<c>CanDrive</c> 依然位于
                // <c>Show</c> 之前：对 Unity 拒绝在其上启动协程的对象，每次尝试都会由
                // 它写出一个红色的引擎错误。
                Native.ShowPanel(pointId);
                onScreen = panel.gameObject != null && panel.gameObject.activeInHierarchy;
                if (onScreen || TryBringCountdownPanelOnline(panel, "native-left-it-off." + pointId))
                {
                    panel.Display();
                }

                if (!FikaUiPanels.CanDrive(panel, "ClientTransitController.TransitTimer.Show",
                        "point=" + pointId + " onScreen=" + onScreen + " chain=" + FikaUiPanels.DescribeChain(panel)))
                {
                    return "panel not drivable";
                }

                panel.Show((float)seconds);
                StartCountdownSurface(CountdownSurface.Own, pointId, seconds);
                
                return $"own (started once with {seconds}s)";
            }

            var secondsLeft = TransitStartGate.SurfacedSecondsLeft(pointId, _countdownSurfacePointId,
                _countdownSurfaceSeconds, _countdownSurfaceStartedAt);
            if (secondsLeft < 0f)
            {
                // 该据点没有界面在运行：没有应用，屏幕上就什么都没有，而
                // 下一次值变化会再次应用并上报它。
                return "none (no surface was started for this countdown yet)";
            }

            if (onScreen)
            {
                return WatchCountdownLabel(panel, pointId,
                    $"{DescribeCountdownSurfaceOwner()} (self-walking, at {secondsLeft:0.0}s, not started again)");
            }

            // 界面在倒计时运行期间消失了（HUD 重建、面板被关闭）：它会用剩下的
            // 秒数再启动一次——那是状态变化，绝不是每个 tick 的驱动。
            var owner = DescribeCountdownSurfaceOwner();
            if (Time.unscaledTime - _countdownSurfaceReshowAt < 1f || secondsLeft <= 0.5f)
            {
                return $"{owner} (surface off, a re-start is not due)";
            }

            _countdownSurfaceReshowAt = Time.unscaledTime;
            if (TryBringCountdownPanelOnline(panel, "reshow." + pointId))
            {
                panel.Display();
            }

            if (!FikaUiPanels.CanDrive(panel, "ClientTransitController.TransitTimer.Show",
                    "point=" + pointId + " re-start chain=" + FikaUiPanels.DescribeChain(panel)))
            {
                return owner + " (surface off, the re-start was refused)";
            }

            panel.Show(secondsLeft);
            StartCountdownSurface(CountdownSurface.Own, pointId, secondsLeft);
            
            return $"own (started once more with {secondsLeft:0.0}s, the surface was off)";
        }
        catch (System.Exception exception)
        {
                        return "error:" + exception.GetType().Name;
        }
    }

    /// <summary>
    /// 记录某个倒计时刚刚启动时所处的界面。它启动时所用的值就是
    /// 面板往下走的起点，因此每秒一次的播报可以被识别为界面自身的值。
    /// </summary>
    private void StartCountdownSurface(CountdownSurface surface, int pointId, float seconds)
    {
        if (_countdownSurfacePointId != pointId)
        {
            // 另一个据点的界面：旧倒计时的标签采样和停滞判定
            // 绝不会沿用到这一个。
            _countdownSurfaceStalled = false;
            _countdownSurfaceLabel = null;
            _countdownSurfaceStalls = 0;
        }

        _countdownSurface = surface;
        _countdownSurfacePointId = pointId;
        _countdownSurfaceSeconds = seconds;
        _countdownSurfaceStartedAt = Time.unscaledTime;
        _countdownSurfaceReshowAt = Time.unscaledTime;
    }

    /// <summary>
    /// 用于日志行的倒计时界面：由哪一种承载该倒计时，以及它此刻
    /// 显示多少秒（倒计时首次启动之前为 <c>none</c>）。
    /// </summary>
    private string DescribeCountdownSurface()
    {
        if (_countdownSurface == CountdownSurface.None || _countdownSurfaceSeconds < 0f) return "none";
        var secondsLeft = _countdownSurfaceSeconds - (Time.unscaledTime - _countdownSurfaceStartedAt);
        return $"{DescribeCountdownSurfaceOwner()} at {secondsLeft:0.0}s";
    }

    /// <summary>本客户端启动了该界面时为 <c>own</c>，由原生应用器启动时为 <c>native</c>。</summary>
    private string DescribeCountdownSurfaceOwner() => _countdownSurface == CountdownSurface.Own ? "own" : "native";

    /// <summary>
    /// 观察运行中界面的标签，并随 tick 上报它，使下一轮的日志
    /// 能显示这个数字自己在动。一个静止超过
    /// <see cref="CountdownLabelStallSeconds"/> 的标签意味着此构建终究不会走倒计时：
    /// 从下一个 tick 起，每个播报值都会被重新应用——得到一个带旧闪烁的可见
    /// 倒计时，而不是一个僵住的数字。只读，且绝不抛异常。
    /// </summary>
    private string WatchCountdownLabel(EFT.UI.LocationTransitTimerPanel panel, int pointId, string surfaceLine)
    {
        try
        {
            var label = TryReadCountdownLabel(panel);
            if (label == null) return surfaceLine + " (the label could not be read)";

            var now = Time.time;
            if (label != _countdownSurfaceLabel)
            {
                _countdownSurfaceLabel = label;
                _countdownSurfaceLabelAt = now;
                return surfaceLine + " (label '" + label + "')";
            }

            if (_countdownSurfaceStalled || now - _countdownSurfaceLabelAt < CountdownLabelStallSeconds)
            {
                return surfaceLine + " (label '" + label + "')";
            }

            _countdownSurfaceStalled = true;
            _countdownSurfaceStalls++;
            
            return surfaceLine + $" (the label stood still at '{label}' for {now - _countdownSurfaceLabelAt:0.0}s, " +
                $"values are applied again, stall #{_countdownSurfaceStalls})";
        }
        catch (System.Exception exception)
        {
            return surfaceLine + " (the label check failed: " + exception.GetType().Name + ")";
        }
    }

    /// <summary>
    /// 计时器界面的标签（<c>LocationTransitTimerPanel._extractionLabel.text</c>，即
    /// 倒计时协程写入的文本，例如 "Transition in 19.4"），无法读取时为 null。只读。
    /// </summary>
    private static string TryReadCountdownLabel(EFT.UI.LocationTransitTimerPanel panel)
    {
        try
        {
            var label = panel._extractionLabel;
            if (label == null) return null;
            var text = label.text;
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    /// <summary>用于诊断的 raid-id 到数量字典的紧凑视图。</summary>
    private static string DescribeGroupSizes(Il2CppSystem.Collections.Generic.Dictionary<int, byte> sizes)
    {
        if (sizes == null) return "null";
        var builder = new System.Text.StringBuilder();
        foreach (var entry in sizes)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(entry.Key).Append('=').Append(entry.Value);
        }
        return builder.Length == 0 ? "empty" : builder.ToString();
    }

    /// <summary>用于诊断的 raid-id 到秒数字典的紧凑视图。</summary>
    private static string DescribeCountdownTimers(Il2CppSystem.Collections.Generic.Dictionary<int, ushort> timers)
    {
        if (timers == null) return "null";
        var builder = new System.Text.StringBuilder();
        foreach (var entry in timers)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(entry.Key).Append('=').Append(entry.Value);
        }
        return builder.Length == 0 ? "empty" : builder.ToString();
    }

    /// <summary>
    /// 本机写入某个据点 <c>Enabled</c> 标志的唯一位置，双向生效
    /// （<c>EFT.Interactive.TransitPoint.Enabled</c>，interop 转储 212407——与主机广播、原生按压路径
    /// 读取的是同一个成员）。它取代了只负责启用的 MirrorPointEnabled：主机的
    /// 状态如今是双向的，因此主机重新关闭的据点在这里也必须关闭。
    /// <para>
    /// 主机的 Show/Confirm/CheckAccess 应答以及被接受的 Extract 仍会以
    /// <c>true</c> 调用它：这样的应答就是该据点对此玩家开放的证明，因此本地标志
    /// 会在下一条状态广播到达之前就被镜像。
    /// </para>
    /// </summary>
    /// <returns>标志确实发生变化时为 true。</returns>
    internal bool SetPointEnabled(int pointId, bool enabled)
    {
        try
        {
            if (Native.pointsById == null || !Native.pointsById.TryGetValue(pointId, out var point) || point == null)
            {
                return false;
            }

            if (point.Enabled == enabled) return false;
            point.Enabled = enabled;
            FikaGlobals.LogWarning($"[SyncDiag] point-apply id={pointId} enabled={(enabled ? "true" : "false")}");
            return true;
        }
        catch (System.Exception exception)
        {
            FikaGlobals.LogWarning($"[SyncDiag] point-sync-failed where=set-point id={pointId} " +
                $"enabled={(enabled ? "true" : "false")} error={exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    internal bool IsReady => _ready;

    private void OnClientPlayerEnter(TransitPoint point, Player player)
    {
        if (!Native.transitPlayers.ContainsKey(player.ProfileId))
            player.TryCast<FikaPlayer>()?.UpdateBtrTraderServiceData().HandleExceptions();
        // (analysis/client-transit-terminal) 原生本地控制器正是在这一刻，为它拥有的玩家
        // 创建逐玩家的转移容器（4.0
        // LocalGameTransitControllerClass.cs:186-205，OnPlayerEnter -> 提示之前的 InitPlayerStash）。
        // 客户端从不运行那个回调，因此这一半在这里应用。
        EnsureLocalTransferStash(point, player, "entered the point (controller event)");
        // 原生区域回调（EFT.TransitController.OnPlayerEnter，由据点
        // 触发器引发）同时也会告诉主机本客户端的玩家站在哪里。
        ReportZonePresence(player, point, true, "controller event");
    }

    /// <summary>
    /// (analysis/client-transit-terminal) 当本地玩家到达一个没有可用容器的据点时，
    /// 创建本机该玩家的转移容器——这是本地控制器在进入时应用、而本移植至今
    /// 只为主机应用过的原生调用（主机会用 SendStashes 发布自己的容器，
    /// 而一个从未到达、或以 0x0 网格到达的容器无法承载
    /// 此玩家的装备）。
    /// <para>
    /// 只创建缺失的或 0x0 的容器，绝不创建已经装有物品的容器：
    /// <c>InitPlayerStash</c> 会清空该 profile 的容器。每产生一个结果写一行不受开关限制的
    /// <c>[TransitDiag] dest=... gear-transfer applied=yes|no side=client</c> 行，形状与
    /// 主机已经写入的相同。
    /// </para>
    /// </summary>
    private void EnsureLocalTransferStash(TransitPoint point, Player player, string source)
    {
        if (point?.parameters == null) return;
        if (player == null || !player.IsYourPlayer) return;
        var profileId = player.ProfileId;
        if (string.IsNullOrEmpty(profileId)) return;

        var destination = TransitDestination.Resolve(point, out _);
        try
        {
            var controller = Native.TransferItemsController;
            if (controller == null)
            {
                

                return;
            }

            var containers = -1;
            var found = false;
            var width = 0;
            var height = 0;
            try
            {
                var list = controller._transferContainers;
                if (list != null)
                {
                    containers = list.Count;
                    for (var i = 0; i < list.Count; i++)
                    {
                        var candidate = list[i];
                        if (candidate == null || candidate.Id != profileId) continue;
                        found = true;
                        var grid = candidate.Grid;
                        if (grid != null)
                        {
                            width = grid.GridWidth;
                            height = grid.GridHeight;
                        }

                        break;
                    }
                }
            }
            catch (System.Exception)
            {
            }

            if (found && width > 0 && height > 0) return;

            controller.InitPlayerStash(player);
            var newWidth = 0;
            var newHeight = 0;
            try
            {
                var list = controller._transferContainers;
                if (list != null)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        var candidate = list[i];
                        if (candidate == null || candidate.Id != profileId) continue;
                        var grid = candidate.Grid;
                        if (grid != null)
                        {
                            newWidth = grid.GridWidth;
                            newHeight = grid.GridHeight;
                        }

                        break;
                    }
                }
            }
            catch (System.Exception)
            {
            }

            
        }
        catch (System.Exception exception)
        {
            
        }
    }

    private void OnClientPlayerExit(TransitPoint point, Player player) =>
        ReportZonePresence(player, point, false, "controller event");

    /// <summary>
    /// 本地区域观测变成一条消息的唯一位置，被原生回调（本
    /// 类）使用，也被
    /// <see cref="Fika.Core.Main.Patches.TransitPresence.TransitPresenceReporter"/> 中的触发器钩子与几何钩子使用。只有本地
    /// 玩家可以被上报：机器人和远程观察玩家在本机也有会进入据点
    /// 触发器的碰撞体。
    /// <para>
    /// 只有当据点授予访问权限时才上报进入，与主机自己的路径完全一致
    /// （<c>FikaHostTransitController.OnHostPlayerEnter</c> 在该情形下会先返回而不登记）；
    /// 原生 <c>PlayerEnter</c> 出于同样的原因中止。这里不验证其他任何东西——
    /// 仅凭在场永远不会授予访问权限。
    /// </para>
    /// </summary>
    internal void ReportZonePresence(Player player, TransitPoint point, bool inside, string source)
    {
        if (point?.parameters == null || player == null || !player.IsYourPlayer) return;
        var pointId = point.parameters.id;
        // 发送方会再次解析本地玩家；当它还解析不出来时，什么都不会发送，而
        // 每个调用方都会重复这条警告。只说一次，让下一次观测去上报。
        if (LocalPlayer == null)
        {
            if (TransitPresenceReporter.MarkSkipLogged("no local player"))
            {
                
            }
            return;
        }

        if (inside && !Native.TryGetAccessToLocation(player, pointId, out _))
        {
            if (TransitPresenceReporter.MarkSkipLogged(player.ProfileId + "@" + pointId + ":no access"))
            {
                
            }
            return;
        }

        SendZonePresence(pointId, inside, source);
        // 同一次观测既告诉了主机此玩家站在哪里，也抬升了本地提示：
        // 客户端不能依赖主机的 Show 事件（见 TryRaiseLocalInteraction）。
        if (inside)
        {
            TryRaiseLocalInteraction(point, player, source);
            return;
        }

        ClearLocalInteraction("the local player left point " + pointId + " (" + source + ")", false);
        // 该据点的倒计时已无法再承载此玩家，而主机会为整个组取消它
        // （用户要求 1）。本机在同一时刻停止显示它，而不是一直倒计时到
        // 空；登记本身留在这里，因为只有主机决定整次
        // 转移是否被取消（它的答复以本类处理的 Hide/Confirm 事件到达）。
        if (TransitCountdownGuard.AbortWhenSomeoneLeaves)
        {
            HideLocalCountdown(pointId, "this machine's player left the transit point", source);
        }
    }

    /// <summary>
    /// 本客户端最后一次告知主机其自身玩家在其中的据点，或 -1。这是发送方自己的
    /// 去重状态，因此几何兜底路径和原生钩子共享同一个视图，而不是
    /// 争抢谁最后上报。
    /// </summary>
    /// <summary>本机可以上报的玩家：战局的本地玩家，解析方式与发送方相同。</summary>
    internal Player LocalPlayer => GamePlayerOwner.MyPlayer ?? FikaSingleton<Networking.FikaClient>.Instance?.MyPlayer;

    internal int SentZonePoint(string profileId) =>
        _zonePresenceSent.TryGetValue(profileId, out var pointId) ? pointId : -1;

    /// <summary>
    /// 告知主机本客户端的玩家进入了（<paramref name="inZone"/>）或离开了
    /// 某个运输据点。主机自己的 <c>_playersInTransitZone</c> 映射只会看到主机
    /// 自己的玩家，因此每个远程玩家都被算作「不在区域内」，
    /// <c>CheckForPlayers</c> 于是回答 <c>TransitMessagesEvent.EType.NonAllTeammates</c>。
    /// <para>
    /// 消息量：每次真正的状态转换只发一个包。<c>PlayerEnter</c> 会把同一个据点反复
    /// 交给客户端，只要玩家还在其触发器内；而 <c>PlayerExit</c>
    /// 会为每个重叠的触发器触发，因此这里对两个方向都做了去重，只发送
    /// 状态变化。主机会对重复的进入再做一次去重，因此一个丢失或
    /// 重复的数据包都无法翻转答案。
    /// </para>
    /// </summary>
    /// <param name="pointId">运输据点；当 <paramref name="inZone"/> 为 false 时忽略。</param>
    /// <param name="inZone">进入为 true；退出、死亡或手动清除为 false。</param>
    /// <param name="source">该消息的诊断来源。</param>
    /// <param name="profileIdOverride">要上报的 profile，取代本地玩家的（客户端死亡路径）。</param>
    /// <param name="force">
    /// (analysis/transit-terminal-20261013) 即使与上次发送的报告相同也照发。
    /// 用于拒绝路径：发送方会对重复的进入去重（否则一个丢包只能靠离开并
    /// 重新进入据点来修复），而主机会以本机在其区域映射中为准来
    /// 为整组设门。
    /// </param>
    internal void SendZonePresence(int pointId, bool inZone, string source = "player", string profileIdOverride = null, bool force = false)
    {
        var player = LocalPlayer;
        if (player == null)
        {
            
            return;
        }

        var profileId = profileIdOverride ?? player.ProfileId;
        var isOwnPlayer = profileIdOverride == null || string.Equals(profileId, player.ProfileId, System.StringComparison.Ordinal);
        if (isOwnPlayer)
        {
            if (inZone)
            {
                // 已经上报过该据点，且主机此后未见过退出。
                if (!force && _zonePresenceSent.TryGetValue(profileId, out var sent) && sent == pointId) return;
                _zonePresenceSent[profileId] = pointId;
            }
            else
            {
                if (!_zonePresenceSent.Remove(profileId)) return;
            }
        }

        var client = FikaSingleton<Networking.FikaClient>.Instance;
        if (client == null)
        {
            
            return;
        }

        TransitZonePresencePacket packet = new()
        {
            ProfileId = profileId,
            PointId = inZone ? pointId : 0,
            Kind = inZone ? TransitZonePresencePacket.ETransitZonePresenceKind.In
                : TransitZonePresencePacket.ETransitZonePresenceKind.Left
        };
        client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        
    }

    /// <summary>
    /// 停止本机为某个据点运行的倒计时界面，而不触碰登记：面板用
    /// 游戏关闭它的方法关闭（<c>BattleUITimerPanel.Close</c>），界面
    /// 记账被重置，因此主机的下一次播报会从头重新启动它。
    /// <para>
    /// 用于本机自身玩家在倒计时运行期间离开据点时（用户要求 1：
    /// 倒计时不得为一个已不在据点上的玩家继续运行）。登记被
    /// 有意保留：整组转移是否被取消是主机决定的事，而
    /// 客户端从本类已处理的同一个 <c>TransitInteractionEvent</c> 中得知它。
    /// </para>
    /// </summary>
    internal void HideLocalCountdown(int pointId, string reason, string source)
    {
        if (_countdownPointId != pointId && _countdownSurfacePointId != pointId) return;
        var panel = CloseCountdownPanel(reason);
        ResetCountdownSurface();
        
    }

    /// <summary>
    /// 主机取消了某个据点的运输（用户要求 1）：本机丢弃其本地
    /// 登记、把它的退出交互还回去并关闭其倒计时面板——与原生离开路径在本地
    /// 所应用的同样三步，因此玩家可以再次交互，也可以再次撤离。
    /// </summary>
    internal void CancelLocalCountdown(int pointId, string reason, string source)
    {
        // 没有自己据点的调用方（死亡钩子）意味着正在运行的倒计时所属的据点。
        if (pointId < 0) pointId = _countdownPointId;
        var player = LocalPlayer;
        var panel = CloseCountdownPanel(reason);
        var registrationRemoved = false;
        var exitsEnabled = "no player";
        if (player != null)
        {
            try
            {
                registrationRemoved = Native.transitPlayers.Remove(player.ProfileId);
                Native.EnableExits(player);
                exitsEnabled = "True";
            }
            catch (System.Exception exception)
            {
                exitsEnabled = "error:" + exception.GetType().Name;
                            }
        }

        InteractPacket = null;
        ResetCountdownSurface();
        
    }

    /// <summary>本机持有该据点的运输登记时为 true。</summary>
    private bool HoldsLocalRegistrationFor(int pointId)
    {
        try
        {
            var player = LocalPlayer;
            return player != null && Native.transitPlayers.TryGetValue(player.ProfileId, out var registered) &&
                registered == pointId;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 关闭本机的倒计时面板。<c>LocationTransitTimerPanel</c> 是一个
    /// <c>BattleUITimerPanel</c>，而 <c>Close()</c> 是面板自身倒计时结束时调用的公开方法；一个
    /// 已经关闭的面板会按其状态上报，而不会被触碰。
    /// </summary>
    private string CloseCountdownPanel(string reason)
    {
        try
        {
            var gameUi = MonoBehaviourSingleton<GameUI>.Instance;
            var panel = gameUi == null ? null : gameUi.LocationTransitTimerPanel;
            if (panel == null) return "no timer panel";
            if (panel.gameObject == null || !panel.gameObject.activeInHierarchy) return "already off";
            panel.Close();
            return "closed";
        }
        catch (System.Exception exception)
        {
                        return "error:" + exception.GetType().Name;
        }
    }

    /// <summary>忘记运行中倒计时的界面，使下一个从头开始。</summary>
    private void ResetCountdownSurface()
    {
        _countdownPointId = -1;
        _countdownRemaining = -1;
        _countdownSurface = CountdownSurface.None;
        _countdownSurfacePointId = -1;
        _countdownSurfaceSeconds = -1f;
        _countdownSurfaceStalled = false;
        _countdownSurfaceLabel = null;
        _appliedTimerSurfacePointId = -1;
        _appliedTimerSurface = null;
    }

    /// <summary>一次按压最多可以持续多久，超过后客户端就上报它没有产生任何结果。</summary>
    private const float PressStallSeconds = 2.5f;
    /// <summary>每次交互尝试的原生可见点刷新次数。</summary>
    private const int MaxVisibilityRefreshes = 2;

    /// <summary>
    /// 运输交互的客户端一半。原生流程在拥有交互玩家的机器上抬升提示：
    /// 本地战局通过 <c>EFT.LocalTransitController.PlayerEnter</c> 到达那里
    /// （见 <c>FikaHostTransitController.OnHostPlayerEnter</c>，它在那里为自己的玩家调用 <c>ShowInteraction</c>
    /// ），而一个网络客户端则需要主机带着 <c>TransitInteractionEvent</c> 的
    /// <c>EType.Show</c>——而主机只在应答它自己原生
    /// <c>PlayerEnter</c> 的那个分支里构造该数据包，远程玩家永远到不了那里。
    /// <para>
    /// 因此该方法从本机已经拥有的观测出发去驱动同一个原生入口点：
    /// 据点触发器、控制器回调或几何兜底路径上报了本地玩家
    /// 在该据点之内，而主机也已被告知此事。访问权限、进入条件、据点的
    /// 主机授予的 <c>Enabled</c> 标志及其原生可见性都从据点读取并记录，因此一个
    /// 关闭的门会在客户端日志里点出自己的名字。这里不授予任何东西：一次按压产生的请求
    /// 由主机验证（<c>FikaHostTransitController.InteractWithTransit</c>）。
    /// </para>
    /// </summary>
    internal void TryRaiseLocalInteraction(TransitPoint point, Player player, string source)
    {
        if (point?.parameters == null || player == null || !_ready || !player.IsYourPlayer) return;
        var pointId = point.parameters.id;
        var profileId = player.ProfileId;

        if (Native.transitPlayers.ContainsKey(profileId) || Native.alreadyTransits.ContainsKey(profileId))
        {
            ClearLocalInteraction("the host already registered this player", false);
            return;
        }

        var visible = ReadFlag(() => Native.IsVisibleTransitPoint(point));
        var enabled = point.Enabled;
        var requirements = ReadFlag(() => EFT.ClientTransitController.HasMetEntryRequirements(point, player));
        string keyId;
        string access;
        try
        {
            access = Native.TryGetAccessToLocation(player, pointId, out keyId) ? "True" : "False";
        }
        catch (System.Exception exception)
        {
            access = "error:" + exception.GetType().Name;
            keyId = null;
        }

        // 一个原生尚未提升到其可见集合中的据点无法显示。原生刷新正是
        // 初始化所用的同一个调用，因此在玩家于据点等待时给它一次机会。
        if (visible == "False" && _interactionPoint != pointId && _interactionVisibilityRefreshes < MaxVisibilityRefreshes)
        {
            _interactionVisibilityRefreshes++;
            try
            {
                UpdateTimers();
            }
            catch (System.Exception exception)
            {
                if (TransitPresenceReporter.MarkSkipLogged("visibility refresh:" + exception.GetType().Name))
                {
                                    }
            }
        }

        var groupIsKnown = point._playerGroups != null && point._playerGroups.ContainsKey(profileId);
        var groupMemberCount = -1;
        try
        {
            if (groupIsKnown && point._groupSize != null)
            {
                var group = point._playerGroups[profileId];
                if (group != null && point._groupSize.ContainsKey(group)) groupMemberCount = point._groupSize[group];
            }
        }
        catch (System.Exception) { groupMemberCount = -2; }
        var maxGroupSize = ReadInt(() => point.GetMaxGroupSize());

        if (visible != "True" || !enabled || access != "True" || requirements != "True")
        {
            return;
        }

        if (_interactionPoint == pointId) return;
        _interactionPoint = pointId;
        try
        {
            Native.ShowInteraction(pointId, player, Native.GetSelectedTime());
        }
        catch (System.Exception)
        {
        }
    }

    /// <summary>
    /// 本地玩家的周期性交互检查，由
    /// <see cref="TransitPresenceReporter.Poll"/> 中的在场轮询驱动（每 <c>0.25 s</c> 一次）。<paramref name="point"/> 是
    /// 玩家所站立的据点，或 null。
    /// </summary>
    internal void ClientInteractionTick(TransitPoint point, Player player, string source)
    {
        if (player == null || !player.IsYourPlayer || !_ready) return;
        if (point?.parameters == null)
        {
            ClearLocalInteraction("the local player is not standing in a transit point anymore", false);
            CheckPressStall(null, player);
            return;
        }

        TryRaiseLocalInteraction(point, player, source);
        CheckPressStall(point, player);
    }

    /// <summary>
    /// (analysis/transit-terminal-20261013) 主机拒绝了本机的一次按压。'Messages' 是唯一一个
    /// 用「不行」应答按压的原生运输事件——<c>TransitMessagesEvent.EType.NonAllTeammates</c>
    /// 由两个主机门产生（<c>FikaHostTransitController.CheckForPlayers</c> 和
    /// <c>EveryoneInteracted</c>）——而在这一侧，它过去除了显示枚举名之外 <b>什么都不做</b>
    /// （通过 <c>NetworkTransitController.Message</c>）。没有任何东西释放本地按压闩锁，也没有任何东西
    /// 修复主机的区域映射，因此一个按压被拒绝、或区域报告丢失的客户端就完全
    /// 停在原处：屏幕上的提示还在，再按一次却显然什么也不发生。
    /// <para>
    /// 两个有界、对原生安全的反应（都可在 <see cref="TransitRecovery"/> 中开关）：本机的本地按压
    /// 闩锁被清除，使下一次按压不受限流；本机的「我站
    /// 在这个据点里」报告被重新主张一次——主机的整组门依赖该报告，而它的发送方
    /// 原本只有在完全离开并重新进入之后才会修复一个丢包。
    /// </para>
    /// </summary>
    private void HandleMessagesEvent(TransitMessagesEvent messages)
    {
        var player = LocalPlayer;
        if (player == null || messages?.Messages == null) return;
        try
        {
            foreach (var entry in messages.Messages)
            {
                if (entry.Key != player.RaidId) continue;
                var type = entry.Value;
                

                // 闩锁等待的是按压的原生确认步骤；一次被拒绝的按压已经结束，因此
                // 它绝不能让下一次按压排在它后面等待。
                _pressStartedAt = -1f;
                _pressStallReported = false;
                _pressPointId = _interactionPoint;

                if (_interactionPoint >= 0)
                {
                    SendZonePresence(_interactionPoint, true, "the host refused " + type, null, true);
                }
            }
        }
        catch (System.Exception exception)
        {
            
        }
    }

    /// <summary>
    /// (analysis/transit-terminal-and-countdown) 一次按压的据点，以及本机可以自行计算的
    /// 原生答案：该据点的目的地、它自身 <c>TransitParameters</c> 的标志，以及
    /// 原生链上的两个逐玩家门在这里是否为 true。
    /// <para>
    /// 这一行是为 Shoreline -&gt; Terminal 转移而存在的：Shoreline 位置的 <c>transits[25]</c>
    /// 是游戏中唯一一个自身条件字符串为时间窗口（'21:00 至 6:00 期间开放'）、其
    /// 目的地（terminal）完全不带 <c>AccessKeys</c>、其 <c>waitForAllParty</c> 为 <b>false</b>
    /// 而相邻的每个据点都说 true、并且是唯一一个在自身 <c>referenceConditions</c> 之外
    /// 还设置了 <c>hideIfNoKey</c> 的据点。这一切在 2026-10-13 各轮的日志里都读不到，
    /// 而这一切都决定了按压究竟能否产生一个请求。
    /// </para>
    /// </summary>
    private string DescribePointFlags(TransitPoint point, Player player)
    {
        try
        {
            var parameters = point?.parameters;
            if (parameters == null) return "point-unknown";
            var access = "unknown";
            var key = "none";
            try
            {
                access = Native.TryGetAccessToLocation(player, parameters.id, out var keyId) ? "granted" : "denied";
                key = string.IsNullOrEmpty(keyId) ? "none" : keyId;
            }
            catch (System.Exception exception)
            {
                access = "error:" + exception.GetType().Name;
            }

            var requirements = ReadFlag(() => EFT.ClientTransitController.HasMetEntryRequirements(point, player));
            return $"destination={parameters.location} target={parameters.target} time={parameters.time} " +
                $"active={parameters.active} activateAfterSec={parameters.activateAfterSec} " +
                $"waitForAllParty={parameters.waitForAllParty} hideIfNoKey={parameters.hideIfNoKey} " +
                $"access={access} key={key} entryRequirements={requirements}";
        }
        catch (System.Exception exception)
        {
            return "error:" + exception.GetType().Name + ": " + exception.Message;
        }
    }

    /// <summary>
    /// 原生按压路径在发送任何东西之前所检验的状态：
    /// <c>TransitInteractionControllerAbstractClass.method_15</c> 会带着 'NeedIdle' 消息提前返回
    /// （本地化文本中为「移动中」），条件是 <c>player.CurrentState</c> 不是空闲状态
    /// （eft40-decompiled/TransitInteractionControllerAbstractClass.cs:351-355）。因此在按压那一刻换了武器
    /// 或移动过的玩家，完全产生不了<b>任何</b>请求——这就是用户所说「也许是换武器把它停住了」的
    /// 机器侧那一半——而主机上没有任何东西能看到那次按压。
    /// </summary>
    private static string DescribeNativePressState(Player player)
    {
        try
        {
            var movementName = "none";
            try
            {
                var movement = player?.MovementContext?.CurrentState;
                movementName = movement == null ? "none" : movement.Name.ToString();
            }
            catch (System.Exception)
            {
                movementName = "unreadable";
            }

            var idle = player?.CurrentState?.TryCast<IdlePlayerState>() != null;
            return (idle ? "idle" : "not-idle") + "(movement=" + movementName + ")";
        }
        catch (System.Exception exception)
        {
            return "error:" + exception.GetType().Name;
        }
    }

    /// <summary>丢掉本客户端的提示状态，并隐藏它抬升的原生通知。</summary>
    private void ClearLocalInteraction(string reason, bool report)
    {
        _interactionVisibilityRefreshes = 0;
        if (_interactionPoint < 0) return;
        FikaGlobals.LogWarning($"[TransitDiag] client clear-interaction point={_interactionPoint} reason={reason}");
        var pointId = _interactionPoint;
        _interactionPoint = -1;
        try
        {
            Native.HideTransitInteractionNotification();
        }
        catch (System.Exception exception)
        {
            if (report && TransitPresenceReporter.MarkSkipLogged("hide:" + exception.GetType().Name))
            {
                            }
        }

    }

    /// <summary>
    /// 原生控制器刚刚为某个据点抬升了交互提示。在客户端上，那就是
    /// 主机带着 <c>EType.Show</c> 的 <c>TransitInteractionEvent</c>；在主机战局中则是主机自己的
    /// <c>PlayerEnter</c> 路径。只上报本客户端自己并未抬升的提示，因此该行
    /// 回答的是主机是否在驱动本机的交互。
    /// </summary>
    internal void NoteNativePromptShown(int pointId, Player player)
    {
        var foreign = _interactionPoint != pointId;
        _interactionPoint = pointId;
        if (!foreign) return;
        
    }

    /// <summary>
    /// 本地玩家按下了交互，因此原生 <c>EnsureConfirmed</c> 步骤
    /// （属于 <c>ShowInteraction</c>）刚刚开始。记录下来，使轮询能把一次产生了主机请求的按压
    /// 按压区分开。
    /// </summary>
    internal void NotePressAttempt(TransitPoint point, Player player)
    {
        if (point?.parameters == null || player == null) return;
        _pressStartedAt = Time.unscaledTime;
        _pressPointId = point.parameters.id;
        _pressStallReported = false;
        _pressFallbackSent = false;
    }

    /// <summary>
    /// 本地玩家按压的原生确认步骤返回了。本机的据点对象正是
    /// 该步骤所等待的东西，而主机只有在持有请求之后才会应答 <c>EType.Confirm</c>，
    /// 因此这里记录本地状态，而不是去猜测它。
    /// </summary>
    internal void NotePressCompleted(TransitPoint point, Player player)
    {
        if (point?.parameters == null || player == null || !player.IsYourPlayer) return;
        
    }

    /// <summary>
    /// 一次没有变成主机请求的按压只上报一次。据点的本地确认是本机能够独自
    /// 满足的那一步原生步骤中唯一的一块（主机的 <c>EType.Confirm</c>
    /// 应答的是一个尚不存在的请求），因此一次明显停滞的按压会在这里被完成
    /// 并在日志中具名。主机验证随后到来的请求，而访问权限、进入条件、
    /// 据点状态与区域检查都保持不变。
    /// </summary>
    private void CheckPressStall(TransitPoint point, Player player)
    {
        if (_pressStartedAt < 0f || _pressStallReported) return;
        if (Time.unscaledTime - _pressStartedAt < PressStallSeconds) return;
        _pressStallReported = true;
        _pressStartedAt = -1f;

        var inPoint = point?.parameters != null;
        var registered = false;
        try { registered = Native.transitPlayers.ContainsKey(player.ProfileId); }
        catch (System.Exception) { }
        // 已登记的玩家意味着这次按压确实到达了主机：没有停滞，无需上报。
        if (registered) return;
        var confirmed = inPoint ? ReadFlag(() => point.IsConfirmed(player.ProfileId)) : "unknown";
        // (analysis/transit-terminal-20261013) 一次完全没产生主机请求的按压，是用户报告
        // （「交互了，没有转移」）的两种形态之一。玩家不处于空闲状态时，
        // 原生按压路径什么都不发送（NeedIdle，见 DescribeNativePressState），因此该行也写明
        // 那个状态。该据点每次按压写一行，所有模式下都是如此。
        _pressStalls++;
        

        // (analysis/transit-terminal-and-countdown) 一行不受开关限制、带据点本身的行：
        // 该据点的原生标志，以及原生链在发送任何东西之前计算的两个逐玩家答案。它正是
        // 说明为什么 Terminal 据点的行为会与其它所有据点不同的那一行，写在
        // 那个无法转移的玩家的机器上。
        

        

        // (analysis/client-transit-terminal) 这次按压在本机明显没有产生任何结果。这里不
        // 重复原生步骤——而是改为发出一次它本会发出的同一个请求，使主机
        // 至少能听到这次按压。它在上面的本地确认之后发出，因此请求到达时，
        // 原生步骤所等待的那个包已经完整。
        TrySendFallbackRequest(point, player, inPoint, registered);

        if (!inPoint || confirmed != "False") return;
        try
        {
            point.SetConfirmed(player.ProfileId);
            
        }
        catch (System.Exception exception)
        {
                    }
    }

    /// <summary>
    /// (analysis/client-transit-terminal) 修复的客户端一半：按压所产生的请求。
    /// <para>
    /// 它为何存在。在 Fika 战局中，本地玩家的运输请求仅由原生按压链
    /// 产生——HUD 动作调用原生确认步骤，该步骤打开战局内的转移
    /// 界面，而界面的关闭回调调用 <c>Player.TransitInteraction</c>，
    /// <see cref="FikaPlayer"/> 会把它变成 <c>TransitInteractPacket</c>。其中每一步都是原生的
    /// 并且无声：一次没能走完它们的按压会让玩家留下提示、没有请求、没有主机
    /// 答复也没有日志行。2026-10-13 的双人轮次正是那种状态
    /// （<c>[TransitDiag] refusal ... reason=press-reached-no-host-request</c>，出现两次，主机空闲、在据点内、
    /// 据点已确认），而它阻塞了整个组：主机的严格门
    /// （<see cref="TransitStartGate.RequireAllTeammatesInteracted"/>）从未看到此玩家交互，因此
    /// 主机自己的按压同样被以 NonAllTeammates 应答——没有人转移，而主机只有在客户端
    /// 离开之后才完成运输。
    /// </para>
    /// <para>
    /// 它做什么。每次按压一次，且只在原生步骤明显没有产生任何结果（调用方
    /// 是 2.5 秒的看门狗）并且该步骤的原生前置条件仍然成立（玩家
    /// 空闲，4.0 链所应用的同一个 <c>NeedIdle</c> 检验）时，本机才发送原生
    /// 路径本会发送的那个请求：由它自己的原生构造器
    /// （<c>TransitController.GetInteractPacket</c>）生成的数据包，带本机自己的访问密钥和选定时间，
    /// 并在其前面加上 <see cref="FikaPlayer.TransitInteraction"/> 所发送的同一条区域报告。这里不授予
    /// 任何东西——主机用它已有的每一道门来验证该请求。
    /// </para>
    /// <para>每次尝试一行不受开关限制的判定行，兜底本身被拒绝时再加一行。</para>
    /// </summary>
    private void TrySendFallbackRequest(TransitPoint point, Player player, bool inPoint, bool registered)
    {
        if (_pressFallbackSent || !inPoint || registered) return;
        if (point?.parameters == null || player == null || !player.IsYourPlayer) return;

        var pointId = point.parameters.id;
        var profileId = player.ProfileId;
        try
        {
            // 本机的登记或目的地意味着这次按压毕竟到达了主机。
            if (Native.transitPlayers.ContainsKey(profileId) || Native.alreadyTransits.ContainsKey(profileId)) return;
        }
        catch (System.Exception)
        {
            return;
        }

        // 原生按压步骤只对空闲玩家继续（4.0
        // TransitInteractionControllerAbstractClass.cs:351-355 应答 NeedIdle 并返回）。兜底
        // 停留在同一个前置条件之内，而不是把一次移动中的按压变成请求。
        if (player.CurrentState?.TryCast<IdlePlayerState>() == null)
        {
            _pressFallbackSent = true;
            

            return;
        }

        if (_pressFallbacks >= 8)
        {
            _pressFallbackSent = true;
            

            return;
        }

        var client = FikaSingleton<Networking.FikaClient>.Instance;
        if (client == null)
        {
            _pressFallbackSent = true;
            

            return;
        }

        _pressFallbackSent = true;
        try
        {
            // 原生请求所携带的同样字段：本机解析出的访问密钥（一个没有访问密钥的
            // 据点会用空字符串应答）以及提示被抬升时所用的时间。
            string keyId = string.Empty;
            try
            {
                if (!Native.TryGetAccessToLocation(player, pointId, out keyId)) keyId = string.Empty;
            }
            catch (System.Exception)
            {
                keyId = string.Empty;
            }

            if (string.IsNullOrEmpty(keyId))
            {
                try
                {
                    if (Native.profileKeys.TryGetValue(profileId, out var knownKey)) keyId = knownKey ?? string.Empty;
                }
                catch (System.Exception)
                {
                }
            }

            var time = Native.GetSelectedTime();
            try
            {
                if (Native.profileTime.TryGetValue(profileId, out var knownTime) && knownTime != EDateTime.CURR) time = knownTime;
            }
            catch (System.Exception)
            {
            }

            var request = Native.GetInteractPacket(pointId, keyId, time);
            var netId = player.TryCast<FikaPlayer>() is { } selfPlayer ? selfPlayer.NetId : 0;
            // (analysis/transit-terminal-and-countdown) 按压链的原生出口是
            // Player.TransitInteraction——也就是 method_16 的界面关闭回调所做的调用，也是
            // 原生 CheckAccess 应答所做的调用。用它可以使请求保持为游戏自己的步骤：FikaPlayer
            // 在同一条可靠有序流上宣告本机的区域在场，然后发送原生构造器
            // 所生成的数据包，因此请求中没有任何东西是本类的发明。下面手写构造的
            // 数据包留作兜底的兜底（开关 PressFallbackNativeExit）。
            if (player.TryCast<FikaPlayer>() is { } fikaPlayerExit &&
                point.Controller != null)
            {
                fikaPlayerExit.TransitInteraction(point.Controller, pointId, keyId, time);
                InteractPacket = request;
                _pressFallbacks++;
                

                return;
            }

            TransitInteractPacket packet = new() { NetId = netId, Data = request };
            if (packet.NetId <= 0)
            {
                
                return;
            }

            // 区域报告在同一条可靠有序流上传输并先到达，与
            // 原生发送方所做的一模一样（见 FikaPlayer.TransitInteraction）：主机的区域门会读取它。
            SendZonePresence(pointId, true, "press fallback", null, true);
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            InteractPacket = request;
            _pressFallbacks++;
            
        }
        catch (System.Exception exception)
        {
            
        }
    }

    /// <summary>
    /// 主机发给本客户端的每个交互事件一行，再加上主机
    /// <c>EType.Confirm</c> 应答所代表的本地登记。在客户端上，运输交互的提示与确认
    /// 都由主机驱动，因此这些行说明主机向本机投递了什么、
    /// 又没投递什么。
    /// </summary>
    private void HandleInteractionEvent(TransitInteractionEvent interaction, TransitEventPacket packet)
    {
        var localPlayer = LocalPlayer;
        var mine = localPlayer != null && localPlayer.RaidId == interaction.PlayerRaidId;
        FikaGlobals.LogWarning($"[TransitDiag] client answer type={interaction.Type} point={interaction.PointId} mine={mine}");
        // (analysis/client-transit-terminal) 主机对本机的应答是按压中只有主机能产生的
        // 那一块，因此它在所有模式下都会写入：没有它，一次到达主机的
        // 按压与一次从未离开本机的按压看起来完全相同（2026-10-13 那一轮无法
        // 将它们区分开）。每个事件类型与据点写一行。

        

        if (!mine) return;
        switch (interaction.Type)
        {
            case TransitInteractionEvent.EType.Show:
                // 主机要求本机抬升提示；紧接在下面的原生处理函数正是
                // 做这件事（对同一据点的每次本地观测都会上报结果）。
                SetPointEnabled(interaction.PointId, true);
                break;
            case TransitInteractionEvent.EType.Confirm:
                // 主机接受了此玩家的请求，因此该据点在那里已被登记：既镜像
                // 主机对该据点的视图，也做下面的登记。
                SetPointEnabled(interaction.PointId, true);
                // 当主机持有它们时，确认数据包会携带请求的密钥与选定时间；
                // 登记则从本地注册表补齐空缺。
                RegisterLocalTransit(interaction.PointId, packet == null ? null : packet.KeyId,
                    packet == null ? EDateTime.CURR : packet.SelectedTime, "host confirmation");
                break;
            case TransitInteractionEvent.EType.CheckAccess:
                // (analysis/transit-terminal-and-countdown) 主机要求本机运行它自己的按压步骤：
                // 该事件的原生处理函数（紧跟本方法之后被调用，即
                // NetworkTransitController.Interact）为本地玩家解析该据点的访问密钥，并
                // 自己调用 Player.TransitInteraction——正是原生按压链末尾所做的那个调用——
                // 因此请求是由游戏自己的代码产生的。当该据点不对此玩家开放时，
                // 同一个处理函数会改为显示 'Transit/AccessNotGranted'。这里不清除任何东西，也不释放
                // 任何东西：一次仍在尝试中的按压，它的提示必须保持显示，正如原生网络
                // 路径中的做法（这个分支绝不能走到下面的 default 分支）。
                SetPointEnabled(interaction.PointId, true);
                

                break;
            case TransitInteractionEvent.EType.Hide:
                // Hide 是唯一一个表示「你的运输交互已经结束」的事件——原生离开路径
                // 为同样的事实发送它。主机在取消整组转移时会发送它
                // （用户要求 1），因此本机随之丢弃其登记、重新启用其退出并关闭
                // 其倒计时面板：否则客户端会一直为一场不会发生的转移倒计时，
                // 而它持有的锁会阻塞下一次交互。
                ClearLocalInteraction("the host sent " + interaction.Type, false);
                if (TransitCountdownGuard.AbortWhenSomeoneLeaves &&
                    (_countdownPointId >= 0 || HoldsLocalRegistrationFor(interaction.PointId)))
                {
                    CancelLocalCountdown(interaction.PointId,
                        "the host cancelled the transit (it sent " + interaction.Type + ")", "host interaction event");
                }

                break;
            default:
                ClearLocalInteraction("the host sent " + interaction.Type, false);
                break;
        }
    }

    /// <summary>
    /// 把主机对被接受的运输交互的应答镜像到本机的原生注册表。
    /// <see cref="ApplySnapshot"/> 对快照构造时主机已知的玩家做同样的事；
    /// 在那之后进入据点的玩家没有这样的快照，而原生客户端状态
    /// （交互、退出、防离开标志）建立在这个注册表之上。
    /// </summary>
    private void RegisterLocalTransit(int pointId, string keyId, EDateTime selectedTime, string reason)
    {
        var player = GamePlayerOwner.MyPlayer;
        if (player == null || !Native.pointsById.ContainsKey(pointId))
        {
            
            return;
        }
        if (Native.transitPlayers.ContainsKey(player.ProfileId))
        {
            
            ClearLocalInteraction("the registry already holds this player", false);
            return;
        }

        if (string.IsNullOrEmpty(keyId) && Native.profileKeys.ContainsKey(player.ProfileId)) keyId = Native.profileKeys[player.ProfileId];
        if (string.IsNullOrEmpty(keyId))
        {
            try { Native.TryGetAccessToLocation(player, pointId, out keyId); }
            catch (System.Exception) { }
        }

        var time = selectedTime;
        if (Native.profileTime.ContainsKey(player.ProfileId) && Native.profileTime[player.ProfileId] != EDateTime.CURR)
            time = Native.profileTime[player.ProfileId];
        else if (time == EDateTime.CURR) time = Native.GetSelectedTime();

        Native.transitPlayers[player.ProfileId] = player.RaidId;
        Native.profileKeys[player.ProfileId] = keyId;
        Native.profileTime[player.ProfileId] = time;
        InteractPacket = new() { hasInteraction = true, pointId = pointId, keyId = keyId, time = time };
        Native.DisableExits(player);
        ClearLocalInteraction("the host confirmed this player", true);
        // (analysis/client-transit-terminal) 客户端自己那一半登记，所有模式下都有：它是
        // 主机接受了这次按压的证明，也是倒计时到达之前的最后一个状态。
        

        
        // 主机在登记玩家之后会把该据点的退出从交互中移除；这一行在客户端
        // 也把这一对写出来，使下一份日志能回答那两个据点是否真的
        // 在失败的那张地图上共享同一个位置。
        
    }

    /// <summary>读取一个原生标志；失败的调用会变成一个具名状态而不是异常。</summary>
    private static string ReadFlag(System.Func<bool> read)
    {
        try { return read() ? "True" : "False"; }
        catch (System.Exception exception) { return "error:" + exception.GetType().Name; }
    }

    /// <summary>读取一个原生数字；-1 表示一次无法读取的调用。</summary>
    private static int ReadInt(System.Func<int> read)
    {
        try { return read(); }
        catch (System.Exception) { return -1; }
    }

    /// <summary>该据点的原生组规模；未知 profile 没有条目，因此不报告任何值。</summary>
    private string DescribeTransitRegistry(string profileId)
    {
        try
        {
            if (string.IsNullOrEmpty(profileId)) return "profile=?";
            return Native.transitPlayers.ContainsKey(profileId) ? "point" + Native.transitPlayers[profileId] : "none";
        }
        catch (System.Exception exception) { return "error:" + exception.GetType().Name; }
    }

    /// <summary>控制器自身的交互状态，就原生属性所能读取的范围。</summary>
    private string DescribeInteractionState()
    {
        try
        {
            var state = Native.AvailableInteractionState;
            return state == null ? "none" : state.ToString();
        }
        catch (System.Exception exception) { return "error:" + exception.GetType().Name; }
    }

    public void Init()
    {
        _ready = true;
        _initializationPending = true;
        ProcessPendingInitialization();
    }

    internal void ProcessPendingInitialization()
    {
        if (!_ready || GamePlayerOwner.MyPlayer is not { } player) return;
        if (!_initializationPending && _pendingInits.Count == 0 && _pendingSnapshots.Count == 0 && _pendingEvents.Count == 0) return;
        // 本地所有者可能先于原生运输目标注册表被填充而存在。
        // 把快照和计时器事件留给下一个 tick。
        if (!NetworkTransitController.IsTargetPlayer(player.RaidId, out _))
        {
            // 否则这次等待是不可见的：战局看起来已经初始化，而此玩家的每个快照和
            // 计时器事件仍被压着。
            if (!_pendingInitReported)
            {
                _pendingInitReported = true;
                
            }

            return;
        }

        if (_pendingInitReported)
        {
            _pendingInitReported = false;
            
        }
        if (_pendingInits.TryGetValue(player.RaidId, out var data))
        {
            OnInitEvent(data);
        }
        ApplySnapshot();
        if (_initializationPending) UpdateTimers();
        _initializationPending = false;
        while (_pendingEvents.TryDequeue(out var pending))
        {
            if (pending.Packet.TransitEvent?.TryCast<TransitGroupTimerEvent>() is { } timer)
            {
                var remaining = new Dictionary<int, ushort>();
                foreach (var entry in timer.Timers)
                    remaining[entry.Key] = (ushort)System.Math.Max(0, entry.Value - (int)(Time.unscaledTime - pending.ReceivedAt));
                timer.Timers = remaining;
            }
            ReceiveEvent(pending.Packet);
        }
    }

    protected override void DisposeNative() => Native.Dispose();
    protected override void DisposeManaged()
    {
        Native.OnPlayerEnter -= _onPlayerEnter;
        Native.OnPlayerExit -= _onPlayerExit;
        _pendingInits.Clear();
        _pendingSnapshots.Clear();
        _ready = false;
        _initializationPending = false;
        _pendingEvents.Clear();
        _zonePresenceSent.Clear();
        _interactionPoint = -1;
        _interactionVisibilityRefreshes = 0;
        _pressStartedAt = -1f;
        _pressStallReported = false;
        _pressFallbackSent = false;
        _pressFallbacks = 0;
        _countdownPointId = -1;
        _countdownRemaining = -1;
        _countdownSurface = CountdownSurface.None;
        _countdownSurfacePointId = -1;
        _countdownSurfaceSeconds = -1f;
        _countdownSurfaceStalled = false;
        _countdownSurfaceLabel = null;
        // 战局结束了；下一场会得到一套全新的诊断字符。
        TransitZonePresence.ResetLog();
        TransitPresenceReporter.ResetSkips();
    }

    /// <summary>
    /// 主机要求本机带上它的玩家（本移植缺失的那一步：一个可游玩主机的
    /// 原生 tick 只转移它自己的玩家，因此每一次客户端转移都由
    /// <c>FikaHostTransitController.IssueClientTransfer</c> 发出并落在这里）。
    /// <para>
    /// 每一次拒绝都带着原因被记录，并释放本客户端持有的运输锁，因此一个
    /// 无法被应用的数据包永远不会留下一个无法撤离的玩家。转移本身使用
    /// 游戏自己的撤离路径并带上运输目的地：
    /// <c>Native.alreadyTransits</c> 先被填充，因为 <c>ClientGameController.Extract</c> 会读它
    /// 来设置 <c>ExitStatus.Transit</c>，而 <c>TarkovApplication.TransitionStatus</c> 会把
    /// 目的地带入下一场战局。
    /// </para>
    /// </summary>
    public void HandleClientExtract(int transitId, int playerId, LocationTransit destination)
    {
        try
        {
            var local = LocalPlayer;
            var mine = local != null && local.RaidId == playerId;
            var summary = destination == null
                ? "none"
                : $"{destination.location} raid={destination.transitionRaidId} players={destination.playersCount} " +
                  $"profiles={(destination.profiles == null ? "null" : destination.profiles.Count.ToString())}";
            FikaGlobals.LogWarning($"[TransitDiag] client landing transit={transitId} player={playerId} dest={summary}");
            

            if (!NetworkTransitController.IsTargetPlayer(playerId, out var player))
            {
                
                return;
            }
            if (!Native.pointsById.TryGetValue(transitId, out var point) || point == null)
            {
                
                ReleaseLocalTransitLock(player, "the transit point of the landing is unknown");
                return;
            }
            if (Native.alreadyTransits.ContainsKey(player.ProfileId))
            {
                
                return;
            }
            // 期望的目的地来自主机构造数据包时所用的同一份游戏数据（见
            // TransitDestination），因此一个把目的地放在别处的据点，就是拿那份数据来验证，
            // 而不是拿某个硬编码字段来验证。
            var expectedDestination = TransitDestination.Resolve(point, out var destinationSource);
            if (destination == null || destination.location != expectedDestination)
            {
                
                ReleaseLocalTransitLock(player, "the destination did not match the point");
                return;
            }
            if (destination.profiles == null || !destination.profiles.ContainsKey(player.ProfileId))
            {
                // 主机原生调用的键集合描述的是它为之构造的那个组；转移
                // 本身并不依赖本机的条目（目的地在上面已对照据点、在下面已对照
                // 本地注册表验证过），因此这里只上报，不强制执行。
                
            }

            var fikaPlayer = player.TryCast<FikaPlayer>();
            if (fikaPlayer == null || FikaSingleton<IFikaGame>.Instance is not CoopGame game)
            {
                
                ReleaseLocalTransitLock(player, "no game to extract from");
                return;
            }

            if (TarkovApplication.Exist(out var application))
            {
                application.TransitionStatus = new(destination.location, false, destination.side, destination.raidMode, destination.dayTime);
            }

            Native.alreadyTransits[player.ProfileId] = destination;
            SetPointEnabled(transitId, true);

            // (2026-10-27 T1) 本机战局结束时，它的本地过场动画绝不能还活着：一个打开的
            // 进程会让本机「处于过场动画中」（并让玩家留在其中），而此时关卡正在被替换，
            // 它的原生结束随后会运行在一个已经在关闭的世界之上（2026-10-21 20:57 试玩中的
            // NullReferenceException）。CG 不会被缩短——它被关闭于本机再也无法观看的
            // 那一刻：它自己的转移着陆。
            try
            {
                var closed = Fika.Core.Main.Components.FikaLocalCutsceneTrigger.EndOpenLocalProcesses("transit landing of point " + transitId);
                if (closed > 0)
                {
                    FikaGlobals.LogInfo($"[CgDiag] local process closed before the transit landing point={transitId} " +
                        $"processes={closed}", "ClientTransitController");
                }
            }
            catch (System.Exception cutsceneException)
            {
                FikaGlobals.LogWarning($"[CgDiag] closing the local process before the transit landing threw point={transitId} error={cutsceneException.GetType().Name}", "ClientTransitController");
            }

            game.Extract(fikaPlayer, null, point);

            // 本机现在处于一次运输重载中：它的战局到此结束，而且它绝不能
            // 在主机开始自己的加载之前创建下一张地图（analysis/transit-reload-sync）。这项挂起本身发生在
            // FikaClientGameWorld.InitLevel 中，即紧接在原生关卡加载之前，并且在每条路径上
            // 都是有界的（TransitReloadGate.ClientHoldSeconds）。
            TransitReloadGate.ArmForTransitReload("a transit landing of point " + transitId
                + " (" + destination.location + ")");
        }
        catch (System.Exception exception)
        {
                        try
            {
                if (NetworkTransitController.IsTargetPlayer(playerId, out var player))
                {
                    ReleaseLocalTransitLock(player, "the transit landing threw");
                }
            }
            catch (System.Exception) { }
        }
    }

    /// <summary>
    /// 解除一次被接受但不会发生的运输的本地那一半：原生注册表条目被
    /// 丢弃，退出交互被重新启用，因此玩家不会在战局剩余时间里被锁在
    /// 撤离之外。有防护且幂等。
    /// </summary>
    private void ReleaseLocalTransitLock(Player player, string reason)
    {
        if (player == null) return;
        try
        {
            var removed = Native.transitPlayers.Remove(player.ProfileId);
            Native.EnableExits(player);
            
        }
        catch (System.Exception exception)
        {
                    }
    }

    public void UpdateTimers()
    {
        var player = GamePlayerOwner.MyPlayer;
        if (!_ready || player == null) return;
        var points = new List<TransitPoint>();
        foreach (var point in Native.pointsById.Values)
        {
            if (!Native.IsVisibleTransitPoint(point)) Native._waitForVisibleTransitPoints.Add(point);
            else points.Add(point);
        }
        Native.SetTimers(points, player, false);
    }
}
