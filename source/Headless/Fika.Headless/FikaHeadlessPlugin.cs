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
    // 1.5.3：插件受模式门控。它只为已确认的主机武装专用主机载荷；
    // 在正常游玩时则完全不安装任何东西。
    public const string HeadlessVersion = "1.5.5";

    public static FikaHeadlessPlugin Instance { get; private set; }
    public static ManualLogSource FikaHeadlessLogger;
    public static bool PlayerDisposeError;

    /// <summary>
    /// 专用主机载荷（注入的原生类型、常驻
    /// ticker、约 100 个补丁、无头战局工厂）安装完成后为 1。
    /// 所有具有破坏性的操作都先检查它：当它为 0 时，本进程是一个
    /// 正常游玩的安装，必须保持不被触碰。
    /// </summary>
    private static int _armed;
    /// <summary>一旦判定为"正常游玩"即为 1：此后本进程永远无法再武装该载荷。</summary>
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
    /// <summary>已武装的补丁，以便强制模式可以被回退。</summary>
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
    /// BepInEx 在游戏知道将使用哪个 profile 之前就加载插件，
    /// 因此这里不能做任何可能损坏图形化安装的事情。
    /// <para>
    /// 阶段 1（本方法）：绑定配置、日志，并安装两个 profile
    /// 观察器。阶段 2（<see cref="ArmHeadlessHost"/>）只为已确认的专用主机
    /// 安装载荷——要么因为进程自身就这样声明
    /// （配置 <c>Mode=Headless</c>、<c>-headless</c>，或没有显示环境的
    /// <c>-batchmode -nographics</c> 组合），要么因为所选的
    /// profile 是服务器生成的 <c>headless_</c> 那个。
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

        // 在此处之前不得安装任何东西：不注入原生类型、
        // 不启动 ticker、不复原其他插件的补丁、不发 HTTP 请求、不做日志
        // 清理。这些全都属于专用主机载荷。
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
    /// 从配置文件读取 <c>Mode</c>。未知值会回退到
    /// <see cref="HeadlessRunMode.Auto"/>，那是唯一安全的选择。
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
    /// 模式未知之前唯一存在的一批补丁。它们观察所选的
    /// profile 并写日志；在正常游玩时它们会立即返回。
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
    /// 安装专用主机载荷。只有在进程是已确认的主机时才会走到：
    /// 加载时依据配置或命令行，否则
    /// 在所选 profile 被发现是服务器生成的
    /// <c>headless_</c> 那个之后，由 profile 观察器调用。
    /// </summary>
    /// <param name="reason">判定文本，会逐字记录在 "[Headless] Mode: " 之后。</param>
    /// <param name="duringLoad">
    /// 当载荷在 BepInEx 仍在加载插件时就被武装则为 true
    /// （此时设置接缝尚未运行）。对 profile 关卡则为 false，因为那时
    /// 对 <c>ClientApplicationInitOperation.LoadUserSettings</c> 来说已经太晚。
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

        // 先注入 IL2CPP 类型，与原始加载顺序完全一致。
        try
        {
            HeadlessNativeTypes.RegisterAll(assembly, Log);
        }
        catch (Exception exception)
        {
            Log.LogError($"[Headless] Mode: registering the native types FAILED, this host cannot create raids: {exception}");
        }

        // 常驻 ticker：内存/GC 维护，仅主机。
        try
        {
            AddComponent<HeadlessTicker>();
        }
        catch (Exception exception)
        {
            Log.LogError($"[Headless] Mode: adding the headless ticker FAILED: {exception}");
        }

        // 主机不需要的客户端插件补丁。在正常游玩中绝不执行：
        // Fika.Core 需要它们来进行合作游玩。
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

        // 从这里开始 Fika.Core 会走它的无头代码路径。
        FikaBackendUtils.IsHeadless = true;
        Log.LogInfo("[Headless] Plugin start: marked this process as a dedicated host (FikaBackendUtils.IsHeadless=true)");

        // 延后安装的补丁：按类手动安装，这样一个未武装的
        // 插件一个都不会装上。
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

        // 仅主机的维护。这些在游玩用的安装里都不得运行。
        try
        {
            GetHeadlessRestartAfterRaidAmount();
        }
        catch (Exception exception)
        {
            Log.LogWarning($"[Headless] Mode: could not read the restart-after-raids setting, keeping {_restartAfterAmountOfRaids}: {exception.Message}");
        }

        CleanupLogFiles();

        // 由 profile 武装的主机错过了 ClientApplicationInitOperation.LoadUserSettings，
        // 而那是设置补丁所替换的接缝。既然模式已确定，
        // 现在就套用同样的无头设置。
        if (!duringLoad)
        {
            FikaHeadlessSettingsManager.ApplyDeferred();
        }

        // 专用主机的启动诊断。两者都加了保护：一个无法描述自己的
        // 程序集或窗口的主机，仍然必须能够
        // 承担主机职责，因此两者都不得从武装路径中抛出异常。
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
    /// 移除所有无头补丁并清除武装标志。仅在用户强制了模式、但所选
    /// profile 结果是普通游玩 profile 时
    /// 才使用。
    /// </summary>
    private void DisarmHeadlessPatches()
    {
        foreach (var type in _installedPatchTypes)
        {
            // SPT 加载器会以类型名注册每个 ModulePatch，
            // 客户端插件正是靠它来取消自己补丁的。
            // 也尝试完整名称，以防这种命名方式将来发生变化。
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
    /// 当 <paramref name="type"/> 仍在其目标上持有 Harmony prefix 或 postfix
    /// 时为 true，也就是强制模式未能干净地回退。
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
            // 上报尽力而为；绝不因此让正在运行的游戏失败。
            return false;
        }
    }

    /// <summary>
    /// 所选的 profile 是普通游玩 profile，因此本次安装不是
    /// 专用主机。只记录一次；插件在此后整个进程期间保持惰性，
    /// 游戏永远不会被退出或触碰。只由 profile
    /// 观察器调用。
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
            // Batchmode/-nographics 进程可能没有控制台句柄。
            // 那种环境下 Console.Title 会抛 IOException；修改可选标题
            // 绝不能妨碍无头插件加载
            // 它的补丁与 websocket。
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
        // ticker 只存在于已武装的主机上，但保留这个保护：下面的
        // 内存管理会卸载资源，绝不能在游玩用的
        // 安装里运行。
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
    /// 禁用无头模式不需要的那些 Fika.Core 补丁
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
    /// 清理所有旧日志文件
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
                                         .Skip(2); // 保留最新的 3 个文件夹，额外计入 1 个，因此剩下 3 个

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
    /// 初始化 <see cref="ConfigFile"/> 设置
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

        UpdateRate = Config.Bind("Headless", "Update Rate", 30,
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
    /// 收到启动战局请求时
    /// </summary>
    /// <param name="request"></param>
    /// <exception cref="NullReferenceException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public void OnFikaStartRaid(StartHeadlessRequest request)
    {
        try
        {
            // StartHeadlessRequest 是值类型：绝不要对它使用 null 条件运算符。
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

            // TarkovApplication.Session 是一个 IEftSession IL2CPP 代理。
            // 具体的 ClientBackendSession 才持有已加载的地点表；
            // 通过接口代理访问它可能返回 null。
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
                        // 预检异常发生在 BeginFikaStartRaid 的
                        // 建房 catch 之前。通过替换它的 socket 来放弃
                        // 那次预留；服务器会在再次向本主机发起邀约之前
                        // 结束任何残缺的房间。
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
    /// 校验该无头客户端是否有资格承担主机
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
            // 绝不静默等待：当无头实例迟迟无法被邀约时，卡住的
            // 语言/配置加载是第一个要排除的原因。
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

        // 暂时禁用
        //await GetQuestTemplates((Class303)tarkovApplication.Session);

        // 角色选择在 batchmode 下可能已经提交，却没有推进
        // 主菜单的数据加载。在让本主机对请求方可见之前，先取回原生地点表；
        // 否则他们的启动通知会在
        // ClientBackendSession.LocationSettings 仍为 null 时到达。
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
                // profile 构建会用 GlobalsDataLoader.Apply 装进来的数据
                // 初始化 SkillManager。只加载
                // GlobalConfiguration 单例，会让这些依赖在 batchmode 下
                // 缺失，因为那里会跳过正常的主菜单加载。
                Log.LogInfo("[Headless] Session: loading global configuration data");
                var globalsLoader = new TarkovApplication.GlobalsDataLoader();
                await globalsLoader.Load(session).AsManaged();
                Log.LogInfo("[Headless] Session: applying global configuration data");
                await globalsLoader.Apply(session, tarkovApplication.ClientBackEnd).AsManaged();
                Log.LogInfo("[Headless] Session: loading profiles");
                await session.GetProfiles().AsManaged();
                Log.LogInfo($"[Headless] Session: profiles loaded (selected={(session.Profile == null ? "no" : "yes")}, count={backendSession.AllProfiles?.Length ?? 0})");
            }

            // LocalGameMatching 会在战局开始前构造一个 OfflineHealthController。
            // 它的再生路径会读 BonusController 的
            // 单例，而那个单例通常在菜单准备阶段安装。
            if (!Singleton<BonusController>.Instantiated)
            {
                Log.LogInfo("[Headless] Session: initializing the bonus controller");
                tarkovApplication.InitBonusController();
            }

            // 无头匹配只需要 SocialNetwork 实例。它的
            // 原生 Init 目前在 SPT 5 的 batchmode 社交路径里会抛异常，
            // 即使联系人和对话框都有效；无头主机
            // 从不打开聊天 UI。为匹配器保留这个原生对象。
            if (backendSession.SocialNetwork == null)
            {
                backendSession._SocialNetwork_k__BackingField = new SocialNetwork();
                Log.LogInfo("[Headless] Session: created a social network stub for matching");
            }

            // MatchmakerOperation 即使在 batchmode 下也会启用原生通知。
            // 它的控制器构造函数会在战局开始前订阅这个
            // 单例；而被跳过的主菜单流程并没有为专用主机
            // 创建它。
            if (!Singleton<NotificationManager>.Instantiated)
            {
                Singleton<NotificationManager>.Create(new NotificationManager());
                Log.LogInfo("[Headless] Session: created the notification manager");
            }

            // batchmode 的 profile 路径从不创建菜单设置
            // 解析器，但 LocalGameMatching 会在显示匹配界面之前
            // 设置它的游戏状态。
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
        // LoadMapAndData 会在加载场景前卸载这两个控制器。
        // 在 batchmode 下，profile 选择与菜单设置可能与战局
        // 请求重叠，导致调用处任一原生控制器缺失。
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
    /// 创建并连接 <see cref="HeadlessWebSocket"/>
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
    /// 校验没有加载无效插件
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
        // 5.0 的 batchmode 选择路径会跳过 TarkovApplication 正常的
        // profile-pref 初始化。RaidSettings.OnlinePveRaid 会在构造期间
        // 写入一个按 profile 区分的偏好。
        if (string.IsNullOrEmpty(PrefsUtils._profileId))
            PrefsUtils.Init(selectedProfile.ProfileId);
        Log.LogInfo("Constructing Headless raid settings");
        // 默认构造函数会让地点表在 batchmode 路径下处于未挂接状态。
        // 匹配在计算所选地点的队伍上限时会读
        // RaidSettings._locationSettings，因此要像游戏正常战局那样
        // 用已加载的后端表来构造它。
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
            // 我们还需要一并处理 DNS 条目
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

        // 这里只有设置的描述文字会受影响；设置本身会
        // 通过下面的 CreateMatch 到达服务器。一行日志绝不能
        // 决定这台主机能否创建房间。
        try
        {
            Log.LogInfo($"[Headless] Raid: starting with {DescribeRaidSettings(raidSettings)}");
        }
        catch (Exception exception)
        {
            // 针对遮蔽程序集的链接失败会在*调用*帧中抛出，
            // 因此 DescribeRaidSettings 自身观察不到它。
            // 本帧可以捕获，并继续执行：战局并不依赖它。
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
    /// 通过本移植版自己的 JSON 设施，为 "Raid: starting with" 这一行
    /// 描述战局设置。
    /// <para>
    /// 之前的 <c>JsonConvert.SerializeObject(raidSettings)</c> 绑定到了
    /// IL2CPP 代理的 <c>SerializeObject(Il2CppSystem.Object)</c>。在一个
    /// BepInEx/plugins 里还带有托管 Newtonsoft.Json.dll 的
    /// 安装上，那个重载并不存在，随之产生的
    /// MissingMethodException 会在调用方法被编译时抛出——
    /// 也就是在其第一条语句执行之前。这就是战局一直无法启动的原因：
    /// <see cref="BeginFikaStartRaid"/> 自己的 try/catch 永远看不到它。
    /// <see cref="FikaJson"/> 会通过游戏自己的
    /// JsonConvert（经 IL2CPP）转发原生值，而托管程序集无法遮蔽它。
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
            // 不致命：设置仍会通过 CreateMatch 到达服务器。
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

                // 原生菜单返回与 Fika 的游戏/网络释放都必须
                // 完成，本进程才能接受下一场战局。
                for (var attempt = 0; attempt < 300; attempt++)
                {
                    if (raidGeneration != Volatile.Read(ref _raidGeneration)) return;
                    var backendSession = tarkovApplication.Session?.TryCast<ClientBackendSession>();
                    var oldRaidReleased = !Singleton<AbstractGame>.Instantiated &&
                        !FikaSingleton<IFikaGame>.Instantiated;
                    // 过境传送会让 Fika 服务器及其 UDP 会话
                    // 存活到下一张地图。普通战局必须在无头实例能接受
                    // 新的预留之前释放它。
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

                            // LocalGameMatching 的 transition 重载会读
                            // TarkovApplication.TransitionStatus，并重建
                            // 目标战局设置。保留现有的
                            // Fika 服务器，并就地启动下一张地图。
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
    /// 把无头后端 profile 标记为可承担主机。SPT 5 可能
    /// 进入角色选择流程却不调用
    /// 生成 IL2CPP 包装器上的 <see cref="ConsoleScreen.OnProfileReceive"/>，
    /// 因此调用方也必须能够仅凭
    /// 选择数据完成这次握手。该互锁保护使两个入口
    /// 在回调被正常送达（即通常情形）时都不会重复启动
    /// 插件校验。
    /// </summary>
    internal void MarkProfileVerified(string source, string nickname = null)
    {
        if (Interlocked.Exchange(ref _profileVerificationStarted, 1) != 0)
            return;

        // 自动信号：只有服务器生成的 headless_ profile
        // 才能武装该载荷。两个观察器已经检查过昵称，因此
        // 这是一次防御性的二次检查——绝不凭猜测武装。
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



