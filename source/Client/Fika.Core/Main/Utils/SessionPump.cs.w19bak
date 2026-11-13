using System;
using System.Collections;
using Fika.Core.Main.GameMode;

namespace Fika.Core.Main.Utils;

/// <summary>
/// The session start sequence of <see cref="CoopGame"/>, hosted by a plain managed static class.
///
/// Why this is not a method of <see cref="CoopGame"/> any more (round 8, analysis/delegate-targets):
/// Il2CppInterop registers <em>every</em> declared method of an injected class with il2cpp, including
/// private ones, and gives each of them its own native entry (a trampoline thunk,
/// <c>ClassInjector.CreateTrampoline</c>) plus a shared managed invoker
/// (<c>ClassInjector.ConvertMethodInfo</c>: <c>InvokerMethod = GetFunctionPointerForDelegate(GetOrCreateInvoker(...))</c>,
/// <c>MethodPointer = VirtualMethodPointer = GetFunctionPointerForDelegate(GetOrCreateTrampoline(...))</c>).
/// The 2026-10-10 host raid died inside exactly that construction: the invoker's closing <c>calli</c> was
/// driven into the trampoline thunk of <c>CoopGame.StartSessionPump</c> - a private method that this
/// codebase only ever called directly - and coreclr fail-fasted ("attempted to call a
/// UnmanagedCallersOnly method from managed code", analysis/run-2026-10-10-1553/scan-thunk-result.txt).
/// A method that is not declared on an injected class has no registered MethodInfo, no trampoline and no
/// invoker, so it cannot be reached that way at all. The engine still only calls the two members it
/// declared itself: <see cref="CoopGame.SessionRun(Il2CppSystem.Action)"/> and
/// <see cref="CoopGame.SessionRun()"/>, which stay on the injected class.
///
/// The sequence itself is unchanged, in the order the native base coroutine ran it: FinishRaidSetup -> the
/// countdown screen -> the native session coroutine (which calls InitSessionUI, then SessionRun and finally
/// the run callback that releases the native PrepareSession). The only difference is who drives it: the
/// Update pump in the plugin instead of the engine, which never receives a managed enumerator for this flow.
/// </summary>
internal static class SessionPump
{
    /// <summary>How long the pump waits for the native session coroutine to reach SessionRun() before it warns.</summary>
    private const float StartWatchdogSeconds = 20f;

    /// <summary>
    /// The native session coroutine <see cref="CoopGame.SessionRun(Il2CppSystem.Action)"/> returned, and the
    /// pump routine that drives it. Both are only held for the duration of the sequence and are cleared when
    /// it ends or when a new sequence starts (a transit), so a finished raid can never keep the il2cpp
    /// enumerator - and through it the profile and the world - alive.
    /// </summary>
    private static Il2CppSystem.Collections.IEnumerator _sessionCoroutine;
    private static FikaRoutinePump.RoutineHandle _pumpRoutine;

    /// <summary>
    /// Starts (or restarts) the session sequence for one raid. Replaces the private
    /// <c>CoopGame.StartSessionPump</c>; the state it used to own on the game instance (the routine handle
    /// and the "the native coroutine reached SessionRun" flag) is passed in as the game's fields.
    /// </summary>
    internal static void Start(CoopGame game, Il2CppSystem.Action runCallback)
    {
        // A second call can only mean a second raid start (a transit) or a re-entered prepare: stop the previous
        // sequence and clear the "the native coroutine reached SessionRun" flag so the watchdog measures this run.
        if (game._sessionRoutine != null)
        {
            FikaRoutinePump.Instance.Stop(game._sessionRoutine, "a new session start sequence replaced it");
            game._sessionRoutine = null;
        }

        // The native session coroutine of the previous sequence (if any) dies with it: the statics are
        // cleared here so a finished raid cannot keep an il2cpp enumerator, its profile and its world alive.
        _sessionCoroutine = null;
        _pumpRoutine = null;
        game._sessionRunReached = false;

        
        // World bound: this sequence drives one raid, so it must be stopped the moment that raid's world is
        // torn down (analysis/transit-crash-unload, see FikaRoutinePump.StopWorldBound).
        game._sessionRoutine = FikaRoutinePump.Instance.StartWorldBound(Sequence(game, runCallback), "SessionRun");
    }

    /// <summary>
    /// Called by WorldLifecycle when the world this sequence belongs to goes down
    /// (analysis/transit-crash-unload). Both halves have to be dropped here:
    ///   * the routine that drives the native session coroutine - its enumerator belongs to the world that
    ///     just died, so the pump must never step it again (this is the crash this method exists for);
    ///   * the enumerator reference itself, so a finished raid cannot keep the il2cpp object - and through
    ///     it the profile and the world - alive (the same rule the class comment states for a new Start).
    /// Returns true when a native enumerator was actually dropped, so the teardown line can say so.
    /// Idempotent and exception free: it runs inside the teardown path.
    /// </summary>
    internal static bool OnWorldTornDown(string reason)
    {
        var dropped = false;
        try
        {
            if (_pumpRoutine != null)
            {
                FikaRoutinePump.Instance.Stop(_pumpRoutine, "the world went down (" + reason + ")");
                _pumpRoutine = null;
            }

            if (_sessionCoroutine != null)
            {
                dropped = true;
                _sessionCoroutine = null;
            }
        }
        catch (Exception exception)
        {
            
        }

        return dropped;
    }

    private static IEnumerator Sequence(CoopGame game, Il2CppSystem.Action runCallback)
    {
        
        yield return FikaBoundaries.Run("SessionSequence step 1/3 (FinishRaidSetup)",
            () => game.GameController.FinishRaidSetup(), null);
        

        
        yield return FikaBoundaries.Run("SessionSequence step 2/3 (CountdownScreen)",
            () => game.GameController.CountdownScreen(game.Profile, game.ProfileId), null);
        

        
        FikaBoundaries.Run("SessionSequence step 3/3 (start the native session coroutine on the pump)",
            () => _sessionCoroutine = NativeBaseCalls.Call69(game, runCallback));

        if (_sessionCoroutine == null)
        {
            
            yield break;
        }

        // drivesNativeObject: true - this routine steps the game's own il2cpp enumerator
        // (BaseLocalGame.SessionRun), which lives in the world of this raid. Stepping it after that world
        // went down is exactly the native access violation of the 2026-10-12 crash trail, so it is stopped
        // by FikaRoutinePump.StopWorldBound the moment the world is torn down, whatever epoch it belongs to.
        FikaBoundaries.Run("SessionSequence step 3/3 (the pump drives the native session coroutine)",
            () => _pumpRoutine = FikaRoutinePump.Instance.StartWorldBound(NativeRoutines.Adapt(_sessionCoroutine),
                "SessionRunNative", true));

        

        var deadline = UnityEngine.Time.time + StartWatchdogSeconds;
        while (UnityEngine.Time.time < deadline)
        {
            if (game._sessionRunReached)
            {
                
                _sessionCoroutine = null;
                _pumpRoutine = null;
                yield break;
            }

            yield return null;
        }

        
    }
}
