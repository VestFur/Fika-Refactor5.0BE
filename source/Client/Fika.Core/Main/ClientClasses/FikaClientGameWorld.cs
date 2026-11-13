using CommonAssets.Scripts.Game;
using EFT.Ballistics;
using EFT.BufferZone;
using JsonType;
using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.MovingPlatforms;
using EFT.SynchronizableObjects;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using HarmonyLib;
using Il2CppSystems.Effects;

namespace Fika.Core.Main.ClientClasses;

/// <summary>
/// <see cref="ClientLocalGameWorld"/> used in Fika for clients to override methods and logic
/// </summary>
public class FikaClientGameWorld : ClientLocalGameWorld
{
    public FikaClientGameWorld(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<ClientLocalGameWorld>(this);
    }


    public FikaClientGameWorld() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClientGameWorld>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public FikaClientWorld FikaClientWorld { get; private set; }
    public Il2CppSystem.Collections.Generic.Dictionary<int, Turnable> TurnableDict => Turnables;

    /// <summary>Guards against a virtual redispatch of the native base call (see InitLevel).</summary>
    private bool _insideBaseInitLevel;

    public static FikaClientGameWorld Create(GameObject gameObject, ObjectsFactory objectsFactory, EUpdateQueue updateQueue, Il2CppSystem.Nullable<MongoID> currentProfileId)
    {
        var gameWorld = gameObject.AddComponent<FikaClientGameWorld>();
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
        gameWorld.FikaClientWorld = FikaClientWorld.Create(gameWorld);
        FikaSingleton<FikaClientGameWorld>.Create(gameWorld);
        return gameWorld;
    }

    public override void ShotDelegate(Shot shotResult)
    {
        // The IL2CPP ballistic prewarmer can fail while constructing its
        // internal Shot before the callback is reached.  A real callback is
        // always supplied a managed Shot instance; keep this guard for the
        // defensive case without changing the native prewarmer path.
        if (shotResult == null)
        {
            return;
        }

        if (!shotResult.IsFlyingOutOfTime)
        {
            DamageInfo damageInfoStruct = new(EDamageType.Bullet, shotResult);
            ShotId shotIdStruct = new(shotResult.Ammo.Id, shotResult.FragmentIndex);
            var shotInfoClass = (shotResult.HittedBallisticCollider != null) ? shotResult.HittedBallisticCollider.ApplyHit(damageInfoStruct, shotIdStruct) : null;
            shotResult.AddClientHitPosition(shotInfoClass);
            var itemComponent = shotResult.Ammo.GetItemComponent<ExplosiveAmmoComponent>();
            if (itemComponent != null && shotResult.TimeSinceShot >= itemComponent.Template.FuzeArmTimeSec)
            {
                if (Singleton<Effects>.Instantiated)
                {
                    var explosionType = itemComponent.Template.ExplosionType;
                    if (!string.IsNullOrEmpty(explosionType) && shotResult.IsFirstHit)
                    {
                        Singleton<Effects>.Instance.EmitGrenade(explosionType, shotResult.HitPoint, shotResult.HitNormal, (float)(shotResult.IsForwardHit ? 1 : 0));
                    }
                    if (itemComponent.Template.ShowHitEffectOnExplode)
                    {
                        Singleton<Effects>.Instance.EffectsCommutator.PlayHitEffect(shotResult, shotInfoClass);
                    }
                }
                Grenade.Explosion(null, itemComponent, shotResult.HitPoint,
                    shotResult.Player.iPlayer.Id, SharedBallisticsCalculator,
                    shotResult.Weapon, shotResult.HitNormal * 0.08f, false);
                return;
            }
            if (Singleton<Effects>.Instantiated)
            {
                Singleton<Effects>.Instance.EffectsCommutator.PlayHitEffect(shotResult, shotInfoClass);
            }
        }
    }

    public override GrenadeFactory CreateGrenadeFactory()
    {
        return new FikaClientGrenadeFactory();
    }

    public override Il2CppSystem.Threading.Tasks.Task InitLevel(ItemFactory itemFactory, ObjectsFactoryConfig config,
        bool loadBundlesAndCreatePools = true, Il2CppSystem.Collections.Generic.List<ResourceKey> resources = null,
        Il2CppSystem.IProgress<InitLevelProgress> progress = null, Il2CppSystem.Threading.CancellationToken ct = default)
    {
        return NativeTask.FromManaged(MainThreadDispatcher.RunAsync(async () =>
        {
            // Same window as the host side: a failure of the base call aborts the map load
            // before LocalGameCreate and used to leave no trace of its own.
            if (_insideBaseInitLevel)
            {
                var recursion = new InvalidOperationException("InitLevel re-entered while the native base call was still in flight; "
                    + "the generated wrapper of this interop build dispatches virtually after all.");
                
                throw recursion;
            }

            
            _insideBaseInitLevel = true;
            try
            {
                // (analysis/transit-reload-oom) Directly before the native level load is the only point at
                // which the replaced raid's assets can still be released: the previous world is gone (its
                // assets are unreachable, so the pass can free them) and this world's own bundles are not
                // loaded yet (so the pass cannot free anything the raid being started needs). Without it the
                // second map of a transit was loaded on top of the first one and the process died with the
                // Unity allocation failure of LogOutput.log:4880.
                await FikaRaidMemory.ReleaseWorldSwapAsync("new map: before the native level load");
                
                // The alignment anchor of a transit reload (analysis/transit-reload-sync): this machine
                // ended its raid as soon as the host's landing arrived, so it reaches this point ~45 s
                // before the host reaches its own level creation. Waiting here - after the released world
                // swap and directly before the level is created - starts both machines' level load in the
                // same second, and it returns immediately for every raid start that is not a transit reload.
                await TransitReloadGate.WaitForHostReloadStartAsync("the level of the joined raid");
                
                await NativeBaseCalls.Call72(this, itemFactory, config, loadBundlesAndCreatePools, resources, progress, ct).AsManaged();
                
                
                Fika.Core.Main.Components.RaidInteractionSync.LevelReady(this);
                
            }
            catch (Exception exception)
            {
                
                throw;
            }
            finally
            {
                _insideBaseInitLevel = false;
            }
        }));
    }

    /// <summary>
    /// Keep SPT 5's native corpse reconstruction path for late-join and
    /// reconnect loot. Invoke the declared ClientGameWorld implementation so
    /// it registers inventory, dogtag, pose and customization with the world.
    /// </summary>
    public override void SpawnLootCorpse(JsonCorpse lootItem)
    {
        if (lootItem == null)
        {
            FikaGlobals.LogWarning("SpawnLootCorpse received a null JsonCorpse descriptor");
            return;
        }

        NativeBaseCalls.CallClientGameWorldSpawnLootCorpse(this, lootItem);
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
    }

    public override void AfterPlayerTick(float dt)
    {
        // (analysis/cutscene-rig-play) The engine half of the cutscene chain. This override used to swallow the
        // native per-frame step on every Fika raid: the native world class is the only per-frame caller the
        // cutscene client controller has besides the bridge, and the round-17 solo log shows the consequence -
        // "gate:ticks ... client engine=0 bridge=820" while the scene of the map stood still at 0.0 s of 29.2 s
        // and its rig stayed switched off. The switch forwards the call to the native base implementation and
        // observes what it ticks (see CutsceneBoundary.NativeEngineTick); the native body is only entered when
        // CutsceneBoundary.ForwardAfterPlayerTick is on, so the ported behaviour is the default and one constant
        // switches it back.
        if (!Fika.Core.Main.Components.CutsceneBoundary.ForwardAfterPlayerTick) return;
        Fika.Core.Main.Components.CutsceneBoundary.NativeEngineTick("client", () =>
            Fika.Core.Main.Utils.NativeBaseInvoker
                .Get<System.Action<float>>(typeof(ClientLocalGameWorld), "AfterPlayerTick", new[] { typeof(float) })(dt));
    }

    public override void UpdateSynchronizableObjectLogic(float dt)
    {
        // Do nothing
    }

    public override void InitAirdrop(string lootTemplateId = null, bool takeNearbyPoint = false, Vector3 position = default)
    {
        // Do nothing
    }

    public override SynchronizableObjectLogicProcessor SyncObjectProcessorFactory()
    {
        ClientSynchronizableObjectLogicProcessor = new ClientSynchronizableObjectLogicProcessor
        {
            TripwireManager = new(Singleton<GameWorld>.Instance)
        };
        return ClientSynchronizableObjectLogicProcessor;
    }

    public override void Dispose()
    {
        Fika.Core.Main.Components.RaidInteractionSync.Release(this);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call6(this);
        FikaSingleton<FikaClientGameWorld>.Release(this);
        NetManagerUtils.DestroyNetManager(false);
        List<SynchronizableObject> syncObjects = [.. SynchronizableObjectLogicProcessor.GetSynchronizableObjects()];
        for (var i = 0; i < syncObjects.Count; i++)
        {
            // One broken synchronizable object (the client airdrop throws a
            // NullReferenceException from ClientAirDrop.ReturnToPool) must not abort
            // the rest of the world teardown, which leaves the raid half disposed and
            // the game unresponsive afterwards.
            var syncObject = syncObjects[i];
            try
            {
                syncObject.remove_OnUpdateRequired((System.Action<SynchronizableObject>)SynchronizableObjectLogicProcessor.UpdateRequiredHandler);
                syncObject.Logic.ReturnToPool();
                syncObject.ReturnToPool();
            }
            catch (Exception exception)
            {
                FikaGlobals.LogWarning($"FikaClientGameWorld.Dispose: sync object {syncObject?.GetIl2CppType().Name} release failed: {exception.Message}");
            }
        }
    }

    public override void PlantTripwire(Item item, int playerRaidId, Vector3 fromPosition, Vector3 toPosition)
    {
        // Do nothing
    }

    public override void TriggerTripwire(TripwireSynchronizableObject tripwire)
    {
        // Do nothing
    }

    public override void DeActivateTripwire(TripwireSynchronizableObject tripwire)
    {
        // Do nothing
    }

}
