using System;
using System.Globalization;
using BepInEx.Configuration;

namespace Fika.Core.Main.Utils;

/// <summary>面向 IL2CPP BepInEx 运行时的 Unity 颜色配置支持。</summary>
internal static class ColorConfigConverter
{
    internal static void Register()
    {
        // 尊重其他插件提供的转换器，包括它们所用的格式。
        if (TomlTypeConverter.CanConvert(typeof(Color))) return;
        TomlTypeConverter.AddConverter(typeof(Color), new TypeConverter
        {
            ConvertToString = (value, _) => Format((Color)value),
            ConvertToObject = (value, _) => Parse(value)
        });
    }

    private static string Format(Color color)
    {
        // BepInEx 的 Mono 颜色格式是 RRGGBBAA，不带前导 '#'。
        static string Channel(float value)
        {
            if (!float.IsFinite(value)) throw new FormatException("Color channels must be finite.");
            return ((byte)Math.Round(Math.Clamp(value, 0f, 1f) * 255f))
                .ToString("X2", CultureInfo.InvariantCulture);
        }
        return Channel(color.r) + Channel(color.g) + Channel(color.b) + Channel(color.a);
    }

    private static Color Parse(string value)
    {
        var text = value?.Trim() ?? "";
        var hex = text.TrimStart('#');
        if ((hex.Length == 6 || hex.Length == 8) &&
            uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgba))
        {
            if (hex.Length == 6) rgba = (rgba << 8) | 255;
            return new Color
            {
                r = ((rgba >> 24) & 255) / 255f, g = ((rgba >> 16) & 255) / 255f,
                b = ((rgba >> 8) & 255) / 255f, a = (rgba & 255) / 255f
            };
        }

        // 同时接受旧版配置工具写出的浮点元组。
        var parts = text.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 3 or 4)
        {
            var channels = new float[] { 0, 0, 0, 1 };
            for (var i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out channels[i]) ||
                    !float.IsFinite(channels[i]))
                    throw new FormatException($"Invalid color channel in '{value}'.");
            }
            return new Color { r = channels[0], g = channels[1], b = channels[2], a = channels[3] };
        }
        throw new FormatException($"Invalid color '{value}'; expected RRGGBB, RRGGBBAA or an RGB(A) float tuple.");
    }
}
