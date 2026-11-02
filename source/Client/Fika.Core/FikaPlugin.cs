using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Diz.Utils;
using EFT;
using EFT.UI;
using Fika.Core.Bundles;
using Fika.Core.ConsoleCommands;
using Fika.Core.Main.Custom;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Websocket;
using HarmonyLib;
#if GOLDMASTER
using Fika.Core.UI; 
#endif
using Fika.Core.UI.Patches;
using SPTushonka.Common.Http;
using SPTushonka.Custom.Patches;
#if RELEASE || GOLDMASTER
using SPTushonka.Custom.Utils;
#endif
using SPTushonka.Reflection.Patching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;

namespace Fika.Core;

/// <summary>
/// Fika.Core 主插件。<br/> <br/>
/// 原作者：Paulov <br/>
/// 重写者：<see langword="Lacyway and the Fika team"/>
/// </summary>
[BepInPlugin("com.fika.core", "Fika.Core", FikaVersion)]
[BepInProcess("EscapeFromTarkov.exe")]
[BepInDependency("com.sptushonka.custom", BepInDependency.DependencyFlags.HardDependency)] // 用于保证我们在 spt-custom 之后加载，这样就可以禁用它的补丁
[BepInDependency("com.sptushonka.singleplayer", BepInDependency.DependencyFlags.HardDependency)] // 用于保证我们在 spt-singleplayer 之后加载，这样就可以禁用它的补丁
[BepInDependency("com.sptushonka.core", BepInDependency.DependencyFlags.HardDependency)] // 用于保证我们在 spt-core 之后加载，这样就可以禁用它的补丁
[BepInDependency("com.sptushonka.debugging", BepInDependency.DependencyFlags.HardDependency)] // 用于保证我们在 spt-debugging 之后加载，这样就可以禁用它的补丁
public sealed class FikaPlugin : BasePlugin
{
    public const string FikaVersion = "3.0.4";
    public const string FikaNATPunchMasterServer = "natpunch.project-fika.com";
    public const ushort FikaNATPunchMasterPort = 6790;

    public static FikaPlugin Instance { get; private set; }
    public static string EFTVersionMajor { get; internal set; }
    public ManualLogSource FikaLogger
    {
        get
        {
            return Log;
        }
    }
    public bool LocalesLoaded { get; internal set; }
    public BotDifficulties BotDifficulties { get; internal set; }
    public FikaModHandler ModHandler = new();
    public string[] LocalIPs { get; internal set; }
    public IPAddress WanIP { get; internal set; }
    public FikaConfig Settings { get; internal set; }
    public GameUI GameUi => MonoBehaviourSingleton<GameUI>.Instance;

    internal static uint Crc32 { get; set; }
    internal InternalBundleLoader BundleLoaderPlugin { get; private set; }
    internal FikaNotificationManager NotificationManager { get; set; }

#if RELEASE || GOLDMASTER
    private static readonly System.Version _requiredServerVersion = new("2.4.0");
#endif
    private PatchManager _patchManager;
    private Il2CppSystem.Threading.Tasks.Task _localesTask;
    public string InitializationError { get; private set; }

    public static HeadlessRequesterWebSocket HeadlessRequesterWebSocket { get; set; }

    public static Dictionary<string, string> RespectedPlayersList = new()
    {
        { "samswat",      "godfather of modern SPT modding ~ SSH"                                                       },
        { "katto",        "kmc leader & founder. OG revolutionary of custom assets ~ SSH"                               },
        { "polivilas",    "who started it all -- #emutarkov2019 ~ Senko-san"                                            },
        { "balist0n",     "author of the first singleplayer-focussed mechanics and good friend ~ Senko-san"             },
        { "ghostfenixx",  "keeps asking me to fix bugs ~ TheSparta"                                                     },
        { "thurman",      "aka TwistedGA, helped a lot of new modders, including me when I first started ~ TheSparta"   },
        { "chomp",        "literally unstoppable, carrying SPT development every single day ~ TheSparta"                },
        { "nimbul",       "Sat with Lacy many night and is loved by both Lacy & me. We miss you <3 ~ SSH"               },
        { "vox",          "My favourite american. ~ Lacyway"                                                            },
        { "rairai",       "Very nice and caring person, someone I've appreciated getting to know. ~ Lacyway"            },
        { "cwx",          "Active and dedicated tester who has contributed a lot of good ideas to Fika. ~ Lacyway"      },
        { "shynd",        "Active contributor and resident helper of Fika ~ Archangel"                                  },
        { "janky",        "It's so, forsooth, alas ~ Lacyway"                                                           }
    };

    public static Dictionary<string, string> DevelopersList = new()
    {
        { "lacyway",      "no one unified the community as much as you ~ Senko-san"                  },
        { "ssh_",         "my little favorite gremlin. ~ Lacyway"                                    },
        { "nexus4880",    "the one who taught me everything I know now. ~ SSH"                       },
        { "thesparta",    "I keep asking him to fix these darn bugs ~ GhostFenixx"                   },
        { "senko-san",    "creator of SPT, extremely talented dev, a blast to work with ~ TheSparta" },
        { "leaves",       "Super talented person who comes up with the coolest ideas ~ Lacyway"      },
        { "archangel",    "The 'tbh' guy :pepeChad: ~ Lacyway"                                       },
        { "trippy",       "One of the chads that made the headless client a reality ~ Archangel"     }
    };

    #region natpunch config
    public bool NatPunchServerEnable;
    public string NatPunchServerIP;
    public ushort NatPunchServerPort;
    public int NatPunchServerNatIntroduceAmount;
    #endregion

    public override void Load()
    {
        Instance = this;
        _patchManager = new(this, true);
        Settings = new FikaConfig(Config);

        // Unity 生命周期组件掌管主线程队列。它必须在下方任何
        // 东西可能失败之前就存在：没有它,每一项派发的操作
        // (库存、生成队列、大厅动作)都会被入队而永不出队,
        // 看起来就完全和游戏卡死一样。
        AddComponent<FikaPluginBehaviour>();

        
        

        FikaNativeTypes.RegisterAll(Log);
        BundleLoaderPlugin = new();

        GetNatPunchServerConfig();
        EnableModulePatches();
        DisableSPTPatches();

        

        GetClientConfig();

        var fikaVersion = Assembly.GetAssembly(typeof(FikaPlugin))
            .GetName()
            .Version.ToString();

        Log.LogInfo($"Fika is loaded! Running version: {fikaVersion}");

        WildSpawnTypeExtension.Init();

        try
        {
            BotDifficulties = FikaRequestHandler.GetBotDifficulties();
        }
        catch (Exception exception)
        {
            // 缺少该值时,机器人会回退到游戏自身的难度设置,
            // 因此服务器或序列化问题不应导致插件中止。
            Log.LogError($"Failed to load bot difficulties, bots will use the game defaults: {exception}");
        }

        ConsoleScreen.Processor.RegisterCommand("debug", (Il2CppSystem.Action<bool>)FikaCommands.Debug, "Toggle debug window");
        ConsoleScreen.Processor.RegisterCommand("openAdminUI", (Il2CppSystem.Action)FikaCommands.OpenAdminUI, "Opens the Admin UI as the raid host");
        ConsoleScreen.Processor.RegisterCommand("clear", (System.Action)FikaCommands.Clear, "Clears the console output");

        if (Settings.AllowItemSending)
        {
            _patchManager.EnablePatch(new ItemContext_Patch());
        }

        if (Settings.FastLoad)
        {
            /*_patchManager.EnablePatch(new LoadAmmo_Task_Transpiler());
            _patchManager.EnablePatch(new ItemViewLoadAmmoComponent_Show_Patch());*/
        }

    }

    /// <summary>
    /// 在启动时执行这些检查,以期确保所有 mod 均已加载
    /// </summary>
    internal void StartDeferredChecks()
    {
        MainThreadDispatcher.RunAsync(RunChecks).Forget();
    }

    private void EnableModulePatches()
    {
        _patchManager.EnablePatches();
    }

#if RELEASE || GOLDMASTER
    private void VerifyServerVersion()
    {
        var version = FikaRequestHandler.CheckServerVersion().Version;
        var failed = true;
        if (System.Version.TryParse(version, out var serverVersion))
        {
            if (serverVersion >= _requiredServerVersion)
            {
                failed = false;
            }
        }

        if (failed)
        {
            FikaLogger.LogError($"Server version check failed. Expected: >{_requiredServerVersion}, received: {serverVersion}");
            MainThreadDispatcher.Post(ShowServerCheckFailMessage);
        }
        else
        {
            FikaLogger.LogInfo($"Server version check passed. Expected: >{_requiredServerVersion}, received: {serverVersion}");
        }
    } 

    private void ShowServerCheckFailMessage()
    {
        Fika.Core.Main.Utils.MessageBoxHelper.Show($"Failed to verify server mod version.\nMake sure that the server mod is installed and up-to-date!\nRequired Server Version: {_requiredServerVersion}",
                "FIKA ERROR", Fika.Core.Main.Utils.MessageBoxHelper.MessageBoxType.OK);
        Application.Quit();
    }
#endif

    /// <summary>
    /// 通过等待 5 秒来确保所有 mod 均已加载的任务
    /// </summary>
    /// <remarks>
    /// 该等待很可能多余,因为它运行在 <see cref="Start"/> 内部,但仍作为最后的安全检查保留
    /// </remarks>
    private async Task RunChecks()
    {
        
        // 首次异常探针：当菜单故障触发时,它会记录引擎正在处理的对象
        // (见 FirstChanceFaultProbe)。它只做观测;
        
        
        ReportStartupSettings();
        await Task.Delay(5000);
#if !DEBUG
        try
        {
            VerifyServerVersion();
        }
        catch (Exception exception)
        {
            // 服务器检查已自行向用户报告其失败;
        }
#endif
        await ModHandler.VerifyMods(_patchManager);

        if (Crc32 == 0)
        {
            Log.LogError($"RunChecks: {LocaleUtils.UI_MOD_VERIFY_FAIL.Localized()}");
            
        }

        _patchManager = null;

        try
        {
            WanIP = await FikaRequestHandler.GetPublicIP();
        }
        catch (Exception ex)
        {
            Log.LogError($"RunChecks: {ex.Message}");
            
        }

        if (WanIP == null)
        {
            // 这属于降级而非失败：WAN 地址只会被 raid
            // 主机(FikaServer.Init)读取以公布自身。其他所有路径——包括
            // 整个客户端加入路径——都不依赖它,因此这里绝不能中止任何东西。
            Log.LogWarning("RunChecks: no public IP address is known. Hosting a raid for players outside "
                + "your network will not work (set 'Force IP' in the network settings to announce one); "
                + "joining a raid is unaffected.");
            
        }
        else
        {
            
        }

        
    }

    /// <summary>
    /// 把决定下一次加入尝试行为的设置写入稳定的
    /// 诊断文件,以便将捕获到的失败与其产生的配置对照阅读。
    ///
    /// RunChecks 在第 1 帧运行,此时尚在语言包就绪之前,因此也早于
    /// <see cref="FikaConfig.SetupConfig"/> 绑定各个条目:那一刻每个条目属性仍是
    /// null,而读取其中之一会让这份诊断报告在其首次使用时就报告自身的
    /// NullReferenceException(2026-10-09 22:57:25)。此处
    /// 对条目做了防御性读取,并在配置绑定后再次重复报告,因此
    /// 文件最终仍会包含本次运行实际使用的设置。
    /// </summary>
    private void ReportStartupSettings()
    {
        try
        {
            var settings = Settings;
            if (settings is null)
            {
                
                return;
            }

            // 'SetupConfig' 会一次性绑定所有这些条目,因此取其中任意一个都是有效的探针。
            var bound = settings.EnableOnlinePlayers is not null;
            if (bound && _boundSettingsReported)
            {
                return;
            }

            _boundSettingsReported = bound;
            

            

            
        }
        catch (Exception exception)
        {
            
        }
    }

    /// <summary>读取一个可能尚未绑定的布尔条目(见 <see cref="ReportStartupSettings"/>)。</summary>
    private static string ReadFlag(BepInEx.Configuration.ConfigEntry<bool> entry)
    {
        return entry is null ? "unbound" : entry.Value.ToString();
    }

    /// <summary>读取一个可能尚未绑定的整数条目(见 <see cref="ReportStartupSettings"/>)。</summary>

    /// <summary>当配置报告已使用真正绑定的条目写出后为 true。</summary>
    private bool _boundSettingsReported;

    private void GetClientConfig()
    {
        Settings.GetClientConfig();
    }

    private void GetNatPunchServerConfig()
    {
        var natPunchServerConfig = FikaRequestHandler.GetNatPunchServerConfig();

        NatPunchServerEnable = natPunchServerConfig.Enable;

        // SPT 后端主机可能是带方括号的 IPv6 字面量,形如
        // https://[::1]:6969;若按 ':' 拆分,会残留一个多余的 '['。
        var backendHost = RequestHandler.Host;
        NatPunchServerIP = Uri.TryCreate(backendHost, UriKind.Absolute, out var backendUri)
            ? backendUri.Host
            : backendHost.Replace("https://", string.Empty).Replace("http://", string.Empty).Trim('[', ']');

        NatPunchServerPort = (ushort)natPunchServerConfig.Port;

        natPunchServerConfig.LogValues();
    }

    /// <summary>
    /// 这是语言包能被正确加载所必需的——不知为何,它们在填充之后仍会有几秒钟不可用
    /// </summary>
    /// <param name="localesTask">负责填充语言包的 <see cref="Task"/></param>
    public void WaitForLocales(Il2CppSystem.Threading.Tasks.Task localesTask)
    {
        ArgumentNullException.ThrowIfNull(localesTask);
        if (System.Threading.Interlocked.CompareExchange(ref _localesTask, localesTask, null) != null) return;
        Log.LogInfo("Waiting for locales to be ready...");
    }

    // Unity 的 Update 掌管所有原生任务、本地化以及配置/UI 访问。
    // CLR 异步续体并不保证会在 Unity 主线程上恢复执行。
    internal void UpdateInitialization()
    {
        if (LocalesLoaded || InitializationError != null) return;
        var task = System.Threading.Volatile.Read(ref _localesTask);
        if (task == null) return;
        try
        {
            if (!task.IsCompleted) return;
            task.GetAwaiter().GetResult(); // 在绑定设置之前观察取消/失败。
            if (!FikaBackendUtils.IsHeadless &&
                LocaleUtils.BEPINEX_H_ADVANCED.Localized() == "F_BepInEx_H_Advanced") return;
            Settings.SetupConfig();
            // 现在条目已存在,记录本次运行真正使用的设置(第 1 帧的
            // 报告只能说"尚未绑定")。
            ReportStartupSettings();
            LocalesLoaded = true;
            FikaVersionLabel_Patch.UpdateVersionLabel();
            Log.LogInfo($"Locales and configuration are ready ({Config.Count} entries).");
        }
        catch (Exception exception)
        {
            LocalesLoaded = false;
            InitializationError = exception.ToString();
            Log.LogError($"Fika initialization failed; cooperative play is unavailable.\n{exception}");
            var preloader = MonoBehaviourSingleton<PreloaderUI>.Instance;
            if (preloader != null)
                preloader.ShowErrorScreen("FIKA INITIALIZATION ERROR", "Fika could not initialize its configuration. See BepInEx/LogOutput.log for details.");
        }
    }

    internal string[] GetLocalAddresses()
    {
        List<string> ips = [];
        ips.Add("Disabled");
        ips.Add("0.0.0.0");
        ips.Add("::");

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (!ua.IsDnsEligible)
                    {
                        continue;
                    }

                    var addr = ua.Address;

                    if (IPAddress.IsLoopback(addr))
                    {
                        continue;
                    }

                    if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        if (addr.IsIPv6LinkLocal || addr.IsIPv6SiteLocal)
                        {
                            continue;
                        }

                        if (ua.AddressPreferredLifetime == 0)
                        {
                            continue;
                        }
                    }

                    if (addr.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    {
                        ips.Add(addr.ToString());
                    }
                }
            }

            LocalIPs = [.. ips.Skip(1)];
            var allIps = string.Join(", ", LocalIPs);
            Log.LogInfo($"Cached local IPs: {allIps}");
            return [.. ips];
        }
        catch (Exception ex)
        {
            FikaGlobals.LogError("GetLocalAddresses: " + ex.Message);
            return [.. ips];
        }
    }

    private void DisableSPTPatches()
    {
        // ModulePatch 不再提供 Disable()。它的 Harmony 归属者是 patch
        // 类型名(已对照安装的 SPTushonka.Reflection DLL 验证)。
        Harmony.UnpatchID(nameof(VersionLabelPatch));
        Harmony.UnpatchID(nameof(PreloaderVersionLabelPatch));
        // 这六个旧的 RaidFix/ScavMode patch 在此 SPT 5 构建中已不存在。
    }

    public enum EPingSound
    {
        SubQuestComplete,
        InsuranceInsured,
        ButtonClick,
        ButtonHover,
        InsuranceItemInsured,
        MenuButtonBottom,
        ErrorMessage,
        InspectWindow,
        InspectWindowClose,
        MenuEscape,
    }

    [Flags]
    public enum EQuestSharingTypes
    {
        None = 0,
        Kills = 1,
        Item = 2,
        Location = 4,
        PlaceBeacon = 8,

        All = Kills | Item | Location | PlaceBeacon
    }

    public enum ELoadPriority
    {
        Low,
        Medium,
        High
    }
}

