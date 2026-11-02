using System;
using System.Collections.Generic;
using Fika.Core.Main.Utils;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Main.BaseClasses;

/// <summary>把原生世界控制器与其托管协作状态关联起来。</summary>
public static class FikaWorldControllers
{
    private static readonly Dictionary<IntPtr, Entry> Controllers = new();

    /// <summary>
    /// 一个已注册的控制器，外加它所属的世界世代（analysis/transit-crash-unload）。
    /// 正是这个世代号让查找在跨地图变更时保持安全：上一个世界释放之后，分配器
    /// 可能把同一个世界控制器的原生地址再次分发出去，而仅以裸指针
    /// 为键的查找届时会把*旧的*托管控制器返回给*新的*原生对象——
    /// 这正是控制器与一个已经消失的世界对话的形态。
    /// </summary>
    private readonly struct Entry(IDisposable controller, int epoch)
    {
        internal readonly IDisposable Controller = controller;
        internal readonly int Epoch = epoch;
    }

    private static int _staleReports;

    internal static void Add(Il2CppObjectBase native, IDisposable controller) =>
        Controllers[native.Pointer] = new Entry(controller, WorldLifecycle.Epoch);

    internal static void Remove(Il2CppObjectBase native) => Controllers.Remove(native.Pointer);

    public static T Find<T>(Il2CppObjectBase native) where T : class
    {
        if (native == null || !Controllers.TryGetValue(native.Pointer, out var entry))
        {
            return null;
        }

        if (!WorldLifecycle.IsCurrent(entry.Epoch))
        {
            // 控制器所属的世界已经销毁：丢弃它并上报最初的几例，这样
            // 一次漏掉了世界控制器释放的运行就能被指名道姓地说出来。
            Controllers.Remove(native.Pointer);
            if (_staleReports < 8)
            {
                _staleReports++;
                FikaGlobals.LogWarning("[WorldLifecycle] a " + typeof(T).Name + " of world epoch " + entry.Epoch
                    + " was looked up while epoch " + WorldLifecycle.Epoch + " is current; the entry was dropped"
                    + " instead of being used on the native object at " + native.Pointer.ToInt64().ToString("X"));
            }

            return null;
        }

        return entry.Controller as T;
    }

    /// <summary>
    /// 在世界拆除时丢弃所有条目，并返回原本有多少个。这些控制器*不会*
    /// 在这里被 Dispose：它们的原生所有者已经消失，而 Dispose 会调用到已释放的
    /// 原生对象（这正是本方法存在的目的——防止那种访问违规）。一旦没有任何东西
    /// 再引用这些托管包装器，它们会照常被回收。
    /// </summary>
    internal static int ClearForWorldTeardown()
    {
        var count = Controllers.Count;
        if (count > 0)
        {
            Controllers.Clear();
        }

        return count;
    }
}

public abstract class FikaWorldController<TNative> : IDisposable where TNative : Il2CppObjectBase
{
    private bool _disposed;

    protected FikaWorldController(TNative native)
    {
        Native = native;
        FikaWorldControllers.Add(native, this);
    }

    public TNative Native { get; }

    public void Dispose()
    {
        if (_disposed) return;
        try { DisposeNative(); }
        finally { OnNativeDisposed(); }
    }

    internal void OnNativeDisposed()
    {
        if (_disposed) return;
        _disposed = true;
        FikaWorldControllers.Remove(Native);
        DisposeManaged();
    }

    protected abstract void DisposeNative();
    protected virtual void DisposeManaged() { }
}
