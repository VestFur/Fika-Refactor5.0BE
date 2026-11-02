using System.Collections.Generic;
using System.Linq;
using CommonAssets.Scripts.Game;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.Utils;
using EFT.Interactive.SecretExfiltrations;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;

namespace Fika.Core.Main.Components;

public class FikaExfilManager : MonoBehaviour
{
    public FikaExfilManager(System.IntPtr pointer) : base(pointer) { }


    public FikaExfilManager() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaExfilManager>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private CoopGame _game;
    private List<ExtractionPlayerHandler> _playerHandlers;
    private List<ExfiltrationPoint> _countdownPoints;
    private ExfiltrationPoint[] _exfiltrationPoints;
    private SecretExfiltrationPoint[] _secretExfiltrationPoints;
    private System.Action<ExfiltrationPoint, Player> _startExtraction;
    private System.Action<ExfiltrationPoint, Player> _cancelExtraction;
    private System.Action<ExfiltrationPoint, EExfiltrationStatus> _statusChanged;
    private System.Action<ExfiltrationPoint, EExfiltrationStatus> _gameStatusChanged;
    private System.Action<ExfiltrationPoint, EExfiltrationStatus> _newSecretExit;
    private System.Action<string, bool> _pointFound;
    private readonly Dictionary<System.IntPtr, ExfiltrationPoint> _boundPoints = [];

    /// <summary>
    /// 供 <see cref="Update"/> 中那两处遍历复用的快照。这些遍历确实需要一份它们所消费
    /// 列表的副本，因为 Extract 和 ExternalSetStatus 可能在遍历过程中取消处理器或 Stop
    /// 管理器——但 ToArray() 会在整场突袭的每一帧都把那份副本构造成一个新的托管数组。
    /// 这些缓冲区保留了副本，同时去掉了这次分配
    /// （analysis/midraid-clr-failfast/REPORT.md）。
    /// </summary>
    private readonly List<ExtractionPlayerHandler> _handlerSnapshot = [];
    private readonly List<ExfiltrationPoint> _countdownSnapshot = [];

    protected void Awake()
    {
        _game = gameObject.GetComponent<CoopGame>();
        _startExtraction = (System.Action<ExfiltrationPoint, Player>)ExfiltrationPoint_OnStartExtraction;
        _cancelExtraction = (System.Action<ExfiltrationPoint, Player>)ExfiltrationPoint_OnCancelExtraction;
        // ExfiltrationPoint.OnStatusChanged 在 47888 中是一个托管事件。
        _statusChanged = ExfiltrationPoint_OnStatusChanged;
        _gameStatusChanged = _game.OnStatusChangedHandler;
        _newSecretExit = _game.ShowNewSecretExit;
        _pointFound = (System.Action<string, bool>)SecretExfiltrationPoint_OnPointFoundEvent;
        _playerHandlers = [];
        _countdownPoints = [];
        _exfiltrationPoints = [];
        _secretExfiltrationPoints = [];
    }

    /// <summary>
    /// (analysis/perf-round2) 撤离管理器的每帧遍历是帧开销探针中的一个槽位。
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
        if (_exfiltrationPoints == null || _secretExfiltrationPoints == null)
        {
            return;
        }

        // (analysis/stutter-frame-skip) 三个槽位而非一个：2026-10-13 20:01 那场突袭读到
        // exfilManager=554.29ms max=552.99ms，即本方法的单次调用就占满了整整一帧，而下面
        // 这三件事彼此毫无共同点。信号弹这一对和 1 Hz 的 [ExtractDiag] 采样属于
        // 诊断； 

        // 仅主机：重发当前的信号弹授权，使在信号弹之后才加入的玩家
        // 仍能看到已激活的点位。
        FikaFlareExfil.HostTick();

        // 两台机器都执行：本机显示过的信号弹成功横幅在超过显示时间后
        // 再次把它从屏幕上移除（游戏里没有任何东西持有 Fika 显示的通知）。
        FikaFlareExfil.FeedbackTick();
        

        // 最多每秒一行 [ExtractDiag] 尝试行。同一次调用还会解除某个中转已经不
        // 再属于任何中转的撤离禁令，正是这一点避免了「从未把玩家带走的转移」把他
        // 困在突袭的每一个出口前。
        if (FikaExtractDiag.ShouldSample())
        {
            
            FikaExtractDiag.Sample(_game, _exfiltrationPoints, DescribeExtractionHandlers(), _countdownPoints.Count);
            
        }

        

        // Extract/ExternalSetStatus 可能同步取消处理器或 Stop
        // 管理器。遍历快照，在回调之前先消费掉每个条目。
        _handlerSnapshot.Clear();
        _handlerSnapshot.AddRange(_playerHandlers);
        for (var i = 0; i < _handlerSnapshot.Count; i++)
        {
            var playerHandler = _handlerSnapshot[i];
            if (playerHandler.StartTime + playerHandler.ExfilPoint.Settings.ExfiltrationTime - _game.PastTime > 0)
            {
                continue;
            }

            // 受信号弹门控的出口只会为激活所覆盖的玩家打开。该处理器
            // 仍留在列表里，因此稍后到达的授权仍然能撤离这名玩家。
            if (!FikaFlareExfil.MayExtract(playerHandler.Player.ProfileId, playerHandler.ExfilPoint.Settings.Name))
            {
                continue;
            }

            if (_playerHandlers.Remove(playerHandler))
            {
                _game.ExitLocation = playerHandler.ExfilPoint.Settings.Name;
                _game.Extract(playerHandler.Player, playerHandler.ExfilPoint);
            }
        }

        _countdownSnapshot.Clear();
        _countdownSnapshot.AddRange(_countdownPoints);
        for (var i = 0; i < _countdownSnapshot.Count; i++)
        {
            var exfiltrationPoint = _countdownSnapshot[i];
            if (_game.PastTime - exfiltrationPoint.ExfiltrationStartTime > exfiltrationPoint.Settings.ExfiltrationTime &&
                _countdownPoints.Remove(exfiltrationPoint))
            {
                var extracted = false;
                foreach (var player in exfiltrationPoint.Entered)
                {
                    if (player == null)
                    {
                        continue;
                    }

                    if (!player.IsYourPlayer)
                    {
                        continue;
                    }

                    if (!player.HealthController.IsAlive)
                    {
                        continue;
                    }

                    if (!exfiltrationPoint.UnmetRequirements(player).AsManaged().Any()
                        && FikaFlareExfil.MayExtract(player.ProfileId, exfiltrationPoint.Settings.Name))
                    {
                        _game.ExitLocation = exfiltrationPoint.Settings.Name;
                        if (player.TryCast<FikaPlayer>() is { } localPlayer)
                        {
                            _game.Extract(localPlayer, exfiltrationPoint);
                            // 这个管理器只撤离本地玩家；
                            // 撤离过程可能改动 Entered 或停掉管理器。
                            extracted = true;
                            break;
                        }
                    }
                }

                // 在本玩家尚未被信号弹激活覆盖期间，把受门控的点位保留在倒计时列表上：
                // 稍后的授权会从同一个倒计时里完成撤离。
                if (!extracted
                    && FikaFlareExfil.IsGatedPoint(exfiltrationPoint.Settings.Name)
                    && !FikaFlareExfil.LocalPlayerAllowed(exfiltrationPoint.Settings.Name))
                {
                    _countdownPoints.Add(exfiltrationPoint);
                    continue;
                }

                exfiltrationPoint.ExternalSetStatus(exfiltrationPoint.Reusable
                    ? EExfiltrationStatus.UncompleteRequirements
                    : EExfiltrationStatus.NotPresent);
            }
        }

        
    }

    public void Run(ExfiltrationPoint[] exfilPoints, SecretExfiltrationPoint[] secretExfilPoints)
    {
        // 在这个管理器中的任何东西碰到这些状态之前先读取它们：哪些出口等待
        // 手动激活，正是区分绿色信号弹点位与普通点位的依据。
        FikaFlareExfil.CaptureInitialStatus(exfilPoints, secretExfilPoints);
        // 重新绑定不得把新集合所保留的点位禁用。Disable
        // 会关掉原生触发器，而再次订阅并不会重新启用它们。
        var retainedPoints = new HashSet<System.IntPtr>(exfilPoints.Select(point => point.Pointer));
        retainedPoints.UnionWith(secretExfilPoints.Select(point => point.Pointer));
        ReleaseBindings(retainedPoints);
        for (var i = 0; i < exfilPoints.Length; i++)
        {
            var exfiltrationPoint = exfilPoints[i];
            UpdateExfilPointFromServer(exfiltrationPoint, true);
            exfiltrationPoint.OnStatusChanged += _gameStatusChanged;
            _game.UpdateExfiltrationUi(exfiltrationPoint, false, true);
            if (FikaPlugin.Instance.Settings.DynamicVExfils && exfiltrationPoint.Settings.PlayersCount > 0 && exfiltrationPoint.Settings.PlayersCount < FikaSingleton<IFikaNetworkManager>.Instance.PlayerAmount)
            {
                exfiltrationPoint.Settings.PlayersCount = FikaSingleton<IFikaNetworkManager>.Instance.PlayerAmount;
            }
        }

        for (var i = 0; i < secretExfilPoints.Length; i++)
        {
            var secretExfiltrationPoint = secretExfilPoints[i];
            UpdateExfilPointFromServer(secretExfiltrationPoint, true);
            secretExfiltrationPoint.OnStatusChanged += _gameStatusChanged;
            secretExfiltrationPoint.OnStatusChanged += _newSecretExit;
            _game.UpdateExfiltrationUi(secretExfiltrationPoint, false, true);
            secretExfiltrationPoint.OnPointFoundEvent += _pointFound;
        }

        _exfiltrationPoints = exfilPoints;
        _secretExfiltrationPoints = secretExfilPoints;

        // 经中转抵达的地点，可能继承上一个地点在玩家确认
        // 转移时施加的撤离禁令与已禁用的出口交互。
        // 要在他走到出口之前把这一对都解除掉。
        FikaExtractDiag.OnRaidBound(_game, exfilPoints.Length, secretExfilPoints.Length);
    }

    private void SecretExfiltrationPoint_OnPointFoundEvent(string exitName, bool sharedExit)
    {
        var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer?.TryCast<FikaPlayer>();
        if (mainPlayer == null) return;
        var teamId = string.IsNullOrEmpty(mainPlayer.TeamId) ? mainPlayer.GroupId : mainPlayer.TeamId;
        FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.SecretExfilFound,
            SecretExfilFound.FromValue(mainPlayer.GroupId, teamId, exitName), true);
    }

    public void Stop() => ReleaseBindings(null);

    /// <summary>
    /// 释放这个管理器绑定的每一个点位：Fika 的处理器被解绑，而
    /// 未被保留的点位则在原生层被禁用。收尾流程从
    /// <see cref="OnDestroy"/>、<c>CoopGame.StopExit</c> 以及
    /// <c>CoopGame.StopFromCancel</c> 进入，而这些调用方在本次调用之后马上还要
    /// 继续执行突袭退出流程，因此本方法绝不能抛异常。等到
    /// 管理器被销毁时，游戏可能已经把点位销毁掉了
    /// （场景卸载、中转、应用退出）；此时原生的
    /// <c>ExfiltrationPoint.Disable()</c> 会从 IL2CPP 内部抛出 NullReferenceException。
    /// 已销毁的点位会被跳过，且每个点位各自独立
    /// 释放，因此一个坏点位不会中断其他点位的释放。这些数组
    /// 在循环之前就被取走，从而使重复调用变成空操作。
    /// </summary>
    private void ReleaseBindings(HashSet<System.IntPtr> retainedPoints)
    {
        _playerHandlers?.Clear();
        _countdownPoints?.Clear();

        // 先取走这些数组：某个点位失败时，绝不能为后续的 Stop() 调用
        // 留下一份只释放了一半的列表。
        var exfiltrationPoints = _exfiltrationPoints;
        var secretExfiltrationPoints = _secretExfiltrationPoints;
        _exfiltrationPoints = [];
        _secretExfiltrationPoints = [];

        var skipped = 0;
        var failures = 0;
        string firstFailure = null;

        if (exfiltrationPoints != null)
        {
            for (var i = 0; i < exfiltrationPoints.Length; i++)
            {
                var exfiltrationPoint = exfiltrationPoints[i];
                // Unity 通过其对象运算符把已销毁的点位报告为 null；
                // 对它调用原生 Disable() 会从 IL2CPP 内部抛出异常。
                if (exfiltrationPoint == null)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    UpdateExfilPointFromServer(exfiltrationPoint, false);
                    exfiltrationPoint.OnStatusChanged -= _gameStatusChanged;
                    if (retainedPoints == null || !retainedPoints.Contains(exfiltrationPoint.Pointer))
                        exfiltrationPoint.Disable();
                }
                catch (System.Exception exception)
                {
                    failures++;
                    firstFailure ??= DescribePoint(exfiltrationPoint) + ": " + exception.Message;
                }
            }
        }

        if (secretExfiltrationPoints != null)
        {
            for (var i = 0; i < secretExfiltrationPoints.Length; i++)
            {
                var secretExfiltrationPoint = secretExfiltrationPoints[i];
                if (secretExfiltrationPoint == null)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    UpdateExfilPointFromServer(secretExfiltrationPoint, false);
                    secretExfiltrationPoint.OnStatusChanged -= _gameStatusChanged;
                    secretExfiltrationPoint.OnStatusChanged -= _newSecretExit;
                    secretExfiltrationPoint.OnPointFoundEvent -= _pointFound;
                    if (retainedPoints == null || !retainedPoints.Contains(secretExfiltrationPoint.Pointer))
                        secretExfiltrationPoint.Disable();
                }
                catch (System.Exception exception)
                {
                    failures++;
                    firstFailure ??= DescribePoint(secretExfiltrationPoint) + ": " + exception.Message;
                }
            }
        }

        foreach (var point in _boundPoints.Values.ToArray())
        {
            try
            {
                UpdateExfilPointFromServer(point, false);
            }
            catch (System.Exception exception)
            {
                failures++;
                firstFailure ??= DescribePoint(point) + ": " + exception.Message;
            }
        }

        _boundPoints.Clear();

        if (failures > 0)
        {
            FikaGlobals.LogWarning($"[ExfilTeardown] released exfil bindings: {skipped} destroyed, {failures} failed; first: {firstFailure}");
        }
        else if (skipped > 0)
        {
            FikaGlobals.LogInfo($"[ExfilTeardown] skipped {skipped} already destroyed exfiltration point(s)");
        }
    }

    /// <summary>
    /// 为收尾日志行尽力取得的点位名称：一个已经销毁的点位
    /// 在读取它自己的设置时可能抛异常。
    /// </summary>
    private static string DescribePoint(ExfiltrationPoint point)
    {
        try
        {
            var name = point?.Settings?.Name;
            return string.IsNullOrEmpty(name) ? "<unnamed>" : name;
        }
        catch
        {
            return "<unreadable>";
        }
    }

    public void UpdateExfilPointFromServer(ExfiltrationPoint point, bool enable)
    {
        if (enable)
        {
            // 初始/重连快照可能在 Run 绑定事件之前就设置了 Countdown。
            // Run 还会在保留事件绑定的同时清空这个列表，
            // 因此即便该点位已经绑定过，也要恢复倒计时。
            if (_countdownPoints != null && point.Status == EExfiltrationStatus.Countdown && !_countdownPoints.Contains(point))
                _countdownPoints.Add(point);
            if (!_boundPoints.TryAdd(point.Pointer, point)) return;
            point.OnStartExtraction += _startExtraction;
            point.OnCancelExtraction += _cancelExtraction;
            point.OnStatusChanged += _statusChanged;
        }
        else
        {
            if (!_boundPoints.Remove(point.Pointer)) return;
            point.OnStartExtraction -= _startExtraction;
            point.OnCancelExtraction -= _cancelExtraction;
            point.OnStatusChanged -= _statusChanged;
        }
    }

    private void OnDestroy() => Stop();

    /// <summary>
    /// 这个管理器当前为本地玩家持有的撤离处理器所属的点位名称，
    /// 供 <c>[ExtractDiag]</c> 尝试行使用。只读：已经销毁的点位
    /// 不会报告任何内容，撤离路径本身也从不依赖这个列表。
    /// </summary>
    internal List<string> DescribeExtractionHandlers()
    {
        var names = new List<string>();
        for (var i = 0; i < _playerHandlers.Count; i++)
        {
            try
            {
                var name = _playerHandlers[i].ExfilPoint?.Settings?.Name;
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }
            }
            catch (System.Exception)
            {
                // 已销毁的点位不会报告任何内容。
            }
        }

        return names;
    }

    private void ExfiltrationPoint_OnCancelExtraction(ExfiltrationPoint point, Player player)
    {
        if (!player.IsYourPlayer)
        {
            return;
        }

        var extractionPlayerHandler = _playerHandlers.FirstOrDefault(x => x.Player == player);
        if (extractionPlayerHandler != null)
        {
            _playerHandlers.Remove(extractionPlayerHandler);
        }
    }

    private void ExfiltrationPoint_OnStartExtraction(ExfiltrationPoint point, Player player)
    {
        if (!player.IsYourPlayer)
        {
            return;
        }

        if (_playerHandlers.All(x => x.Player != player))
        {
            _playerHandlers.Add(new(player, point, _game.PastTime));
        }
    }

    private void ExfiltrationPoint_OnStatusChanged(ExfiltrationPoint point, EExfiltrationStatus prevStatus)
    {
        var isCounting = _countdownPoints.Contains(point);
        if (isCounting && point.Status != EExfiltrationStatus.Countdown)
        {
            // (2026-10-27 W43 / TARGETS T2-A 第二半) 这条分支原来无条件把倒计时就地销毁：置
            // ExfiltrationStartTime = -100 并从 _countdownPoints 删掉。而 ExfilCountdown 包只来一次，
            // 于是"主机先撤/主机把点位状态改了"→ 客机的倒计时被永久销毁，本机再也结算不了
            // （现象：停在 exits stay disabled）。现在改成"只在本机这条倒计时确实没用了才销毁"。
            var elapsed = _game.PastTime - point.ExfiltrationStartTime;
            var remaining = point.Settings.ExfiltrationTime - elapsed;
            var localPlayerStillIn = false;
            foreach (var entered in point.Entered)
            {
                if (entered != null && entered.IsYourPlayer && entered.HealthController != null && entered.HealthController.IsAlive)
                {
                    localPlayerStillIn = true;
                    break;
                }
            }

            // 保留条件：本机自己的倒计时还在跑（有有效起点、还没走完），本机玩家还活着且在点位里，
            // 且点位不是被真正关闭（NotPresent / Hidden）。其余情况照旧销毁。
            var keepLocalCountdown = point.ExfiltrationStartTime > 0f
                && remaining > 0f
                && localPlayerStillIn
                && point.Status != EExfiltrationStatus.NotPresent
                && point.Status != EExfiltrationStatus.Hidden;

            if (keepLocalCountdown)
            {
                FikaGlobals.LogInfo($"[CarExtract] kept the local countdown of '{point.Settings.Name}' " +
                    $"(host status {point.Status}, remaining {remaining:F1}s of {point.Settings.ExfiltrationTime:F0}s) " +
                    "- this machine still finishes it itself", "FikaExfilManager");
            }
            else
            {
                point.ExfiltrationStartTime = -100;
                _countdownPoints.Remove(point);
                if (IsPaidExfiltrationPoint(point))
                {
                    FikaGlobals.LogInfo($"[CarExtract] point '{point.Settings.Name}' left the countdown " +
                        $"(status {point.Status}, queued {point.QueuedPlayers?.Count ?? 0}, remaining {remaining:F1}s)");
                }
            }
        }

        if (!isCounting && point.Status == EExfiltrationStatus.Countdown)
        {
            if (point.ExfiltrationStartTime is <= 0 and > -90)
            {
                point.ExfiltrationStartTime = _game.PastTime;
                if (IsPaidExfiltrationPoint(point))
                {
                    FikaGlobals.LogInfo($"[CarExtract] activation broadcast: point '{point.Settings.Name}' " +
                        $"started its countdown at {point.ExfiltrationStartTime}, queued {point.QueuedPlayers?.Count ?? 0}");
                }
                if (FikaFlareExfil.IsGatedPoint(point.Settings.Name))
                {
                    // 没有标识的倒计时会让该点位在每个对等端上都被打开。信号弹
                    // 激活则是以授权的方式送达每一名被覆盖的玩家。
                    FikaFlareExfil.LogCountdownSuppressed(point.Settings.Name);
                }
                else
                {
                    FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.ExfilCountdown,
                            ExfilCountdown.FromValue(point.Settings.Name, point.ExfiltrationStartTime), true);
                }
            }
            _countdownPoints.Add(point);
        }
    }

    /// <summary>
    /// 当该点位是付费撤离点位（“car extract”）时为 true：它唯一的
    /// 要求就是把现金交给司机。
    /// </summary>
    internal static bool IsPaidExfiltrationPoint(ExfiltrationPoint point)
    {
        try
        {
            return point != null && point.TransferItemRequirement != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 该点位是否已经把这名玩家记为已付款。两个原生信号
    /// 被合并使用，因为 <c>[CarExtract]</c> 绝不能放行第二次交付：
    /// 需求记账以档案 id 为键
    /// （<see cref="ExfiltrationPoint.HasMetRequirements(string)"/>），而
    /// 付款队列保存的是每场突袭内数值型的玩家 id。抛异常的检查从不会
    /// 阻止付款；它只是让那次点击失去重复保护。
    /// </summary>
    internal static bool HasPlayerPaid(ExfiltrationPoint point, Player player)
    {
        try
        {
            if (point == null || player == null)
            {
                return false;
            }

            if (point.HasMetRequirements(player.ProfileId))
            {
                return true;
            }

            var queued = point.QueuedPlayers;
            if (queued == null)
            {
                return false;
            }

            return queued.Contains(player.Id) || queued.Contains(player.RaidId);
        }
        catch (System.Exception exception)
        {
            FikaGlobals.LogWarning($"[CarExtract] failed to read the paid state of " +
                $"'{point?.Settings?.Name}': {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 按触发器的名称查找撤离点位（普通或秘密）。
    /// 由主机上的 <c>[CarExtract]</c> 付款守卫使用。
    /// </summary>
    internal static ExfiltrationPoint FindExfiltrationPoint(string pointName)
    {
        if (string.IsNullOrEmpty(pointName))
        {
            return null;
        }

        var controller = ExfiltrationController.Instance;
        if (controller == null)
        {
            return null;
        }

        var points = controller.ExfiltrationPoints;
        if (points != null)
        {
            for (var i = 0; i < points.Length; i++)
            {
                if (points[i] != null && points[i].Settings?.Name == pointName)
                {
                    return points[i];
                }
            }
        }

        var secretPoints = controller.SecretExfiltrationPoints;
        if (secretPoints != null)
        {
            for (var i = 0; i < secretPoints.Length; i++)
            {
                if (secretPoints[i] != null && secretPoints[i].Settings?.Name == pointName)
                {
                    return secretPoints[i];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 描述汽车撤离付款描述符中的物品，供
    /// <c>[CarExtract]</c> 日志行使用。
    /// </summary>
    internal static string DescribePaymentItems(EFT.InventoryLogic.Operations.TransferExfiltrationPaymentOperationDescriptor descriptor)
    {
        try
        {
            var items = descriptor?.Items;
            if (items == null || items.Length == 0)
            {
                return "no items";
            }

            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (i > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(item.ItemId).Append(" x").Append(item.NumberToDestroy);
            }
            return builder.ToString();
        }
        catch (System.Exception exception)
        {
            return $"items unavailable ({exception.Message})";
        }
    }

    private class ExtractionPlayerHandler(Player player, ExfiltrationPoint point, float startTime)
    {
        public readonly FikaPlayer Player = player.TryCast<FikaPlayer>()
            ?? throw new System.InvalidOperationException("Extraction handler received a non-Fika player");
        public readonly ExfiltrationPoint ExfilPoint = point;
        public readonly float StartTime = startTime;
    }
}