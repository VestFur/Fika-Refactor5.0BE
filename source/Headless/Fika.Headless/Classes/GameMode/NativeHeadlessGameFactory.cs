using System;
using System.Linq;
using System.Reflection;
using EFT;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppObjectPool = Il2CppInterop.Runtime.Runtime.Il2CppObjectPool;

namespace Fika.Headless.Classes.GameMode;

/// <summary>
/// 以原生的 HeadlessGame 泛型参数调用 AbstractGame.Create。
/// 对于注入的游戏类型，生成的 CLR 泛型方法可能解析到错误的 IL2CPP 泛型
/// 仓库，因此改为通过原生反射来完成。
/// </summary>
internal static class NativeHeadlessGameFactory
{
    private static IntPtr _method;

    internal static IntPtr Resolve()
    {
        if (_method != IntPtr.Zero) return _method;
        var field = typeof(AbstractGame).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(f => f.Name.StartsWith("NativeMethodInfoPtr_Create_", StringComparison.Ordinal));
        var definition = (IntPtr)field.GetValue(null);
        var reflected = new Il2CppSystem.Reflection.MethodInfo(IL2CPP.il2cpp_method_get_object(
            definition, Il2CppClassPointerStore<AbstractGame>.NativeClassPtr));
        var closed = reflected.MakeGenericMethod(new Il2CppReferenceArray<Il2CppSystem.Type>(
            new[] { Il2CppType.Of<HeadlessGame>() }));
        _method = IL2CPP.il2cpp_method_get_from_reflection(closed.Pointer);
        if (_method == IntPtr.Zero)
            throw new MissingMethodException("Native AbstractGame.Create<HeadlessGame> is unavailable.");
        return _method;
    }

    internal static unsafe HeadlessGame Create(EUpdateQueue updateQueue,
        Il2CppSystem.Nullable<Il2CppSystem.TimeSpan> sessionTime, string goName = "GAME")
    {
        var args = stackalloc IntPtr[3];
        args[0] = (IntPtr)(&updateQueue);
        args[1] = IL2CPP.il2cpp_object_unbox(IL2CPP.Il2CppObjectBaseToPtrNotNull(sessionTime));
        args[2] = IL2CPP.ManagedStringToIl2Cpp(goName);
        var exception = IntPtr.Zero;
        try
        {
            var result = IL2CPP.il2cpp_runtime_invoke(Resolve(), IntPtr.Zero, (void**)args, ref exception);
            Il2CppException.RaiseExceptionIfNecessary(exception);
            if (result == IntPtr.Zero)
                throw new InvalidOperationException("Native headless game factory returned null.");
            return Il2CppObjectPool.Get<HeadlessGame>(result);
        }
        finally { GC.KeepAlive(sessionTime); }
    }
}
