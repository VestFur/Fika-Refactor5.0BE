using System;
using System.Collections.Concurrent;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Main.Utils;

/// <summary>访问内联的 Nullable 字段，避开 IL2CPP 的特殊装箱。</summary>
public static class NativeNullable<T> where T : new()
{
    private static readonly ConcurrentDictionary<(IntPtr Class, string Name), IntPtr> Fields = new();

    private static IntPtr Resolve(Il2CppObjectBase owner, string name) =>
        Fields.GetOrAdd((IL2CPP.il2cpp_object_get_class(owner.Pointer), name), key =>
        {
            var field = IL2CPP.il2cpp_class_get_field_from_name(key.Class, key.Name);
            if (field == IntPtr.Zero || IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_field_get_type(field)) !=
                Il2CppClassPointerStore<Il2CppSystem.Nullable<T>>.NativeClassPtr)
                throw new MissingFieldException($"Expected native Nullable<{typeof(T).Name}> field {key.Name}");
            return field;
        });

    public static unsafe Il2CppSystem.Nullable<T> Read(Il2CppObjectBase owner, string name)
    {
        var result = new Il2CppSystem.Nullable<T>();
        // 生成的 getter 使用 value_box，它对
        // Nullable<T> 会返回 null 或装箱后的 T。请把内联值复制进真正的 Nullable 包装器。
        IL2CPP.il2cpp_field_get_value(owner.Pointer, Resolve(owner, name),
            (void*)IL2CPP.il2cpp_object_unbox(result.Pointer));
        GC.KeepAlive(owner);
        return result;
    }

    public static unsafe void Write(Il2CppObjectBase owner, string name, Il2CppSystem.Nullable<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        IL2CPP.il2cpp_field_set_value(owner.Pointer, Resolve(owner, name),
            (void*)IL2CPP.il2cpp_object_unbox(value.Pointer));
        GC.KeepAlive(value);
        GC.KeepAlive(owner);
    }
}
