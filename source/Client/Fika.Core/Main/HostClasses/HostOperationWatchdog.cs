using System;
using System.Collections.Generic;
using Comfort.Common;
using Fika.Core.Main.Utils;
using UnityEngine;

namespace Fika.Core.Main.HostClasses;

/// <summary>
/// Host inventory operations are dispatched fire-and-forget. When an operation never reaches a
/// terminal result, the native EFT.PickUpState callback is never completed and the player stays in
/// the pickup state until CoopGame's 5 s interaction watchdog releases it. This watchdog fails and
/// releases the operation after 10 s, so a lost callback cannot lock pickup for the rest of the raid.
///
/// The handler is pooled and reused. The entry therefore stores the operation it was created for:
/// an old entry whose handler already runs another operation is stale and must be dropped, never
/// timed out against the new operation.
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

                // Walk backwards and remove in place: this runs during ordinary host inventory traffic,
                // so no per-frame snapshot copy. Only a real timeout allocates the expired list.
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

    /// <summary>Clears per-raid state so a pooled operation from a previous raid can never time out here.</summary>
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
