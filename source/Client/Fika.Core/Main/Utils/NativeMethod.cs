using System;
using System.Reflection;
using System.Collections.Concurrent;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Main.Utils;

/// <summary>调用指定的原生实现，不经过虚方法重新分派。</summary>
internal static class NativeMethod
{
    private static readonly ConcurrentDictionary<MethodInfo, IntPtr> Methods = new();

    private static IntPtr Resolve(MethodInfo method, int argumentCount)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (method.IsStatic || method.IsAbstract || method.ContainsGenericParameters ||
            method.ReturnType != typeof(void) || method.GetParameters().Length != argumentCount)
            throw new ArgumentException($"Unsupported native void signature: {method}", nameof(method));
        return Methods.GetOrAdd(method, value =>
        {
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(value)
                ?? throw new MissingMethodException($"No native implementation for {value}");
            var pointer = (IntPtr)field.GetValue(null);
            if (pointer == IntPtr.Zero) throw new MissingMethodException($"Native method pointer is null: {value}");
            return pointer;
        });
    }

    internal static unsafe void InvokeVoid(Il2CppObjectBase target, MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(target);
        var pointer = Resolve(method, 0);
        var exception = IntPtr.Zero;
        try
        {
            IL2CPP.il2cpp_runtime_invoke(pointer, target.Pointer, null, ref exception);
            Il2CppException.RaiseExceptionIfNecessary(exception);
        }
        finally { GC.KeepAlive(target); }
    }

    /// <summary>调用带一个托管 IL2CPP 对象参数的原生 void 方法。</summary>
    internal static unsafe void InvokeVoid(Il2CppObjectBase target, MethodInfo method, Il2CppObjectBase argument)
    {
        ArgumentNullException.ThrowIfNull(target);
        var pointer = Resolve(method, 1);
        var parameter = method.GetParameters()[0].ParameterType;
        if (!typeof(Il2CppObjectBase).IsAssignableFrom(parameter) ||
            typeof(Il2CppSystem.ValueType).IsAssignableFrom(parameter) ||
            (argument != null && !parameter.IsInstanceOfType(argument)))
            throw new ArgumentException($"Expected a native reference argument for {method}", nameof(argument));
        var argumentPointer = argument == null ? IntPtr.Zero : argument.Pointer;
        var exception = IntPtr.Zero;
        var arguments = stackalloc void*[1];
        arguments[0] = (void*)argumentPointer;
        try
        {
            IL2CPP.il2cpp_runtime_invoke(pointer, target.Pointer, arguments, ref exception);
            Il2CppException.RaiseExceptionIfNecessary(exception);
        }
        finally
        {
            GC.KeepAlive(argument);
            GC.KeepAlive(target);
        }
    }
}
