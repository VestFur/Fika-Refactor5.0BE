using Diz.Resources;
using EFT.Ballistics;
using EFT.Communications;
using EFT.UI.Matchmaker;
using EFT.Utilities;
using JsonType;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InputSystem;
using EFT.UI;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.UI.Custom;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.LocalGame;

/// <summary>
/// Created by: Paulov
/// Paulov: Overwrite and use our own CoopGame instance instead
/// </summary>
public sealed class TarkovApplication_LocalGameCreator_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(TarkovApplication)
            .GetMethod(nameof(TarkovApplication.LocalGameCreate));
    }

    [PatchPrefix]
    public static bool Prefix(ref Il2CppSystem.Threading.Tasks.Task __result, TarkovApplication __instance, TimeAndWeatherSettings timeAndWeather, ClientMetricsEvents metricsEvents, ClientMetricsConfig metricsConfig, GameWorld gameWorld)
    {
        

        // The standalone Headless plugin owns LocalGameCreate on dedicated
        // hosts. Do not let the normal player-owned game factory race it if
        // Harmony retains its prefix while enabling Headless's own patch.
        if (FikaBackendUtils.IsHeadless) return true;

        // A failed result operation may never consume the previous raid's
        // Scav descriptor. Starting any new raid invalidates that snapshot,
        // including when the next raid is a native single-player session.
        Main.Patches.Overrides.RaidEndProfile.Clear();
        if (FikaBackendUtils.ClientType == EClientType.None)
        {
            // AP (analysis/nonfika-raid-inertness): this is the decisive moment - the raid that starts
            // now is the native local game (the map selection screen without Fika), so the port has to
            // stay out of it until it creates a game of its own again. Reported once per transition; the
            // line is [Inert] raid kind=native source=... in the game log.
            FikaRaidGate.NativeRaidStarted("LocalGameCreate with clientType=None");
            return true;
        }
#if DEBUG
        Logger.LogInfo("TarkovApplication_LocalGameCreator_Patch:Prefix");

#endif
        __result = NativeTask.FromManaged(MainThreadDispatcher.RunAsync(() => CreateFikaGame(__instance, timeAndWeather, __instance._raidSettings,
            __instance._inputTree, __instance._localGameDateTime, __instance._fixedDeltaTime,
            metricsEvents, metricsConfig, gameWorld, __instance._menuOperation,
            __instance._unsubscriber, __instance.BundleLock)));
        return false;
    }

    /// <summary>
    /// Entry point of the cooperative raid start. Any failure of the session setup is turned
    /// into a clean abort here: the reason goes to BepInEx/Fika-diagnostics.log with its full
    /// stack, the half-created session (client, loading screen) is torn down so the player
    /// lands on a usable main menu, and a readable error screen replaces the bare
    /// "Object reference not set to an instance of an object." box.
    /// </summary>
    public static async Task CreateFikaGame(TarkovApplication instance, TimeAndWeatherSettings timeAndWeather,
        RaidSettings raidSettings, InputTree inputTree, GameDateTime localGameDateTime, float fixedDeltaTime,
        ClientMetricsEvents metricsEvents, ClientMetricsConfig metricsConfig,
        GameWorld gameWorld, MainMenuShowOperation menuOperation,
        CompositeDisposable unsubscriber, BundleLock bundleLock)
    {
        var isServer = FikaBackendUtils.IsServer;
        
        try
        {
            await CreateFikaGameCore(instance, timeAndWeather, raidSettings, inputTree, localGameDateTime,
                fixedDeltaTime, metricsEvents, metricsConfig, gameWorld, menuOperation, unsubscriber, bundleLock);
            
        }
        catch (Exception exception)
        {
            
            FikaRaidGate.Disarm("the Fika game creation failed");
            FikaBackendUtils.MarkJoinFailed(exception.Message, exception);
            NetManagerUtils.AbortFailedJoin(isServer, "raid start failed: " + exception.Message);

            try
            {
                Singleton<PreloaderUI>.Instance.ShowErrorScreen("FIKA RAID START FAILED",
                    exception.Message + "\n\nSee BepInEx/LogOutput.log for the full stack trace.");
            }
            catch (Exception screenException)
            {
                
            }

            throw;
        }
    }

    private static async Task CreateFikaGameCore(TarkovApplication instance, TimeAndWeatherSettings timeAndWeather,
        RaidSettings raidSettings, InputTree inputTree, GameDateTime localGameDateTime, float fixedDeltaTime,
        ClientMetricsEvents metricsEvents, ClientMetricsConfig metricsConfig,
        GameWorld gameWorld, MainMenuShowOperation menuOperation,
        CompositeDisposable unsubscriber, BundleLock bundleLock)
    {
        var isServer = FikaBackendUtils.IsServer;
        // AP (analysis/nonfika-raid-inertness): the one place that knows a raid of ours really starts.
        // Everything that asks FikaRaidGate.Skip before acting inside a raid is enabled by this line and
        // by nothing else - not by the plugin being loaded, not by FikaBackendUtils.ClientType, which a
        // finished raid used to leave at Host/Client for the rest of the session.
        FikaRaidGate.Arm("CreateFikaGameCore(location=" + (raidSettings?.LocationId ?? "-") + ", role="
            + (isServer ? "host" : "client") + ")");
        
        var isTransit = FikaBackendUtils.IsTransit;

        // A preparation that failed (no network manager, no host endpoint, a rejected join)
        // must stop the raid start here: continuing is what produced the "0 % loading screen
        // plus generic NullReferenceException" report.
        if (FikaBackendUtils.JoinFailed)
        {
            throw new InvalidOperationException("The raid could not be started: " + FikaBackendUtils.JoinFailure);
        }

        if (FikaPlugin.Instance.Settings.NoAI.Value)
        {
            FikaGlobals.LogWarning("No AI enabled - stopping bot spawns");
            raidSettings.BotSettings.BotAmount = EFT.Bots.EBotAmount.NoBots;
            var waves = raidSettings.WavesSettings;
            waves.BotAmount = EFT.Bots.EBotAmount.NoBots;
            waves.IsBosses = false;
            raidSettings.WavesSettings = waves;
        }

        if (isServer && !isTransit)
        {
            FikaBackendUtils.CachedRaidSettings = raidSettings;
        }
        else if (isServer && isTransit && FikaBackendUtils.CachedRaidSettings != null)
        {
            Logger.LogInfo("Applying cached raid settings from previous raid");
            var cachedSettings = FikaBackendUtils.CachedRaidSettings;
            raidSettings.WavesSettings = cachedSettings.WavesSettings;
            raidSettings.BotSettings = cachedSettings.BotSettings;
            raidSettings.MetabolismDisabled = cachedSettings.MetabolismDisabled;
            raidSettings.PlayersSpawnPlace = cachedSettings.PlayersSpawnPlace;
        }

        metricsEvents.SetGamePrepared();

        if (Singleton<NotificationManager>.Instantiated)
        {
            Singleton<NotificationManager>.Instance.Deactivate();
        }

        var session = instance.Session;

        if (session == null)
        {
            throw new InvalidOperationException(
                "The backend session was null when the raid was initialized (the client is not logged in?).");
        }

        var profile = session.GetProfileBySide(raidSettings.Side);

        if (profile == null)
        {
            // session.GetProfileBySide returns null when the profile for the requested side
            // was never loaded, and the next statement used to fault on it.
            throw new InvalidOperationException(
                $"The backend session has no {raidSettings.Side} profile, so the raid cannot be started for that side.");
        }

        profile.Inventory.Stash = null;
        profile.Inventory.QuestStashItems = null;
        profile.Inventory.DiscardLimits = Singleton<ItemFactory>.Instance.GetDiscardLimits();

#if DEBUG
        Logger.LogInfo("TarkovApplication_LocalGameCreator_Patch:Postfix: Attempt to set Raid Settings");
        Logger.LogInfo($"RaidSettings Location: {raidSettings.LocationId}, TransitType: {raidSettings.transitionType}");
#endif

        if (!isServer)
        {
#if DEBUG
            Logger.LogInfo("Waiting for host to receive location");
#endif
            await WaitForServerToReceiveLocation();
#if DEBUG
            Logger.LogInfo("Host has received location, continuing");
#endif
        }

        if (!raidSettings.isInTransition)
        {
            await session.SendRaidSettings(raidSettings).AsManaged();
        }
        LocalRaidSettings localRaidSettings = new()
        {
            location = raidSettings.LocationId,
            timeVariant = raidSettings.SelectedDateTime,
            mode = ELocalMode.PVE_OFFLINE,
            playerSide = raidSettings.Side,
            transitionType = raidSettings.transitionType
        };
        instance._localRaidSettings = localRaidSettings;

        var localSettings = await instance.Session.LocalRaidStarted(localRaidSettings).AsManaged();
        raidSettings.BotSettings.ExcludedBosses = localSettings.excludedBosses;
        var raidSettingsToUpdate = instance._localRaidSettings;
        // SPT 5 applies scav raid-time adjustments to the returned location.
        var escapeTimeLimit = raidSettings.IsScav ? localSettings.locationLoot.EscapeTimeLimit : raidSettings.SelectedLocation.EscapeTimeLimit;
        raidSettings.SelectedLocation = localSettings.locationLoot;
        raidSettings.SelectedLocation.EscapeTimeLimit = escapeTimeLimit;
        raidSettingsToUpdate.serverId = localSettings.serverId;
        raidSettingsToUpdate.selectedLocation = localSettings.locationLoot;
        raidSettingsToUpdate.selectedLocation.EscapeTimeLimit = escapeTimeLimit;

        var transitData = FikaBackendUtils.TransitData;
        transitData.transitionType = raidSettings.transitionType;
        // The default TransitData getter creates a new native object until it
        // is assigned. Keep the configured transition on the same object used
        // by this raid and by Stop() when the party changes maps.
        FikaBackendUtils.TransitData = transitData;
        raidSettingsToUpdate.transition = transitData;

        var profileInsurance = localSettings.profileInsurance;
        if ((profileInsurance?.insuredItems) != null)
        {
            profile.InsuredItems = localSettings.profileInsurance.insuredItems;
        }

        if (!isServer)
        {
            instance.Matchmaker.UpdateMatchingStatus(EMatchingStage.ServerConnecting, new Il2CppSystem.Nullable<float>());

            RaidSettingsRequest data = new();
            var raidSettingsResponse = await FikaRequestHandler.GetRaidSettings(data);

            if (!raidSettingsResponse.Received)
            {
                throw new InvalidDataException("Failed to retrieve raid settings");
            }

            raidSettings.MetabolismDisabled = raidSettingsResponse.MetabolismDisabled;
            raidSettings.PlayersSpawnPlace = raidSettingsResponse.PlayersSpawnPlace;
            Logger.LogInfo($"Raid settings from the server: hourOfDay={raidSettingsResponse.HourOfDay}, " +
                $"scav={FikaBackendUtils.IsScav}, requested={timeAndWeather.HourOfDay}");
            timeAndWeather.HourOfDay = raidSettingsResponse.HourOfDay;
            timeAndWeather.TimeFlowType = raidSettingsResponse.TimeFlowType;

            FikaBackendUtils.CustomRaidSettings = raidSettingsResponse.CustomRaidSettings;
            Logger.LogInfo($"Received CustomRaidSettings: {raidSettingsResponse.CustomRaidSettings}");
        }
        else
        {
            instance.Matchmaker.UpdateMatchingStatus(EMatchingStage.LocalGameStarting, new Il2CppSystem.Nullable<float>());
            FikaSingleton<FikaServer>.Instance.LocationReceived = true;
        }

        // This gets incorrectly reset by the server, update it manually here during transit
        if (!FikaBackendUtils.IsHeadless && isTransit && MainMenuUIScript.Exist)
        {
            MainMenuUIScript.Instance.UpdatePresence(UI.FikaUIGlobals.EFikaPlayerPresence.IN_RAID);
        }

        StartHandler startHandler = new(instance, session.Profile, session.ProfileOfPet, raidSettings.SelectedLocation);

        var raidLimits = GetRaidMinutes(raidSettings.SelectedLocation.EscapeTimeLimit);

        var coopGame = CoopGame.Create(inputTree, profile, gameWorld, localGameDateTime, instance.Session.InsuranceCompany,
            MonoBehaviourSingleton<GameUI>.Instance, raidSettings.SelectedLocation,
            timeAndWeather, raidSettings.WavesSettings, raidSettings.SelectedDateTime,
            NativeDelegates.Bind<Callback<ExitStatus, Il2CppSystem.TimeSpan, ClientMetrics>>(startHandler, nameof(StartHandler.HandleStop)),
            fixedDeltaTime, instance.PlayerUpdateQueue, instance.Session, raidLimits, metricsEvents,
            new ClientMetricsCollector(metricsConfig, instance), localRaidSettings, raidSettings);

        startHandler.CoopGame = coopGame;

        Singleton<AbstractGame>.Create(coopGame);
        // Register both raid-scoped cleanups through the game's own NON-generic
        // CompositeDisposable.AddDisposable(Il2CppSystem.Action) overload. The generated
        // AddDisposable<T>(T) overload must not be reached from here: its interop wrapper resolves the
        // type argument through Il2CppClassPointerStore<T>, and that store only exists for
        // interop-generated types, enums, primitives/strings and ClassInjector-registered types.
        // Handing it a CLR delegate (System.Action) leaves NativeClassPtr at 0 and the generated
        // MethodInfoStoreGeneric cctor dies with a TypeInitializationException inside Il2CppInterop's
        // Class::FromIl2CppType hook (47888 run 2026-10-09 23:38, frame 9425). The game's own field is
        // a native List<Action>, so the Action overload is also the semantically correct one.
        // The two cleanups are plain managed lambdas declared on this class, never il2cpp delegates bound
        // to an injected instance method (see AddRaidDisposable): an injected method's registered MethodInfo
        // is reached by the engine through the interop invoker, which is the fatal path of
        // analysis/delegate-targets/REPORT.md.
        AddRaidDisposable(unsubscriber, "CoopGame.Dispose", () => coopGame.Dispose());
        AddRaidDisposable(unsubscriber, "StartHandler.ReleaseSingleton", () => startHandler.ReleaseSingleton());
        metricsEvents.SetGameCreated();
        FikaEventDispatcher.DispatchEvent(new AbstractGameCreatedEvent(coopGame));

        using ScreenUpdater updater = new(instance.Matchmaker, coopGame);
        if (!isServer)
        {
            coopGame.SetMatchmakerStatus("Coop game joined");
        }
        else
        {
            coopGame.SetMatchmakerStatus("Coop game created");
        }

        await coopGame.InitPlayer(raidSettings.BotSettings);
        UnityEngine.Object.DestroyImmediate(MonoBehaviourSingleton<MenuUI>.Instance.gameObject);
        menuOperation?.Unsubscribe();
        bundleLock.MaxConcurrentOperations = FikaPlugin.Instance.Settings.MaxBundleLock.Value;
        gameWorld.OnGameStarted();

        if (FikaBackendUtils.IsSpectator)
        {
            Logger.LogInfo("Starting game as spectator");
            await HandleJoinAsSpectator();
        }
    }

    /// <summary>
    /// Logs, once per session, that the file must never instantiate AddDisposable&lt;System.Action&gt;.
    /// Reading the store only runs the managed type initializer of the type argument
    /// (RunClassConstructor, no il2cpp call), so this measurement cannot fault; it documents the state
    /// that aborted the 23:38 run as a named line.
    /// </summary>
    private static void ReportGenericOverloadHazard()
    {
        if (_genericAddDisposableHazardReported)
        {
            return;
        }

        _genericAddDisposableHazardReported = true;

        long classPointer;
        try
        {
            classPointer = Il2CppInterop.Runtime.Il2CppClassPointerStore<System.Action>.NativeClassPtr.ToInt64();
        }
        catch (Exception exception)
        {
            
            return;
        }

        
    }

    /// <summary>
    /// Registers one raid cleanup with the game's CompositeDisposable.
    ///
    /// The delegate target is always a managed lambda declared on this (plain managed, non-injected)
    /// class. It must never be an il2cpp delegate built over an injected instance method
    /// (<c>Il2CppSystem.Delegate.CreateDelegate</c> / <c>NativeDelegates.Bind</c>): such a delegate carries
    /// the injected method's registered MethodInfo, and the engine reaches it through the interop invoker
    /// whose closing <c>calli</c> targets the method's trampoline thunk - the pair coreclr fail-fasts on
    /// ("attempted to call a UnmanagedCallersOnly method from managed code",
    /// analysis/delegate-targets/REPORT.md). <c>CoopGame.Dispose</c> and
    /// <c>StartHandler.ReleaseSingleton</c> were bound that way until round 8.
    ///
    /// The conversion below is the interop's managed-to-native delegate bridge
    /// (<c>DelegateSupport.ConvertDelegate</c>): it builds its own reverse-P/Invoke trampoline whose body
    /// performs one plain managed call into the lambda, i.e. the engine's invoke stops at managed code and
    /// never enters an injected method's invoker. The result stays <c>Il2CppSystem.Action</c> so the
    /// declaring type's own non-generic member is the better candidate: no generic method instantiation is
    /// created, and the native delegate is stored in the game's List&lt;Action&gt; exactly as before.
    /// </summary>
    private static void AddRaidDisposable(CompositeDisposable unsubscriber, string label, System.Action cleanup)
    {
        if (unsubscriber is null)
        {
            throw new InvalidOperationException(
                "The raid unsubscriber was null, so " + label + " could not be registered.");
        }

        ReportGenericOverloadHazard();

        Il2CppSystem.Action action;
        try
        {
            action = (Il2CppSystem.Action)cleanup;
        }
        catch (Exception exception)
        {
            
            throw;
        }

        if (action is null)
        {
            throw new InvalidOperationException(
                "The il2cpp delegate for " + label
                + " came back null, so the raid cannot clean up after itself.");
        }

        try
        {
            unsubscriber.AddDisposable(action);
        }
        catch (Exception exception) when (exception is TypeInitializationException or TypeLoadException)
        {
            
            throw;
        }
        catch (Exception exception)
        {
            
            throw;
        }

        
    }

    /// <summary>
    /// The measured probe above is a property of the interop, not of one call, so it is logged once.
    /// </summary>
    private static bool _genericAddDisposableHazardReported;

    /// <summary>
    /// Waits until the host confirms it received the local player's location.
    ///
    /// The loop used to dereference <see cref="FikaSingleton{T}.Instance"/> unconditionally and
    /// never time out: a client that was never created (preparation aborted) threw the generic
    /// NullReferenceException, and a client that never reached the host span here forever while
    /// the Fika loading screen stayed at 0 %. It now waits for the client to appear, reports
    /// its connection state every second and fails with a readable reason.
    /// </summary>
    /// <summary>
    /// Waits until the host confirms it received the local player's location.
    ///
    /// Three different states used to look the same here, and the healthy case paid for it:
    ///   * a host that is still loading its own map answers every poll with HostReceivedLocation=false;
    ///   * a host whose main thread is blocked inside a long frame answers nothing at all while its
    ///     socket thread keeps the connection alive;
    ///   * a host that is gone stops answering *and* the transport drops.
    /// A single fixed cap therefore aborted joins that only needed more time - in the 2026-10-11 18:58
    /// round the host needed 145 s for TarkovStreets (host Fika-diagnostics.log 464548 -> 609164 ms)
    /// while the client reached its own location wait after 89 s and aborted after a fixed 40 s
    /// (client Fika-diagnostics.log 477018 -> 517290 ms); that abort is what left the host waiting for a
    /// character that never came.
    ///
    /// What ends this wait, in this order:
    ///   * the transport: <c>ServerConnection.ConnectionState != Connected</c> is the only proof that the
    ///     host is gone (LiteNetLib's own thread maintains the connection, acks and keepalives, so it
    ///     survives a busy host main thread);
    ///   * <see cref="LoadSyncGate.ClientCeilingSeconds"/>, which is longer inside a transit reload
    ///     window - a connected host that never makes progress cannot be waited for forever;
    ///   * a long application-level silence is reported as a warning (every 15 s) and never ends the wait
    ///     by itself. That is the difference to the fixed cap this join died on.
    /// The counter of host responses and the local loading progress are in every [LoadSync] line, so a
    /// run can be judged whether it succeeded or not.
    /// </summary>
    private static async Task WaitForServerToReceiveLocation()
    {
        var packet = new InformationPacket();
        var span = TimeSpan.FromSeconds(1);
        var attempts = 0;
        var discoveryLimit = (FikaPlugin.Instance.Settings.ConnectionTimeout?.Value ?? 30) + 10;

        FikaClient client = null;
        while (client == null)
        {
            client = FikaSingleton<FikaClient>.Instance;
            if (client != null)
            {
                break;
            }

            if (++attempts > discoveryLimit)
            {
                throw new InvalidOperationException(
                    "The Fika client was never created, so the host could not be asked for the raid location.");
            }

            await Task.Delay(span);
        }

        attempts = 0;
        var locationCeiling = LoadSyncGate.ClientCeilingSeconds(FikaBackendUtils.IsTransit);
        var silentSeconds = 0;
        var lastResponseCount = client.HostResponseCount;
        var reportedAt = 0;

        string DescribeLocationWait()
        {
            return "stage=location role=client elapsed=" + attempts + "s responses=" + client.HostResponseCount
                + " hostReceivedLocation=" + client.HostReceivedLocation + " hostLoaded=" + client.HostLoaded
                + " hostReady=" + client.HostReady + ", transport="
                + (client.ServerConnection == null ? "no peer" : client.ServerConnection.ConnectionState.ToString())
                + " silentFor=" + silentSeconds + "s, ceiling=" + locationCeiling + "s, "
                + LoadSync.DescribePeerTraffic() + ", " + DescribeLocalProgress()
                + " (" + LoadSync.DescribeScreen() + ")";
        }

        while (!client.HostReceivedLocation)
        {
            if (client.ServerConnection == null && attempts > 5)
            {
                throw new InvalidOperationException(
                    "The Fika client is not connected to the host, so the raid cannot be started. "
                    + "The host session was reachable while joining (the ping handshake succeeded) but the game "
                    + "connection never completed - check the host's UDP port and the firewall.");
            }

            if (client.ServerConnection != null && client.ServerConnection.ConnectionState != ConnectionState.Connected
                && attempts > 5)
            {
                throw new InvalidOperationException(
                    "The connection to the raid host was lost while this client waited for the location "
                    + "acknowledgement. " + DescribeLocationWait());
            }

            try
            {
                client.SendData(ref packet, DeliveryMethod.ReliableUnordered);
            }
            catch (Exception exception)
            {
                
                throw;
            }

            // A growing response counter means the host's *application* is answering; the flags inside
            // those responses say how far its own raid start is. Silence with a live transport means the
            // host's main thread is busy (its own map load) - that is a warning, not a failure.
            if (client.HostResponseCount != lastResponseCount)
            {
                lastResponseCount = client.HostResponseCount;
                silentSeconds = 0;
            }
            else
            {
                silentSeconds++;
            }

            if (attempts >= reportedAt)
            {
                reportedAt = attempts + 5;
                LoadSync.Report("WaitForServerToReceiveLocation", DescribeLocationWait()
                    + " => waiting; the host acknowledges this once its own copy of the raid is created");
            }

            if (silentSeconds >= LoadSyncGate.ClientSilentWarnSeconds && silentSeconds % 15 == 0)
            {
                LoadSync.Warn("WaitForServerToReceiveLocation", DescribeLocationWait()
                    + " => no application-level response for " + silentSeconds + "s while the transport is "
                    + (client.ServerConnection?.ConnectionState.ToString() ?? "gone")
                    + ": the host's main thread is most likely loading its own copy of the raid; the wait continues until "
                    + locationCeiling + "s");
            }

            if (++attempts > locationCeiling)
            {
                throw new InvalidOperationException(
                    "The host has not acknowledged the raid location within " + locationCeiling + " seconds. "
                    + DescribeLocationWait() + " - see the [LoadSync] lines for how far the host's own raid start got.");
            }

            await Task.Delay(span);
        }

        LoadSync.Report("WaitForServerToReceiveLocation",
            "the host acknowledged the location after " + attempts + "s (responses=" + client.HostResponseCount
            + ") => this client continues with its own game creation");
    }

    /// <summary>
    /// The progress this machine has published for itself, for the [LoadSync] lines ("local=92.5").
    /// </summary>
    private static string DescribeLocalProgress()
    {
        try
        {
            var screen = LoadingScreenUI.Instance;
            return screen == null ? "local=absent (no screen)" : screen.DescribeLocalRow();
        }
        catch (Exception)
        {
            return "local=absent";
        }
    }

    private static TimeSpan GetRaidMinutes(int defaultMinutes)
    {
        return TimeSpan.FromSeconds(60 * defaultMinutes);
    }

    private sealed class StartHandler : Il2CppSystem.Object
    {
        private readonly TarkovApplication _tarkovApplication;
        private readonly Profile _pmcProfile;
        private readonly Profile _scavProfile;
        private readonly LocationSettings.Location _location;
        public CoopGame CoopGame;

        public StartHandler(IntPtr pointer) : base(pointer) { }

        public StartHandler(TarkovApplication tarkovApplication, Profile pmcProfile, Profile scavProfile,
            LocationSettings.Location location)
            : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<StartHandler>())
        {
            _tarkovApplication = tarkovApplication;
            _pmcProfile = pmcProfile;
            _scavProfile = scavProfile;
            _location = location;
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
            Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Il2CppSystem.Object>(this);
        }

        public void HandleStop(Result<ExitStatus, Il2CppSystem.TimeSpan, ClientMetrics> result)
        {
            _tarkovApplication.OnGameEnd(_pmcProfile.Id, _scavProfile, _location, result);
        }

        public void ReleaseSingleton()
        {
            Singleton<AbstractGame>.Release(CoopGame);
            FikaSingleton<IFikaGame>.TryRelease(CoopGame);
        }
    }

    private static async Task HandleJoinAsSpectator()
    {
        var MainPlayer = Singleton<GameWorld>.Instance.MainPlayer;

        // Teleport the player underground to avoid it from being looted
        var currentPosition = MainPlayer.Position;
        MainPlayer.Teleport(new(currentPosition.x, currentPosition.y - 75, currentPosition.z));

        // Small delay to ensure the teleport command is processed first
        await Task.Delay(250);

        DamageInfo damageInfo = new()
        {
            Damage = 1000,
            DamageType = EDamageType.Impact
        };

        // Kill the player to put it in spectator mode
        MainPlayer.ApplyDamageInfo(damageInfo, EBodyPart.Head, EBodyPartColliderType.Eyes, 0);
    }
}

internal class ScreenUpdater : IDisposable
{
    public static string StatusText { get; private set; }

    private readonly CoopGame _coopGame;
    private readonly System.Action<EMatchingStage, Il2CppSystem.Nullable<float>> _updateStatus;

    public ScreenUpdater(MatchmakerPlayersController controller, CoopGame game)
    {
        _coopGame = game;
        
    }

    public static void SetStatusText(string text) => StatusText = text;

    public void Dispose()
    {
        if (_updateStatus is not null)
        {
            _coopGame.OnMatchingStatusChanged -= _updateStatus;
        }

        StatusText = null;
    }
}
