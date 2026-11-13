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
/// Loads and verifies Fika patches against the SPT 5 IL2CPP patch API.
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
    /// SPT 5 gives every instantiation of a generic method the same native body
    /// and passes the instantiation in a hidden argument. The IL2CPP detour
    /// replays that body with the instantiation it was installed for, so all
    /// sibling instantiations then run with the wrong generic context: patching
    /// <c>SimpleContextMenu.ShowMenu&lt;EItemInfoButton&gt;</c> broke the
    /// magazine, magazine preset and wishlist menus with MethodAccessException,
    /// and patching <c>TransitController.Exist&lt;ClientTransitController&gt;</c>
    /// answered the game's <c>Exist&lt;TransitController&gt;</c> and interaction
    /// lookups with the wrong controller in raid. Only patches listed here may
    /// still target a generic method.
    /// </summary>
    private static readonly HashSet<string> GenericTargetAllowList = new(StringComparer.Ordinal)
    {
        // The only seam that decides which world class a raid receives.
        // Create&lt;HideoutGameWorld&gt; and Create&lt;NarrateGameWorld&gt; share
        // its body; they are unaffected while this patch keeps returning true for
        // non-Fika worlds, but the shared body is the reason this entry exists.
        "Fika.Core.Main.Patches.GameWorldPatches.GameWorld_Create_Patch"
    };

    public void EnablePatches()
    {
        if (!_autoPatch) return;
        var types = GetAutomaticPatches(_assembly).ToArray();
        // Check every patch before mutating Harmony. The SPT loader silently
        // ignores transpiler markers and catches missing-target exceptions.
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
                _logger.LogError($"{patch.GetType().Name}: {exception.Message}");
            }
        }
        if (failed.Count == 0)
        {
            _logger.LogInfo($"Verified {_enabled.Count} Fika patches");
            return;
        }
        _logger.LogWarning($"Verified {_enabled.Count} Fika patches - not applied: {string.Join(", ", failed)}");
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
        // Harmony can silently fall back to patching a CLR wrapper. Native game
        // calls then bypass the patch despite a populated Harmony patch table.
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
