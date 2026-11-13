using System;
using System.Linq;
using System.Reflection;
using EFT;
using Fika.Core.Networking.Serialization;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 为服务器序列化 raid 结束时的 profile 描述符。
/// <para>
/// 原始调用 —— <c>descriptor.ToUnparsedData(new Il2CppReferenceArray&lt;JsonConverter&gt;(0)).JToken.ToString()</c> ——
/// 需要 Newtonsoft.Json 的 IL2CPP interop 代理。当 BepInEx/plugins 中的
/// 一个托管 Newtonsoft.Json.dll 遮蔽了该代理时，转换器
/// 数组无法构造，JToken 成员也不再能解析，这
/// 会中止整个补丁并丢失 scav 存档。
/// </para>
/// <para>
/// 两种形式产生的描述符都用游戏自己的转换器序列化，
/// 因此被遮蔽的安装会退回到通过 IL2CPP 直接
/// 用游戏的 JsonConvert 序列化描述符 —— 与 JSON 桥
/// 已用于其他每个原生对象的是同一次调用。
/// </para>
/// </summary>
internal static class ProfileDescriptorJson
{
    /// <summary>
    /// 引擎自身的 <c>UnparsedData</c> 句柄，用于 raid 结束时的 profile 描述符 —— 即
    /// <c>SessionResult.profile</c> 和 <c>IEftSession.LocalRaidEnded</c> 接受的形式。
    ///
    /// <para>
    /// 它放在这里、紧邻 <see cref="ToServerJson"/>，这样唯一需要 IL2CPP
    /// <c>JsonConverter</c> 数组的那次调用就留在此文件中 —— 而这个文件的全部意义正是在于撑过
    /// 一个托管 Newtonsoft.Json 遮蔽 interop 代理的安装环境（见类注释）。
    /// 如果该数组无法构造，就改由引擎的序列化器生成 token，并通过原生桥交回，
    /// 两者产生相同的负载。
    /// </para>
    /// </summary>
    internal static UnparsedData ToUnparsedData(ProfileDescriptor descriptor)
    {
        if (descriptor == null) return null;

        var engine = TryEngineJson(descriptor);
        if (!string.IsNullOrEmpty(engine))
        {
            var fromEngine = FromJson(engine);
            if (fromEngine != null) return fromEngine;
        }

        if (!InteropAvailable())
        {
            var fromFallback = FromJson(NativeNewtonsoftJson.Serialize(descriptor.Cast<Il2CppSystem.Object>()));
            if (fromFallback != null) return fromFallback;
        }

        return DirectUnparsedData(descriptor);
    }

    /// <summary>
    /// 通过反射从引擎产出的 JSON 构造 <see cref="UnparsedData"/>，因此这条
    /// 路径从不链接到 Newtonsoft 代理。
    /// </summary>
    private static UnparsedData FromJson(string json)
    {
        try
        {
            var token = InvokeStatic("Newtonsoft.Json.Linq.JToken", "Parse", json);
            if (token == null) return null;

            var unparsed = new UnparsedData();
            var property = unparsed.GetType().GetProperty("JToken",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.CanWrite) return null;
            property.SetValue(unparsed, token);
            return unparsed;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"The engine profile JSON could not be turned into a descriptor ({exception.GetBaseException().Message})");
            return null;
        }
    }

    /// <summary>通过反射调用一个静态 IL2CPP 方法；无法解析时返回 null。</summary>
    private static object InvokeStatic(string typeName, string methodName, string argument)
    {
        var type = typeof(ProfileDescriptor).Assembly.GetType(typeName);
        var method = type?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static,
            null, [typeof(string)], null);
        return method?.Invoke(null, [argument]);
    }

    /// <summary>
    /// 引擎自身的转换，在 JSON 往返不可用时使用。它需要
    /// IL2CPP <c>JsonConverter</c> 数组；在托管 Newtonsoft.Json 遮蔽了该
    /// 代理的安装环境中，这次调用预期会失败，上面两条路径正是为此而存在。
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static UnparsedData DirectUnparsedData(ProfileDescriptor descriptor) =>
        descriptor.ToUnparsedData(
            new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Newtonsoft.Json.JsonConverter>(0));

    internal static string ToServerJson(ProfileDescriptor descriptor)
    {
        if (descriptor == null) return null;

        // 引擎自己的管道是服务器路由唯一接受的形式：
        // /raid/profile/scavsave 反序列化为 ScavSaveRequestData，其 Info.Side
        // 是字符串，而普通的 JsonConvert 会把该枚举写成数字。
        var engineJson = TryEngineJson(descriptor);
        if (!string.IsNullOrEmpty(engineJson)) return NormalizeForServer(engineJson);

        if (!InteropAvailable())
        {
            return NormalizeForServer(NativeNewtonsoftJson.Serialize(descriptor.Cast<Il2CppSystem.Object>()));
        }

        return NormalizeForServer(SerializeThroughInterop(descriptor));
    }

    /// <summary>
    /// SPT 会把该请求体反序列化为 PmcData，其 Info.Side 是字符串
    /// ("Usec"/"Bear"/"Savage" —— 正是它自己的 profile 文件所包含的内容)，
    /// 而引擎把该枚举写成数字。写成数字后
    /// 服务器回答 "The JSON value could not be converted to System.String.
    /// Path: $.Info.Side" 而 scav 存档就此丢失。
    /// <para>
    /// 只有顶层 Info 对象内部的 Side 会被重写；之后出现的
    /// 同名属性（背包数据）保持其数字形式。
    /// </para>
    /// </summary>
    private static string NormalizeForServer(string json) => NormalizeInfoType(NormalizePlayerSide(json));

    /// <summary>
    /// SPT 的 Tables.Info 也把 Type 声明为字符串，而引擎把
    /// 该枚举写成数字。服务器对 /raid/profile/scavsave 回答
    /// "The JSON value could not be converted to System.String. Path: $.Info.Type"
    /// 于是整份 scav 存档被丢弃。该数字属性被删除而不是被改名：SPT 自己的
    /// profile 让 Type 保持 null，
    /// 因此服务器存储的正是它自己写入的内容。
    /// </summary>
    private static string NormalizeInfoType(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;

        const string infoMarker = "\"Info\":{";
        const string typeMarker = "\"Type\":";

        var info = json.IndexOf(infoMarker, StringComparison.Ordinal);
        if (info < 0) return json;

        // Info 会先写它的标量，再写任何嵌套对象，所以 Info 之后的第一个
        // Type 标记属于它。无论如何都要给搜索加上限界。
        var type = json.IndexOf(typeMarker, info + infoMarker.Length, StringComparison.Ordinal);
        if (type < 0 || type - info > 2000) return json;

        var valueStart = type + typeMarker.Length;
        var valueEnd = valueStart;
        while (valueEnd < json.Length && (char.IsDigit(json[valueEnd]) || json[valueEnd] == '-')) valueEnd++;
        if (valueEnd == valueStart) return json; // 已经是字符串或 null，不做改动

        var removeEnd = valueEnd;
        if (removeEnd < json.Length && json[removeEnd] == ',')
        {
            removeEnd++;
        }
        else
        {
            var removeStart = type;
            while (removeStart > 0 && char.IsWhiteSpace(json[removeStart - 1])) removeStart--;
            if (removeStart == 0 || json[removeStart - 1] != ',') return json;
            type = removeStart - 1;
        }

        FikaGlobals.LogInfo($"Dropped the numeric Info.Type '{json[valueStart..valueEnd]}' from the scav save payload");
        return json[..type] + json[removeEnd..];
    }

    private static string NormalizePlayerSide(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;

        const string infoMarker = "\"Info\":{";
        const string sideMarker = "\"Side\":";

        var info = json.IndexOf(infoMarker, StringComparison.Ordinal);
        if (info < 0) return json;

        var side = json.IndexOf(sideMarker, info + infoMarker.Length, StringComparison.Ordinal);
        if (side < 0) return json;

        var valueStart = side + sideMarker.Length;
        var valueEnd = valueStart;
        while (valueEnd < json.Length && (char.IsDigit(json[valueEnd]) || json[valueEnd] == '-')) valueEnd++;
        if (valueEnd == valueStart) return json;

        var value = json.Substring(valueStart, valueEnd - valueStart);
        var name = value switch
        {
            "1" => "Usec",
            "2" => "Bear",
            "4" => "Savage",
            _ => null
        };

        if (name == null)
        {
            FikaGlobals.LogWarning($"Unknown EPlayerSide value '{value}' in the profile payload, sending it unchanged");
            return json;
        }

        return json[..valueStart] + "\"" + name + "\"" + json[valueEnd..];
    }

    /// <summary>
    /// 运行引擎的 <c>JsonExtensions.ToUnparsedData&lt;T&gt;(T, JsonConverter[] = null)</c>
    /// 并返回它的 JToken 文本。该方法是泛型的，且接收一个转换器
    /// 数组，而在托管 Newtonsoft.Json 遮蔽 IL2CPP 代理时
    /// 该数组的类型无法被写出来，因此通过反射触达它，
    /// 并以原生方式读取 JToken。
    /// </summary>
    private static string TryEngineJson(ProfileDescriptor descriptor)
    {
        try
        {
            var extensions = typeof(ProfileDescriptor).Assembly.GetType("JsonExtensions");
            var method = extensions?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(x => x.Name == "ToUnparsedData" && x.IsGenericMethodDefinition);
            if (method == null)
            {
                FikaGlobals.LogWarning("JsonExtensions.ToUnparsedData was not found, serializing the profile directly");
                return null;
            }

            var unparsed = method.MakeGenericMethod(typeof(ProfileDescriptor))
                .Invoke(null, new object[] { descriptor, null });
            if (unparsed is not Il2CppObjectBase native) return null;

            var token = NativeIl2Cpp.ReadMember(native.Pointer, "JToken");
            if (token == IntPtr.Zero)
            {
                FikaGlobals.LogWarning("The engine profile serialization produced no JToken, serializing the profile directly");
                return null;
            }

            var jToken = NativeIl2Cpp.FindClass("Newtonsoft.Json", "Newtonsoft.Json.Linq", "JToken");
            return NativeIl2Cpp.ToManagedString(
                NativeIl2Cpp.Invoke(NativeIl2Cpp.FindMethod(jToken, "ToString", 0), token));
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"The engine profile serialization failed ({exception.GetBaseException().Message}), " +
                "falling back to a direct serialization");
            return null;
        }
    }

    /// <summary>
    /// 当 Newtonsoft.Json 仍解析到 IL2CPP interop 代理时为 true。
    /// 遮蔽用的托管程序集不是 Il2CppObjectBase。
    /// </summary>
    internal static bool InteropAvailable()
    {
        try
        {
            return typeof(Il2CppObjectBase).IsAssignableFrom(typeof(Newtonsoft.Json.JsonConverter));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 保持在独立的、不被内联的方法中：在有遮蔽程序集的情况下，链接
    /// 失败会在编译此方法时浮现，而调用方中的 try/catch
    /// 否则就无法观察到它。
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string SerializeThroughInterop(ProfileDescriptor descriptor) =>
        descriptor.ToUnparsedData(
            new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Newtonsoft.Json.JsonConverter>(0))
            .JToken.ToString();
}
