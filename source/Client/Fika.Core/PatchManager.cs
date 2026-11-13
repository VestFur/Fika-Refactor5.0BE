using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime.Runtime;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core;

/// <summary>
/// 基于 SPT 5 的 IL2CPP 补丁 API 加载并校验 Fika 补丁。
/// </summary>
public sealed class PatchManager
{
    private readonly Assembly _assembly;
    private readonly bool _autoPatch;
    private readonly ManualLogSource _logger = BepInEx.Logging.Logger.CreateLogSource("Fika.Patches");
    private readonly HashSet<Type> _enabled = new();

    public PatchManager(object owner, bool autoPatch)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _assembly = owner.GetType().Assembly;
        _autoPatch = autoPatch;
    }

    /// <summary>
    /// SPT 5 让一个泛型方法的每个实例化共用同一份原生方法体，
    /// 并通过一个隐藏参数传入实例化信息。IL2CPP detour 会用
    /// 当初安装时所用的实例化来重放那份方法体，因此所有
    /// 同类实例化随后都会带着错误的泛型上下文运行：补丁
    /// <c>SimpleContextMenu.ShowMenu&lt;EItemInfoButton&gt;</c> 让杂志、
    /// 杂志预设与愿望单菜单以 MethodAccessException 崩掉，
    /// 而补丁 <c>TransitController.Exist&lt;ClientTransitController&gt;</c>
    /// 在 raid 中以错误的 controller 应答了游戏的 <c>Exist&lt;TransitController&gt;</c> 与交互
    /// 查找。只有此处列出的补丁
    /// 才仍然可以针对泛型方法。
    /// </summary>
    private static readonly HashSet<string> GenericTargetAllowList = new(StringComparer.Ordinal)
    {
        // 唯一决定一场 raid 收到哪个世界类的接缝。
        // Create&lt;HideoutGameWorld&gt; 与 Create&lt;NarrateGameWorld&gt; 共用
        // 它的方法体；只要这个补丁对非 Fika 世界持续返回 true，它们就不受影响，
        // 但共用方法体正是这条记录存在的原因。
        "Fika.Core.Main.Patches.GameWorldPatches.GameWorld_Create_Patch"
    };

    /// <summary>
    /// 本构建中有意缺席的补丁，与补丁计数放在一起，使
    /// 启动日志能说明原因而不是静默丢弃它们。每个条目必须对应一个携带
    /// <see cref="IgnoreAutoPatchAttribute"/> 的补丁类型（Fika.PatchChecks 会把那些报告为
    /// "conditional"）；这个列表就是该决定中人类可读的那一半。
    /// </summary>
    private static readonly string[] DeliberateSkips =
    {
        "MuzzleManager_Play_Patch: duplicate of SPTushonka.Custom MemoryImprovements.MuzzleManagerPlayPatch, and its explicit interface " +
        "implementation is not detourable with Il2CppInterop 1.6.7 (the generated stub loads NativeInterfaceMethodInfoPtr_, not NativeMethodInfoPtr_)"
    };

    public void EnablePatches()
    {
        if (!_autoPatch) return;
        var types = GetAutomaticPatches(_assembly).ToArray();
        // 在改动 Harmony 之前先检查每个补丁。SPT 加载器会静默忽略
        // transpiler 标记，并捕获目标缺失的异常。
        var patches = types.Select(type => (ModulePatch)Activator.CreateInstance(type, true)).ToArray();
        var failed = new List<string>();
        foreach (var patch in patches)
        {
            try
            {
                _logger.LogDebug($"Validating {patch.GetType().FullName}");
                EnableValidated(patch, ValidatePatch(patch));
            }
            catch (Exception exception)
            {
                failed.Add(patch.GetType().Name);
                // GetTargetMethod 是通过反射调用的，因此每个失败都以 Message 毫无信息的
                // TargetInvocationException 形式到达。改为记录真正的原因（以及
                // 补丁所期望的签名）—— "Exception has been thrown by the target of an
                // invocation." 这句话曾让字体防护的 byref 查找失败时多花了一轮猜测。
                var reason = exception is TargetInvocationException { InnerException: { } inner }
                    ? inner.GetType().Name + ": " + inner.Message
                    : exception.GetType().Name + ": " + exception.Message;
                _logger.LogError($"{patch.GetType().Name}: {reason}");
                
            }
        }
        if (failed.Count == 0)
        {
            _logger.LogInfo($"Verified {_enabled.Count} Fika patches");
        }
        else
        {
            _logger.LogWarning($"Verified {_enabled.Count} Fika patches - not applied: {string.Join(", ", failed)}");
        }
        if (DeliberateSkips.Length > 0)
        {
            _logger.LogWarning($"Deliberately skipped {DeliberateSkips.Length} patch(es): {string.Join(" | ", DeliberateSkips)}");
        }
    }

    internal static IEnumerable<Type> GetAutomaticPatches(Assembly assembly)
    {
        return assembly.GetTypes()
            .Where(type => !type.IsAbstract && !type.ContainsGenericParameters && typeof(ModulePatch).IsAssignableFrom(type))
            .Where(type => !type.IsDefined(typeof(IgnoreAutoPatchAttribute), false))
#if !DEBUG
            .Where(type => !type.IsDefined(typeof(DebugPatchAttribute), false))
#endif
            .OrderBy(type => type.FullName, StringComparer.Ordinal);
    }

    public void EnablePatch(ModulePatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        EnableValidated(patch, ValidatePatch(patch));
    }

    internal static MethodBase ValidatePatch(ModulePatch patch)
    {
        var type = patch.GetType();
        var methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (methods.Any(method => method.IsDefined(typeof(PatchTranspilerAttribute), false)))
            throw new NotSupportedException($"{type.FullName} still contains a Mono transpiler; an IL2CPP replacement is required.");

        var prefixCount = methods.Count(method => method.IsDefined(typeof(PatchPrefixAttribute), false));
        var postfixCount = methods.Count(method => method.IsDefined(typeof(PatchPostfixAttribute), false));
        if (prefixCount + postfixCount == 0 || prefixCount > 1 || postfixCount > 1)
            throw new InvalidOperationException($"{type.FullName} must declare one prefix and/or one postfix supported by the SPT loader.");

        var target = AccessTools.Method(type, "GetTargetMethod").Invoke(patch, null) as MethodBase;
        if (target == null)
            throw new MissingMethodException($"{type.FullName}: target method does not exist in this game build.");
        EnsurePatchableTarget(type, target);
        return target;
    }

    private static void EnsurePatchableTarget(Type patchType, MethodBase target)
    {
        if (target is not MethodInfo { IsGenericMethod: true }) return;
        if (GenericTargetAllowList.Contains(patchType.FullName)) return;
        throw new NotSupportedException($"{patchType.FullName}: {target} is a generic method. IL2CPP shares one native body between all of its " +
            "instantiations and the IL2CPP detour replays it with this instantiation's generic context, so every sibling instantiation would run as " +
            "this one. Patch a non-generic caller instead.");
    }

    private unsafe void EnableValidated(ModulePatch patch, MethodBase target)
    {
        var type = patch.GetType();
        if (_enabled.Contains(type)) return;
        _logger.LogDebug($"Installing {type.FullName} on {target}");
        using var resolution = new NativePatchScope(target);
        using var values = new NativeDetourScope(target);
        // Harmony 可能静默退化为对 CLR 包装器打补丁。此时原生游戏
        // 调用会绕过该补丁，尽管 Harmony 补丁表里已有条目。
        if (typeof(Il2CppObjectBase).IsAssignableFrom(target.DeclaringType))
        {
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(target);
            if (field != null)
            {
                var native = UnityVersionHandler.Wrap((Il2CppMethodInfo*)(IntPtr)field.GetValue(null));
                var first = Marshal.ReadByte(native.MethodPointer);
                if (first is 0xC3 or 0xC2)
                    throw new NotSupportedException($"{type.FullName}: {target} has an empty native body that may be shared with unrelated methods; choose a non-empty caller.");
            }
            var backend = HarmonyLib.Public.Patching.PatchManager.GetMethodPatcher(target);
            if (backend?.GetType().FullName != "Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher")
                throw new NotSupportedException($"{type.FullName}: {target} requires an IL2CPP detour; resolved {backend?.GetType().FullName ?? "no backend"}.");
        }
        patch.Enable();
        var info = Harmony.GetPatchInfo(target);
        var methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var method in methods)
        {
            var prefix = method.IsDefined(typeof(PatchPrefixAttribute), false);
            var postfix = method.IsDefined(typeof(PatchPostfixAttribute), false);
            if (!prefix && !postfix) continue;
            var installed = prefix ? info?.Prefixes : info?.Postfixes;
            if (installed == null || !installed.Any(entry => entry.PatchMethod == method && entry.owner == type.Name))
                throw new InvalidOperationException($"{type.FullName}: SPT did not install {method.Name} on {target}.");
        }
        _enabled.Add(type);
        _logger.LogDebug($"Installed {type.FullName}");
    }
}
