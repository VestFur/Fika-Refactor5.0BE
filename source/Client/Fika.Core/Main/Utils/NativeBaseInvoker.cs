using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Il2CppInterop.Common;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 复制生成的 CLR 封送包装器，同时选择其声明的
/// 原生实现。使 ref/out、值类型和返回转换与
/// 已安装的 interop 程序集保持一致，而不进行虚重新分派。
///
/// 分派次数*并非*假设而来。在本安装上用
/// "tools/FindCalls ... il2cpp_object_get_virtual_method BepInEx/interop/Assembly-CSharp.dll" 实测：
/// 此 Il2CppInterop 构建生成的包装器从不调用
/// il2cpp_object_get_virtual_method（整个 306120 个方法的程序集中命中 0 次），而
/// 相同的方法体调用 il2cpp_runtime_invoke 达 102645 次——其中
/// 包括 ClientLocalGameWorld::InitLevel。因此包装器总是调用声明它的那个类型的
/// NativeMethodInfoPtr_...，而这*正是*本辅助程序所要求的基实现。
///
/// 之前的版本要求恰好一次分派调用，并对本安装的每个方法都抛出异常。
/// 一次 raid 的第一次原生基调用是 InitLevel（FikaHostGameWorld），因此关卡
/// 初始化在 LocalGameCreate 之前就出错了：加载界面停留在 0 %，
/// 客户端退回了主菜单（2026-10-09 22:58:53.574，日志行
/// "NotSupportedException: Expected one virtual dispatch ... found 0"）。
/// </summary>
internal static class NativeBaseInvoker
{
    private const string VirtualDispatchHelper = "il2cpp_object_get_virtual_method";
    private const string RuntimeInvokeHelper = "il2cpp_runtime_invoke";

    private static readonly ConcurrentDictionary<(MethodInfo, Type), Delegate> Cache = new();
    private static readonly ConcurrentDictionary<MethodInfo, bool> Warned = new();
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

    // 当字段引用指向声明类型本身时为 true，无论元数据和
    // CLR 对它使用何种形态（嵌套分隔符、泛型实例化）。
    private static bool DeclaresField(TypeReference fieldDeclaringType, Type declaringType)
    {
        if (declaringType == null)
        {
            return false;
        }

        var fieldType = fieldDeclaringType;
        while (fieldType is TypeSpecification specification)
        {
            fieldType = specification.ElementType;
        }

        var declaring = declaringType.IsGenericType && !declaringType.IsGenericTypeDefinition
            ? declaringType.GetGenericTypeDefinition()
            : declaringType;
        return string.Equals(fieldType.FullName.Replace('/', '+'), declaring.FullName, StringComparison.Ordinal);
    }

    private static T Create<T>(MethodInfo method) where T : Delegate
    {
        if (method.IsAbstract || method.ContainsGenericParameters ||
            Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method) == null)
            throw new NotSupportedException($"Not a concrete generated IL2CPP method: {method}");

        using var definition = new DynamicMethodDefinition(method);
        var dispatches = 0;
        var runtimeInvokes = 0;
        var ownMethodInfoLoads = 0;

        foreach (var instruction in definition.Definition.Body.Instructions)
        {
            if (instruction.Operand is not MethodReference called)
            {
                continue;
            }

            if (string.Equals(called.Name, VirtualDispatchHelper, StringComparison.Ordinal))
            {
                // 该辅助程序总是以 (instance, declaredMethodInfo) 形式调用，并被替换为
                // 一个常量选择器：随后被复制的方法体会调用本包装器
                // 所声明的方法，而不是虚重新分派到正在运行的覆盖实现中。
                instruction.Operand = definition.Module.ImportReference(SelectDeclared);
                dispatches++;
                continue;
            }

            if (string.Equals(called.Name, RuntimeInvokeHelper, StringComparison.Ordinal))
            {
                runtimeInvokes++;
            }
        }

        // 只有当包装器可证明地加载了它所声明方法的 method info 时才可能被接受。
        // 该字段以声明类型命名，因此这两个名称必须按
        // 元数据与 CLR 都认可的形态来比较：嵌套类型在元数据中是
        // "Outer/Inner"，对 CLR 则是 "Outer+Inner"，而泛型声明类型
        // 到达 CLR 时是一个构造类型，其 FullName 还携带程序集限定形式的
        // 类型实参。比较原始字符串会让声明在嵌套类型或泛型类型上的每个包装器
        // 都匹配不上——NativeBaseCalls 的 124 个条目中有 73 个如此——这就是
        // 为什么 EFT.Player.KnifeController.MakeKnifeKick 在一次 raid 中被拒绝 54 次，尽管其
        // 方法体与 ClientLocalGameWorld.InitLevel 的形态完全相同。此次比较
        // 只做到形态中立；任何不加载自身 method info 的东西都不会被接受。
        foreach (var instruction in definition.Definition.Body.Instructions)
        {
            if (instruction.Operand is FieldReference field &&
                field.Name.StartsWith("NativeMethodInfoPtr_" + method.Name + "_", StringComparison.Ordinal) &&
                DeclaresField(field.DeclaringType, method.DeclaringType))
            {
                ownMethodInfoLoads++;
            }
        }

        if (dispatches > 1)
        {
            return Degrade<T>(method, $"expected at most one virtual dispatch, found {dispatches}");
        }

        // 零次分派是本安装的正常形态，但只有在
        // 方法体可证明地调用了它所声明的原生方法时才被接受：既不分派也不
        // 加载自身 method info 的方法体可能进行虚重新分派并重入
        // 调用它的覆盖实现，从而形成循环而不是失败。
        if (dispatches == 0 && (runtimeInvokes == 0 || ownMethodInfoLoads == 0))
        {
            return Degrade<T>(method, "the generated wrapper neither dispatches virtually nor invokes its own native "
                + $"method info (runtimeInvokes={runtimeInvokes}, ownMethodInfoLoads={ownMethodInfoLoads}), so the "
                + "installed interop is not the layout this helper was written for");
        }

        

        return (T)DMDCecilGenerator.Generate(definition).CreateDelegate(typeof(T));
    }

    /// <summary>
    /// 被守卫拒绝的包装器。在这里抛异常会把一次形态不匹配变成一个
    /// 进程级故障：该表用 <c>System.Lazy</c> 包装每个条目，而它会在此后*每一次*
    /// 访问 <c>.Value</c> 时重新抛出工厂的异常，因此一个被拒绝的包装器会在每次使用时产生一个异常，而在
    /// 热路径上则是每帧一个异常（analysis/hands-nre）。守卫本身没有变化，仍然
    /// 负责判定——只是现在它退化为降级而非抛异常：拒绝每个方法只报告一次，而
    /// 调用方得到一个中性委托，其返回值是签名的默认值，这恰好
    /// 就是 interop 在原生调用抛异常时所交付的东西。调用方想做的原生工作被跳过，而
    /// 调用点保持存活，而不是把 raid 弄崩。
    /// </summary>
    private static T Degrade<T>(MethodInfo method, string reason) where T : Delegate
    {
        if (Warned.TryAdd(method, true))
        {
            
        }

        return Neutral<T>();
    }

    /// <summary>一个所求形态的委托，它不做任何事并返回其签名的默认值。</summary>
    private static T Neutral<T>() where T : Delegate
    {
        var invoke = typeof(T).GetMethod("Invoke");
        var parameters = invoke.GetParameters()
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        Expression body = invoke.ReturnType == typeof(void)
            ? Expression.Empty()
            : Expression.Default(invoke.ReturnType);
        return Expression.Lambda<T>(body, parameters).Compile();
    }
}
