using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 为签名中提到可能被托管程序集遮蔽的类型的调用
/// 提供的最小 IL2CPP 反射。
/// <para>
/// BepInEx/plugins 下的托管 <c>Newtonsoft.Json.dll</c> 会在名称解析中
/// 压过 IL2CPP interop 代理，因此托管元数据再也无法表达
/// 诸如 <c>ToUnparsedData(Il2CppReferenceArray&lt;JsonConverter&gt;)</c>
/// 或 <c>JToken</c> 返回值这样的签名。按名字通过 IL2CPP
/// 解析这些类并用裸指针调用它们，可以完全绕开
/// 托管类型解析。
/// </para>
/// </summary>
internal static unsafe class NativeIl2Cpp
{
    internal static IntPtr OpenAssembly(string name)
    {
        var native = Marshal.StringToHGlobalAnsi(name);
        try
        {
            return IL2CPP.il2cpp_domain_assembly_open(IL2CPP.il2cpp_domain_get(), native);
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    internal static IntPtr FindClass(string assemblyName, string @namespace, string name)
    {
        var assembly = OpenAssembly(assemblyName);
        if (assembly == IntPtr.Zero) return IntPtr.Zero;
        var image = IL2CPP.il2cpp_assembly_get_image(assembly);
        return image == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_from_name(image, @namespace, name);
    }

    internal static IntPtr FindMethod(IntPtr type, string name, int parameterCount, string firstParameterType = null)
    {
        // 继承来的方法不属于派生类自身的方法列表，
        // 因此必须遍历整个层次结构。否则一个继承了目标方法的
        // 运行时类型（例如从菜单使用的后端会话）会报出
        // "could not resolve ... through IL2CPP"，调用方的
        // 托管回退随后又会被遮蔽的 Newtonsoft.Json 打败。
        var owner = type;
        while (owner != IntPtr.Zero)
        {
            var iterator = IntPtr.Zero;
            while (true)
            {
                var method = IL2CPP.il2cpp_class_get_methods(owner, ref iterator);
                if (method == IntPtr.Zero) break;
                if (IL2CPP.il2cpp_method_get_name_(method) != name) continue;
                if (IL2CPP.il2cpp_method_get_param_count(method) != (uint)parameterCount) continue;
                if (firstParameterType != null && parameterCount > 0 &&
                    ParameterTypeName(IL2CPP.il2cpp_method_get_param(method, 0)) != firstParameterType)
                {
                    continue;
                }

                return method;
            }

            owner = IL2CPP.il2cpp_class_get_parent(owner);
        }

        return IntPtr.Zero;
    }

    internal static string ParameterTypeName(IntPtr parameter)
    {
        if (parameter == IntPtr.Zero) return null;
        var handle = IL2CPP.il2cpp_type_get_object(parameter);
        return handle == IntPtr.Zero ? null : new Il2CppSystem.Type(handle).FullName;
    }

    internal static IntPtr FindGetter(IntPtr type, string propertyName, IntPtr fallbackType = default)
    {
        var native = Marshal.StringToHGlobalAnsi(propertyName);
        try
        {
            var owner = type;
            while (owner != IntPtr.Zero)
            {
                var property = IL2CPP.il2cpp_class_get_property_from_name(owner, native);
                if (property != IntPtr.Zero)
                {
                    var getter = IL2CPP.il2cpp_property_get_get_method(property);
                    if (getter != IntPtr.Zero) return getter;
                }

                // 基于名字的查找在某些运行时上会漏掉继承来的属性，
                // 因此也要遍历属性列表。
                var iterator = IntPtr.Zero;
                while (true)
                {
                    var candidate = IL2CPP.il2cpp_class_get_properties(owner, ref iterator);
                    if (candidate == IntPtr.Zero) break;
                    if (IL2CPP.il2cpp_property_get_name_(candidate) != propertyName) continue;
                    var getter = IL2CPP.il2cpp_property_get_get_method(candidate);
                    if (getter != IntPtr.Zero) return getter;
                }

                owner = IL2CPP.il2cpp_class_get_parent(owner);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }

        return fallbackType == IntPtr.Zero ? IntPtr.Zero : FindGetter(fallbackType, propertyName);
    }

    /// <summary>
    /// 按名字在程序集中的每个类里查找方法。扩展方法
    /// 会被编译进静态辅助类，这些类的名字并不出现在
    /// 调用点，因此无法事先指明声明类型。
    /// </summary>
    internal static IntPtr FindMethodInAssembly(string assemblyName, string methodName, int parameterCount,
        out string declaringType, string firstParameterType = null)
    {
        declaringType = null;
        var assembly = OpenAssembly(assemblyName);
        if (assembly == IntPtr.Zero) return IntPtr.Zero;
        var image = IL2CPP.il2cpp_assembly_get_image(assembly);
        if (image == IntPtr.Zero) return IntPtr.Zero;

        // 参数的类型名可能无法获得，因此精确的名字匹配只是
        // 偏好而非要求：唯一一个具有所请求的名字和参数个数的方法
        // 本身就已经没有歧义。
        var fallback = IntPtr.Zero;
        var fallbackType = (string)null;
        var matches = 0;

        var classCount = IL2CPP.il2cpp_image_get_class_count(image);
        for (var index = 0u; index < classCount; index++)
        {
            var type = IL2CPP.il2cpp_image_get_class(image, index);
            if (type == IntPtr.Zero) continue;

            var iterator = IntPtr.Zero;
            while (true)
            {
                var method = IL2CPP.il2cpp_class_get_methods(type, ref iterator);
                if (method == IntPtr.Zero) break;
                if (IL2CPP.il2cpp_method_get_name_(method) != methodName) continue;
                if (IL2CPP.il2cpp_method_get_param_count(method) != (uint)parameterCount) continue;

                matches++;
                var @namespace = IL2CPP.il2cpp_class_get_namespace_(type);
                var name = IL2CPP.il2cpp_class_get_name_(type);
                var owner = string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";

                if (firstParameterType != null && parameterCount > 0)
                {
                    var actual = ParameterTypeName(IL2CPP.il2cpp_method_get_param(method, 0));
                    if (actual != null && actual.Contains(firstParameterType))
                    {
                        declaringType = owner;
                        return method;
                    }
                }

                if (fallback != IntPtr.Zero) continue;
                fallback = method;
                fallbackType = owner;
            }
        }

        if (matches != 1) return IntPtr.Zero;
        declaringType = fallbackType;
        return fallback;
    }

    /// <summary>描述所有使用此名字的方法，用于诊断失败的查找。</summary>
    internal static string Describe(string assemblyName, string methodName)
    {
        var assembly = OpenAssembly(assemblyName);
        if (assembly == IntPtr.Zero) return $"{assemblyName} was not found";
        var image = IL2CPP.il2cpp_assembly_get_image(assembly);
        if (image == IntPtr.Zero) return $"{assemblyName} has no image";

        var builder = new System.Text.StringBuilder();
        var classCount = IL2CPP.il2cpp_image_get_class_count(image);
        for (var index = 0u; index < classCount; index++)
        {
            var type = IL2CPP.il2cpp_image_get_class(image, index);
            if (type == IntPtr.Zero) continue;

            var iterator = IntPtr.Zero;
            while (true)
            {
                var method = IL2CPP.il2cpp_class_get_methods(type, ref iterator);
                if (method == IntPtr.Zero) break;
                if (IL2CPP.il2cpp_method_get_name_(method) != methodName) continue;

                var @namespace = IL2CPP.il2cpp_class_get_namespace_(type);
                var name = IL2CPP.il2cpp_class_get_name_(type);
                builder.Append(string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}").Append('(');
                var parameters = IL2CPP.il2cpp_method_get_param_count(method);
                for (var parameter = 0u; parameter < parameters; parameter++)
                {
                    if (parameter > 0) builder.Append(", ");
                    builder.Append(ParameterTypeName(IL2CPP.il2cpp_method_get_param(method, parameter)) ?? "?");
                }

                builder.Append(") ");
            }
        }

        return builder.Length == 0 ? "no method named " + methodName : builder.ToString();
    }

    /// <summary>读取一个可能是属性也可能是字段的成员。</summary>
    internal static IntPtr ReadMember(IntPtr instance, string memberName)
    {
        if (instance == IntPtr.Zero) return IntPtr.Zero;
        var type = IL2CPP.il2cpp_object_get_class(instance);

        var getter = FindGetter(type, memberName);
        if (getter != IntPtr.Zero)
        {
            var exception = IntPtr.Zero;
            return IL2CPP.il2cpp_runtime_invoke(getter, instance, null, ref exception);
        }

        var field = IL2CPP.il2cpp_class_get_field_from_name(type, memberName);
        return field == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_field_get_value_object(field, instance);
    }

    /// <summary>列出对象所属类的成员，用于诊断失败的查找。</summary>
    internal static string DescribeMembers(IntPtr instance)
    {
        if (instance == IntPtr.Zero) return "no instance";
        var type = IL2CPP.il2cpp_object_get_class(instance);
        var builder = new System.Text.StringBuilder(IL2CPP.il2cpp_class_get_name_(type)).Append(':');

        var iterator = IntPtr.Zero;
        while (true)
        {
            var field = IL2CPP.il2cpp_class_get_fields(type, ref iterator);
            if (field == IntPtr.Zero) break;
            builder.Append(" f:").Append(IL2CPP.il2cpp_field_get_name_(field));
        }

        iterator = IntPtr.Zero;
        while (true)
        {
            var property = IL2CPP.il2cpp_class_get_properties(type, ref iterator);
            if (property == IntPtr.Zero) break;
            builder.Append(" p:").Append(IL2CPP.il2cpp_property_get_name_(property));
        }

        return builder.ToString();
    }

    internal static IntPtr NewArray(IntPtr elementType, ulong length) =>
        elementType == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_array_new(elementType, length);

    internal static IntPtr Invoke(IntPtr method, IntPtr instance, params IntPtr[] arguments)
    {
        if (method == IntPtr.Zero)
        {
            throw new InvalidOperationException("The IL2CPP method could not be resolved.");
        }

        var buffer = stackalloc void*[arguments.Length == 0 ? 1 : arguments.Length];
        for (var index = 0; index < arguments.Length; index++) buffer[index] = (void*)arguments[index];

        var exception = IntPtr.Zero;
        var result = IL2CPP.il2cpp_runtime_invoke(method, instance, buffer, ref exception);
        if (exception != IntPtr.Zero)
        {
            var exceptionType = IL2CPP.il2cpp_class_get_name_(IL2CPP.il2cpp_object_get_class(exception));
            var message = new Il2CppSystem.Exception(exception).Message;
            throw new InvalidOperationException($"IL2CPP invocation failed with {exceptionType}: {message}");
        }

        return result;
    }

    internal static string ToManagedString(IntPtr value) =>
        value == IntPtr.Zero ? null : IL2CPP.Il2CppStringToManaged(value);
}
