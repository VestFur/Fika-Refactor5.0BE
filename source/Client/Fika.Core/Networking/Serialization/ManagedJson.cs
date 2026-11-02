using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fika.Core.Networking.Serialization;

/// <summary>托管 DTO 序列化，沿用 Fika 既有的 DataContract 线缆名称。</summary>
internal static class ManagedJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new DataContractConverterFactory());
        options.Converters.Add(new NumericEnumConverterFactory());
        return options;
    }

    private sealed class DataContractConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsDefined(typeof(DataContractAttribute), false);

        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(DataContractConverter<>).MakeGenericType(type));
    }

    private sealed class DataContractConverter<T> : JsonConverter<T>
    {
        private static readonly ContractMember[] Members = typeof(T)
            .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(member => member is FieldInfo or PropertyInfo && member.IsDefined(typeof(DataMemberAttribute), true))
            .Select(member => new ContractMember(member)).ToArray();

        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException($"Expected object for {type}.");
            object instance = Activator.CreateInstance(type);
            var seen = new bool[Members.Length];
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // Newtonsoft 的契约解析器先按精确名称匹配，
                // 再回退到不区分大小写的匹配。保留这一行为。
                var index = Array.FindIndex(Members, m => m.Name == property.Name);
                if (index < 0 && options.PropertyNameCaseInsensitive)
                    index = Array.FindIndex(Members, m => string.Equals(m.Name, property.Name, StringComparison.OrdinalIgnoreCase));
                if (index < 0) continue;
                var member = Members[index];
                member.Set(instance, JsonSerializer.Deserialize(property.Value.GetRawText(), member.Type, options));
                seen[index] = true;
            }
            for (var i = 0; i < Members.Length; i++)
                if (Members[i].Required && !seen[i]) throw new JsonException($"Missing required member '{Members[i].Name}'.");
            return (T)instance;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var member in Members)
            {
                var memberValue = member.Get(value);
                if (!member.EmitDefault && Equals(memberValue, member.Type.IsValueType ? Activator.CreateInstance(member.Type) : null)) continue;
                writer.WritePropertyName(member.Name);
                JsonSerializer.Serialize(writer, memberValue, member.Type, options);
            }
            writer.WriteEndObject();
        }
    }

    private sealed class ContractMember
    {
        private readonly MemberInfo _member;
        public readonly string Name;
        public readonly Type Type;
        public readonly bool Required;
        public readonly bool EmitDefault;

        public ContractMember(MemberInfo member)
        {
            _member = member;
            var attribute = member.GetCustomAttribute<DataMemberAttribute>();
            Name = attribute.Name ?? member.Name;
            Type = member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;
            Required = attribute.IsRequired;
            EmitDefault = attribute.EmitDefaultValue;
        }

        public object Get(object instance) => _member is FieldInfo field ? field.GetValue(instance) : ((PropertyInfo)_member).GetValue(instance);
        public void Set(object instance, object value)
        {
            if (_member is FieldInfo field) field.SetValue(instance, value);
            else ((PropertyInfo)_member).SetValue(instance, value);
        }
    }

    // Newtonsoft 既接受名称也接受数字，但在没有
    // StringEnumConverter 的情况下写出的是数字。JsonStringEnumConverter 会改变 Fika 的协议。
    private sealed class NumericEnumConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsEnum;
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(NumericEnumConverter<>).MakeGenericType(type));
    }

    private sealed class NumericEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        private static readonly Type UnderlyingType = Enum.GetUnderlyingType(typeof(T));
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                if (Enum.TryParse<T>(reader.GetString(), true, out var value)) return value;
                throw new JsonException($"Invalid {type.Name} value.");
            }
            return (T)Enum.ToObject(type, JsonSerializer.Deserialize(ref reader, UnderlyingType, options));
        }
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, Convert.ChangeType(value, UnderlyingType), UnderlyingType, options);
    }
}
