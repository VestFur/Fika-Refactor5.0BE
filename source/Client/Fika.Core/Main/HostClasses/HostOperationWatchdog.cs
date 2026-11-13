using System;
using System.Collections.Generic;
using Comfort.Common;
using Fika.Core.Main.Utils;
using UnityEngine;

namespace Fika.Core.Main.HostClasses;

/// <summary>
/// 主机端库存操作以 fire-and-forget 方式派发。当某个操作始终不进入终态结果时，
/// 原生的 EFT.PickUpState 回调便永远不会被完成，玩家会一直停留在拾取状态，
/// 直到 CoopGame 的 5 秒交互看门狗将其释放。本看门狗在 10 秒后令该操作失败并
/// 释放它，因此丢失的回调不会把拾取锁死整场 raid。
///
/// 处理器是池化复用的。因此条目会记录它被创建时对应的那个操作：
/// 某个旧条目的处理器若已在运行另一个操作，该条目即为过期，必须直接丢弃，
/// 绝不能拿它去对新操作做超时判定。
/// </summary>
internal static class HostOperationWatchdog
{
    private const float TimeoutSeconds = 10f;

    private sealed class Entry
    {
        internal HostInventoryOperationHandler Handler;
        internal EFT.InventoryLogic.Operations.AbstractOperation Operation;
        internal float Since;
        internal string Label;
    }

    private static readonly List<Entry> Pending = new();

    internal static void Track(HostInventoryOperationHandler handler, string label)
    {
        try
        {
            if (handler == null || handler.Operation == null)
            {
                return;
            }

            lock (Pending)
            {
                Pending.Add(new Entry
                {
                    Handler = handler,
                    Operation = handler.Operation,
                    Since = Time.unscaledTime,
                    Label = label
                });
            }
        }
        catch
        {
        }
    }

    internal static void Tick()
    {
        try
        {
            var now = Time.unscaledTime;
            List<Entry> expired = null;
            lock (Pending)
            {
                if (Pending.Count == 0)
                {
                    return;
                }

                // 反向遍历并原地移除：这段逻辑跑在常规的主机端库存流量里，
                // 因此不做逐帧的快照拷贝。只有真正超时时才会分配 expired 列表。
                for (var i = Pending.Count - 1; i >= 0; i--)
                {
                    var entry = Pending[i];
                    var handler = entry.Handler;
                    if (handler == null || handler.Completed ||
                        !ReferenceEquals(handler.Operation, entry.Operation))
                    {
                        Pending.RemoveAt(i);
                        continue;
                    }

                    if (now - entry.Since < TimeoutSeconds)
                    {
                        continue;
                    }

                    Pending.RemoveAt(i);
                    (expired ??= new List<Entry>()).Add(entry);
                }
            }

            if (expired == null)
            {
                return;
            }

            for (var i = 0; i < expired.Count; i++)
            {
                FailTimedOut(expired[i], now);
            }
        }
        catch
        {
        }
    }

    /// <summary>清除每场 raid 的状态，使上一场 raid 池化下来的操作永远无法在这里超时。</summary>
    internal static void Reset()
    {
        try
        {
            lock (Pending)
            {
                Pending.Clear();
            }
        }
        catch
        {
        }
    }

    private static void FailTimedOut(Entry entry, float now)
    {
        var handler = entry.Handler;
        if (handler == null)
        {
            return;
        }

        FikaGlobals.LogWarning($"[LootFix] host-op-timeout label={entry.Label} " +
            $"age={now - entry.Since:0.0}s", "HostOperationWatchdog");

        var controller = handler.InventoryController;
        try
        {
            handler.Callback?.Fail("host operation timed out", 1);
        }
        catch
        {
        }

        try
        {
            handler.CancelDispatch();
        }
        catch
        {
        }

        try
        {
            NativeInteractionRelease.Release(controller?.FikaPlayer, "host-op-timeout");
        }
        catch
        {
        }
    }
}
