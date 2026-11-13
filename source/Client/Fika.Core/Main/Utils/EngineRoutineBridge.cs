using System;
using System.Collections;
using System.Threading.Tasks;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Fika 交给引擎的每一个托管协程的值边界。
///
/// 分析:analysis/red-errors-coroutines (E1)。本次移植用托管的
/// <see cref="WaitSeconds"/> 来表达定时等待,因为从托管代码读取引擎的 <c>WaitForSeconds.m_Seconds</c> 正是
/// 2026-10-10 那些主机 raid 崩溃的原因。该规则属于 *pump*(泵)。凡是引擎驱动的协程,
/// 经由 <c>MonoBehaviour.StartCoroutine</c> 与 <see cref="EngineCoroutines.Wrap"/>,都会把它产出的每一个值
/// 交给 <c>Il2CppManagedEnumerator.Current</c>,而后者只能转发 il2cpp 对象。在 2026-10-10 21:35
/// (raid BHERKP,第 15998 帧)其中之一就落到了那里:
///
/// <code>
/// System.NotSupportedException: Type Fika.Core.Main.Utils.WaitSeconds cannot be converted directly to an Il2Cpp object
///   at BepInEx.Unity.IL2CPP.Utils.Collections.Il2CppManagedEnumerator.get_Current()
///   at UnityEngine.MonoBehaviour.StartCoroutine
///   at Fika.Core.Il2CppCoroutineExtensions.StartCoroutine
///   at Fika.Core.Main.FreeCamera.FreeCameraController.MainPlayer_DiedEvent
/// </code>
///
/// 互操作跳板会吞掉该异常,因此协程不会明显地死掉——它丢失了这次等待并
/// 以错乱的步调继续运行(5 秒的死亡镜头等待变成了一帧)。
///
/// 本类维持「任何托管等待对象都绝不进入引擎」这一不变量:内层协程
/// 产出的值会在引擎读取它之前被翻译。
/// <list type="bullet">
/// <item><description><see cref="WaitSeconds"/> 会变成一个真正的引擎 <c>WaitForSeconds</c>。引擎
/// 在原生代码中读取该对象自身的字段,这是安全的——只有对 <c>m_Seconds</c> 的*托管*读取才是
/// 致命的。本次移植在别处就已经构造过正是这种类型的引擎对象(<c>new WaitForEndOfFrame()</c>
/// 就出现在同一批协程里)。</description></item>
/// <item><description>嵌套的托管 <c>IEnumerator</c> 会变成另一个 <see cref="EngineRoutineBridge"/>,
/// 并为其做 il2cpp 包装,因此该不变量在往下一层同样成立。</description></item>
/// <item><description>il2cpp 对象(引擎等待对象、<c>AsyncOperation</c>、原生枚举器)会
/// 原样透传。</description></item>
/// <item><description><c>null</c> 保持为 <c>null</c>(即引擎自身的一帧让出)。</description></item>
/// <item><description>其他任何东西都要花掉一帧并输出一行诊断;它绝不会被交出去,因为
/// 引擎对它的转换只会抛出同一个异常。</description></item>
/// </list>
/// 如果引擎的 <c>WaitForSeconds</c> 无法构建,这次等待就降级为按
/// <c>Time.time</c> 计量的相同时长,并且诊断会点名该协程——等待绝不会被静默丢弃。
/// </summary>
internal sealed class EngineRoutineBridge : IEnumerator
{
    private readonly IEnumerator _routine;
    private readonly string _site;

    /// <summary>托管回退等待的结束时刻,单位为 <c>Time.time</c> 秒;负无穷表示「未停放」。</summary>
    private float _parkUntilScaled = float.NegativeInfinity;

    internal EngineRoutineBridge(IEnumerator routine, string site)
    {
        _routine = routine ?? throw new ArgumentNullException(nameof(routine));
        _site = string.IsNullOrEmpty(site) ? "unknown" : site;
    }

    /// <summary>引擎读取的值。它总能安全转换(null 或一个 il2cpp 对象)。</summary>
    public object Current { get; private set; }

    public bool MoveNext()
    {
        if (Time.time < _parkUntilScaled)
        {
            // 托管回退等待仍在进行:引擎看到的是一次普通的单帧让出。
            Current = null;
            return true;
        }

        _parkUntilScaled = float.NegativeInfinity;

        for (var steps = 0; steps < EngineCoroutines.MaxStepsPerTrigger; steps++)
        {
            if (!_routine.MoveNext())
            {
                Current = null;
                return false;
            }

            var yielded = _routine.Current;

            // 托管任务:泵的约定是「只要已完成就继续,否则让出一帧」。
            if (yielded is Task task)
            {
                if (task.IsCompleted)
                {
                    continue;
                }

                Current = null;
                return true;
            }

            if (yielded is Il2CppSystem.Threading.Tasks.Task nativeTask)
            {
                if (nativeTask.IsCompleted)
                {
                    continue;
                }

                Current = null;
                return true;
            }

            Current = Convert(yielded);
            return true;
        }

        EngineCoroutines.ReportStepLimit(_site);
        Current = null;
        return true;
    }

    public void Reset()
    {
        // Unity 的协程调度器从不调用它所驱动枚举器的 Reset,而 BepInEx 的包装
        // 只是因为接口要求才实现该成员。
    }

    /// <summary>
    /// 唯一的转换点。其契约是:返回值要么是 <c>null</c>,要么是一个 <see cref="Il2CppObjectBase"/>,
    /// 因此 <c>Il2CppManagedEnumerator.Current</c> 再也不会抛出异常。
    /// </summary>
    private object Convert(object yielded)
    {
        switch (yielded)
        {
            case null:
                return null;

            case WaitSeconds wait:
                return EngineWait(wait);

            case Il2CppSystem.Collections.IEnumerator nativeNested:
                // 原生枚举器(游戏自身协程的形态)已经是引擎值;引擎
                // 会自行步进它,正如泵自身的原生分支所做的那样。
                return nativeNested;

            case IEnumerator nested:
                // 嵌套的托管协程遵循同一条规则:引擎收到的是一个 il2cpp 枚举器,
                // 其 Current 由本类应答,因此嵌套更深处出现的 WaitSeconds 也会被翻译。
                return EngineCoroutines.Bridge(nested, _site).WrapToIl2Cpp();

            case Il2CppObjectBase engineObject:
                // WaitForSeconds、WaitForEndOfFrame、WaitUntil、原生枚举器、AsyncOperation:这些
                // 都归引擎所有,并由它以原生方式读取。
                return engineObject;

            default:
                // 没有引擎对应物的托管对象。把它交出去就会抛出本类
                // 存在就是为了防止的那个异常,因此它要花掉一帧并自我点名一次。
                EngineCoroutines.ReportUnsupportedYield(_site, yielded);
                return null;
        }
    }

    private object EngineWait(WaitSeconds wait)
    {
        try
        {
            var engineWait = new WaitForSeconds(wait.Seconds);
            EngineCoroutines.ReportConversion(_site, "WaitSeconds(" + wait.Seconds + " s) -> engine WaitForSeconds");
            return engineWait;
        }
        catch (Exception exception)
        {
            // 等待被保留,只有它的时钟改变了:从现在起由这座
            // 桥接改用 Time.time 来计量,而不再由引擎对象计量。
            _parkUntilScaled = Time.time + wait.Seconds;
            EngineCoroutines.ReportConversionFallback(_site, wait.Seconds, exception);
            return null;
        }
    }
}
