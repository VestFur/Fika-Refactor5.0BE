using Fika.Headless.Patches;
using SPTushonka.Reflection.Patching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Fika.Headless.Classes;

/// <summary>
/// 唯一知道在模式确定之前可以安装哪些补丁、以及哪些补丁带有专用主机
/// 载荷的地方。
/// </summary>
internal static class HeadlessPatchCatalog
{
    /// <summary>
    /// 在插件加载时安装，除 <c>Disabled</c> 之外的所有模式都会安装。它们
    /// 只检查选中的档案并记录日志；从不接触渲染器、
    /// 材质、摄像机或任何其他游戏对象，当档案不是 headless 档案时
    /// 它们会直接返回、不做任何事。
    /// </summary>
    internal static readonly Type[] ObserverPatches =
    [
        typeof(ConsoleScreen_OnProfileReceive_Patch),
        typeof(CharacterSelectionScreen_Show_Patch)
    ];

    /// <summary>
    /// 其补丁会移除渲染器、材质、纹理、灯光、后期效果或摄像机，
    /// 覆盖图形能力，或替换本地战局工厂的命名空间。
    /// 把它们中的任何一个加载进正在游玩的客户端，
    /// 就会破坏其渲染器，因此只有在进程被确认为专用主机之后
    /// 才会安装它们。
    /// </summary>
    internal static readonly string[] GraphicsNamespaces =
    [
        "Fika.Headless.Patches.DestroyGraphics",
        "Fika.Headless.Patches.DLSS",
        "Fika.Headless.Patches.VRAM",
        "Fika.Headless.Patches.ReflexPatches",
        "Fika.Headless.Patches.TextureValidateFormat",
        "Fika.Headless.Patches.TODPatches"
    ];

    internal static bool IsGraphicsPatch(Type type)
    {
        var space = type.Namespace;
        if (string.IsNullOrEmpty(space))
            return false;
        foreach (var candidate in GraphicsNamespaces)
        {
            if (space.Equals(candidate, StringComparison.Ordinal) ||
                space.StartsWith(candidate + ".", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 所有在游玩安装中不得存在的补丁。与客户端插件的
    /// 自动补丁发现保持一致，但减去观察者（已安装），
    /// 以及在用户于配置中禁用渲染器破坏时减去
    /// 图形相关命名空间。
    /// </summary>
    internal static List<Type> GetDeferredPatches(Assembly assembly, bool includeGraphics) =>
        assembly.GetTypes()
            .Where(type => !type.IsAbstract && !type.ContainsGenericParameters && typeof(ModulePatch).IsAssignableFrom(type))
            .Where(type => !type.IsDefined(typeof(IgnoreAutoPatchAttribute), false))
#if !DEBUG
            .Where(type => !type.IsDefined(typeof(DebugPatchAttribute), false))
#endif
            .Where(type => !ObserverPatches.Contains(type))
            .Where(type => includeGraphics || !IsGraphicsPatch(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();
}
