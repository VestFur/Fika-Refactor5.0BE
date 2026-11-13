using Fika.Core.Main.BaseClasses;
using System;
using System.Collections;
using System.Threading.Tasks;
using Comfort.Common;
using CommonAssets.Scripts.Game;
using EFT;
using EFT.Bots;
using EFT.Communications;
using EFT.Counters;
using EFT.Game.Spawning;
using EFT.Interactive;
using EFT.Interactive.SecretExfiltrations;
using EFT.UI;
using EFT.UI.Screens;
using EFT.Weather;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Components;
using Fika.Core.Main.FreeCamera;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using Fika.Core.Networking.Packets;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.World;
using JsonType;
using static JsonType.LocationSettings;

namespace Fika.Core.Main.GameMode;

public class ClientGameController : BaseGameController
{
    public ClientGameController(IFikaGame game, EUpdateQueue updateQueue, GameWorld gameWorld, IEftSession session)
        : base(game, updateQueue, gameWorld, session) { }


    public bool ExfiltrationReceived { get; set; }
    public bool HasReceivedLoot { get; set; }
    public bool InteractablesInitialized { get; set; }

    public override IEnumerator WaitForHostInit(int timeBeforeDeployLocal)
    {
        Logger.LogInfo("Waiting for host init");
        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_WAIT_FOR_HOST_FINISH_INIT.Localized());

        var client = FikaSingleton<FikaClient>.Instance;
        WaitForEndOfFrame waitForEndOfFrame = new();
        do
        {
            yield return waitForEndOfFrame;
        } while (!client.HostReady);
        LootItems = null;

        Logger.LogInfo("Host init complete");
    }

    public override async Task WaitForHostToStart()
    {
        await base.WaitForHostToStart();

        GameObject startButton = null;
        if (FikaBackendUtils.IsHeadlessRequester || FikaPlugin.Instance.Settings.AnyoneCanStartRaid)
        {
            startButton = CreateStartButton() ?? throw new NullReferenceException("Start button could not be created!");
            if (FikaPlugin.Instance.Settings.DevMode.Value)
            {
                Logger.LogWarning("DevMode is enabled, skipping wait...");
                FikaNotifications.DisplayMessageNotification("DevMode enabled, starting automatically...", iconType: EFT.Communications.ENotificationIconType.Note);
                var fikaClient = FikaSingleton<FikaClient>.Instance ?? throw new NullReferenceException("CreateStartButton::FikaClient was null!");
                InformationPacket devModePacket = new()
                {
                    RequestStart = true
                };
                fikaClient.SendData(ref devModePacket, DeliveryMethod.ReliableOrdered);
            }
        }
        var client = FikaSingleton<FikaClient>.Instance;
        InformationPacket packet = new();
        client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        do
        {
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            await Task.Delay(250);
        }
        while (!RaidStarted);

        if (startButton != null)
        {
            GameObject.Destroy(startButton);
        }
    }

    public override async Task WaitForOtherPlayersToLoad()
    {
        var networkManager = FikaSingleton<IFikaNetworkManager>.Instance;
        var client = FikaSingleton<FikaClient>.Instance;
        var initialPlayerCount = Math.Max(1, networkManager.PlayerAmount);
        // The host sends player-count updates on disconnect. A late join must
        // not expand the barrier established when this client began loading.
        int ExpectedPlayers() => Math.Max(1, Math.Min(initialPlayerCount, networkManager.PlayerAmount));
        int ExpectedHumans() => Math.Max(0, ExpectedPlayers() - (FikaBackendUtils.IsHeadlessGame ? 1 : 0));
        void EnsureConnected()
        {
            if (client.ServerConnection?.ConnectionState != ConnectionState.Connected)
                throw new InvalidOperationException("Connection to the raid host was lost while waiting for players to load.");
        }

#if DEBUG
        Logger.LogWarning("Client: Waiting for coopHandler.AmountOfHumans < expected players, expected: " + ExpectedHumans());
#endif
        // Judgement line (analysis/loading-screen-sync): the client's side of the same barrier. It waits
        // for the characters (the other humans) and, in the second loop, for the host's ready count.
        //
        // What ends this wait, in this order:
        //   * EnsureConnected - the transport (LiteNetLib's own thread keeps the connection and its acks
        //     alive even while the host's main thread is busy loading a map, so "not connected" is the
        //     only proof that the host is really gone);
        //   * the ceiling from LoadSyncGate, which is longer inside a transit reload window - a connected
        //     host that never makes progress cannot be waited for forever;
        //   * a long application-level silence is *reported* (warning) but never ends the wait by itself.
        // That split is what keeps a slow host alive: the fixed 40 s cap that aborted the 2026-10-11
        // 18:58 transit join was an application-level stopwatch, not a liveness check.
        var charactersStartedAt = UnityEngine.Time.realtimeSinceStartup;
        var charactersCeiling = LoadSyncGate.ClientCeilingSeconds(FikaBackendUtils.IsTransit);
        var nextCharacterReport = 0f;
        var silentSeconds = 0;
        var lastResponseCount = client.HostResponseCount;

        string DescribeCharacterWait(int expectedHumans, int silentFor, float elapsed)
        {
            return "stage=characters role=client humans=" + _coopHandler.AmountOfHumans + "/" + expectedHumans
                + " playerAmount=" + networkManager.PlayerAmount + " responses=" + client.HostResponseCount
                + " hostLoaded=" + client.HostLoaded + " hostReady=" + client.HostReady
                + " elapsed=" + elapsed.ToString("0") + "s, " + LoadSync.DescribePeerTraffic()
                + ", " + DescribeLocalProgress() + " (" + LoadSync.DescribeScreen() + ")";
        }

        do
        {
            await Task.Delay(100);
            EnsureConnected();
            var expectedHumans = ExpectedHumans();
            _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_WAIT_FOR_OTHER_PLAYERS.Localized(),
                expectedHumans == 0 ? 1f : Math.Min(1f, (float)_coopHandler.AmountOfHumans / expectedHumans));

            if (client.HostResponseCount != lastResponseCount)
            {
                lastResponseCount = client.HostResponseCount;
                silentSeconds = 0;
            }
            else
            {
                silentSeconds++;
            }

            var elapsed = UnityEngine.Time.realtimeSinceStartup - charactersStartedAt;
            if (elapsed >= nextCharacterReport)
            {
                nextCharacterReport = elapsed + 5f;
                LoadSync.Report("ClientGameController.WaitForOtherPlayersToLoad",
                    DescribeCharacterWait(expectedHumans, silentSeconds, elapsed)
                    + " => waiting for the peer character(s) to be spawned in this world");
            }

            if (silentSeconds >= LoadSyncGate.ClientSilentWarnSeconds && silentSeconds % 15 == 0)
            {
                LoadSync.Warn("ClientGameController.WaitForOtherPlayersToLoad",
                    DescribeCharacterWait(expectedHumans, silentSeconds, elapsed)
                    + " => no application-level response for " + silentSeconds + "s while the transport is connected: "
                    + "the host's main thread is most likely loading its own copy of the raid; the wait continues until "
                    + charactersCeiling + "s");
            }

            if (elapsed >= charactersCeiling)
            {
                throw new InvalidOperationException(
                    "This client waited " + charactersCeiling + "s for the other players' characters. "
                    + DescribeCharacterWait(expectedHumans, silentSeconds, elapsed)
                    + " - see the [LoadSync] lines for the host's side of the barrier.");
            }
        } while (_coopHandler.AmountOfHumans < ExpectedHumans());

        InformationPacket packet = new()
        {
            ReadyPlayers = 1
        };

        client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
#if DEBUG
        Logger.LogWarning("Client: Waiting for client.ReadyClients < expected players, expected: " + ExpectedPlayers());
#endif
        var readyStartedAt = UnityEngine.Time.realtimeSinceStartup;
        var readyCeiling = LoadSyncGate.ClientCeilingSeconds(FikaBackendUtils.IsTransit);
        var nextReadyReport = 0f;
        var readySilentSeconds = 0;
        var lastReadyResponseCount = client.HostResponseCount;

        string DescribeReadyWait(int expectedPlayers, int silentFor, float elapsed)
        {
            return "stage=ready role=client readyClients=" + client.ReadyClients + "/" + expectedPlayers
                + " playerAmount=" + networkManager.PlayerAmount + " responses=" + client.HostResponseCount
                + " hostLoaded=" + client.HostLoaded + " hostReady=" + client.HostReady
                + " elapsed=" + elapsed.ToString("0") + "s, " + LoadSync.DescribePeerTraffic()
                + ", " + DescribeLocalProgress() + " (" + LoadSync.DescribeScreen() + ")";
        }

        do
        {
            await Task.Delay(100);
            EnsureConnected();
            var expectedPlayers = ExpectedPlayers();
            _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_WAIT_FOR_OTHER_PLAYERS.Localized(),
                Math.Min(1f, (float)client.ReadyClients / expectedPlayers));

            if (client.HostResponseCount != lastReadyResponseCount)
            {
                lastReadyResponseCount = client.HostResponseCount;
                readySilentSeconds = 0;
            }
            else
            {
                readySilentSeconds++;
            }

            var elapsed = UnityEngine.Time.realtimeSinceStartup - readyStartedAt;
            if (elapsed >= nextReadyReport)
            {
                nextReadyReport = elapsed + 5f;
                LoadSync.Report("ClientGameController.WaitForOtherPlayersToLoad",
                    DescribeReadyWait(expectedPlayers, readySilentSeconds, elapsed)
                    + " => waiting for the host to report every client as ready");
            }

            if (readySilentSeconds >= LoadSyncGate.ClientSilentWarnSeconds && readySilentSeconds % 15 == 0)
            {
                LoadSync.Warn("ClientGameController.WaitForOtherPlayersToLoad",
                    DescribeReadyWait(expectedPlayers, readySilentSeconds, elapsed)
                    + " => no application-level response for " + readySilentSeconds + "s while the transport is connected; "
                    + "the wait continues until " + readyCeiling + "s");
            }

            if (elapsed >= readyCeiling)
            {
                throw new InvalidOperationException(
                    "This client waited " + readyCeiling + "s for the host's ready barrier. "
                    + DescribeReadyWait(expectedPlayers, readySilentSeconds, elapsed)
                    + " - see the [LoadSync] lines for the host's side of the barrier.");
            }
        } while (client.ReadyClients < ExpectedPlayers());

        var finalPacket = new InformationPacket
        {
            RemoteNetId = client.NetId
        };
        client.SendData(ref finalPacket, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// The progress this machine has published for itself, for the [LoadSync] lines ("local=92.5").
    /// Never throws and never blocks: the screen is a pure managed read.
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

    public override async Task GenerateWeathers()
    {
        if (WeatherController.Instance != null)
        {
            _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_INIT_WEATHER.Localized());
            Logger.LogInfo("Generating and initializing weather...");
            await GetWeather();
            WeatherController.Instance.SetWeatherNodes(WeatherClasses);
        }
    }

    /// <summary>
    /// Gets the weather from the host
    /// </summary>
    /// <returns></returns>
    private async Task GetWeather()
    {
        RequestPacket packet = new()
        {
            Type = ERequestSubPacketType.Weather
        };

        var client = FikaSingleton<FikaClient>.Instance;
        client.SendData(ref packet, DeliveryMethod.ReliableOrdered);

        while (!WeatherReady)
        {
            await Task.Delay(1000);
            if (!WeatherReady)
            {
                client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    public override IEnumerator CountdownScreen(Profile profile, string profileId)
    {
        yield return base.CountdownScreen(profile, profileId);
        _localPlayer.PacketSender.Init();
    }

    public override async Task ReceiveSpawnPoint(Profile profile)
    {
        var spawnTogether = CheckSpawnTogether();
        if (!spawnTogether)
        {
            Logger.LogInfo("Using random spawn points!");
            FikaNotifications.DisplayMessageNotification(LocaleUtils.RANDOM_SPAWNPOINTS.Localized(),
                iconType: EFT.Communications.ENotificationIconType.Alert);

            CreateSpawnSystem(profile);
            return;
        }

        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_RETRIEVE_SPAWN_INFO.Localized());

        RequestPacket packet = new()
        {
            Type = ERequestSubPacketType.SpawnPoint
        };
        var client = FikaSingleton<FikaClient>.Instance;

        do
        {
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            await Task.Delay(1000);
            if (string.IsNullOrEmpty(InfiltrationPoint))
            {
                await Task.Delay(2000);
            }
        } while (string.IsNullOrEmpty(InfiltrationPoint));

        Logger.LogInfo($"Retrieved infiltration point '{InfiltrationPoint}' from server");
    }

    private bool CheckSpawnTogether()
    {
        if (string.Equals(RaidSettings.SelectedLocation.Id, "labyrinth", StringComparison.OrdinalIgnoreCase)
            && FikaPlugin.Instance.Settings.RandomLabyrinthSpawns)
        {
            return false;
        }

        return RaidSettings.PlayersSpawnPlace == EPlayersSpawnPlace.SamePlace;
    }

    public override void CreateSpawnSystem(Profile profile)
    {
        _spawnPoints = SpawnPointsCollection.CreateFromScene(new Il2CppSystem.Nullable<Il2CppSystem.DateTime>(DateTimeExtensions.LocalDateTimeFromUnixTime(Location.UnixDateTime)),
                                Location.SpawnPointParams);
        var spawnSafeDistance = (Location.SpawnSafeDistanceMeters > 0) ? Location.SpawnSafeDistanceMeters : 100;
        SpawnSystemSettings settings = new(Location.MinDistToFreePoint, Location.MaxDistToFreePoint, Location.MaxBotPerZone, spawnSafeDistance, Location.NoGroupSpawn, Location.OneTimeSpawn);
        SpawnSystem = SpawnSystemFactory.CreateSpawnSystem(settings, (Il2CppSystem.Func<float>)FikaGlobals.GetApplicationTime, Singleton<GameWorld>.Instance, null, _spawnPoints);
        _spawnPoint = SpawnSystem.SelectSpawnPoint(ESpawnCategory.Player, profile.Info.Side, null, null, null, null, profile.Id);
        InfiltrationPoint = string.IsNullOrEmpty(_spawnPoint.Infiltration) ? "MissingInfiltration" : _spawnPoint.Infiltration;
        ClientSpawnPosition = _spawnPoint.Position;
        ClientSpawnRotation = _spawnPoint.Rotation;
    }

    /// <summary>
    /// Waits for the host to finish its own raid initialisation (HostLoaded, i.e. the host set
    /// FikaServer.RaidInitialized at the end of its CoopGame.InitPlayer).
    ///
    /// Bounded by silence, not by a stopwatch: a host that keeps answering InformationPackets is still
    /// loading its own copy of the raid and abandoning it would abort a join that is only slow (the
    /// mistake that killed the 2026-10-11 18:58 transit join after a fixed 40 s). A host that stops
    /// answering is a dead session, and the client reports it as such.
    /// </summary>
    public async Task WaitForHostToLoad()
    {
        var client = FikaSingleton<FikaClient>.Instance;

        Logger.LogInfo("Waiting for host to init");

        InformationPacket packet = new();
        var startedAt = UnityEngine.Time.realtimeSinceStartup;
        var hostInitCeiling = LoadSyncGate.ClientCeilingSeconds(FikaBackendUtils.IsTransit);
        var nextReport = 0f;
        var silentSeconds = 0;
        var lastResponseCount = client.HostResponseCount;

        string DescribeHostInitWait(int silentFor, float elapsed)
        {
            return "stage=host-init role=client hostLoaded=" + client.HostLoaded + " hostReady=" + client.HostReady
                + " responses=" + client.HostResponseCount + " elapsed=" + elapsed.ToString("0")
                + "s, " + LoadSync.DescribePeerTraffic() + ", " + DescribeLocalProgress()
                + " (" + LoadSync.DescribeScreen() + ")";
        }

        do
        {
            _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_WAIT_FOR_HOST_INIT.Localized());

            // The transport is the liveness check: a host whose main thread is busy keeps the connection
            // (and its acks) alive from LiteNetLib's own thread, a dead host does not.
            if (client.ServerConnection?.ConnectionState != ConnectionState.Connected)
            {
                throw new InvalidOperationException(
                    "Connection to the raid host was lost while waiting for its raid initialisation. "
                    + DescribeHostInitWait(silentSeconds, UnityEngine.Time.realtimeSinceStartup - startedAt));
            }

            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);

            if (client.HostResponseCount != lastResponseCount)
            {
                lastResponseCount = client.HostResponseCount;
                silentSeconds = 0;
            }
            else
            {
                silentSeconds++;
            }

            var elapsed = UnityEngine.Time.realtimeSinceStartup - startedAt;
            if (elapsed >= nextReport)
            {
                nextReport = elapsed + 5f;
                LoadSync.Report("ClientGameController.WaitForHostToLoad",
                    DescribeHostInitWait(silentSeconds, elapsed)
                    + " => waiting for the host to finish its own raid initialisation");
            }

            if (silentSeconds >= LoadSyncGate.ClientSilentWarnSeconds && silentSeconds % 15 == 0)
            {
                LoadSync.Warn("ClientGameController.WaitForHostToLoad",
                    DescribeHostInitWait(silentSeconds, elapsed)
                    + " => no application-level response for " + silentSeconds + "s while the transport is connected; "
                    + "the wait continues until " + hostInitCeiling + "s");
            }

            if (elapsed >= hostInitCeiling)
            {
                throw new InvalidOperationException(
                    "This client waited " + hostInitCeiling + "s for the host's raid initialisation. "
                    + DescribeHostInitWait(silentSeconds, elapsed) + " - see the [LoadSync] lines.");
            }

            await Task.Delay(1000);
        } while (!client.HostLoaded);

        LoadSync.Report("ClientGameController.WaitForHostToLoad",
            "role=client the host finished its raid initialisation after "
            + (UnityEngine.Time.realtimeSinceStartup - startedAt).ToString("0") + "s (responses=" + client.HostResponseCount
            + ") => this client creates its local player and announces its character now");
    }

    public override async Task InitializeLoot(LocationSettings.Location location)
    {
        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_RETRIEVE_LOOT.Localized());
        await RetrieveLootFromServer(true, location);
        location.Loot = LootItems;
    }

    private async Task RetrieveLootFromServer(bool register,
        LocationSettings.Location location)
    {
        var client = FikaSingleton<FikaClient>.Instance;
        WorldLootPacket packet = new()
        {
            Data = []
        };
        do
        {
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            await Task.Delay(TimeSpan.FromSeconds(1));
            // WaitForHostInit clears LootItems so stale data cannot be reused.
            // It remains null until the host descriptor arrives.
            if (!HasReceivedLoot && (LootItems == null || LootItems.Count < 1))
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        } while (!HasReceivedLoot);

        if (register)
        {
            RegisterPlayerRequest request = new(0, location.Id, 0);
            await FikaRequestHandler.RegisterPlayer(request);
        }
    }

    public async Task InitExfils()
    {
        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_RETRIEVE_EXFIL_DATA.Localized());
        var client = FikaSingleton<FikaClient>.Instance;
        RequestPacket request = new()
        {
            Type = ERequestSubPacketType.Exfiltration
        };

        var attempts = 0;
        do
        {
            client.SendData(ref request, DeliveryMethod.ReliableOrdered);
            attempts++;
            if (attempts % 10 == 0)
            {
                // A stalled exfil request used to be invisible: the client simply
                // stayed on the "retrieving exfiltration data" screen forever.
                FikaGlobals.LogWarning($"InitExfils: still waiting for the exfiltration data " +
                    $"after {attempts} requests (controller={(ExfiltrationController.Instance == null ? "null" : "ready")})");
            }

            await Task.Delay(1000);
        } while (!ExfiltrationReceived);
    }

    public async Task InitInteractables()
    {
        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_RETRIEVE_INTERACTABLES.Localized());
        var client = FikaSingleton<FikaClient>.Instance;
        InteractableInitPacket packet = new(true);

        do
        {
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            await Task.Delay(1000);
            if (!InteractablesInitialized)
            {
                await Task.Delay(2000);
            }
        } while (!InteractablesInitialized);
    }

    public override void SetupEventsAndExfils(Player player)
    {
        if (_fikaGame is not CoopGame coopGame)
        {
            throw new NullReferenceException("Could not find CoopGame");
        }

        coopGame.GameTimer.Start(GameTime.ToNative(), SessionTime.ToNative());
        coopGame.Spawn();

        var skills = coopGame.Profile.Skills.Skills;
        var skillsLength = skills.Length;
        for (var i = 0; i < skillsLength; i++)
        {
            skills[i].SetPointsEarnedInSession(0f, false);
        }

        coopGame.Profile.Info.EntryPoint = InfiltrationPoint;
        Logger.LogInfo("[CLIENT] SpawnPosition: " + ClientSpawnPosition + ", InfiltrationPoint: " + InfiltrationPoint);

        var exfilController = ExfiltrationController.Instance;
        var isScav = player.Side is EPlayerSide.Savage;
        ExfiltrationPoint[] exfilPoints;
        SecretExfiltrationPoint[] secretExfilPoints;
        ExfiltrationController.Instance.InitSecretExfils(player);

        if (isScav)
        {
            exfilController.ScavExfiltrationClaim(player.Position, player.ProfileId, player.Profile.FenceInfo.AvailableExitsCount);
            var mask = exfilController.GetScavExfiltrationMask(player.ProfileId);
            exfilPoints = exfilController.ScavExfiltrationClaim(mask, player.ProfileId);
            secretExfilPoints = ExfiltrationController.Instance.GetScavSecretExits();
        }
        else
        {
            exfilPoints = exfilController.EligiblePoints(coopGame.Profile);
            secretExfilPoints = ExfiltrationController.Instance.SecretEligiblePoints();
        }

        // The game's ExtractionTimersPanel starts its slide/colour coroutines from inside SetTime,
        // and Unity refuses that on the closed (inactive) panel. Drive it only while it is live.
        if (FikaUiPanels.CanDrive(coopGame.GameUi.TimerPanel, "ClientGameController.Spawn.TimerPanel.SetTime"))
        {
            coopGame.GameUi.TimerPanel.SetTime(DateTimeExtensions.UtcNow,
                coopGame.Profile.Info.Side, coopGame.GameTimer.EscapeTimeSeconds(),
                exfilPoints, secretExfilPoints);
        }

        if (FikaWorldControllers.Find<ClientClasses.ClientTransitController>(Singleton<GameWorld>.Instance.TransitController) is { } transitController)
        {
            transitController.Init();
        }

        if (Location.EventTrapsData != null)
        {
            _gameWorld.SyncModule = new();
        }

        ExfilManager.Run(exfilPoints, secretExfilPoints);

        coopGame.Status = GameStatus.Started;

        ConsoleScreen.ApplyStartCommands();
    }

    public override void Extract(FikaPlayer player, ExfiltrationPoint exfiltrationPoint, TransitPoint transitPoint = null)
    {
        if (_fikaGame is not CoopGame coopGame)
        {
            throw new NullReferenceException("CoopGame was missing");
        }

        if (coopGame.ExtractedPlayers.Contains(player.NetId)) return;

        var preloaderUI = Singleton<PreloaderUI>.Instance;
        _localTriggerZones = [.. player.TriggerZones];

        player.ClientMovementContext.SetGravity(false);
        var position = player.Position;
        position.y += 500f;
        player.Teleport(position);

        if (coopGame.ExitStatus == ExitStatus.MissingInAction)
        {
            FikaNotifications.DisplayMessageNotification(LocaleUtils.PLAYER_MIA.Localized(), iconType: EFT.Communications.ENotificationIconType.Alert, textColor: new Il2CppSystem.Nullable<Color>(Color.red));
        }

        if (player.FikaQuests is ClientQuestController clientQuestController)
        {
            clientQuestController.ToggleSend(false);
        }

        if (player.FikaQuests is ClientSharedQuestController sharedQuestController)
        {
            sharedQuestController.ToggleQuestSharing(false);
        }

        // The native statistics base queues health and metabolism experience
        // until extraction; consume it before Runner and result evaluation.
        player.StatisticsManager?.TryCast<HealthStatisticsManager>()?.ConsumeExperience();

#if !DEBUG
        var matchEndConfig = Singleton<GlobalConfiguration>.Instance.Experience.MatchEnd;
        if (player.Profile.EftStats.SessionCounters.GetAllInt(NativeCounters.ExperienceTags()) < matchEndConfig.SurvivedExpRequirement && coopGame.PastTime < matchEndConfig.SurvivedTimeRequirement)
        {
            coopGame.ExitStatus = ExitStatus.Runner;
        }
#endif

        if (exfiltrationPoint != null)
        {
            exfiltrationPoint.Disable();

            if (exfiltrationPoint.HasRequirements && exfiltrationPoint.TransferItemRequirement?.Met(player, exfiltrationPoint) == true && player.IsYourPlayer)
            {
                // Seems to already be handled by SPT so we only add it visibly
                player.Profile.EftStats.SessionCounters.AddDouble(0.2d, NativeCounters.ExitStanding());
            }
        }

        if (player.Side == EPlayerSide.Savage)
        {
            // Seems to already be handled by SPT so we only add it visibly
            player.Profile.EftStats.SessionCounters.AddDouble(0.01d, NativeCounters.ExitStanding());
        }

        var transitController = Singleton<GameWorld>.Instance.TransitController;
        if (transitController != null && transitPoint != null)
        {
            if (transitController.alreadyTransits.TryGetValue(player.ProfileId, out var data))
            {
                coopGame.ExitStatus = ExitStatus.Transit;
                coopGame.ExitLocation = transitPoint.parameters.name;
                FikaBackendUtils.IsTransit = true;
            }
            else
            {
                Logger.LogError("Exit was transit, but could not find alreadyTransits data");
            }
        }

        if (_coopHandler != null)
        {
            try // This is to allow clients to extract if they lose connection
            {
                FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.ClientExtract,
                    ClientExtract.FromValue(player.NetId), true);
            }
            catch
            {

            }

            var fikaPlayer = player;
            coopGame.ExtractedPlayers.Add(fikaPlayer.NetId);
            _coopHandler.ExtractedPlayers.Add(fikaPlayer.NetId);
            _coopHandler.Players.Remove(fikaPlayer.NetId);

            preloaderUI.StartBlackScreenShow(2f, 2f, (System.Action)(() => preloaderUI.FadeBlackScreen(2f, -2f)));

            player.ActiveHealthController.SetDamageCoeff(0);
            player.ActiveHealthController.DamageMultiplier = 0;
            player.ActiveHealthController.DisableMetabolism();
            player.ActiveHealthController.PauseAllEffects();

            _extractRoutine = coopGame.StartCoroutine(ExtractRoutine(player, coopGame));

            // Prevents players from looting after extracting
            EftScreenManager.Instance.CloseAllScreensForced();

            // Destroys session timer
            if (TimeManager != null)
            {
                UnityEngine.Object.Destroy(TimeManager);
            }
            if (FikaUiPanels.CanDrive(coopGame.GameUi.TimerPanel, "ClientGameController.Extract.TimerPanel.Close"))
            {
                coopGame.GameUi.TimerPanel.Close();
            }

            if (FikaPlugin.Instance.Settings.AutoExtract.Value || FikaBackendUtils.IsTransit)
            {
                coopGame.Stop(_localPlayer.ProfileId, coopGame.ExitStatus, _localPlayer.ActiveHealthController.IsAlive ? coopGame.ExitLocation : null, 0);
            }
        }
        else
        {
            throw new NullReferenceException("Extract: CoopHandler was null!");
        }
    }

    public override async Task StartBotSystemsAndCountdown(BotControllerSettings controllerSettings, GameWorld gameWorld)
    {
        LoadingScreenUI.Instance.UpdateAndBroadcast(80f);

        if (Location.Id == "laboratory")
        {
            Logger.LogInfo("Location is 'Laboratory', skipping weather generation");
            Season = ESeason.Summer;
            FikaBackendUtils.CustomRaidSettings.UseCustomWeather = false;
        }
        else
        {
            await GenerateWeathers();
        }

        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_FINISHING_RAID_INIT.Localized());

        gameWorld.RegisterRestrictableZones();
        gameWorld.RegisterBorderZones();

#if DEBUG
        Logger.LogWarning("Starting " + nameof(BaseGameController.WaitForOtherPlayersToLoad));
#endif
        await WaitForOtherPlayersToLoad();

        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_FINISHING_RAID_INIT.Localized());
        Logger.LogInfo("All players are loaded, continuing...");

        // Add FreeCamController to GameWorld GameObject
        var freeCamController = gameWorld.gameObject.AddComponent<FreeCameraController>();
        FikaSingleton<FreeCameraController>.Create(freeCamController);

        await SetupRaidCode();

        LoadingScreenUI.Instance.UpdateAndBroadcast(85f);

        Singleton<GlobalConfiguration>.Instance.TimeBeforeDeployLocal = Math.Max(Singleton<GlobalConfiguration>.Instance.TimeBeforeDeployLocal, 3);
    }
}

