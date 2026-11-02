using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Fika.Core.Main.Utils;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace Fika.Core;

/// <summary>
/// Fika 仍然把托管枚举器交给引擎的唯一地方，也是把这件事
/// 记入诊断文件的地方。
///
/// 这刻意是仅剩的一种交接形态：引擎在整个协程生命周期内持有一个
/// <c>BepInEx.Unity.IL2CPP.Utils.Collections.Il2CppManagedEnumerator</c>，随后释放它，
/// 而该释放路径正是引擎解引用假指针 0xA 的地方
/// （菜单面板见 analysis/panel-crash-round7，主机 raid 启动见 analysis/host-raid-coroutine）。
/// 过去在 raid 启动期间运行的每个站点现在改由 <see cref="FikaRoutinePump"/> 驱动；此处
/// 留下的站点都已在经过验证的会话中无故障运行过，且每次启动都会写一行，给出
/// 站点、托管例程和两个指针，以便下一次故障能被归因而非猜测。
///
/// 2026-10-10 21:35 轮次（analysis/red-errors-coroutines，E1）：交接现在被包装在
/// <see cref="EngineRoutineBridge"/> 中，它在向外传递时转换每一个 yield 出的值。从
/// 这里驱动的例程过去会把托管的 <c>WaitSeconds</c> 交给引擎，从而丢失这次等待，因为
/// <c>Il2CppManagedEnumerator.Current</c> 只能转发 il2cpp 对象。现在引擎永远不会收到托管的
/// 值，因此下面每个站点的这一失效模式被一次性关闭。
/// </summary>
/// <remarks>
/// 该类为 public，以便另一个 Fika 程序集（Fika.Headless，其 raid 启动会把
/// <c>CreateStashes()</c> 和 <c>SyncTraps()</c> 交给引擎）可以走同一边界，而不是直接
/// 调用 <c>WrapToIl2Cpp()</c>——这两个例程会 yield <c>WaitSeconds</c>，并原样复现了
/// 本类所关闭的那个失效。见 analysis/red-errors-coroutines，E1，"invariant completeness"。
/// </remarks>
public static class EngineCoroutines
{
    /// <summary>每个站点与会话记录启动次数的上限；每帧站点不得刷爆诊断文件。</summary>
    private const int ReportsPerSite = 4;

    /// <summary>
    /// 一次引擎调用中允许的步进次数。Unity 会在同一帧内步进嵌套例程直到遇到真正的等待；
    /// 该上限可阻止因在已完成任务上循环的例程而卡住该帧（见 EngineRoutineBridge）。
    /// </summary>
    internal const int MaxStepsPerTrigger = 128;

    private static readonly Dictionary<string, int> _startsPerSite = [];

    /// <summary>到目前为止报告的转换、拒绝和步进上限，每个站点与种类一条记录。</summary>
    private static readonly HashSet<string> _reportedKinds = [];

    /// <summary>不同报告键的上界；足以命名一次 raid 触及的每个站点。</summary>
    private const int MaxReportedKinds = 128;

    /// <summary>
    /// 为引擎包装一个托管例程，并写出命名它的那一行。请使用它而不是
    /// <c>routine.WrapToIl2Cpp()</c>，这样每个由引擎驱动的托管协程都可归因且值安全。
    /// </summary>
    public static Il2CppSystem.Collections.IEnumerator Wrap(IEnumerator routine, string site, string host = null)
    {
        var bridge = Bridge(routine, site);
        var wrapped = bridge.WrapToIl2Cpp();
        var enumerator = wrapped is Il2CppObjectBase wrappedObject
            ? NativeRefGuard.Describe(wrappedObject)
            : (routine is null ? "null" : routine.GetType().FullName);

        Report(site, routine, host, enumerator);
        return wrapped;
    }

    /// <summary>
    /// 桥接器本身，未包装形式。值边界需要它来处理嵌套例程（见
    /// <see cref="EngineRoutineBridge"/>），该例程必须在引擎能够步进进入之前先被桥接。
    /// </summary>
    internal static EngineRoutineBridge Bridge(IEnumerator routine, string site)
    {
        return new EngineRoutineBridge(routine, site);
    }

    private static void Report(string site, IEnumerator routine, string host, string enumerator)
    {
        var key = string.IsNullOrEmpty(site) ? "unknown" : site;
        _startsPerSite.TryGetValue(key, out var count);
        _startsPerSite[key] = count + 1;
        if (count > ReportsPerSite)
        {
            return;
        }

        if (count == ReportsPerSite)
        {
            
            return;
        }

        
    }

    /// <summary>
    /// 一次托管等待已为引擎完成转换。每个站点一行：站点是边界被行使过的
    /// 证据，值是转换保持了例程时序的证据。
    /// </summary>
    internal static void ReportConversion(string site, string detail)
    {
        Report(site, "converted", detail);
    }

    /// <summary>无法构建引擎等待对象，因此改为以 Time.time 为基准来度量这次等待。</summary>
    internal static void ReportConversionFallback(string site, float seconds, Exception exception)
    {
        Report(site, "conversion-fallback", "the engine WaitForSeconds for " + seconds +
            " s could not be built (" + exception.GetType().Name + ": " + exception.Message +
            "); the wait is kept, measured against Time.time");
    }

    /// <summary>
    /// 由引擎驱动的例程 yield 出了一个没有引擎对应物的值。它不会被交出（
    /// 引擎对它的转换会抛异常），因此该例程改为等待一帧，而该行会同时给出两者。
    /// </summary>
    internal static void ReportUnsupportedYield(string site, object yielded)
    {
        Report(site, "unsupported-yield:" + (yielded?.GetType().FullName ?? "null"),
            "an engine driven routine yielded " + (yielded?.GetType().FullName ?? "null") +
            ", which has no il2cpp counterpart; it waits one frame instead of losing the step");
    }

    /// <summary>某个例程撞上了每次调用的步进上限；其余部分在下一帧继续。</summary>
    internal static void ReportStepLimit(string site)
    {
        Report(site, "step-limit", "the routine reached the per-call step cap (" + MaxStepsPerTrigger +
            "); it continues on the next frame");
    }

    private static void Report(string site, string kind, string detail)
    {
        try
        {
            if (_reportedKinds.Count >= MaxReportedKinds || !_reportedKinds.Add(site + " | " + kind))
            {
                return;
            }

            
        }
        catch (Exception)
        {
            // 报告绝不能弄坏它所报告的协程。
        }
    }
}

internal static class Il2CppCoroutineExtensions
{
    public static Coroutine StartCoroutine(this MonoBehaviour host, IEnumerator routine, [CallerMemberName] string site = null)
    {
        // 此后由引擎驱动该例程；命名它的那一行见 EngineCoroutines，
        // 它可能 yield 的值见 EngineRoutineBridge。
        var hostDescription = NativeRefGuard.Describe(host);
        var wrapped = EngineCoroutines.Wrap(routine, site, hostDescription);
        return host.StartCoroutine(wrapped);
    }
}
