using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Main.Utils;

public static class FikaNativeTypes
{
    internal static Type[] RegistrationOrder() => RegistrationOrder(typeof(FikaPlugin).Assembly);

    private static Type[] RegistrationOrder(Assembly assembly) => assembly.GetTypes()
        .Where(t => t.IsClass && !t.ContainsGenericParameters && typeof(Il2CppObjectBase).IsAssignableFrom(t))
        .OrderBy(Depth).ThenBy(t => t.FullName, StringComparer.Ordinal).ToArray();

    private static int Depth(Type type)
    {
        var depth = 0;
        for (; type.BaseType != null; type = type.BaseType) depth++;
        return depth;
    }

    internal static void RegisterAll(ManualLogSource logger)
    {
        RegisterAll(typeof(FikaPlugin).Assembly, logger);
    }

    public static void RegisterAll(Assembly assembly, ManualLogSource logger)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(logger);
        using var signatures = new NativeInjectionScope(assembly);
        var types = RegistrationOrder(assembly);
        foreach (var type in types)
        {
            ValidateInterfaces(type);
            NativeInjectionScope.ValidateDeclaredMethods(type);
        }
        foreach (var type in types)
        {
            if (type.GetConstructor(new[] { typeof(IntPtr) }) == null)
                throw new MissingMethodException(type.FullName, ".ctor(IntPtr)");
            logger.LogInfo($"Registering Fika native type: {type.FullName}");
            try
            {
                InitializeNativeHierarchy(type.BaseType, assembly);
                foreach (var contract in type.GetInterfaces().Where(i =>
                             i != typeof(IIl2CppObjectBase) && typeof(IIl2CppObjectBase).IsAssignableFrom(i)))
                    InitializeNativeClass(contract);
                logger.LogDebug($"Native hierarchy ready: {type.FullName}");
                // Keep the runtime's declared-interface resolver: passing an
                // empty list would discard game contracts such as IBotGame.
                ClassInjector.RegisterTypeInIl2Cpp(type);
                if (Il2CppClassPointerStore.GetNativeClassPointer(type) == IntPtr.Zero)
                    throw new InvalidOperationException("Registration returned a null native class");
                NativeInjectedGc.PreserveReferences(type);
                NativeInjectedFinalizer.Register(type);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Fika cannot register {type.FullName} (base {type.BaseType}).", exception);
            }
        }
        logger.LogInfo($"Registered {types.Length} Fika native types from {assembly.GetName().Name}.");
    }

    private static void InitializeNativeHierarchy(Type type, Assembly assembly)
    {
        if (type == null || !typeof(Il2CppObjectBase).IsAssignableFrom(type)) return;
        InitializeNativeHierarchy(type.BaseType, assembly);
        if (type.Assembly == assembly || type == typeof(Il2CppObjectBase)) return;
        InitializeNativeClass(type);
    }

    private static void InitializeNativeClass(Type type)
    {
        var pointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException($"Native base is unavailable: {type}");
        // IsAssignableFrom on distinct classes calls vm::Class::Init, which
        // materializes vtables/interface offsets without running game ctors.
        // runtime_class_init only runs the managed static constructor.
        var objectClass = Il2CppClassPointerStore<Il2CppSystem.Object>.NativeClassPtr;
        if (pointer != objectClass)
            _ = IL2CPP.il2cpp_class_is_assignable_from(objectClass, pointer);
        // Native class_init does not necessarily materialize each ancestor's
        // lazy method table; the injector walks these tables directly.
        var iterator = IntPtr.Zero;
        while (IL2CPP.il2cpp_class_get_methods(pointer, ref iterator) != IntPtr.Zero) { }
        iterator = IntPtr.Zero;
        IntPtr contract;
        while ((contract = IL2CPP.il2cpp_class_get_interfaces(pointer, ref iterator)) != IntPtr.Zero)
        {
            _ = IL2CPP.il2cpp_class_is_assignable_from(objectClass, contract);
            var methods = IntPtr.Zero;
            while (IL2CPP.il2cpp_class_get_methods(contract, ref methods) != IntPtr.Zero) { }
            _ = IL2CPP.il2cpp_class_is_assignable_from(contract, pointer);
        }
    }

    private static void ValidateInterfaces(Type type)
    {
        if (type.IsAbstract) return;
        foreach (var contract in type.GetInterfaces().Where(i => i.Assembly != type.Assembly &&
                     i != typeof(IIl2CppObjectBase) && typeof(IIl2CppObjectBase).IsAssignableFrom(i)))
        {
            var map = type.GetInterfaceMap(contract);
            for (var index = 0; index < map.TargetMethods.Length; index++)
                if (map.TargetMethods[index].DeclaringType?.IsInterface == true)
                    throw new MissingMethodException($"{type.FullName} does not implement {contract.FullName}.{map.InterfaceMethods[index]}");
        }
    }
}
