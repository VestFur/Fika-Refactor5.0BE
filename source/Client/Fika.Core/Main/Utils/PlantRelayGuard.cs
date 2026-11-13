using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;

namespace Fika.Core.Main.Utils;

/// <summary>
/// (analysis/plant-client-host-crash)主机侧针对唯一一种被转发的操作形态的守卫,该形态
/// 会让主机在*远端*玩家的物品图上运行原生库存操作:
/// <c>EFT.RemoveOperationDescriptor</c>。
///
/// 主机目前对客户端 descriptor 所做的处理
/// (<c>FikaServer.Callbacks.OnInventoryPacketReceived</c> -> 发送者的
/// <c>ObservedInventoryController.CreateOperationFromDescriptor</c>,ObservedInventoryController.cs:320-324)
/// 就是把它交给原生重建:<c>RemoveOperationDescriptor.ToInventoryOperation(IPlayer)</c> =
/// <c>Player.FindItemById</c> + <c>ItemManipulator.Discard(item, controller, simulate: true)</c> +
/// <c>new RemoveOperation(operationId, controller, discardResult)</c>,随后结果被
/// 以原生方式执行(4.0 反编译 <c>eft40-decompiled/GClass1977.cs:24-37</c>)。在第一次被转发的种植之前,
/// 这条链从未在本安装中对远端玩家运行过(
/// logs-coop2-20261013-110333/LogOutput.log 恰好只包含两次移除形态的发送,都来自该
/// 种植,且其中一次得到的回答是 "Could not find item with id")。
///
/// 本守卫就是该转发所缺失的防御性提前退出:一次主机已经能判断自己
/// 无法完成的移除——一次刚刚接受过的移除的重复(种植门对每次种植会触发两次,见
/// PlantItemConsumptionPatch)、格式错误的 descriptor,或预检读不到的任何东西——都会在
/// 原生重建之前于托管代码中被拒绝,只留下恰好一行 [PlantGuard] 警告,并给出
/// 与其他所有拒绝相同的回答(Failed + 权威重同步 + id 重同步),因此
/// 客户端永远不会干等,原生路径也永远看不到它。
///
/// 没有任何拒绝时,代价是一次 null 判断、一次 il2cpp 类型判断以及(仅对
/// RemoveOperationDescriptor)一次字典查找。开关:Fika.cfg -> [Gameplay] ->
/// "Plant Relay Guard"(默认开启)。
/// </summary>
internal static class PlantRelayGuard
{
    /// <summary>
    /// 主机已接受的移除在账本中保留多久。长到足以捕获一次种植的原生
    /// 双重派发(同一帧),又短到同一份电荷可以在之后的 raid 中
    /// 再次被移除而不会被拒绝。
    /// </summary>
    private const float LedgerTtlSeconds = 60f;

    /// <summary>硬性上限,使病态对端无法让账本无界增长。</summary>
    private const int LedgerLimit = 128;

    /// <summary>本主机已承诺执行的移除,以 netId + item id 为键。</summary>
    private static readonly Dictionary<string, float> _accepted = new(StringComparer.Ordinal);

    /// <summary>
    /// 行为开关。开启(默认)时执行上面的预检。关闭时:descriptor 会像在本构建
    /// 之前那样直接进入原生重建(一键回滚)。
    /// </summary>
    internal static bool Enabled
    {
        get
        {
            try
            {
                var settings = FikaPlugin.Instance?.Settings;
                return settings?.PlantRelayGuard == null || settings.PlantRelayGuard.Value;
            }
            catch
            {
                // 这里切换的是一个真能拒绝工作的守卫:读不到设置对象时
                // 保持开启。
                return true;
            }
        }
    }

    /// <summary>
    /// 该操作是否必须被拒绝而不是被重建。False 表示「像以前那样
    /// 原样交给转发」;true 表示调用方必须回应 Failed + 重同步然后返回。
    /// </summary>
    internal static bool TryRefuse(FikaPlayer sender, InventoryOperationDescriptor descriptor, out string reason)
    {
        reason = null;
        try
        {
            if (!Enabled)
            {
                return false;
            }

            if (descriptor == null)
            {
                reason = "no-descriptor";
                return true;
            }

            // 只有摧毁形态的 descriptor 会走预检。其他每个操作都保持
            // 守卫之前的老路径,因此普通流量只付一次类型判断的代价。
            if (descriptor.TryCast<RemoveOperationDescriptor>() is not { } removal)
            {
                return false;
            }

            if (sender == null)
            {
                reason = "no-sender";
                return true;
            }

            var itemKey = Key(sender, removal);
            if (itemKey == null)
            {
                // 没有 item id 的移除在原生侧同样无法重建;在此拒绝
                // 就是同样的回答,外加一行证据。
                reason = "empty-item-id";
                return true;
            }

            Prune();

            if (_accepted.ContainsKey(itemKey))
            {
                reason = "duplicate-removal";
                return true;
            }

            // 主机即将承诺执行这次移除。在原生重建之前先记下它,以便同一
            // 次移除的第二份副本(同一次种植、同一个 tick)会在上面被拒绝。
            _accepted[itemKey] = Time.time;
            ReportAccepted(sender.NetId, removal, itemKey);
            return false;
        }
        catch (Exception exception)
        {
            // 会抛异常的守卫不得决定结果:拒绝,这样原生路径保持关闭。
            reason = "preflight-threw: " + exception.Message;
            return true;
        }
    }

    /// <summary>
    /// 主机拒绝一个自己不会重建的操作时写下的那一行。它刻意不放在
    /// 探针开关之后:拒绝很少发生,而且它是证据的主机侧那一半
    /// (「客户端发来了本主机不会执行的东西」)。
    /// </summary>
    internal static void ReportRefusal(int netId, ushort callbackId, InventoryOperationDescriptor descriptor, string reason)
    {
        try
        {
            FikaGlobals.LogWarning($"[PlantGuard] host-refuse-unsupported netId={netId} op={callbackId} " +
                $"descriptor={Describe(descriptor)} reason={reason} " +
                "-> answered Failed + authoritative inventory, the native rebuild was never entered");
        }
        catch
        {
            // 上报绝不能把数据包处理器弄崩。
        }
    }

    /// <summary>
    /// 客户端那行 "Sending inventory operation ... (RemoveOperation)" 的主机侧对应物:
    /// 本主机为哪个对端接受了哪一次移除。受探针开关控制(默认零开销)。
    /// </summary>
    private static void ReportAccepted(int netId, RemoveOperationDescriptor removal, string itemKey)
    {
        

        try
        {
            FikaGlobals.LogInfo($"[PlantGuard] host-accept-removal netId={netId} op={removal.OperationId} " +
                $"item={removal.ItemId} key={itemKey} side=host step=preflight");
        }
        catch
        {
            // 诊断绝不能改变结果。
        }
    }

    internal static string Describe(InventoryOperationDescriptor descriptor)
    {
        try
        {
            return descriptor == null ? "none" : descriptor.GetIl2CppType()?.Name ?? descriptor.ToString();
        }
        catch
        {
            return "unreadable";
        }
    }

    private static string Key(FikaPlayer sender, RemoveOperationDescriptor removal)
    {
        try
        {
            var itemId = removal.ItemId.ToString();
            return string.IsNullOrEmpty(itemId) ? null : $"{sender.NetId}:{itemId}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>丢弃账本中的过期条目,以及在达到上限时丢弃最旧的条目。</summary>
    private static void Prune()
    {
        var now = Time.time;
        List<string> stale = null;
        foreach (var entry in _accepted)
        {
            if (now - entry.Value > LedgerTtlSeconds)
            {
                (stale ??= []).Add(entry.Key);
            }
        }

        if (stale != null)
        {
            foreach (var key in stale)
            {
                _accepted.Remove(key);
            }
        }

        if (_accepted.Count <= LedgerLimit)
        {
            return;
        }

        // 只有移除了超过一场 raid 所能容纳物品数的对端才可能触到该上限:保留
        // 最新的条目、放走最旧的条目,而不是无界增长。
        List<KeyValuePair<string, float>> ordered = [.. _accepted];
        ordered.Sort((left, right) => left.Value.CompareTo(right.Value));
        var excess = _accepted.Count - LedgerLimit;
        for (var index = 0; index < excess; index++)
        {
            _accepted.Remove(ordered[index].Key);
        }
    }
}
