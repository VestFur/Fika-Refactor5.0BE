using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Main.BaseClasses;

/// <summary>Associates native world controllers with their managed co-op state.</summary>
public static class FikaWorldControllers
{
    private static readonly Dictionary<IntPtr, IDisposable> Controllers = new();

    internal static void Add(Il2CppObjectBase native, IDisposable controller) => Controllers.Add(native.Pointer, controller);
    internal static void Remove(Il2CppObjectBase native) => Controllers.Remove(native.Pointer);

    public static T Find<T>(Il2CppObjectBase native) where T : class =>
        native != null && Controllers.TryGetValue(native.Pointer, out var controller) ? controller as T : null;
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
