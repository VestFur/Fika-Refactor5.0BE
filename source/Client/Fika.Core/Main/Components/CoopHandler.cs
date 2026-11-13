using EFT.Communications;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BepInEx.Logging;
using Comfort.Common;
using Diz.Jobs;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.ObservedClasses;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;

namespace Fika.Core.Main.Components;

/// <summary>
/// CoopHandler handles most of the spawning logic in the raid, e.g. other players or AI. It also handles the extraction of the local player.
/// </summary>
public class CoopHandler : MonoBehaviour
{
    public CoopHandler(System.IntPtr pointer) : base(pointer) { }


    public CoopHandler() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<CoopHandler>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    #region Fields/Properties
    /// <summary>
    /// Reference to the local <see cref="CoopGame"/> instance
    /// </summary>
    public IFikaGame LocalGameInstance { get; internal set; }
    /// <summary>
    /// The ID of the raid session
    /// </summary>
    public string ServerId { get; internal set; }
    /// <summary>
    /// Reference to the local <see cref="FikaPlayer"/> player
    /// </summary>
    public FikaPlayer MyPlayer { get; internal set; }
    /// <summary>
    /// If the <see cref="CoopHandler"/> should sync and spawn profiles during <see cref="Update"/>
    /// </summary>
    public bool ShouldSync { get; set; }
    /// <summary>
    /// Dictionary of key = <see cref="FikaPlayer.NetId"/>, value = <see cref="FikaPlayer"/>
    /// </summary>
    public Dictionary<int, FikaPlayer> Players { get; internal set; }
    /// <summary>
    /// All human players in the form of <see cref="FikaPlayer"/>
    /// </summary>
    public List<FikaPlayer> HumanPlayers { get; internal set; }
    /// <summary>
    /// The amount of human players
    /// </summary>
    public int AmountOfHumans
    {
        get
        {
            return HumanPlayers.Count;
        }
    }
    /// <summary>
    /// List of <see cref="FikaPlayer.NetId"/>s that have extracted
    /// </summary>
    public List<int> ExtractedPlayers { get; internal set; }
    /// <summary>
    /// Returns the current <see cref="EQuitState"/>
    /// </summary>
    public EQuitState QuitState
    {
        get
        {
            // error?
            if (LocalGameInstance == null || MyPlayer == null)
            {
                return EQuitState.None;
            }

            // are we alive
            if (!MyPlayer.HealthController.IsAlive)
            {
                return EQuitState.Dead;
            }

            // have we extracted
            if (LocalGameInstance.ExtractedPlayers.Contains(MyPlayer.NetId))
            {
                return EQuitState.Extracted;
            }

            return EQuitState.None;
        }
    }

    private ManualLogSource _logger;
    /// <summary>
    /// List of all queued players by <see cref="FikaPlayer.NetId"/>
    /// </summary>
    private List<int> _queuedPlayers;
    private readonly Dictionary<int, SpawnObject> _pendingPlayerStates = [];
    /// <summary>
    /// Queue of <see cref="SpawnObject"/> containing all players to spawn
    /// </summary>
    private Queue<SpawnObject> _spawnQueue;
    private bool _isClient;
    private float _charSyncCounter;
    private bool _requestQuitGame;
    #endregion

    public static bool TryGetCoopHandler(out CoopHandler coopHandler)
    {
        coopHandler = null;
        var networkManager = FikaSingleton<IFikaNetworkManager>.Instance;
        if (networkManager != null)
        {
            coopHandler = networkManager.CoopHandler;
            return true;
        }

        return false;
    }

    public static string GetServerId()
    {
        var networkManager = FikaSingleton<IFikaNetworkManager>.Instance;
        if (networkManager != null && networkManager.CoopHandler != null)
        {
            return networkManager.CoopHandler.ServerId;
        }

        return FikaBackendUtils.GroupId;
    }

    public void CleanUpForTransit()
    {
        ShouldSync = false;
        Players.Clear();
        HumanPlayers.Clear();
        ExtractedPlayers.Clear();
        _queuedPlayers.Clear();
        _pendingPlayerStates.Clear();
        _spawnQueue.Clear();
        LocalGameInstance = null;
        _requestQuitGame = false;
        if (_isClient)
        {
            FikaSingleton<FikaClient>.Instance.FikaClientWorld = null;
        }
    }

    /// <summary>
    /// Checks if all human players are alive/downed
    /// </summary>
    /// <returns><see langword="true"/> if everyone is dead; otherwise <see langword="false"/></returns>
    public bool AreAllHumanPlayersDead()
    {
        var deadPlayers = 0;
        for (var i = 0; i < HumanPlayers.Count; i++)
        {
            var player = HumanPlayers[i];
            if (player.IsYourPlayer)
            {
                continue;
            }

            if (!player.HealthController.IsAlive || player.Downed)
            {
                deadPlayers++;
            }
        }

        return deadPlayers >= (AmountOfHumans - 1);
    }

    protected void Awake()
    {
        _logger = Logger.CreateLogSource("CoopHandler");
        _spawnQueue = new(50);
        _queuedPlayers = [];
        Players = [];
        HumanPlayers = [];
        ExtractedPlayers = [];
        ShouldSync = false;
        _charSyncCounter = 0f;
        _isClient = FikaBackendUtils.IsClient;
    }

    protected void Update()
    {
        if (LocalGameInstance == null)
        {
            return;
        }

        if (ShouldSync)
        {
            if (_spawnQueue.Count > 0)
            {
                var spawn = _spawnQueue.Dequeue();
                _logger.LogInfo($"Spawning queued player: netId={spawn.NetId}, profile={spawn.Profile?.ProfileId}, isAI={spawn.IsAI}");
                MainThreadDispatcher.RunAsync(() => SpawnPlayer(spawn).AsTask()).Forget();
            }

            if (!_isClient)
            {
                foreach (var human in HumanPlayers)
                {
                    if (human.TryCast<ObservedPlayer>() is not { } observed || !observed.Downed || observed.HasDied ||
                        observed.CaptureRevivalState().BleedoutRemaining != 0) continue;
                    var death = new EFT.HealthSystem.SyncHealthPacket
                    {
                        SyncType = EFT.HealthSystem.SyncHealthPacket.ESyncType.IsAlive
                    };
                    var data = death.Data;
                    var alive = data.IsAlive;
                    alive.IsAlive = false;
                    alive.DamageType = EDamageType.Undefined;
                    data.IsAlive = alive;
                    death.Data = data;
                    observed.SetupCorpseSyncPacket(death);
                    observed.NetworkHealthController.IsAlive = true;
                    observed.NetworkHealthController.HandleSyncPacket(death);
                }
                _charSyncCounter += Time.unscaledDeltaTime;
                var waitTime = LocalGameInstance.GameController.GameInstance.Status == GameStatus.Started ? 15 : 5;
                if (_charSyncCounter > waitTime)
                {
                    _charSyncCounter = 0f;
                    if (Players == null)
                    {
                        return;
                    }

                    SyncPlayersWithClients();
                }
            }
        }

        FikaFinalMissionDirector.Tick();
        ProcessQuitting();
    }

    private void SyncPlayersWithClients()
    {
        FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.CharacterSync,
            CharacterSyncPacket.FromValue(Players), true);
    }

    protected void OnDestroy()
    {
        _pendingPlayerStates.Clear();
        Players.Clear();
        HumanPlayers.Clear();
    }

    public void ReInitInteractables()
    {
        Singleton<GameWorld>.Instance.World.RegisterNetworkInteractionObjects(null);
    }

    /// <summary>
    /// This handles the ways of exiting the active game session
    /// </summary>
    private void ProcessQuitting()
    {
        if (FikaPlugin.Instance.Settings.ExtractKey.Value.IsDown())
        {
            RequestQuit();
        }
    }

    /// <summary>Requests extraction using the same state and peer checks as the configured hotkey.</summary>
    public void RequestQuit()
    {
        if (FikaFinalMissionDirector.DeferStop) return;
        var quitState = QuitState;
        if (quitState == EQuitState.None || _requestQuitGame)
        {
            return;
        }

        ConsoleScreen.Log("Attempting to extract!");
        _logger.LogInfo("Attempting to extract!");

        _requestQuitGame = true;
        var fikaGame = LocalGameInstance;

        var isPlayerAlive = MyPlayer.ActiveHealthController.IsAlive;
        var exitLocation = isPlayerAlive ? fikaGame.ExitLocation : null;

        // client logic
        if (_isClient)
        {
            fikaGame.Stop(MyPlayer.ProfileId, fikaGame.ExitStatus, exitLocation, 0);
            return;
        }

        // host logic
        var server = FikaSingleton<FikaServer>.Instance;
        var peers = server.NetServer.ConnectedPeersCount;

        if (fikaGame.ExitStatus == ExitStatus.Transit && HumanPlayers.Count <= 1)
        {
            fikaGame.Stop(MyPlayer.ProfileId, fikaGame.ExitStatus, exitLocation, 0);
            return;
        }

        if (peers > 0)
        {
            NotificationManager.DisplayWarningNotification(LocaleUtils.HOST_CANNOT_EXTRACT.Localized());
            _requestQuitGame = false;
            return;
        }

        var recentDisconnect = server.TimeSinceLastPeerDisconnected > DateTime.Now.AddSeconds(-5);
        if (server.HasHadPeer && recentDisconnect)
        {
            NotificationManager.DisplayWarningNotification(LocaleUtils.HOST_WAIT_5_SECONDS.Localized());
            _requestQuitGame = false;
            return;
        }

        fikaGame.Stop(MyPlayer.ProfileId, fikaGame.ExitStatus, exitLocation, 0);
    }

    private async ValueTask SpawnPlayer(SpawnObject spawnObject)
    {
        try
        {
            await SpawnPlayerCore(spawnObject);
            _logger.LogInfo($"Spawned queued player: netId={spawnObject.NetId}, profile={spawnObject.Profile?.ProfileId}");
        }
        catch (Exception exception)
        {
            _logger.LogError($"Failed to spawn queued player netId={spawnObject.NetId}: {exception}");
            throw;
        }
        finally
        {
            if (_pendingPlayerStates.TryGetValue(spawnObject.NetId, out var current) && ReferenceEquals(current, spawnObject))
            {
                _queuedPlayers.Remove(spawnObject.NetId);
                _pendingPlayerStates.Remove(spawnObject.NetId);
            }
        }
    }

    /// <summary>
    /// How long a player spawn waits for its prefab pools once the raid is live.
    /// </summary>
    private const int PrefabLoadTimeoutMs = 120000;

    internal bool QueueSpawningPlayerState(Fika.Core.Networking.Packets.Player.Common.CommonPlayerPacket packet)
    {
        if (!_pendingPlayerStates.TryGetValue(packet.NetId, out var spawn)) return false;
        // Update timers at receive time so bundle loading cannot extend bleedout
        // or rescue pauses. Their visual state is applied after player creation.
        if (packet.SubPacket is DownedSyncPacket downed)
        {
            if (!spawn.DeathPending) spawn.RevivalClock.Restore(downed.State);
            return true;
        }
        if (packet.SubPacket is RevivingPlayerPacket reviving)
        {
            if (!spawn.DeathPending) spawn.RevivalClock.SetReviving(reviving.Reviving, reviving.Nickname, FikaPlugin.Instance.Settings.ReviveConfig.ReviveTime);
            return true;
        }
        if (packet.SubPacket is RevivedPlayerPacket)
        {
            var state = spawn.RevivalClock.Capture();
            if (!spawn.DeathPending && state.Downed && state.BleedoutRemaining != 0) spawn.RevivalClock.SetDowned(false, 0);
            return true;
        }
        if (packet.SubPacket is HealthSyncPacket health &&
            health.Packet.SyncType == EFT.HealthSystem.SyncHealthPacket.ESyncType.IsAlive && !health.Packet.Data.IsAlive.IsAlive)
            spawn.DeathPending = true;
        // Packet instances are pooled. Retain serialized bytes while native
        // prefab/hands creation yields, never references to recycled subpackets.
        var writer = new NetDataWriter();
        packet.Serialize(writer);
        spawn.PendingPackets.Add(writer.CopyData());
        return true;
    }

    private async ValueTask SpawnPlayerCore(SpawnObject spawnObject)
    {
        var spawningWorld = Singleton<GameWorld>.Instance;
        var spawningGame = LocalGameInstance;
        if (spawnObject.Profile == null)
        {
            _logger.LogError("SpawnPlayer: Profile was null!");
            _queuedPlayers.Remove(spawnObject.NetId);
            return;
        }

        foreach (IPlayer player in Singleton<GameWorld>.Instance.AllPlayersEverExisted)
        {
            if (player.ProfileId == spawnObject.Profile.ProfileId)
            {
                return;
            }
        }

        var allPrefabPaths = new Il2CppSystem.Collections.Generic.List<ResourceKey>();
        foreach (var path in spawnObject.Profile.GetAllPrefabPaths(!spawnObject.IsAI))
            allPrefabPaths.Add(path);
        if (allPrefabPaths.Count == 0)
        {
            _logger.LogError($"SpawnPlayer::{spawnObject.Profile.Info.Nickname}::PrefabPaths are empty!");
            return;
        }

        await JobScheduler.Yield().AsManaged();
        // A player that walks into a raid which already runs cannot wait for the
        // configured (low) load priority: the raid never goes idle again, so the
        // bundle job would never be picked up and the spawn would hang forever.
        var raidAlreadyRunning = FikaSingleton<IFikaGame>.Instance?.GameController?.RaidStarted == true;
        // Inside a live raid the "Local" assembly type never finishes its pool job,
        // which is why a late joiner's prefabs (for example the player scav body,
        // wild_body.bundle) stayed unloaded and the spawn failed with
        // "wild_body.bundle is not loaded". Mid-raid loads use the online assembly
        // type and the general priority, exactly like the world object spawner.
        var assemblyType = raidAlreadyRunning
            ? ObjectsFactory.AssemblyType.Online
            : ObjectsFactory.AssemblyType.Local;
        var priority = raidAlreadyRunning
            ? JobYieldPriority.General
            : FikaPlugin.Instance.Settings.LoadPriority.Value.ToLoadPriorty();
        _logger.LogInfo($"SpawnPlayerCore:: loading {allPrefabPaths.Count} prefabs for netId={spawnObject.NetId} " +
            $"(raidStarted={raidAlreadyRunning})");
        try
        {
            var loadTask = Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid,
                assemblyType, allPrefabPaths, priority,
                null, new Il2CppSystem.Threading.CancellationToken()).AsManaged();
            // Never wait for the pool job without a bound. A pending job would keep
            // the whole spawn - and with it peer.Player binding - from completing,
            // and every gameplay packet of that client (inventory operations,
            // pickups, proceeds) would then be rejected by the host, which the
            // player experiences as the client freezing the moment it picks
            // something up. If the load does not finish in time the spawn continues
            // with whatever is already resident; a failure is logged and retried
            // when the client announces its character again.
            var finished = await Task.WhenAny(loadTask, Task.Delay(PrefabLoadTimeoutMs));
            if (finished != loadTask)
            {
                _logger.LogWarning($"SpawnPlayerCore:: the prefab load for netId={spawnObject.NetId} did not " +
                    $"finish within {PrefabLoadTimeoutMs} ms, continuing with the resident prefabs");
            }
            else
            {
                await loadTask;
            }

            _logger.LogInfo($"SpawnPlayerCore:: prefabs ready for netId={spawnObject.NetId}");
        }
        catch (OperationCanceledException)
        {
            _logger.LogError($"SpawnPlayer::{spawnObject.Profile.Info.Nickname}::Load Cancelled");
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError($"SpawnPlayer::{spawnObject.Profile.Info.Nickname}::Load Failed: {ex}");
            return;
        }
        await JobScheduler.Yield().AsManaged();

        if (spawningWorld == null || Singleton<GameWorld>.Instance?.Pointer != spawningWorld.Pointer ||
            LocalGameInstance != spawningGame) return;
        _logger.LogInfo($"SpawnPlayerCore:: creating the observed player for netId={spawnObject.NetId}");
        var otherPlayer = await SpawnObservedPlayer(spawnObject);
        _logger.LogInfo($"SpawnPlayerCore:: observed player for netId={spawnObject.NetId} is {(otherPlayer == null ? "null" : "ready")}");
        if (otherPlayer == null) return;

        var revival = spawnObject.RevivalClock.Capture();
        if (revival.Downed)
        {
            otherPlayer.ToggleDowned(true);
            if (revival.RevivePauseRemaining > 0)
                otherPlayer.ToggleRevive(true, revival.ReviverNickname);
        }
        else if (!revival.IsAlive)
        {
            otherPlayer.OnDead(EDamageType.Undefined);
            otherPlayer.NetworkHealthController.IsAlive = false;
        }
        otherPlayer.RevivalClock.Restore(revival);
        for (var i = 0; i < spawnObject.PendingPackets.Count; i++)
        {
            var pending = new Fika.Core.Networking.Packets.Player.Common.CommonPlayerPacket();
            try
            {
                pending.Deserialize(new NetDataReader(spawnObject.PendingPackets[i]));
                pending.Execute(otherPlayer);
            }
            finally { pending.Clear(); }
        }
        spawnObject.PendingPackets.Clear();

        if (FikaBackendUtils.IsServer)
        {
            if (LocalGameInstance != null)
            {
                var botController = (FikaSingleton<IFikaGame>.Instance.GameController as HostGameController)?.BotsController;
                if (botController != null)
                {
                    // Start Coroutine as botController might need a while to start sometimes...
#if DEBUG
                    _logger.LogInfo("Starting AddClientToBotEnemies routine.");
#endif
                    Fika.Core.Il2CppCoroutineExtensions.StartCoroutine(this, AddClientToBotEnemies(botController, otherPlayer));
                }
                else
                {
                    _logger.LogError("botController was null when trying to add player to enemies!");
                }
            }
            else
            {
                _logger.LogError("LocalGameInstance was null when trying to add player to enemies!");
            }
        }

        _queuedPlayers.Remove(spawnObject.NetId);
    }

    public void QueueProfile(Profile profile, byte[] healthByteArray, Vector3 position, int netId, bool isAlive, bool isAI, MongoID firstId, ushort firstOperationId, bool isZombie, MongoID? itemId,
        EHandsControllerType controllerType = EHandsControllerType.None, RevivalState? revivalState = null, bool isStationary = false)
    {
        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld == null)
        {
            _logger.LogInfo("QueueProfile::GameWorld was null, not queuing");
            return;
        }

        foreach (IPlayer player in gameWorld.AllPlayersEverExisted)
        {
            if (player.ProfileId == profile.ProfileId)
            {
                _logger.LogInfo($"QueueProfile::Skipped {profile.ProfileId} (netId {netId}), the profile already exists in the world");
                return;
            }
        }

        if (_queuedPlayers.Contains(netId) && !Players.ContainsKey(netId))
        {
            _logger.LogInfo($"QueueProfile::Skipped netId {netId} for {profile.ProfileId}, it is already queued");
            return;
        }

        if (!_queuedPlayers.Contains(netId))
        {
            _queuedPlayers.Add(netId);
        }
#if DEBUG
        _logger.LogInfo($"Queueing profile: {profile.Nickname}, {profile.ProfileId}");
#endif
        SpawnObject spawnObject = new(profile, position, isAlive, isAI, netId, firstId, firstOperationId, isZombie);
        spawnObject.RevivalClock.Restore(revivalState ?? new RevivalState { IsAlive = isAlive, BleedoutRemaining = -1f });
        spawnObject.IsStationary = isStationary;
        _pendingPlayerStates[netId] = spawnObject;
        if (controllerType != EHandsControllerType.None)
        {
            spawnObject.ControllerType = controllerType;
            if (itemId is not null)
            {
                spawnObject.ItemId = itemId;
            }
        }
        if (healthByteArray != null)
        {
            spawnObject.HealthBytes = healthByteArray;
        }
        _spawnQueue.Enqueue(spawnObject);
    }

    private async ValueTask<ObservedPlayer> SpawnObservedPlayer(SpawnObject spawnObject)
    {
        var isAi = spawnObject.IsAI;
        var profile = spawnObject.Profile;
        var position = spawnObject.Position;
        var netId = spawnObject.NetId;
        var firstId = spawnObject.CurrentId;
        var firstOperationId = spawnObject.FirstOperationId;
        var healthBytes = spawnObject.HealthBytes;
        var isZombie = spawnObject.IsZombie;

        // Handle null bytes on players
        if (!isAi && (spawnObject.HealthBytes == null || spawnObject.HealthBytes.Length == 0))
        {
            healthBytes = profile.Health.SerializeHealthInfo();
        }

        var gameWorld = Singleton<GameWorld>.Instance;

        if (isAi) // spawn underground until everyone has loaded the AI
        {
            position = new(0f, -5000f, 0f);
        }

        profile.SetSpawnedInSession((profile.Info.Side == EPlayerSide.Savage || FikaPlugin.Instance.Settings.PMCFoundInRaid) && isAi);
        if (profile.Info.Side != EPlayerSide.Savage)
        {
            var backpack = profile.Inventory.Equipment.GetSlot(EquipmentSlot.Backpack).ContainedItem;
            if (backpack != null)
            {
                foreach (var item in backpack.GetAllItems())
                {
                    if (item != backpack)
                    {
                        item.SpawnedInSession = true;
                    }
                }
            }

            var armband = profile.Inventory.Equipment.GetSlot(EquipmentSlot.ArmBand).ContainedItem;
            if (armband != null)
            {
                armband.SpawnedInSession = false;
            }

            var melee = profile.Inventory.Equipment.GetSlot(EquipmentSlot.Scabbard).ContainedItem;
            if (melee != null)
            {
                melee.SpawnedInSession = false;
            }
        }

        var otherPlayer = await ObservedPlayer.CreateObservedPlayer(gameWorld, netId, position, Quaternion.identity, "Player",
            isAi ? "Bot_" : $"Player_{profile.Nickname}_", EPointOfView.ThirdPerson, profile, healthBytes, isAi,
            EUpdateQueue.Update, Player.EUpdateMode.Manual, Player.EUpdateMode.Auto,
            AppEnvironment.Config.CharacterController.ObservedPlayerMode,
            FikaGlobals.GetOtherPlayerSensitivity, FikaGlobals.GetOtherPlayerSensitivity, ObservedViewFilter.Default,
            firstId, firstOperationId, isZombie);

        if (otherPlayer == null)
        {
            return null;
        }

        FikaSingleton<IFikaNetworkManager>.Instance.ObservedPlayers.Add(otherPlayer);
#if DEBUG
        _logger.LogInfo($"SpawnObservedPlayer: {profile.GetCorrectedNickname()} spawning with NetId {netId} at [{position:F1}]");
#endif

        if (!Players.ContainsKey(netId))
        {
            Players.Add(netId, otherPlayer);
        }
        else
        {
            _logger.LogError($"Trying to add {otherPlayer.Profile.Nickname} to list of players but it was already there!");
        }

        if (!isAi)
        {
            HumanPlayers.Add(otherPlayer);
        }

        foreach (var player in Players.Values)
        {
            if (player.TryCast<ObservedPlayer>() == null)
            {
                continue;
            }

            var playerCollider = otherPlayer.GetCharacterControllerCommon().GetCollider();
            var otherCollider = player.GetCharacterControllerCommon().GetCollider();

            if (playerCollider != null && otherCollider != null)
            {
                PhysicsExtensions.IgnoreCollision(playerCollider, otherCollider);
            }
        }

        otherPlayer.InitObservedPlayer();

        var controllerType = spawnObject.ControllerType;
        var itemId = spawnObject.ItemId;
        var isStationary = spawnObject.IsStationary;
        if (controllerType != EHandsControllerType.None)
        {
            if (controllerType != EHandsControllerType.Empty && (itemId is null || itemId.Equals(new MongoID())))
            {
                _logger.LogError($"CreateLocalPlayer: ControllerType was not Empty but itemId was default! ControllerType: {controllerType}");
            }
            else
            {
                otherPlayer.SpawnHandsController(controllerType, itemId, isStationary);
            }
        }
        return otherPlayer;
    }

    private IEnumerator AddClientToBotEnemies(BotsController botController, LocalPlayer playerToAdd)
    {
        var coopGame = LocalGameInstance;
        _logger.LogInfo($"AddClientToBotEnemies: {playerToAdd.Profile.GetCorrectedNickname()}");
        while (coopGame.GameController.GameInstance.Status != GameStatus.Running && !botController.IsEnable)
        {
            yield return null;
        }

        while (botController.BotSpawner == null)
        {
            yield return null;
        }

#if DEBUG
        _logger.LogInfo($"Adding Client {playerToAdd.Profile.GetCorrectedNickname()} to enemy list");
#endif
        botController.AddActivePLayer(playerToAdd);

        var found = false;

        for (var i = 0; i < botController.BotSpawner.PlayersCount; i++)
        {
            if (botController.BotSpawner.GetPlayer(i) == playerToAdd)
            {
                found = true;
                break;
            }
        }

        if (found)
        {
#if DEBUG
            _logger.LogInfo($"Verified that {playerToAdd.Profile.GetCorrectedNickname()} was added to the enemy list.");
#endif
            yield break;
        }

        _logger.LogError($"Failed to add {playerToAdd.Profile.GetCorrectedNickname()} to the enemy list.");
    }

    public void CheckIds(List<int> playerIds, List<int> missingIds)
    {
        foreach (var netId in playerIds)
        {
            if (!_queuedPlayers.Contains(netId) && !Players.ContainsKey(netId))
            {
                missingIds.Add(netId);
            }
        }
    }

    /// <summary>
    /// The state your character or game is in to Quit.
    /// </summary>
    public enum EQuitState
    {
        None = -1,
        Dead,
        Extracted
    }

    public sealed class SpawnObject(Profile profile, Vector3 position, bool isAlive, bool isAI, int netId, MongoID currentId, ushort firstOperationId, bool isZombie)
    {
        public Profile Profile = profile;
        public Vector3 Position = position;
        public bool IsAlive = isAlive;
        public bool IsAI = isAI;
        public int NetId = netId;
        public MongoID CurrentId = currentId;
        public ushort FirstOperationId = firstOperationId;
        public EHandsControllerType ControllerType;
        public MongoID ItemId;
        public bool IsStationary;
        public byte[] HealthBytes;
        public bool IsZombie = isZombie;
        internal readonly RevivalStateClock RevivalClock = new();
        internal readonly List<byte[]> PendingPackets = [];
        internal bool DeathPending;
    }
}

