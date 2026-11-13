using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using EFT;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Fika.Core.Main.Utils;

namespace Fika.Core.Networking.Serialization;

/// <summary>在 JSON 边界上把托管 Fika DTO 与原生游戏对象区分开。</summary>
internal static class FikaJson
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = ManagedJson.CreateOptions();
        options.Converters.Insert(0, new MongoIdConverter());
        options.Converters.Insert(1, new NativeObjectConverterFactory());
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    private sealed class MongoIdConverter : JsonConverter<MongoID>
    {
        public override MongoID Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var thread = new NativeThreadScope();
            return new(reader.GetString());
        }
        public override void Write(Utf8JsonWriter writer, MongoID value, JsonSerializerOptions options)
        {
            using var thread = new NativeThreadScope();
            writer.WriteStringValue(value.ToString());
        }
    }

    private sealed class NativeObjectConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => typeof(Il2CppObjectBase).IsAssignableFrom(type);
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(NativeObjectConverter<>).MakeGenericType(type));
    }

    private sealed class NativeObjectConverter<T> : JsonConverter<T> where T : Il2CppObjectBase
    {
        // 一旦检测到遮蔽 IL2CPP 代理的托管 Newtonsoft.Json
        // 就设置该标志；之后每次调用都直接走 IL2CPP。
        private static bool _throughIl2Cpp;

        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var thread = new NativeThreadScope();
            using var document = JsonDocument.ParseValue(ref reader);
            var json = document.RootElement.GetRawText();

            if (_throughIl2Cpp)
            {
                var bridged = NativeNewtonsoftJson.Deserialize(json, type);
                return bridged == null ? null : bridged.Cast<T>();
            }

            try
            {
                return DeserializeThroughInterop(json, type);
            }
            catch (Exception exception) when (IsShadowedInterop(exception))
            {
                _throughIl2Cpp = true;
                ReportShadowed(exception);
                var bridged = NativeNewtonsoftJson.Deserialize(json, type);
                return bridged == null ? null : bridged.Cast<T>();
            }
        }

        /// <summary>
        /// 单独放在不内联的方法里：针对遮蔽 IL2CPP 的
        /// Newtonsoft.Json 的链接失败会在编译该方法时暴露，而同一方法内的
        /// try/catch 永远无法观察到它。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T DeserializeThroughInterop(string json, Type type)
        {
            // 使用非泛型的原生重载。托管的泛型 DTO 在游戏中没有
            // AOT 实例化，绝不能走到 JsonConvert<T>。
            var native = Newtonsoft.Json.JsonConvert.DeserializeObject(json, Il2CppType.From(type));
            return native == null ? null : native.Cast<T>();
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            using var thread = new NativeThreadScope();
            string json;

            if (_throughIl2Cpp)
            {
                json = NativeNewtonsoftJson.Serialize(value.Cast<Il2CppSystem.Object>());
            }
            else
            {
                try
                {
                    json = SerializeThroughInterop(value);
                }
                catch (Exception exception) when (IsShadowedInterop(exception))
                {
                    _throughIl2Cpp = true;
                    ReportShadowed(exception);
                    json = NativeNewtonsoftJson.Serialize(value.Cast<Il2CppSystem.Object>());
                }
            }

            using var document = JsonDocument.Parse(json);
            document.RootElement.WriteTo(writer);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string SerializeThroughInterop(T value) =>
            Newtonsoft.Json.JsonConvert.SerializeObject(value.Cast<Il2CppSystem.Object>());

        /// <summary>
        /// BepInEx/plugins 中的托管 Newtonsoft.Json 会在程序集解析中胜过
        /// IL2CPP interop 代理，并且缺少 IL2CPP 重载。两个
        /// 辅助方法都位于 <see cref="ShadowedAssemblyReporter"/>，它还
        /// 会指出胜出的程序集文件，并兼作启动路径在依赖这些
        /// 重载之前可以运行的预检。
        /// </summary>
        private static bool IsShadowedInterop(Exception exception) =>
            ShadowedAssemblyReporter.IsShadowedInterop(exception);

        private static void ReportShadowed(Exception exception) =>
            ShadowedAssemblyReporter.ReportShadowed(exception);
    }
}

internal static class FikaJsonCompat
{
    public static string Serialize<T>(T value) => FikaJson.Serialize(value);
    public static T Deserialize<T>(string json) => FikaJson.Deserialize<T>(json);
}
