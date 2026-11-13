using CommonAssets.Scripts.Game;
using EFT.Airdrop;
using EFT.BufferZone;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.SynchronizableObjects;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.World;
using HarmonyLib;

namespace Fika.Core.Main.HostClasses;

/// <summary>
/// <see cref="ClientLocalGameWorld"/> used in Fika for hosts to override methods and logic
/// </summary>
public class FikaHostGameWorld : ClientLocalGameWorld
{
    public FikaHostGameWorld(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<ClientLocalGameWorld>(this);
    }


    public FikaHostGameWorld() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaHostGameWorld>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private FikaServer Server
    {
        get
        {
            return FikaSingleton<FikaServer>.Instance;
        }
    }

    public FikaHostWorld FikaHostWorld { get; private set; }
    public Il2CppSystem.Collections.Generic.Dictionary<int, Turnable> TurnablesDict => Turnables;

    public static FikaHostGameWorld Create(GameObject gameObject, ObjectsFactory objectsFactory, EUpdateQueue updateQueue, Il2CppSystem.Nullable<MongoID> currentProfileId)
    {
        var gameWorld = gameObject.AddComponent<FikaHostGameWorld>();
        gameWorld.ObjectsFactory = objectsFactory;
        gameWorld._updateQueue = updateQueue;
        gameWorld.SpeakerManager = gameObject.AddComponent<SpeakerManager>();
        gameWorld.ExfiltrationController = new ExfiltrationController();
        gameWorld.BufferZoneController = new BufferZoneController();
        gameWorld.RaidDialogEntryController = new RaidDialogEntryController();
        Fika.Core.Main.Components.RaidInteractionSync.Bind(gameWorld);
        gameWorld.CurrentProfileId = currentProfileId;
        gameWorld.UnityTickListener = GameWorldUnityTickListener.Create(gameObject, gameWorld);
        gameWorld.AudioSourceCulling = gameObject.GetOrAddComponent<AudioSourceCulling>();
        gameWorld.FikaHostWorld = FikaHostWorld.Create(gameWorld);
        gameWorld.FikaHostWorld.RegisterNetworkInteractionObjects();
        FikaSingleton<FikaHostGameWorld>.Create(gameWorld);
        return gameWorld;
    }

    public override void PlayerTick(float dt)
    {
        for (var i = AllAlivePlayersList.Count - 1; i >= 0; i--)
        {
            var player = AllAlivePlayersList[i];
            try
            {
                player.UpdateTick();
            }
            catch (Exception ex)
            {
                FikaGlobals.LogError($"[{player.FullIdInfo}] tick operation exception: {ex}");
            }
        }
        ClientSynchronizableObjectLogicProcessor.RemoveNonActiveAndStaticObjects();
        ClientSynchronizableObjectLogicProcessor.ManualUpdate(dt);
    }

    public override void ChangeLampState(Turnable turnable, Turnable.EState state)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call71(this, turnable, state);
        Server.SendGenericPacket(EGenericSubPacketType.SyncableItem,
            SyncableItemPacket.FromValue(turnable.NetId, state), true);
    }

    public override void AfterPlayerTick(float dt)
    {
        // (analysis/cutscene-rig-play) Host half of the same repair as the client world: the native per-frame
        // step of ClientLocalGameWorld is what drives the cutscene controllers (and everything else the port
        // made Fika own) on a normal raid. Off by default - one constant switches it on - and it never throws
        // into the raid tick.
        if (!Fika.Core.Main.Components.CutsceneBoundary.ForwardAfterPlayerTick) return;
        Fika.Core.Main.Components.CutsceneBoundary.NativeEngineTick("host", () =>
            Fika.Core.Main.Utils.NativeBaseInvoker
                .Get<System.Action<float>>(typeof(ClientLocalGameWorld), "AfterPlayerTick", new[] { typeof(float) })(dt));
    }

    public override GrenadeFactory CreateGrenadeFactory()
    {
        return new HostGrenadeFactory();
    }

    public override Il2CppSystem.Threading.Tasks.Task InitLevel(ItemFactory itemFactory, ObjectsFactoryConfig config, bool loadBundlesAndCreatePools = true,
        Il2CppSystem.Collections.Generic.List<ResourceKey> resources = null, Il2CppSystem.IProgress<InitLevelProgress> progress = null,
        Il2CppSystem.Threading.CancellationToken ct = default)
    {
        return NativeTask.FromManaged(MainThreadDispatcher.RunAsync(() =>
            InitLevelManaged(itemFactory, config, loadBundlesAndCreatePools, resources, progress, ct)));
    }

    /// <summary>
    /// Level initialisation of a host raid: this is the step behind the loading screen progress
    /// bar, and it runs *before* <c>TarkovApplication.LocalGameCreate</c> ever sees the raid.
    ///
    /// It is instrumented because it used to be the silent dead end of the whole raid start: a
    /// failure of the base call here aborted the map load - no loading screen dismissal, no
    /// LocalGameCreate, no Fika log line - and the client dropped back to the main menu with the
    /// progress bar at 0 % (2026-10-09 22:58:53.574, NotSupportedException from the native base
    /// call machinery).
    /// </summary>
    private async Task InitLevelManaged(ItemFactory itemFactory, ObjectsFactoryConfig config, bool loadBundlesAndCreatePools,
        Il2CppSystem.Collections.Generic.List<ResourceKey> resources, Il2CppSystem.IProgress<InitLevelProgress> progress,
        Il2CppSystem.Threading.CancellationToken ct)
    {
        if (_insideBaseInitLevel)
        {
            // The native base call would have redispatched virtually into this override: fail
            // loudly instead of re-entering until the loading screen is stuck forever.
            var recursion = new InvalidOperationException("InitLevel re-entered while the native base call was still in flight; "
                + "the generated wrapper of this interop build dispatches virtually after all.");
            
            throw recursion;
        }

        
        _insideBaseInitLevel = true;
        try
        {
            // The alignment anchor of a transit reload (analysis/transit-reload-sync): this is the moment
            // this host starts creating the next map, and the clients that already ended their own raid are
            // waiting for exactly this signal before they create their level. A host that never sends it is
            // not waited for - the clients release themselves through the bounded fallbacks of
            // TransitReloadGate - and the host itself never waits for a peer.
            if (FikaBackendUtils.IsServer && FikaBackendUtils.IsTransit)
            {
                TransitReloadGate.AnnounceHostReloadStart(Server, LocationId ?? "unknown");
            }

            // (analysis/transit-reload-oom) Symmetrical to the client: directly before the native level
            // load the replaced raid's world is already disposed and this raid's bundles are not loaded
            // yet, so this is the one point where the previous map's assets can be handed back to the
            // system without touching anything this raid needs. Only a continuing transit schedules it
            // (FikaRaidMemory.NoteWorldSwap is gated on FikaBackendUtils.IsTransit), it runs at most once
            // per swap, and FikaRaidMemory.ReleaseWorldSwapAsync never faults - the level load below must
            // never be able to fail because of the release.
            try
            {
                await FikaRaidMemory.ReleaseWorldSwapAsync("new map: before the native level load (host)");
            }
            catch (Exception releaseException)
            {
                            }
            
            
            await global::Fika.Core.Main.Utils.NativeBaseCalls.Call72(this, itemFactory, config, loadBundlesAndCreatePools, resources, progress, ct).AsManaged();
            
            
            Fika.Core.Main.Components.RaidInteractionSync.LevelReady(this);
            System.Action<MineDirectional> mineExplode = (System.Action<MineDirectional>)OnMineExplode;
            MineManager.OnExplosion += mineExplode;
            _mineExplodeHandler = mineExplode;
            
        }
        catch (Exception exception)
        {
            
            throw;
        }
        finally
        {
            _insideBaseInitLevel = false;
        }
    }

    /// <summary>Guards against a virtual redispatch of the native base call (see InitLevelManaged).</summary>
    private bool _insideBaseInitLevel;

    /// <summary>
    /// Triggers when a <see cref="MineDirectional"/> explodes
    /// </summary>
    /// <param name="directional"></param>
    private void OnMineExplode(MineDirectional directional)
    {
        if (directional == null || directional.gameObject == null || !directional.gameObject.active)
        {
            return;
        }

        Server.SendGenericPacket(EGenericSubPacketType.Mine,
            MineEvent.FromValue(directional.transform.position), true);
    }

    public override void Dispose()
    {
        Fika.Core.Main.Components.RaidInteractionSync.Release(this);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call6(this);
        FikaSingleton<FikaHostGameWorld>.Release(this);
        if (_mineExplodeHandler != null)
            MineManager.OnExplosion -= _mineExplodeHandler;
        NetManagerUtils.DestroyNetManager(true);
    }

    private System.Action<MineDirectional> _mineExplodeHandler;

    public override void InitAirdrop(string lootTemplateId = null, bool takeNearbyPoint = false, Vector3 position = default)
    {
        var gameObject = TakeAirdropPoint(takeNearbyPoint, position);
        if (gameObject == null)
        {
            FikaGlobals.LogError("There are no airdrop points here!");
            return;
        }
        var synchronizableObject = ClientSynchronizableObjectLogicProcessor.TakeFromPool(SynchronizableObjectType.AirPlane);
        if (synchronizableObject.Logic?.TryCast<ClientAirPlane>() is { } airplaneLogicClass && airplaneLogicClass.offlineMode)
        {
            airplaneLogicClass.OfflineServerLogic.ContainerTemplateId = lootTemplateId;
        }
        ClientSynchronizableObjectLogicProcessor.InitSyncObject(synchronizableObject, gameObject.transform.position, Vector3.forward, -1);
    }

    public override SynchronizableObjectLogicProcessor SyncObjectProcessorFactory()
    {
        ClientSynchronizableObjectLogicProcessor = new ClientSynchronizableObjectLogicProcessor
        {
            TripwireManager = new(Singleton<GameWorld>.Instance)
        };
        return ClientSynchronizableObjectLogicProcessor;
    }

    public override void PlantTripwire(Item item, int playerRaidId, Vector3 fromPosition, Vector3 toPosition)
    {
        if (item?.TryCast<ThrowWeap>() is not { } grenadeClass)
        {
            return;
        }

        if (SynchronizableObjectLogicProcessor.TripwireManager == null)
        {
            FikaGlobals.LogError("TripwireManager was null! Creating new...");
            SynchronizableObjectLogicProcessor.TripwireManager = new TripwireManager(this);
        }

        var tripwireSynchronizableObject = SynchronizableObjectLogicProcessor.TakeFromPool(SynchronizableObjectType.Tripwire).Cast<TripwireSynchronizableObject>();
        tripwireSynchronizableObject.transform.SetPositionAndRotation(fromPosition, Quaternion.identity);
        SynchronizableObjectLogicProcessor.InitSyncObject(tripwireSynchronizableObject, fromPosition, Vector3.forward, -1);
        tripwireSynchronizableObject.SetupGrenade(grenadeClass, playerRaidId, fromPosition, toPosition);
        SynchronizableObjectLogicProcessor.TripwireManager.AddTripwire(tripwireSynchronizableObject);
        var vector = (fromPosition + toPosition) * 0.5f;
        Singleton<GlobalEventDispatcher>.Instance.PlantTripwire(tripwireSynchronizableObject, vector);

        SpawnSyncObjectPacket packet = new()
        {
            ObjectType = SynchronizableObjectType.Tripwire,
            SubPacket = new SpawnSyncObjectSubPackets.SpawnTripwire()
            {
                ObjectId = tripwireSynchronizableObject.ObjectId,
                IsStatic = tripwireSynchronizableObject.IsStatic,
                GrenadeTemplate = grenadeClass.TemplateId,
                GrenadeId = grenadeClass.Id,
                PlayerRaidId = playerRaidId,
                Position = fromPosition,
                ToPosition = toPosition,
                Rotation = tripwireSynchronizableObject.transform.rotation,
                State = tripwireSynchronizableObject.TripwireState
            }
        };

        Server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    public override void TriggerTripwire(TripwireSynchronizableObject tripwire)
    {
        SynchronizableObjectPacket packet = new()
        {
            ObjectType = SynchronizableObjectType.Tripwire,
            ObjectId = tripwire.ObjectId,
            PacketData = new()
            {
                TripwireDataPacket = new()
                {
                    State = ETripwireState.Active
                }
            },
            Position = tripwire.transform.position,
            Rotation = tripwire.transform.rotation.eulerAngles,
            IsActive = true
        };
        FikaHostWorld.WorldPacket.SyncObjectPackets.Add(packet);
        FikaHostWorld.SetCritical();

        global::Fika.Core.Main.Utils.NativeBaseCalls.Call73(this, tripwire);
    }

    public override void DeActivateTripwire(TripwireSynchronizableObject tripwire)
    {
        SynchronizableObjectPacket packet = new()
        {
            ObjectType = SynchronizableObjectType.Tripwire,
            ObjectId = tripwire.ObjectId,
            PacketData = new()
            {
                TripwireDataPacket = new()
                {
                    State = ETripwireState.Inert
                }
            },
            Position = tripwire.transform.position,
            Rotation = tripwire.transform.rotation.eulerAngles,
            IsActive = true
        };
        FikaHostWorld.WorldPacket.SyncObjectPackets.Add(packet);
        FikaHostWorld.SetCritical();

        global::Fika.Core.Main.Utils.NativeBaseCalls.Call74(this, tripwire);
    }
}
