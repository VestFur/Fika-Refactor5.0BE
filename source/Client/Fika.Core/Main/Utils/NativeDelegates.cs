using Il2CppInterop.Runtime;

namespace Fika.Core;

internal static class NativeDelegates
{
    // Bind native methods directly so native code can later remove the same
    // target/method pair. A managed trampoline would have a different identity.
    public static T Bind<T>(Il2CppSystem.Object target, string method) where T : Il2CppSystem.Delegate =>
        Il2CppSystem.Delegate.CreateDelegate(Il2CppType.Of<T>(), target, method).Cast<T>();

    public static T Find<T>(Il2CppSystem.Delegate handlers, Il2CppSystem.Object target, string method)
        where T : Il2CppSystem.Delegate
    {
        if (handlers == null || target == null) return null;
        foreach (var handler in handlers.GetInvocationList())
        {
            if (handler.Target?.Pointer == target.Pointer && handler.Method?.Name == method)
                return handler.Cast<T>();
        }
        return null;
    }
}
