using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Common;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 较新的 interop 包装器通过一个
/// NativeInterfaceMethodInfoPtr 字段来分派显式接口成员。Harmony 必须挂接具体的类
/// 实现，而它的 NativeMethodInfoPtr 字段仍然是生成出来的。
/// </summary>
internal sealed class NativePatchScope : IDisposable
{
    private readonly Harmony _harmony;
    private static MethodBase _target;
    private static FieldInfo _field;
    private static readonly MethodInfo Resolver = AccessTools.Method(typeof(Il2CppInteropUtils),
        nameof(Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod));

    internal static FieldInfo GetMethodField(MethodBase method)
    {
        var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
        if (field != null || method is not MethodInfo) return field;
        using var wrapper = new DynamicMethodDefinition(method);
        foreach (var instruction in wrapper.Definition.Body.Instructions)
        {
            if (instruction.OpCode != OpCodes.Ldsfld || instruction.Operand is not FieldReference reference ||
                !reference.Name.StartsWith("NativeInterfaceMethodInfoPtr_", StringComparison.Ordinal)) continue;
            var name = reference.Name.Replace("NativeInterfaceMethodInfoPtr_", "NativeMethodInfoPtr_");
            return AccessTools.DeclaredField(method.DeclaringType, name)
                ?? throw new MissingFieldException(method.DeclaringType.FullName, name);
        }
        return null;
    }

    internal NativePatchScope(MethodBase target)
    {
        if (Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(target) != null) return;
        var field = GetMethodField(target);
        if (field == null) return;
        if (_target != null) throw new InvalidOperationException("Native patch resolution scopes cannot overlap.");
        _target = target;
        _field = field;
        _harmony = new Harmony("com.fika.core.native-patch-resolution");
        try { _harmony.Patch(Resolver, prefix: new HarmonyMethod(typeof(NativePatchScope), nameof(ResolveConcrete))); }
        catch { Dispose(); throw; }
    }

    private static bool ResolveConcrete(MethodBase method, ref FieldInfo __result)
    {
        if (method != _target) return true;
        __result = _field;
        return false;
    }

    public void Dispose()
    {
        if (_harmony == null) return;
        _harmony.Unpatch(Resolver, HarmonyPatchType.All, _harmony.Id);
        _target = null;
        _field = null;
    }
}
