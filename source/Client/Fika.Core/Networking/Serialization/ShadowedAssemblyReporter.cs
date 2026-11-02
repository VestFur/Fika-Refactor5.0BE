using System;
using System.IO;
using System.Threading;
using Il2CppInterop.Runtime.InteropTypes;
using Fika.Core.Main.Utils;

namespace Fika.Core.Networking.Serialization;

/// <summary>
/// 识别并命名那些按程序集名称遮蔽了 IL2CPP
/// interop 代理的托管插件程序集。
/// <para>
/// BepInEx 按简单程序集名称解析插件的引用，因此一个被放入
/// BepInEx/plugins 的托管 <c>Newtonsoft.Json.dll</c> 会胜过 Fika 编译时所
/// 针对的 <c>BepInEx/interop</c> 代理。此后每个需要用到该代理某个
/// IL2CPP 重载的调用点都会因
/// <see cref="MissingMethodException"/> 而失败，<b>而且是在该调用点自身的方法
/// 被编译时</b>。因此失败是在调用方的帧中抛出的，在
/// 失败方法的第一条语句执行之前，这就是为什么该方法内部的 try/catch
/// 永远无法观察到它——外围方法必须处于
/// 另一个帧中。
/// </para>
/// <para>
/// 这个类是检测该状况的唯一场所，报告它时带上
/// 实际赢得名称解析的那个文件，并且可以从
/// 预检中安全调用，且是在任何东西依赖这些重载之前。
/// </para>
/// </summary>
public static class ShadowedAssemblyReporter
{
    /// <summary>Fika 所依赖其 IL2CPP 重载的程序集的简单名称。</summary>
    public const string NewtonsoftJsonAssembly = "Newtonsoft.Json";

    private static readonly object Gate = new();

    private static bool _probed;
    private static bool _newtonsoftInteropAvailable;
    private static string _newtonsoftDescription;
    private static int _reportedIfShadowed;

    /// <summary>
    /// 当 <c>Newtonsoft.Json</c> 仍解析到 IL2CPP interop
    /// 代理时为 true。该探测只询问哪个程序集胜出；它从不调用它。
    /// </summary>
    public static bool NewtonsoftInteropAvailable
    {
        get
        {
            Probe();
            return _newtonsoftInteropAvailable;
        }
    }

    /// <summary>
    /// 命名赢得 <c>Newtonsoft.Json</c> 名称解析的那个程序集文件，
    /// 形式为 <c>folder/file (state)</c>，以便日志行永远不会泄漏安装路径。
    /// </summary>
    public static string NewtonsoftDescription
    {
        get
        {
            Probe();
            return _newtonsoftDescription;
        }
    }

    /// <summary>
    /// 标准的、既有的遮蔽报告。保留在此方法名下，以便
    /// <see cref="FikaGlobals"/> 仍能给该行打上 <c>[ReportShadowed]</c> 标记。
    /// </summary>
    public static void ReportShadowed(Exception exception)
    {
        FikaGlobals.LogWarning($"Newtonsoft.Json resolved to an assembly without the IL2CPP overloads " +
            $"({exception.GetType().Name}: {exception.Message}); using the game's JsonConvert through IL2CPP instead. " +
            $"{NewtonsoftDescription}");
    }

    /// <summary>
    /// 当 IL2CPP 重载不可用时，每个进程记录一次，以便
    /// 该状况从启动路径即可见，而不只是从恰好第一个
    /// 失败的调用处可见。传入调用方的日志前缀
    /// （例如 <c>"[Headless]"</c>），以便该行可以用 grep 找到。
    /// </summary>
    public static void ReportIfShadowed(string source)
    {
        Probe();
        if (_newtonsoftInteropAvailable)
        {
            return;
        }

        if (Interlocked.Exchange(ref _reportedIfShadowed, 1) != 0)
        {
            return;
        }

        FikaGlobals.LogWarning($"{source} assemblies: Newtonsoft.Json resolves to an assembly without the IL2CPP overloads " +
            $"({_newtonsoftDescription}). Fika reaches the game's own JsonConvert through IL2CPP instead, but any other " +
            "plugin that calls the IL2CPP overloads directly will fail with MissingMethodException; keep exactly one copy " +
            "of Newtonsoft.Json.dll in the install.");
    }

    /// <summary>
    /// 对于被遮蔽的 interop 代理所产生的异常返回 true。调用方把它用作
    /// 包裹代理调用的 <c>when</c> 过滤器。
    /// </summary>
    public static bool IsShadowedInterop(Exception exception) =>
        exception is MissingMethodException or MissingFieldException or TypeLoadException
            or FileLoadException or FileNotFoundException or BadImageFormatException
        || exception.InnerException is MissingMethodException or TypeLoadException;

    private static void Probe()
    {
        lock (Gate)
        {
            if (_probed)
            {
                return;
            }

            _probed = true;

            try
            {
                // typeof() 报告哪个程序集赢得了名称解析，且从不
                // 在其中执行代码。进行遮蔽的托管程序集不是
                // Il2CppObjectBase，这正是该检查得以成立的原因。
                _newtonsoftInteropAvailable =
                    typeof(Il2CppObjectBase).IsAssignableFrom(typeof(Newtonsoft.Json.JsonConverter));
            }
            catch (Exception exception)
            {
                _newtonsoftInteropAvailable = false;
                _newtonsoftDescription =
                    $"Newtonsoft.Json -> could not be probed ({exception.GetType().Name}: {exception.Message})";
                return;
            }

            _newtonsoftDescription = DescribeNewtonsoft(_newtonsoftInteropAvailable);
        }
    }

    private static string DescribeNewtonsoft(bool interopAvailable)
    {
        try
        {
            var assembly = typeof(Newtonsoft.Json.JsonConverter).Assembly;
            var location = assembly.Location;
            if (string.IsNullOrEmpty(location))
            {
                return $"Newtonsoft.Json -> {assembly.GetName().Name}.dll " +
                    $"({(interopAvailable ? "IL2CPP interop proxy" : "managed assembly without the IL2CPP overloads")})";
            }

            var folder = Path.GetFileName(Path.GetDirectoryName(location));
            return $"Newtonsoft.Json -> {folder}/{Path.GetFileName(location)} " +
                $"({(interopAvailable ? "IL2CPP interop proxy" : "managed assembly without the IL2CPP overloads")})";
        }
        catch (Exception exception)
        {
            return $"Newtonsoft.Json -> resolution unknown ({exception.GetType().Name}: {exception.Message})";
        }
    }
}
