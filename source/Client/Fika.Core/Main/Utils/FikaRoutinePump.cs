using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Update driven driver for managed <see cref="IEnumerator"/> routines, plus the native
/// <see cref="Il2CppSystem.Collections.IEnumerator"/> Fika hands to the engine where a native caller
/// insists on an enumerator.
///
/// Why this exists (read analysis/host-raid-coroutine before changing it):
/// handing the engine a managed enumerator (<c>StartCoroutine(IEnumerator)</c> /
/// <c>WrapToIl2Cpp()</c>) makes it hold - and later release - an
/// <c>BepInEx.Unity.IL2CPP.Utils.Collections.Il2CppManagedEnumerator</c>. That release path is where the
/// engine dereferenced the bogus pointer 0xA twice on this build: once for the main-menu online players
/// panel (analysis/panel-crash-round7) and once during the host raid start, one frame after
/// <c>BaseLocalGame.SessionRun</c> was entered and in the frame the Fika loading screen was hidden
/// (analysis/host-raid-coroutine: the wrapper pointer named by the first-chance probe).
///
/// A routine that runs here keeps the exact shape it had as a coroutine - nesting, <c>WaitForSeconds</c>,
/// per-frame yields, <c>WaitForEndOfFrame</c> - but the engine never receives it, so it cannot be released
/// badly. A routine that has to stay a coroutine (because the engine itself drives it) keeps using
/// <c>EngineCoroutines.Wrap</c>, which writes the line that names it.
/// </summary>
internal sealed class FikaRoutinePump
{
    /// <summary>One pump per process: routines are registered by the raid flow and ticked by the plugin Update.</summary>
    internal static readonly FikaRoutinePump Instance = new();

    private readonly List<Routine> _routines = [];
    private readonly HashSet<string> _reportedSlowRoutines = [];
    private int _lastTickFrame = -1;

    /// <summary>
    /// A frame longer than this is reported by name (round 3). The 13:45 run froze <c>Time.frameCount</c> at
    /// 17849 for 3.1 s - three heartbeats read the same frame while the main thread wrote nothing - so the
    /// pump was not ticked for 3.1 s and no line said what that frame was doing. The stall report turns that
    /// into a named event: the gap, the two frame numbers it spans and the last boundary crossed before it.
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
    /// Registers a managed routine. Its first <c>MoveNext</c> runs on the next frame tick (one frame later than
    /// <c>StartCoroutine</c>, which runs the first step immediately) and it is ticked once per frame from then on.
    /// </summary>
    internal RoutineHandle Start(IEnumerator routine, string label, Action completed = null, Action<Exception> failed = null)
    {
        return StartInternal(routine, label, false, completed, failed);
    }

    /// <summary>
    /// Registers a routine that belongs to the world that is up right now
    /// (analysis/transit-crash-unload). A world-bound routine is stopped by <see cref="StopWorldBound"/>
    /// as soon as that world is torn down; one that drives a *native* enumerator is stopped no matter
    /// which epoch it was started in, because such an enumerator belongs to the world that owns the
    /// coroutine - stepping it after that world went down is the shape of the 0xc0000005 crashes of
    /// 2026-10-12 (UnityPlayer.dll; see WorldLifecycle).
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
    /// Stops every routine that cannot survive the world it was started in: the ones that drive a native
    /// object (their enumerator dies with its owner) and the ones started in an older epoch. Returns the
    /// number of routines stopped; WorldLifecycle puts it on the teardown judgement line.
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

    /// <summary>The labels of the running routines, for a teardown or load-cost line.</summary>
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

    /// <summary>Stops a routine without running its completion handler.</summary>
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
    /// Drives every registered routine by one frame. Called from the plugin's Unity Update (the same place
    /// the main-thread dispatcher is drained) and, as a fallback, from the game's FixedUpdate - hence the
    /// frame guard, so a frame is never advanced twice.
    /// </summary>
    internal void Tick()
    {
        
        FikaBoundaries.Enter(BoundaryTick);
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
    /// Writes the frame-stall label when the main thread was away longer than <see cref="StallReportMillis"/>
    /// (round 3). One unbuffered WriteFile on this thread, and only when a stall really happened: a frame
    /// that is on time costs one subtraction and one comparison.
    /// </summary>
    private void ReportStallIfAny()
    {
        // (analysis/perf-diagnostics-cleanup) Purely a trail label (one unbuffered WriteFile through the crash
        // trail). With the master switch off the whole report is skipped - including its per-frame
        // Time.frameCount read - which is what "no per-frame boundary crossing while off" means. With the
        // switch on (Coop / Diagnostics) the stall trail is exactly what it was.
        

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
            // A report must never break the tick it reports about.
        }
    }

    /// <summary>Journal label of the pump tick; a constant so the journal never allocates for it.</summary>
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

    /// <summary>Opaque handle of a running routine.</summary>
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
    /// One routine: a stack of nested iterators (the outermost is the one that was started), the wait it is
    /// currently in, and the step count. All of it is plain managed state.
    /// </summary>
    internal sealed class Routine
    {
        private const int MaxStepsPerRoutinePerFrame = 128;

        /// <summary>
        /// Waits shorter than this are not traced (round 3). A routine that polls with a 0.5 s wait
        /// (CreateStashes, SyncTraps) must not turn the trail into a metronome; the countdown's deploy wait
        /// is 10 s and is traced.
        /// </summary>
        private const float MinTracedWaitSeconds = 1f;

        /// <summary>Upper bound of the traced waits per routine; past it the trace mutes itself once.</summary>
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
            // Built once: the journal only ever stores the reference, so stepping a routine never allocates.
            _journalLabel = "pump routine: '" + label + "'";
        }

        internal string Label { get; }

        /// <summary>The world generation this routine was started in (see WorldLifecycle).</summary>
        internal int Epoch { get; }

        /// <summary>True when this routine steps a native object (an il2cpp enumerator) that dies with its world.</summary>
        internal bool DrivesNativeObject { get; }

        internal bool IsRunning { get; private set; } = true;

        internal void Step()
        {
            if (!IsRunning)
            {
                return;
            }

            // Breadcrumb: if the process dies inside this step (a runtime fail fast cannot be caught),
            // this is the line that names what was running.
            FikaBoundaries.Enter(_journalLabel);
            if (_waitForInstruction != null)
            {
                // WaitForSecondsRealtime, WaitUntil, WaitWhile and friends answer "keepWaiting" themselves.
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
                    // A nested iterator ended: its parent continues in this frame, exactly like Unity's
                    // scheduler does. Only the outermost iterator ends the routine.
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
        /// A wait object this port did not create. Reading its m_Seconds is the fatal read, so the routine
        /// degrades to a single frame and the diagnostics say which routine did it.
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
        /// Reads the duration of a foreign <c>WaitForSeconds</c> straight out of il2cpp memory. The field
        /// handle is resolved once; this never calls the generated property that ended the 2026-10-10 host
        /// raids (its last diagnostics line was "read WaitForSeconds.m_Seconds") and it cannot throw into
        /// the caller.
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

        /// <summary>Returns true when the routine may continue in this frame.</summary>
        private bool HandleYielded(object yielded)
        {
            switch (yielded)
            {
                case null:
                    WaitOneFrame();
                    return false;
                case WaitSeconds managedWait:
                    // The port's own waits never touch an engine object: reading the engine
                    // WaitForSeconds.m_Seconds from this thread is what killed the 2026-10-10 host
                    // raids (the last diagnostics line was "read WaitForSeconds.m_Seconds", then
                    // coreclr raised its uncatchable 0x80131506).
                    _waitUntilScaled = Time.time + managedWait.Seconds;
                    TraceWaitEntered(managedWait.Seconds);
                    return false;
                case WaitForSeconds seconds:
                    // A wait object this port did not create (usually yielded by a native coroutine this
                    // pump is driving). Its generated m_Seconds property is the read that killed the
                    // 2026-10-10 host raids, so the duration comes from the field API instead - that
                    // touches il2cpp memory and never enters a managed entry from this thread.
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
                    // Round 9 (pumpfix): a WaitForFixedUpdate resumes at Unity's next fixed step, which is at
                    // most one frame away, and this pump is ticked once per frame from the plugin Update and
                    // from CoopGame.FixedUpdate - so "next frame" is the closest equivalent it can express.
                    WaitOneFrame();
                    return false;
                case YieldInstruction:
                    // WaitForEndOfFrame / AsyncOperation: the pump is frame bound, so "next frame" is the
                    // honest answer (the port's routines only use it that way).
                    WaitOneFrame();
                    return false;
                case Il2CppSystem.Collections.IEnumerator nativeNested:
                    // Round 9 (pumpfix): a *native* enumerator (an il2cpp object, e.g. the one the game's own
                    // session coroutine returns) is stepped by this pump instead of the engine. The pump is
                    // frame grained, which is exactly the granularity MonoBehaviour.StartCoroutine ran it at,
                    // and this is the only shape in which Fika can drive a native routine without calling
                    // MonoBehaviour.StartCoroutine from managed code - see SessionPump for what that call did.
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
        /// Round 3 - the two ends of a timed wait, written immediately on the thread that owns the routine.
        /// The pump implements <c>WaitForSeconds</c> itself, so it can name the routine as parked before the
        /// wait and as resumed after it. That pair is what separates "died while the routine was parked"
        /// from "died in the first statement after the wait": the 13:45 death could not be told apart.
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
            FikaBoundaries.Mark("pump wait: routine '" + Label + "' parked in WaitForSeconds(" + Millis(seconds)
                + ") at frame=" + Time.frameCount + " - nothing of this routine runs until the pump resumes it");
        }

        /// <summary>The other end: the routine is running again (this is the first step after the wait).</summary>
        private void TraceWaitReturned()
        {
            if (!_waitTraceActive)
            {
                return;
            }

            _waitTraceActive = false;
            FikaBoundaries.Mark("pump wait: routine '" + Label + "' left WaitForSeconds(" + Millis(_waitTraceSeconds)
                + ") after " + Millis(Time.time - _waitTraceStartedAt) + " (frame=" + Time.frameCount + ")");
        }

        /// <summary>Whole milliseconds, culture free, so a label reads the same in every locale.</summary>
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
/// The enumerators Fika hands to native callers. Both are real il2cpp objects (never
/// <c>Il2CppManagedEnumerator</c>), so the engine can release them on its own native path.
/// </summary>
internal static class NativeRoutines
{
    /// <summary>
    /// Makes a <em>native</em> enumerator (an il2cpp object the game owns, returned by one of its own
    /// coroutine methods) readable by the managed pump. Round 9 (pumpfix).
    /// <para>
    /// The pump cannot take the native object directly: <c>Il2CppSystem.Collections.IEnumerator</c> is an
    /// il2cpp interface, so <c>Current</c> would come back as a raw <c>IntPtr</c> that the pump's yield
    /// switch cannot classify. The adapter forwards <c>MoveNext</c> to the native object and converts
    /// <c>Current</c> through the interop wrapper of the declared slot type
    /// (<c>Il2CppSystem.Object</c>, which already covers the <c>WaitForSeconds</c>,
    /// <c>WaitForEndOfFrame</c>, <c>null</c> and nested-enumerator cases the pump handles), so the yielded
    /// value arrives at <see cref="FikaRoutinePump"/> exactly as it would from a managed iterator.
    /// </para>
    /// <para>
    /// This is the counterpart of <see cref="Completed"/>: that one lets a native caller drive a flow Fika
    /// owns, this one lets Fika drive a flow the game owns - without either side creating a managed
    /// enumerator for the engine to hold and release (the fault class of analysis/panel-crash-round7 and
    /// analysis/host-raid-coroutine).
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
    /// An already finished enumerator: the engine calls <c>MoveNext</c> once, sees false and releases the
    /// object through its normal path. Used where the engine (or a native caller) demands an enumerator for
    /// a flow Fika owns on <see cref="FikaRoutinePump"/>.
    /// </summary>
    internal static Il2CppSystem.Collections.IEnumerator Completed()
    {
        return new Il2CppSystem.Collections.ArrayList().GetEnumerator();
    }
}
