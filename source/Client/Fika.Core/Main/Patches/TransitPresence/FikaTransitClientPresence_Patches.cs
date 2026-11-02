using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.Interactive;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;
using JsonType;
using UnityEngine;
using ClientTransitController = Fika.Core.Main.ClientClasses.ClientTransitController;

namespace Fika.Core.Main.Patches.TransitPresence;

/// <summary>
/// 每一个把"本客户端的玩家站在过境点内"这一事实转成发往
/// 主机的消息的钩子。目标方法的依据（SPT 5.0 / 47965 interop dump，47888 与之对应；见
/// analysis/transit-presence-binding/REPORT.md）：
/// <para>
/// <c>PlayerEnter(TransitPoint, Player)</c> / <c>PlayerExit(TransitPoint, Player)</c> 声明在
/// <c>EFT.LocalTransitController</c>（dump 242850 / 242851）上，<b>而不是</b>
/// <c>EFT.ClientTransitController</c> 上。战局客户端的原生控制器是一个
/// <c>EFT.NetworkTransitController</c>（Fika 会为每个非主机在
/// <c>BaseGameController:1469-1471</c> 处创建一个），它从不声明这些方法，因此之前的
/// 目标根本无法绑定——<c>LogOutput.log:594-597</c> 中的两次失败正是
/// 如此——而客户端对主机一直不可见
/// （<c>LogOutput.log:2507 in=1/2 allowed=False</c>）。
/// </para>
/// <para>
/// 在每台机器上都同时持有过境点与进入玩家的唯一对象就是过境点本身：
/// <c>EFT.Interactive.TransitPoint</c> 声明了 <c>OnTriggerEnter(Collider)</c> /
/// <c>OnTriggerExit(Collider)</c>（dump 212394 / 212395）、公开静态的
/// <c>TryGetPlayer(Collider, out Player)</c>（dump 212400），实现了物理触发器
/// （<c>IPhysicsTrigger.enabled</c>）并持有 <c>Controller</c> 引用。Fika 在客户端上
/// 保持这些过境点存活（<c>ClientTransitController.cs:39</c> 会重新激活它们，且 Fika 中没有任何代码
/// 调用 <c>NetworkTransitController.DisablePointsTrigger</c>），因此过境点触发器就是客户端的
/// 真正入口。下面这些 <c>LocalTransitController</c> 钩子保留下来，供任何确实
/// 运行该控制器的机器使用（也能覆盖给客户端分配该控制器的构建）；它们在
/// Fika 主机上是惰性的，因为那场战局的世界控制器是 <see cref="FikaHostTransitController"/>，
/// 而它不是 <see cref="ClientTransitController"/>。
/// </para>
/// </summary>
internal static class TransitPresenceBinding
{
    /// <summary>
    /// 解析一个补丁目标，并在日志中证明结果。目标缺失会以
    /// ERROR（而不只是 HarmonyX 的警告）上报，因为毁掉这个功能的正是静默未绑定的钩子。
    /// </summary>
    internal static MethodBase Resolved(string patch, System.Type declaring, string name, params System.Type[] parameters)
    {
        var method = AccessTools.DeclaredMethod(declaring, name, parameters);
        if (method == null)
        {
            
            return null;
        }

        
        return method;
    }

    private static string Describe(ParameterInfo[] parameters)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var parameter in parameters)
        {
            if (builder.Length > 0) builder.Append(", ");
            builder.Append(parameter.ParameterType.Name);
        }
        return builder.ToString();
    }

    private static string Describe(System.Type[] parameters)
    {
        if (parameters == null || parameters.Length == 0) return string.Empty;
        var builder = new System.Text.StringBuilder();
        foreach (var parameter in parameters)
        {
            if (builder.Length > 0) builder.Append(", ");
            builder.Append(parameter.Name);
        }
        return builder.ToString();
    }

    /// <summary>日志尽力而为：尚未就绪的日志器不得破坏补丁安装。</summary>
    internal static void Log(bool error, string message)
    {
        try
        {
            if (error) FikaGlobals.LogError(message);
            else FikaGlobals.LogWarning(message);
        }
        catch (System.Exception exception)
        {
            System.Console.WriteLine(message + " (log failed: " + exception.Message + ")");
        }
    }
}

/// <summary>
/// 所有客户端侧存在感钩子统一上报的单一漏斗，因此原生路径与
/// 几何回退不会重复上报或互相矛盾："主机是否已经
/// 知道"这一判断是从 <see cref="ClientTransitController.SentZonePoint"/> 读回的，而它与
/// 发包方去重所用的是同一张映射表。
/// </summary>
internal static class TransitPresenceReporter
{
    /// <summary>每秒五次几何扫描：对步行玩家足够，对战局线程也很廉价。</summary>
    private const float PollInterval = 0.25f;

    /// <summary>
    /// (2026-10-19) 关闭：每次轮询的位置扫描已被原生触发器事件与主机的
    /// 交互应答取代。仅在需要验证缺失触发器的构建上开启。
    /// </summary>
    internal const bool GeometricFallback = false;

    /// <summary>有上限：每场战局中每个玩家、每个过境点和每种原因各一行。</summary>
    private static readonly System.Collections.Generic.HashSet<string> LoggedSkips = [];

    private static float _nextPoll;

    /// <summary>
    /// 每场战局上报一次几何回退被禁用的情况，这样静默失去
    /// 最后一个入口的客户端，就不会看起来像一个玩家从未站进过境区域的客户端。
    /// </summary>
    private static bool _pollDisabledReported;

    /// <summary>原生触发器路径：过境点已经解析出进入或离开它的碰撞体。</summary>
    internal static void FromCollider(TransitPoint point, Collider col, bool inside, string source)
    {
        if (point == null || col == null) return;
        if (!TransitPoint.TryGetPlayer(col, out var player)) return;
        Report(point, player, inside, source);
    }

    /// <summary>为已解析出的玩家上报（触发器路径与控制器事件路径）。</summary>
    internal static void Report(TransitPoint point, Player player, bool inside, string source)
    {
        // 机器人和远端被观察的玩家也有碰撞体，在本机上同样可能进入过境点触发器；
        // 只有在本机属于本地的玩家才有资格代表本机发言。
        if (point?.parameters == null || player == null || !player.IsYourPlayer) return;
        ClientWith(Singleton<GameWorld>.Instance?.TransitController)?.ReportZonePresence(player, point, inside, source);
    }

    /// <summary>
    /// 几何回退，独立于任何原生回调：每 <see cref="PollInterval"/>
    /// 执行一次本地玩家位置与各过境点触发体积的判定，且只上报变化。
    /// 判定标准是过境点自身的触发边界（见 <see cref="FindPointInside"/>）。
    /// </summary>
    internal static void Poll(GameWorld world)
    {
        if (world == null || Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + PollInterval;

        // 主机自己的过境倒计时也从这里驱动：主机战局没有客户端
        // 包装器（见下面的提前返回），而本地战局的原生 tick 并不保证会
        // 为被传送的组运行。此处加了保护，因为轮询绝不能破坏它所在的这一帧。
        try
        {
            FikaWorldControllers.Find<FikaHostTransitController>(world.TransitController)?.HostCountdownTick();
        }
        catch (System.Exception exception)
        {
            MarkHostTickFailure(exception);
        }

        var controller = ClientWith(world.TransitController);
        // 主机战局或无头战局走主机自己的原生路径；没有需要上报的主机。
        // 在那里这是预期行为，在客户端上则是静默失败——在客户端，这个轮询是
        // 原生触发器路径离线时仍能工作的回退，因此要说清这里究竟是哪种情况。
        if (controller == null)
        {
            if (!_pollDisabledReported)
            {
                _pollDisabledReported = true;
                var transit = world.TransitController;
                
            }

            return;
        }

        _pollDisabledReported = false;
        var player = world.MainPlayer;
        if (player == null || !player.IsYourPlayer) return;

        // (2026-10-19) 下面的几何扫描每秒遍历每个过境点体积四次。存在感与
        // 提示现在都是事件驱动的（原生触发器 enter/exit 补丁加上主机的 Show/Confirm/
        // InactivePoint 应答），因此扫描默认关闭，仅作为显式回退保留。
        if (!GeometricFallback) return;

        var point = FindPointInside(world.TransitController, player.Position);
        var reported = controller.SentZonePoint(player.ProfileId);
        if (point != null)
        {
            if (reported != point.parameters.id) controller.ReportZonePresence(player, point, true, "geo poll");
            // 存在感上报会去重，交互检查不会：在区域上报发出之后提示仍可能
            // 缺失（主机授予的状态、原生可见性，或第一次
            // 尝试在主机的快照到达之前就已经执行）。
            controller.ClientInteractionTick(point, player, "geo poll");
            return;
        }

        if (reported >= 0)
        {
            // 离开体积必须清除主机的登记；原生 PlayerExit 在接收该触发器的
            // 机器上做这件事，而这里在没有它的情况下保持同样的语义。
            controller.SendZonePresence(0, false, "geo poll");
        }

        controller.ClientInteractionTick(null, player, "geo poll");
    }

    private static ClientTransitController ClientWith(EFT.TransitController transit) =>
        FikaWorldControllers.Find<ClientTransitController>(transit);

    /// <summary>
    /// 本地玩家所站的过境点，或 null。判定标准：过境点自身触发碰撞体的
    /// 边界包含玩家位置。
    /// <para>
    /// 该碰撞体就是触发体积的依据：TransitPoint 接收 Unity 的触发器
    /// 消息（<c>OnTriggerEnter/OnTriggerExit</c>，dump 212394/212395）并实现了
    /// <c>IPhysicsTrigger</c>，因此产生这些消息的碰撞体就位于过境点自身的
    /// GameObject 上。这里用边界包含判断而非 <c>Collider.ClosestPoint</c>，因为它适用于
    /// 所有碰撞体形状，并且在碰撞体被禁用时也能工作；它是
    /// 触发体积的超集，而触发体积只会登记已经位于该点的玩家——准入、
    /// 进入条件、任务条件、队伍人数上限以及主机自身的
    /// 登记检查都不受它影响。
    /// </para>
    /// </summary>
    private static TransitPoint FindPointInside(EFT.TransitController transit, Vector3 position)
    {
        var points = transit?.pointsById;
        if (points == null) return null;

        foreach (var point in points.Values)
        {
            if (point == null) continue;
            var collider = TriggerOf(point);
            if (collider != null && collider.bounds.Contains(position)) return point;
        }
        return null;
    }

    private static Collider TriggerOf(TransitPoint point)
    {
        var collider = point.GetComponent<Collider>();
        return collider != null ? collider : point.GetComponentInChildren<Collider>(true);
    }

    /// <summary>每场战局中每个玩家、每个过境点和每种原因各一行被跳过上报的日志。</summary>
    internal static bool MarkSkipLogged(string key) => LoggedSkips.Count < 64 && LoggedSkips.Add(key);

    /// <summary>
    /// 每场战局上报一次主机倒计时 tick 失败。该 tick 本身已经停止了倒计时
    /// 并释放了它所做的登记，因此这一行只需指出引发它的异常。
    /// </summary>
    private static bool MarkHostTickFailure(System.Exception exception)
    {
        if (!MarkSkipLogged("host countdown tick:" + exception.GetType().Name)) return false;
                return true;
    }

    /// <summary>战局已结束；下一场战局会获得一组全新的诊断键。</summary>
    internal static void ResetSkips()
    {
        LoggedSkips.Clear();
        // 同时也重新武装回退上报：同一个进程可以先主持一场战局，然后以客户端身份
        // 加入下一场，在那里缺少包装器是失败，而不是主机的正常路径。
        _pollDisabledReported = false;
    }
}

/// <summary>
/// 原生 <c>EFT.LocalTransitController.PlayerEnter(TransitPoint, Player)</c>——主机战局已经通过
/// <see cref="Fika.Core.Main.Patches.TransitController.FikaTransitEnterPatch"/> 使用的那个声明。
/// 客户端战局的控制器是 <c>NetworkTransitController</c>，永远不会到达这个方法，因此
/// 在客户端上这个钩子只是一层安全网（见 <see cref="TransitPresenceBinding"/> 的类注释；
/// 下面的过境点触发器钩子才是客户端的真实路径）。
/// </summary>
public sealed class FikaTransitClientPresenceEnterPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => TransitPresenceBinding.Resolved(
        nameof(FikaTransitClientPresenceEnterPatch), typeof(EFT.LocalTransitController), "PlayerEnter",
        typeof(TransitPoint), typeof(Player));

    /// <summary>
    /// 在拥有该控制器的机器上运行。只上报本机自己的玩家；机器人和
    /// 远端被观察的玩家会被忽略，而没有客户端包装器的机器（Fika 主机）
    /// 什么都不上报。
    /// </summary>
    [PatchPrefix]
    private static void Prefix(EFT.LocalTransitController __instance, TransitPoint point, Player player) =>
        TransitPresenceReporter.Report(point, player, true, "local transit controller");
}

/// <inheritdoc cref="FikaTransitClientPresenceEnterPatch"/>
public sealed class FikaTransitClientPresenceExitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => TransitPresenceBinding.Resolved(
        nameof(FikaTransitClientPresenceExitPatch), typeof(EFT.LocalTransitController), "PlayerExit",
        typeof(TransitPoint), typeof(Player));

    [PatchPrefix]
    private static void Prefix(EFT.LocalTransitController __instance, TransitPoint point, Player player) =>
        TransitPresenceReporter.Report(point, player, false, "local transit controller");
}

/// <summary>
/// 客户端的真实入口：过境点自身的触发器。<c>TransitPoint.OnTriggerEnter/OnTriggerExit</c>
/// 在每个激活了该过境点的机器上运行，使用游戏自身的
/// <c>TryGetPlayer</c> 辅助方法解析碰撞体，且与战局安装了哪个控制器类无关。
/// 这里使用 Prefix，这样触发器内部的原生异常就无法吞掉这次上报。
/// </summary>
public sealed class FikaTransitClientPointEnterPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => TransitPresenceBinding.Resolved(
        nameof(FikaTransitClientPointEnterPatch), typeof(TransitPoint), "OnTriggerEnter", typeof(Collider));

    [PatchPrefix]
    private static void Prefix(TransitPoint __instance, Collider col) =>
        TransitPresenceReporter.FromCollider(__instance, col, true, "point trigger");
}

/// <inheritdoc cref="FikaTransitClientPointEnterPatch"/>
public sealed class FikaTransitClientPointExitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => TransitPresenceBinding.Resolved(
        nameof(FikaTransitClientPointExitPatch), typeof(TransitPoint), "OnTriggerExit", typeof(Collider));

    [PatchPrefix]
    private static void Prefix(TransitPoint __instance, Collider col) =>
        TransitPresenceReporter.FromCollider(__instance, col, false, "point trigger");
}

/// <summary>
/// 几何回退的宿主：<c>EFT.GameWorld.Update()</c> 在战局中每台机器上每帧运行一次，
/// 而 <see cref="TransitPresenceReporter.Poll"/> 会把自己限流到
/// <c>0.25 s</c>，并对主机（无客户端包装器）或战局之外立即返回。它存在的目的
/// 是让客户端在原生触发器或控制器回调不运行的构建上，仍然能告诉主机
/// 它的玩家站在哪里。
/// </summary>
public sealed class FikaTransitPresencePollPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => TransitPresenceBinding.Resolved(
        nameof(FikaTransitPresencePollPatch), typeof(GameWorld), "Update");

    [PatchPostfix]
    private static void Postfix(GameWorld __instance) => TransitPresenceReporter.Poll(__instance);
}

/// <summary>
/// 原生 <c>EFT.ClientTransitController.OnPlayerDead(Player, IPlayer, DamageInfo, EBodyPart)</c>
/// （声明在基类控制器上，因此可以从客户端的
/// <c>NetworkTransitController</c> 到达；已对照 dump 并通过
/// <c>LogOutput.log:598 Enabled patch FikaTransitDeathPresencePatch</c> 验证）是过境交互的
/// 死亡路径。它是"该玩家再也不会站进该区域"的精确信号，因此
/// 主机可以丢弃客户端自己不会清除的登记。客户端分支上报自己的
/// 死亡；主机分支清除其本地映射表。
/// </summary>
public sealed class FikaTransitDeathPresencePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => TransitPresenceBinding.Resolved(
        nameof(FikaTransitDeathPresencePatch), typeof(EFT.ClientTransitController), "OnPlayerDead",
        typeof(Player), typeof(EFT.IPlayer), typeof(DamageInfo), typeof(EBodyPart));

    [PatchPrefix]
    private static void Prefix(EFT.ClientTransitController __instance, Player player)
    {
        if (player == null) return;
        if (FikaWorldControllers.Find<ClientTransitController>(__instance) is { } client)
        {
            client.SendZonePresence(0, false, "died", player.ProfileId);
            if (player.IsYourPlayer)
            {
                // 死掉的玩家不能被任何倒计时携带：本机丢弃自己的过境
                // 登记、归还其退出交互并关闭自己显示的倒计时面板，而
                // 主机对同一个 profile 应用同样的规则（TransitCountdownGuard：死亡会丢弃
                // 该参与者，或在 Coop / Transit: Abort When Someone Dies 开启时取消传送）。
                client.CancelLocalCountdown(-1, "this machine's player died", "death hook");
            }

            return;
        }

        FikaWorldControllers.Find<FikaHostTransitController>(__instance)?.OnHostPlayerDead(player);
    }
}

/// <summary>
/// 原生 <c>EFT.ClientTransitController.ShowInteraction(Int32, Player, EDateTime)</c>（interop dump
/// 238472 / 238522）是唯一把过境交互呈现到玩家面前的调用。本地战局
/// 从自己的 <c>PlayerEnter</c> 路径到达它；网络客户端则需要主机的
/// <c>TransitInteractionEvent</c>（带 <c>EType.Show</c>），而主机只在
/// 应答自己原生 <c>PlayerEnter</c> 的那个分支里构建该数据包——远端玩家永远到不了这个方法。该
/// Postfix 告诉客户端控制器有一个并非本机自己弹出的提示，因此客户端日志
/// 可以回答究竟是主机驱动了交互，还是客户端不得不自己驱动。
/// </summary>
public sealed class FikaTransitClientShowInteractionPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => TransitPresenceBinding.Resolved(
        nameof(FikaTransitClientShowInteractionPatch), typeof(EFT.ClientTransitController), "ShowInteraction",
        typeof(int), typeof(Player), typeof(EDateTime));

    [PatchPostfix]
    private static void Postfix(EFT.ClientTransitController __instance, int pointId, Player player)
    {
        if (player == null || !player.IsYourPlayer) return;
        FikaWorldControllers.Find<ClientTransitController>(__instance)?.NoteNativePromptShown(pointId, player);
    }
}

/// <summary>
/// 原生 <c>EFT.ClientTransitController.EnsureConfirmed(TransitPoint, Player, Action)</c>（interop dump
/// 238471 / 238521）是按下过境提示之后、到向
/// <c>EFT.Player.TransitInteraction</c> 索取请求的那个交互动作之间的一步——也就是 Fika 从
/// <see cref="Fika.Core.Main.Players.FikaPlayer.TransitInteraction"/> 发给主机的那个请求。这里会记录按下动作与本地确认
/// 状态，控制器还会上报卡住的按下动作，而这是客户端可能被阻塞
/// 而主机却完全不知情的唯一一处。
/// </summary>
public sealed class FikaTransitClientEnsureConfirmedPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => TransitPresenceBinding.Resolved(
        nameof(FikaTransitClientEnsureConfirmedPatch), typeof(EFT.ClientTransitController), "EnsureConfirmed",
        (System.Type[])null);

    [PatchPrefix]
    private static void Prefix(EFT.ClientTransitController __instance, TransitPoint point, Player player)
    {
        if (player == null || !player.IsYourPlayer) return;
        FikaWorldControllers.Find<ClientTransitController>(__instance)?.NotePressAttempt(point, player);
    }

    [PatchPostfix]
    private static void Postfix(EFT.ClientTransitController __instance, TransitPoint point, Player player)
    {
        if (player == null || !player.IsYourPlayer) return;
        FikaWorldControllers.Find<ClientTransitController>(__instance)?.NotePressCompleted(point, player);
    }
}
