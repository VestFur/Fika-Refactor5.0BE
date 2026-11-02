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
        var defaultImplemented = new List<string>();
        var inheritedDescriptors = new List<string>();
        foreach (var type in types)
        {
            ValidateInterfaces(type, defaultImplemented);
            NativeInjectionScope.ValidateDeclaredMethods(type);
        }
        if (defaultImplemented.Count > 0)
            logger.LogWarning($"Interface validation skipped {defaultImplemented.Count} default-implemented member(s) with no native vtable slot: "
                + $"{string.Join(", ", defaultImplemented.Distinct())}. The interop contract supplies their body (the *_Managed helpers and the "
                + "bridges to BCL interface members), so the injector never binds them; every real native slot is still enforced.");
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
                // 保留运行时声明接口的解析器：传入空列表
                // 会丢弃 IBotGame 这类游戏契约。
                ClassInjector.RegisterTypeInIl2Cpp(type);
                if (Il2CppClassPointerStore.GetNativeClassPointer(type) == IntPtr.Zero)
                    throw new InvalidOperationException("Registration returned a null native class");
                if (NativeInjectedGc.PreserveReferences(type))
                    inheritedDescriptors.Add(type.FullName);
                NativeInjectedFinalizer.Register(type);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Fika cannot register {type.FullName} (base {type.BaseType}).", exception);
            }
        }
        if (inheritedDescriptors.Count > 0)
            logger.LogWarning($"Il2CppInterop copied a base class GC descriptor into {inheritedDescriptors.Count} Fika native type(s). "
                + "That descriptor only describes the base class layout, while the injector appends this class's own fields after it, "
                + "so the descriptor was dropped and conservative reference tracing was enabled instead: "
                + string.Join(", ", inheritedDescriptors));
        logger.LogInfo($"Registered {types.Length} Fika native types from {assembly.GetName().Name}.");
        // 每个注入方法的 invoker 都必须是托管 shim，而不只是注册时的 postfix 恰好
        // 看到的那些方法：在注册完成后扫描一遍这些类型
        // （analysis/managed-invoker/REPORT-sweep.md）。该扫描在入口点被校验之前运行，因此被
        // 扫描改写过的方法会以其最终形态接受校验。
        NativeManagedInvoker.Sweep(types, "Fika registration complete");
        // 在拒绝仍可捕获的时候对生成的原生入口点做 JIT 校验
        // （analysis/adjust-shot-vectors/REPORT.md）。
        NativeEntryWarmup.WarmAll(types, logger);
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
        // 对不同类调用 IsAssignableFrom 会触发 vm::Class::Init，它会
        // 实体化 vtable/接口偏移，但不运行游戏构造函数。
        // runtime_class_init 只运行托管静态构造函数。
        var objectClass = Il2CppClassPointerStore<Il2CppSystem.Object>.NativeClassPtr;
        if (pointer != objectClass)
            _ = IL2CPP.il2cpp_class_is_assignable_from(objectClass, pointer);
        // 原生 class_init 不一定会实体化每个祖先的
        // 惰性方法表；注入器会直接遍历这些表。
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

    private static void ValidateInterfaces(Type type, List<string> defaultImplemented)
    {
        if (type.IsAbstract) return;
        foreach (var contract in type.GetInterfaces().Where(i => i.Assembly != type.Assembly &&
                     i != typeof(IIl2CppObjectBase) && typeof(IIl2CppObjectBase).IsAssignableFrom(i)))
        {
            var map = type.GetInterfaceMap(contract);
            for (var index = 0; index < map.TargetMethods.Length; index++)
            {
                if (map.TargetMethods[index].DeclaringType?.IsInterface != true) continue;
                var member = map.InterfaceMethods[index];
                // 只有 interop 的原生槽位存在于游戏元数据中，并且
                // 需要托管实现供注入器绑定。*_Managed 便捷方法
                // 以及指向 BCL 接口成员的私有 final 桥接
                // 都由契约自身默认实现，因此它们只被记录，
                // 并不致命。
                if (IsNativeSlot(contract, member))
                    throw new MissingMethodException($"{type.FullName} does not implement {contract.FullName}.{member}");
                defaultImplemented.Add($"{contract.Name}.{member.Name}");
            }
        }
    }

    /// <summary>
    /// 原生槽位是生成器用 NativeMethodInfoPtr 字段支撑的
    /// public virtual newslot。托管便捷访问器以及指向 BCL 接口的
    /// 显式桥接没有这样的指针，在游戏中也没有
    /// 对应的原生实现。
    /// </summary>
    private static bool IsNativeSlot(Type contract, MethodInfo member)
    {
        if (member.IsStatic || !member.IsPublic || !member.IsVirtual || member.IsFinal ||
            (member.Attributes & MethodAttributes.NewSlot) == 0) return false;
        var prefix = "NativeMethodInfoPtr_" + member.Name + "_";
        return contract.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(field => field.Name.StartsWith(prefix, StringComparison.Ordinal));
    }
}
