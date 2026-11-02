using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 清扫：每一个注入的方法 info 都必须落在 shim 上，而不只是注册时
/// 后缀所看到的那些。
///
/// 为什么注册时后缀不够（analysis/managed-invoker/REPORT-sweep.md）：该
/// 后缀依附在 <see cref="NativeInjectionScope"/> 的 Harmony 实例上，而那个实例
/// 会在其作用域被释放时撤销对目标的打补丁（<c>NativeInjectionScope.Dispose</c>）。Fika 自己的
/// 注册早在游戏启动之前就已结束，因此从那一刻起注入器又会用原版的那种
/// invoker（calli 形状、会被 JIT 拒绝）来转换方法：
///
///   * 另一个插件稍后注册的*作用域内*类型（BepInEx 的 <c>Il2CppManagedEnumerator</c> 会在第一次
///     <c>WrapToIl2Cpp()</c> 时惰性注册，而它正是 <c>Fika.Core</c> 的协程
///     辅助类，并且作为本移植版触及的兼容类型它处于作用域内），
///   * 在本类安装之前注册的*作用域内*类型（Fika 自己的 <c>FikaPluginBehaviour</c>），
///   * 作用域外的类型（interop 自己的 delegate holder、ConfigurationManager、SPTushonka）会被
///     <see cref="ReportOutOfScope"/> 枚举并命名一次，但从不被改写——NativeManagedInvoker.cs 中的
///     作用域规则就是那条规矩，它在触碰任何方法 info 之前都会被检查，
///   * 引擎在运行时索要的一个泛型实例化
///     （<c>Hooks.GenericMethod_GetMethod_Hook</c> / <c>..._Unity6_Hook</c> 会
///     直接从原生代码中随时调用 <c>ClassInjector.ConvertMethodInfo</c>）。
///
/// 三张相互独立的网按引擎遇到它们的顺序把这个缺口堵住：
///
///   1. <b>注册钩子</b>（<c>ClassInjector.RegisterTypeInIl2CppInternal</c> 上的 postfix，挂在本类
///      自己的 Harmony 实例上）：在移植版作用域之后注册的类型会在
///      其注册返回时被清扫；
///   2. <b>清扫</b>（<see cref="Sweep"/>）：注入器已知的每个注入类都会通过
///      <c>il2cpp_class_get_methods</c> 被枚举，每个仍使用原版 invoker 的方法 info 都会被收养；
///      它在安装时运行（在 Fika 自己的注册之后），并在引擎首次遇到一个
///      shim 不认识的方法 info 时再运行一次；
///   3. <b>运行时守卫</b>（<c>IL2CPP.il2cpp_runtime_invoke</c> 上的 prefix，见
///      <see cref="EnsureAdopted"/>）：原生侧解引用 <c>InvokerMethod</c> 之前的最后
///      时刻——未知的方法 info 会在那里被收养，而无论哪种情况，原版 invoke 都会
///      照常运行，不做改动。
///
/// 这里什么都不抛，也什么都不改变一次调用的含义：它要么替换一个引擎
/// 尚未进入的入口，要么写下一行诊断。
/// </summary>
internal static unsafe partial class NativeManagedInvoker
{
    private sealed class ClassEntry
    {
        internal Type Type;
        internal nint Pointer;
    }

    /// <summary>本类见过的注入类型，来自注册钩子、清扫或 Fika 自身。</summary>
    private static readonly ConcurrentDictionary<Type, byte> KnownTypes = new();

    /// <summary>原生类指针到它被注册时所用的那个托管类型。</summary>
    private static readonly ConcurrentDictionary<nint, Type> TypesByClass = new();

    /// <summary>全名到托管类型，供注入器自己的注册表使用（后者只存名字）。</summary>
    private static readonly ConcurrentDictionary<string, Type> TypesByName = new();

    private static int _sweepRewritten;
    private static int _sweepKept;
    private static int _sweepEnumerated;
    private static int _sweepAlreadyShim;
    private static int _sweepNoInvoker;
    private static int _sweepTypes;
    private static int _sweepUnresolvedNames;
    private static int _vtableSlots;
    private static int _vtableStockInvokers;
    private static int _vtableRepointed;
    private static int _runtimeRepairs;
    private static int _runtimeSweep;
    private static int _unresolvedReports;
    private static int _scopeForeignTypes;

    /// <summary>
    /// 供正在清扫的线程使用的重入守卫。诊断会写一个帧号，而它是一次
    /// il2cpp 回调，会到达运行时守卫，否则守卫就会在第一次清扫
    /// 内部启动第二次清扫。每个线程一次只做一次清扫就够了：内层那次会枚举
    /// 相同的类。
    /// </summary>
    [ThreadStatic]
    private static bool _sweeping;

    /// <summary>记住一个注入类型。由注册钩子在类型被清扫之前调用。</summary>
    internal static void RememberType(Type type)
    {
        if (type == null) return;
        try
        {
            KnownTypes.TryAdd(type, 0);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 清扫每一个注入类：枚举它的方法 info，收养仍停留在原版
    /// invoker 上的那些，并报告 vtable 槽位里装的是什么。即使注入器的注册表
    /// 无法读取，<paramref name="explicitTypes"/> 也会被清扫。
    /// </summary>
    internal static void Sweep(Type[] explicitTypes, string reason)
    {
        if (_sweeping) return;
        _sweeping = true;
        try
        {
            var classes = CollectClasses(explicitTypes, out var unresolved);
            var enumerated = 0;
            var rewritten = 0;
            var kept = 0;
            var noInvoker = 0;
            var already = 0;
            foreach (var entry in classes)
                SweepClass(entry.Type, entry.Pointer, reason, ref enumerated, ref rewritten, ref kept, ref noInvoker, ref already);

            Interlocked.Add(ref _sweepEnumerated, enumerated);
            Interlocked.Add(ref _sweepRewritten, rewritten);
            Interlocked.Add(ref _sweepKept, kept);
            Interlocked.Add(ref _sweepAlreadyShim, already);
            Interlocked.Add(ref _sweepNoInvoker, noInvoker);
            Interlocked.Add(ref _sweepTypes, classes.Count);
            Interlocked.Add(ref _sweepUnresolvedNames, unresolved);

            
            
        }
        catch (Exception exception)
        {
            
        }
        finally
        {
            _sweeping = false;
        }
    }

    /// <summary>
    /// 清扫某一个类型下的注入类型。在一次注册之后立即调用，因此当移植版自己的
    /// 注册作用域早已消失时出现的类型，会在它出现的那一刻就被覆盖。
    /// </summary>
    internal static void SweepType(Type type, string reason)
    {
        if (_sweeping) return;
        _sweeping = true;
        try
        {
            if (type == null) return;
            RememberType(type);
            var pointer = ClassPointerOf(type);
            if (pointer == 0) return;
            TypesByClass[pointer] = type;
            var enumerated = 0;
            var rewritten = 0;
            var kept = 0;
            var noInvoker = 0;
            var already = 0;
            SweepClass(type, pointer, reason, ref enumerated, ref rewritten, ref kept, ref noInvoker, ref already);
            Interlocked.Add(ref _sweepEnumerated, enumerated);
            Interlocked.Add(ref _sweepRewritten, rewritten);
            Interlocked.Add(ref _sweepKept, kept);
            Interlocked.Add(ref _sweepAlreadyShim, already);
            Interlocked.Add(ref _sweepNoInvoker, noInvoker);
            if (rewritten != 0 || kept != 0)
            {
                
            }
        }
        catch (Exception exception)
        {
            
        }
        finally
        {
            _sweeping = false;
        }
    }

    /// <summary>
    /// 完整清扫一次，在引擎首次遇到 shim 不认识的方法 info 时执行。
    /// 按设计是有界的：第一次调用之后它什么也不做。
    /// </summary>
    internal static void SweepOnce(string reason)
    {
        // 本线程已在清扫时不执行，也不执行两次：令牌只在真正
        // 尝试清扫时才会被消耗。
        if (_sweeping) return;
        if (Interlocked.Exchange(ref _runtimeSweep, 1) != 0) return;
        Sweep(null, reason);
    }

    /// <summary>
    /// 收养一个 shim 从未见过的方法 info：从方法 info 本身（它所属的声明类和
    /// 名字）解析出托管方法，并为它构建一个计划。当该方法 info 现在
    /// 由 shim 提供服务时返回 true——无论它本来就已如此，还是刚刚才被收养。
    /// </summary>
    internal static bool EnsureAdopted(Il2CppMethodInfo* info, string origin, string detail)
    {
        var key = (nint)info;
        if (key == 0) return false;
        try
        {
            if (Plans.ContainsKey(key)) return true;
            if (NotOurs.ContainsKey(key)) return false;
            var wrapper = UnityVersionHandler.Wrap(info);
            if (wrapper == null) return false;
            if (!IsInjectedMethodInfo(wrapper))
            {
                // 引擎自己的方法 info 永远不归我们碰；把这一点记住，好让守卫
                // 在之后每次 invoke 时仍然只是一次字典查找。
                NotOurs[key] = 0;
                return false;
            }

            if (wrapper.InvokerMethod == 0)
            {
                // 抽象方法与泛型定义：注入器根本不会绑定 invoker。
                NotOurs[key] = 0;
                return false;
            }

            var declaringType = TypeOfClass(wrapper);
            if (declaringType != null && !IsShimScope(declaringType))
            {
                // 运行时守卫自己的作用域闸门：该方法 info 属于另一个插件，所以
                // 注入器的 invoker 保持不变。与其它所有"不是我们的"结论一样被记忆，好让守卫
                // 在 invoke 路径上仍然只是一次字典查找。
                NotOurs[key] = 0;
                ReportOutOfScope(declaringType, origin);
                return false;
            }

            var name = Marshal.PtrToStringUTF8(wrapper.Name);
            var method = ResolveManagedMethod(declaringType, name, wrapper);
            if (method == null)
            {
                ReportUnresolved(key, declaringType, name, wrapper, origin);
                return false;
            }

            if (!string.Equals(origin, "registration", StringComparison.Ordinal))
                Interlocked.Increment(ref _runtimeRepairs);
            return TryAdopt(info, method, origin, detail, out _);
        }
        catch (Exception exception)
        {
            NotOurs[key] = 0;
            
            return false;
        }
    }

    private static void ReportUnresolved(nint key, Type declaringType, string name, INativeMethodInfoStruct info, string origin)
    {
        try
        {
            if (Interlocked.Increment(ref _unresolvedReports) > 32) return;
            var native = declaringType?.FullName ?? NativeFullName(UnityVersionHandler.Wrap(info.Class));
            
        }
        catch (Exception)
        {
        }
    }

    // ------------------------------------------------------------------ 枚举

    private static List<ClassEntry> CollectClasses(Type[] explicitTypes, out int unresolved)
    {
        var result = new List<ClassEntry>();
        var seen = new HashSet<nint>();
        var missing = 0;

        void Consider(Type type)
        {
            if (type == null) return;
            RememberType(type);
            var pointer = ClassPointerOf(type);
            if (pointer == 0) return;
            if (!seen.Add(pointer)) return;
            TypesByClass[pointer] = type;
            result.Add(new ClassEntry { Type = type, Pointer = pointer });
        }

        foreach (var type in KnownTypes.Keys)
        {
            try
            {
                Consider(type);
            }
            catch (Exception)
            {
            }
        }

        if (explicitTypes != null)
        {
            foreach (var type in explicitTypes)
            {
                try
                {
                    Consider(type);
                }
                catch (Exception)
                {
                }
            }
        }

        foreach (var name in InjectorTypeNames())
        {
            var type = ResolveTypeByName(name);
            if (type == null)
            {
                missing++;
                continue;
            }

            try
            {
                Consider(type);
            }
            catch (Exception)
            {
            }
        }

        unresolved = missing;
        return result;
    }

    /// <summary>
    /// 注入器自己的已注册类型名注册表。它是唯一也持有本类安装之前所注册
    /// 类型的来源，也是唯一能在不做原生调用的情况下把它们的名字给出来的
    /// 来源。
    /// </summary>
    private static string[] InjectorTypeNames()
    {
        try
        {
            var field = AccessTools.Field(typeof(ClassInjector), "InjectedTypes");
            if (field?.GetValue(null) is not IEnumerable<string> names) return Array.Empty<string>();
            var copy = new List<string>();
            // 该集合属于注入器且非线程安全：一次并行进行的注册可能让这次枚举
            // 抛出异常，这就是它既有界又被捕获的原因。
            var enumerator = names.GetEnumerator();
            while (true)
            {
                try
                {
                    if (!enumerator.MoveNext()) break;
                }
                catch (Exception)
                {
                    break;
                }

                if (enumerator.Current != null) copy.Add(enumerator.Current);
            }

            return copy.ToArray();
        }
        catch (Exception exception)
        {
            
            return Array.Empty<string>();
        }
    }

    private static void SweepClass(Type type, nint pointer, string reason, ref int enumerated, ref int rewritten, ref int kept,
        ref int noInvoker, ref int already)
    {
        // 作用域闸门（NativeManagedInvoker，"shim 的作用域"）：不属于 Fika 的类会
        // 完全保持注入器构建时的样子——包括它的方法 info *和* 它的 vtable 槽位，因为
        // 下面的扫描会重定向槽位，那同样是对另一个插件调用路径的改动。
        if (!IsShimScope(type))
        {
            if (ReportOutOfScope(type, reason)) Interlocked.Increment(ref _scopeForeignTypes);
            return;
        }

        var shim = ShimPointer();
        var iterator = nint.Zero;
        while (true)
        {
            nint method;
            try
            {
                method = IL2CPP.il2cpp_class_get_methods(pointer, ref iterator);
            }
            catch (Exception exception)
            {
                
                break;
            }

            if (method == 0) break;
            enumerated++;
            try
            {
                var info = UnityVersionHandler.Wrap((Il2CppMethodInfo*)method);
                if (info == null) continue;
                if (Plans.ContainsKey(method))
                {
                    already++;
                    continue;
                }

                if (info.InvokerMethod == 0)
                {
                    // 抽象方法与泛型定义：注入器不绑定 invoker，所以没有
                    // 可替换的原版条目。*已膨胀*的泛型方法是另一个不同的方法
                    // info，由注入器自己的钩子创建，并通过注册钩子和
                    // 运行时守卫到达 shim。
                    noInvoker++;
                    continue;
                }

                var name = Marshal.PtrToStringUTF8(info.Name);
                if (string.Equals(name, "Finalize", StringComparison.Ordinal) && info.InvokerMethod != shim)
                {
                    // 引擎在原生侧运行终结器，而没有可调用的托管方法
                    // （C# 的终结器就是析构函数），所以收养它只会每次垃圾回收产生一条
                    // "Handle is not initialized"。它保留注入器的 invoker。
                    kept++;
                    
                    continue;
                }

                var mono = ResolveManagedMethod(type, name, info);
                if (mono == null)
                {
                    kept++;
                    
                    continue;
                }

                if (TryAdopt((Il2CppMethodInfo*)method, mono, "sweep:" + reason, null, out _)) rewritten++;
                else kept++;
            }
            catch (Exception exception)
            {
                kept++;
                
            }
        }

        ScanVTable(type, pointer, ref enumerated, ref rewritten, ref kept);
    }

    /// <summary>
    /// 报告该类的 vtable 里装的是什么。一个槽位装着*方法 info* 加上直接调用所用的
    /// 入口，所以替换该方法 info 的 invoker 才是修复槽位的办法；而装着
    /// invoker 本身的槽位——反射 ABI，五个参数——会被重定向到该方法自己的
    /// trampoline，也就是注入器在注册期间写入那里的那个指针。
    /// </summary>
    private static void ScanVTable(Type type, nint pointer, ref int enumerated, ref int rewritten, ref int kept)
    {
        try
        {
            var klass = UnityVersionHandler.Wrap((Il2CppClass*)pointer);
            if (klass == null) return;
            var slots = (VirtualInvokeData*)klass.VTable;
            if (slots == null) return;
            var count = (int)klass.VtableCount;
            var stock = 0;
            var repointed = 0;
            for (var i = 0; i < count; i++)
            {
                var method = (nint)slots[i].method;
                if (method == 0) continue;
                var info = UnityVersionHandler.Wrap((Il2CppMethodInfo*)method);
                if (info == null) continue;
                var invoker = info.InvokerMethod;
                if (invoker == 0 || Plans.ContainsKey(method)) continue;
                if (!IsInjectedMethodInfo(info)) continue;
                stock++;
                var name = Marshal.PtrToStringUTF8(info.Name);
                var pointedAtInvoker = slots[i].methodPtr != 0 && slots[i].methodPtr == invoker;
                if (pointedAtInvoker && info.MethodPointer != 0)
                {
                    slots[i].methodPtr = info.MethodPointer;
                    repointed++;
                }

                var adopted = EnsureAdopted((Il2CppMethodInfo*)method, "vtable", "slot " + i + " of " + type.FullName);
                if (adopted) rewritten++;
                else kept++;
                
            }

            Interlocked.Add(ref _vtableSlots, count);
            Interlocked.Add(ref _vtableStockInvokers, stock);
            Interlocked.Add(ref _vtableRepointed, repointed);
            if (stock != 0)
            {
                
            }
        }
        catch (Exception exception)
        {
            
        }
    }

    // ------------------------------------------------------------------ 解析

    private static nint ClassPointerOf(Type type)
    {
        try
        {
            var pointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
            if (pointer == 0) return 0;
            return RuntimeSpecificsStore.IsInjected(pointer) ? pointer : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static bool IsInjectedMethodInfo(INativeMethodInfoStruct info)
    {
        try
        {
            return info.Class != null && RuntimeSpecificsStore.IsInjected((nint)info.Class);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Type TypeOfClass(INativeMethodInfoStruct info)
    {
        try
        {
            var pointer = (nint)info.Class;
            if (pointer == 0) return null;
            if (TypesByClass.TryGetValue(pointer, out var known) && known != null) return known;
            var klass = UnityVersionHandler.Wrap(info.Class);
            if (klass == null) return null;
            var fullName = NativeFullName(klass);
            var type = ResolveTypeByName(fullName);
            if (type == null)
            {
                // 注入器只写入注入类型的简单名和命名空间
                // （ClassInjector.decompiled.cs:439-440），所以嵌套类型在这里读起来就像
                // 顶级类型。用这个形状去匹配本类确实知道的那些类型。
                type = ResolveTypeByFlattenedName(fullName);
            }

            if (type != null)
            {
                TypesByClass[pointer] = type;
                RememberType(type);
            }

            return type;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string NativeFullName(INativeClassStruct klass)
    {
        try
        {
            if (klass == null) return string.Empty;
            var name = Marshal.PtrToStringUTF8(klass.Name) ?? string.Empty;
            var declaring = klass.DeclaringType;
            if (declaring != null)
            {
                var outer = NativeFullName(UnityVersionHandler.Wrap(declaring));
                return string.IsNullOrEmpty(outer) ? name : outer + "+" + name;
            }

            var space = Marshal.PtrToStringUTF8(klass.Namespace);
            return string.IsNullOrEmpty(space) ? name : space + "." + name;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static Type ResolveTypeByName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return null;
        if (TypesByName.TryGetValue(fullName, out var cached)) return cached;
        Type found = null;
        try
        {
            try
            {
                found = Type.GetType(fullName, throwOnError: false);
            }
            catch (Exception)
            {
                found = null;
            }

            if (found == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        found = assembly.GetType(fullName, throwOnError: false);
                    }
                    catch (Exception)
                    {
                        found = null;
                    }

                    if (found != null) break;
                }
            }
        }
        catch (Exception)
        {
        }

        if (found != null) TypesByName[fullName] = found;
        return found;
    }

    private static Type ResolveTypeByFlattenedName(string flattened)
    {
        if (string.IsNullOrEmpty(flattened)) return null;
        try
        {
            foreach (var type in KnownTypes.Keys)
            {
                if (type.FullName != null && string.Equals(type.FullName.Replace('+', '.'), flattened, StringComparison.Ordinal))
                {
                    TypesByName[flattened] = type;
                    return type;
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static MethodInfo ResolveManagedMethod(Type type, string name, INativeMethodInfoStruct info)
    {
        if (type == null || string.IsNullOrEmpty(name)) return null;
        var isStatic = (info.Flags & Il2CppMethodFlags.METHOD_ATTRIBUTE_STATIC) != 0;
        var count = (int)info.ParametersCount;
        var candidates = new List<MethodInfo>();
        for (var scope = type; scope != null && scope != typeof(object); scope = scope.BaseType)
        {
            try
            {
                foreach (var method in scope.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != name) continue;
                    if (method.IsStatic != isStatic) continue;
                    if (method.GetParameters().Length != count) continue;
                    candidates.Add(method);
                }
            }
            catch (Exception)
            {
            }

            if (candidates.Count != 0) break;
        }

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];
        foreach (var candidate in candidates)
        {
            if (ParametersMatchNative(candidate, info)) return candidate;
        }

        return candidates[0];
    }

    /// <summary>
    /// 把一个候选的参数列表与方法 info 的原生签名作比较，做法与
    /// <c>ClassInjector.FindOverrideByName</c> 区分重写的方式相同。
    /// </summary>
    private static bool ParametersMatchNative(MethodInfo method, INativeMethodInfoStruct info)
    {
        var parameters = method.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            nint native;
            try
            {
                native = IL2CPP.il2cpp_class_from_il2cpp_type((nint)UnityVersionHandler.Wrap(info.Parameters, i).ParameterType);
            }
            catch (Exception)
            {
                return false;
            }

            var managed = parameters[i].ParameterType;
            if (managed.IsByRef) managed = managed.GetElementType();
            nint expected;
            try
            {
                expected = Il2CppClassPointerStore.GetNativeClassPointer(managed);
            }
            catch (Exception)
            {
                continue;
            }

            if (expected == 0 || native == 0) continue;
            if (expected != native) return false;
        }

        return true;
    }
}
