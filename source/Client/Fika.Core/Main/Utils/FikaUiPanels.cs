using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Fika 所触及的游戏 HUD 面板的驱动守卫。
///
/// 游戏面板会从调用方所调用的那些方法内部，启动它们自己的 UI 协程（滑入、颜色混合、倒计时）——
/// 例如 <c>EFT.UI.ExtractionTimersPanel</c> 拥有
/// _panelCoroutine/_timerCoroutine/_timerColorCoroutine 以及对应的 Co_PullUpPanel、
/// Co_MovePivot 和 ChangeColor 枚举器，而 <c>EFT.UI.BattleUITimerPanel</c> 通过
/// ShowPanel/ShowCountdownPanel 拥有 Co_Countdown。Unity 拒绝在一个其 GameObject 处于非激活状态的
/// 组件上启动协程，并为每次尝试写一条错误：
///
/// [Error : Unity] Coroutine couldn't be started because the the game object 'TimerPanel' is inactive!
///
/// 关闭一个面板（撤离、死亡、停止 raid）会让组件仍然存活，但其 GameObject
/// 变为非激活，因此我们这边之后的驱动只能产出那条错误 —— 面板既不可见
/// 也无法播放动画。跳过该调用保持了功能结果（之前本来什么也没显示），
/// 并去掉了噪音；每个位点的首次跳过仍会以 Info 级报告一次，
/// 这样这个决定的证据就不会丢失。
/// </summary>
internal static class FikaUiPanels
{
    /// <summary>已报告过的位点；每个位点每会话一行，绝不每帧一行。</summary>
    private static readonly HashSet<string> Reported = [];

    /// <summary>
    /// 当 <paramref name="panel"/> 是一个可用的 il2cpp 引用且其 GameObject 处于激活状态时为 true，
    /// 即游戏可以合法地为它启动面板协程时。
    /// </summary>
    /// <param name="panel">将被驱动的游戏面板。</param>
    /// <param name="site">用于一次性报告的稳定调用方名称。</param>
    internal static bool CanDrive(Component panel, string site, string detail = null)
    {
        if (panel is null || !NativeRefGuard.IsUsable(panel))
        {
            Report(site, "the panel reference is null or not a usable il2cpp object" + Suffix(detail));
            return false;
        }

        try
        {
            var panelObject = panel.gameObject;
            if (panelObject is null || !panelObject.activeInHierarchy)
            {
                Report(site, "the panel object is inactive" + Suffix(detail));
                return false;
            }
        }
        catch (Exception exception)
        {
            Report(site, "reading the panel object failed (" + exception.GetType().Name + ": " + exception.Message + ")");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 让本游戏的一个面板上线，并回答引擎现在是否可以在它上面启动该面板的
    /// 协程。
    ///
    /// 为什么需要它 (analysis/terminal-transit-mechanism)：在街机 UI 根上调用 <c>SetActive(true)</c>
    /// 是游戏里唯一打开 raid 内 HUD 的语句，而本移植跳过了它
    /// （'Skip Countdown Body' 总开关），之后又从
    /// <c>BaseGameController.RestoreInRaidHudRoutine</c> 恢复它。每一个*不*被那次
    /// 恢复覆盖的 HUD 面板 —— <c>EFT.UI.LocationTransitTimerPanel</c>，其 GameObject 名为 'LocationTransitTimerPanel'
    /// 且负责显示中转倒计时 —— 因此保持非激活。它的原生应用逻辑以
    /// <c>Display()</c> + <c>Show(seconds)</c> 结尾，而这两个方法都会启动该面板自己的协程，所以
    /// 倒计时被拒的方式与撤离面板的驱动完全一样：门禁通过了，
    /// 协程被拒了，而请求倒计时的机器什么也显示不出来。
    /// 2026-10-13 的几轮验证证明了兄弟面板同样如此
    /// (<c>[PanelGuard] skipped ClientGameController.Spawn.TimerPanel.SetTime because the panel object
    /// is inactive</c> and <c>raw: [PanelGuard] refused a coroutine start on the inactive game object
    /// 'TimerPanel': host=EFT.UI.ExtractionTimersPanel</c>).
    ///
    /// 这次调用是刻意受限的：只打开面板自身的 GameObject，并且只在其
    /// 祖先（HUD 根、它所在的 canvas）处于激活状态时 —— 在非激活父级下打开子级
    /// 在 Unity 里不会带来任何改变，所以那种情况只会上报而不会被强行处理。一个 UI 元素
    /// 恰好因为 HUD 层级从未建立而处于非激活状态，正是本方法存在的场景。
    /// </summary>
    /// <param name="panel">应当承载倒计时的游戏面板。</param>
    /// <param name="site">用于一次性报告的稳定调用方名称。</param>
    /// <returns>面板此刻可以被驱动时为 true。</returns>
    internal static bool TryActivateForDrive(Component panel, string site)
    {
        if (panel is null || !NativeRefGuard.IsUsable(panel))
        {
            Report(site, "the panel reference is null or not a usable il2cpp object");
            return false;
        }

        try
        {
            var panelObject = panel.gameObject;
            if (panelObject is null)
            {
                Report(site, "the panel has no game object");
                return false;
            }

            if (panelObject.activeInHierarchy)
            {
                return true;
            }

            if (panelObject.activeSelf)
            {
                // 元素已开启，所以原因在它之上：一个被禁用的 canvas 或 HUD 根。这里不
                // 激活任何祖先 —— 那会与被关掉它的东西对抗。
                Report(site, "the panel is inactive inside an inert HUD hierarchy (" + DescribeChain(panelObject) +
                    "); the panel is not forced on");
                return false;
            }

            panelObject.SetActive(true);
            Report(site, "the panel was switched on (it was inactive, and this port's countdown drive must not " +
                "run on an object the engine refuses to start coroutines on); chain now " + DescribeChain(panelObject));

            if (!panelObject.activeInHierarchy)
            {
                Report(site, "the panel was switched on but its hierarchy is still inert (" + DescribeChain(panelObject) +
                    "); the panel is not driven");
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            Report(site, "reading or activating the panel failed (" + exception.GetType().Name + ": " + exception.Message + ")");
            return false;
        }
    }

    /// <summary>
    /// 一个对象的激活链，从对象自身一直到场景根：<c>name(self)/name(activeInHierarchy) &lt;- ...</c>。
    /// 它指明的正是非激活面板*位于何处* —— 这是单凭 [PanelGuard] 一行无法展现的事实。
    /// 只读、永不抛出异常，并做了限界，使某个怪异的层级无法在此空转。
    /// </summary>
    internal static string DescribeChain(Component panel, string fallback = "unreadable")
    {
        try
        {
            var owner = panel?.gameObject;
            return owner is null ? fallback : DescribeChain(owner);
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>一个 GameObject 的激活链，见 <see cref="DescribeChain(Component, string)"/>。</summary>
    internal static string DescribeChain(GameObject start)
    {
        try
        {
            var builder = new System.Text.StringBuilder();
            var current = start;
            for (var hops = 0; current != null && hops < 8; hops++)
            {
                if (builder.Length > 0)
                {
                    builder.Append(" <- ");
                }

                builder.Append(ReadName(current)).Append('(').Append(ReadActive(current)).Append(')');
                var transform = current.transform;
                current = transform == null ? null : transform.parent?.gameObject;
            }

            return builder.Length == 0 ? "unreadable" : builder.ToString();
        }
        catch (Exception)
        {
            return "unreadable";
        }
    }

    private static string ReadName(GameObject target)
    {
        try
        {
            var name = target.name;
            return string.IsNullOrEmpty(name) ? "?" : name;
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static string ReadActive(GameObject target)
    {
        try
        {
            var self = target.activeSelf;
            var inHierarchy = target.activeInHierarchy;
            if (self && !inHierarchy)
            {
                return "self-active, hierarchy-inactive";
            }

            return self ? "active" : "inactive";
        }
        catch (Exception)
        {
            return "unreadable";
        }
    }

    private static string Suffix(string detail) => string.IsNullOrEmpty(detail) ? string.Empty : "; " + detail;

    /// <summary><see cref="ReportRefusedEngineStart"/> 已报告过的驱动，各占一行。</summary>
    private static readonly HashSet<string> ReportedRefusals = [];

    /// <summary>拒绝报告的上界；超过此数的不同驱动将保持静默。</summary>
    private const int MaxReportedRefusals = 64;

    /// <summary>
    /// 由 <c>MonoBehaviour_StartCoroutine_Patch</c> 调用，针对引擎自身会拒绝的一次启动：
    /// 组件的 GameObject 处于非激活状态，于是 Unity 写出
    /// <c>Coroutine couldn't be started because the the game object 'X' is inactive!</c> 并且什么都不启动。
    /// 补丁同样什么都不启动 —— 调用方仍然收到 null，与之前完全一样 —— 而这一行
    /// 会一次性点名驱动（组件类型、协程类型、对象），因为那条红色错误只点名了
    /// 对象，而 2026-10-10 21:35 的 raid (BHERKP) 留下了五条无法归因的记录。
    /// </summary>
    internal static void ReportRefusedEngineStart(Component host, object routine)
    {
        try
        {
            if (host is null)
            {
                return;
            }

            string hostName = NativeRefGuard.TryReadTypeName(host.Pointer) ?? host.GetType().FullName;
            string routineName = DescribeCoroutine(routine);

            if (ReportedRefusals.Count >= MaxReportedRefusals ||
                !ReportedRefusals.Add(hostName + " | " + routineName))
            {
                return;
            }

            string objectName = "unreadable";
            try
            {
                objectName = host.gameObject?.name ?? "null";
            }
            catch (Exception)
            {
            }

            FikaGlobals.LogInfo("[PanelGuard] refused a coroutine start on the inactive game object '" + objectName +
                "': host=" + hostName + " routine=" + routineName +
                "; chain " + DescribeChain(host, "unreadable") +
                "; nothing was started, exactly as the engine's own refusal would have it");
        }
        catch (Exception)
        {
            // 报告绝不能破坏它所报告的那次调用。
        }
    }

    /// <summary>
    /// 为一个交给引擎的协程命名。优先使用 il2cpp 类名 —— 运行时会用方法名来命名
    /// 迭代器的状态机（<c>EFT.UI.ExtractionTimersPanel+&lt;Co_MovePivot&gt;d__NN</c>），
    /// 而这正是 Unity 自己的错误消息所隐瞒的部分 —— 对于无法读取引用的值，
    /// 则回退到托管类型。
    /// </summary>
    private static string DescribeCoroutine(object routine)
    {
        if (routine is null)
        {
            return "null";
        }

        try
        {
            if (routine is Il2CppObjectBase native)
            {
                string name = NativeRefGuard.TryReadTypeName(native.Pointer);
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }
        }
        catch (Exception)
        {
        }

        try
        {
            return routine.GetType().FullName ?? routine.GetType().Name;
        }
        catch (Exception)
        {
            return "unreadable";
        }
    }

    /// <summary>
    /// 为日志行命名一个面板：可读取时给出其 GameObject 名称，<paramref name="fallback"/>
    /// 在引用为 null 或其名称不可读时给出。永不抛出异常 —— 它在构造
    /// 诊断行时被使用 (analysis/inraid-ui)。
    /// </summary>
    internal static string DescribeObject(Component panel, string fallback)
    {
        if (panel is null)
        {
            return fallback;
        }

        try
        {
            var owner = panel.gameObject;
            if (owner is null)
            {
                return fallback;
            }

            var name = owner.name;
            return string.IsNullOrEmpty(name) ? fallback : "'" + name + "'";
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static void Report(string site, string detail)
    {
        if (!Reported.Add(site))
        {
            return;
        }

        FikaGlobals.LogInfo("[PanelGuard] skipped " + site + " because " + detail +
            "; the game starts this panel's coroutines from inside the call, which Unity refuses on an inactive object");
    }
}
