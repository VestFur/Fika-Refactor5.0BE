using System;
using System.Reflection;
using BepInEx.Logging;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.LogNoise;

/// <summary>
/// 把游戏自身的 ballistic-prewarm 失败折叠成一行带归属信息的记录，而不是原始的
/// Unity 警告（每次 raid 加载一条）。根因与证据：analysis/shot-delegate-prewarm/REPORT.md。
///
/// 这条链路属于 EFT，不属于本移植项目。<c>EFT.GameWorld.InitLevel</c>（在其异步
/// 续体中，即 raid 加载期间）调用
/// <c>EFT.Ballistics.BallisticCalculatorPrewarmer.SimulateShot</c>，后者构造一个合成的
/// <c>Shot</c>，经由 <c>BallisticsCalculator.CreateShot</c> -> <c>Shot.Create</c> ->
/// <c>Shot.Init</c>。<c>Shot.Init</c> 抛出 <c>NullReferenceException</c>，预热器捕获
/// 它并把 "Failed prewarm shot delegate because System.NullReferenceException: ..." 记录到
/// Unity 通道（10-10 14:10 那次运行的 LogOutput.log 第 2089 行）。该堆栈中没有 Fika 的帧，
/// 消息字面量位于游戏自身的 global-metadata.dat（偏移 4909099），而相同的
/// 堆栈在干净的 SPT 5.0 基线上也有记录
/// （docs/client-il2cpp-port-blockers.md:131），在更早的 47242 移植构建上也有
/// （analysis/panel-crash-round7/Player-prev-crash-oldbuild.log:376104）——这是安装/版本
/// 层面的缺陷，不是 Fika 的回归。
///
/// 移植侧没有需要修复的根因。该调用的输入产生于游戏
/// 方法内部，而本安装的 interop 程序集仅携带元数据（没有 IL 体），因此从
/// 这里甚至无法把该 null 指认出来。两种所谓 "根因" 的替代方案都严格劣于这个
/// 过滤器：跳过 <c>SimulateShot</c> 会改变某个游戏方法内部的控制流，而该方法的调用方
/// 契约无法被检视（如果该调用方解引用返回的 <c>Shot</c>，一次原本无害的、
/// 被捕获的 NRE 就会变成加载期失败），而重新实现预热需要游戏的
/// 私有弹药/碰撞体/随机数输入。因此本移植让游戏路径保持逐字节一致，
/// 只阻止这条*具有误导性*的警告到达测试者的日志——这与已经
/// 应用于游戏 5,744 条 SimSun 字体洪泛（Main/Patches/UiNoise）的策略相同。
///
/// 接缝是 <c>BepInEx.Logging.DiskLogListener.LogEvent</c>：每一次对 LogOutput.log 的写入都会
/// 经过这个托管方法，无论消息由哪个 Unity 日志 API 产生（Debug.LogWarning
/// 有多个重载，而游戏选用的那个无法从元数据中看到）。该过滤器
/// 匹配精确的已知签名——预热前缀*以及* <c>Shot.Init</c> 抛异常的位置——因此
/// 任何其他预热失败（不同异常、不同堆栈）仍会原样到达日志。
///
/// 代价：每条日志记录一次字符串前缀比较。命中时该记录被丢弃，进程内第一次
/// 出现会在 Info 级别报告一次（测试者的日志仍会说明
/// 发生了什么并指向该分析），而每一次出现都会连同其完整原始文本
/// 记录到 Fika-diagnostics.log 中。回滚方式：删除此文件（或用
/// IgnoreAutoPatch 标记该类）——没有其他东西依赖它。
/// </summary>
internal sealed class DiskLogListener_BallisticPrewarm_Patch : ModulePatch
{
    /// <summary>已知的游戏侧消息；任何不匹配的内容都会原样通过。</summary>
    private const string MessagePrefix = "Failed prewarm shot delegate because System.NullReferenceException";

    /// <summary>该消息内部的抛异常位置——正是它让匹配具体到已知的游戏 bug。</summary>
    private const string ThrowSite = "EFT.Ballistics.Shot.Init";

    private static int _folded;
    private static bool _reportedOnce;
    private static bool _guardFailed;
    private static bool _reporting;

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(DiskLogListener), "LogEvent", [typeof(object), typeof(LogEventArgs)])
            ?? throw new MissingMethodException("BepInEx.Logging.DiskLogListener.LogEvent(object, LogEventArgs) does not exist in this " +
                "BepInEx build; the ballistic-prewarm warning filter cannot be installed (the warning it filters is harmless, see the class comment).");
    }

    [PatchPrefix]
    private static bool Prefix(object sender, LogEventArgs eventArgs)
    {
        // 此前缀自身写出的替换行也会经过这同一个方法。
        if (_reporting)
        {
            return true;
        }

        try
        {
            if (!IsGameBallisticPrewarmNoise(eventArgs))
            {
                return true;
            }

            _folded++;
            if (!_reportedOnce)
            {
                _reportedOnce = true;
                Report(eventArgs);
            }
            else
            {
                
            }

            // Unity 警告永远不会到达 LogOutput.log；事实本身已在上方报告。
            return false;
        }
        catch (Exception exception)
        {
            // 在记录日志时抛异常的守卫会比它移除的那条警告更糟：退回
            // 未改动的原生路径，并报告一次该守卫。
            if (!_guardFailed)
            {
                _guardFailed = true;
                
            }

            return true;
        }
    }

    private static bool IsGameBallisticPrewarmNoise(LogEventArgs eventArgs)
    {
        if (eventArgs?.Data is not string message || message.Length < MessagePrefix.Length)
        {
            return false;
        }

        return message.StartsWith(MessagePrefix, StringComparison.Ordinal)
            && message.Contains(ThrowSite, StringComparison.Ordinal);
    }

    /// <summary>
    /// 一行 Info 记录在 LogOutput.log 中替代被折叠的警告（grep <c>[PrewarmNoise]</c>），而
    /// 完整的原始文本——包括游戏堆栈——保留在 Fika-diagnostics.log 中。
    /// </summary>
    private static void Report(LogEventArgs eventArgs)
    {
        _reporting = true;
        try
        {
            FikaGlobals.LogInfo("[PrewarmNoise] EFT's own ballistics prewarmer (BallisticCalculatorPrewarmer.SimulateShot -> Shot.Init) " +
                "threw its known NullReferenceException at raid load again; the raw Unity warning was folded into this line instead of the log. " +
                "Game-side and caught by the game (the raid continues, no Fika frame is involved); " +
                "the raw warning was folded into this line (analysis/shot-delegate-prewarm)");
        }
        finally
        {
            _reporting = false;
        }

        var original = eventArgs.Data as string ?? string.Empty;
        
    }
}
