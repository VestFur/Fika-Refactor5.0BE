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
/// <see cref="ClientLocalGameWorld"/>，在 Fika 中供主机覆写方法与逻辑
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
        // (analysis/cutscene-rig-play) 与客户端世界同一修复的主机那一半：在正常 raid 中，
        // 驱动过场动画 controller（以及本移植让 Fika 接管的其余一切）的正是 ClientLocalGameWorld 的
        // 原生逐帧步骤。默认关闭 —— 由一个常量打开 —— 且它绝不会向 raid tick
        // 抛出异常。
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
    /// 主机 raid 的关卡初始化：这就是加载界面进度条背后的那一步，
    /// 它运行在 <c>TarkovApplication.LocalGameCreate</c> 真正看到这场 raid 之 *前*。
    ///
    /// 之所以给它加上埋点，是因为它过去是整个 raid 启动过程中沉默的死胡同：
    /// 这里基类调用的失败会中断地图加载 —— 加载界面不消失、没有
    /// LocalGameCreate、也没有任何 Fika 日志行 —— 而客户端会退回主菜单，
    /// 进度条停在 0 %（2026-10-09 22:58:53.574，来自原生基类调用
    /// 机制抛出的 NotSupportedException）。
    /// </summary>
    private async Task InitLevelManaged(ItemFactory itemFactory, ObjectsFactoryConfig config, bool loadBundlesAndCreatePools,
        Il2CppSystem.Collections.Generic.List<ResourceKey> resources, Il2CppSystem.IProgress<InitLevelProgress> progress,
        Il2CppSystem.Threading.CancellationToken ct)
    {
        if (_insideBaseInitLevel)
        {
            // 这个原生基类调用本会以虚派发方式重新进入本覆写：因此改为响亮地失败，
            // 而不是反复重入直到加载界面永远卡住。
            var recursion = new InvalidOperationException("InitLevel re-entered while the native base call was still in flight; "
                + "the generated wrapper of this interop build dispatches virtually after all.");
            
            throw recursion;
        }

        
        _insideBaseInitLevel = true;
        try
        {
            // 中转重载的对齐锚点（analysis/transit-reload-sync）：此刻这台主机
            // 开始创建下一张地图，而已经结束自己 raid 的客户端正
            // 在等待的正是这个信号，然后它们才创建自己的关卡。从不发送它的主机
            // 不会被等待 —— 客户端会通过 TransitReloadGate 的有界兜底自我放行 ——
            // 而主机自己也从不等待任何对端。
            if (FikaBackendUtils.IsServer && FikaBackendUtils.IsTransit)
            {
                TransitReloadGate.AnnounceHostReloadStart(Server, LocationId ?? "unknown");
            }

            // (analysis/transit-reload-oom) 与客户端对称：紧接在原生关卡加载之前，
            // 被替换 raid 的世界已经释放，而本 raid 的 bundle 尚未加载，
            // 因此这是唯一能把上一张地图的资源交还系统、同时不触碰
            // 本 raid 所需任何东西的时刻。只有持续中的中转才会安排它
            // （FikaRaidMemory.NoteWorldSwap 以 FikaBackendUtils.IsTransit 为条件），每次世界切换最多运行一次，
            // 且 FikaRaidMemory.ReleaseWorldSwapAsync 从不置为故障 —— 下面的关卡加载
            // 绝不能因为这次释放而失败。
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

    /// <summary>防止对原生基类调用的虚重派发（见 InitLevelManaged）。</summary>
    private bool _insideBaseInitLevel;

    /// <summary>
    /// 当 <see cref="MineDirectional"/> 爆炸时触发
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
