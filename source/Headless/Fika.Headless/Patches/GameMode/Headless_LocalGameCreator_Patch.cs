using Comfort.Common;
using Diz.Resources;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Fika.Core;
using EFT;
using EFT.Communications;
using EFT.InputSystem;
using EFT.UI;
using EFT.Utilities;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Headless.Classes.GameMode;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using JsonType;
using SPTushonka.Reflection.Patching;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace Fika.Headless.Patches.GameMode;

internal class Headless_LocalGameCreator_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(TarkovApplication)
            .GetMethod(nameof(TarkovApplication.LocalGameCreate));
    }

    [PatchPrefix]
    public static bool Prefix(ref Il2CppSystem.Threading.Tasks.Task __result, TarkovApplication __instance, TimeAndWeatherSettings timeAndWeather,
        ClientMetricsEvents metricsEvents, ClientMetricsConfig metricsConfig, GameWorld gameWorld)
    {
        FikaHeadlessPlugin.FikaHeadlessLogger.LogInfo("[Headless] Stage: LocalGameCreate intercepted; building the headless raid host instead of a player-owned game");
        __result = NativeTask.FromManaged(MainThreadDispatcher.RunAsync(() => CreateFikaGame(__instance, timeAndWeather, __instance._raidSettings, __instance._localGameDateTime,
            __instance._fixedDeltaTime, metricsEvents, gameWorld, __instance._menuOperation,
            __instance._unsubscriber, __instance.BundleLock)));
        return false;
    }

    public static async Task CreateFikaGame(TarkovApplication instance, TimeAndWeatherSettings timeAndWeather,
        RaidSettings raidSettings, GameDateTime localGameDateTime, float fixedDeltaTime, ClientMetricsEvents metricsEvents,
        GameWorld gameWorld, MainMenuShowOperation menuOperation, CompositeDisposable unsubscriber, BundleLock bundleLock)
    {
        var isTransit = FikaBackendUtils.IsTransit;

        if (FikaPlugin.Instance.Settings.NoAI.Value)
        {
            FikaHeadlessPlugin.FikaHeadlessLogger.LogWarning("No AI enabled - stopping Headless bot spawns");
            raidSettings.BotSettings.BotAmount = EFT.Bots.EBotAmount.NoBots;
            var waves = raidSettings.WavesSettings;
            waves.BotAmount = EFT.Bots.EBotAmount.NoBots;
            waves.IsBosses = false;
            raidSettings.WavesSettings = waves;
        }

        if (!isTransit)
        {
            FikaBackendUtils.CachedRaidSettings = raidSettings;
        }
        else if (isTransit && FikaBackendUtils.CachedRaidSettings != null)
        {
            FikaHeadlessPlugin.FikaHeadlessLogger.LogInfo("Applying cached raid settings from previous raid");
            var cachedSettings = FikaBackendUtils.CachedRaidSettings;
            raidSettings.WavesSettings = cachedSettings.WavesSettings;
            raidSettings.BotSettings = cachedSettings.BotSettings;
            raidSettings.MetabolismDisabled = cachedSettings.MetabolismDisabled;
            raidSettings.PlayersSpawnPlace = cachedSettings.PlayersSpawnPlace;

            // LocalGameMatching(inTransition: true) restores the destination
            // from TarkovApplication.TransitionStatus, while the native raid
            // settings object is rebuilt by SPT. Keep the two transition
            // representations aligned so LocalGameStarted and the transit
            // history receive the continuation state instead of the first
            // raid's None/Event value.
            var transitionSettings = FikaBackendUtils.TransitData;
            raidSettings.isInTransition = true;
            raidSettings.transitionType = transitionSettings?.transitionType ?? ELocationTransition.Common;
        }

        metricsEvents.SetGamePrepared();

        if (Singleton<NotificationManager>.Instantiated)
        {
            Singleton<NotificationManager>.Instance.Deactivate();
        }

        var session = instance.Session;
        if (session == null)
        {
            throw new NullReferenceException("Backend session was null when initializing game!");
        }

        var profile = session.GetProfileBySide(raidSettings.Side);
        if (profile?.Inventory == null)
            throw new InvalidOperationException($"Headless {raidSettings.Side} profile or inventory is unavailable");
        FikaHeadlessPlugin.FikaHeadlessLogger.LogInfo($"[Headless] Stage: raid host profile ready (profileId={profile.ProfileId}, side={raidSettings.Side}, " +
            $"stashCleared={profile.Inventory.Stash == null})");

        profile.Inventory.Stash = null;
        profile.Inventory.QuestStashItems = null;
        profile.Inventory.DiscardLimits = Singleton<ItemFactory>.Instance.GetDiscardLimits();

#if DEBUG
        FikaHeadlessPlugin.FikaHeadlessLogger.LogInfo($"RaidSettings TransitType: {raidSettings.transitionType}");
#endif

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
        if (localSettings?.locationLoot == null)
            throw new InvalidOperationException("Headless local raid did not return location loot");
        FikaHeadlessPlugin.FikaHeadlessLogger.LogInfo($"[Headless] Stage: server returned the local raid (serverId={localSettings.serverId}, " +
            $"location={localSettings.locationLoot.Id}, escapeTimeLimit={localSettings.locationLoot.EscapeTimeLimit})");
        var botControllerSettings = raidSettings.BotSettings;
        botControllerSettings.ExcludedBosses = localSettings.excludedBosses;
        raidSettings.BotSettings = botControllerSettings;
        var raidSettingsToUpdate = instance._localRaidSettings;
        var escapeTimeLimit = raidSettings.IsScav
            ? localSettings.locationLoot.EscapeTimeLimit
            : raidSettings.SelectedLocation.EscapeTimeLimit;
        raidSettings.SelectedLocation = localSettings.locationLoot;
        raidSettings.SelectedLocation.EscapeTimeLimit = escapeTimeLimit;
        raidSettingsToUpdate.serverId = localSettings.serverId;
        raidSettingsToUpdate.selectedLocation = localSettings.locationLoot;
        raidSettingsToUpdate.selectedLocation.EscapeTimeLimit = escapeTimeLimit;

        var transitData = FikaBackendUtils.TransitData;
        transitData.transitionType = raidSettings.transitionType;
        FikaBackendUtils.TransitData = transitData;
        raidSettingsToUpdate.transition = transitData;

        if (localSettings.profileInsurance?.insuredItems != null)
            profile.InsuredItems = localSettings.profileInsurance.insuredItems;

        instance.Matchmaker.UpdateMatchingStatus(EFT.UI.Matchmaker.EMatchingStage.LocalGameStarting,
            new Il2CppSystem.Nullable<float>());
        FikaSingleton<FikaServer>.Instance.LocationReceived = true;

        StartHandler startHandler = new(instance, session.Profile, session.ProfileOfPet, raidSettings.SelectedLocation);

        var raidLimits = GetRaidMinutes(raidSettings.SelectedLocation.EscapeTimeLimit);

        var headlessGame = HeadlessGame.Create(gameWorld, localGameDateTime, raidSettings.SelectedLocation, timeAndWeather,
            raidSettings.WavesSettings, raidSettings.SelectedDateTime,
            Bind<Callback<ExitStatus, Il2CppSystem.TimeSpan, ClientMetrics>>(startHandler, nameof(StartHandler.HandleStop)),
            fixedDeltaTime, instance.PlayerUpdateQueue, instance.Session, raidLimits, localRaidSettings, raidSettings);

        startHandler.HeadlessGame = headlessGame;

        FikaHeadlessPlugin.FikaHeadlessLogger.LogInfo("[Headless] Stage: creating the headless game singleton");
        Singleton<AbstractGame>.Create(headlessGame);
        unsubscriber.AddDisposable(headlessGame.Cast<AbstractGame>());
        unsubscriber.AddDisposable(Bind<Il2CppSystem.Action>(startHandler, nameof(StartHandler.ReleaseSingleton)));
        metricsEvents.SetGameCreated();
        FikaEventDispatcher.DispatchEvent(new AbstractGameCreatedEvent(headlessGame));

        headlessGame.SetMatchmakerStatus("Headless game created");

        try
        {
            await headlessGame.Init(raidSettings.BotSettings);
        }
        catch (Exception ex)
        {
            FikaHeadlessPlugin.FikaHeadlessLogger.LogError($"[Headless] Raid init FAILED: {ex.Message}");
            throw;
        }
        if (MonoBehaviourSingleton<MenuUI>.Instantiated)
            UnityEngine.Object.DestroyImmediate(MonoBehaviourSingleton<MenuUI>.Instance.gameObject);
        menuOperation?.Unsubscribe();
        bundleLock.MaxConcurrentOperations = 1;
        gameWorld.OnGameStarted();
        FikaHeadlessPlugin.FikaHeadlessLogger.LogInfo("[Headless] Stage: world started, raid host is up");

        FikaEventDispatcher.DispatchEvent(new GameWorldStartedEvent(gameWorld));
    }

    private static TimeSpan GetRaidMinutes(int defaultMinutes)
    {
        return TimeSpan.FromSeconds(60 * defaultMinutes);
    }

    private static T Bind<T>(Il2CppSystem.Object target, string method) where T : Il2CppSystem.Delegate =>
        Il2CppSystem.Delegate.CreateDelegate(Il2CppType.Of<T>(), target, method).Cast<T>();

    private sealed class StartHandler : Il2CppSystem.Object
    {
        private readonly TarkovApplication _tarkovApplication;
        private readonly Profile _pmcProfile;
        private readonly Profile _scavProfile;
        private readonly LocationSettings.Location _location;
        public HeadlessGame HeadlessGame;

        public StartHandler(IntPtr pointer) : base(pointer) { }

        public StartHandler(TarkovApplication tarkovApplication, Profile pmcProfile, Profile scavProfile,
            LocationSettings.Location location)
            : this(ClassInjector.DerivedConstructorPointer<StartHandler>())
        {
            _tarkovApplication = tarkovApplication;
            _pmcProfile = pmcProfile;
            _scavProfile = scavProfile;
            _location = location;
            ClassInjector.DerivedConstructorBody(this);
            ClassInjector.InvokeBaseConstructor<Il2CppSystem.Object>(this);
        }

        public void HandleStop(Result<ExitStatus, Il2CppSystem.TimeSpan, ClientMetrics> result)
        {
            _tarkovApplication.OnGameEnd(_pmcProfile.Id, _scavProfile, _location, result);
        }

        public void ReleaseSingleton()
        {
            Singleton<AbstractGame>.Release(HeadlessGame);
            FikaSingleton<IFikaGame>.TryRelease(HeadlessGame);
        }
    }
}
