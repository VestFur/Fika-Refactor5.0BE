using Il2CppInterop.Runtime;

namespace Fika.Core;

internal static class NativeDelegates
{
    // 直接绑定原生方法，这样原生代码稍后可以移除同一个
    // target/method 对。托管 trampoline 会具有不同的身份。
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

    // 47888 以托管委托类型（System.Action<...>）暴露了若干事件，
    // 而不是 Il2CppSystem 委托，所以无法使用 Il2CppSystem.Delegate.CreateDelegate。
    // 托管委托按 target+method 比较，因此同一个调用点也能取消订阅。
    public static T BindManaged<T>(Il2CppSystem.Object target, string method) where T : System.Delegate
    {
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
        var info = target.GetType().GetMethod(method, flags);
        if (info == null)
        {
            throw new System.MissingMethodException(target.GetType().FullName, method);
        }
        return (T)(object)System.Delegate.CreateDelegate(typeof(T), target, info);
    }

    /// <summary>
    /// <see cref="BindManaged{T}"/> 的变体：失败时上报而不是抛出异常。
    ///
    /// 参数列表中含有按值传递结构体（Nullable&lt;T&gt;、Vector3、
    /// DamageInfo、ValueStruct、一对小结构体……）的 CLR 委托并不总能跨过这道
    /// 边界被封送：互操作层在委托绑定时会构建按签名生成的转换计划，并在
    /// 某个参数是寄存器大小的时候以 ArgumentException 中止。这种形态已经通过
    /// 匹配器状态委托（Nullable&lt;float&gt;）中止过一次主机突袭。一次事件
    /// 订阅从不值得拿一整局突袭来冒，所以调用方会关掉那一个特性、保留自己的
    /// 拉取式路径，而不是把异常继续传播出去。
    /// </summary>
    public static bool TryBindManaged<T>(Il2CppSystem.Object target, string method, string site, out T handler)
        where T : System.Delegate
    {
        try
        {
            handler = BindManaged<T>(target, method);
        }
        catch (System.Exception exception)
        {
            handler = null;
            Fika.Core.Main.Utils.FikaGlobals.LogError(site + ": could not bind " + method + " as " + typeof(T).Name
                + "; this subscription is disabled and the raid continues: " + exception);
            return false;
        }

        if (handler is null)
        {
            Fika.Core.Main.Utils.FikaGlobals.LogWarning(site + ": " + method + " bound to a null " + typeof(T).Name);
            return false;
        }

        return true;
    }
}
