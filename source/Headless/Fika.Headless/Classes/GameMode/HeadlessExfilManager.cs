using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Interactive;
using EFT.Interactive.SecretExfiltrations;
using Fika.Core;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Il2CppInterop.Runtime.Injection;

namespace Fika.Headless.Classes.GameMode;

/// <summary>
/// 持有共享撤离点的主机侧状态。远程玩家通过各自的
/// FikaExfilManager 撤离；无头主机没有可撤离的本地玩家。
/// </summary>
public sealed class HeadlessExfilManager : MonoBehaviour
{
    public HeadlessExfilManager(IntPtr pointer) : base(pointer) { }

    public HeadlessExfilManager() : this(ClassInjector.DerivedConstructorPointer<HeadlessExfilManager>())
    {
        ClassInjector.DerivedConstructorBody(this);
    }

    private HeadlessGame _game;
    private System.Action<ExfiltrationPoint, EExfiltrationStatus> _statusChanged;
    private readonly Dictionary<IntPtr, ExfiltrationPoint> _boundPoints = [];
    private readonly Dictionary<IntPtr, ExfiltrationPoint> _countdownPoints = [];
    private readonly List<ExfiltrationPoint> _countdownSnapshot = [];

    private void Awake()
    {
        _game = gameObject.GetComponent<HeadlessGame>();
        _statusChanged = (System.Action<ExfiltrationPoint, EExfiltrationStatus>)OnStatusChanged;
    }

    public void Run(ExfiltrationPoint[] points, SecretExfiltrationPoint[] secretPoints)
    {
        // 哪些出口需要等待手动激活，正是区分绿色信号弹出口与普通出口的依据；
        // 该分类结果也供下面的倒计时抑制使用。
        Fika.Core.Main.Components.FikaFlareExfil.CaptureInitialStatus(points, secretPoints);
        ReleaseBindings(false);

        foreach (var point in points ?? [])
        {
            Bind(point);
            if (FikaPlugin.Instance.Settings.DynamicVExfils && point.Settings.PlayersCount > 0 &&
                point.Settings.PlayersCount < FikaSingleton<IFikaNetworkManager>.Instance.PlayerAmount)
            {
                point.Settings.PlayersCount = FikaSingleton<IFikaNetworkManager>.Instance.PlayerAmount;
            }
        }

        foreach (var point in secretPoints ?? [])
            Bind(point);
    }

    private void Bind(ExfiltrationPoint point)
    {
        if (point == null || !_boundPoints.TryAdd(point.Pointer, point))
            return;

        point.OnStatusChanged += _statusChanged;
        // 主机完成设置时，某个点可能已经在倒计时了。
        if (point.Status == EExfiltrationStatus.Countdown)
            _countdownPoints[point.Pointer] = point;
    }

    private void Update()
    {
        if (_game == null || _game.Status != GameStatus.Started)
            return;

        // 无头主机没有 FikaExfilManager，因此这里是唯一会重新向对端发送
        // 当前信号弹授权的地方（迟到加入者、丢失的数据包）。
        Fika.Core.Main.Components.FikaFlareExfil.HostTick();

        // (2026-10-27 W44) 这个 Update 每帧跑，`.Values.ToArray()` 每帧都会分配一个新数组
        // （哪怕倒计时字典是空的）。改成复用同一个缓冲：先拷进缓冲、再遍历，语义不变。
        if (_countdownPoints.Count == 0)
        {
            return;
        }

        _countdownSnapshot.Clear();
        foreach (var pair in _countdownPoints)
        {
            _countdownSnapshot.Add(pair.Value);
        }

        foreach (var point in _countdownSnapshot)
        {
            if (_game.PastTime - point.ExfiltrationStartTime > point.Settings.ExfiltrationTime &&
                _countdownPoints.Remove(point.Pointer))
            {
                point.ExternalSetStatus(point.Reusable
                    ? EExfiltrationStatus.UncompleteRequirements
                    : EExfiltrationStatus.NotPresent);
            }
        }
    }

    private void OnStatusChanged(ExfiltrationPoint point, EExfiltrationStatus previousStatus)
    {
        if (_countdownPoints.ContainsKey(point.Pointer) && point.Status != EExfiltrationStatus.Countdown)
        {
            point.ExfiltrationStartTime = -100;
            _countdownPoints.Remove(point.Pointer);
        }

        if (point.Status != EExfiltrationStatus.Countdown || _countdownPoints.ContainsKey(point.Pointer))
            return;

        if (point.ExfiltrationStartTime is <= 0 and > -90)
        {
            point.ExfiltrationStartTime = _game.PastTime;
            if (Fika.Core.Main.Components.FikaFlareExfil.IsGatedPoint(point.Settings.Name))
            {
                // 不带身份的倒计时复制会在每个对端上打开该信号弹出口。激活
                // 反而会以授权形式传达给被覆盖的玩家。
                Fika.Core.Main.Components.FikaFlareExfil.LogCountdownSuppressed(point.Settings.Name);
            }
            else
            {
                FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.ExfilCountdown,
                    ExfilCountdown.FromValue(point.Settings.Name, point.ExfiltrationStartTime), true);
            }
        }

        _countdownPoints[point.Pointer] = point;
    }

    public void Stop() => ReleaseBindings(true);

    /// <summary>
    /// 释放本管理器绑定的每个点。清理会从 <see cref="OnDestroy"/>
    /// 和 <see cref="Run"/>（重新绑定）调用，因此它绝不能抛异常。等到
    /// 管理器被销毁时，游戏可能已经把那些点销毁了（场景卸载、
    /// 中转、应用退出）；此时原生 <c>ExfiltrationPoint.Disable()</c> 会在
    /// IL2CPP 内部抛出 NullReferenceException。已销毁的点会被跳过，
    /// 且每个点独立释放，因此一个坏点无法中断其他点的
    /// 释放。字典在循环之前就被取走，这使重复调用变成空操作。
    /// </summary>
    private void ReleaseBindings(bool disablePoints)
    {
        _countdownPoints.Clear();

        // 先取走绑定：失败的点不得为后续的 Stop() 调用留下一个
        // 只释放了一半的字典。
        var boundPoints = _boundPoints.Values.ToArray();
        _boundPoints.Clear();

        var skipped = 0;
        var failures = 0;
        string firstFailure = null;

        for (var i = 0; i < boundPoints.Length; i++)
        {
            var point = boundPoints[i];
            // Unity 通过 Unity 对象运算符把已销毁的点报告为 null；
            // 对它调用原生 Disable() 会在 IL2CPP 内部抛出异常。
            if (point == null)
            {
                skipped++;
                continue;
            }

            try
            {
                point.OnStatusChanged -= _statusChanged;
                if (disablePoints)
                    point.Disable();
            }
            catch (System.Exception exception)
            {
                failures++;
                firstFailure ??= DescribePoint(point) + ": " + exception.Message;
            }
        }

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
    /// 为清理日志行尽力获取点名称：已销毁的点在读取自身设置时
    /// 可能抛异常。
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

    private void OnDestroy() => Stop();
}
