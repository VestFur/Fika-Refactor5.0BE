using System;
using System.Collections.Generic;
using System.Reflection;
using EFT.UI.Matchmaker;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 游戏本身在某些地图上钉死的时钟时间（"这张地图始终运行在 HH:mm"）。
///
/// 原版数值所在的位置：匹配器自己的条件面板，
/// <see cref="EFT.UI.Matchmaker.LocationConditionsPanel"/>，每张这样的地图都带有一个静态 DateTime 属性，
/// 已安装的 5.0 构建中有 FactoryDayTime、FactoryNightTime 和 IcebreakerFixedTime
/// （interop Assembly-CSharp.dll，类型 EFT.UI.Matchmaker.LocationConditionsPanel）。该面板把这个值
/// 送入战局时间选择器，这就是为什么正式游戏中玩家在这些地图上得不到可用的
/// 时间选项。战局时钟本身只会被告知一个整数小时
/// （EFT.TimeAndWeatherSettings.HourOfDay），因此游戏所消费的全部内容就是这个小时数。
///
/// 为什么需要这个类：Fika 的战局从不运行那个面板，因此没有任何东西把钉死的时间带进
/// 战局，而战局会以战局设置中持有的任意值启动——在本移植版上这通常是
/// Fika 战局设置界面留下的 -1（"跟随世界时钟"）。本类按名字读取
/// 面板本会使用的同一个原生数值，因此游戏以后钉死的地图无需在此改动代码
/// 即可工作。证据与判定依据见 analysis/map-fixed-time/REPORT.md。
/// </summary>
internal static class MapFixedTime
{
    /// <summary>
    /// 游戏给钉死的地图时间起的名字：地点 id 后接 "FixedTime"
    /// （地点 id 为 "Icebreaker" 时即 IcebreakerFixedTime）。查找依据这个名字，而不再依据
    /// 逐地图的列表，因此这里没有硬编码任何地图。
    /// </summary>
    private const string NativeSuffix = "FixedTime";

    /// <summary>
    /// 为无法从原生面板读到钉死时间的地图提供的显式回退（UI
    /// 类型缺失，或属性被游戏更新重命名/移除）。
    ///
    /// 故意保持为空。该数值只存在于一个地方——
    /// GameAssembly.dll 中那个原生 getter 的函数体——而它没有可用的反编译（4.0 代码树止于该面板之前，
    /// 5.0 未提供），因此写在这里的任何数字都只能是猜测。猜出来的时间比
    /// 现状更糟：战局会在一个凭空编造的小时上静默启动。在表为空期间
    /// 不会有任何损失——"no pinned time found" 表示 CoopGame 保留玩家自己的设置，这
    /// 正是本类存在之前的行为。
    ///
    /// 要填充它，取该地图上诊断行打印出的小时数
    /// （"Raid time: ... fixed=HH:mm (native ...)"），然后添加一个条目，例如：
    ///     { "Icebreaker", (19, 0) },
    /// </summary>
    private static readonly Dictionary<string, (int Hour, int Minute)> Fallback =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly struct Entry
    {
        internal readonly bool Found;
        internal readonly int Hour;
        internal readonly int Minute;
        internal readonly string Source;

        internal Entry(bool found, int hour, int minute, string source)
        {
            Found = found;
            Hour = hour;
            Minute = minute;
            Source = source;
        }
    }

    /// <summary>已解析的查找结果：原生读取每地图每进程只运行一次，未命中同样缓存。</summary>
    private static readonly Dictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>缓存由创建战局的调用方写入；读取本身不是线程安全的。</summary>
    private static readonly object Gate = new();

    /// <summary>
    /// <paramref name="locationId"/> 的钉死时间；当地图没有该时间（或本
    /// 构建读不到它）时返回 false。永不抛出异常：战局启动不应因一次诊断性查找而失败。
    /// </summary>
    /// <param name="source">数值的来源，用于诊断行："native &lt;name&gt;"、"table" 或 "none"。</param>
    internal static bool TryGet(string locationId, out int hour, out int minute, out string source)
    {
        hour = 0;
        minute = 0;
        source = "none";
        if (string.IsNullOrEmpty(locationId))
        {
            return false;
        }

        Entry entry;
        lock (Gate)
        {
            if (!Cache.TryGetValue(locationId, out entry))
            {
                entry = Resolve(locationId);
                Cache[locationId] = entry;
            }
        }

        hour = entry.Hour;
        minute = entry.Minute;
        source = entry.Source;
        return entry.Found;
    }

    private static Entry Resolve(string locationId)
    {
        if (TryResolveNative(locationId, out var hour, out var minute, out var nativeSource))
        {
            return new Entry(true, hour, minute, nativeSource);
        }

        if (Fallback.TryGetValue(locationId, out var table))
        {
            return new Entry(true, table.Hour, table.Minute, "table");
        }

        return new Entry(false, 0, 0, "none");
    }

    private static bool TryResolveNative(string locationId, out int hour, out int minute, out string source)
    {
        hour = 0;
        minute = 0;
        source = "none";
        var name = locationId + NativeSuffix;
        try
        {
            var panel = typeof(LocationConditionsPanel);

            // 这里的名称比较故意不区分大小写：战局报告的地点 id
            // （"Icebreaker"）与面板使用的名称（"IcebreakerFixedTime"）来自两个不同的
            // 地方，它们的大小写只需在地图身份上一致，而不必在拼写上一致。
            object value = null;
            var found = false;
            foreach (var property in panel.GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                value = property.GetValue(null);
                found = true;
                break;
            }

            if (!found)
            {
                foreach (var candidate in panel.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!string.Equals(candidate.Name, "get_" + name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    value = candidate.Invoke(null, null);
                    found = true;
                    break;
                }
            }

            if (!found || value is not Il2CppSystem.DateTime pinned)
            {
                return false;
            }

            hour = pinned.Hour;
            minute = pinned.Minute;
            source = "native " + name;
            return true;
        }
        catch (Exception exception)
        {
            
            return false;
        }
    }
}
