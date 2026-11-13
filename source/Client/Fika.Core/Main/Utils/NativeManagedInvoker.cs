using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 用一个纯托管的 shim 替换 Il2CppInterop 为某个注入方法生成的 invoker。
///
/// 起因：生成的 invoker 方法体以一次间接调用（<c>calli</c>，ClassInjector.decompiled.cs:1469）结束，
/// 目标是该方法自身的 trampoline thunk。CoreCLR 恰好把这种组合——托管代码
/// 调用 reverse P/Invoke / UnmanagedCallersOnly 入口——视为不可捕获的 fail fast
/// （0x80131506，“Invalid Program: attempted to call a UnmanagedCallersOnly method from managed code”，
/// coreclr+0x2C7085 位于 JIT_ReversePInvokeEnterRare 之后，coreclr+0x19BBD6），它杀掉了
/// 2026-10-10 的主机突袭（analysis/crash-clr、analysis/injected-invoker-fix、analysis/managed-invoker）。
/// 该 calli 无法在方法体内部修复（改写已被证明产生字节完全相同的
/// 代码），所以本类改为替换 *入口*：把 <c>Il2CppMethodInfo::InvokerMethod</c> 指向
/// <see cref="Shim"/>，即使用同一五参数 ABI 的托管静态方法——这正是
/// 注入器对构造函数已经信任的构造（StaticVoidIntPtrInvoker_MetadataV29，
/// ClassInjector.decompiled.cs:1541-1544）。
///
/// 覆盖面：shim 能为任意参数个数复现生成 invoker 的转换（引用、
/// 指针、基元、枚举、按值结构体，以及以上所有类型的 by-ref/out），也能复现
/// void / 引用 / 指针 / 基元 / 枚举 / 结构体返回值的转换。凡是无法转换的，都保留
/// 注入器自己的 invoker（现状），并在诊断中以一条带原因的记录点名。
///
/// 范围：shim 只覆盖 *Fika 自己注入的类型*——第 10 轮那条“每个注入类的每个
/// method info”不变量已被撤回，因为它同时也改写了其他插件的类型（见下面的
/// 范围区域）。在该范围内该不变量成立：shim 能服务的每个 Fika 类的每个
/// method info 都由它服务，不论那条路径如何找到该 method info。仅靠注册无法
/// 做到这点——注入器在运行时还会从它自己的原生泛型钩子转换方法，而
/// 本端口自身协程辅助所需的那层协程包装器，是在端口的
/// 注册作用域消失之后才注册的。堵住这些路径的扫描、注册钩子与运行时守卫
/// 在 NativeManagedInvoker.Sweep.cs 中描述。
///
/// 三条硬性规则，按此顺序：
///   1. 注册绝不能因为本类而失败（每个入口点都被包裹）；
///   2. callout 必须始终正常返回——异常绝不能穿回 il2cpp；
///   3. 无法精确复现的形状不改写（并且会说明）。
///
/// 参数 ABI 不是推断出来的：它是从所安装构建的 interop 包装器中读出的
/// （analysis/managed-invoker/arg-abi.txt），这些包装器就是 <c>il2cpp_runtime_invoke</c> 的调用者：
///   * 引用类型（string / Il2CppObjectBase / 接口）：
///     <c>ldloc args; ldarg.n; call Il2CppObjectBaseToPtr|ManagedStringToIl2Cpp; stind.i</c>
///     -> 槽位持有对象指针本身（EFT.BaseLocalGame`1::SessionRun 的 IL_0013 处）；
///   * 指针类型（<c>T*</c>）：<c>ldloc args; ldarg.n; stind.i</c>
///     -> 槽位持有指针本身（AreaLight::FillShadowCubePlanes 的 IL_001F 处）；
///   * by-ref（<c>ref</c>/<c>out</c>）：<c>ldloc args; ldarg.n; conv.i; stind.i</c>
///     -> 槽位持有该存储的地址（EFT.MovementContext::Flash 的 IL_0014 处）；
///   * 按值 value type（基元 / 枚举 / 结构体）：<c>ldloc args; ldarga.s p; stind.i</c>
///     -> 槽位持有该存储的地址（AmplifyMotionEffectBase::CreateRenderTexture 的 IL_0026 处）；
/// 而注入器自己的 invoker 恰好在原生类型不是 <c>nint</c> 时对槽位解引用
/// （ClassInjector.decompiled.cs:1451-1459，TrampolineHelpers.NativeType），这与上面的划分一致。
/// </summary>
internal static unsafe partial class NativeManagedInvoker
{
    /// <summary>
    /// 持有本类各个钩子的 Harmony 实例。有意 *不* 使用
    /// <see cref="NativeInjectionScope"/> 的实例：那个实例在其作用域被释放时会逐个
    /// 反补丁它的目标（<c>NativeInjectionScope.Dispose</c>），从而悄悄移除了
    /// <c>ConvertMethodInfo</c> 后置补丁，而注入器仍在转换此后注册的
    /// 方法——这正是本类要补上的缺口（analysis/managed-invoker/REPORT-sweep.md）。
    /// 独立的 owner id 不会被另一个作用域的 dispose 反补丁。
    /// </summary>
    internal const string OwnHarmonyId = "com.fika.core.managed-invoker";

    private static Harmony _hooks;

    /// <summary>
    /// 无需再看第二眼的 method info：引擎自身的（永远不归我们处理），以及没有绑定
    /// invoker 的（抽象方法、泛型定义）。只有运行时守卫读取它，因此一次
    /// invoke 绝不会为解析付出两次代价。
    /// </summary>
    private static readonly ConcurrentDictionary<nint, byte> NotOurs = new();

    /// <summary>
    /// 在 metadata v29+ 下 il2cpp 调用每个注入方法时使用的入口形状
    /// （<c>InvokerDelegateMetadataV29</c>，ClassInjector.decompiled.cs:31）。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void InvokerShim(nint methodPointer, Il2CppMethodInfo* methodInfo, nint obj, nint* args,
        nint* returnValue);

    /// <summary>静态字段就是 GC 根：导出指针背后的委托得以存活。</summary>
    private static readonly InvokerShim ShimDelegate = Shim;

    /// <summary>在校验 codec 期间为结构体往返预留的字节数。</summary>
    private const int ScratchBytes = 256;

    private static nint _shimPointer;
    private static nint _scratch;
    private static bool _unavailable;
    private static bool _installed;
    private static int _shimCount;
    private static int _stockCount;
    private static int _summaryReported;
    private static MethodInfo _reportTrampolineException;

    /// <summary>Il2CppMethodInfo* -> shim 运行该托管方法所需的一切。</summary>
    private static readonly ConcurrentDictionary<nint, Plan> Plans = new();

    /// <summary>
    /// 针对“callout 拿到的 MethodInfo 不是本类填充的那个结构体”的安全网：il2cpp 可能会传递它的
    /// *副本*。方法指针（每个注入方法一个 thunk）标识的是同一个 plan。
    /// 这里只包含被改写过的指针。
    /// </summary>
    private static readonly ConcurrentDictionary<nint, Plan> PlansByMethodPointer = new();

    /// <summary>已写入 warm 表的指针，好让日志中每个方法只留一行。</summary>
    private static readonly ConcurrentDictionary<nint, byte> Reported = new();

    /// <summary>不可在每次调用时重复的诊断（它们运行在引擎线程上）。</summary>
    private static readonly ConcurrentDictionary<nint, byte> ReportedAtRuntime = new();

    /// <summary>每个托管类型的转换 codec（<see cref="Codec.Read"/> 为 null 表示“不支持”）。</summary>
    private static readonly ConcurrentDictionary<Type, Codec> Codecs = new();

    private static readonly Codec Unsupported = new Codec { Kind = "unsupported" };

    private enum SlotKind
    {
        Reference,
        Pointer,
        Value,
        ByRef,
    }

    /// <summary>一个参数如何从其槽位读取（对 by-ref 而言，还包括写回）。</summary>
    private sealed class ArgumentPlan
    {
        internal string Name;
        internal string Type;
        internal SlotKind Slot;
        internal Func<nint, object> Read;
        internal Action<nint, object> WriteBack;
    }

    private sealed class ReturnPlan
    {
        internal bool IsVoid;
        internal Action<object, nint> Write;
    }

    private sealed class Plan
    {
        internal MethodInfo Target;
        internal string Name;
        internal bool Static;
        internal ArgumentPlan[] Arguments;
        internal ReturnPlan Return;

        internal object[] NewArguments() =>
            Arguments.Length == 0 ? Array.Empty<object>() : new object[Arguments.Length];
    }

    /// <summary>从某个地址读取托管值，并将其写回该地址。</summary>
    private sealed class Codec
    {
        internal string Kind;
        internal Func<nint, object> Read;
        internal Action<nint, object> Write;
        internal object Default;
    }

    // ------------------------------------------------------------------ shim 的作用域
    //
    // shim 替换引擎通过反射到达的入口（Il2CppMethodInfo::InvokerMethod），适用于
    // 注入器所服务的每个注入方法。在本轮之前，那是 *每个插件* 的 *每个* 注入
    // 方法：注册钩子对注入器转换的任何对象都会触发，而扫描
    // 会枚举它能叫出名字的每个注入类。第 10 轮某测试者的日志显示了这样做的代价——
    // 扫描覆盖了 ArmorClassIcon.RefreshBehaviour，而引擎通过 Fika 的托管
    // shim 到达了 AmandsGraphics.AmandsGraphicsClass.Awake/.Start/.Update，即某个第三方
    // 模组的引擎回调被导流进了本端口的反射路径。这是本端口引入却从未
    // 需要过的兼容面：另一个插件的注入方法由 *它自己* 的代码路径进入，
    // 而注入器为它生成的 invoker 正是其作者编写并测试过的入口
    // （analysis/managed-invoker/REPORT-sweep.md 是第 10 轮扩大了覆盖面的那份
    // 文档；本区域把它重新收窄）。
    //
    // 因此规则是一道作用域，在进入 shim 的每一道门口都会检查（TryAdopt 是权威
    // 的那一个；注册钩子、扫描与运行时守卫都会经过它，或先检查
    // 它）。三个层级，按此顺序：
    //
    //   1. own ............ 该类型属于 Fika：以 "Fika.*" 开头的程序集、"Fika.Core" 命名空间，或
    //                       OwnGlobalNamespaceTypes 中的一个全局命名空间 Fika UI 类型（每一项
    //                       都写明了它为何在此）；
    //   2. port-reached ... 一个非 Fika 类型，Fika *自己* 的运行时路径会通过反射到达它，且
    //                       注入器的 invoker 经实测对该类型是致命的（PortReachedTypes，每一项
    //                       对应一个实测用例）。开关：ScopePortReachedKey，默认开启；
    //   3. out of scope ... 其他所有类型。它们的方法与其 vtable 槽位完全按
    //                       注入器构建的样子保留，且该类型会在诊断中被点名一次。
    //
    // 两个开关可为需要旧行为的测试者扩大作用域，两者都需要重启
    // （shim 改写了什么每个进程只决定一次，且无法撤销改写）：
    // ScopeEverythingKey 整体恢复第 10 轮的覆盖面，ScopeExtraNamesKey 按名字添加
    // 单个类型，这样某个特定模组可以被覆盖而不必覆盖全部。
    //
    // Fika 自身的类型完全不受这一切影响：本地一次运行所注册的 141 个类型中有 135 个
    // 声明在 Fika.Core.dll 中（analysis/shim-scope/injected-types-by-assembly.txt），而
    // 不在其中的那六个，就是 PortReachedTypes 中列出或被报告为保留的第三方类型。

    /// <summary>作用域开关所属的配置节；它们与诊断开关放在一起。</summary>
    internal const string ScopeConfigSection = "Diagnostics";

    /// <summary>
    /// 全量开关。为 true 时恢复第 10 轮的行为（注入器所服务的每个注入类型都会被
    /// 改写，不论由哪个插件注册）。false——默认值——则让第三方类型继续使用
    /// 注入器为它们生成的 invoker。
    /// </summary>
    internal const string ScopeEverythingKey = "Compat: Shim Third-Party Injected Types";

    /// <summary><see cref="PortReachedTypes"/> 兼容列表的开关（默认开启）。</summary>
    internal const string ScopePortReachedKey = "Compat: Shim Port-Reached Third-Party Types";

    /// <summary>运维逃生口：要额外改写的类型全名，以逗号分隔（默认为空）。</summary>
    internal const string ScopeExtraNamesKey = "Compat: Additional Shimmed Type Names";

    /// <summary>Fika 自身各程序集的程序集名前缀（Fika.Core、Fika.Headless、……）。</summary>
    private const string OwnAssemblyPrefix = "Fika.";

    /// <summary>所有未声明在全局命名空间中的 Fika 类型所用的命名空间。</summary>
    private const string OwnNamespace = "Fika.Core";

    /// <summary>
    /// Fika 自身那些位于 *全局* 命名空间的注入类型——在那里，程序集规则与命名空间规则都无法
    /// 仅凭形状叫出它们的名字。每一项都写明了它被列出的原因。它们声明于
    /// Fika.Core.dll——已用 analysis/shim-scope/scan-injected-types.ps1 对所安装的构建
    /// 做过核对（见 analysis/shim-scope/injected-types-by-assembly.txt）。
    /// 既不在此表中、又不是 Fika.Core 类型的类型，保留注入器的 invoker。
    /// </summary>
    private static readonly Dictionary<string, string> OwnGlobalNamespaceTypes = new(StringComparer.Ordinal)
    {
        { "LoadingScreenUI", "Fika's loading screen: the client/server callbacks drive it (LoadingScreenUI.Instance.AddPlayer/SetProgress) and the engine enters its Awake/OnDestroy" },
        { "LoadingScreenPlayer", "the per-player row that screen instantiates; the engine drives its lifecycle" },
        { "MainMenuUI", "the Fika panel Fika injects into EFT's main menu; engine-driven Awake/OnDestroy" },
        { "MainMenuUIPlayer", "the animated player plate of the main-menu decoration" },
        { "MatchMakerUI", "the Fika matchmaker panel; engine-driven Awake/Update/OnDestroy" },
        { "PlayerPlateUI", "the plate shown next to a player while the raid loads" },
        { "SendItemUI", "the Fika 'send item to player' dialog" },
        { "ListPlayer", "one row of the Fika player list" },
        { "IntUpDown", "the numeric stepper the Fika admin/limit windows use" },
        { "ButtonHandler", "the Fika menu button; it drives its own canvas by coroutine" },
        { "DebugUI", "Fika's debug overlay" },
        { "AdminSettingsUI", "the headless/admin settings window (no injectable member today; listed so a future one is covered)" },
        { "AdminSettingsUIScript", "the script of that window" },
        { "FikaChatUI", "the Fika chat window (no injectable member today)" },
        { "FreecamUI", "the free-camera overlay (no injectable member today)" },
        { "RaidAdminUI", "the raid-admin window (no injectable member today)" },
    };

    /// <summary>
    /// Fika *自己* 的运行时路径通过反射到达的非 Fika 类型，且注入器的 invoker
    /// 经实测对它们是致命的。一项就是一个实测用例，绝不是笼统的“这个模组看起来有用”：
    /// 只有在观察结果点明了到达它的那个 Fika 帧时，才允许新增一项。
    /// <see cref="ScopePortReachedKey"/> 开关可整表关闭。
    /// </summary>
    private static readonly Dictionary<string, string> PortReachedTypes = new(StringComparer.Ordinal)
    {
        {
            "BepInEx.Unity.IL2CPP.Utils.Collections.Il2CppManagedEnumerator",
            "the carrier of Fika's own coroutines: EngineCoroutines.Wrap(...).WrapToIl2Cpp() hands them to the engine, "
            + "BepInEx registers this type lazily inside that first Wrap - i.e. after the port's registration scope is gone - "
            + "and the engine enters get_Current/MoveNext/Reset through the invoker. Round 10 measured the fatal shape exactly "
            + "there (Invoker_IntPtrThis, IL2CPP-to-Managed trampoline; LogOutput.log:1791-1806, "
            + "4745/7791/18405, and the round-11 session entered get_Current 954 times through this shim)"
        },
    };

    /// <summary>解析后的作用域策略，在注入器安装期间绑定一次（见 <see cref="ConfigureScope"/>）。</summary>
    private static bool _scopeEverything;
    private static bool _scopePortReached = true;
    private static string[] _scopeExtraNames = Array.Empty<string>();

    /// <summary>“保留在作用域外”一行已写出的类型（每个类型一行，绝不按方法）。</summary>
    private static readonly ConcurrentDictionary<string, byte> ReportedOutOfScope = new();

    /// <summary>
    /// 把三个作用域开关绑定到插件的配置文件中，并记录解析后的策略。
    /// 在注入器安装期间调用，即在对第一个类型注册之前：shim 改写什么
    /// 每个进程只决定一次，因此改动开关需要重启（已经采纳的 invoker 无法
    /// 被取消采纳）。绝不抛出——失败时内置的收窄规则继续生效。
    /// </summary>
    private static void ConfigureScope()
    {
        try
        {
            var config = FikaPlugin.Instance?.Config;
            if (config == null)
            {
                
                return;
            }

            _scopeEverything = config.Bind(ScopeConfigSection, ScopeEverythingKey, false, new ConfigDescription(
                "Also rewrite the invoker of third-party injected types (the round-10 behaviour). Off: another plugin's "
                + "engine callbacks stay on the invoker the injector generated for them. Needs a restart.")).Value;
            _scopePortReached = config.Bind(ScopeConfigSection, ScopePortReachedKey, true, new ConfigDescription(
                "Keep the shim on the non-Fika types Fika's own runtime path reaches; every entry in PortReachedTypes "
                + "(NativeManagedInvoker.cs) is a measured fatal path. Needs a restart.")).Value;
            var extra = config.Bind(ScopeConfigSection, ScopeExtraNamesKey, string.Empty, new ConfigDescription(
                "Comma separated full names of extra injected types to rewrite (for example AmandsGraphics.AmandsGraphicsClass). "
                + "Use it when one specific mod needs the shim without the wholesale switch. Needs a restart.")).Value;
            _scopeExtraNames = ParseExtraNames(extra);

            
        }
        catch (Exception exception)
        {
            
        }
    }

    private static string[] ParseExtraNames(string configured)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configured)) return Array.Empty<string>();
            var parts = configured.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var names = new List<string>(parts.Length);
            foreach (var part in parts)
            {
                var name = part.Trim();
                if (name.Length != 0 && !names.Contains(name)) names.Add(name);
            }

            return names.ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>以单行形式表示的解析后策略，用于诊断头部。</summary>
    private static string DescribeScope() =>
        (_scopeEverything
            ? "every injected type is rewritten (the third-party switch is ON: the round-10 behaviour)"
            : "only Fika's own injected types are rewritten (" + OwnAssemblyPrefix + "* assemblies, the " + OwnNamespace
              + " namespace and " + OwnGlobalNamespaceTypes.Count + " whitelisted global-namespace types); every other injection keeps the invoker the injector generated for it")
        + "; port-reached compatibility list " + (_scopePortReached
            ? "on (" + PortReachedTypes.Count + "): " + string.Join(", ", PortReachedTypes.Keys)
            : "off")
        + (_scopeExtraNames.Length == 0
            ? "; no extra type names configured"
            : "; extra type names: " + string.Join(", ", _scopeExtraNames));

    /// <summary>
    /// 当该注入类型位于 shim 的作用域内（即 shim 可以替换它的 invoker）时为 true。
    /// 其他类型都保留注入器生成的 invoker，也就是这些类型在本类存在之前所拥有的
    /// 那个。绝不抛出：无法检视的类型视为在作用域外。
    /// </summary>
    internal static bool IsShimScope(Type type)
    {
        if (type == null) return false;
        if (_scopeEverything) return true;
        try
        {
            var name = type.FullName;
            if (string.IsNullOrEmpty(name)) return false;
            if (OwnGlobalNamespaceTypes.ContainsKey(name)) return true;
            if (string.Equals(name, OwnNamespace, StringComparison.Ordinal)
                || name.StartsWith(OwnNamespace + ".", StringComparison.Ordinal)) return true;
            var assembly = type.Assembly?.GetName().Name;
            if (assembly != null && assembly.StartsWith(OwnAssemblyPrefix, StringComparison.Ordinal)) return true;
            if (_scopePortReached && PortReachedTypes.ContainsKey(name)) return true;
            for (var i = 0; i < _scopeExtraNames.Length; i++)
                if (string.Equals(_scopeExtraNames[i], name, StringComparison.Ordinal)) return true;
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 因作用域而保留在注入器 invoker 上的类型各写一行——有意按类型
    /// 而非按方法：收窄的意义就在于测试者能读到哪些被放过
    /// 了。进程生命周期内去重。
    /// </summary>
    private static bool ReportOutOfScope(Type owner, string origin)
    {
        try
        {
            var name = owner == null ? "<unknown type>" : (owner.FullName ?? owner.Name);
            if (!ReportedOutOfScope.TryAdd(name, 0)) return false;
            var assembly = "<unknown assembly>";
            try
            {
                assembly = owner?.Assembly?.GetName().Name ?? assembly;
            }
            catch (Exception)
            {
            }

            
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ 安装

    /// <summary>
    /// 添加那个改写每个注入方法 invoker 的后置补丁。由
    /// <see cref="NativeInjectionScope"/> 在安装自己的钩子时调用，即在对第一个类型
    /// 注册之前。绝不抛出：此处失败意味着“完全不改写”，绝不是“加载失败”。
    /// </summary>
    internal static void Install()
    {
        if (_installed || _unavailable) return;
        try
        {
            _hooks = new Harmony(OwnHarmonyId);
            var converter = AccessTools.DeclaredMethod(typeof(ClassInjector), "ConvertMethodInfo");
            if (converter == null)
            {
                _unavailable = true;
                
                return;
            }

            _scratch = Marshal.AllocHGlobal(ScratchBytes);
            // 作用域开关决定本类到底改写什么，因此它们在任何钩子能够采纳一个
            // method info 之前就完成绑定（绝不抛出；见 ConfigureScope）。
            ConfigureScope();
            if (!SelfTest())
            {
                // JIT 拒绝校验的方法体不可作为入口点发布：引发该失败的东西
                // 会在原生 callout 内部再次引发，而在那里没有任何托管
                // 帧能处理它。“完全不改写”才是安全的答案（硬性规则 2）。
                _unavailable = true;
                return;
            }
            WarmInstanceFastPath();
            _hooks.Patch(converter, postfix: new HarmonyMethod(
                AccessTools.DeclaredMethod(typeof(NativeManagedInvoker), nameof(CaptureMethodInfo))));
            InstallRegistrationHook();
            InstallInvokeGuard();
            _installed = true;
            
            Sweep(null, "install");
        }
        catch (Exception exception)
        {
            _unavailable = true;
            
        }
    }

    /// <summary>
    /// <c>ClassInjector.RegisterTypeInIl2CppInternal</c> 上的后置补丁：当类型注册返回时，
    /// 它的 method info 已经存在，可以替换。这是为“本端口自己的注册作用域早已消失之后”注册的
    /// 每个 *作用域内* 类型所设的网——尤其是协程载体
    /// <c>Il2CppManagedEnumerator</c>（PortReachedTypes），BepInEx 会在本端口协程辅助的第一次
    /// <c>WrapToIl2Cpp()</c> 内部懒加载注册它。作用域外的类型会在诊断中被
    /// 点名一次，并保留注入器生成的 invoker（见作用域区域）。
    /// </summary>
    private static void InstallRegistrationHook()
    {
        try
        {
            var registration = AccessTools.DeclaredMethod(typeof(ClassInjector), "RegisterTypeInIl2CppInternal");
            if (registration == null)
            {
                
                return;
            }

            _hooks.Patch(registration, postfix: new HarmonyMethod(
                AccessTools.DeclaredMethod(typeof(NativeManagedInvoker), nameof(CaptureRegisteredType))));
        }
        catch (Exception exception)
        {
            
        }
    }

    private static void CaptureRegisteredType(Type type)
    {
        try
        {
            RememberType(type);
            SweepType(type, "registered after the port's own scope");
        }
        catch (Exception exception)
        {
            
        }
    }

    /// <summary>
    /// 引擎反射 invoke 的托管入口点上的前缀补丁：这是原生侧对
    /// <c>Il2CppMethodInfo::InvokerMethod</c> 解引用之前的最后时刻。仍然带着
    /// 原装 invoker 的 method info（在没有安装任何钩子时被转换，或由注入器在运行时为某个泛型
    /// 实例化创建）会在此被修复，而不是被直接进入。该前缀只观察与
    /// 修复，并始终让原 invoke 执行，因此不会改变一次调用的语义。
    /// </summary>
    private static void InstallInvokeGuard()
    {
        GuardInvoke("il2cpp_runtime_invoke", nameof(GuardRuntimeInvoke));
        GuardInvoke("il2cpp_runtime_invoke_convert_args", nameof(GuardRuntimeInvokeConvertArgs));
    }

    private static void GuardInvoke(string name, string patch)
    {
        try
        {
            var method = AccessTools.DeclaredMethod(typeof(IL2CPP), name);
            if (method == null)
            {
                
                return;
            }

            _hooks.Patch(method, prefix: new HarmonyMethod(
                AccessTools.DeclaredMethod(typeof(NativeManagedInvoker), patch)));
        }
        catch (Exception exception)
        {
            
        }
    }

    private static bool GuardRuntimeInvoke(nint __0) => GuardInvokeTarget(__0, "runtime invoke");

    private static bool GuardRuntimeInvokeConvertArgs(nint __0) => GuardInvokeTarget(__0, "runtime invoke (argument conversion)");

    private static bool GuardInvokeTarget(nint method, string origin)
    {
        try
        {
            if (method == 0) return true;
            if (Plans.ContainsKey(method)) return true;
            if (NotOurs.ContainsKey(method)) return true;
            // 守卫遇到的第一个未知 method info，就是用一次扫描看清注入器已注册的
            // 一切的时刻——一个类型只会出现一次，而扫描是唯一能看到那些
            // 注册过程没人钩住的类型的地方。
            SweepOnce(origin);
            if (Plans.ContainsKey(method)) return true;
            EnsureAdopted((Il2CppMethodInfo*)method, origin, null);
        }
        catch (Exception exception)
        {
            
        }

        // 始终执行原来的 invoke。
        return true;
    }

    /// <summary>
    /// 在本端口仍处于托管上下文时，触碰一次实例快路径所要比较的那个原生类。
    /// 该查找是 interop 类指针存储中的一个静态字段，因此若不这样做，
    /// 第一次 callout 就会在引擎线程上运行它的初始化器——也就是在原生 callout 内部，那里
    /// 任何东西都不允许逃逸。它解析到的值会被记录下来：那里为 0 是唯一一种
    /// 让快路径对所有零句柄都退回 interop 自身查找、而不是跳过它们的情况。
    ///
    /// 绝不抛出（Installing 不能因为一个诊断而失败）。
    /// </summary>
    private static void WarmInstanceFastPath()
    {
        try
        {
            var pointer = Il2CppClassPointerStore<Il2CppSystem.MulticastDelegate>.NativeClassPtr;
            
        }
        catch (Exception exception)
        {
            
        }
    }

    /// <summary>
    /// 在托管上下文中用一次空调用进入 shim 一次，好让方法体（以及失败路径）
    /// 在加载时就被 JIT 校验，而不是等到第一次原生 callout 内部。
    /// </summary>
    private static bool SelfTest()
    {
        try
        {
            Shim(0, null, 0, null, null);
            
            return true;
        }
        catch (Exception exception)
        {
            
            return false;
        }
    }

    /// <summary>
    /// <c>ClassInjector.ConvertMethodInfo</c> 上的 Harmony 后置补丁：对注入类型的每个
    /// 方法执行，恰好发生在注入器填好其 method info 之后——包括注入器在运行时从它自己的
    /// 原生钩子转换出来的那些已膨胀的泛型方法。实际工作位于
    /// <see cref="TryAdopt"/>，扫描与运行时守卫以相同规则调用它，因此这三条路径
    /// 发布的都是同一种入口。
    /// </summary>
    private static void CaptureMethodInfo(MethodInfo monoMethod, Il2CppMethodInfo* __result)
    {
        if (__result == null) return;
        try
        {
            TryAdopt(__result, monoMethod, "registration", null, out _);
        }
        catch (Exception exception)
        {
            
        }
    }

    // ------------------------------------------------------------------ 唯一的采纳路径

    /// <summary>
    /// 把一个 method info 的 invoker 指向托管 shim，或说明为何做不到。
    ///
    /// 三个调用者——注册后置补丁、扫描与运行时守卫——唯一的差别在于它们如何
    /// 找到该 method info；它们所适用的规则就是注册一贯适用的那条：
    ///   * 形状必须可复现（<see cref="BuildPlan"/>），否则保留注入器的 invoker
    ///     并把原因写一次；
    ///   * 方法指针被注册为第二个键，因为引擎可能把 method info 的 *副本*
    ///     交给 shim；
    ///   * 每个 method info 一行 <c>[WarmTable]</c>，格式与此前各轮解析时所用的一致：
    ///     指针三元组、方法名，以及 <c>[shim]</c> 加被替换的原装指针，
    ///     或者 <c>[stock: reason]</c>。
    ///
    /// 改写之后的行为与注入器自身 invoker 会做的那次调用相同：plan 把引用
    /// 槽位读作对象指针本身，把按值/by-ref 槽位读作地址，并按返回值自身的宽度
    /// 把它写回（NativeManagedInvoker 的类注释）。
    /// </summary>
    internal static bool TryAdopt(Il2CppMethodInfo* methodInfo, MethodInfo monoMethod, string origin, string detail, out string reason)
    {
        reason = null;
        if (methodInfo == null) return false;
        var key = (nint)methodInfo;
        var name = Describe(monoMethod);
        try
        {
            // 权威的作用域门槛（类注释“shim 的作用域”）：注册、扫描与
            // 运行时守卫都会落在这里，因此第三方 method info 永远不会被采纳，即使
            // 某个其他调用者找到了它。声明类型为 null *不是* 作用域判定结论——下面的
            // plan 构建器仍会为它写出真正的原因。
            var owner = monoMethod?.DeclaringType;
            if (owner != null && !IsShimScope(owner))
            {
                reason = "outside the shim scope (only Fika's own injected types are rewritten)";
                ReportOutOfScope(owner, origin);
                return false;
            }

            Plan plan = null;
            string why = null;
            if (!UnityVersionHandler.IsMetadataV29OrHigher)
                why = "this install does not use the metadata v29 invoker ABI";
            else
                plan = BuildPlan(monoMethod, out why);

            var info = UnityVersionHandler.Wrap(methodInfo);
            var methodPointer = info.MethodPointer;
            var stockInvoker = info.InvokerMethod;
            var status = "stock";
            if (plan != null)
            {
                var shim = ShimPointer();
                if (shim == 0)
                {
                    plan = null;
                    why = "the managed shim pointer could not be marshalled";
                }
                else
                {
                    try
                    {
                        info.InvokerMethod = shim;
                        Plans[key] = plan;
                        if (methodPointer != 0) PlansByMethodPointer[methodPointer] = plan;
                        status = "shim";
                    }
                    catch (Exception exception)
                    {
                        
                        plan = null;
                        why = "the managed shim pointer could not be marshalled";
                    }
                }
            }
            else if (why == null)
            {
                why = "the injector bound no invoker to this method";
            }

            reason = why;
            var first = Report(key, "methodInfo=0x" + key.ToString("X") + " methodPointer=0x" + methodPointer.ToInt64().ToString("X")
                + " invokerMethod=0x" + info.InvokerMethod.ToInt64().ToString("X") + " " + name
                + (status == "shim"
                    ? " [shim] stock-invoker=0x" + stockInvoker.ToInt64().ToString("X")
                    : " [stock: " + why + "]")
                + (string.Equals(origin, "registration", StringComparison.Ordinal) ? string.Empty : " origin=" + origin)
                + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail));
            if (first)
            {
                if (status == "shim") System.Threading.Interlocked.Increment(ref _shimCount);
                else System.Threading.Interlocked.Increment(ref _stockCount);
            }

            
            return status == "shim";
        }
        catch (Exception exception)
        {
            reason = "the method info could not be inspected (" + exception.GetType().Name + ")";
            
            return false;
        }
    }

    // ------------------------------------------------------------------ plan 构建

    /// <summary>
    /// 判定 shim 能否精确复现该方法。对每个留在注入器自身 invoker 上的形状，
    /// 都返回 null（绝不抛出）并给出原因。
    /// </summary>
    private static Plan BuildPlan(MethodInfo method, out string reason)
    {
        reason = null;
        try
        {
            if (method == null)
            {
                reason = "the injector reported no method";
                return null;
            }

            if (method.IsAbstract)
            {
                reason = "abstract";
                return null;
            }

            if (method.ContainsGenericParameters || method.IsGenericMethod)
            {
                reason = "generic (the injector binds no invoker, or binds it per instantiation)";
                return null;
            }

            if (method.IsConstructor)
            {
                reason = "constructor";
                return null;
            }

            if (method.CallingConvention == CallingConventions.VarArgs)
            {
                reason = "varargs";
                return null;
            }

            if (!NativeInjectionScope.IsNativeSignatureType(method.ReturnType))
            {
                reason = "the injector binds no native ABI to the return type " + method.ReturnType.Name;
                return null;
            }

            var returnPlan = BuildReturn(method.ReturnType);
            if (returnPlan == null)
            {
                reason = "the return type " + method.ReturnType.Name + " cannot be written back";
                return null;
            }

            var parameters = method.GetParameters();
            var arguments = new ArgumentPlan[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var argument = BuildArgument(parameters[i]);
                if (argument == null)
                {
                    reason = "parameter " + i + " (" + parameters[i].Name + ": "
                        + parameters[i].ParameterType.Name + ") cannot be reproduced";
                    return null;
                }

                arguments[i] = argument;
            }

            return new Plan
            {
                Target = method,
                Name = Describe(method),
                Static = method.IsStatic,
                Arguments = arguments,
                Return = returnPlan,
            };
        }
        catch (Exception exception)
        {
            reason = "the shape could not be inspected (" + exception.GetType().Name + ")";
            return null;
        }
    }

    private static ArgumentPlan BuildArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type.IsByRef)
        {
            var element = type.GetElementType();
            var codec = ResolveCodec(element);
            if (codec == null || codec.Write == null) return null;
            var isOut = parameter.IsOut;
            return new ArgumentPlan
            {
                Name = parameter.Name,
                Type = type.Name,
                Slot = SlotKind.ByRef,
                Read = slot => isOut ? codec.Default : codec.Read(slot),
                WriteBack = codec.Write,
            };
        }

        if (type.IsPointer)
        {
            var element = type.GetElementType();
            return new ArgumentPlan
            {
                Name = parameter.Name,
                Type = type.Name,
                Slot = SlotKind.Pointer,
                // 指针槽位持有指针本身（arg-abi.txt，指针类别）。
                Read = slot => Pointer.Box((void*)slot, element),
                WriteBack = null,
            };
        }

        if (IsNativeReference(type))
        {
            var wrap = ReferenceWrapper(type);
            if (wrap == null) return null;
            return new ArgumentPlan
            {
                Name = parameter.Name,
                Type = type.Name,
                Slot = SlotKind.Reference,
                // 引用槽位持有对象指针本身：不要对它解引用。
                Read = slot => slot == 0 ? null : wrap(slot),
                WriteBack = null,
            };
        }

        var value = ResolveCodec(type);
        if (value == null) return null;
        return new ArgumentPlan
        {
            Name = parameter.Name,
            Type = type.Name,
            Slot = SlotKind.Value,
            // value type 槽位持有该存储的地址。
            Read = value.Read,
            WriteBack = null,
        };
    }

    private static ReturnPlan BuildReturn(Type type)
    {
        if (type == typeof(void)) return new ReturnPlan { IsVoid = true };
        if (type.IsPointer)
        {
            return new ReturnPlan
            {
                Write = (value, buffer) =>
                {
                    *(nint*)buffer = value == null ? 0 : (nint)Pointer.Unbox(value);
                },
            };
        }

        var codec = ResolveCodec(type);
        if (codec == null || codec.Write == null) return null;
        return new ReturnPlan { Write = (value, buffer) => codec.Write(buffer, value) };
    }

    private static bool IsNativeReference(Type type) =>
        type == typeof(string) || typeof(Il2CppObjectBase).IsAssignableFrom(type) || type.IsInterface;

    // ------------------------------------------------------------------ codec

    /// <summary>
    /// 一个托管类型在某个 *地址* 上的转换：<c>Read(address)</c> 取得装箱后的托管
    /// 值，<c>Write(address, value)</c> 把它存回。无法转换该类型时为 null。
    /// </summary>
    private static Codec ResolveCodec(Type type)
    {
        var codec = Codecs.GetOrAdd(type, BuildCodec);
        return codec.Read == null ? null : codec;
    }

    private static Codec BuildCodec(Type type)
    {
        try
        {
            if (IsNativeReference(type))
            {
                var wrap = ReferenceWrapper(type);
                if (wrap == null) return Unsupported;
                return new Codec
                {
                    Kind = type.Name,
                    Read = address =>
                    {
                        var pointer = *(nint*)address;
                        return pointer == 0 ? null : wrap(pointer);
                    },
                    Write = (address, value) => *(nint*)address = NativePointer(value),
                    Default = null,
                };
            }

            if (type.IsPointer)
            {
                var element = type.GetElementType();
                return new Codec
                {
                    Kind = type.Name,
                    Read = address => Pointer.Box((void*)*(nint*)address, element),
                    Write = (address, value) => *(nint*)address = value == null ? 0 : (nint)Pointer.Unbox(value),
                    Default = Pointer.Box(null, element),
                };
            }

            if (type.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(type);
                var underlyingCodec = ResolveCodec(underlying);
                if (underlyingCodec == null) return Unsupported;
                return new Codec
                {
                    Kind = type.Name,
                    Read = address => Enum.ToObject(type, underlyingCodec.Read(address)),
                    Write = (address, value) => underlyingCodec.Write(address,
                        Convert.ChangeType(value, underlying, System.Globalization.CultureInfo.InvariantCulture)),
                    Default = Enum.ToObject(type, underlyingCodec.Default),
                };
            }

            if (type == typeof(bool))
                return new Codec { Kind = "bool", Read = a => *(byte*)a != 0, Write = (a, v) => *(byte*)a = (bool)v ? (byte)1 : (byte)0, Default = false };
            if (type == typeof(byte))
                return new Codec { Kind = "byte", Read = a => *(byte*)a, Write = (a, v) => *(byte*)a = (byte)v, Default = default(byte) };
            if (type == typeof(sbyte))
                return new Codec { Kind = "sbyte", Read = a => *(sbyte*)a, Write = (a, v) => *(sbyte*)a = (sbyte)v, Default = default(sbyte) };
            if (type == typeof(short))
                return new Codec { Kind = "short", Read = a => *(short*)a, Write = (a, v) => *(short*)a = (short)v, Default = default(short) };
            if (type == typeof(ushort))
                return new Codec { Kind = "ushort", Read = a => *(ushort*)a, Write = (a, v) => *(ushort*)a = (ushort)v, Default = default(ushort) };
            if (type == typeof(char))
                return new Codec { Kind = "char", Read = a => (char)*(ushort*)a, Write = (a, v) => *(ushort*)a = (char)v, Default = default(char) };
            if (type == typeof(int))
                return new Codec { Kind = "int", Read = a => *(int*)a, Write = (a, v) => *(int*)a = (int)v, Default = 0 };
            if (type == typeof(uint))
                return new Codec { Kind = "uint", Read = a => *(uint*)a, Write = (a, v) => *(uint*)a = (uint)v, Default = 0u };
            if (type == typeof(long))
                return new Codec { Kind = "long", Read = a => *(long*)a, Write = (a, v) => *(long*)a = (long)v, Default = 0L };
            if (type == typeof(ulong))
                return new Codec { Kind = "ulong", Read = a => *(ulong*)a, Write = (a, v) => *(ulong*)a = (ulong)v, Default = 0UL };
            if (type == typeof(float))
                return new Codec { Kind = "float", Read = a => *(float*)a, Write = (a, v) => *(float*)a = (float)v, Default = 0f };
            if (type == typeof(double))
                return new Codec { Kind = "double", Read = a => *(double*)a, Write = (a, v) => *(double*)a = (double)v, Default = 0d };
            if (type == typeof(IntPtr))
                return new Codec { Kind = "nint", Read = a => *(nint*)a, Write = (a, v) => *(nint*)a = (nint)v, Default = IntPtr.Zero };
            if (type == typeof(UIntPtr))
                return new Codec { Kind = "nuint", Read = a => *(nuint*)a, Write = (a, v) => *(nuint*)a = (nuint)v, Default = UIntPtr.Zero };

            if (type.IsValueType && !type.ContainsGenericParameters) return BuildStructCodec(type);
        }
        catch (Exception exception)
        {
            
        }

        return Unsupported;
    }

    /// <summary>
    /// 结构体通过生成的方法以原始字节搬运：读取用 <c>ldobj T; box T</c>，
    /// 写入用 <c>unbox T; cpblk min(managed size, il2cpp value size)</c>。两者都在此执行一次，
    /// 且处于托管上下文中，因此 JIT 问题会在注册时暴露，而不是在 callout 内部。
    /// </summary>
    private static Codec BuildStructCodec(Type type)
    {
        var readMethod = new DynamicMethod("FikaReadStruct_" + type.Name, typeof(object), new[] { typeof(nint) },
            typeof(NativeManagedInvoker), true);
        var readBody = readMethod.GetILGenerator();
        readBody.Emit(OpCodes.Ldarg_0);
        readBody.Emit(OpCodes.Ldobj, type);
        readBody.Emit(OpCodes.Box, type);
        readBody.Emit(OpCodes.Ret);
        var read = (Func<nint, object>)readMethod.CreateDelegate(typeof(Func<nint, object>));

        var managedSize = ManagedSize(type);
        var nativeSize = NativeValueSize(type, managedSize);
        var copy = Math.Min(managedSize, nativeSize);
        var writeMethod = new DynamicMethod("FikaWriteStruct_" + type.Name, typeof(void),
            new[] { typeof(nint), typeof(object) }, typeof(NativeManagedInvoker), true);
        var writeBody = writeMethod.GetILGenerator();
        writeBody.Emit(OpCodes.Ldarg_0);
        writeBody.Emit(OpCodes.Ldarg_1);
        writeBody.Emit(OpCodes.Unbox, type);
        writeBody.Emit(OpCodes.Conv_U);
        writeBody.Emit(OpCodes.Ldc_I4, copy);
        writeBody.Emit(OpCodes.Cpblk);
        writeBody.Emit(OpCodes.Ret);
        var write = (Action<nint, object>)writeMethod.CreateDelegate(typeof(Action<nint, object>));

        // 针对暂存内存各执行一次：完成 JIT 校验，并为 'out' 准备一个默认实例。
        var defaultValue = read(_scratch);
        write(_scratch, defaultValue);
        return new Codec { Kind = type.Name, Read = read, Write = write, Default = defaultValue };
    }

    private static int ManagedSize(Type type)
    {
        var method = new DynamicMethod("FikaSizeOf_" + type.Name, typeof(int), Type.EmptyTypes,
            typeof(NativeManagedInvoker), true);
        var body = method.GetILGenerator();
        body.Emit(OpCodes.Sizeof, type);
        body.Emit(OpCodes.Conv_I4);
        body.Emit(OpCodes.Ret);
        return ((Func<int>)method.CreateDelegate(typeof(Func<int>)))();
    }

    private static int NativeValueSize(Type type, int fallback)
    {
        try
        {
            var align = 0u;
            var size = (int)IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore.GetNativeClassPointer(type), ref align);
            if (size > 0 && size < ScratchBytes) return size;
        }
        catch (Exception)
        {
        }

        return fallback;
    }

    /// <summary>装箱托管值（对象、字符串或 null）的原生指针。</summary>
    private static nint NativePointer(object value)
    {
        if (value == null) return 0;
        if (value is string text) return IL2CPP.ManagedStringToIl2Cpp(text);
        if (value is Il2CppObjectBase wrapped) return IL2CPP.Il2CppObjectBaseToPtr(wrapped);
        return 0;
    }

    /// <summary>
    /// <c>pointer -> Il2CppObjectPool.Get&lt;T&gt;(pointer)</c> 每个类型绑定一次，因此热路径只是一次
    /// 委托调用。无法绑定该包装器时为 null（此时该形状保留原装 invoker）。
    /// </summary>
    private static Func<nint, object> ReferenceWrapper(Type type)
    {
        if (type == typeof(string)) return pointer => IL2CPP.Il2CppStringToManaged(pointer);
        if (!typeof(Il2CppObjectBase).IsAssignableFrom(type) && !type.IsInterface) return null;
        try
        {
            MethodInfo get = null;
            foreach (var candidate in typeof(Il2CppObjectPool).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (candidate.Name != "Get" || !candidate.IsGenericMethodDefinition) continue;
                if (candidate.GetGenericArguments().Length != 1) continue;
                var parameters = candidate.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(nint)) continue;
                get = candidate;
                break;
            }

            if (get == null) return null;
            var converter = get.MakeGenericMethod(type).CreateDelegate(typeof(Func<nint, object>));
            return (Func<nint, object>)converter;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static nint ShimPointer()
    {
        var pointer = _shimPointer;
        if (pointer != 0) return pointer;
        lock (ShimDelegate)
        {
            if (_shimPointer == 0) _shimPointer = Marshal.GetFunctionPointerForDelegate(ShimDelegate);
            return _shimPointer;
        }
    }

    // ------------------------------------------------------------------ 入口

    /// <summary>
    /// 引擎现在到达的托管入口，取代生成的 invoker。它运行在引擎线程上、
    /// 处于原生 callout 之内：在任何情况下都必须正常返回。
    /// </summary>
    private static void Shim(nint methodPointer, Il2CppMethodInfo* methodInfo, nint obj, nint* args, nint* returnValue)
    {
        Plan plan = null;
        try
        {
            var key = (nint)methodInfo;
            if (!Plans.TryGetValue(key, out plan))
            {
                if (methodPointer != 0 && PlansByMethodPointer.TryGetValue(methodPointer, out plan))
                {
                    // 引擎传来的是 method info 的副本；由它的 thunk 完成了识别。
                }
                else if (methodInfo != null && EnsureAdopted(methodInfo, "callout", null)
                                               && Plans.TryGetValue(key, out plan))
                {
                    
                }
                else
                {
                    return;
                }
            }

            object instance = null;
            if (!plan.Static)
            {
                // 有意在写入口追踪之前先解析：没有任何东西可运行的 callout 永远不会
                // 进入托管代码，因此它不能发布那一对中的“已进入”那一半——正是
                // 缺失的另一半点出进程死于哪个方法（TraceEntered/TraceLeft）。
                if (!TryResolveInstance(key, obj, plan, out instance))
                {
                    return;
                }
            }

            var arguments = plan.NewArguments();
            if (arguments.Length != 0)
            {
                if (args == null) throw new InvalidOperationException("the engine passed no argument array");
                for (var i = 0; i < arguments.Length; i++) arguments[i] = plan.Arguments[i].Read(args[i]);
            }

            var result = plan.Target.Invoke(instance, arguments);

            // By-ref/out：被调用方写入了该数组的参数槽位；把这些值交回
            // 引擎给我们的那些地址。
            for (var i = 0; i < arguments.Length; i++)
            {
                var write = plan.Arguments[i].WriteBack;
                if (write != null) write(args[i], arguments[i]);
            }

            var writeReturn = plan.Return.Write;
            if (writeReturn != null && returnValue != null) writeReturn(result, (nint)returnValue);

            // 入口行写在调用之前，因此只有“entered”而没有匹配的“left”就点出了
            // 进程死于哪个方法。fail fast 会在调用中途杀掉进程，而这正是那个
            // 信号：诊断尾部就是致命错误之前最后写下的内容。
        }
        catch (Exception exception)
        {
            ReportRuntimeFailure(plan, exception);
        }
    }

    /// <summary>
    /// 解析某个 callout 的托管实例，同时绝不让 interop 的查找抛出异常。
    ///
    /// <c>ClassInjectorBase.GetMonoObjectFromIl2CppPointer</c> 最终落到
    /// <c>GCHandle.FromIntPtr(handle).Target</c>（所安装 Il2CppInterop.Runtime.dll 的 ildump：
    /// 先 <c>GetGcHandlePtrFromIl2CppObject</c>，再委托兜底，再 <c>GCHandle.FromIntPtr</c>）。
    /// 对于托管侧已经消失的对象，该句柄为零，调用便会抛出
    /// <c>InvalidOperationException: "Handle is not initialized."</c>——这是实测而非假定：它正是
    /// 2026-10-10 那次突袭中十七条终结器日志背后的异常
    /// （analysis/log-noise-cleanup/REPORT.md）。句柄指针本身可以无异常地读出，因此
    /// 判定在会抛出的调用 *之前* 就完成：零句柄意味着“这个 callout 没有东西
    /// 可运行”，那是 callout 的正常结束，而不是失败。
    ///
    /// 没有东西可运行时返回 false（每个方法只报告一次）。绝不抛出。
    /// </summary>
    private static bool TryResolveInstance(nint key, nint obj, Plan plan, out object instance)
    {
        instance = null;
        if (obj == 0)
        {
            ReportNothingToRun(plan, "the callout carries no object pointer");
            return false;
        }

        // interop 对该句柄还有第二个来源——委托保留其目标的那个句柄，见
        // ClassInjectorBase.FallbackGetGcHandlePtrFromIl2CppDelegateMTarget——那种形状会被原样交给
        // interop 的查找。其他所有零句柄的对象根本没有托管侧，
        // 去索取它只会产生 first-chance 探测必须报告的那个异常。
        if (ClassInjectorBase.GetGcHandlePtrFromIl2CppObject(obj) == 0 && !IsMulticastDelegate(obj))
        {
            ReportNothingToRun(plan, "Handle is not initialized.");
            return false;
        }

        try
        {
            instance = ClassInjectorBase.GetMonoObjectFromIl2CppPointer(obj);
        }
        catch (Exception exception) when (IsNothingToRun(exception))
        {
            // 经由 interop 自身路径得到的同一结论：没有东西可运行，绝不是失败。
            ReportNothingToRun(plan, exception.Message);
            return false;
        }

        if (instance == null)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 当该对象是——或可能是——<c>Il2CppSystem.MulticastDelegate</c> 时为 true，那是唯一一种
    /// 托管句柄位于其目标上而非其自身上的形状。凡是无法完成检查时都故意返回 true
    /// （没有原生类，或 interop 的类指针尚未具现化）：此时调用者
    /// 保留 interop 自身的查找，也就是本端口在快路径存在之前就有的行为，
    /// 而不是静默跳过一个它无法归类的 callout。
    /// </summary>
    private static bool IsMulticastDelegate(nint obj)
    {
        try
        {
            var delegateClass = Il2CppClassPointerStore<Il2CppSystem.MulticastDelegate>.NativeClassPtr;
            var klass = IL2CPP.il2cpp_object_get_class(obj);
            if (delegateClass == IntPtr.Zero || klass == IntPtr.Zero) return true;
            return IL2CPP.il2cpp_class_is_assignable_from(delegateClass, klass);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// shim 为“没有东西可运行”的 callout 写下的那一行。这就是终结器路径：
    /// il2cpp 回收某个注入对象的原生侧，然后再调用一次 <c>Finalize</c>，
    /// 而此时托管侧——以及用来解析该实例的 GC 句柄——已经
    /// 消失。按方法去重，且到达时不抛出任何东西：过去是靠让 interop 的查找抛出异常
    /// 才发现同样的情形，那使每一次回收都变成一次
    /// first-chance 异常和一条详细探测记录（单次突袭中有 17 条）。
    /// </summary>
    private static void ReportNothingToRun(Plan plan, string reason)
    {
        try
        {
            var name = plan == null ? "<unknown>" : plan.Name;
            if (!_reportedNothingToRun.TryAdd(name, 0)) return;
            
        }
        catch (Exception)
        {
        }
    }

    // ------------------------------------------------------------------ 诊断

    /// <summary>
    /// 每个注入方法一行：这组指针三元组能把 dump 里的 calli 目标或 invoker stub
    /// 还原成方法名。2026-10-10 的主机突袭需要一份加载器记录来手工完成这件事。
    /// </summary>
    private static bool Report(nint key, string line)
    {
        try
        {
            if (!Reported.TryAdd(key, 0)) return false;
            
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ReportRuntimeFailure(Plan plan, Exception exception)
    {
        try
        {
            var name = plan == null ? "<unknown>" : plan.Name;

            // 每个方法一行。终结器路径过去每次回收都打印同一条失败十七次
            // （见下面的 IsNothingToRun），把其他所有诊断都埋掉了。
            if (!_reportedFailures.TryAdd(name, 0)) return;

            if (IsNothingToRun(exception))
            {
                ReportNothingToRun(plan, exception.Message);
                return;
            }

            
            _reportTrampolineException ??= AccessTools.DeclaredMethod(typeof(ClassInjector), "ReportTrampolineException");
            _reportTrampolineException?.Invoke(null, new object[] { exception, name });
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 当该失败意味着“这个 callout 没有东西可运行”而不是“shim 坏了”时为 true：该
    /// 对象已不再拥有托管实例，这正是终结器路径的样子（il2cpp 回收
    /// il2cpp 侧，然后再调用一次 Finalize）。
    /// </summary>
    private static bool IsNothingToRun(Exception exception) =>
        exception is InvalidOperationException
        && exception.Message.Contains("Handle is not initialized", StringComparison.Ordinal);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _reportedFailures = new();

    /// <summary>
    /// “没有东西可运行”的结束（终结器路径，见 ReportNothingToRun）已写出的方法。
    /// 有意与 <see cref="_reportedFailures"/> 分成两张表：一个方法在一次会话中可能合法地以两种
    /// 方式结束，而其中一种不应让另一种沉默。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _reportedNothingToRun = new();

    private static string Describe(MethodInfo method) =>
        method == null ? "<unknown method>" : method.DeclaringType?.FullName + "." + method.Name;
}
