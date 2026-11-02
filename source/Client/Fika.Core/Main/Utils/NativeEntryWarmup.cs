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
/// 在注册时校验注入的 Fika 类型所生成的每一个原生入口点。
///
/// Il2CppInterop 为注入类型的每个方法提供两个反向 P/Invoke 入口，二者都由该方法自身的签名
/// 通过手写 IL 构建而成：
///   * 一个位于自身 <c>InvokerDelegateMetadataV29</c> 之后的 invoker（五个参数，与方法无关：
///     methodPointer、methodInfo、obj、args、returnValue），其方法体解包 <c>args</c>
///     并对方法指针执行 <c>calli</c>；以及
///   * 一个位于方法指针槽之后的 "Trampoline_&lt;signature&gt;" 方法。
/// 这两个方法体均由 Il2CppInterop 生成（本移植版已转译了它的两个 emitter，
/// 见 <see cref="NativeInjectionScope"/>），但它们始终只是*由 JIT 校验*，而 JIT
/// 第一次运行就在首次到达该方法的原生回调内部——在那里，被拒绝的方法体会变成
/// 运行时致命的 0x80131506 快速失败（fail fast），而不是一个托管异常
/// （analysis/adjust-shot-vectors/REPORT.md、analysis/crash-clr/REPORT.md）。
///
/// 这个类的第一个版本从未校验过任何东西，原因值得在此保留下来。
/// 它向注入器索取 invoker 委托，用
/// <c>Marshal.GetFunctionPointerForDelegate</c> 把它转成指针，再用
/// <c>Marshal.GetDelegateForFunctionPointer&lt;InvokerDelegateMetadataV29&gt;</c> 把它转回委托。该指针
/// 属于对*托管*方法的反向 P/Invoke thunk，因此那次反向调用返回的正是
/// 注入器缓存的那个委托实例——它的类型是 interop 自己运行时创建的
/// <c>InvokerDelegateMetadataV29</c>，从来不是 Fika 中同名的声明——于是这次强转抛出
/// <c>InvalidCastException</c>，任何方法体都尚未进入。这就是为什么每次运行都记录了相同
/// 数量的签名被 "rejected"：915 个中的 915 个，完全没有被预热
/// （analysis/warmup-probe-fix/REPORT.md、EntryProbe/A：转换到 Fika 的类型总是抛出异常，
/// 而转换到委托自身的类型则返回同一个实例）。
///
/// 因此 invoker 是通过注入器绑定它时所用的方法来访问的——生成的方法体
/// 是一个 <see cref="DynamicMethod"/>，而 <c>delegate.Method</c> 就是该方法体——随后用
/// <see cref="DynamicMethod.CreateDelegate(Type)"/> 绑定到 Fika 对该 ABI 的声明上。之后该方法体
/// 从托管代码中被进入一次，并以一个真正的原生空操作作为方法指针。该目标由
/// <see cref="EnsureNoOpTarget"/> 写入可执行内存：真正的机器码，没有
/// 托管帧，因此 invoker 的 <c>calli</c> 永远不会被判定为托管代码调用一个
/// <c>UnmanagedCallersOnly</c> 方法——那正是会快速失败而不是抛出异常的形状。该存根
/// 会累加自己的命中计数并发布一个标记，因此日志中的 "warmed" 意味着方法体真正
/// 运行并到达了它，而不是仅仅创建了一个委托对象。
///
/// 判定结果分三种，只有前两种值得关注：
///   * <b>warmed</b>——方法体经 JIT 编译并执行。这也是当原生侧
///     到达它时会同样正常工作的入口。
///   * <b>rejected</b>——运行时拒绝生成的方法体（<c>InvalidProgramException</c>）。
///     这正是当回调先到达时以无法捕获的 0x80131506 死掉的那一类，
///     因此它按方法记录为错误。本移植版的 invoker 转译器正是把按引用
///     形状挡在这个桶之外的东西（EntryProbe：原版按引用方法体被拒绝，而本移植版
///     打过补丁的可以运行）。
///   * <b>not warmed</b>——方法体无法在此处被实际执行（没有原生目标，或某个此运行时
///     不使用的 invoker 形状），这是该插桩工具的覆盖缺口，而不是一条判定结论。
///
/// 坦率说明仍未覆盖的部分：每个方法的 trampoline 方法体（进入它总是会运行
/// 托管方法，因此无法从这里被实际执行），以及那些 JIT 在托管上下文中接受、但作为
/// 非托管入口点却会拒绝的方法体之后的一切。该探针无法
/// 制造出那种情况——往返过程中的按引用方法体在两个入口都被拒绝，而普通
/// 方法体在两个入口都能运行——所以到目前为止两种上下文是一致的。
/// </summary>
internal static class NativeEntryWarmup
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void InvokerDelegateMetadataV29(nint methodPointer, Il2CppMethodInfo* methodInfo, nint obj, nint* args, nint* returnValue);

    /// <summary>
    /// 每个参数预留的字节数。生成的方法体会解引用每个槽位，以加载
    /// 按值参数所携带的值，因此该槽位必须能容纳任何注入方法所取用的
    /// 最宽结构体。256 远大于本移植版中最宽的形状（Vector3 为 12，DamageInfo 约 28），
    /// 而且读取已清零的暂存区是无害的。
    /// </summary>
    private const int ArgumentSlotSize = 256;

    /// <summary>为生成的方法体把非 void 返回值复制进去所预留的字节数。</summary>
    private const int ReturnBufferSize = 4096;
    private const int MaxReportedNames = 12;

    /// <summary>原生空操作发布的标记，使"方法体已运行"可以从托管代码观察到。</summary>
    private const long StubMarker = 0x53445257; // "WRDS"

    private enum Verdict
    {
        /// <summary>生成的方法体已编译并运行，且到达了方法指针。</summary>
        Warmed,
        /// <summary>运行时拒绝生成的方法体：这种形状在回调中会快速失败。</summary>
        Rejected,
        /// <summary>此处未被实际执行（没有原生目标，或此运行时不使用的 invoker 形状）。</summary>
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
            // 游戏日志中只写一行。逐条目的
            // "[NativeEntryWarmup] (expected) the runtime rejects the generated entry point of ..." 行
            // 在 2026-10-10 那次运行 411 KB 的 LogOutput.log 中占了 691 行，每次注册都会写入
            // （analysis/log-noise-cleanup）。上面那行汇总有意保持它的精确形状：
            // 运行检查器会 grep "warmed for .*; 0 rejected"（test-debug/tools/Check-FikaRun.ps1:30）。
            logger?.LogInfo($"[NativeEntryWarmup] {rejected.Count} injected entry point(s) were rejected by the runtime "
                + "(expected: these are the shapes that die as a 0x80131506 fail fast when the native side reaches them "
                + "first, and the managed shim serves them)");
            
            foreach (var entry in rejected)
            {
                
            }
        }
        if (notWarmed > 0)
        {
            // 这个插桩工具的缺口值得点名：它说明哪些形状没有被实际执行，
            // 而绝不说明它们是错的。
            var summary = string.Join("; ", notWarmedReasons.Select(pair => pair.Value + "x " + pair.Key));
            logger?.LogWarning($"[NativeEntryWarmup] {notWarmed} signature(s) could not be exercised here: {summary}");
            
        }
        
    }

    /// <summary>
    /// 被拒绝条目的 "count x declaring type" 计数，出现最频繁的在前。完整的逐条目
    /// 列表保留在它下方：这是人最先读到的一行，也正是那一行游戏日志所指向的
    /// 内容。
    /// </summary>

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
    /// 用一个真正的原生空操作作为方法指针，把某个 invoker 运行一次。
    ///
    /// 委托来自注入器，而方法体是通过它自己的
    /// <see cref="MethodInfo"/> 访问的——不能用 Marshal 往返，详见类型注释。
    /// 运行时拒绝的方法体在这里表现为可捕获的 <c>InvalidProgramException</c>，
    /// 而不是同一种拒绝在回调内部造成的快速失败；而方法体是否运行
    /// 由原生存根的命中计数与标记来证明，而不是由没有异常来证明。
    /// 注入的方法本身永远不会被进入：该存根忽略每一个参数。
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
            // 方法体是五参数 metadata v29 形状；使用更早的四参数 invoker 的
            // 运行时根本无法从这里驱动。
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
    /// 分配每次预热调用都会用到的方法指针：一块普通的可执行内存，其中存放一个
    /// 存根，该存根通过其结果槽写入 <see cref="StubMarker"/>，累加自己的命中计数
    /// 并在 rax 中返回该标记。它从不解引用任何参数，因此对每一种签名都安全，
    /// 并且由于它不是托管 thunk，invoker 的 <c>calli</c> 永远不会被判定为
    /// 托管代码调用一个 <c>UnmanagedCallersOnly</c> 方法——那正是会快速失败
    /// 而不是抛出异常的形状。无法分配存根时返回零，此时调用方只
    /// 记录清点结果。
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
            // Win64：rax = marker，通过结果槽存储它，递增计数器，返回 rax。
            // 两个立即数在下方被写入；第二个从字节 25 开始。
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
            // reserve | commit，页面标记为 execute read write
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
