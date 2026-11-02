using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Mono.Cecil.Cil;
using MonoMod.Utils;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 在生成 Fika detour 时保留内联的 ref 值包装器。
/// <para>
/// detour trampoline 把一个 byref 值参数（例如 <c>ref DamageInfo</c>）以真正的包装器
/// 交给托管替换方法，并必须在之后把该（可能已被修改的）值复制回
/// 调用方的内联存储中。Il2CppInterop 曾以两种形态生成这次复制：
/// 至少到 1.5.x 为止，trampoline 以 <c>call Il2CppObjectBaseToPtr</c> + <c>stind.i</c> 结束，
/// 它把一个对象指针写入结构的前八个字节，这里会把它改写为
/// 逐字段复制；1.6.7 则以 <c>call TrampolineHelpers.CopyBoxedStructTo(Il2CppObjectBase src,
/// IntPtr dst)</c> 结束，它已经把完整的装箱结构复制回去，必须保持原样。
/// 两种形态都不包含的 trampoline 只报告一次并保持不动，而不是让依赖它的
/// 补丁失败。
/// </para>
/// </summary>
internal sealed class NativeDetourScope : IDisposable
{
    private static readonly ManualLogSource _logger = Logger.CreateLogSource("Fika.NativeDetour");
    private readonly Harmony _harmony;
    private readonly MethodInfo _readEmitter;
    private readonly MethodInfo _generator;
    private static readonly Dictionary<int, Type> ValueLocals = new();
    private static bool _reportedCopyBack;
    private static bool _reportedRewrite;
    private static bool _reportedMissing;

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
        if (ValueLocals.Count == 0) return;

        var instructions = __result.Definition.Body.Instructions;
        var writeInline = AccessTools.DeclaredMethod(typeof(NativeDetourScope), nameof(WriteInline));
        var handled = new HashSet<int>();
        var rewritten = 0;
        var copiedBack = 0;
        for (var i = 1; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (instruction.OpCode != Mono.Cecil.Cil.OpCodes.Call ||
                instruction.Operand is not Mono.Cecil.MethodReference call ||
                instructions[i - 1].Operand is not VariableDefinition local ||
                !ValueLocals.TryGetValue(local.Index, out var element)) continue;

            if (call.Name == "CopyBoxedStructTo")
            {
                // Il2CppInterop 1.6.7：栈上已经有调用方的内联地址和
                // 更新后的包装器，而 CopyBoxedStructTo 会把整个装箱结构复制回去。
                if (handled.Add(local.Index)) copiedBack++;
                continue;
            }

            if (call.Name != "Il2CppObjectBaseToPtr" || i + 1 >= instructions.Count ||
                instructions[i + 1].OpCode != Mono.Cecil.Cil.OpCodes.Stind_I) continue;

            // 旧形态：栈上持有调用方的内联地址和更新后的包装器，而该包装器
            // 会被当作对象指针写入结构的前八个字节。
            // 改为逐字段复制回去，并包含 GC 引用屏障。
            instruction.Operand = __result.Module.ImportReference(writeInline.MakeGenericMethod(element));
            instructions[i + 1].OpCode = Mono.Cecil.Cil.OpCodes.Nop;
            if (handled.Add(local.Index)) rewritten++;
        }

        if (rewritten > 0 && !_reportedRewrite)
        {
            _reportedRewrite = true;
            _logger.LogInfo($"Rewrote {rewritten} ref value argument(s) whose trampoline stored an object pointer over the inline structure; fields are copied back instead.");
        }
        if (copiedBack > 0 && !_reportedCopyBack)
        {
            _reportedCopyBack = true;
            _logger.LogInfo($"Il2CppInterop copies {copiedBack} ref value argument(s) back with CopyBoxedStructTo; the trampoline needs no rewrite.");
        }
        if (handled.Count != ValueLocals.Count && !_reportedMissing)
        {
            _reportedMissing = true;
            // 补丁本身仍然保持安装：读取侧与回写无关，因此
            // 托管代码仍能看到正确的值。缺失的只是复制回原生内存这一步，
            // 它必须是可见的，而不是启动时静默地「补丁未应用」。
            _logger.LogWarning($"{__result.Definition.Name}: {handled.Count} of {ValueLocals.Count} ref value argument(s) have a native writeback; " +
                "the remaining argument(s) keep their pre-call value in native memory.");
        }
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
            // field_set_value 会把装箱字段偏移加到其所有者指针上。
            // 针对调用方未装箱的结构做调整，且不触碰它的头部。
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
