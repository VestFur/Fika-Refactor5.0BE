using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.GlobalEvents;
using EFT.Interactive;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Communication;
using JsonType;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.TransitController;

/// <summary>
/// (analysis/transit-point-sync) 中转点 <c>Enabled</c> 标志的每次变更都会经过
/// 原生属性 setter <c>EFT.Interactive.TransitPoint.set_Enabled(Boolean)</c>（interop 转储 212407：
/// <c>P|EFT.Interactive.TransitPoint|Enabled|System.Boolean Enabled|get:public set:public</c>；生成的
/// 存根字段为 <c>NativeMethodInfoPtr_set_Enabled_Public_set_Void_Boolean_0</c>，转储 212312），
/// 因此这个 setter 就是本钩子所需的唯一事件：地图触发、战局开始的启用以及 Fika 自身的写入
/// 都会落到这里。
/// <para>
/// postfix 仅在主机上运行，并以既有的
/// <see cref="TransitEventPacket"/> 初始化重新广播主机的完整启用列表（<see cref="TransitEventPacket.ETransitEventType.Init"/>、
/// <see cref="TransitInitEvent"/>、<see cref="TransitEventPacket.EnabledPointIds"/>，构造方式同
/// <c>FikaServer.SendMapStateSnapshot</c>），且不指向任何玩家
/// （<see cref="TransitEventPacket.AnyPlayerRaidId"/>）。不新增包、不新增字段、不轮询；客户端
/// 双向应用该列表（ClientTransitController.SetPointEnabled）。
/// </para>
/// </summary>
internal sealed class TransitPoint_SetEnabled_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var property = typeof(TransitPoint).GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
        var target = property?.GetSetMethod(true);
        if (target == null)
        {
            throw new MissingMethodException("EFT.Interactive.TransitPoint.set_Enabled(Boolean)");
        }
        return target;
    }

    [PatchPostfix]
    public static void Postfix(TransitPoint __instance)
    {
        try
        {
            // 客户端应用快照时写入的是同一个属性；只有主机才是数据源。
            if (!FikaBackendUtils.IsServer) return;
            if (__instance == null || __instance.parameters == null) return;

            var pointId = __instance.parameters.id;
            var enabled = __instance.Enabled;
            // (2026-10-26) 以前没有真正发出的发送是静默的：`Send()` 返回 false，
            // postfix 一行日志都没有就返回，看起来完全就是「原生 setter 从未被调用」。
            // 现在原因会写进日志，因此一次主机运行就能判定两者中的哪一种。
            if (!TransitPointStateBroadcast.Send(out var skipReason))
            {
                FikaGlobals.LogWarning($"[SyncDiag] point-broadcast-skipped id={pointId} " +
                    $"enabled={(enabled ? "true" : "false")} reason={skipReason}");
                return;
            }

            FikaGlobals.LogWarning($"[SyncDiag] point-broadcast id={pointId} enabled={(enabled ? "true" : "false")}");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[SyncDiag] point-sync-failed where=host-broadcast error={exception.GetType().Name}: {exception.Message}");
        }
    }
}

/// <summary>
/// 构造并发送当前运行中战局的状态专用初始化：只有主机的完整
/// <c>point.Enabled</c> 列表，别无其他。由上面的 setter 钩子共用；它从不修改世界状态。
/// </summary>
internal static class TransitPointStateBroadcast
{
    internal static bool Send() => Send(out _);

    /// <summary>
    /// 同样的发送，附带未发生的原因（2026-10-26）。成功时为空；否则为
    /// <c>no-network</c>、<c>no-transit-controller</c>、<c>no-points-by-id</c> 之一，使主机日志能把接线问题
    /// 与从未运行的原生 setter 区分开。
    /// </summary>
    internal static bool Send(out string detail)
    {
        detail = string.Empty;
        var network = FikaSingleton<IFikaNetworkManager>.Instance;
        if (network == null)
        {
            detail = "no-network";
            return false;
        }

        var transit = Singleton<GameWorld>.Instance?.TransitController;
        if (transit == null)
        {
            detail = "no-transit-controller";
            return false;
        }

        if (transit.pointsById == null)
        {
            detail = "no-points-by-id";
            return false;
        }

        var points = new Il2CppSystem.Collections.Generic.Dictionary<int, LocationSettings.Location.TransitParameters>();
        var requirements = new Il2CppSystem.Collections.Generic.Dictionary<int, bool>();
        var enabled = new List<int>();
        foreach (var entry in transit.pointsById)
        {
            var point = entry.Value;
            if (point == null) continue;
            if (point.parameters != null && point.parameters.active)
            {
                points.Add(entry.Key, point.parameters);
            }
            // Fika 会把每项进入条件都报告为已满足（FikaServer.MapState.cs:178-183 与
            // FikaHostTransitController.CreateInitialization:3225-3228）：原生原始结果会在
            // 每个客户端 UI 中禁用该点，而按玩家校验仍留在显示提示的机器
            // 以及响应请求的主机上。
            requirements.Add(entry.Key, true);
            if (point.Enabled) enabled.Add(entry.Key);
        }

        var transitData = FikaBackendUtils.TransitData;
        var packet = new TransitEventPacket
        {
            EventType = TransitEventPacket.ETransitEventType.Init,
            TransitEvent = new TransitInitEvent
            {
                // 该初始化不属于任何玩家：它只承载主机的点状态，而哨兵
                // raid id 可防止任何接收方把它应用到自己的中转历史上。
                PlayerRaidId = TransitEventPacket.AnyPlayerRaidId,
                Points = points,
                CompletedQuestRequirementMetByPointId = requirements,
                TransitionCount = (ushort)System.Math.Clamp(transitData?.transitionCount ?? 0, 0, ushort.MaxValue),
                EventPlayer = transitData != null &&
                    (transitData.transitionType & ELocationTransition.Event) == ELocationTransition.Event
            },
            EnabledPointIds = enabled.ToArray(),
            RegisteredPointId = -1,
            KeyId = string.Empty,
            SelectedTime = default
        };

        network.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        return true;
    }
}
