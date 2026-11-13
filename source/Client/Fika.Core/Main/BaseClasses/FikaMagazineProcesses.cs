using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Comfort.Common;
using EFT.InventoryLogic;
using Fika.Core.Main.Utils;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Main.BaseClasses;

internal interface IFikaMagazineProcess
{
    bool IsCancelled { get; }
    Task<IResult> Start();
    void Proceed(bool success);
    void TryProceedForItem(Item item);
}

/// <summary>保存游戏的进程句柄，使原生停止/物品变更路径能够到达 Fika 的批处理进程。</summary>
internal static class FikaMagazineProcesses
{
    private static readonly Dictionary<IntPtr, Entry> Processes = new();

    internal static void Add(Il2CppObjectBase native, IFikaMagazineProcess process) =>
        Processes[native.Pointer] = new Entry(process, WorldLifecycle.Epoch);

    internal static void Remove(Il2CppObjectBase native) => Processes.Remove(native.Pointer);

    internal static IFikaMagazineProcess Find(Il2CppObjectBase native)
    {
        // AP（analysis/nonfika-raid-inertness）：2026-10-12 23:52 那次崩溃终止于这条
        // unload/load 链路（"Perform UNLOAD" 是快速失败前的最后一行）。无论如何，在
        // 非我们自己的战局之外注册表都是空的，所以下面的条目不会改变任何行为——
        // 它只是把这个问题显式化，并在原生 unload 询问时把 [Inert] skip=FikaMagazineProcesses.Find 写进日志。
        if (FikaRaidGate.Skip("FikaMagazineProcesses.Find"))
        {
            return null;
        }

        if (native == null || !Processes.TryGetValue(native.Pointer, out var entry))
        {
            return null;
        }

        if (!WorldLifecycle.IsCurrent(entry.Epoch))
        {
            // 已释放世界的弹匣进程（analysis/transit-crash-unload）：直接丢弃，绝不进入。
            Processes.Remove(native.Pointer);
            return null;
        }

        return entry.Process;
    }

    /// <summary>在世界销毁时丢弃所有进程；返回丢弃的数量。</summary>
    internal static int ClearForWorldTeardown()
    {
        var count = Processes.Count;
        if (count > 0)
        {
            Processes.Clear();
        }

        return count;
    }

    /// <summary>一个进程以及它所属的世界代号。</summary>
    private readonly struct Entry(IFikaMagazineProcess process, int epoch)
    {
        internal readonly IFikaMagazineProcess Process = process;
        internal readonly int Epoch = epoch;
    }
}
