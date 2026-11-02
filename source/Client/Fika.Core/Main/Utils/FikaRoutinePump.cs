using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 托管 <see cref="IEnumerator"/> 例程的 Update 驱动式驱动器，外加一个原生
/// <see cref="Il2CppSystem.Collections.IEnumerator"/>，用于 Fika 需要交给引擎、而原生调用方
/// 坚持要一个枚举器的场合。
///
/// 为什么需要它（改动之前请先读 analysis/host-raid-coroutine）：
/// 把托管枚举器交给引擎（<c>StartCoroutine(IEnumerator)</c> /
/// <c>WrapToIl2Cpp()</c>）会让引擎持有——并在之后释放——一个
/// <c>BepInEx.Unity.IL2CPP.Utils.Collections.Il2CppManagedEnumerator</c>。正是在那条释放路径上，
/// 引擎在这个构建上两次解引用了无效指针 0xA：一次在主菜单在线玩家
/// 面板（analysis/panel-crash-round7），一次在主机战局启动期间，发生在刚刚进入
/// <c>BaseLocalGame.SessionRun</c> 的下一帧、也就是 Fika 加载界面被隐藏的那一帧
/// （analysis/host-raid-coroutine：first-chance 探针所指认的那个 wrapper 指针）。
///
/// 在这里运行的例程保持它作为协程时的确切形态——嵌套、<c>WaitForSeconds</c>、
/// 逐帧 yield、<c>WaitForEndOfFrame</c>——但引擎永远收不到它，因此它不会被
/// 错误地释放。必须保持协程的例程（因为引擎自己驱动它）继续使用
/// <c>EngineCoroutines.Wrap</c>，它会写出指明该例程的那一行。
/// </summary>
internal sealed class FikaRoutinePump
{
    /// <summary>每个进程一个泵：例程由战局流程注册，并由插件的 Update 驱动。</summary>
    internal static readonly FikaRoutinePump Instance = new();

    private readonly List<Routine> _routines = [];
    private readonly HashSet<string> _reportedSlowRoutines = [];
    private int _lastTickFrame = -1;

    /// <summary>
    /// 超过此长度的帧会按名字被上报（第 3 轮）。13:45 那次运行让 <c>Time.frameCount</c> 冻结在
    /// 17849 达 3.1 秒——三次心跳读到同一帧，而主线程什么都没有写——因此
    /// 泵在 3.1 秒内没有被 tick，也没有任何行说明那一帧在做什么。卡顿报告把这
    /// 变成一个有名有姓的事件：间隙、它跨越的两个帧号，以及它之前最后跨过的边界。
    /// </summary>
    private const int StallReportMillis = 1000;

    private long _lastTickAtMillis;
    private int _lastTickFrameAtLastTick = -1;

    private FikaRoutinePump()
    {
    }

    internal int ActiveCount
    {
        get
        {
            return _routines.Count;
        }
    }

    /// <summary>
    /// 注册一个托管例程。它的第一次 <c>MoveNext</c> 在下一个帧 tick 上运行（比
    /// <c>StartCoroutine</c> 晚一帧，后者会立即执行第一步），此后每帧被 tick 一次。
    /// </summary>
    internal RoutineHandle Start(IEnumerator routine, string label, Action completed = null, Action<Exception> failed = null)
    {
        return StartInternal(routine, label, false, completed, failed);
    }

    /// <summary>
    /// 注册一个属于此刻正在运行的那个世界的例程
    /// （analysis/transit-crash-unload）。绑定世界的例程会在该世界被拆除时立即由 <see cref="StopWorldBound"/>
    /// 停止；而驱动 *原生* 枚举器的例程，无论在哪个纪元启动都会被停止，
    /// 因为这样的枚举器属于拥有该协程的那个世界——在那个世界倒下之后继续步进它，
    /// 正是 2026-10-12 那批 0xc0000005 崩溃的形态
    /// （UnityPlayer.dll；见 WorldLifecycle）。
    /// </summary>
    internal RoutineHandle StartWorldBound(IEnumerator routine, string label, bool drivesNativeObject = false,
        Action completed = null, Action<Exception> failed = null)
    {
        return StartInternal(routine, label, drivesNativeObject, completed, failed);
    }

    private RoutineHandle StartInternal(IEnumerator routine, string label, bool drivesNativeObject,
        Action completed, Action<Exception> failed)
    {
        ArgumentNullException.ThrowIfNull(routine);
        var state = new Routine(routine, label ?? "unnamed", completed, failed, drivesNativeObject);
        _routines.Add(state);
        
        return new RoutineHandle(state);
    }

    /// <summary>
    /// 停止每一个无法在它启动时所在的世界中存活的例程：驱动原生
    /// 对象的那些（它们的枚举器会随其所有者一起消亡），以及更早纪元中启动的那些。返回
    /// 被停止的例程数；WorldLifecycle 会把它放到拆除判定行上。
    /// </summary>
    internal int StopWorldBound(string reason)
    {
        var stopped = 0;
        var native = 0;
        for (var i = _routines.Count - 1; i >= 0; i--)
        {
            var state = _routines[i];
            var staleEpoch = !WorldLifecycle.IsCurrent(state.Epoch);
            if (!state.DrivesNativeObject && !staleEpoch)
            {
                continue;
            }

            state.Stop();
            _routines.RemoveAt(i);
            stopped++;
            if (state.DrivesNativeObject)
            {
                native++;
            }
        }

        if (stopped > 0)
        {
            
        }

        return stopped;
    }

    /// <summary>正在运行的例程的标签，用于拆除行或加载开销行。</summary>
    internal string DescribeActive()
    {
        if (_routines.Count == 0)
        {
            return "none";
        }

        var builder = new System.Text.StringBuilder(_routines.Count * 24);
        for (var i = 0; i < _routines.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(_routines[i].Label);
        }

        return builder.ToString();
    }

    /// <summary>停止一个例程，但不运行它的完成处理器。</summary>
    internal void Stop(RoutineHandle handle, string reason)
    {
        if (handle == null || handle.State == null)
        {
            return;
        }

        var state = handle.State;
        if (!state.IsRunning)
        {
            return;
        }

        state.Stop();
        _routines.Remove(state);
        
    }

    internal void StopAll(string reason)
    {
        if (_routines.Count == 0)
        {
            return;
        }

        for (var i = 0; i < _routines.Count; i++)
        {
            _routines[i].Stop();
        }

        
        _routines.Clear();
    }

    /// <summary>
    /// 把每个已注册的例程推进一帧。由插件的 Unity Update 调用（与
    /// 主线程派发器被排空的是同一处），并作为兜底由游戏的 FixedUpdate 调用——因此有
    /// 帧守卫，一帧绝不会被推进两次。
    /// </summary>
    internal void Tick()
    {
        
        ReportStallIfAny();
        try
        {
            TickCore();
        }
        catch (Exception exception)
        {
            FikaBoundaries.Fail(BoundaryTick, exception);
        }
        finally
        {
        }
    }

    /// <summary>
    /// 当主线程离开的时间超过 <see cref="StallReportMillis"/> 时写入帧卡顿标签
    /// （第 3 轮）。在此线程上执行一次无缓冲的 WriteFile，且只在真的发生卡顿时：
    /// 准时的一帧只花一次减法加一次比较。
    /// </summary>
    private void ReportStallIfAny()
    {
        // （analysis/perf-diagnostics-cleanup）纯粹是一个 trail 标签（经由崩溃
        // trail 的一次无缓冲 WriteFile）。总开关关闭时整份报告都被跳过——包括它每帧的
        // Time.frameCount 读取——这正是“关闭时没有逐帧边界穿越”的含义。开关
        // 打开时（Coop / Diagnostics），卡顿 trail 与原先完全一致。
        

        try
        {
            var now = Environment.TickCount64;
            var previous = _lastTickAtMillis;
            var frame = Time.frameCount;
            var previousFrame = _lastTickFrameAtLastTick;
            _lastTickAtMillis = now;
            _lastTickFrameAtLastTick = frame;

            if (previous == 0)
            {
                return;
            }

            var gap = now - previous;
            if (gap < StallReportMillis)
            {
                return;
            }

            
        }
        catch (Exception)
        {
            // 报告绝不能破坏它所报告的那次 tick。
        }
    }

    /// <summary>泵 tick 的 journal 标签；用常量，这样 journal 永远不必为它分配内存。</summary>
    internal const string BoundaryTick = "FikaRoutinePump.Tick (Update/FixedUpdate -> managed)";

    private void TickCore()
    {
        if (_routines.Count == 0)
        {
            return;
        }

        var frame = Time.frameCount;
        if (_lastTickFrame == frame)
        {
            return;
        }

        _lastTickFrame = frame;
        for (var i = 0; i < _routines.Count; i++)
        {
            var state = _routines[i];
            if (!state.IsRunning)
            {
                _routines.RemoveAt(i);
                i--;
                continue;
            }

            try
            {
                state.Step();
            }
            catch (Exception exception)
            {
                
                state.Stop();
            }

            if (!state.IsRunning)
            {
                _routines.RemoveAt(i);
                i--;
            }
        }
    }

    internal void ReportSlowRoutine(string label)
    {
        if (!_reportedSlowRoutines.Add(label))
        {
            return;
        }

        
    }

    /// <summary>正在运行的例程的不透明句柄。</summary>
    internal sealed class RoutineHandle
    {
        internal RoutineHandle(Routine state)
        {
            State = state;
        }

        internal Routine State { get; }

        internal string Label
        {
            get
            {
                return State?.Label;
            }
        }

        internal bool IsRunning
        {
            get
            {
                return State is { IsRunning: true };
            }
        }
    }

    /// <summary>
    /// 一个例程：嵌套迭代器的栈（最外层就是被启动的那一个）、它当前所处的
    /// 等待，以及步进计数。这一切都是纯托管状态。
    /// </summary>
    internal sealed class Routine
    {
        private const int MaxStepsPerRoutinePerFrame = 128;

        /// <summary>
        /// 短于此值的等待不会被追踪（第 3 轮）。以 0.5 秒等待轮询的例程
        /// （CreateStashes、SyncTraps）不能把 trail 变成节拍器；倒计时的部署等待
        /// 是 10 秒，会被追踪。
        /// </summary>
        private const float MinTracedWaitSeconds = 1f;

        /// <summary>每个例程被追踪等待的上限；超过后追踪会自行静音一次。</summary>
        private const int MaxTracedWaits = 32;

        private readonly List<IEnumerator> _stack = [];
        private readonly Action _completed;
        private readonly Action<Exception> _failed;
        private readonly int _startFrame;
        private readonly float _startTime;
        private readonly string _journalLabel;
        private float _waitUntilScaled = float.NegativeInfinity;
        private int _waitUntilFrame = int.MinValue;
        private CustomYieldInstruction _waitForInstruction;
        private int _tracedWaits;
        private bool _waitTraceMuted;
        private bool _waitTraceActive;
        private float _waitTraceSeconds;
        private float _waitTraceStartedAt;

        internal Routine(IEnumerator routine, string label, Action completed, Action<Exception> failed,
            bool drivesNativeObject = false)
        {
            _stack.Add(routine);
            Label = label;
            _completed = completed;
            _failed = failed;
            _startFrame = Time.frameCount;
            _startTime = Time.time;
            Epoch = WorldLifecycle.Epoch;
            DrivesNativeObject = drivesNativeObject;
            // 只构建一次：journal 只保存引用，因此步进例程永远不会分配内存。
            _journalLabel = "pump routine: '" + label + "'";
        }

        internal string Label { get; }

        /// <summary>该例程启动时所在的世界代号（见 WorldLifecycle）。</summary>
        internal int Epoch { get; }

        /// <summary>当该例程步进的原生对象（il2cpp 枚举器）会随其世界消亡时为 true。</summary>
        internal bool DrivesNativeObject { get; }

        internal bool IsRunning { get; private set; } = true;

        internal void Step()
        {
            if (!IsRunning)
            {
                return;
            }

            // 面包屑：如果进程在这一次步进中死亡（运行时 fail fast 无法被捕获），
            // 这就是指明当时在运行什么的那一行。
            if (_waitForInstruction != null)
            {
                // WaitForSecondsRealtime、WaitUntil、WaitWhile 之类会自己回答 "keepWaiting"。
                if (_waitForInstruction.keepWaiting)
                {
                    return;
                }

                _waitForInstruction = null;
            }

            if (_waitUntilFrame > Time.frameCount || Time.time < _waitUntilScaled)
            {
                return;
            }

            TraceWaitReturned();

            for (var steps = 0; steps < MaxStepsPerRoutinePerFrame; steps++)
            {
                if (_stack.Count == 0)
                {
                    Finish(null);
                    return;
                }

                var current = _stack[_stack.Count - 1];
                bool moved;
                try
                {
                    moved = current.MoveNext();
                }
                catch (Exception exception)
                {
                    Finish(exception);
                    return;
                }

                if (!moved)
                {
                    // 嵌套迭代器结束：它的父级在同一帧继续，与 Unity 调度器的
                    // 行为完全一致。只有最外层迭代器才结束例程。
                    _stack.RemoveAt(_stack.Count - 1);
                    if (_stack.Count == 0)
                    {
                        Finish(null);
                        return;
                    }

                    continue;
                }

                if (!HandleYielded(current.Current))
                {
                    return;
                }
            }

            FikaRoutinePump.Instance.ReportSlowRoutine(Label);
        }

        internal void Stop()
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            _stack.Clear();
        }

        /// <summary>
        /// 本移植没有创建的等待对象。读取它的 m_Seconds 就是那次致命读取，因此该例程
        /// 退化为单帧，诊断会说明是哪个例程这么做的。
        /// </summary>
        private void ReportForeignWait(WaitForSeconds seconds)
        {
            try
            {
                if (_reportedForeignWait) return;
                _reportedForeignWait = true;
                
            }
            catch (Exception)
            {
            }
        }

        private bool _reportedForeignWait;

        private static IntPtr _foreignSecondsField;

        /// <summary>
        /// 直接从 il2cpp 内存读出外部 <c>WaitForSeconds</c> 的时长。字段
        /// 句柄只解析一次；这里绝不调用那个终结了 2026-10-10 主机
        /// 战局的生成属性（它最后一条诊断行是 "read WaitForSeconds.m_Seconds"），并且它不会把异常抛进
        /// 调用方。
        /// </summary>
        private static unsafe bool TryReadForeignSeconds(WaitForSeconds seconds, out float value)
        {
            value = 0f;
            try
            {
                if (seconds == null) return false;
                if (_foreignSecondsField == IntPtr.Zero)
                {
                    var klass = IL2CPP.il2cpp_object_get_class(seconds.Pointer);
                    if (klass == IntPtr.Zero) return false;
                    _foreignSecondsField = IL2CPP.il2cpp_class_get_field_from_name(klass, "m_Seconds");
                    if (_foreignSecondsField == IntPtr.Zero) return false;
                }

                float read = 0f;
                IL2CPP.il2cpp_field_get_value(seconds.Pointer, _foreignSecondsField, &read);
                value = read;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>当例程可以在本帧继续时返回 true。</summary>
        private bool HandleYielded(object yielded)
        {
            switch (yielded)
            {
                case null:
                    WaitOneFrame();
                    return false;
                case WaitSeconds managedWait:
                    // 本移植自己的等待对象从不触碰引擎对象：从该线程读取引擎的
                    // WaitForSeconds.m_Seconds 正是终结 2026-10-10 主机
                    // 战局的原因（最后一条诊断行是 "read WaitForSeconds.m_Seconds"，随后
                    // coreclr 抛出了它那不可捕获的 0x80131506）。
                    _waitUntilScaled = Time.time + managedWait.Seconds;
                    TraceWaitEntered(managedWait.Seconds);
                    return false;
                case WaitForSeconds seconds:
                    // 本移植没有创建的等待对象（通常由本泵正在驱动的原生协程
                    // yield 出来）。它生成的 m_Seconds 属性就是那记让
                    // 2026-10-10 主机战局终结的读取，所以时长改由字段 API 提供——那条路径
                    // 触碰 il2cpp 内存，并且绝不会从这个线程进入托管入口。
                    if (TryReadForeignSeconds(seconds, out var foreignSeconds))
                    {
                        _waitUntilScaled = Time.time + foreignSeconds;
                        TraceWaitEntered(foreignSeconds);
                        return false;
                    }

                    ReportForeignWait(seconds);
                    WaitOneFrame();
                    return false;
                case WaitForSecondsRealtime realtime:
                    _waitForInstruction = realtime;
                    return false;
                case CustomYieldInstruction custom:
                    _waitForInstruction = custom;
                    return false;
                case WaitForFixedUpdate:
                    // 第 9 轮（pumpfix）：WaitForFixedUpdate 会在 Unity 的下一个固定步恢复，最多
                    // 只有一帧之隔，而本泵每帧由插件的 Update 以及
                    // CoopGame.FixedUpdate 各 tick 一次——因此“下一帧”是它能表达的最接近的等价物。
                    WaitOneFrame();
                    return false;
                case YieldInstruction:
                    // WaitForEndOfFrame / AsyncOperation：本泵是帧绑定的，因此“下一帧”是
                    // 最诚实的答案（本移植的例程只用它做这件事）。
                    WaitOneFrame();
                    return false;
                case Il2CppSystem.Collections.IEnumerator nativeNested:
                    // 第 9 轮（pumpfix）：*原生* 枚举器（一个 il2cpp 对象，例如游戏自身
                    // 会话协程返回的那个）由本泵而不是引擎来步进。本泵是
                    // 帧粒度的，而这正是 MonoBehaviour.StartCoroutine 驱动它时的粒度，
                    // 并且这是 Fika 能够在不从托管代码调用
                    // MonoBehaviour.StartCoroutine 的前提下驱动原生例程的唯一形态——那次调用干了什么见 SessionPump。
                    _stack.Add(NativeRoutines.Adapt(nativeNested));
                    return true;
                case IEnumerator nested:
                    _stack.Add(nested);
                    return true;
                case Task task:
                    if (task.IsCompleted)
                    {
                        return true;
                    }

                    WaitOneFrame();
                    return false;
                case Il2CppSystem.Threading.Tasks.Task nativeTask:
                    if (nativeTask.IsCompleted)
                    {
                        return true;
                    }

                    WaitOneFrame();
                    return false;
                default:
                    FikaGlobals.LogWarning("FikaRoutinePump: routine '" + Label + "' yielded an unsupported value of type "
                        + yielded.GetType().FullName + "; the pump waits one frame for it");
                    WaitOneFrame();
                    return false;
            }
        }

        private void WaitOneFrame()
        {
            _waitUntilFrame = Time.frameCount + 1;
        }

        /// <summary>
        /// 第 3 轮——定时等待的两端，在拥有该例程的线程上立即写出。
        /// 泵自己实现了 <c>WaitForSeconds</c>，因此它可以在等待之前把例程记为 parked，
        /// 在等待之后记为 resumed。正是这一对把“在例程停驻时死亡”与
        /// “在等待之后的第一条语句中死亡”区分开来：13:45 那次死亡无法被区分。
        /// </summary>
        private void TraceWaitEntered(float seconds)
        {
            if (seconds < MinTracedWaitSeconds)
            {
                return;
            }

            if (_tracedWaits >= MaxTracedWaits)
            {
                return;
            }

            _tracedWaits++;
            _waitTraceActive = true;
            _waitTraceSeconds = seconds;
            _waitTraceStartedAt = Time.time;
        }

        /// <summary>另一端：例程又运行起来了（这是等待之后的第一步）。</summary>
        private void TraceWaitReturned()
        {
            if (!_waitTraceActive)
            {
                return;
            }

            _waitTraceActive = false;
        }

        /// <summary>整毫秒、与区域设置无关，这样一个标签在任何语言环境下读起来都相同。</summary>
        private static string Millis(float seconds)
        {
            return ((int)(seconds * 1000f)) + " ms";
        }

        private void Finish(Exception failure)
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            _stack.Clear();

            if (failure != null)
            {
                
                SafeInvoke(() => _failed?.Invoke(failure));
                return;
            }

            
            SafeInvoke(() => _completed?.Invoke());
        }

        private static void SafeInvoke(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                
            }
        }
    }
}

/// <summary>
/// Fika 交给原生调用方的枚举器。两者都是真实的 il2cpp 对象（绝不是
/// <c>Il2CppManagedEnumerator</c>），因此引擎可以在它自己的原生路径上释放它们。
/// </summary>
internal static class NativeRoutines
{
    /// <summary>
    /// 让一个<em>原生</em>枚举器（游戏拥有的 il2cpp 对象，由它自己的某个
    /// 协程方法返回）可以被托管泵读取。第 9 轮（pumpfix）。
    /// <para>
    /// 泵不能直接接收原生对象：<c>Il2CppSystem.Collections.IEnumerator</c> 是一个
    /// il2cpp 接口，因此 <c>Current</c> 会以裸 <c>IntPtr</c> 的形式从泵的 yield
    /// switch 中返回，且无法被分类。适配器把 <c>MoveNext</c> 转发给原生对象，并
    /// 通过所声明槽位类型的 interop wrapper 转换 <c>Current</c>
    /// （<c>Il2CppSystem.Object</c>，它已经覆盖了 <c>WaitForSeconds</c>、
    /// <c>WaitForEndOfFrame</c>、<c>null</c> 以及泵会处理的嵌套枚举器情形），于是被 yield 的
    /// 值会完全像来自托管迭代器那样到达 <see cref="FikaRoutinePump"/>。
    /// </para>
    /// <para>
    /// 这是 <see cref="Completed"/> 的对偶：那个让原生调用方驱动 Fika
    /// 拥有的流程，这个让 Fika 驱动游戏拥有的流程——两边都不会为引擎创建
    /// 一个需要它持有并释放的托管枚举器（analysis/panel-crash-round7 与
    /// analysis/host-raid-coroutine 的故障类别）。
    /// </para>
    /// </summary>
    internal static IEnumerator Adapt(Il2CppSystem.Collections.IEnumerator native)
    {
        if (native == null)
        {
            yield break;
        }

        while (native.MoveNext())
        {
            yield return native.Current;
        }
    }

    /// <summary>
    /// 一个已经结束的枚举器：引擎调用一次 <c>MoveNext</c>，看到 false，然后通过它的正常
    /// 路径释放该对象。用于引擎（或某个原生调用方）为 Fika 在
    /// 上拥有的流程索要一个枚举器的场合（<see cref="FikaRoutinePump"/>）。
    /// </summary>
    internal static Il2CppSystem.Collections.IEnumerator Completed()
    {
        return new Il2CppSystem.Collections.ArrayList().GetEnumerator();
    }
}
