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
/// Brings the dedicated host's own game window up minimised.
/// <para>
/// The decision lives in the server generated <c>HeadlessConfig.json</c> of the
/// selected profile (<c>StartMinimized</c>), which is the same file the
/// generated <c>StartHeadless.cmd</c> is rendered from - so the launcher and the
/// game agree on one value. A dedicated host is never watched, so the documented
/// default is <c>true</c>: an absent file, an absent property or an unreadable
/// file all start the host minimised. Only an explicit <c>false</c> restores the
/// window.
/// </para>
/// <para>
/// Both halves are deliberately independent: <c>start /min</c> in the launcher
/// covers a host that is started from the .cmd, and the Win32 call below covers
/// every other way of starting one (shortcut, SPT launcher, another script). It
/// is Windows only, guarded and logged, and can never fail the plugin load.
/// </para>
/// </summary>
internal static class HeadlessWindow
{
    /// <summary>Win32 <c>SW_MINIMIZE</c>.</summary>
    private const int SwMinimize = 6;

    /// <summary>Unity's player window class, preferred when several windows qualify.</summary>
    private const string UnityWindowClass = "UnityWndClass";

    /// <summary>How often the window is looked for before giving up. One attempt per second.</summary>
    private const int MaxAttempts = 15;

    /// <summary>
    /// HeadlessConfig schema that first carried a meaningful <c>StartMinimized</c>.
    /// Must match <c>HeadlessLaunchSettings.CurrentSettingsVersion</c> in the server:
    /// server builds before it wrote <c>StartMinimized: false</c> unconditionally, so
    /// their file cannot be told apart from a deliberate choice and is treated as if
    /// the property were absent.
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
    /// Reads the profile's launch settings, logs the decision once, and starts
    /// the window minimisation when it says so. Safe to call more than once.
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
    /// The StartMinimized value together with where it came from, so the log
    /// line explains the decision without anyone having to open the file.
    /// </summary>
    internal readonly struct Decision
    {
        internal Decision(bool startMinimized, string source)
        {
            StartMinimized = startMinimized;
            Source = source;
        }

        internal bool StartMinimized { get; }

        /// <summary>Relative path of the file the value was read from, or why the default applies.</summary>
        internal string Source { get; }
    }

    /// <summary>
    /// Resolves the setting for the profile this process was started for.
    /// Everything is looked up relative to the current directory and the folder
    /// holding the executable; no install path is stored anywhere.
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

                // The file exists but carries no usable value: the documented
                // default is "minimised", so an older file cannot silently bring
                // the window back.
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

    /// <summary>The launcher's <c>-token=PROFILE_ID</c>, which names the profile folder.</summary>
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
    /// The known locations of the server's per-profile scripts folder, relative
    /// to every folder a headless host can be started from.
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
    /// The working directory (the launcher starts the game in its own folder)
    /// and the folder holding the executable, each used once.
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
        if (IsIconic(window))
        {
            log.LogInfo("[Headless] Window: the game window is already minimised");
            return;
        }

        // ShowWindow reports the *previous* visibility, so only IsIconic decides.
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
    /// The process's own top-level window. <see cref="Process.MainWindowHandle"/>
    /// is tried first; the enumeration is the fallback for the first seconds of
    /// startup, while that property can still report no window.
    /// </summary>
    private static IntPtr FindGameWindow()
    {
        try
        {
            // A fresh Process object is required: MainWindowHandle is cached
            // after its first read.
            using var process = Process.GetCurrentProcess();
            var handle = process.MainWindowHandle;
            if (handle != IntPtr.Zero)
            {
                return handle;
            }
        }
        catch (Exception)
        {
            // Fall through to the enumeration.
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
