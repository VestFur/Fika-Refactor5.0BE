using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 注入器会把 System.Nullable&lt;T&gt; 解析成 CLR 结构体，而生成的
/// 游戏 wrapper 暴露的是 Il2CppSystem.Nullable&lt;T&gt;。只在注册 Fika 类型时
/// 修正该签名查找；保留生成的 wrapper 的原生 ABI。
/// </summary>
internal sealed class NativeInjectionScope : IDisposable
{
    private readonly Harmony _harmony = new("com.fika.core.native-signatures");
    private readonly Assembly _assembly;
    private static Assembly _activeAssembly;
    private readonly MethodInfo _resolver;
    private readonly MethodInfo _postfix;
    private readonly MethodInfo _prefix;
    private readonly MethodInfo _rewriter;
    private readonly MethodInfo _rewritePrefix;
    private readonly MethodInfo _eligibility;
    private readonly MethodInfo _eligibilityPrefix;
    private readonly MethodInfo _converter;
    private readonly MethodInfo _converterPostfix;
    private readonly MethodInfo _interfaceLookup;
    private readonly MethodInfo _interfacePrefix;
    private readonly MethodInfo _trampoline;
    private readonly MethodInfo _invoker;
    private readonly MethodInfo _invokerCapture;
    // 一个真正的 CLR 泛型参数，仅作为继承而来的开放
    // 泛型方法的查找标记使用。它永远不会被写进原生签名。
    private static readonly Type InheritedParameter = typeof(InheritedGeneric<>).GetGenericArguments()[0];
    private sealed class InheritedGeneric<T> { }

    // ------------------------------------------------------------------
    // 注入方法调用器：把发出的 calli 替换为托管调用。
    //
    // Il2CppInterop 会把注入方法的 MethodInfo 填上两个指针，而这两者
    // 都是托管的反向 P/Invoke 桩（对托管委托使用 Marshal.GetFunctionPointerForDelegate）：
    // InvokerMethod（调用器）与 MethodPointer（每方法一个的
    // trampoline）。CreateInvoker 生成的函数体随后执行
    //     ldarg.0; <push native args>; EmitCalli(Calli, Cdecl, trampolineReturn, trampolineParams)
    // 也就是说，托管代码直接对一个托管的反向
    // P/Invoke 桩做间接调用。CoreCLR 正是以不可捕获的
    // fail-fast 拒绝这种组合（"attempted to call a UnmanagedCallersOnly method from managed code"，
    // HRESULT 0x80131506），而那正是杀死 2026-10-10 14:22 那场主机战局的原因。
    //
    // 注入器对构造函数已经避免了这种组合：它们的 InvokerMethod
    // 是托管的 StaticVoidIntPtrInvoker_MetadataV29，它会把方法指针
    // 用 Marshal.GetDelegateForFunctionPointer<T> 变回同一个缓存委托，
    // 再像调用任何其它托管委托那样调用它（ClassInjector.decompiled.cs:1541-1544）。
    // 下面的代码把同样的技巧应用到每一个注入方法上。重写发生在
    // 构建单个函数体的过程中，而任何不匹配都会保留注入器原来的间接调用
    // （ResolveCurrentTrampolineInvoke 返回 null，助手函数会把 calli 原样重新发出）。
    // ------------------------------------------------------------------

    /// <summary>当前正在被转换为调用器的方法，由 <see cref="CaptureInvokerMethod"/> 设置。</summary>
    [ThreadStatic]
    private static MethodInfo _invokerMethod;

    /// <summary>方法指针的委托类型，只解析一次（或只上报一次失败）。</summary>
    private static readonly Dictionary<nint, DelegateInvocation> ManagedTrampolineInvocations = new();

    /// <summary>调用器函数体已上报过调用点的方法指针，这样日志能保持安静。</summary>
    private static readonly HashSet<nint> ReportedInvokerCallSites = new();

    /// <summary>托管调用器路径的每进程一次性状态。</summary>
    private static bool _managedInvokerDegraded;

    // Il2CppInterop 的委托工厂、它嵌套的签名键、Marshal 的泛型
    // 指针->委托入口，以及每个委托类型一个已编译的转换器。
    private static MethodInfo _getOrCreateDelegateType;
    private static ConstructorInfo _methodSignatureConstructor;
    private static MethodInfo _getDelegateForFunctionPointer;
    private static readonly Dictionary<Type, MethodInfo> DelegateConverters = new();
    private static readonly MethodInfo _reportInvokerCallSite =
        typeof(NativeInjectionScope).GetMethod(nameof(ReportInvokerCallSite), BindingFlags.Static | BindingFlags.NonPublic);

    private sealed class DelegateInvocation
    {
        internal Type DelegateType;
        internal MethodInfo Invoke;
        internal int ParameterCount;
    }

    internal NativeInjectionScope(Assembly assembly)
    {
        _assembly = assembly;
        if (System.Threading.Interlocked.CompareExchange(ref _activeAssembly, assembly, null) != null)
            throw new InvalidOperationException("Native injection scopes cannot overlap.");
        try
        {
            _resolver = AccessTools.DeclaredMethod(typeof(ClassInjector), "SystemTypeFromIl2CppType")
                ?? throw new MissingMethodException(typeof(ClassInjector).FullName, "SystemTypeFromIl2CppType");
            _postfix = AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(UseGeneratedNullable));
            _prefix = AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(KeepInheritedGeneric));
            _harmony.Patch(_resolver, prefix: new HarmonyMethod(_prefix), postfix: new HarmonyMethod(_postfix));
            _rewriter = AccessTools.DeclaredMethod(typeof(ClassInjector), "RewriteType");
            _rewritePrefix = AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(KeepOpenSignature));
            _harmony.Patch(_rewriter, prefix: new HarmonyMethod(_rewritePrefix));
            _eligibility = AccessTools.DeclaredMethod(typeof(ClassInjector), "IsMethodEligible");
            _eligibilityPrefix = AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(KeepNativeSignatures));
            _harmony.Patch(_eligibility, prefix: new HarmonyMethod(_eligibilityPrefix));
            _converter = AccessTools.DeclaredMethod(typeof(ClassInjector), "ConvertMethodInfo");
            _converterPostfix = AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(BindUsableItemEntry));
            _harmony.Patch(_converter, postfix: new HarmonyMethod(_converterPostfix));
            // 引擎反射所到达的正是每个注入方法的调用器；
            // 生成的函数体以一次 calli 进入反向 P/Invoke thunk 结尾，而这正是致命的形态
            // （analysis/managed-invoker）。在形态被覆盖的地方替换入口，其它地方保留
            // 注入器自己的调用器。就在这里安装，也就是在第一个类型注册之前。
            //
            // 这些钩子刻意放在它们自己的 Harmony 实例上（见
            // NativeManagedInvoker.OwnHarmonyId）：下面的 Dispose() 会取消本作用域打过补丁的每个
            // 目标，而一个随作用域消失的重写钩子，恰恰就是让之后注册的方法
            // （BepInEx 的协程 wrapper、注入器在运行时转换的某个泛型实例化、
            // 另一个插件的类型）继续保留原生那套 calli 形态调用器的原因
            // （analysis/managed-invoker/REPORT-sweep.md）。
            NativeManagedInvoker.Install();
            _interfaceLookup = AccessTools.DeclaredMethod(typeof(ClassInjector), "FindBaseInterfaceImplementation");
            _interfacePrefix = AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(MatchInterfaceSignature));
            _harmony.Patch(_interfaceLookup, prefix: new HarmonyMethod(_interfacePrefix));
            _trampoline = AccessTools.DeclaredMethod(typeof(ClassInjector), "CreateTrampoline");
            _harmony.Patch(_trampoline, transpiler: new HarmonyMethod(
                AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(PreserveNullableBoxing))));
            _harmony.Patch(_trampoline, transpiler: new HarmonyMethod(
                AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(PreserveByRefValues))));
            _invoker = AccessTools.DeclaredMethod(typeof(ClassInjector), "CreateInvoker");
            _harmony.Patch(_invoker, transpiler: new HarmonyMethod(
                AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(PreserveInvokerReferences))));
            // 调用器函数体也必须不对托管 trampoline 桩做间接调用；
            // 元数据平面命名的是同一个方法，所以用同一个函数体上的 prefix
            // 捕获它，并解析出它本应复现的委托类型。
            // 缺失的 interop API 绝不能中止加载：此时助手函数会保留
            // 注入器自己的间接调用，并只上报一次（见 ResolveCurrentTrampolineInvoke）。
            ResolveDelegateSupport();
            _invokerCapture = AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(CaptureInvokerMethod));
            _harmony.Patch(_invoker, prefix: new HarmonyMethod(_invokerCapture));
            _harmony.Patch(_invoker, transpiler: new HarmonyMethod(
                AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(PreserveInvokerCalli))));
        }
        catch
        {
            // 会抛异常的构造函数永远不会被 using 语句释放。
            // 在允许加载失败之前，先移除已经安装的任何钩子。
            Dispose();
            throw;
        }
    }

    private static unsafe bool MatchInterfaceSignature(Type type, INativeClassStruct baseClass, int slot, ref MethodInfo __result)
    {
        if (type.Assembly != _activeAssembly) return true;
        __result = null;
        // 安装好的解析器只匹配名称与参数个数。ApplyItem 的两个
        // 重载参数个数相同，但值/引用 ABI 不兼容。
        // 改用真实接口槽位的完整参数签名。
        var resolver = AccessTools.DeclaredMethod(typeof(ClassInjector), "SystemTypeFromIl2CppType");
        Type Resolve(Il2CppTypeStruct* pointer) => (Type)resolver.Invoke(null,
            new[] { Pointer.Box(pointer, resolver.GetParameters()[0].ParameterType) });
        for (var i = 0; i < baseClass.InterfaceOffsetsCount; i++)
        {
            var offset = baseClass.InterfaceOffsets[i];
            var contract = UnityVersionHandler.Wrap(offset.interfaceType);
            if (slot < offset.offset || slot >= offset.offset + contract.MethodCount) continue;
            var managedContract = Resolve((Il2CppTypeStruct*)IL2CPP.il2cpp_class_get_type((IntPtr)offset.interfaceType));
            if (!managedContract.IsInterface || !managedContract.IsAssignableFrom(type)) continue;
            var nativeMethod = UnityVersionHandler.Wrap(contract.Methods[slot - offset.offset]);
            var name = Marshal.PtrToStringUTF8(nativeMethod.Name);
            var parameters = new Type[nativeMethod.ParametersCount];
            for (var p = 0; p < parameters.Length; p++)
                parameters[p] = Resolve(UnityVersionHandler.Wrap(nativeMethod.Parameters, p).ParameterType);
            var map = type.GetInterfaceMap(managedContract);
            for (var m = 0; m < map.InterfaceMethods.Length; m++)
            {
                var method = map.InterfaceMethods[m];
                if (method.Name != name || method.IsGenericMethod != nativeMethod.IsGeneric ||
                    !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters)) continue;
                var implementation = map.TargetMethods[m];
                if (implementation.DeclaringType == type) __result = implementation;
                return false;
            }
        }
        return false;
    }

    private static unsafe void BindUsableItemEntry(MethodInfo monoMethod, Il2CppMethodInfo* __result)
    {
        if (!IsUsableItemEntry(monoMethod)) return;
        // Proceed<T> 的 ABI 是封闭的，其函数体不依赖 T。
        // Il2CppInterop 会让泛型定义的指针为空，随后又拒绝
        // 该 vtable 槽位。在保留 IsGeneric 与
        // 注入器正常的按实例化膨胀字典的前提下，提供一个有效的兜底。
        var closed = monoMethod.MakeGenericMethod(typeof(EFT.Player.UsableItemController));
        var trampoline = (Delegate)AccessTools.DeclaredMethod(typeof(ClassInjector), "GetOrCreateTrampoline").Invoke(null, new object[] { closed });
        var invoker = (Delegate)AccessTools.DeclaredMethod(typeof(ClassInjector), "GetOrCreateInvoker").Invoke(null, new object[] { closed });
        var info = UnityVersionHandler.Wrap(__result);
        info.MethodPointer = Marshal.GetFunctionPointerForDelegate(trampoline);
        info.VirtualMethodPointer = info.MethodPointer;
        info.InvokerMethod = Marshal.GetFunctionPointerForDelegate(invoker);
    }

    private static bool IsUsableItemEntry(MethodInfo method) =>
        method.DeclaringType == typeof(Players.FikaPlayer) && method.Name == "Proceed" && method.IsGenericMethodDefinition;

    private static bool KeepNativeSignatures(MethodInfo method, ref bool __result)
    {
        if (method.DeclaringType?.Assembly != _activeAssembly) return true;
        var nativeOverride = method.GetBaseDefinition().DeclaringType?.Assembly != _activeAssembly;
        var managedGeneric = method.IsGenericMethodDefinition && !nativeOverride;
        if (method.IsGenericMethodDefinition && nativeOverride && !IsUsableItemEntry(method))
            throw new NotSupportedException($"Generic native override requires an explicit ABI adapter: {method}");
        if (!managedGeneric && IsNativeSignatureType(method.ReturnType) &&
            method.GetParameters().All(p => IsNativeSignatureType(p.ParameterType))) return true;
        if (nativeOverride)
            throw new NotSupportedException($"Native override has a managed-only signature: {method.DeclaringType}.{method}");
        // ValueTask 与 Fika 的数据包结构体通过了原生的 IsValueType 检查，
        // 但没有原生的类/ABI。它们的调用完全保持托管。
        __result = false;
        return false;
    }

    internal static bool IsNativeSignatureType(Type type)
    {
        if (type.IsByRef) return IsNativeSignatureType(type.GetElementType());
        if (type.ContainsGenericParameters) return false;
        if (type == typeof(void) || type == typeof(string) || type.IsPrimitive ||
            type == typeof(IntPtr) || type == typeof(UIntPtr)) return true;
        if (typeof(IIl2CppObjectBase).IsAssignableFrom(type)) return true;
        return type.IsValueType && type.Assembly != _activeAssembly &&
            type.Assembly != typeof(FikaPlugin).Assembly &&
            type.Assembly != typeof(object).Assembly;
    }

    private static bool KeepOpenSignature(Type type, ref Type __result)
    {
        if (!type.ContainsGenericParameters) return true;
        // 例如 IEnumerable<TInventoryOperation>。对一个开放
        // 构造类型来说 FullName 为 null。为查找保留它；同时保留原生方法。
        __result = type;
        return false;
    }

    private static unsafe bool KeepInheritedGeneric(Il2CppTypeStruct* typePointer, ref Type __result)
    {
        var type = UnityVersionHandler.Wrap(typePointer);
        if (type.Type != Il2CppTypeEnum.IL2CPP_TYPE_MVAR) return true;
        // Execute<TInventoryOperation> 被完整继承。原生解析器
        // 把 TInventoryOperation 当成程序集类型，并在它能够
        // 保留该原生 vtable 槽位之前就抛出异常。任何 Fika 方法都不得匹配这个
        // 标记；ValidateDeclaredMethods 强制这条边界。
        __result = type.ByRef ? InheritedParameter.MakeByRefType() : InheritedParameter;
        return false;
    }

    internal static void ValidateDeclaredMethods(Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (method.IsDefined(typeof(HideFromIl2CppAttribute), false)) continue;
            if (method.IsSpecialName && type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Any(p => (p.GetMethod == method || p.SetMethod == method) && p.IsDefined(typeof(HideFromIl2CppAttribute), false))) continue;
            var definition = method.GetBaseDefinition();
            if (method.IsAbstract && definition.DeclaringType?.Assembly == type.Assembly)
                throw new NotSupportedException($"Managed abstract members must be hidden from the native injector: {type}.{method}");
            if (definition.DeclaringType?.Assembly == type.Assembly) continue;
            foreach (var parameter in method.GetParameters())
                if (parameter.ParameterType.ContainsGenericParameters)
                    throw new NotSupportedException($"Native injection of open generic parameters requires an explicit adapter: {type}.{method}");
        }
    }

    private static void UseGeneratedNullable(ref Type __result)
    {
        var byRef = __result.IsByRef;
        var type = byRef ? __result.GetElementType() : __result;
        var element = Nullable.GetUnderlyingType(type);
        if (element == null) return;
        var wrapper = typeof(Il2CppSystem.Nullable<>).MakeGenericType(element);
        __result = byRef ? wrapper.MakeByRefType() : wrapper;
    }

    // 这段代码只在注册 Fika 期间重写 CLR 注入器的 IL 生成器。
    // 原生 Nullable 装箱会抹掉 wrapper（null 或装箱的 T）；生成的
    // interop 参数需要一个同时带两个字段的真实 Nullable<T> 对象。
    private static System.Collections.Generic.IEnumerable<CodeInstruction> PreserveNullableBoxing(
        System.Collections.Generic.IEnumerable<CodeInstruction> source)
    {
        var pendingBoxLookup = false;
        var replacements = 0;
        foreach (var instruction in source)
        {
            yield return instruction;
            if (instruction.opcode == System.Reflection.Emit.OpCodes.Ldstr &&
                Equals(instruction.operand, nameof(IL2CPP.il2cpp_value_box))) pendingBoxLookup = true;
            else if (pendingBoxLookup && instruction.operand is MethodInfo method &&
                method.DeclaringType == typeof(Type) && method.Name == nameof(Type.GetMethod))
            {
                yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call,
                    AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(SelectValueBox)));
                pendingBoxLookup = false;
                replacements++;
            }
        }
        if (replacements != 1) throw new NotSupportedException($"Expected one trampoline boxing lookup, found {replacements}.");
    }

    // 安装好的注入器对 ref 结构体发出 ldind.i/stind.i。那只读写
    // Vector3 中指针大小的那一部分（并且会误读 float
    // 引用）。为 CLR 值类型发出带类型的拷贝；对原生对象、字符串和数组
    // 保留原来的引用转换。
    private static System.Collections.Generic.IEnumerable<CodeInstruction> PreserveByRefValues(
        System.Collections.Generic.IEnumerable<CodeInstruction> source)
    {
        var instructions = source.ToList();
        CodeInstruction elementLocal = null;
        var readPending = false;
        var writePending = false;
        var reads = 0;
        var writes = 0;
        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (instruction.operand is MethodInfo element && element.DeclaringType == typeof(Type) &&
                element.Name == nameof(Type.GetElementType))
            {
                var store = instructions[i + 1];
                var index = store.opcode == System.Reflection.Emit.OpCodes.Stloc_0 ? 0 :
                    store.opcode == System.Reflection.Emit.OpCodes.Stloc_1 ? 1 :
                    store.opcode == System.Reflection.Emit.OpCodes.Stloc_2 ? 2 :
                    store.opcode == System.Reflection.Emit.OpCodes.Stloc_3 ? 3 : -1;
                if (index >= 0) elementLocal = new CodeInstruction(System.Reflection.Emit.OpCodes.Ldloc, index);
                else if (store.opcode == System.Reflection.Emit.OpCodes.Stloc || store.opcode == System.Reflection.Emit.OpCodes.Stloc_S)
                    elementLocal = new CodeInstruction(System.Reflection.Emit.OpCodes.Ldloc, store.operand);
                else throw new NotSupportedException("Trampoline element type is no longer stored in a local.");
            }
            if (instruction.operand is FieldInfo field)
            {
                if (field.DeclaringType == typeof(System.Reflection.Emit.OpCodes) && field.Name == "Ldind_I") readPending = true;
                if (field.Name == "StIndOpcodes") writePending = true;
            }
            if ((readPending || writePending) && instruction.operand is MethodInfo emit &&
                emit.DeclaringType == typeof(System.Reflection.Emit.ILGenerator) && emit.Name == "Emit" &&
                emit.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(System.Reflection.Emit.OpCode) }))
            {
                if (elementLocal == null) throw new NotSupportedException("Missing byref element local.");
                var load = new CodeInstruction(elementLocal);
                load.labels.AddRange(instruction.labels);
                instruction.labels.Clear();
                load.blocks.AddRange(instruction.blocks);
                instruction.blocks.Clear();
                yield return load;
                instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                instruction.operand = AccessTools.DeclaredMethod(typeof(NativeInjectionScope),
                    readPending ? nameof(EmitByRefRead) : nameof(EmitByRefWrite));
                if (readPending) reads++; else writes++;
                readPending = writePending = false;
            }
            yield return instruction;
        }
        if (reads != 1 || writes != 1)
            throw new NotSupportedException($"Expected one byref read/write emitter; found {reads}/{writes}.");
    }

    private static void EmitByRefRead(System.Reflection.Emit.ILGenerator body, System.Reflection.Emit.OpCode original, Type element)
    {
        if (element.IsValueType) body.Emit(System.Reflection.Emit.OpCodes.Ldobj, element);
        else body.Emit(original);
    }

    private static void EmitByRefWrite(System.Reflection.Emit.ILGenerator body, System.Reflection.Emit.OpCode original, Type element)
    {
        if (element.IsValueType) body.Emit(System.Reflection.Emit.OpCodes.Stobj, element);
        else body.Emit(original);
    }

    private static System.Collections.Generic.IEnumerable<CodeInstruction> PreserveInvokerReferences(
        System.Collections.Generic.IEnumerable<CodeInstruction> source)
    {
        foreach (var instruction in source)
        {
            if (instruction.operand is MethodInfo emit && emit.DeclaringType == typeof(System.Reflection.Emit.ILGenerator) &&
                emit.Name == "Emit" && emit.GetParameters().Select(p => p.ParameterType)
                    .SequenceEqual(new[] { typeof(System.Reflection.Emit.OpCode), typeof(Type) }))
            {
                instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                instruction.operand = AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(EmitInvokerValue));
            }
            yield return instruction;
        }
    }

    private static void EmitInvokerValue(System.Reflection.Emit.ILGenerator body, System.Reflection.Emit.OpCode opcode, Type type)
    {
        // args[i] 已经包含 ref/out 参数的地址。原生
        // 对象引用会被重写为 IntPtr*，CLR 结构体则重写为 T&。
        // 这两个地址都不应被再次解引用。
        if (opcode == System.Reflection.Emit.OpCodes.Ldobj && (type.IsByRef || type.IsPointer)) return;
        body.Emit(opcode, type);
    }

    // ------------------------------------------------------------------
    // 调用器函数体：calli -> 托管委托调用
    // ------------------------------------------------------------------

    /// <summary>
    /// 对 <c>ClassInjector.CreateInvoker</c> 的 Harmony prefix：记住正在构建的是哪个方法的函数体，
    /// 这样下面的 transpiler 才能解析出为它创建的那个完全相同的 trampoline 委托类型
    /// （即 <c>CreateTrampoline</c> 为它创建的类型）。注册是每个线程一次同步的
    /// 过程，而 transpiler 在该调用内部运行，也就是在值被清空之前。
    /// </summary>
    private static void CaptureInvokerMethod(object[] __args)
    {
        try
        {
            _invokerMethod = __args.Length > 0 ? __args[0] as MethodInfo : null;
        }
        catch (Exception exception)
        {
            _invokerMethod = null;
            
        }
    }

    /// <summary>
    /// 把 <c>ILGenerator.EmitCalli(OpCodes.Calli, CallingConvention.Cdecl, returnType,
    /// parameterTypes)</c>——<c>CreateInvoker</c> 中唯一的那次间接调用——重写为一段
    /// 改为发出托管委托序列的调用（见 <see cref="EmitManagedTrampolineInvoke"/>）。
    /// 如果无法复现该委托形态，原 calli 保持不动，并且
    /// 这次降级只上报一次：无法被证明的修复不得让这次回调变得更糟。
    /// </summary>
    private static System.Collections.Generic.IEnumerable<CodeInstruction> PreserveInvokerCalli(
        System.Collections.Generic.IEnumerable<CodeInstruction> source)
    {
        // 这段代码只在注入器函数体第一次被打补丁时运行一次——那时还没有方法
        // 正被注入。因此它只能重定向该调用：是否保留
        // 原来的间接调用由下面的助手函数决定，它在注入器
        // 构建某个具体函数体时运行（Harmony transpiler 不会每次调用都重跑）。
        var callis = 0;
        foreach (var instruction in source)
        {
            if (instruction.operand is MethodInfo emit && IsCalliEmitter(emit))
            {
                callis++;
                instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                instruction.operand = AccessTools.DeclaredMethod(typeof(NativeInjectionScope),
                    nameof(EmitManagedTrampolineInvoke));
            }
            yield return instruction;
        }
        if (callis != 1)
            ReportDowngrade("the injector no longer has exactly one indirect call in CreateInvoker;", null);
    }

    private static bool IsCalliEmitter(MethodInfo method)
    {
        if (method.DeclaringType != typeof(System.Reflection.Emit.ILGenerator)) return false;
        if (method.Name != nameof(System.Reflection.Emit.ILGenerator.EmitCalli)) return false;
        var parameters = method.GetParameters();
        if (parameters.Length != 4) return false;
        return parameters[0].ParameterType == typeof(System.Reflection.Emit.OpCode)
            && parameters[1].ParameterType == typeof(CallingConvention)
            && parameters[2].ParameterType == typeof(Type)
            && parameters[3].ParameterType == typeof(Type[]);
    }

    /// <summary>
    /// 发出 <c>CreateInvoker</c> 过去所发 calli 的托管等价物。
    ///
    /// 栈契约（与 calli 的完全相同，这正是它能作为直接替换的原因）：
    ///   in:  [native parameters..., methodPointer]   （方法指针最后压栈）
    ///   out: trampoline 的原生返回值（空 trampoline 则什么也不返回）
    ///
    /// trampoline 委托是 <c>(nint obj, [nint returnBuffer,] nativeParameters...,
    /// Il2CppMethodInfo* methodInfo)</c>——正好是 calli 的参数列表再加末尾的
    /// <c>methodInfo</c>，调用器把它放在第 1 个参数里（<c>ldarg.1</c>）。
    /// </summary>
    internal static void EmitManagedTrampolineInvoke(System.Reflection.Emit.ILGenerator body,
        System.Reflection.Emit.OpCode opcode, CallingConvention callingConvention, Type returnType,
        Type[] parameterTypes)
    {
        // 一切都在这里决定，也就是在构建某个具体函数体的过程中。任何不匹配
        // 都会保留注入器自己的间接调用：无法被证明的修复不得把一个
        // 能工作（尽管不安全）的调用变成坏掉的调用。
        var invocation = ResolveCurrentTrampolineInvoke(opcode, callingConvention, returnType, parameterTypes);
        if (invocation == null)
        {
            body.EmitCalli(opcode, callingConvention, returnType, parameterTypes);
            return;
        }

        // 1) 保存 calli 本会消耗掉的东西。栈是 [parameters..., pointer]
        //    （指针在最后），所以存储按相反顺序进行：先暂存指针。
        var locals = new System.Reflection.Emit.LocalBuilder[parameterTypes.Length + 1];
        for (var i = parameterTypes.Length; i >= 0; i--)
        {
            var type = i == parameterTypes.Length ? typeof(nint) : parameterTypes[i];
            locals[i] = body.DeclareLocal(type);
            body.Emit(System.Reflection.Emit.OpCodes.Stloc, locals[i]);
        }

        // 2) methodPointer -> CreateTrampoline 所安装的那个确切委托实例。
        body.Emit(System.Reflection.Emit.OpCodes.Ldloc, locals[parameterTypes.Length]);
        body.Emit(System.Reflection.Emit.OpCodes.Call, DelegateConverter(invocation.DelegateType));

        // 3) trampoline 自己的参数：按原始顺序排列的 calli 参数……
        for (var i = 0; i < parameterTypes.Length; i++)
            body.Emit(System.Reflection.Emit.OpCodes.Ldloc, locals[i]);

        // ……再加上调用器作为第二个参数收到的 Il2CppMethodInfo*。
        var methodInfo = body.DeclareLocal(typeof(Il2CppMethodInfo*));
        body.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
        body.Emit(System.Reflection.Emit.OpCodes.Stloc, methodInfo);
        body.Emit(System.Reflection.Emit.OpCodes.Ldloc, methodInfo);

        // 3b) 记录引擎到达了哪个方法，每个方法指针一次。这是
        //      “谁在反射调用某个注入方法”的唯一直接证据。
        body.Emit(System.Reflection.Emit.OpCodes.Ldloc, methodInfo);
        body.Emit(System.Reflection.Emit.OpCodes.Conv_I);
        body.Emit(System.Reflection.Emit.OpCodes.Call, _reportInvokerCallSite);

        // 4) 一次普通的托管委托调用；trampoline 运行，不涉及任何反向 P/Invoke
        //    过渡，也不涉及对托管桩的 calli。
        body.Emit(System.Reflection.Emit.OpCodes.Ldloc, methodInfo);
        body.Emit(System.Reflection.Emit.OpCodes.Callvirt, invocation.Invoke);
    }

    /// <summary>
    /// 解析 <c>CreateTrampoline</c> 正用于当前被 transpile 的方法的委托类型：
    /// 做法是用它所用的同一个参数调用注入器自己的工厂
    /// （<c>new DelegateSupport.MethodSignature(monoMethod, hasThis: true)</c>）。
    /// <c>DelegateSupport.GetOrCreateDelegateType</c> 会按该签名缓存结果，因此这里
    /// 返回的是同一个 <see cref="Type"/> 对象——经由
    /// <c>Marshal</c> 的往返所依赖的类型同一性（见 analysis/injected-invoker-fix/REPORT.md）。
    /// </summary>
    /// <summary>
    /// 解析此刻正在构建函数体的那个方法的 trampoline 委托。
    /// 只要形态有任何不匹配就返回 null（并只上报一次），
    /// 好让调用方改为发出注入器原来的间接调用。
    /// </summary>
    private static DelegateInvocation ResolveCurrentTrampolineInvoke(System.Reflection.Emit.OpCode opcode,
        CallingConvention callingConvention, Type returnType, Type[] parameterTypes)
    {
        if (opcode != System.Reflection.Emit.OpCodes.Calli)
        {
            ReportDowngrade("the invoker's indirect call is no longer a Calli", null);
            return null;
        }
        if (callingConvention != CallingConvention.Cdecl)
        {
            ReportDowngrade("the invoker's indirect call is no longer Cdecl", null);
            return null;
        }
        if (_invokerMethod == null)
        {
            ReportDowngrade("an invoker body was built without CaptureInvokerMethod running first", null);
            return null;
        }
        try
        {
            if (!ResolveDelegateSupport())
            {
                ReportDowngrade("Il2CppInterop's DelegateSupport API is unavailable in this install", null);
                return null;
            }
            var delegateType = ResolveTrampolineDelegateType();
            var invocation = ResolveManagedTrampolineInvoke(delegateType);
            if (invocation?.Invoke == null)
            {
                ReportDowngrade("the trampoline delegate of " + DescribeMethod(_invokerMethod)
                    + " has no usable Invoke method", null);
                return null;
            }
            if (invocation.ParameterCount != parameterTypes.Length || invocation.Invoke.ReturnType != returnType)
            {
                ReportDowngrade("the trampoline delegate of " + DescribeMethod(_invokerMethod)
                    + " does not match the invoker's indirect call (, " + parameterTypes.Length + " vs "
                    + invocation.ParameterCount + " parameter(s), " + returnType?.Name + " vs "
                    + invocation.Invoke.ReturnType.Name + ")", null);
                return null;
            }
            return invocation;
        }
        catch (Exception exception)
        {
            ReportDowngrade("resolving the trampoline delegate of " + DescribeMethod(_invokerMethod) + " failed", exception);
            return null;
        }
    }

    private static bool ResolveDelegateSupport()
    {
        if (_getOrCreateDelegateType != null) return true;
        _getOrCreateDelegateType = AccessTools.Method(typeof(DelegateSupport), "GetOrCreateDelegateType");
        _getDelegateForFunctionPointer = AccessTools.Method(typeof(Marshal), "GetDelegateForFunctionPointer",
            new[] { typeof(nint) });
        if (_getOrCreateDelegateType == null || _getDelegateForFunctionPointer == null
            || !_getDelegateForFunctionPointer.IsGenericMethodDefinition
            || _getDelegateForFunctionPointer.GetParameters().Length != 1)
        {
            _getOrCreateDelegateType = null;
            _getDelegateForFunctionPointer = null;
            return false;
        }
        return true;
    }

    private static Type ResolveTrampolineDelegateType()
    {
        var method = _invokerMethod;
        if (method == null) throw new InvalidOperationException("no method being injected on this thread");
        var signature = CreateMethodSignature(method);
        return (Type)_getOrCreateDelegateType.Invoke(null, new[] { signature, method });
    }

    private static object CreateMethodSignature(MethodInfo method)
    {
        if (_methodSignatureConstructor == null)
        {
            var type = typeof(DelegateSupport).GetNestedType("MethodSignature", BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(typeof(DelegateSupport).FullName, "MethodSignature");
            _methodSignatureConstructor = type.GetConstructor(new[] { typeof(MethodInfo), typeof(bool) })
                ?? throw new MissingMethodException(type.FullName, ".ctor(MethodInfo, bool)");
        }
        return _methodSignatureConstructor.Invoke(new object[] { method, true });
    }

    /// <summary>
    /// 解析（并缓存）<see cref="EmitManagedTrampolineInvoke"/> 对某个委托类型
    /// 所需的数据。当该委托没有可用的 Invoke 时返回 null 且从不抛出，
    /// 于是调用方回退到原来的 calli。
    /// </summary>
    private static DelegateInvocation ResolveManagedTrampolineInvoke(Type delegateType)
    {
        if (ManagedTrampolineInvocations.TryGetValue((nint)delegateType.TypeHandle.Value, out var known))
            return known;
        try
        {
            if (!typeof(Delegate).IsAssignableFrom(delegateType)) return null;
            var invoke = delegateType.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance);
            if (invoke == null || invoke.GetParameters().Length == 0) return null;
            var resolved = new DelegateInvocation
            {
                DelegateType = delegateType,
                Invoke = invoke,
                ParameterCount = invoke.GetParameters().Length - 1,
            };
            ManagedTrampolineInvocations[(nint)delegateType.TypeHandle.Value] = resolved;
            return resolved;
        }
        catch (Exception exception)
        {
            ReportDowngrade("inspecting the trampoline delegate of " + DescribeMethod(_invokerMethod) + " failed", exception);
            return null;
        }
    }

    // Marshal.GetDelegateForFunctionPointer<T>(nint)，闭合到确切的 trampoline 委托
    // 类型并缓存：调用器函数体调用的是普通静态方法，而不是反射。
    private static MethodInfo DelegateConverter(Type delegateType)
    {
        lock (DelegateConverters)
        {
            if (DelegateConverters.TryGetValue(delegateType, out var cached)) return cached;
            var method = BuildDelegateConverter(delegateType);
            DelegateConverters[delegateType] = method;
            return method;
        }
    }

    private static MethodInfo BuildDelegateConverter(Type delegateType)
    {
        var parameters = new[] { typeof(Delegate), typeof(nint) };
        var method = new System.Reflection.Emit.DynamicMethod(
            "LoadTrampoline_" + delegateType.Name, typeof(Delegate), parameters, typeof(NativeInjectionScope), true);
        var body = method.GetILGenerator();
        body.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        body.Emit(System.Reflection.Emit.OpCodes.Castclass, delegateType);
        body.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
        body.Emit(System.Reflection.Emit.OpCodes.Call,
            _getDelegateForFunctionPointer.MakeGenericMethod(delegateType));
        body.Emit(System.Reflection.Emit.OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// 每个不同方法指针一条诊断：这正是引擎实际到达的
    /// 入口，而它恰好能指认究竟是谁在反射调用某个注入方法。
    /// 运行在 il2cpp 回调内部，因此只读取原生方法信息结构体——绝不调用某个
    /// il2cpp 对象的 ToString——也绝不让失败干扰这次调用。
    /// </summary>
    private static unsafe void ReportInvokerCallSite(nint methodInfoPointer)
    {
        try
        {
            var key = methodInfoPointer;
            lock (ReportedInvokerCallSites)
            {
                if (!ReportedInvokerCallSites.Add(key)) return;
            }
            var description = DescribeNativeMethod((Il2CppMethodInfo*)methodInfoPointer);
            
        }
        catch (Exception)
        {
        }
    }

    private static unsafe string DescribeNativeMethod(Il2CppMethodInfo* methodInfo)
    {
        if (methodInfo == null) return "<unknown>";
        var wrapper = UnityVersionHandler.Wrap(methodInfo);
        if (wrapper == null) return "<unknown>";
        var name = Marshal.PtrToStringUTF8(wrapper.Name);
        var declaring = DescribeNativeClass(wrapper.Class);
        if (string.IsNullOrEmpty(name)) name = "<unnamed>";
        return declaring + "." + name;
    }

    private static unsafe string DescribeNativeClass(Il2CppClass* klass)
    {
        if (klass == null) return "<unknown>";
        var wrapper = UnityVersionHandler.Wrap(klass);
        var name = Marshal.PtrToStringUTF8(wrapper.Name);
        var space = Marshal.PtrToStringUTF8(wrapper.Namespace);
        if (string.IsNullOrEmpty(space)) return string.IsNullOrEmpty(name) ? "<unknown>" : name;
        return space + "." + name;
    }

    private static string DescribeMethod(MethodInfo method) =>
        method == null ? "<unknown>" : method.DeclaringType?.FullName + "." + method.Name;

    private static void ReportDowngrade(string what, Exception exception)
    {
        try
        {
            if (_managedInvokerDegraded) return;
            _managedInvokerDegraded = true;
            var message = "the injected invoker keeps its original indirect call: " + what
                + " (" + (_invokerMethod == null ? "no method" : DescribeMethod(_invokerMethod)) + ")";
            
            
        }
        catch (Exception)
        {
        }
    }

    private static MethodInfo SelectValueBox(MethodInfo original) =>
        AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(BoxNativeValue));

    internal static unsafe IntPtr BoxNativeValue(IntPtr klass, IntPtr data)
    {
        if (IL2CPP.il2cpp_class_get_name_(klass) != "Nullable`1" || IL2CPP.il2cpp_class_get_namespace_(klass) != "System")
            return IL2CPP.il2cpp_value_box(klass, data);

        var boxed = IL2CPP.il2cpp_object_new(klass);
        var headerSize = IL2CPP.il2cpp_object_unbox(boxed).ToInt64() - boxed.ToInt64();
        var iterator = IntPtr.Zero;
        IntPtr field;
        while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref iterator)) != IntPtr.Zero)
        {
            if ((IL2CPP.il2cpp_field_get_flags(field) & (int)FieldAttributes.Static) != 0) continue;
            var offset = (long)IL2CPP.il2cpp_field_get_offset(field) - headerSize;
            // 字段 API 既能处理原生引用/写屏障，也能处理
            // 原始值；不要假设 Nullable<T> 是可 blittable 的。
            IL2CPP.il2cpp_field_set_value(boxed, field, (byte*)data + offset);
        }
        return boxed;
    }

    public void Dispose()
    {
        foreach (var target in new[] { _resolver, _rewriter, _eligibility, _converter, _interfaceLookup, _trampoline, _invoker })
            if (target != null) _harmony.Unpatch(target, HarmonyPatchType.All, _harmony.Id);
        System.Threading.Interlocked.CompareExchange(ref _activeAssembly, null, _assembly);
    }
}
