using System;
using System.Reflection;
using System.Linq;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;

namespace Fika.Core.Main.Utils;

/// <summary>
/// The injector resolves System.Nullable&lt;T&gt; to a CLR struct, whereas generated
/// game wrappers expose Il2CppSystem.Nullable&lt;T&gt;. Correct that signature lookup
/// only while registering Fika types; keep the generated wrapper's native ABI.
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
    // A real CLR generic parameter, used only as a lookup marker for inherited
    // open generic methods. It is never written into a native signature.
    private static readonly Type InheritedParameter = typeof(InheritedGeneric<>).GetGenericArguments()[0];
    private sealed class InheritedGeneric<T> { }

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
        }
        catch
        {
            // A throwing constructor is never disposed by a using statement.
            // Remove any hooks already installed before allowing load to fail.
            Dispose();
            throw;
        }
    }

    private static unsafe bool MatchInterfaceSignature(Type type, INativeClassStruct baseClass, int slot, ref MethodInfo __result)
    {
        if (type.Assembly != _activeAssembly) return true;
        __result = null;
        // The installed resolver matches only name/arity. ApplyItem's two
        // overloads have the same arity but incompatible value/reference ABIs.
        // Use the actual interface slot's full parameter signature instead.
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
        // Proceed<T> has a closed ABI and its body does not depend on T.
        // Il2CppInterop leaves generic definitions' pointers empty, then rejects
        // the vtable slot. Supply a valid fallback while retaining IsGeneric and
        // the injector's normal per-instantiation inflation dictionary.
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
        // ValueTask and Fika packet structs pass the stock IsValueType check,
        // but have no native class/ABI. Their calls remain entirely managed.
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
        // E.g. IEnumerable<TInventoryOperation>. FullName is null for an open
        // constructed type. Preserve it for lookup; retain the native method.
        __result = type;
        return false;
    }

    private static unsafe bool KeepInheritedGeneric(Il2CppTypeStruct* typePointer, ref Type __result)
    {
        var type = UnityVersionHandler.Wrap(typePointer);
        if (type.Type != Il2CppTypeEnum.IL2CPP_TYPE_MVAR) return true;
        // Execute<TInventoryOperation> is inherited intact. The stock resolver
        // treats TInventoryOperation as an assembly type and throws before it
        // can retain that native vtable slot. No Fika method may match this
        // marker; ValidateDeclaredMethods enforces that boundary.
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

    // This rewrites the CLR injector's IL generator only during Fika registration.
    // Native Nullable boxing erases the wrapper (null or boxed T); generated
    // interop parameters require an actual Nullable<T> object with both fields.
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

    // The installed injector emits ldind.i/stind.i for ref structs. That reads
    // and writes only a pointer-sized portion of Vector3 (and misreads float
    // references). Emit typed copies for CLR value types; retain the original
    // reference conversion for native objects, strings and arrays.
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
        // args[i] already contains the address of a ref/out argument. Native
        // object references are rewritten to IntPtr*, CLR structs to T&.
        // Neither address should be dereferenced a second time.
        if (opcode == System.Reflection.Emit.OpCodes.Ldobj && (type.IsByRef || type.IsPointer)) return;
        body.Emit(opcode, type);
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
            // The field API handles native references/write barriers as well
            // as primitive values; do not assume Nullable<T> is blittable.
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
