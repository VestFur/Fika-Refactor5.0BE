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
/// <see cref="ClientLocalGameWorld"/>，在 Fika 中供客户端覆写方法与逻辑
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

    /// <summary>防止对原生基类调用的虚重派发（见 InitLevel）。</summary>
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
        // IL2CPP 弹道预热器在到达回调之前构造其内部 Shot
        // 时可能失败。真正的回调总会得到一个托管 Shot
        // 实例；保留这个防护仅用于防御性的
        // 情形，且不改变原生预热器路径。
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
            // 与主机侧相同的窗口：基类调用的失败会在 LocalGameCreate 之前中断地图加载，
            // 而它过去不会留下任何自身的痕迹。
            if (_insideBaseInitLevel)
            {
                var recursion = new InvalidOperationException("InitLevel re-entered while the native base call was still in flight; "
                    + "the generated wrapper of this interop build dispatches virtually after all.");
                
                throw recursion;
            }

            
            _insideBaseInitLevel = true;
            try
            {
                // (analysis/transit-reload-oom) 紧接在原生关卡加载之前的这一刻，是唯一还能释放
                // 被替换 raid 资源的时刻：上一个世界已经消失（它的资源不可达，因此这一遍
                // 可以释放它们），而这个世界自己的 bundle 尚未加载（因此这一遍不会释放
                // 正在启动的 raid 所需要的任何东西）。没有它时，一次中转的
                // 第二个地图会叠加在第一个地图之上加载，进程最终死于
                // LogOutput.log:4880 记录的 Unity 分配失败。
                await FikaRaidMemory.ReleaseWorldSwapAsync("new map: before the native level load");
                
                // 中转重载的对齐锚点（analysis/transit-reload-sync）：本机在主机落地消息一到达时
                // 就结束了自己的 raid，因此它比主机到达自己的关卡创建早约 45 秒
                // 就到达这里。在此等待 —— 在已释放的世界切换之后、紧接关卡加载之前 ——
                // 让两台机器的关卡加载在同一秒开始，而对每一次并非中转重载的 raid 启动，
                // 它会立即返回。
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
    /// 为迟加入与重连的战利品保留 SPT 5 的原生尸体重建路径。
    /// 调用已声明的 ClientGameWorld 实现，使
    /// 它把库存、狗牌、姿态与自定义信息注册到世界中。
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
        // (analysis/cutscene-rig-play) 过场动画链的引擎那一半。这个覆写过去在每一次 Fika raid 中都会吞掉
        // 原生逐帧步骤：原生世界类是该过场客户端 controller 除桥接之外唯一的逐帧调用者，
        // 而第 17 轮单人日志显示了后果 ——
        // "gate:ticks ... client engine=0 bridge=820"，同时该地图的场景停在 29.2 秒中的 0.0 秒
        // 且它的 rig 一直处于关闭状态。该开关把调用转发给原生基类实现，
        // 然后再切换回来。
    }

    public override void UpdateSynchronizableObjectLogic(float dt)
    {
        // 不做任何事
    }

    public override void InitAirdrop(string lootTemplateId = null, bool takeNearbyPoint = false, Vector3 position = default)
    {
        // 不做任何事
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
            // 一个损坏的可同步对象（客户端空投会在 ClientAirDrop.ReturnToPool 中抛出
            // NullReferenceException）不得中断世界拆解的其余部分，
            // 否则 raid 会处于半释放状态，之后游戏也会
            // 失去响应。
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
        // 不做任何事
    }

    public override void TriggerTripwire(TripwireSynchronizableObject tripwire)
    {
        // 不做任何事
    }

    public override void DeActivateTripwire(TripwireSynchronizableObject tripwire)
    {
        // 不做任何事
    }

}
