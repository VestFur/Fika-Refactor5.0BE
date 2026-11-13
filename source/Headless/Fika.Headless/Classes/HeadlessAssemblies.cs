using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Serialization;

namespace Fika.Headless.Classes;

/// <summary>
/// 报告本次安装中可能被插件副本遮蔽的程序集，使一类
/// 「主机卡住而不是创建战局」的故障
/// 可以从启动路径直接看到，而不必看最先挂掉的是哪一行。
/// <para>
/// BepInEx 按简单程序集名解析插件的引用。当同一个名字
/// 也出现在 BepInEx/plugins 下时，插件副本可能压过
/// 本移植版本编译时针对的 BepInEx/interop 代理；此后每个
/// 需要该代理某个 IL2CPP 重载的调用点都会以
/// <see cref="MissingMethodException"/> 失败——该异常是在编译*调用*方法时抛出的。
/// 无头模式启动路径就曾通过
/// <c>Newtonsoft.Json</c> 遇到过这个问题。
/// </para>
/// <para>
/// 只检查 Fika.Headless 与 Fika.Core 实际引用的程序集，
/// 因此携带自有 JSON 库的无关插件不会被报告。
/// 扫描只读取程序集元数据——不加载任何东西。
/// </para>
/// </summary>
internal static class HeadlessAssemblies
{
    /// <summary>
    /// 提供 Fika 编译时所用程序集的文件夹。同名程序集
    /// 也存在于 <c>plugins</c> 下即为遮蔽情形。
    /// </summary>
    private static readonly string[] ProxyFolders = ["core", "interop"];

    /// <summary>
    /// 每个已武装的主机记录一次完整清单。永不抛异常：调用方
    /// 已包裹了它，但诊断代码不得有能力中断载荷。
    /// </summary>
    internal static void Report(ManualLogSource log)
    {
        var root = BepInExRoot();
        if (string.IsNullOrEmpty(root))
        {
            log.LogWarning("[Headless] Assemblies: the BepInEx root could not be derived from the plugin location, " +
                "only the Newtonsoft.Json resolution is reported");
            ReportNewtonsoft(log);
            return;
        }

        var plugins = ScanManagedAssemblies(Path.Combine(root, "plugins"));
        var referenced = ReferencedAssemblyNames();

        log.LogInfo($"[Headless] Assemblies: {plugins.Count} managed assemblies under BepInEx/plugins, " +
            $"{referenced.Count} referenced by Fika.Headless and Fika.Core");

        var shadowed = 0;
        foreach (var name in referenced.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!plugins.TryGetValue(name, out var pluginFile))
            {
                continue;
            }

            foreach (var folder in ProxyFolders)
            {
                if (!File.Exists(Path.Combine(root, folder, name + ".dll")))
                {
                    continue;
                }

                shadowed++;
                log.LogError($"[Headless] Assemblies: '{name}' exists as both {folder}/{name}.dll and plugins/{pluginFile}; " +
                    "the plugins copy can shadow the IL2CPP proxy and make IL2CPP overloads throw MissingMethodException. " +
                    "Keep exactly one copy - remove the plugins copy.");
                break;
            }
        }

        if (shadowed == 0)
        {
            log.LogInfo($"[Headless] Assemblies: none of the {referenced.Count} referenced assemblies is shadowed by a " +
                "BepInEx/plugins copy");
        }

        ReportNewtonsoft(log);
    }

    /// <summary>
    /// 仅告警的形式，用于刚因被遮蔽的查找而失败的启动路径。
    /// 每个进程只报告一次。
    /// </summary>
    internal static void ReportIfShadowed(ManualLogSource log)
    {
        if (ShadowedAssemblyReporter.NewtonsoftInteropAvailable)
        {
            return;
        }

        ShadowedAssemblyReporter.ReportIfShadowed("[Headless]");
        log.LogError($"[Headless] Assemblies: the IL2CPP overloads of Newtonsoft.Json are NOT available " +
            $"({ShadowedAssemblyReporter.NewtonsoftDescription}). Fika itself routes native values through the game's own " +
            "JsonConvert via IL2CPP and keeps working, but any other plugin that calls the IL2CPP overloads directly " +
            "throws MissingMethodException. Remove plugins/Newtonsoft.Json.dll to restore them.");
    }

    private static void ReportNewtonsoft(ManualLogSource log)
    {
        log.LogInfo($"[Headless] Assemblies: {ShadowedAssemblyReporter.NewtonsoftDescription}");
        ReportIfShadowed(log);
    }

    /// <summary>
    /// 本插件被加载时所在的 BepInEx 根目录，由插件自身位置推导
    /// （…/BepInEx/plugins/fika/Fika.Headless.dll）。采用推导而非
    /// 读取 BepInEx API，因此不存储安装路径，也不受 API 版本
    /// 影响。
    /// </summary>
    private static string BepInExRoot()
    {
        try
        {
            var folder = Path.GetDirectoryName(typeof(FikaHeadlessPlugin).Assembly.Location);
            while (!string.IsNullOrEmpty(folder))
            {
                if (string.Equals(Path.GetFileName(folder), "plugins", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetDirectoryName(folder);
                }

                folder = Path.GetDirectoryName(folder);
            }
        }
        catch (Exception)
        {
            // 落到 null：调用方会退化为 Newtonsoft 报告。
        }

        return null;
    }

    /// <summary>本进程在运行时解析的程序集名称。</summary>
    private static List<string> ReferencedAssemblyNames()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        Assembly[] assemblies = [typeof(FikaHeadlessPlugin).Assembly, typeof(FikaBackendUtils).Assembly];

        foreach (var assembly in assemblies)
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (!string.IsNullOrEmpty(reference.Name))
                {
                    names.Add(reference.Name);
                }
            }
        }

        return [.. names];
    }

    /// <summary>
    /// 将程序集简单名映射为它相对 <paramref name="folder"/> 的路径。
    /// 非托管程序集的文件会被跳过。
    /// </summary>
    private static Dictionary<string, string> ScanManagedAssemblies(string folder)
    {
        Dictionary<string, string> found = new(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder))
        {
            return found;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories))
        {
            string name;
            try
            {
                name = AssemblyName.GetAssemblyName(file).Name;
            }
            catch (Exception)
            {
                // 原生 DLL 或不可读的文件：不是能造成遮蔽的程序集。
                continue;
            }

            if (string.IsNullOrEmpty(name) || found.ContainsKey(name))
            {
                continue;
            }

            found[name] = Path.GetRelativePath(folder, file).Replace('\\', '/');
        }

        return found;
    }
}
