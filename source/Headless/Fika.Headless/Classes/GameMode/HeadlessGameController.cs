using Comfort.Common;
using CommonAssets.Scripts.Game;
using CommonAssets.Scripts.Game.LabyrinthEvent;
using EFT;
using EFT.Game.Spawning;
using EFT.GlobalEvents;
using EFT.Interactive;
using EFT.UI;
using Fika.Core;
using Fika.Core.Main.Components;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.Networking.Packets.Communication;
using JsonType;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Fika.Headless.Classes.GameMode;

internal class HeadlessGameController : HostGameController
{
    public HeadlessGameController(IFikaGame game, EUpdateQueue updateQueue, GameWorld gameWorld, IEftSession session,
        LocationSettings.Location location, WavesSettings wavesSettings, GameDateTime gameDateTime)
        : base(game, updateQueue, gameWorld, session, location, wavesSettings, gameDateTime)
    {
    }

    public override void SetupEventsAndExfils(Player player)
    {
        // SelectSpawnPoint can return null (no spawn point for the requested
        // side on this location). The client host path logs "unknown" in that
        // case; dereferencing it here would abort the raid setup coroutine.
        Logger.LogInfo("[Headless] Stage: raid started at spawn point: " + (_spawnPoint?.Id ?? "unknown") +
            ", InfiltrationPoint: " + (InfiltrationPoint ?? "unknown"));
        _abstractGame.GameTimer.Start(GameTime.ToNative(), SessionTime.ToNative());

        // Headless has no profile or local player. Bind all initialized exits;
        // each remote client applies its own eligibility and performs its own extract.
        var exfilController = ExfiltrationController.Instance;
        HeadlessExfilManager ??= _abstractGame.gameObject.AddComponent<HeadlessExfilManager>();

        // The headless host is the authority for remote flare activations. It has
        // no local player, so the manager only relays and applies reports here.
        if (_abstractGame.gameObject.GetComponent<FikaFlareExfilManager>() == null)
        {
            _abstractGame.gameObject.AddComponent<FikaFlareExfilManager>();
        }
        if (exfilController != null)
        {
            HeadlessExfilManager.Run(exfilController.ExfiltrationPoints, exfilController.SecretExfiltrationPoints);
            Logger.LogInfo($"[Headless] Stage: bound {exfilController.ExfiltrationPoints?.Length ?? 0} exfiltration points and " +
                $"{exfilController.SecretExfiltrationPoints?.Length ?? 0} secret points");
        }
        else
        {
            Logger.LogError("[Headless] ExfiltrationController was not instantiated; no exfiltration points were bound");
        }

        if (TransitController.Exist(out FikaHeadlessTransitController transitController))
        {
            transitController.Init();
            foreach (var activePlayer in CoopHandler.HumanPlayers)
            {
                // Transit history belongs to each remote player. The
                // dedicated host's LocalRaidSettings describe only the host
                // profile and can diverge after players cross maps.
                if (!transitController.summonedTransits.TryGetValue(activePlayer.ProfileId, out var history))
                {
                    Logger.LogWarning($"Skipping Headless transit initialization for {activePlayer.ProfileId}: no player history");
                    continue;
                }
                var enabledPointIds = new System.Collections.Generic.List<int>();
                var registeredPointId = -1;
                foreach (var entry in transitController.pointsById)
                {
                    if (entry.Value.Enabled)
                    {
                        enabledPointIds.Add(entry.Key);
                    }
                    if (transitController.transitPlayers.ContainsKey(activePlayer.ProfileId) &&
                        entry.Value._playerGroups.ContainsKey(activePlayer.ProfileId))
                    {
                        registeredPointId = entry.Key;
                    }
                }
                transitController.profileKeys.TryGetValue(activePlayer.ProfileId, out var keyId);
                var selectedTime = transitController.profileTime.TryGetValue(activePlayer.ProfileId, out var savedTime)
                    ? savedTime : transitController.LocalRaidSettings.timeVariant;
                var initEvent = new TransitInitEvent
                {
                    PlayerRaidId = activePlayer.RaidId,
                    Points = ActiveTransitPoints(transitController),
                    CompletedQuestRequirementMetByPointId = CompletedQuestRequirements(activePlayer),
                    TransitionCount = (ushort)history.count,
                    EventPlayer = history.events
                };

                var initPacket = new TransitEventPacket
                {
                    EventType = TransitEventPacket.ETransitEventType.Init,
                    TransitEvent = initEvent,
                    EnabledPointIds = enabledPointIds.ToArray(),
                    RegisteredPointId = registeredPointId,
                    KeyId = keyId,
                    SelectedTime = selectedTime
                };
                _server.SendData(ref initPacket, DeliveryMethod.ReliableOrdered);
            }
        }

        if (Location.EventTrapsData != null)
        {
            LabyrinthSyncableTraps.InitLabyrinthSyncableTraps(Location.EventTrapsData);
            _gameWorld.SyncModule = new();
        }
        _abstractGame.Status = GameStatus.Started;
        Logger.LogInfo("[Headless] Stage: game status is Started");

        // The start-command console screen is a UI object. A headless host can
        // run without it, and this hook runs inside the raid setup coroutine,
        // so a UI failure here must not prevent the raid from starting.
        try
        {
            ConsoleScreen.ApplyStartCommands();
        }
        catch (Exception exception)
        {
            Logger.LogError($"[Headless] ConsoleScreen.ApplyStartCommands failed (non fatal in a headless host): {exception}");
        }
    }

    public void ActivateBots()
    {
        _botsController.Bots.CheckActivation();
    }

    public HeadlessExfilManager HeadlessExfilManager { get; private set; }

    public override void CreateSpawnSystem(Profile profile)
    {
        _spawnPoints = SpawnPointsCollection.CreateFromScene(new Il2CppSystem.Nullable<Il2CppSystem.DateTime>(DateTimeExtensions.LocalDateTimeFromUnixTime(Location.UnixDateTime)),
                                Location.SpawnPointParams);
        var spawnSafeDistance = (Location.SpawnSafeDistanceMeters > 0) ? Location.SpawnSafeDistanceMeters : 100;
        SpawnSystemSettings settings = new(Location.MinDistToFreePoint,
            Location.MaxDistToFreePoint, Location.MaxBotPerZone, spawnSafeDistance,
            Location.NoGroupSpawn, Location.OneTimeSpawn);
        SpawnSystem = SpawnSystemFactory.CreateSpawnSystem(settings, (Il2CppSystem.Func<float>)FikaGlobals.GetApplicationTime, Singleton<GameWorld>.Instance, _botsController, _spawnPoints);

        var side = FikaSingleton<IFikaNetworkManager>.Instance.RaidSide == ESideType.Pmc ? EPlayerSide.Usec : EPlayerSide.Savage;

        _spawnPoint = SpawnSystem.SelectSpawnPoint(ESpawnCategory.Player, side,
            null, null, null, null, null);
        if (_spawnPoint == null)
        {
            // A missing spawn point is not fatal for a dedicated host: it has
            // no local player to place, and the spawn point is only used for
            // the native spawn system and the first player's camera anchor.
            Logger.LogWarning($"[Headless] No player spawn point was available for side {side} on {Location?.Id}");
            InfiltrationPoint = "MissingInfiltration";
            return;
        }

        InfiltrationPoint = string.IsNullOrEmpty(_spawnPoint.Infiltration) ? "MissingInfiltration" : _spawnPoint.Infiltration;
    }

    public async Task WaitForHeadlessInit(float timeBeforeDeployLocal)
    {
        if (_fikaGame is not AbstractGame abstractGame)
        {
            throw new NullReferenceException("AbstractGame was missing");
        }

        var server = FikaSingleton<FikaServer>.Instance;
        server.HostReady = true;
        Logger.LogInfo($"[Headless] Stage: host ready flag set (timeBeforeDeployLocal={timeBeforeDeployLocal})");

        var startTime = DateTimeExtensions.UtcNow.AddSeconds((double)timeBeforeDeployLocal);
        GameTime = startTime.ToManaged();
        server.GameStartTime = GameTime;
        var waitTimer = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (abstractGame.GameTimer != null)
                SessionTime = NativeNullable<Il2CppSystem.TimeSpan>.Read(abstractGame.GameTimer,
                    "<SessionTime>k__BackingField").ToManaged();
            if (SessionTime.HasValue)
                break;
            if (waitTimer.Elapsed >= TimeSpan.FromSeconds(30))
            {
                server.HostReady = false;
                throw new TimeoutException("Headless raid timer SessionTime did not become available within 30 seconds");
            }
            await Task.Yield();
        }

        InformationPacket packet = new()
        {
            RaidStarted = RaidStarted,
            ReadyPlayers = server.ReadyClients,
            HostReady = server.HostReady,
            GameTime = GameTime.Value,
            SessionTime = SessionTime.Value,
            GameDateTime = GameDateTime
        };

        server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        Logger.LogInfo($"[Headless] Stage: first host information packet sent (sessionTime={SessionTime.Value}, gameTime={GameTime.Value})");
        LootData = null;

    }

    public override void InitializeTransitSystem(GameWorld gameWorld, GlobalConfiguration instance, Profile profile, LocalRaidSettings localRaidSettings, LocationSettings.Location location)
    {
        bool transitActive;
        if (instance == null)
        {
            transitActive = false;
        }
        else
        {
            var transitSettings = instance.transitSettings;
            transitActive = transitSettings != null && transitSettings.active;
        }
        if (transitActive)
        {
            gameWorld.TransitController = new FikaHeadlessTransitController(instance.transitSettings, location.transitParameters, localRaidSettings);
        }
        else
        {
            Logger.LogInfo("Transits are disabled");
            TransitController.DisableTransitPoints();
        }
    }

    private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<LocationSettings.Location.TransitParameters> TransitParameters()
    {
        return Location.transitParameters ?? new(0);
    }

    private Il2CppSystem.Collections.Generic.Dictionary<int, LocationSettings.Location.TransitParameters> ActiveTransitPoints(EFT.TransitController transitController)
    {
        var result = new Il2CppSystem.Collections.Generic.Dictionary<int, LocationSettings.Location.TransitParameters>();
        foreach (var parameters in TransitParameters())
        {
            if (parameters.active && transitController.pointsById.ContainsKey(parameters.id))
                result[parameters.id] = parameters;
        }
        return result;
    }

    private Il2CppSystem.Collections.Generic.Dictionary<int, bool> CompletedQuestRequirements(Player player)
    {
        var result = new Il2CppSystem.Collections.Generic.Dictionary<int, bool>();
        foreach (var parameters in TransitParameters())
        {
            if (parameters.active && !string.IsNullOrEmpty(parameters.completedQuestId))
            {
                result[parameters.id] = new TransitCompletedQuestRequirement(parameters.completedQuestId).Met(player);
            }
        }

        return result;
    }
}

