using System.Text.Json;

// 为从官方 5.0 代码树迁移而来的代码提供的兼容外观，
// 由独立的 Fika.Headless 插件使用。
namespace Fika.Core.Networking.Http;

public static class FikaJson
{
    public static JsonSerializerOptions Options => Fika.Core.Networking.Serialization.FikaJson.Options;

    public static string Serialize<T>(T value) => Fika.Core.Networking.Serialization.FikaJsonCompat.Serialize(value);
    public static T Deserialize<T>(string json) => Fika.Core.Networking.Serialization.FikaJsonCompat.Deserialize<T>(json);
}
