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
/// Fika.Core main plugin. <br/> <br/>
/// Originally by: Paulov <br/>
/// Re-written by: <see langword="Lacyway and the Fika team"/>
/// </summary>
[BepInPlugin("com.fika.core", "Fika.Core", FikaVersion)]
[BepInProcess("EscapeFromTarkov.exe")]
[BepInDependency("com.sptushonka.custom", BepInDependency.DependencyFlags.HardDependency)] // This is used so that we guarantee to load after spt-custom, that way we can disable its patches
[BepInDependency("com.sptushonka.singleplayer", BepInDependency.DependencyFlags.HardDependency)] // This is used so that we guarantee to load after spt-singleplayer, that way we can disable its patches
[BepInDependency("com.sptushonka.core", BepInDependency.DependencyFlags.HardDependency)] // This is used so that we guarantee to load after spt-core, that way we can disable its patches
[BepInDependency("com.sptushonka.debugging", BepInDependency.DependencyFlags.HardDependency)] // This is used so that we guarantee to load after spt-debugging, that way we can disable its patches
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

        // The Unity lifecycle component owns the main-thread queue. It has to
        // exist before anything below can fail: without it every dispatched
        // operation (inventory, spawn queue, lobby actions) would be enqueued
        // and never drained, which looks exactly like the game freezing.
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
            // Bots fall back to the game's own difficulty when this is missing,
            // so a server or serialization problem must not abort the plugin.
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
    /// Run these at start to hopefully ensure that all mods are loaded
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
    /// Task that ensure all mods are loaded by waiting 5 seconds
    /// </summary>
    /// <remarks>
    /// The wait is most likely redundant as it runs inside <see cref="Start"/>, however it is kept as last safety check
    /// </remarks>
    private async Task RunChecks()
    {
        
        // First-chance probe: names the object the engine is working on when the menu fault
        // fires (see FirstChanceFaultProbe). It only observes; 
        
        
        ReportStartupSettings();
        await Task.Delay(5000);
#if !DEBUG
        try
        {
            VerifyServerVersion();
        }
        catch (Exception exception)
        {
            // The server check already reports its own failure to the user; 
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
            // Degradation, not a failure: the WAN address is only ever read by the raid
            // host (FikaServer.Init) to advertise itself. Every other path - including the
            // whole client join path - works without it, so this must never abort anything.
            Log.LogWarning("RunChecks: no public IP address is known. Hosting a raid for players outside "
                + "your network will not work (set 'Force IP' in the network settings to announce one); "
                + "joining a raid is unaffected.");
            
        }
        else
        {
            
        }

        
    }

    /// <summary>
    /// Writes the settings that change what the next join attempt does into the stable
    /// diagnostics file, so a captured failure can be read against the config that produced it.
    ///
    /// RunChecks runs at frame 1, which is *before* the locales are ready and therefore before
    /// <see cref="FikaConfig.SetupConfig"/> has bound the entries: every entry property is still
    /// null at that moment, and reading one of them made this diagnostic report a
    /// NullReferenceException about itself on its very first use (2026-10-09 22:57:25). The
    /// entries are read defensively here and the report is repeated once the config is bound, so
    /// the file still ends up with the settings the run actually used.
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

            // 'SetupConfig' binds every one of these at once, so one of them is a valid probe.
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

    /// <summary>Reads a boolean entry that may not be bound yet (see <see cref="ReportStartupSettings"/>).</summary>
    private static string ReadFlag(BepInEx.Configuration.ConfigEntry<bool> entry)
    {
        return entry is null ? "unbound" : entry.Value.ToString();
    }

    /// <summary>Reads an integer entry that may not be bound yet (see <see cref="ReportStartupSettings"/>).</summary>
    private static string ReadNumber(BepInEx.Configuration.ConfigEntry<int> entry)
    {
        return entry is null ? "unbound" : entry.Value.ToString();
    }

    /// <summary>True once the config report was written with the entries that were really bound.</summary>
    private bool _boundSettingsReported;

    private void GetClientConfig()
    {
        Settings.GetClientConfig();
    }

    private void GetNatPunchServerConfig()
    {
        var natPunchServerConfig = FikaRequestHandler.GetNatPunchServerConfig();

        NatPunchServerEnable = natPunchServerConfig.Enable;

        // The SPT backend host can be a bracketed IPv6 literal such as
        // https://[::1]:6969; splitting on ':' would leave a stray '[' behind.
        var backendHost = RequestHandler.Host;
        NatPunchServerIP = Uri.TryCreate(backendHost, UriKind.Absolute, out var backendUri)
            ? backendUri.Host
            : backendHost.Replace("https://", string.Empty).Replace("http://", string.Empty).Trim('[', ']');

        NatPunchServerPort = (ushort)natPunchServerConfig.Port;

        natPunchServerConfig.LogValues();
    }

    /// <summary>
    /// This is required for the locales to be properly loaded, for some reason they are still unavailable for a few seconds after getting populated
    /// </summary>
    /// <param name="localesTask">The <see cref="Task"/> that populates the locales</param>
    public void WaitForLocales(Il2CppSystem.Threading.Tasks.Task localesTask)
    {
        ArgumentNullException.ThrowIfNull(localesTask);
        if (System.Threading.Interlocked.CompareExchange(ref _localesTask, localesTask, null) != null) return;
        Log.LogInfo("Waiting for locales to be ready...");
    }

    // Unity's Update owns all native task, localization and configuration/UI access.
    // A CLR async continuation is not guaranteed to resume on Unity's main thread.
    internal void UpdateInitialization()
    {
        if (LocalesLoaded || InitializationError != null) return;
        var task = System.Threading.Volatile.Read(ref _localesTask);
        if (task == null) return;
        try
        {
            if (!task.IsCompleted) return;
            task.GetAwaiter().GetResult(); // Observe cancellation/failure before binding settings.
            if (!FikaBackendUtils.IsHeadless &&
                LocaleUtils.BEPINEX_H_ADVANCED.Localized() == "F_BepInEx_H_Advanced") return;
            Settings.SetupConfig();
            // Now that the entries exist, record the settings this run really uses (the frame-1
            // report could only say "not bound yet").
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
        // ModulePatch no longer has Disable(). Its Harmony owner is the patch
        // type name (verified against the installed SPTushonka.Reflection DLL).
        Harmony.UnpatchID(nameof(VersionLabelPatch));
        Harmony.UnpatchID(nameof(PreloaderVersionLabelPatch));
        // The six old RaidFix/ScavMode patches are absent in this SPT 5 build.
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

