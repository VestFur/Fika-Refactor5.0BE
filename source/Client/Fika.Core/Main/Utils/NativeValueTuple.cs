using System;
using Il2CppInterop.Runtime;

namespace Fika.Core.Main.Utils;

internal static class NativeValueTuple<T1, T2> where T1 : unmanaged where T2 : unmanaged
{
    private static readonly IntPtr First = Resolve("Item1", Il2CppClassPointerStore<T1>.NativeClassPtr);
    private static readonly IntPtr Second = Resolve("Item2", Il2CppClassPointerStore<T2>.NativeClassPtr);

    private static IntPtr Resolve(string name, IntPtr expected)
    {
        var field = IL2CPP.il2cpp_class_get_field_from_name(
            Il2CppClassPointerStore<Il2CppSystem.ValueTuple<T1, T2>>.NativeClassPtr, name);
        if (field == IntPtr.Zero || IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_field_get_type(field)) != expected)
            throw new MissingFieldException("Native tuple field " + name);
        return field;
    }

    internal static unsafe (T1, T2) Read(Il2CppSystem.ValueTuple<T1, T2> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        T1 first = default;
        T2 second = default;
        // 在此互操作运行时中，生成的泛型 getter 会对基元/枚举字段重复装箱，
        // 并把装箱头部当作值读取。请直接读取原生字段。
        IL2CPP.il2cpp_field_get_value(value.Pointer, First, &first);
        IL2CPP.il2cpp_field_get_value(value.Pointer, Second, &second);
        GC.KeepAlive(value);
        return (first, second);
    }
}
