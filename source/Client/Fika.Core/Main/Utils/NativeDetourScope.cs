using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Mono.Cecil.Cil;
using MonoMod.Utils;

namespace Fika.Core.Main.Utils;

/// <summary>Preserves inline ref value wrappers while generating Fika detours.</summary>
internal sealed class NativeDetourScope : IDisposable
{
    private readonly Harmony _harmony;
    private readonly MethodInfo _readEmitter;
    private readonly MethodInfo _generator;
    private static readonly Dictionary<int, Type> ValueLocals = new();

    internal NativeDetourScope(MethodBase target)
    {
        if (!Array.Exists(target.GetParameters(), p => p.ParameterType.IsByRef &&
                typeof(Il2CppSystem.ValueType).IsAssignableFrom(p.ParameterType.GetElementType()))) return;
        if (ValueLocals.Count != 0) throw new InvalidOperationException("Native detour value scopes cannot overlap.");
        var patcher = AccessTools.TypeByName("Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher")
            ?? throw new TypeLoadException("IL2CPP detour backend missing");
        _readEmitter = AccessTools.DeclaredMethod(patcher, "EmitConvertArgumentToManaged");
        _generator = AccessTools.DeclaredMethod(patcher, "GenerateNativeToManagedTrampoline");
        _harmony = new Harmony("com.fika.core.native-detour-values");
        try
        {
            _harmony.Patch(_readEmitter, prefix: new HarmonyMethod(typeof(NativeDetourScope), nameof(ReadValueArgument)));
            _harmony.Patch(_generator, postfix: new HarmonyMethod(typeof(NativeDetourScope), nameof(WriteValueArguments)));
        }
        catch { Dispose(); throw; }
    }

    private static bool ReadValueArgument(ILGenerator il, int argIndex, Type managedParamType, ref LocalBuilder variable)
    {
        if (!managedParamType.IsByRef) return true;
        var element = managedParamType.GetElementType();
        if (!typeof(Il2CppSystem.ValueType).IsAssignableFrom(element)) return true;
        variable = il.DeclareLocal(element);
        ValueLocals.Add(variable.LocalIndex, element);
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg, argIndex);
        il.Emit(System.Reflection.Emit.OpCodes.Call, AccessTools.DeclaredMethod(typeof(NativeDetourScope), nameof(ReadInline)).MakeGenericMethod(element));
        il.Emit(System.Reflection.Emit.OpCodes.Stloc, variable);
        il.Emit(System.Reflection.Emit.OpCodes.Ldloca, variable);
        return false;
    }

    private static void WriteValueArguments(DynamicMethodDefinition __result)
    {
        var body = __result.Definition.Body;
        var replaced = 0;
        for (var i = 1; i < body.Instructions.Count - 1; i++)
        {
            var instruction = body.Instructions[i];
            if (instruction.OpCode != Mono.Cecil.Cil.OpCodes.Call ||
                instruction.Operand is not Mono.Cecil.MethodReference method || method.Name != "Il2CppObjectBaseToPtr" ||
                body.Instructions[i + 1].OpCode != Mono.Cecil.Cil.OpCodes.Stind_I ||
                body.Instructions[i - 1].Operand is not VariableDefinition local ||
                !ValueLocals.TryGetValue(local.Index, out var element)) continue;
            // Stack holds the caller's inline address and the updated wrapper.
            // Copy fields back, including GC reference barriers, instead of
            // storing an object pointer over the first eight structure bytes.
            instruction.Operand = __result.Module.ImportReference(
                AccessTools.DeclaredMethod(typeof(NativeDetourScope), nameof(WriteInline)).MakeGenericMethod(element));
            body.Instructions[i + 1].OpCode = Mono.Cecil.Cil.OpCodes.Nop;
            replaced++;
        }
        if (replaced != ValueLocals.Count)
            throw new NotSupportedException($"Expected {ValueLocals.Count} native value writebacks; found {replaced}.");
        ValueLocals.Clear();
    }

    private static T ReadInline<T>(IntPtr data) where T : Il2CppObjectBase =>
        Il2CppInterop.Runtime.Runtime.Il2CppObjectPool.Get<T>(
            NativeInjectionScope.BoxNativeValue(Il2CppClassPointerStore<T>.NativeClassPtr, data));

    private static unsafe void WriteInline<T>(IntPtr destination, T value) where T : Il2CppObjectBase
    {
        ArgumentNullException.ThrowIfNull(value);
        var klass = Il2CppClassPointerStore<T>.NativeClassPtr;
        var header = IL2CPP.il2cpp_object_unbox(value.Pointer).ToInt64() - value.Pointer.ToInt64();
        var iterator = IntPtr.Zero;
        IntPtr field;
        while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref iterator)) != IntPtr.Zero)
        {
            if ((IL2CPP.il2cpp_field_get_flags(field) & (int)FieldAttributes.Static) != 0) continue;
            var offset = (long)IL2CPP.il2cpp_field_get_offset(field);
            // field_set_value adds the boxed field offset to its owner pointer.
            // Adjust for the caller's unboxed struct without touching its header.
            var fieldClass = IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_field_get_type(field));
            if (IL2CPP.il2cpp_class_is_valuetype(fieldClass))
                IL2CPP.il2cpp_field_set_value(destination - (int)header, field, (byte*)value.Pointer + offset);
            else
                IL2CPP.il2cpp_gc_wbarrier_set_field(destination - (int)header,
                    destination + (int)(offset - header), *(IntPtr*)((byte*)value.Pointer + offset));
        }
        GC.KeepAlive(value);
    }

    public void Dispose()
    {
        if (_harmony == null) return;
        if (_readEmitter != null) _harmony.Unpatch(_readEmitter, HarmonyPatchType.All, _harmony.Id);
        if (_generator != null) _harmony.Unpatch(_generator, HarmonyPatchType.All, _harmony.Id);
        ValueLocals.Clear();
    }
}
