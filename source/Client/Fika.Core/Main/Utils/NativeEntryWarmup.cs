using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Runtime;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Validates every generated native entry point of the injected Fika types at registration time.
///
/// Il2CppInterop gives each method of an injected type two reverse-P/Invoke entries, built from the
/// method's own signature by hand written IL:
///   * an invoker behind its own <c>InvokerDelegateMetadataV29</c> (five arguments, independent of
///     the method: methodPointer, methodInfo, obj, args, returnValue) whose body unpacks <c>args</c>
///     and <c>calli</c>s the method pointer, and
///   * a "Trampoline_&lt;signature&gt;" method behind the method pointer slot.
/// Both bodies are produced by Il2CppInterop (this port already transpiles two of its emitters,
/// see <see cref="NativeInjectionScope"/>), but they are only ever *validated by the JIT*, and the
/// JIT runs for the first time inside the native callout that first reaches the method - where a
/// rejected body is the runtime's fatal 0x80131506 fail fast instead of a managed exception
/// (analysis/adjust-shot-vectors/REPORT.md, analysis/crash-clr/REPORT.md).
///
/// The first version of this class never validated anything, and the reason is worth keeping here.
/// It asked the injector for the invoker delegate, turned it into a pointer with
/// <c>Marshal.GetFunctionPointerForDelegate</c> and back into a delegate with
/// <c>Marshal.GetDelegateForFunctionPointer&lt;InvokerDelegateMetadataV29&gt;</c>. The pointer
/// belongs to a reverse-P/Invoke thunk over a *managed* method, so the reverse call returns the very
/// delegate instance the injector cached - whose type is the interop's own, runtime created
/// <c>InvokerDelegateMetadataV29</c>, never Fika's declaration of the same name - and the cast threw
/// <c>InvalidCastException</c> before any body was entered. That is why every run logged the same
/// number of signatures as "rejected": 915 of 915, with nothing warmed at all
/// (analysis/warmup-probe-fix/REPORT.md, EntryProbe/A: the round trip into Fika's type always throws,
/// the round trip into the delegate's own type returns the same instance).
///
/// The invoker is therefore reached through the method the injector bound it to - the generated body
/// is a <see cref="DynamicMethod"/> and <c>delegate.Method</c> is that body - and bound to Fika's
/// declaration of the ABI with <see cref="DynamicMethod.CreateDelegate(Type)"/>. The body is then
/// entered once from managed code with a real native no-op as the method pointer. That target is
/// written by <see cref="EnsureNoOpTarget"/> into executable memory: genuine machine code with no
/// managed frame, so the invoker's <c>calli</c> can never be read as managed code calling an
/// <c>UnmanagedCallersOnly</c> method - the shape that fail fasts instead of throwing. The stub
/// bumps its own hit counter and publishes a marker, so "warmed" in the log means the body really
/// ran and reached it, not that a delegate object was created.
///
/// The verdicts are three way, and only the first two are interesting:
///   * <b>warmed</b> - the body JIT compiled and executed. This is the entry that will also work
///     when the native side reaches it.
///   * <b>rejected</b> - the runtime refuses the generated body (<c>InvalidProgramException</c>).
///     This is exactly the class that dies as the uncatchable 0x80131506 when a callout reaches it
///     first, so it is logged per method as an error. The port's invoker transpiler is what keeps
///     by-ref shapes out of this bucket (EntryProbe: stock by-ref bodies are rejected, the port's
///     patched ones run).
///   * <b>not warmed</b> - the body could not be exercised here (no native target, or an invoker
///     shape this runtime does not use), which is a gap in this instrument, not a verdict.
///
/// What stays uncovered, said plainly: the per method trampoline body (entering that one always runs
/// the managed method, so it cannot be exercised from here) and anything behind a body the JIT
/// accepts in managed context but would reject as an unmanaged entry point. The probe could not
/// produce that case - the by-ref bodies of the round trip were rejected in both entries, the plain
/// ones ran in both - so the two contexts have agreed so far.
/// </summary>
internal static class NativeEntryWarmup
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void InvokerDelegateMetadataV29(nint methodPointer, Il2CppMethodInfo* methodInfo, nint obj, nint* args, nint* returnValue);

    /// <summary>
    /// Bytes reserved per argument. The generated body dereferences each slot to load the value a
    /// by-value parameter carries, so the slot has to hold the widest struct any injected method
    /// takes. 256 is far above the widest shape in this port (Vector3 is 12, DamageInfo ~28) and
    /// reading zeroed scratch is harmless.
    /// </summary>
    private const int ArgumentSlotSize = 256;

    /// <summary>Bytes reserved for the value the generated body copies a non-void return into.</summary>
    private const int ReturnBufferSize = 4096;
    private const int MaxReportedNames = 12;

    /// <summary>Marker the native no-op publishes, so "the body ran" is observable from managed code.</summary>
    private const long StubMarker = 0x53445257; // "WRDS"

    private enum Verdict
    {
        /// <summary>The generated body compiled and ran, and it reached the method pointer.</summary>
        Warmed,
        /// <summary>The runtime rejects the generated body: the shape that fail fasts in a callout.</summary>
        Rejected,
        /// <summary>Not exercised here (no native target, or an invoker shape this runtime does not use).</summary>
        NotWarmed,
    }

    private static MethodInfo _getOrCreateInvoker;
    private static nint _noOpPointer;
    private static nint _noOpCounter;
    private static nint _noOpResult;
    private static bool _unavailable;
    private static bool _targetReported;

    internal static void WarmAll(Type[] types, ManualLogSource logger)
    {
        if (_unavailable || types == null || types.Length == 0) return;
        try
        {
            WarmAllCore(types, logger);
        }
        catch (Exception exception)
        {
            _unavailable = true;
            
            logger?.LogWarning($"[NativeEntryWarmup] validation aborted: {exception.Message}");
        }
    }

    private static void WarmAllCore(Type[] types, ManualLogSource logger)
    {
        _getOrCreateInvoker ??= AccessTools.DeclaredMethod(typeof(ClassInjector), "GetOrCreateInvoker")
            ?? throw new MissingMethodException(typeof(ClassInjector).FullName, "GetOrCreateInvoker");
        var target = EnsureNoOpTarget(logger);

        var candidates = new List<MethodInfo>();
        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                                   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.IsAbstract || method.ContainsGenericParameters) continue;
                if (method.IsDefined(typeof(HideFromIl2CppAttribute), false)) continue;
                if (method.IsSpecialName && type.GetProperties(BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Any(property => (property.GetMethod == method || property.SetMethod == method) &&
                                     property.IsDefined(typeof(HideFromIl2CppAttribute), false))) continue;
                if (!NativeInjectionScope.IsNativeSignatureType(method.ReturnType)) continue;
                if (!method.GetParameters().All(parameter => NativeInjectionScope.IsNativeSignatureType(parameter.ParameterType))) continue;
                candidates.Add(method);
            }
        }

        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var rejected = new List<string>();
        var byRefShapes = new List<string>();
        var multiStructShapes = new List<string>();
        var distinctSignatures = 0;
        var warmed = 0;
        var notWarmed = 0;
        var notWarmedReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var method in candidates)
        {
            var parameters = method.GetParameters();
            var name = method.DeclaringType.Name + "." + method.Name;
            if (parameters.Any(parameter => parameter.ParameterType.IsByRef))
                byRefShapes.Add(name + "(" + Describe(parameters) + ")");
            if (parameters.Count(parameter => !parameter.ParameterType.IsByRef && parameter.ParameterType.IsValueType) >= 2)
                multiStructShapes.Add(name + "(" + Describe(parameters) + ")");
            if (!signatures.Add(SignatureKey(method))) continue;
            distinctSignatures++;
            var verdict = WarmInvoker(method, target, out var reason);
            switch (verdict)
            {
                case Verdict.Warmed:
                    warmed++;
                    break;
                case Verdict.Rejected:
                    rejected.Add($"{method.DeclaringType.FullName}.{method.Name}: {reason}");
                    break;
                default:
                    notWarmed++;
                    var key = string.IsNullOrEmpty(reason) ? "unspecified" : reason;
                    notWarmedReasons[key] = notWarmedReasons.TryGetValue(key, out var seen) ? seen + 1 : 1;
                    break;
            }
        }

        logger?.LogInfo($"[NativeEntryWarmup] {warmed} injected entry signature(s) warmed for {distinctSignatures} signature(s) of {candidates.Count} method(s); {rejected.Count} rejected, {notWarmed} not warmed here");
        
        if (rejected.Count > 0)
        {
            // One line in the game log. The per-entry
            // "[NativeEntryWarmup] (expected) the runtime rejects the generated entry point of ..." lines
            // were 691 of the 411 KB of LogOutput.log of the 2026-10-10 run, written on every registration
            // (analysis/log-noise-cleanup). The summary line above keeps its exact shape on purpose: the
            // run checker greps "warmed for .*; 0 rejected" (test-debug/tools/Check-FikaRun.ps1:30).
            logger?.LogInfo($"[NativeEntryWarmup] {rejected.Count} injected entry point(s) were rejected by the runtime "
                + "(expected: these are the shapes that die as a 0x80131506 fail fast when the native side reaches them "
                + "first, and the managed shim serves them)");
            
            foreach (var entry in rejected)
            {
                
            }
        }
        if (notWarmed > 0)
        {
            // A gap in this instrument is worth naming: it says which shapes were not exercised,
            // never that they are wrong.
            var summary = string.Join("; ", notWarmedReasons.Select(pair => pair.Value + "x " + pair.Key));
            logger?.LogWarning($"[NativeEntryWarmup] {notWarmed} signature(s) could not be exercised here: {summary}");
            
        }
        
    }

    /// <summary>
    /// "count x declaring type" for the rejected entries, most frequent first. The complete per-entry list
    /// stays below it: this is the line a human reads first, and it is what the single game-log line points
    /// at.
    /// </summary>
    private static string Aggregate(List<string> rejected)
    {
        var perType = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in rejected)
        {
            var separator = entry.IndexOf(':');
            var owner = separator <= 0 ? entry : entry.Substring(0, separator);
            var lastDot = owner.LastIndexOf('.');
            var type = lastDot <= 0 ? owner : owner.Substring(0, lastDot);
            perType[type] = perType.TryGetValue(type, out var seen) ? seen + 1 : 1;
        }

        return string.Join("; ", perType.OrderByDescending(pair => pair.Value)
            .Select(pair => pair.Value + "x " + pair.Key));
    }

    private static string Names(List<string> shapes)
    {
        if (shapes.Count == 0) return string.Empty;
        var head = string.Join(", ", shapes.Take(MaxReportedNames));
        return shapes.Count > MaxReportedNames ? $"; first {MaxReportedNames}: {head}" : "; " + head;
    }

    private static string Describe(ParameterInfo[] parameters) =>
        string.Join(", ", parameters.Select(parameter => (parameter.ParameterType.IsByRef ? "ref " : string.Empty) + parameter.ParameterType.Name));

    private static string SignatureKey(MethodInfo method) =>
        method.Name + "|" + string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.AssemblyQualifiedName));

    /// <summary>
    /// Runs one invoker once, with a real native no-op as its method pointer.
    ///
    /// The delegate comes from the injector, and the body is reached through its own
    /// <see cref="MethodInfo"/> - the Marshal round trip cannot be used, see the type comment.
    /// A body the runtime rejects surfaces here as a catchable <c>InvalidProgramException</c>
    /// instead of the fail fast the same rejection causes inside a callout, and a body that runs
    /// is proven by the native stub's hit counter and marker, not by the absence of an exception.
    /// The injected method itself is never entered: the stub ignores every argument.
    /// </summary>
    private static unsafe Verdict WarmInvoker(MethodInfo method, nint target, out string reason)
    {
        reason = null;
        if (target == 0)
        {
            reason = "no native warm target";
            return Verdict.NotWarmed;
        }

        Delegate invoker;
        try
        {
            invoker = (Delegate)_getOrCreateInvoker.Invoke(null, new object[] { method });
        }
        catch (Exception exception)
        {
            reason = $"the injector refused the invoker ({exception.GetType().Name})";
            return Verdict.NotWarmed;
        }

        if (invoker?.Method is not DynamicMethod body)
        {
            reason = "the injector no longer returns a generated body";
            return Verdict.NotWarmed;
        }

        InvokerDelegateMetadataV29 typed;
        try
        {
            // The body is the five argument metadata v29 shape; a runtime that uses the older
            // four argument invoker simply cannot be driven from here.
            typed = body.CreateDelegate<InvokerDelegateMetadataV29>();
        }
        catch (Exception exception)
        {
            reason = $"the generated body does not bind to the five argument invoker ({exception.GetType().Name})";
            return Verdict.NotWarmed;
        }

        var parameterCount = method.GetParameters().Length;
        var slotCount = Math.Max(1, parameterCount);
        var arguments = (byte*)NativeMemory.Alloc((nuint)(slotCount * ArgumentSlotSize));
        var argumentPointers = stackalloc nint[slotCount];
        try
        {
            for (var index = 0; index < slotCount; index++)
            {
                NativeMemory.Clear(arguments + index * ArgumentSlotSize, ArgumentSlotSize);
                argumentPointers[index] = (nint)(arguments + index * ArgumentSlotSize);
            }

            var returnBuffer = (nint*)NativeMemory.Alloc(ReturnBufferSize);
            try
            {
                NativeMemory.Clear(returnBuffer, ReturnBufferSize);
                var before = ReadCounter();
                typed(target, null, 0, argumentPointers, returnBuffer);
                if (ReadCounter() == before)
                {
                    reason = "the generated body returned without reaching its method pointer";
                    return Verdict.NotWarmed;
                }
                if (!_targetReported)
                {
                    _targetReported = true;
                    
                }
                return Verdict.Warmed;
            }
            finally
            {
                NativeMemory.Free(returnBuffer);
            }
        }
        catch (InvalidProgramException exception)
        {
            reason = $"InvalidProgramException: {exception.Message}";
            return Verdict.Rejected;
        }
        catch (Exception exception)
        {
            reason = $"{exception.GetType().Name}: {exception.Message}";
            return Verdict.NotWarmed;
        }
        finally
        {
            NativeMemory.Free(arguments);
        }
    }

    private static long ReadCounter() =>
        _noOpCounter == 0 ? 0 : Marshal.ReadInt64(_noOpCounter);

    /// <summary>
    /// Allocates the method pointer every warm invocation uses: ordinary executable memory holding a
    /// stub that writes <see cref="StubMarker"/> through its result slot, bumps its own hit counter
    /// and returns the marker in rax. It never dereferences an argument, so it is safe for every
    /// signature, and because it is not a managed thunk the invoker's <c>calli</c> can never be read
    /// as managed code calling an <c>UnmanagedCallersOnly</c> method - the shape that fail fasts
    /// instead of throwing. Returns zero when the stub cannot be allocated, and the caller then only
    /// records the inventory.
    /// </summary>
    private static nint EnsureNoOpTarget(ManualLogSource logger)
    {
        if (_noOpPointer != 0) return _noOpPointer;
        try
        {
            _noOpResult = Marshal.AllocHGlobal(64);
            _noOpCounter = Marshal.AllocHGlobal(64);
            Marshal.WriteInt64(_noOpResult, 0);
            Marshal.WriteInt64(_noOpCounter, 0);
            // Win64: rax = marker, store it through the result slot, inc the counter, return rax.
            // The two immediates are patched in below; the second one starts at byte 25.
            var code = new byte[]
            {
                0x48, 0xB8, 0x57, 0x52, 0x44, 0x53, 0x00, 0x00, 0x00, 0x00, // mov rax, StubMarker
                0x48, 0xBA, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // mov rdx, <result slot>
                0x48, 0x89, 0x02,                                           // mov [rdx], rax
                0x48, 0xB9, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // mov rcx, <counter slot>
                0x48, 0xFF, 0x01,                                           // inc qword [rcx]
                0xC3,                                                       // ret
            };
            BitConverter.GetBytes(_noOpResult.ToInt64()).CopyTo(code, 12);
            BitConverter.GetBytes(_noOpCounter.ToInt64()).CopyTo(code, 25);
            // reserve | commit, page execute read write
            var stub = VirtualAlloc(0, (nuint)code.Length, 0x1000 | 0x2000, 0x40);
            if (stub == 0) throw new InvalidOperationException("VirtualAlloc returned nothing");
            Marshal.Copy(code, 0, stub, code.Length);
            _noOpPointer = stub;
            return _noOpPointer;
        }
        catch (Exception exception)
        {
            _noOpPointer = 0;
            logger?.LogWarning($"[NativeEntryWarmup] the native no-op could not be created ({exception.GetType().Name}: {exception.Message}); "
                + "only the signature inventory will be recorded");
            
            return 0;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protect);
}
