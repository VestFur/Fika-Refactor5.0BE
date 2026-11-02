using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Networking.Serialization;

/// <summary>
/// 通过 IL2CPP 运行时抵达游戏自身的 Newtonsoft.Json，而不是
/// 通过托管互操作代理。
/// <para>
/// Fika 是针对 IL2CPP 代理编译的，其签名使用 IL2CPP 类型
/// （例如 <c>DeserializeObject(String, Il2CppSystem.Type)</c>）。某个安装若同时
/// 把托管的 <c>Newtonsoft.Json.dll</c> 放在 BepInEx/plugins 中，就会按程序集名
/// 遮蔽那个代理，那些重载便不再能解析，插件在加载时
/// 就以 MissingMethodException 死掉。通过 IL2CPP 解析
/// <c>JsonConvert</c> 则无法被托管程序集遮蔽。
/// </para>
/// </summary>
internal static unsafe class NativeNewtonsoftJson
{
    private const string AssemblyName = "Newtonsoft.Json";
    private const string Namespace = "Newtonsoft.Json";
    private const string ClassName = "JsonConvert";

    private static bool _probed;
    private static string _unavailable;
    private static IntPtr _deserializeObject;
    private static IntPtr _serializeObject;

    internal static string UnavailableReason => _unavailable;

    internal static bool IsAvailable { get { Probe(); return _deserializeObject != IntPtr.Zero; } }

    internal static Il2CppSystem.Object Deserialize(string json, Type type)
    {
        EnsureResolved(nameof(Deserialize));
        var arguments = stackalloc void*[2];
        arguments[0] = (void*)IL2CPP.il2cpp_string_new(json);
        arguments[1] = (void*)Il2CppType.From(type).Pointer;
        return Wrap(Invoke(_deserializeObject, arguments));
    }

    internal static string Serialize(Il2CppSystem.Object value)
    {
        EnsureResolved(nameof(Serialize));
        var arguments = stackalloc void*[1];
        arguments[0] = (void*)IL2CPP.Il2CppObjectBaseToPtr(value);
        return IL2CPP.Il2CppStringToManaged(Invoke(_serializeObject, arguments));
    }

    private static IntPtr Invoke(IntPtr method, void** arguments)
    {
        var exception = IntPtr.Zero;
        var result = IL2CPP.il2cpp_runtime_invoke(method, IntPtr.Zero, arguments, ref exception);
        if (exception != IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"The game's JsonConvert threw: {new Il2CppSystem.Exception(exception)}");
        }

        return result;
    }

    private static Il2CppSystem.Object Wrap(IntPtr pointer) =>
        pointer == IntPtr.Zero ? null : new Il2CppSystem.Object(pointer);

    private static void EnsureResolved(string operation)
    {
        Probe();
        if (_deserializeObject == IntPtr.Zero || _serializeObject == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Cannot {operation} through the game's JsonConvert: {_unavailable}");
        }
    }

    private static void Probe()
    {
        if (_probed) return;
        _probed = true;

        try
        {
            var domain = IL2CPP.il2cpp_domain_get();
            var assembly = OpenAssembly(domain, AssemblyName);
            if (assembly == IntPtr.Zero)
            {
                _unavailable = $"the IL2CPP assembly '{AssemblyName}' was not found";
                return;
            }

            var image = IL2CPP.il2cpp_assembly_get_image(assembly);
            var type = IL2CPP.il2cpp_class_from_name(image, Namespace, ClassName);
            if (type == IntPtr.Zero)
            {
                _unavailable = $"the IL2CPP class '{Namespace}.{ClassName}' was not found";
                return;
            }

            IL2CPP.il2cpp_runtime_class_init(type);
            // DeserializeObject(String, Type)——而不是 JsonSerializerSettings 重载。
            _deserializeObject = FindMethod(type, "DeserializeObject", 2, "System.Type");
            // SerializeObject(Object)
            _serializeObject = FindMethod(type, "SerializeObject", 1, "System.Object");
            if (_deserializeObject == IntPtr.Zero || _serializeObject == IntPtr.Zero)
            {
                _unavailable = "the JsonConvert methods used by Fika were not found";
            }
        }
        catch (Exception exception)
        {
            _unavailable = exception.Message;
        }
    }

    private static IntPtr OpenAssembly(IntPtr domain, string name)
    {
        var native = Marshal.StringToHGlobalAnsi(name);
        try
        {
            return IL2CPP.il2cpp_domain_assembly_open(domain, native);
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    private static IntPtr FindMethod(IntPtr type, string name, uint parameterCount, string lastParameterType)
    {
        var iterator = IntPtr.Zero;
        while (true)
        {
            var method = IL2CPP.il2cpp_class_get_methods(type, ref iterator);
            if (method == IntPtr.Zero) return IntPtr.Zero;
            if (IL2CPP.il2cpp_method_get_name_(method) != name) continue;
            if (IL2CPP.il2cpp_method_get_param_count(method) != parameterCount) continue;

            var parameter = IL2CPP.il2cpp_method_get_param(method, parameterCount - 1);
            if (lastParameterType != null && ParameterTypeName(parameter) != lastParameterType) continue;

            return method;
        }
    }

    private static string ParameterTypeName(IntPtr parameter)
    {
        if (parameter == IntPtr.Zero) return null;
        var handle = IL2CPP.il2cpp_type_get_object(parameter);
        return handle == IntPtr.Zero ? null : new Il2CppSystem.Type(handle).FullName;
    }
}
