using System;
using System.Linq;

namespace Fika.Headless.Classes;

/// <summary>
/// 此进程允许以何种方式运行。
/// <see cref="HeadlessRunMode.Auto"/> 是默认值：模式由
/// 进程自身决定（选中的档案，或明确的命令行
/// 选择加入），绝不来自插件加载时的猜测。
/// </summary>
internal enum HeadlessRunMode
{
    /// <summary>由进程决定：先看命令行，再看选中的档案。</summary>
    Auto = 0,
    /// <summary>强制作为专用主机。用户保证这不是游玩安装。</summary>
    Headless = 1,
    /// <summary>从不动作：插件只记录日志，完全不触碰游戏。</summary>
    Disabled = 2
}

/// <summary>
/// 进程级选择加入。正常的游玩启动不会产生其中任何一个：
/// SPT 启动器启动游戏时不带图形标志，游玩档案
/// 也从不命名为 <c>headless_</c>。因此可以安全地把它们视为
/// 以专用主机运行的明确请求。
/// </summary>
internal static class HeadlessProcessFlags
{
    private static readonly string[] _headlessOptIns = ["-headless", "--headless"];
    private static readonly string[] _unattendedFlags = ["-batchmode", "-nographics"];

    private static string[] Arguments => Environment.GetCommandLineArgs() ?? [];

    private static bool HasArgument(string[] arguments, string flag) =>
        arguments.Any(argument => string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>命令行上明确、刻意的宿主选择加入。</summary>
    internal static bool HasHeadlessOptIn(out string matched)
    {
        var arguments = Arguments;
        matched = _headlessOptIns.FirstOrDefault(flag => HasArgument(arguments, flag));
        return matched != null;
    }

    /// <summary>
    /// 仅对无人值守、无显示的进程为 true：Unity 同时收到了
    /// <c>-batchmode</c> 与 <c>-nographics</c>。游玩的客户端由 SPT 启动器
    /// 启动，永远不可能匹配此条件。
    /// </summary>
    internal static bool IsUnattended()
    {
        var arguments = Arguments;
        return _unattendedFlags.All(flag => HasArgument(arguments, flag));
    }
}
