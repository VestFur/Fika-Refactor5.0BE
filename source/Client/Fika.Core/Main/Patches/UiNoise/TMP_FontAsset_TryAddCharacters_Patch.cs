using System;
using System.Reflection;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;
using TMPro;

namespace Fika.Core.Main.Patches.UiNoise;

/// <summary>
/// 阻止 SimSun 字符添加洪水（一场 raid 中 5,744 条警告）。
///
/// 中文区域设置让 EFT 把其本地化表中的字符添加到后备
/// SimSun 字体资源：<c>EFT.LocalizationManager.UpdateFonts(string)</c> 会为它遍历的每个条目
/// 调用一次 <c>TMPro.TMP_FontAsset.TryAddCharacters(string, bool)</c>，而没有字符的条目
/// 会以 null/空的形式到达。该情形下原版的方法体只是：记录
/// 该日志原文为 "Unable to add characters to font asset [...] because the provided character list is Null or
/// Empty"，把输入复制进 "missing" 输出参数并返回 false —— 不添加任何字形，
/// 也不触碰图集。09:45 那场 raid 连续输出了 5,744 条这样的警告
/// （LogOutput.log 第 833-6576 行），它们全都来自 Fika 的
/// "Waiting for locales to be ready..." 之后紧接着开始的那一次区域设置更新。
///
/// 因此对 null/空列表跳过这次调用既能保持游戏行为完全一致，又能消除
/// 刷屏。第一次跳过会以 Info 级别带字体名报告一次，使这一决定的
/// 证据保留在日志中。
///
/// 两个防护都是异常安全的：在游戏本身方法内部抛出的防护会比它所要消除的警告
/// 更糟，因此意外的形态会降级为一行警告，而原版
/// 调用照常执行（见 <see cref="TrySkipEmptyRequest"/>）。
/// </summary>
internal sealed class TMP_FontAsset_TryAddCharacters_Patch : ModulePatch
{
    private static bool _reported;
    private static bool _guardFailed;

    protected override MethodBase GetTargetMethod()
    {
        return FontGuardTarget.Resolve(
            "TryAddCharacters(string, bool)",
            static parameters => parameters.Length == 2
                && IsString(parameters[0], byRef: false)
                && parameters[1].ParameterType == typeof(bool),
            [typeof(string), typeof(bool)]);
    }

    [PatchPrefix]
    private static bool Prefix(TMP_FontAsset __instance, string characters, ref bool __result)
    {
        try
        {
            if (!string.IsNullOrEmpty(characters))
            {
                // 真正的请求：游戏必须添加这些字符。
                return true;
            }

            // 与原版方法体对该输入给出的答复相同，只是少了那条警告。
            __result = false;

            if (!_reported)
            {
                _reported = true;
                FikaGlobals.LogInfo("[FontGuard] suppressed the first empty TMP character request for font '" +
                    (__instance == null ? "null" : __instance.name) +
                    "'; EFT.LocalizationManager.UpdateFonts sends one per locale entry without characters " +
                    "(5,744 warnings in the 09:45 raid). Further empty calls are skipped silently.");
            }

            return false;
        }
        catch (Exception exception)
        {
            ReportGuardFailure(ref _guardFailed, exception);
            return true;
        }
    }

    internal static bool IsString(ParameterInfo parameter, bool byRef)
    {
        var type = parameter.ParameterType;
        if (type.IsByRef != byRef)
        {
            return false;
        }

        var element = byRef ? type.GetElementType() : type;
        // interop 把 System.String 写作 "String"（Il2CppSystem.String 呈现的结果也
        // 一样），因此元素名才是形态检测 —— 绝不用程序集限定字符串。
        return element is not null && element.Name == "String";
    }

    internal static void ReportGuardFailure(ref bool reported, Exception exception)
    {
        if (reported)
        {
            return;
        }

        reported = true;
        
    }
}

/// <summary>
/// 针对真正产生那条警告的重载的同一个防护。上面的两参数重载
/// 会汇入这一个，而这个方法正是记录 "Unable to add characters to font asset
/// [...] because the provided character list is Null or Empty" 然后返回 false、不
/// 触碰图集的那个。因此跳过空的情形正好得到原版的结果。
/// </summary>
internal sealed class TMP_FontAsset_TryAddCharacters_Missing_Patch : ModulePatch
{
    private static bool _guardFailed;

    protected override MethodBase GetTargetMethod()
    {
        // 第二个参数是 out/byref 字符串。byref 参数的 Type.Name 是 "String&"，因此直接比较名称的
        // 扫描永远匹配不上（那在 10:11 那次运行的补丁校验中抛出了
        // "TMPro.TMP_FontAsset.TryAddCharacters(string, out string, bool)" 并使该补丁未被应用）；
        // 元素类型才是识别它的形态。
        return FontGuardTarget.Resolve(
            "TryAddCharacters(string, out string, bool)",
            static parameters => parameters.Length == 3
                && TMP_FontAsset_TryAddCharacters_Patch.IsString(parameters[0], byRef: false)
                && TMP_FontAsset_TryAddCharacters_Patch.IsString(parameters[1], byRef: true)
                && parameters[2].ParameterType == typeof(bool),
            [typeof(string), typeof(string).MakeByRefType(), typeof(bool)]);
    }

    [PatchPrefix]
    private static bool Prefix(string characters, ref string missingCharacters, ref bool __result)
    {
        try
        {
            if (!string.IsNullOrEmpty(characters))
            {
                return true;
            }

            // 该输入下的原版行为：不报告缺失、不添加任何东西、返回 false。
            missingCharacters = characters;
            __result = false;
            return false;
        }
        catch (Exception exception)
        {
            TMP_FontAsset_TryAddCharacters_Patch.ReportGuardFailure(ref _guardFailed, exception);
            return true;
        }
    }
}

/// <summary>
/// 两个防护共享的目标解析：先按精确的带命名空间限定的 interop 签名
/// （<c>AccessTools.Method</c>，byref 参数按元数据里的写法拼写），然后对 interop 中类型写法不同的
/// 构建做一次参数形态扫描。
/// </summary>
internal static class FontGuardTarget
{
    internal static MethodBase Resolve(string expectedSignature, Func<ParameterInfo[], bool> shapeMatches, Type[] exact)
    {
        var exactMatch = AccessToolsMethod("TryAddCharacters", exact);
        if (exactMatch != null)
        {
            return exactMatch;
        }

        foreach (var method in typeof(TMP_FontAsset).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.Name != nameof(TMP_FontAsset.TryAddCharacters))
            {
                continue;
            }

            if (shapeMatches(method.GetParameters()))
            {
                
                return method;
            }
        }

        throw new MissingMethodException("TMPro.TMP_FontAsset." + expectedSignature +
            " does not exist in this game build; the empty-character guard cannot be installed.");
    }

    private static MethodBase AccessToolsMethod(string name, Type[] parameters)
    {
        try
        {
            return AccessTools.Method(typeof(TMP_FontAsset), name, parameters);
        }
        catch (Exception exception)
        {
            
            return null;
        }
    }
}
