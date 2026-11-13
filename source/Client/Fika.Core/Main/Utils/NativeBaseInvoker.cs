using System;
using System.Collections.Concurrent;
using System.Reflection;
using Il2CppInterop.Common;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Copies the generated CLR marshalling wrapper while selecting its declared
/// native implementation. Keeps ref/out, value types and return conversions
/// identical to the installed interop assembly, without virtual redispatch.
/// </summary>
internal static class NativeBaseInvoker
{
    private static readonly ConcurrentDictionary<(MethodInfo, Type), Delegate> Cache = new();
    private static readonly MethodInfo SelectDeclared = typeof(NativeBaseInvoker)
        .GetMethod(nameof(DeclaredImplementation), BindingFlags.Static | BindingFlags.NonPublic);

    private static IntPtr DeclaredImplementation(IntPtr instance, IntPtr method) => method;

    internal static T Get<T>(Type declaringType, string name, Type[] parameters) where T : Delegate
    {
        var method = declaringType.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.DeclaredOnly, null, parameters, null)
            ?? throw new MissingMethodException(declaringType.FullName, name);
        return (T)Cache.GetOrAdd((method, typeof(T)), key => Create<T>(key.Item1));
    }

    private static T Create<T>(MethodInfo method) where T : Delegate
    {
        if (method.IsAbstract || method.ContainsGenericParameters ||
            Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method) == null)
            throw new NotSupportedException($"Not a concrete generated IL2CPP method: {method}");

        using var definition = new DynamicMethodDefinition(method);
        var replacements = 0;
        foreach (var instruction in definition.Definition.Body.Instructions)
        {
            if (instruction.OpCode != OpCodes.Call || instruction.Operand is not MethodReference called ||
                called.DeclaringType.FullName != "Il2CppInterop.Runtime.IL2CPP" ||
                called.Name != "il2cpp_object_get_virtual_method") continue;
            instruction.Operand = definition.Module.ImportReference(SelectDeclared);
            replacements++;
        }
        if (replacements != 1)
            throw new NotSupportedException($"Expected one virtual dispatch in {method}, found {replacements}");
        return (T)DMDCecilGenerator.Generate(definition).CreateDelegate(typeof(T));
    }
}
