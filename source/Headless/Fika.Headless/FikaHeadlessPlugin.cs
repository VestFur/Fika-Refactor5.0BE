using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Comfort.Common;
using Diz.Jobs;
using EFT;
using EFT.Communications;
using EFT.UI;
using EFT.UI.Matchmaker;
using Fika.Core;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Patches.LocalGame;
using Fika.Core.Main.Patches.Overrides;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models.Headless;
using Fika.Core.Networking.Packets.Communication;

#if DEBUG
using Fika.Core.Networking.Websocket.Headless;
#endif
using Fika.Core.UI.Patches;
using Fika.Core.UI.Patches.MainMenuUI;
using Fika.Headless.Classes;
using Fika.Headless.Patches;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using SPTushonka.Custom.Patches;
using SPTushonka.Custom.Utils;
using SPTushonka.Reflection.Patching;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Fika.Headless;

[BepInPlugin("com.fika.headless", "Fika.Headless", HeadlessVersion)]
[BepInDependency("com.fika.core", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("com.sptushonka.custom", BepInDependency.DependencyFlags.HardDependency)]
public class FikaHeadlessPlugin : BasePlugin
{
    // 1.5.3: the plugin is mode-gated. It only arms the dedicated-host payload
    // for a confirmed host; in normal play it installs nothing at all.
    public const string HeadlessVersion = "1.5.5";

    public static FikaHeadlessPlugin Instance { get; private set; }
    public static ManualLogSource FikaHeadlessLogger;
    public static bool PlayerDisposeError;

    /// <summary>
    /// 1 once the dedicated-host payload (injected native types, resident
    /// ticker, ~100 patches, the headless raid factory) is installed.
    /// Everything destructive checks this first: while it is 0 this process is
    /// a normal playing install and must stay untouched.
    /// </summary>
    private static int _armed;
    /// <summary>1 once the decision is "normal play": the payload can never be armed in this process.</summary>
    private static int _inert;
    private static string _armedReason;

    public static bool IsArmed => Volatile.Read(ref _armed) == 1;
    public static bool IsInert => Volatile.Read(ref _inert) == 1;
    public static string ArmedReason => Volatile.Read(ref _armedReason);
    public static bool IsRunningWindows
    {
        get
        {
            return SystemInfo.operatingSystemFamily == OperatingSystemFamily.Windows;
        }
    }
    public bool CanHost { get; internal set; }
    public int CurrentRaidCount { get; private set; }

    private HeadlessWebSocket _fikaHeadlessWebSocket;
    private float _gcCounter;
    private float _gcPoint;
    private bool _invalidPluginsFound;
    private int _restartAfterAmountOfRaids;
    private bool _hasVerified;
    private int _profileVerificationStarted;
    private int _raidGeneration;
    private string _raidReservationToken;
    private int _raidSocketGeneration;
    /// <summary>Patches that were armed, so a forced mode can be undone again.</summary>
    private readonly List<Type> _installedPatchTypes = [];

    public static ConfigEntry<string> RunMode { get; private set; }
    public static ConfigEntry<int> UpdateRate { get; private set; }
    public static ConfigEntry<int> RAMCleanInterval { get; private set; }
    public static ConfigEntry<bool> ShouldBotsSleep { get; private set; }
    public static ConfigEntry<bool> ShouldDestroyGraphics { get; private set; }
    public static ConfigEntry<bool> DestroyRenderersOnSceneLoad { get; private set; }

#if DEBUG
    public BetterAudio BetterAudio
    {
        get
        {
            return Singleton<BetterAudio>.Instance;
        }
    }

    public GUISounds GUISounds
    {
        get
        {
            return Singleton<GUISounds>.Instance;
        }
    }
#endif

    /// <summary>
    /// BepInEx loads plugins before the game knows which profile it will use,
    /// so nothing that can damage a graphical install may happen here.
    /// <para>
    /// Phase 1 (this method): bind the config, log, and install the two profile
    /// observers. Phase 2 (<see cref="ArmHeadlessHost"/>) installs the payload
    /// only for a confirmed dedicated host - either because the process itself
    /// says so (config <c>Mode=Headless</c>, <c>-headless</c>, or the display
    /// less <c>-batchmode -nographics</c> combination), or because the selected
    /// profile is the server-generated <c>headless_</c> one.
    /// </para>
    /// </summary>
    public override void Load()
    {
        Instance = this;
        _gcCounter = 0;

        TrySetConsoleTitle();

        FikaHeadlessLogger = Log;

        SetupConfig();

        _gcPoint = RAMCleanInterval.Value * 60f;

        Log.LogInfo("[Headless] Plugin start: Fika.Headless " + HeadlessVersion + $" loaded (OS: {SystemInfo.operatingSystem})");
        Log.LogInfo($"[Headless] Plugin start: config mode={RunMode.Value}, updateRate={UpdateRate.Value}, ramCleanInterval={RAMCleanInterval.Value}, " +
            $"botsSleep={ShouldBotsSleep.Value}, destroyGraphics={ShouldDestroyGraphics.Value}, destroyRenderers={DestroyRenderersOnSceneLoad.Value}");
        if (!IsRunningWindows)
        {
            Log.LogWarning("You are not running an officially supported operating system by Fika. Minimal support will be given.");
        }

        // Nothing may be installed before this point: no native type injection,
        // no ticker, no unpatching of other plugins, no HTTP request, no log
        // cleanup. All of that belongs to the dedicated-host payload.
        var configuredMode = ParseRunMode();

        if (configuredMode == HeadlessRunMode.Disabled)
        {
            Log.LogInfo("[Headless] Mode: Disabled -> normal play (config Mode=Disabled), the plugin stays inert");
            return;
        }

        InstallObserverPatches();

        if (configuredMode == HeadlessRunMode.Headless)
        {
            ArmHeadlessHost("Headless -> dedicated host (config Mode=Headless)", duringLoad: true);
            return;
        }

        if (HeadlessProcessFlags.HasHeadlessOptIn(out var flag))
        {
            ArmHeadlessHost($"Auto -> dedicated host (command line opt-in {flag})", duringLoad: true);
            return;
        }

        if (HeadlessProcessFlags.IsUnattended())
        {
            ArmHeadlessHost("Auto -> dedicated host (process flags -batchmode -nographics: there is no display, so this cannot be a playing install)", duringLoad: true);
            return;
        }

        Log.LogInfo("[Headless] Mode: Auto -> waiting for the selected profile; only the profile observers are installed and the plugin stays inert until then");
    }

    /// <summary>
    /// Reads <c>Mode</c> from the config file. An unknown value falls back to
    /// <see cref="HeadlessRunMode.Auto"/>, which is the only safe choice.
    /// </summary>
    private static HeadlessRunMode ParseRunMode()
    {
        var raw = RunMode.Value;
        if (Enum.TryParse(raw, true, out HeadlessRunMode mode))
        {
            return mode;
        }

        FikaHeadlessLogger.LogWarning($"[Headless] Mode: config value '{raw}' is not one of Auto/Headless/Disabled, using Auto");
        return HeadlessRunMode.Auto;
    }

    /// <summary>
    /// The only patches that exist before the mode is known. They observe the
    /// selected profile and log; in normal play they return immediately.
    /// </summary>
    private void InstallObserverPatches()
    {
        Fika.Core.PatchManager manager = new(this, true);
        var installed = 0;
        foreach (var type in HeadlessPatchCatalog.ObserverPatches)
        {
            try
            {
                manager.EnablePatch((ModulePatch)Activator.CreateInstance(type, true));
                installed++;
            }
            catch (Exception exception)
            {
                Log.LogError($"[Headless] Mode: the profile observer {type.FullName} was not applied: {exception.Message}");
            }
        }

        Log.LogInfo($"[Headless] Mode: {installed}/{HeadlessPatchCatalog.ObserverPatches.Length} profile observers installed (no renderers, no cameras, no game objects)");
    }

    /// <summary>
    /// Installs the dedicated-host payload. Reached only when the process is a
    /// confirmed host: at load from the config or the command line, otherwise
    /// from the profile observer once the selected profile turned out to be
    /// the server-generated <c>headless_</c> one.
    /// </summary>
    /// <param name="reason">Decision text that is logged verbatim behind "[Headless] Mode: ".</param>
    /// <param name="duringLoad">
    /// True when the payload is armed while BepInEx is still loading plugins
    /// (the settings seam has not run yet). False for the profile gate, which
    /// is too late for <c>ClientApplicationInitOperation.LoadUserSettings</c>.
    /// </param>
    private void ArmHeadlessHost(string reason, bool duringLoad)
    {
        if (IsInert)
        {
            Log.LogError("[Headless] Refusing to start: this does not look like a dedicated headless host. Remove Fika.Headless.dll from a playing install.");
            return;
        }

        if (Interlocked.Exchange(ref _armed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _armedReason, reason);

        var assembly = typeof(FikaHeadlessPlugin).Assembly;
        List<Type> patches;
        try
        {
            patches = HeadlessPatchCatalog.GetDeferredPatches(assembly, ShouldDestroyGraphics.Value);
        }
        catch (Exception exception)
        {
            Log.LogError($"[Headless] Mode: enumerating the deferred patches FAILED, nothing destructive was installed: {exception}");
            patches = [];
        }

        Log.LogInfo($"[Headless] Mode: {reason}, arming {patches.Count} patches " +
            $"(destroyGraphics={ShouldDestroyGraphics.Value}, destroyRenderers={DestroyRenderersOnSceneLoad.Value})");

        // Injected IL2CPP types first, exactly like the original load order.
        try
        {
            HeadlessNativeTypes.RegisterAll(assembly, Log);
        }
        catch (Exception exception)
        {
            Log.LogError($"[Headless] Mode: registering the native types FAILED, this host cannot create raids: {exception}");
        }

        // Resident ticker: RAM/GC maintenance, host only.
        try
        {
            AddComponent<HeadlessTicker>();
        }
        catch (Exception exception)
        {
            Log.LogError($"[Headless] Mode: adding the headless ticker FAILED: {exception}");
        }

        // Client plugin patches the host does not need. Never done in normal
        // play: Fika.Core needs them for cooperative play.
        try
        {
            DisableFikaCorePatches();
        }
        catch (Exception exception)
        {
            Log.LogError($"[Headless] Mode: disabling the Fika.Core patches FAILED: {exception}");
        }

        try
        {
            DisableSPTPatches();
        }
        catch (Exception exception)
        {
            Log.LogError($"[Headless] Mode: disabling the SPT patch FAILED: {exception}");
        }

        // From here on Fika.Core takes its headless code paths.
        FikaBackendUtils.IsHeadless = true;
        Log.LogInfo("[Headless] Plugin start: marked this process as a dedicated host (FikaBackendUtils.IsHeadless=true)");

        // The deferred patches: manual per-class installation, so an unarmed
        // plugin has installed none of them.
        Fika.Core.PatchManager manager = new(this, true);
        var installed = 0;
        List<string> failed = [];
        foreach (var type in patches)
        {
            try
            {
                manager.EnablePatch((ModulePatch)Activator.CreateInstance(type, true));
                _installedPatchTypes.Add(type);
                installed++;
            }
            catch (Exception exception)
            {
                failed.Add(type.Name);
                Log.LogError($"[Headless] Mode: {type.FullName} was not applied: {exception.Message}");
            }
        }

        Log.LogInfo($"[Headless] Mode: {installed}/{patches.Count} patches armed" +
            (failed.Count == 0 ? string.Empty : $", not applied: {string.Join(", ", failed)}"));

        if (!ShouldBotsSleep.Value)
        {
            try
            {
                Fika.Core.PatchManager botManager = new(this, true);
                botManager.EnablePatch(new BotStandBy_Update_Transpiler());
            }
            catch (Exception exception)
            {
                Log.LogError($"[Headless] Mode: BotStandBy_Update_Transpiler was not applied: {exception.Message}");
            }
        }

        // Host-only maintenance. None of this may run in a playing install.
        try
        {
            GetHeadlessRestartAfterRaidAmount();
        }
        catch (Exception exception)
        {
            Log.LogWarning($"[Headless] Mode: could not read the restart-after-raids setting, keeping {_restartAfterAmountOfRaids}: {exception.Message}");
        }

        CleanupLogFiles();

        // A profile-armed host missed ClientApplicationInitOperation.LoadUserSettings,
        // which is the seam the settings patch replaces. Apply the same headless
        // settings now that the mode is known.
        if (!duringLoad)
        {
            FikaHeadlessSettingsManager.ApplyDeferred();
        }

        // Startup diagnostics for the dedicated host. Both are guarded: a host
        // that cannot describe its assemblies or its window must still be able
        // to host, so neither may throw out of the arming path.
        try
        {
            HeadlessAssemblies.Report(Log);
        }
        catch (Exception exception)
        {
            Log.LogWarning($"[Headless] Assemblies: the shadowing check FAILED: {exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            HeadlessWindow.ApplyStartMinimized(Log);
        }
        catch (Exception exception)
        {
            Log.LogWarning($"[Headless] Window: the start-minimized setting could not be applied: {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Removes every headless patch and clears the armed flag. Only used when
    /// the mode was forced by the user but the selected profile turned out to
    /// be a normal playing profile.
    /// </summary>
    private void DisarmHeadlessPatches()
    {
        foreach (var type in _installedPatchTypes)
        {
            // The SPT loader registers every ModulePatch under its type name,
            // which is what the client plugin uses to unpatch its own patches.
            // Try the full name as well in case that naming ever changes.
            foreach (var id in new[] { type.Name, type.FullName })
            {
                try
                {
                    HarmonyLib.Harmony.UnpatchID(id);
                }
                catch (Exception exception)
                {
                    Log.LogWarning($"[Headless] Mode: could not remove {id}: {exception.Message}");
                }
            }

            if (IsStillPatched(type))
            {
                Log.LogError($"[Headless] Mode: {type.FullName} is STILL installed. Restart this install without " +
                    "'Mode = Headless' and without the -headless flag to get a clean playing client.");
            }
        }

        _installedPatchTypes.Clear();
        Volatile.Write(ref _armed, 0);
        FikaBackendUtils.IsHeadless = false;
        Log.LogError("[Headless] Mode: the headless patches were removed; this process continues as an ordinary game");
    }

    /// <summary>
    /// True when <paramref name="type"/> still owns a Harmony prefix or postfix
    /// on its target, i.e. the forced mode could not be undone cleanly.
    /// </summary>
    private static bool IsStillPatched(Type type)
    {
        try
        {
            var instance = (ModulePatch)Activator.CreateInstance(type, true);
            var getter = type.GetMethod("GetTargetMethod", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (getter?.Invoke(instance, null) is not System.Reflection.MethodBase target)
            {
                return false;
            }

            var info = HarmonyLib.Harmony.GetPatchInfo(target);
            if (info == null)
            {
                return false;
            }

            return info.Prefixes.Any(patch => patch.PatchMethod.DeclaringType == type)
                || info.Postfixes.Any(patch => patch.PatchMethod.DeclaringType == type);
        }
        catch
        {
            // Reporting is best effort; never fail the running game for it.
            return false;
        }
    }

    /// <summary>
    /// The selected profile is a normal playing profile, so this install is not
    /// a dedicated host. Logged once; the plugin stays inert for the rest of
    /// the process and the game is never quit or touched. Called from the
    /// profile observers only.
    /// </summary>
    internal void NoteNormalPlay(string source, string nickname)
    {
        var profile = string.IsNullOrEmpty(nickname) ? "<none>" : nickname;

        if (IsArmed)
        {
            Log.LogError($"[Headless] Refusing to run as a dedicated host: profile '{profile}' is not a headless host, but the mode was forced " +
                $"(config Mode or command line). Removing every headless patch so this playing install is left untouched. (source: {source})");
            DisarmHeadlessPatches();
        }

        if (Interlocked.Exchange(ref _inert, 1) != 0)
        {
            return;
        }

        Log.LogInfo($"[Headless] Mode: Auto -> normal play (profile '{profile}' is not a headless host), the plugin stays inert");
        Log.LogInfo($"[Headless] Mode: no renderer, camera, asset, settings or UI patch is installed; Fika.Headless.dll can stay in this install (source: {source})");
    }

    private void TrySetConsoleTitle()
    {
        var title = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("-title=", StringComparison.OrdinalIgnoreCase))
            ?["-title=".Length..];

        if (!string.IsNullOrEmpty(title))
        {
            Log.LogInfo($"Using custom window title: {title}");
            // Batchmode/-nographics processes may not have a console handle.
            // Console.Title throws IOException in that environment; changing
            // the optional title must never prevent the headless plugin from
            // loading its patches and websocket.
            try
            {
                Console.Title = $"Fika Headless {HeadlessVersion} - {title}";
            }
            catch (IOException exception)
            {
                Log.LogWarning($"Unable to set console title in this host: {exception.Message}");
            }
        }
    }

    internal void Tick()
    {
        // The ticker only exists on an armed host, but keep the guard: the
        // memory management below unloads assets and must never run in a
        // playing install.
        if (!IsArmed)
        {
            return;
        }

        _gcCounter += Time.unscaledDeltaTime;
        if (_gcCounter > _gcPoint)
        {
            _gcCounter -= _gcPoint;
            if (FikaGlobals.IsInRaid)
            {
                GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
                GarbageCollector.CollectIncremental(1000000);
                GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
            }
            else if (!FikaBackendUtils.IsTransit)
            {
                Resources.UnloadUnusedAssets();
                InGameMemoryManagement.Collect(2, Il2CppSystem.GCCollectionMode.Forced, true, true, true);
            }
        }
    }

    private static void DisableSPTPatches()
    {
        HarmonyLib.Harmony.UnpatchID(nameof(SetPreRaidSettingsScreenDefaultsPatch));
    }

    /// <summary>
    /// Disables patches from Fika.Core that the headless does not need
    /// </summary>
    private static void DisableFikaCorePatches()
    {
        HarmonyLib.Harmony.UnpatchID(nameof(TarkovApplication_InitNotificationManager_Patch));
        HarmonyLib.Harmony.UnpatchID(nameof(MenuScreen_Awake_Patch));
        HarmonyLib.Harmony.UnpatchID(nameof(TarkovApplication_LocalGameCreator_Patch));
    }

#if DEBUG
    private void StartDebugGame()
    {
        var rawData = @"{""Type"":""HeadlessStartRaid"",""StartHeadlessRequest"":{""headlessSessionID"":""6840a12f76cac3fada302293"",""time"":""CURR"",""locationId"":""5b0fc42d86f7744a585f9105"",""spawnPlace"":""SamePlace"",""metabolismDisabled"":false,""timeAndWeatherSettings"":{""isRandomTime"":false,""isRandomWeather"":false,""cloudinessType"":""Clear"",""rainType"":""NoRain"",""windType"":""Light"",""fogType"":""NoFog"",""timeFlowType"":""x1"",""hourOfDay"":-1},""botSettings"":{""isScavWars"":false,""botAmount"":""AsOnline""},""wavesSettings"":{""botAmount"":""AsOnline"",""botDifficulty"":""AsOnline"",""isBosses"":true,""isTaggedAndCursed"":false},""side"":""Pmc"",""customWeather"":false}}";
        var data = System.Text.Json.JsonSerializer.Deserialize<StartRaid>(rawData, HeadlessWebSocket.RequestOptions);

        OnFikaStartRaid(data.StartHeadlessRequest);
    }
#endif

    /// <summary>
    /// Cleans up all old log files
    /// </summary>
    private void CleanupLogFiles()
    {
        try
        {
            var exePath = AppContext.BaseDirectory;
            var logsPath = Path.Combine(exePath, "Logs");

            if (!Directory.Exists(logsPath))
            {
                Log.LogError("CleanupLogFiles: '/Logs' folder not found!");
                return;
            }

            DirectoryInfo logsDir = new(logsPath);

            var directoriesToDelete = logsDir.EnumerateDirectories()
                                         .OrderByDescending(d => d.LastWriteTime)
                                         .Skip(2); // keep the latest 3 folders, 1 is added which leaves 3

            foreach (var dir in directoriesToDelete)
            {
                try
                {
                    Log.LogInfo($"CleanupLogFiles: Deleting directory '{dir.FullName}'");
                    dir.Delete(true);
                }
                catch (IOException ioEx)
                {
                    Log.LogWarning($"CleanupLogFiles: I/O error deleting '{dir.FullName}': {ioEx.Message}");
                }
                catch (UnauthorizedAccessException uaEx)
                {
                    Log.LogWarning($"CleanupLogFiles: Access denied for '{dir.FullName}': {uaEx.Message}");
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"CleanupLogFiles: Unexpected error for '{dir.FullName}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogError($"CleanupLogFiles: Fatal error: {ex.Message}");
        }
    }

    /// <summary>
    /// Initializes the <see cref="ConfigFile"/> settings
    /// </summary>
    private void SetupConfig()
    {
        RunMode = Config.Bind("General", "Mode", nameof(HeadlessRunMode.Auto),
            new ConfigDescription(
                "Auto: this install is a dedicated host only when the selected profile is a headless_ profile, " +
                "when -headless/--headless is passed, or when the process runs with -batchmode -nographics; " +
                "otherwise the plugin installs nothing and the game plays normally. " +
                "Headless: force the dedicated host at plugin load. " +
                "Disabled: never install anything, the plugin only logs.",
                new AcceptableValueList<string>(
                    nameof(HeadlessRunMode.Auto), nameof(HeadlessRunMode.Headless), nameof(HeadlessRunMode.Disabled))));

        UpdateRate = Config.Bind("Headless", "Update Rate", 60,
            new ConfigDescription("How often the server should update (frame cap / tick rate). Only works if your machine can actually reach the selected setting",
            new AcceptableValueRange<int>(30, 120)));

        RAMCleanInterval = Config.Bind("Headless", "RAM Clean Interval", 15,
            new ConfigDescription("How often in minutes the RAM cleaner should run outside of raids",
            new AcceptableValueRange<int>(2, 30)));

        ShouldBotsSleep = Config.Bind("Headless", "Bot sleeping", false,
            new ConfigDescription("Should the headless host allow bots to sleep? (BSG bot sleeping logic)"));

        ShouldDestroyGraphics = Config.Bind("Headless", "Destroy Graphics", true,
            new ConfigDescription("If the headless plugin should run patches to disable various graphical elements"));

        DestroyRenderersOnSceneLoad = Config.Bind("Headless", "Destroy Renderers", true,
            new ConfigDescription("If the headless plugin should hook scene loading to disable unnecessary renderers as well as unloading all materials (Requires 'Destroy Graphics' to be enabled)"));
    }

    /// <summary>
    /// When a request to start a raid is received
    /// </summary>
    /// <param name="request"></param>
    /// <exception cref="NullReferenceException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public void OnFikaStartRaid(StartHeadlessRequest request)
    {
        try
        {
            // StartHeadlessRequest is a value type: never use the null-conditional operator on it.
            Log.LogInfo($"[Headless] Start request: location={request.LocationId}, side={request.Side}, " +
                $"time={request.Time}, canHost={CanHost}, reservationTokenLength={request.ReservationToken?.Length ?? 0}");

            if (!TarkovApplication.Exist(out var tarkovApplication))
            {
                Log.LogError("[Headless] Start request rejected: no TarkovApplication in this process");
                return;
            }

            if (!CanHost)
            {
                Log.LogError("[Headless] Start request rejected: this host is not ready yet (CanHost=false)");
                return;
            }

            var session = tarkovApplication.Session;
            if (session == null)
            {
                Log.LogError("[Headless] Start request rejected: backend session was null");
                return;
            }

            // TarkovApplication.Session is an IEftSession IL2CPP proxy. The
            // concrete ClientBackendSession owns the loaded location table;
            // accessing it through the interface proxy can return null.
            var backendSession = session.TryCast<ClientBackendSession>();
            var locations = backendSession?.LocationSettings?.locations;
            Log.LogInfo($"Headless start state: location={request.LocationId}, backendSession={(backendSession == null ? "null" : "ready")}, locations={(locations == null ? "null" : "ready")}");
            if (locations == null)
            {
                Log.LogError("[Headless] Start request rejected: location settings are not loaded yet");
                return;
            }
            if (!locations.TryGetValue(request.LocationId, out var location) || location == null)
            {
                Log.LogError($"[Headless] Start request rejected: location {request.LocationId} does not exist in the loaded location table");
                return;
            }

            FikaBackendUtils.CustomRaidSettings = request.CustomRaidSettings;
            Log.LogInfo($"Received CustomRaidSettings: {request.CustomRaidSettings}");

            Log.LogInfo($"Starting on location {location.Name}");
            CanHost = false;
            _raidReservationToken = request.ReservationToken;
            _raidSocketGeneration = _fikaHeadlessWebSocket?.ConnectionGeneration ?? 0;
            var raidGeneration = Interlocked.Increment(ref _raidGeneration);
            ToggleFramelimit(false);
            _ = MainThreadDispatcher.RunAsync(() => BeginFikaStartRaid(request, session, tarkovApplication, location))
                .ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        Log.LogError($"Headless start task failed: {task.Exception?.GetBaseException()}");
                        // Preflight exceptions occur before BeginFikaStartRaid's
                        // match-creation catch. Abandon that reservation by
                        // replacing its socket; the server ends any partial
                        // room before it offers this host again.
                        MainThreadDispatcher.Post(() =>
                        {
                            if (raidGeneration != Volatile.Read(ref _raidGeneration) || CanHost) return;
                            if (OnReady()) CanHost = true;
                            _fikaHeadlessWebSocket?.Reconnect();
                        });
                    }
                }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Log.LogError($"OnFikaStartRaid failed: {ex}");
        }
    }

    /// <summary>
    /// Verifies that this headless client is valid for hosting
    /// </summary>
    /// <returns></returns>
    public async Task RunPluginValidation()
    {
        if (_hasVerified)
        {
            return;
        }

        Log.LogInfo("[Headless] Validation: running plugin validation");
        Log.LogInfo("[Headless] Validation: waiting for Fika locales and configuration");
        var waitedMilliseconds = 0;
        while (!FikaPlugin.Instance.LocalesLoaded)
        {
            await Task.Delay(100);
            waitedMilliseconds += 100;
            // Never wait silently: a stalled locale/configuration load is the
            // first thing to rule out when a headless never becomes offerable.
            if (waitedMilliseconds % 10000 == 0)
            {
                Log.LogError($"[Headless] Validation: Fika locales are still not loaded after {waitedMilliseconds / 1000}s " +
                    $"(initializationError={FikaPlugin.Instance.InitializationError ?? "none"})");
            }
        }
        Log.LogInfo($"[Headless] Validation: Fika locales and configuration ready after {waitedMilliseconds / 1000.0:0.0}s");
        await VerifyPlugins();

        await Task.Delay(1000);

        FikaPlugin.Instance.Settings.AutoExtract.Value = true;
        FikaPlugin.Instance.Settings.QuestTypesToShareAndReceive.Value = 0;
        FikaPlugin.Instance.Settings.ConnectionTimeout.Value = 30;
        FikaPlugin.Instance.Settings.UseNamePlates.Value = false;

        FikaPlugin.Instance.Settings.AllowFreeCam = true;
        FikaPlugin.Instance.Settings.AllowSpectateFreeCam = true;

        Log.LogInfo("[Headless] Validation: plugin validation completed");

        if (!TarkovApplication.Exist(out var tarkovApplication))
        {
            Log.LogWarning("[Headless] Validation: no TarkovApplication in this process yet; the websocket will not be started");
            return;
        }

        // Temporarily disabled
        //await GetQuestTemplates((Class303)tarkovApplication.Session);

        // Character selection can submit in batchmode without advancing the
        // main-menu data load. Fetch the native location table before making
        // this host visible to requesters; otherwise their start notification
        // arrives while ClientBackendSession.LocationSettings is still null.
        Fika.Core.Main.Utils.MainThreadDispatcher.Post(() =>
            _ = CompleteHeadlessInitializationAsync(tarkovApplication));
        _hasVerified = true;
    }

    private async Task CompleteHeadlessInitializationAsync(TarkovApplication tarkovApplication)
    {
        try
        {
            var session = tarkovApplication.Session;
            var backendSession = session?.TryCast<ClientBackendSession>();
            if (backendSession == null)
                throw new InvalidOperationException("Headless backend session is unavailable after profile selection");

            if (session.Profile == null)
            {
                // Profile construction initializes SkillManager from the data
                // installed by GlobalsDataLoader.Apply. Loading only the
                // GlobalConfiguration singleton leaves those dependencies
                // absent in batchmode, where the normal menu load is skipped.
                Log.LogInfo("[Headless] Session: loading global configuration data");
                var globalsLoader = new TarkovApplication.GlobalsDataLoader();
                await globalsLoader.Load(session).AsManaged();
                Log.LogInfo("[Headless] Session: applying global configuration data");
                await globalsLoader.Apply(session, tarkovApplication.ClientBackEnd).AsManaged();
                Log.LogInfo("[Headless] Session: loading profiles");
                await session.GetProfiles().AsManaged();
                Log.LogInfo($"[Headless] Session: profiles loaded (selected={(session.Profile == null ? "no" : "yes")}, count={backendSession.AllProfiles?.Length ?? 0})");
            }

            // LocalGameMatching constructs an OfflineHealthController before
            // the raid starts. Its regeneration path reads BonusController's
            // singleton, normally installed during menu preparation.
            if (!Singleton<BonusController>.Instantiated)
            {
                Log.LogInfo("[Headless] Session: initializing the bonus controller");
                tarkovApplication.InitBonusController();
            }

            // Headless matching only needs the SocialNetwork instance. Its
            // native Init currently throws inside SPT 5's batchmode social
            // path, even with valid contacts and dialogs; the headless host
            // never opens chat UI. Keep the native object for matchmaker.
            if (backendSession.SocialNetwork == null)
            {
                backendSession._SocialNetwork_k__BackingField = new SocialNetwork();
                Log.LogInfo("[Headless] Session: created a social network stub for matching");
            }

            // MatchmakerOperation enables native notifications even in
            // batchmode. Its controller constructor subscribes to this
            // singleton before a raid can start; the skipped menu flow has
            // not created it for a dedicated host.
            if (!Singleton<NotificationManager>.Instantiated)
            {
                Singleton<NotificationManager>.Create(new NotificationManager());
                Log.LogInfo("[Headless] Session: created the notification manager");
            }

            // The batchmode profile path never creates the menu settings
            // resolver, but LocalGameMatching sets its game state before
            // showing the matching screen.
            if (tarkovApplication._settingsScreenResolver == null)
            {
                tarkovApplication._settingsScreenResolver = new SettingsScreenResolver(session);
                Log.LogInfo("[Headless] Session: created the settings screen resolver");
            }

            EnsureHeadlessMapLoadControllers(tarkovApplication);

            if (backendSession.LocationSettings?.locations == null)
            {
                Log.LogInfo("[Headless] Session: loading location settings from the server");
                var settings = await session.GetLevelSettings().AsManaged();
                if (settings?.locations == null)
                    throw new InvalidOperationException("Headless location settings request returned no locations");
                backendSession._LocationSettings_k__BackingField = settings;
            }

            Log.LogInfo($"[Headless] Session: location settings ready ({backendSession.LocationSettings.locations.Count} locations)");
            CreateHeadlessWebsocket();
        }
        catch (Exception exception)
        {
            Log.LogError($"[Headless] Session: initialization FAILED, this host will never be offered: {exception}");
        }
    }

    private void EnsureHeadlessMapLoadControllers(TarkovApplication tarkovApplication)
    {
        // LoadMapAndData unloads both controllers before loading the scene.
        // In batchmode, profile selection and menu setup can overlap the raid
        // request, leaving either native controller absent at the call site.
        if (tarkovApplication.NarrateControllerAccess == null)
        {
            tarkovApplication._NarrateControllerAccess_k__BackingField =
                new TarkovApplication.NarrateController(tarkovApplication);
            Log.LogInfo("Created Headless narrate controller for map loading");
        }
        if (tarkovApplication.HideoutControllerAccess == null)
        {
            tarkovApplication._HideoutControllerAccess_k__BackingField =
                new TarkovApplication.HideoutController(tarkovApplication);
            Log.LogInfo("Created Headless hideout controller for map loading");
        }
    }

    /// <summary>
    /// Creates and connects the <see cref="HeadlessWebSocket"/>
    /// </summary>
    private void CreateHeadlessWebsocket()
    {
        _fikaHeadlessWebSocket = new();
        if (!_invalidPluginsFound)
        {
            Log.LogInfo($"[Headless] Server registration: connecting the headless websocket to {_fikaHeadlessWebSocket.Url}");
            _fikaHeadlessWebSocket.Connect();
        }
        else
        {
            Log.LogError("[Headless] Server registration: not connecting, unsupported plugins were detected");
        }
    }

    /// <summary>
    /// Verifies that no invalid plugins are loaded
    /// </summary>
    private Task VerifyPlugins()
    {
        Log.LogInfo("[Headless] Validation: verifying that no unsupported plugins are loaded");

        List<string> invalidPluginList =
        [
            "com.Amanda.Graphics",
            "com.Amanda.Sense",
            "VIP.TommySoucy.MoreCheckmarks",
            "com.kmyuhkyuk.EFTApi",
            "com.mpstark.DynamicMaps",
            "IhanaMies.LootValue",
            "com.cactuspie.ramcleanerinterval",
            "com.TYR.DeClutter",
            "SPTVRAMCleaner.UniqueGUID",
            "harmonyzt.sptleaderboard",
            "com.acidphantasm.stattrack"
        ];
        PluginInfo[] pluginInfos = [.. IL2CPPChainloader.Instance.Plugins.Values];
        List<string> unsupportedMods = [];

        foreach (var Info in pluginInfos)
        {
            if (invalidPluginList.Contains(Info.Metadata.GUID))
            {
                unsupportedMods.Add($"{Info.Metadata.Name}, GUID: {Info.Metadata.GUID}");
            }
        }

        if (unsupportedMods.Count > 0)
        {
            var modsString = string.Join("; ", unsupportedMods);
            Log.LogFatal($"[Headless] Validation: {unsupportedMods.Count} invalid plugins found, this headless host will not be available for hosting! Remove these mods: {modsString}");
            _invalidPluginsFound = true;
            if (IsRunningWindows && MonoBehaviourSingleton<PreloaderUI>.Instantiated)
            {
                MonoBehaviourSingleton<PreloaderUI>.Instance.ShowCriticalErrorScreen("HEADLESS ERROR",
                    $"{unsupportedMods.Count} invalid plugins found, this headless host will not be available for hosting! Check your log files for more information.",
                    ErrorScreen.EButtonType.OkButton, 0f);
            }
            Thread.Sleep(-1);
            return Task.CompletedTask;
        }

        _invalidPluginsFound = false;

        Log.LogInfo("[Headless] Validation: no unsupported plugins found");

        return Task.CompletedTask;
    }

    private async Task BeginFikaStartRaid(StartHeadlessRequest request, IEftSession session,
        TarkovApplication tarkovApplication, JsonType.LocationSettings.Location selectedLocation)
    {
        var selectedProfile = session.Profile;
        if (selectedProfile == null)
            throw new InvalidOperationException("Headless backend session has no selected profile");
        // The 5.0 batchmode selection path skips TarkovApplication's normal
        // profile-pref initialization. RaidSettings.OnlinePveRaid writes a
        // per-profile preference during construction.
        if (string.IsNullOrEmpty(PrefsUtils._profileId))
            PrefsUtils.Init(selectedProfile.ProfileId);
        Log.LogInfo("Constructing Headless raid settings");
        // The default constructor leaves the location table detached in the
        // batchmode path. Matching reads RaidSettings._locationSettings when
        // it calculates the selected location's group limits, so construct
        // it with the loaded backend table as the game does for normal raids.
        var locationSettings = session.TryCast<ClientBackendSession>()?.LocationSettings
            ?? throw new InvalidOperationException("Headless location settings are unavailable");
        RaidSettings raidSettings = new(request.Side, request.Time, ERaidMode.Local,
            request.TimeAndWeatherSettings, locationSettings)
        {
            Side = request.Side,
            PlayersSpawnPlace = request.SpawnPlace,
            MetabolismDisabled = request.MetabolismDisabled,
            BotSettings = request.BotSettings,
            WavesSettings = request.WavesSettings,
            TimeAndWeatherSettings = request.TimeAndWeatherSettings,
            SelectedDateTime = request.Time,
            SelectedLocation = selectedLocation,
            isInTransition = false,
            RaidMode = ERaidMode.Local,
            IsPveOffline = true,
            OnlinePveRaid = false,
            transitionType = request.UseEvent ? ELocationTransition.Event : ELocationTransition.None
        };

        raidSettings.BotSettings.BotAmount = request.WavesSettings.BotAmount;

        tarkovApplication._raidSettings = raidSettings;

        Log.LogInfo($"[Headless] Raid: settings initialized (location={selectedLocation.Id}, raidMode={raidSettings.RaidMode}, isPveOffline={raidSettings.IsPveOffline})");

        if (FikaPlugin.Instance.Settings.ForceIP.Value != "")
        {
            // We need to handle DNS entries as well
            var ip = FikaPlugin.Instance.Settings.ForceIP.Value;
            try
            {
                var endpoint = NetUtils.ResolveAddress(ip);
            }
            catch
            {
                var message = $"'{ip}' is not a valid IP address to connect to! Check your 'Force IP' setting.";
                if (Singleton<PreloaderUI>.Instantiated)
                    Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen("ERROR FORCING IP",
                    message,
                    ErrorScreen.EButtonType.OkButton, 10f);
                Log.LogError(message);
                if (OnReady()) CanHost = true;
                _fikaHeadlessWebSocket?.Reconnect();
                return;
            }
        }

        if (FikaPlugin.Instance.Settings.ForceBindIP.Value != "Disabled")
        {
            if (!IPAddress.TryParse(FikaPlugin.Instance.Settings.ForceBindIP.Value, out _))
            {
                var message = $"'{FikaPlugin.Instance.Settings.ForceBindIP.Value}' is not a valid IP address to bind to! Check your 'Force Bind IP' setting.";
                if (Singleton<PreloaderUI>.Instantiated)
                    Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen("ERROR BINDING",
                    message,
                    ErrorScreen.EButtonType.OkButton, 10f);
                Log.LogError(message);
                if (OnReady()) CanHost = true;
                _fikaHeadlessWebSocket?.Reconnect();
                return;
            }
        }

        // Only the description of the settings is at risk here; the settings
        // themselves reach the server through CreateMatch below. A log line must
        // never decide whether this host can create the room.
        try
        {
            Log.LogInfo($"[Headless] Raid: starting with {DescribeRaidSettings(raidSettings)}");
        }
        catch (Exception exception)
        {
            // A linkage failure against a shadowing assembly is raised in the
            // *calling* frame, so DescribeRaidSettings cannot observe it itself.
            // This frame can, and continues: the raid does not depend on it.
            Log.LogError($"[Headless] Raid: the raid settings could not be described for the log line " +
                $"({exception.GetType().Name}: {exception.Message}), continuing with the raid");
            HeadlessAssemblies.ReportIfShadowed(Log);
        }

        try
        {
            Log.LogInfo("[Headless] Server registration: creating the match/room on the Fika server");
            await FikaBackendUtils.CreateMatch(selectedProfile.ProfileId,
                selectedProfile.Info.Nickname, raidSettings, request.ReservationToken);
        }
        catch (Exception exception)
        {
            Log.LogError($"[Headless] Server registration FAILED (match creation): {exception}");
            CanHost = true;
            ToggleFramelimit(true);
            _fikaHeadlessWebSocket?.Reconnect();
            return;
        }

        Log.LogInfo("[Headless] Server registration: match created, the server may now route the requester to this host");

        FikaBackendUtils.IsHeadlessGame = true;

        //_verifyConnectionsRoutine = StartCoroutine(VerifyPlayersRoutine(tarkovApplication));

        try
        {
            EnsureHeadlessMapLoadControllers(tarkovApplication);
            var matchingSession = session.TryCast<ClientBackendSession>();
            var mainProfile = matchingSession?.Profile;
            var petProfile = matchingSession?.ProfileOfPet;
            Log.LogInfo($"Headless matchmaker prerequisites: session={matchingSession != null}, " +
                $"profile={mainProfile != null}, profileInfo={mainProfile?.Info != null}, " +
                $"allProfiles={matchingSession?.AllProfiles?.Length ?? -1}, " +
                $"pet={petProfile != null}, petInfo={petProfile?.Info != null}, " +
                $"petIdNull={mainProfile?.PetId is null}, petProfileIdNull={petProfile?.Id is null}, " +
                $"bonus={mainProfile?.BonusController != null}, " +
                $"selectedLocation={raidSettings.SelectedLocation != null}, locationTable={raidSettings._locationSettings != null}, " +
                $"social={matchingSession?.SocialNetwork != null}, itemUi={ItemUiContext.Instance != null}, " +
                $"notificationManager={Singleton<NotificationManager>.Instantiated}, " +
                $"settingsScreenResolver={tarkovApplication._settingsScreenResolver != null}, " +
                $"narrateController={tarkovApplication.NarrateControllerAccess != null}, " +
                $"hideoutController={tarkovApplication.HideoutControllerAccess != null}, " +
                $"matchmakerOperation={tarkovApplication._localMatchmakerOperation != null}");
            tarkovApplication.CurrentRaidNum++;
            tarkovApplication.CurrentTotalRaidNum++;
            Singleton<JobScheduler>.Instance.SetForceMode(true);
            Log.LogInfo($"[Headless] Raid: loading map {raidSettings.SelectedLocation.Name.Localized()}");
            _ = WaitForPlayersToConnect(Volatile.Read(ref _raidGeneration));
            await tarkovApplication.LocalGameMatching(raidSettings.TimeAndWeatherSettings);
            Log.LogInfo("[Headless] Raid: init complete, the host is now in the raid");
            CurrentRaidCount++;
        }
        catch (Exception ex)
        {
            Log.LogError($"[Headless] Raid init FAILED: {ex.Message}");
            Log.LogError(ex);
            Log.LogError("[Headless] Shutdown: quitting the process because the raid could not be initialized");
            if (FikaSingleton<FikaServer>.Instantiated)
            {
                MessagePacket packet = new()
                {
                    Message = LocaleUtils.UI_ERROR_RAID_INIT,
                    NotificationDurationType = ENotificationDurationType.Default,
                    NotificationIconType = ENotificationIconType.Alert,
                    Color = Color.red
                };
                FikaSingleton<FikaServer>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            }

            Application.Quit(1);
            //tarkovApplication.method_39("Local game matching", ex);
        }
        Singleton<JobScheduler>.Instance.SetForceMode(false);
    }

    /// <summary>
    /// Describes the raid settings for the "Raid: starting with" line through
    /// the port's own JSON facility.
    /// <para>
    /// The previous <c>JsonConvert.SerializeObject(raidSettings)</c> bound to
    /// <c>SerializeObject(Il2CppSystem.Object)</c> of the IL2CPP proxy. On an
    /// install that also carries a managed Newtonsoft.Json.dll in
    /// BepInEx/plugins that overload does not exist, and the resulting
    /// MissingMethodException is raised while the calling method is compiled -
    /// before its first statement runs. That is why the raid never started:
    /// <see cref="BeginFikaStartRaid"/>'s own try/catch could never see it.
    /// <see cref="FikaJson"/> routes native values through the game's own
    /// JsonConvert via IL2CPP, which a managed assembly cannot shadow.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string DescribeRaidSettings(RaidSettings raidSettings)
    {
        try
        {
            return FikaJson.Serialize(raidSettings);
        }
        catch (Exception exception)
        {
            // Not fatal: the settings still reach the server through CreateMatch.
            FikaHeadlessLogger.LogError($"[Headless] Raid: serializing the raid settings for the log line failed " +
                $"({exception.GetType().Name}: {exception.Message}); the raid continues without the description");
            HeadlessAssemblies.ReportIfShadowed(FikaHeadlessLogger);
            return "<unavailable, see the log>";
        }
    }

    private async Task WaitForPlayersToConnect(int raidGeneration)
    {
        await Task.Delay(TimeSpan.FromMinutes(2));
        MainThreadDispatcher.Post(() =>
        {
            if (raidGeneration != Volatile.Read(ref _raidGeneration) || CanHost ||
                FikaSingleton<FikaServer>.Instance is not { } server ||
                server.NetServer.ConnectedPeersCount != 0)
                return;

            Log.LogWarning("No connections after 2 minutes, terminating");
            _ = QuitIfStillUnoccupied(raidGeneration, server);
        });
    }

    private async Task QuitIfStillUnoccupied(int raidGeneration, FikaServer server)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        MainThreadDispatcher.Post(() =>
        {
            if (raidGeneration == Volatile.Read(ref _raidGeneration) && !CanHost &&
                ReferenceEquals(FikaSingleton<FikaServer>.Instance, server) &&
                server.NetServer.ConnectedPeersCount == 0)
                Application.Quit();
        });
    }

    internal void WatchMenuReturn(TarkovApplication tarkovApplication, Il2CppSystem.Threading.Tasks.Task menuTask)
    {
        var raidGeneration = Volatile.Read(ref _raidGeneration);
        var isTransit = FikaBackendUtils.IsTransit;
        _ = MainThreadDispatcher.RunAsync(async () =>
        {
            try
            {
                await menuTask.AsManaged();

                // Native menu return and Fika's game/network disposal must
                // both finish before this process can accept another raid.
                for (var attempt = 0; attempt < 300; attempt++)
                {
                    if (raidGeneration != Volatile.Read(ref _raidGeneration)) return;
                    var backendSession = tarkovApplication.Session?.TryCast<ClientBackendSession>();
                    var oldRaidReleased = !Singleton<AbstractGame>.Instantiated &&
                        !FikaSingleton<IFikaGame>.Instantiated;
                    // A transit keeps the Fika server and its UDP session
                    // alive for the next map. A normal raid must release it
                    // before the headless can accept a fresh reservation.
                    var networkReady = isTransit
                        ? FikaSingleton<FikaServer>.Instantiated
                        : !FikaSingleton<FikaServer>.Instantiated;
                    if (oldRaidReleased &&
                        networkReady &&
                        backendSession?.Profile != null &&
                        backendSession.LocationSettings?.locations != null)
                    {
                        if (isTransit)
                        {
                            var cachedSettings = FikaBackendUtils.CachedRaidSettings;
                            if (cachedSettings == null)
                            {
                                throw new InvalidOperationException("Transit returned without cached raid settings");
                            }

                            // LocalGameMatching's transition overload reads
                            // TarkovApplication.TransitionStatus and rebuilds
                            // the destination raid settings. Keep the existing
                            // Fika server and start the next map in-place.
                            ToggleFramelimit(false);
                            Log.LogInfo("[Headless] Shutdown: transit cleanup complete, starting the next map");
                            await tarkovApplication.LocalGameMatching(
                                cachedSettings.TimeAndWeatherSettings, true).AsManaged();
                            CurrentRaidCount++;
                            Log.LogInfo("[Headless] Shutdown: transit map initialization complete");
                            return;
                        }

                        if (OnReady())
                        {
                            CanHost = true;
                            var readyToken = _raidSocketGeneration == _fikaHeadlessWebSocket?.ConnectionGeneration
                                ? _raidReservationToken : null;
                            await _fikaHeadlessWebSocket.SendReadyAsync(readyToken);
                            _raidReservationToken = null;
                            Log.LogInfo("[Headless] Shutdown: menu returned, reporting readiness for another raid");
                        }
                        return;
                    }
                    await Task.Delay(100);
                }

                Log.LogError("[Headless] Shutdown: menu returned, but old raid cleanup or profile/location readiness did not complete");
            }
            catch (Exception exception)
            {
                Log.LogError($"[Headless] Shutdown: menu return failed: {exception}");
            }
        });
    }

    public bool OnReady()
    {
        ToggleFramelimit(true);

        if (PlayerDisposeError)
        {
            Log.LogError("There were some errors in the last raid while disposing of players. This is most likely caused by any content mods that adds new equipment. The headless will now shut down to prevent memory leaks.");
            Application.Quit();
            return false;
        }

        if (CurrentRaidCount == 0)
        {
            return true;
        }

        Log.LogInfo($"Headless has done {CurrentRaidCount} raids, and is set to restart after {_restartAfterAmountOfRaids}");
        if (_restartAfterAmountOfRaids != 0 && CurrentRaidCount >= _restartAfterAmountOfRaids && !FikaBackendUtils.IsTransit)
        {
            Application.Quit();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Marks the headless backend profile as ready for hosting.  SPT 5 can
    /// enter the character-selection flow without invoking
    /// <see cref="ConsoleScreen.OnProfileReceive"/> on the generated IL2CPP
    /// wrapper, so callers must be able to complete this handshake from the
    /// selection data as well.  The interlocked guard keeps both entry points
    /// from starting plugin validation twice when the callback is delivered
    /// normally.
    /// </summary>
    internal void MarkProfileVerified(string source, string nickname = null)
    {
        if (Interlocked.Exchange(ref _profileVerificationStarted, 1) != 0)
            return;

        // The automatic signal: only the server-generated headless_ profile
        // may arm the payload. Both observers already check the nickname, so
        // this is a defensive second check - never arm on a guess.
        if (string.IsNullOrEmpty(nickname) || !nickname.Contains("headless_", StringComparison.OrdinalIgnoreCase))
        {
            NoteNormalPlay(source, nickname);
            return;
        }

        if (!IsArmed && !IsInert)
        {
            ArmHeadlessHost($"Auto -> dedicated host (profile {nickname})", duringLoad: false);
        }

        var selected = Fika.Core.Main.Utils.FikaBackendUtils.Profile;
        FikaHeadlessLogger.LogInfo($"[Headless] Profile: verified (source: {source}, " +
            $"profileId={selected?.ProfileId ?? "<not selected yet>"}, nickname={selected?.Nickname ?? "<none>"})");
        if (OnReady())
        {
            CanHost = true;
            FikaHeadlessLogger.LogInfo("[Headless] Profile: this host is available for hosting (CanHost=true)");
        }
        _ = RunPluginValidation();
    }

    public void ToggleFramelimit(bool enabled)
    {        
        if (enabled)
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 1;
            if (Singleton<JobScheduler>.Instantiated)
            {
                Singleton<JobScheduler>.Instance.SetTargetFrameRate(1);
            }
        }
        else
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = UpdateRate.Value;
            if (Singleton<JobScheduler>.Instantiated)
            {
                Singleton<JobScheduler>.Instance.SetTargetFrameRate(UpdateRate.Value);
            }
        }
        Log.LogInfo($"Setting frame limiter to {(enabled ? "enabled" : "disabled")}, current target is {Application.targetFrameRate}Hz");
    }

    private void GetHeadlessRestartAfterRaidAmount()
    {
        var headlessConfig = FikaRequestHandler.GetHeadlessRestartAfterRaidAmount();
        _restartAfterAmountOfRaids = headlessConfig.Amount;
    }
}



