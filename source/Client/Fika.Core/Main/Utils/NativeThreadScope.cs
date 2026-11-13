using System;
using Il2CppInterop.Runtime;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 原生序列化可能运行在 CLR HTTP 延续中。请把该
/// 线程连同其 GC 根注册给 IL2CPP，以执行这次同步的原生操作。
/// 这并不允许在工作线程上使用 Unity 场景 API，也不允许跨越 await。
/// </summary>
internal readonly struct NativeThreadScope : IDisposable
{
    private readonly IntPtr _attached;

    public NativeThreadScope()
    {
        _attached = IntPtr.Zero;
        if (IL2CPP.il2cpp_thread_current() != IntPtr.Zero) return;
        _attached = IL2CPP.il2cpp_thread_attach(IL2CPP.il2cpp_domain_get());
        if (_attached == IntPtr.Zero)
            throw new InvalidOperationException("Could not attach the managed worker thread to IL2CPP.");
    }

    public void Dispose()
    {
        if (_attached != IntPtr.Zero) IL2CPP.il2cpp_thread_detach(_attached);
    }
}
