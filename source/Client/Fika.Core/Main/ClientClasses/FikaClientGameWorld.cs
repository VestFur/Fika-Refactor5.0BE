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
            await NativeBaseCalls.Call72(this, itemFactory, config, loadBundlesAndCreatePools, resources, progress, ct).AsManaged();
            Fika.Core.Main.Components.RaidInteractionSync.LevelReady(this);
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
        // Do nothing
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
                syncObject.remove_OnUpdateRequired((Il2CppSystem.Action<SynchronizableObject>)SynchronizableObjectLogicProcessor.UpdateRequiredHandler);
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

    public Item CreateReconnectQuestItem(JsonLootItem lootItem, bool initial, Player questPlayer = null, MovingPlatform platform = null)
    {
        var item = (questPlayer == null) ? lootItem.Item : lootItem.Item.CloneItem(null);
        new ItemController(item, item.Id, item.ShortName, true, EOwnerType.Profile);
        var gameObject = Singleton<ObjectsFactory>.Instance.CreateLootPrefab(item, ECameraType.Default, null);
        gameObject.SetActive(true);
        if (platform != null)
        {
            gameObject.transform.position = platform.transform.TransformPoint(lootItem.Position);
        }
        else
        {
            gameObject.transform.position = lootItem.Position;
            gameObject.transform.rotation = Quaternion.Euler(lootItem.Rotation);
        }
        var lootItem2 = lootItem.useGravity
            ? CreateLootWithRigidbody(gameObject, item, item.ShortName, lootItem.randomRotation, lootItem.ValidProfiles, out var _, false, true, 0f)
            : CreateStaticLoot(gameObject, item, item.ShortName, lootItem.randomRotation, lootItem.ValidProfiles, null, lootItem.Shift);
        if (platform != null)
        {
            lootItem2.Board(platform);
            gameObject.transform.localRotation = Quaternion.Euler(lootItem.Rotation);
            return item;
        }
        var component = gameObject.GetComponent<PreviewPivot>();
        if (component != null && component.SpawnPosition != Vector3.zero)
        {
            var gameObject2 = new GameObject("Weapon spawn root");
            gameObject2.transform.position = lootItem.Position;
            gameObject2.transform.rotation = Quaternion.Euler(lootItem.Rotation);
            var transform = lootItem2.transform;
            transform.SetParent(gameObject2.transform);
            transform.localPosition = (initial ? (-component.SpawnPosition) : Vector3.zero);
            if (lootItem.randomRotation)
            {
                transform.rotation = Quaternion.Euler(new Vector3(0f, (float)UnityEngine.Random.Range(0, 360), 0f));
            }
            transform.localScale = Vector3.one;
        }

        return item;
    }
}
