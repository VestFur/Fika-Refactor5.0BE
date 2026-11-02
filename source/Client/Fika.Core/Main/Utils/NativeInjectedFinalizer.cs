using System;
using System.Collections.Concurrent;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Runtime;

namespace Fika.Core.Main.Utils;

/// <summary>未经构造函数分配的原生对象不持有任何需要释放的 CLR 句柄。</summary>
internal static class NativeInjectedFinalizer
{
    private static readonly ConcurrentDictionary<IntPtr, byte> FikaClasses = new();
    private static Harmony _harmony;
    internal static int UnboundFinalizations;

    internal static void Register(Type type)
    {
        // 把这个运行时的兼容性修补限定在我们自己注册过的类上。
        FikaClasses.TryAdd(Il2CppClassPointerStore.GetNativeClassPointer(type), 0);
        if (_harmony != null) return;
        _harmony = new Harmony("com.fika.core.unbound-finalizer");
        _harmony.Patch(AccessTools.DeclaredMethod(typeof(ClassInjector), nameof(ClassInjector.Finalize), new[] { typeof(IntPtr) }),
            prefix: new HarmonyMethod(typeof(NativeInjectedFinalizer), nameof(HasHandleToRelease)));
    }

    private static bool HasHandleToRelease(IntPtr ptr)
    {
        if (!FikaClasses.ContainsKey(IL2CPP.il2cpp_object_get_class(ptr)) ||
            ClassInjectorBase.GetGcHandlePtrFromIl2CppObject(ptr) != IntPtr.Zero) return true;
        // 这样的对象从未执行过 ProcessNewObject/DerivedConstructorBody。
        // 与正常绑定的实例不同，它没有任何托管状态需要释放。
        Interlocked.Increment(ref UnboundFinalizations);
        return false;
    }
}
