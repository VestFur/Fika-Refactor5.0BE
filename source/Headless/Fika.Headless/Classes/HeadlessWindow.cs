using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;

namespace Fika.Headless.Classes;

/// <summary>
/// 以最小化方式启动专用主机自己的游戏窗口。
/// <para>
/// 这个决定存在于服务器为所选 profile 生成的 <c>HeadlessConfig.json</c> 中，
/// 也就是 <c>StartMinimized</c>，它与生成的
/// <c>StartHeadless.cmd</c> 来自同一个文件 —— 因此启动器与
/// 游戏对同一个值保持一致。专用主机从不被人观看，所以文档化的
/// 默认值是 <c>true</c>：文件缺失、属性缺失或文件不可读
/// 都会让主机以最小化方式启动。只有显式的 <c>false</c> 才恢复
/// 窗口。
/// </para>
/// <para>
/// 两半是刻意独立的：启动器中的 <c>start /min</c>
/// 覆盖从 .cmd 启动的主机，而下面的 Win32 调用覆盖
/// 其他每一种启动方式（快捷方式、SPT 启动器、另一个脚本）。
/// 它仅限 Windows，带守卫且有日志，且绝不能让插件加载失败。
/// </para>
/// </summary>
internal static class HeadlessWindow
{
    /// <summary>Win32 <c>SW_MINIMIZE</c>。</summary>
    private const int SwMinimize = 6;

    /// <summary>Win32 <c>SW_HIDE</c>。</summary>
    private const int SwHide = 0;

    /// <summary>
    /// (2026-10-27 W38 / TARGETS T6) 无头主机"只留控制台、不显示图形界面"：默认把游戏窗口**隐藏**（SW_HIDE）
    /// 而不是最小化（SW_MINIMIZE）——最小化仍占任务栏并保留可见窗口，而用户要桌面只剩 BepInEx 控制台。
    /// 置 false 可退回旧的最小化行为。
    /// </summary>
    internal static bool HideInsteadOfMinimize = true;

    /// <summary>Unity 的播放器窗口类，有多个窗口符合条件时优先使用。</summary>
    private const string UnityWindowClass = "UnityWndClass";

    /// <summary>放弃之前查找窗口的次数。每秒尝试一次。</summary>
    private const int MaxAttempts = 15;

    /// <summary>
    /// 第一个带有有意义的 <c>StartMinimized</c> 的 HeadlessConfig schema。
    /// 必须与服务器中的 <c>HeadlessLaunchSettings.CurrentSettingsVersion</c> 一致：
    /// 早于它的服务器构建会无条件写入 <c>StartMinimized: false</c>，所以
    /// 它们的文件无法与一次刻意的选择区分开，会被当作
    /// 该属性不存在来处理。
    /// </summary>
    private const int SettingsVersionThatCarriesTheWindowState = 2;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private static int _started;
    private static EnumWindowsProc _callback;
    private static IntPtr _unityWindow;
    private static IntPtr _fallbackWindow;
    private static uint _processId;
    private static readonly StringBuilder _className = new(256);

    /// <summary>
    /// 读取该 profile 的启动设置，把决定记录一次，并在它要求时
    /// 开始窗口最小化。可以安全地多次调用。
    /// </summary>
    internal static void ApplyStartMinimized(ManualLogSource log)
    {
        var decision = ReadLaunchSettings();

        if (!decision.StartMinimized)
        {
            log.LogInfo($"[Headless] Window: starting restored (StartMinimized=false); source: {decision.Source}");
            return;
        }

        log.LogInfo($"[Headless] Window: starting minimised (StartMinimized=true); source: {decision.Source}");

        if (HeadlessProcessFlags.IsUnattended())
        {
            log.LogInfo("[Headless] Window: this host runs -batchmode -nographics, it has no window to minimise");
            return;
        }

        if (!FikaHeadlessPlugin.IsRunningWindows)
        {
            log.LogInfo($"[Headless] Window: minimising is only implemented on Windows (OS: {SystemInfo.operatingSystem})");
            return;
        }

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _ = MinimiseWhenReady(log);
    }

    /// <summary>
    /// StartMinimized 的取值连同它的来源，这样日志行
    /// 就能解释这个决定，而无需任何人去打开那个文件。
    /// </summary>
    internal readonly struct Decision
    {
        internal Decision(bool startMinimized, string source)
        {
            StartMinimized = startMinimized;
            Source = source;
        }

        internal bool StartMinimized { get; }

        /// <summary>读取该值的文件的相对路径，或默认值适用的原因。</summary>
        internal string Source { get; }
    }

    /// <summary>
    /// 为本进程所启动的那个 profile 解析该设置。
    /// 一切查找都相对于当前目录和存放可执行文件的文件夹
    /// 进行；任何地方都不存储安装路径。
    /// </summary>
    internal static Decision ReadLaunchSettings()
    {
        var profileId = ProfileIdFromCommandLine();
        if (string.IsNullOrEmpty(profileId))
        {
            return new Decision(true, "default (no -token=<profileId> on the command line)");
        }

        foreach (var candidate in CandidateConfigFiles(profileId))
        {
            if (!File.Exists(candidate.FullPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(candidate.FullPath));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return new Decision(true, candidate.Display + " (not a settings object, using the default)");
                }

                var version = document.RootElement.TryGetProperty("SettingsVersion", out var versionElement) &&
                    versionElement.ValueKind == JsonValueKind.Number
                        ? versionElement.GetInt32()
                        : 0;
                var carriesTheWindowState = version >= SettingsVersionThatCarriesTheWindowState;

                if (carriesTheWindowState &&
                    document.RootElement.TryGetProperty("StartMinimized", out var element) &&
                    (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False))
                {
                    return new Decision(element.GetBoolean(), candidate.Display);
                }

                // 文件存在但没有可用的值：文档化的默认值是
                // 「最小化」，因此较旧的文件无法悄悄把
                // 窗口带回来。
                return new Decision(true, carriesTheWindowState
                    ? candidate.Display + " (no StartMinimized value, using the default)"
                    : candidate.Display + $" (SettingsVersion {version}, written before the window state was consumed, using the default)");
            }
            catch (Exception exception)
            {
                FikaHeadlessPlugin.FikaHeadlessLogger.LogWarning(
                    $"[Headless] Window: {candidate.Display} could not be read " +
                    $"({exception.GetType().Name}: {exception.Message}), using the default");
                return new Decision(true, candidate.Display + " (unreadable, using the default)");
            }
        }

        return new Decision(true, $"default (no HeadlessConfig.json for profile {profileId})");
    }

    /// <summary>启动器传入的 <c>-token=PROFILE_ID</c>，它指定 profile 文件夹。</summary>
    private static string ProfileIdFromCommandLine()
    {
        foreach (var argument in Environment.GetCommandLineArgs() ?? [])
        {
            if (argument.StartsWith("-token=", StringComparison.OrdinalIgnoreCase))
            {
                return argument["-token=".Length..].Trim('"');
            }
        }

        return null;
    }

    private readonly struct Candidate
    {
        internal Candidate(string fullPath, string display)
        {
            FullPath = fullPath;
            Display = display;
        }

        internal string FullPath { get; }
        internal string Display { get; }
    }

    /// <summary>
    /// 服务器按 profile 划分的脚本文件夹的已知位置，相对于
    /// 无头主机可能从其启动的每一个文件夹。
    /// </summary>
    private static IEnumerable<Candidate> CandidateConfigFiles(string profileId)
    {
        string[] relativeFolders =
        [
            "SPT_Runtime/user/mods/fika-server/assets/scripts",
            "user/mods/fika-server/assets/scripts"
        ];

        foreach (var root in SearchRoots())
        {
            foreach (var folder in relativeFolders)
            {
                var relative = $"{folder}/{profileId}/HeadlessConfig.json";
                var full = Path.Combine(root,
                    folder.Replace('/', Path.DirectorySeparatorChar), profileId, "HeadlessConfig.json");
                yield return new Candidate(full, relative);
            }
        }
    }

    /// <summary>
    /// 工作目录（启动器在它自己的文件夹里启动游戏）
    /// 与存放可执行文件的文件夹，各使用一次。
    /// </summary>
    private static IEnumerable<string> SearchRoots()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        List<string> roots = [Environment.CurrentDirectory, AppContext.BaseDirectory, ExecutableFolder()];
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(root);
            }
            catch (Exception)
            {
                continue;
            }

            if (seen.Add(full))
            {
                yield return full;
            }
        }
    }

    private static string ExecutableFolder()
    {
        try
        {
            return Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task MinimiseWhenReady(ManualLogSource log)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var window = FindGameWindow();
                if (window != IntPtr.Zero)
                {
                    Minimise(window, log);
                    return;
                }
            }
            catch (Exception exception)
            {
                log.LogWarning($"[Headless] Window: minimising is not available in this host " +
                    $"({exception.GetType().Name}: {exception.Message})");
                return;
            }

            try
            {
                await Task.Delay(RetryDelay).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }
        }

        log.LogWarning($"[Headless] Window: no game window appeared within {MaxAttempts}s, leaving it alone; " +
            "a host started through StartHeadless.cmd is still minimised by the launcher's 'start /min'");
    }

    private static void Minimise(IntPtr window, ManualLogSource log)
    {
        // (2026-10-27 W38 / T6) 无头主机默认直接隐藏窗口：桌面只留 BepInEx 控制台。
        if (HideInsteadOfMinimize)
        {
            ShowWindow(window, SwHide);
            log.LogInfo($"[Headless] Window: hid the game window (ShowWindow SW_HIDE); visibleNow={IsWindowVisible(window)}");
            return;
        }

        if (IsIconic(window))
        {
            log.LogInfo("[Headless] Window: the game window is already minimised");
            return;
        }

        // ShowWindow 报告的是*先前*的可见性，所以只有 IsIconic 能做判断。
        ShowWindow(window, SwMinimize);

        if (IsIconic(window))
        {
            log.LogInfo("[Headless] Window: minimised the game window (ShowWindow SW_MINIMIZE)");
            return;
        }

        log.LogWarning("[Headless] Window: the game window did not minimise (ShowWindow SW_MINIMIZE); " +
            "a host started through StartHeadless.cmd is still minimised by the launcher's 'start /min'");
    }

    /// <summary>
    /// 本进程自己的顶层窗口。<see cref="Process.MainWindowHandle"/>
    /// 会先被尝试；枚举是启动最初几秒的回退手段，
    /// 那时该属性仍可能报告没有窗口。
    /// </summary>
    private static IntPtr FindGameWindow()
    {
        try
        {
            // 必须使用新的 Process 对象：MainWindowHandle 在首次读取之后
            // 会被缓存。
            using var process = Process.GetCurrentProcess();
            var handle = process.MainWindowHandle;
            if (handle != IntPtr.Zero)
            {
                return handle;
            }
        }
        catch (Exception)
        {
            // 落到枚举路径。
        }

        _processId = (uint)Process.GetCurrentProcess().Id;
        _unityWindow = IntPtr.Zero;
        _fallbackWindow = IntPtr.Zero;
        _callback ??= OnWindow;
        EnumWindows(_callback, IntPtr.Zero);
        return _unityWindow != IntPtr.Zero ? _unityWindow : _fallbackWindow;
    }

    private static bool OnWindow(IntPtr window, IntPtr parameter)
    {
        GetWindowThreadProcessId(window, out var owner);
        if (owner != _processId || !IsWindowVisible(window))
        {
            return true;
        }

        if (GetClassName(window, _className, _className.Capacity) > 0 &&
            string.Equals(_className.ToString(), UnityWindowClass, StringComparison.Ordinal))
        {
            _unityWindow = window;
            return false;
        }

        if (_fallbackWindow == IntPtr.Zero)
        {
            _fallbackWindow = window;
        }

        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);
}
