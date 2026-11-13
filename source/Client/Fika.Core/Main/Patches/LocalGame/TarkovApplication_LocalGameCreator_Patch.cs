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
        // Harmony retains this prefix while enabling Headless's own patch.
        if (FikaBackendUtils.IsHeadless) return true;

        // A failed result operation may never consume the previous raid's
        // Scav descriptor. Starting any new raid invalidates that snapshot,
        // including when the next raid is a native single-player session.
        Main.Patches.Overrides.RaidEndProfile.Clear();
        if (FikaBackendUtils.ClientType == EClientType.None) return true;
#if DEBUG
        Logger.LogInfo("TarkovApplication_LocalGameCreator_Patch:Prefix");

#endif
        __result = NativeTask.FromManaged(MainThreadDispatcher.RunAsync(() => CreateFikaGame(__instance, timeAndWeather, __instance._raidSettings,
            __instance._inputTree, __instance._localGameDateTime, __instance._fixedDeltaTime,
            metricsEvents, metricsConfig, gameWorld, __instance._menuOperation,
            __instance._unsubscriber, __instance.BundleLock)));
        return false;
    }

    public static async Task CreateFikaGame(TarkovApplication instance, TimeAndWeatherSettings timeAndWeather,
        RaidSettings raidSettings, InputTree inputTree, GameDateTime localGameDateTime, float fixedDeltaTime,
        ClientMetricsEvents metricsEvents, ClientMetricsConfig metricsConfig,
        GameWorld gameWorld, MainMenuShowOperation menuOperation,
        CompositeDisposable unsubscriber, BundleLock bundleLock)
    {
        var isServer = FikaBackendUtils.IsServer;
        var isTransit = FikaBackendUtils.IsTransit;

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
            throw new NullReferenceException("Backend session was null when initializing game!");
        }

        var profile = session.GetProfileBySide(raidSettings.Side);

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
        // Use the concrete Action overload. The generated generic closure
        // cannot represent Fika's injected type or a CLR delegate type.
        unsubscriber.AddDisposable(NativeDelegates.Bind<Il2CppSystem.Action>(coopGame, nameof(CoopGame.Dispose)));
        unsubscriber.AddDisposable(NativeDelegates.Bind<Il2CppSystem.Action>(startHandler, nameof(StartHandler.ReleaseSingleton)));
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

    private static async Task WaitForServerToReceiveLocation()
    {
        var packet = new InformationPacket();
        var client = FikaSingleton<FikaClient>.Instance;
        var span = TimeSpan.FromSeconds(1);

        do
        {
            client.SendData(ref packet, DeliveryMethod.ReliableUnordered);
            await Task.Delay(span);
        } while (!client.HostReceivedLocation);
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
    private readonly Il2CppSystem.Action<EMatchingStage, Il2CppSystem.Nullable<float>> _updateStatus;

    public ScreenUpdater(MatchmakerPlayersController controller, CoopGame game)
    {
        _coopGame = game;
        // Nullable<float> is a boxed native value type. Bind the native method
        // directly; DelegateSupport cannot marshal it through a CLR Action.
        _updateStatus = NativeDelegates.Bind<Il2CppSystem.Action<EMatchingStage, Il2CppSystem.Nullable<float>>>(
            controller, nameof(MatchmakerPlayersController.UpdateMatchingStatus));
        game.OnMatchingStatusChanged += _updateStatus;
    }

    public static void SetStatusText(string text) => StatusText = text;

    public void Dispose()
    {
        _coopGame.OnMatchingStatusChanged -= _updateStatus;
        StatusText = null;
    }
}
