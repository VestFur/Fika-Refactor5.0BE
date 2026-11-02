using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.World;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.ClientClasses;

/// <summary>
/// 客户端用于同步游戏逻辑的 <see cref="World"/>
/// </summary>
public class FikaClientWorld : World
{
    public FikaClientWorld(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<World>(this);
    }


    public FikaClientWorld() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClientWorld>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public List<EFT.LootSyncPacket> LootSyncPackets;
    public List<SynchronizableObjectPacket> SyncObjectPackets;
    public List<CorpseSyncPacket> RagdollPackets;
    public WorldPacket WorldPacket;
    internal WorldSyncSpawnQueue WorldSpawns { get; private set; }

    private FikaClientGameWorld _clientGameWorld;
    private FikaClient _client;
    private bool _hasCriticalData;
    private bool _disposed;
    internal bool IsDisposed => _disposed;

    /// <summary>
    /// 两个接收路径列表的上限，只有应用路径会收缩它们。两者都由
    /// FikaClient 的 world 数据包回调追加，且都会在下面每一帧被完整遍历，因此一个
    /// 战利品物品或尸体始终没有生成的包，否则会在整场 raid 的剩余时间里
    /// 被反复检查。上限与 FikaClient 的 MaxPendingWorldPackets 处于同一量级，远
    /// 高于一场 raid 会排入的数量，所以正常 raid 永远达不到它们。
    /// (analysis/midraid-clr-failfast/REPORT.md).
    /// </summary>
    internal const int MaxPendingLootSyncPackets = 256;
    internal const int MaxPendingRagdollPackets = 256;

    private bool _pendingTrimLogged;

    // [LootDiag] 计数器 (analysis/pickup-stuck-again)。这个列表是唯一能看到
    // 有多少远程战利品位置更新到达本客户端、有多少仍在等待自己的物品、
    // 以及上限是否曾丢弃过一条的地方，这正是「无法拿取地面战利品」
    // 报告区分失步与交互被拒所需要的信息。
    private int _lootDiagApplied;
    private int _lootDiagTrimmed;
    private int _lootDiagPeak;
    private float _nextLootDiagTime;

    public static FikaClientWorld Create(FikaClientGameWorld gameWorld)
    {
        var clientWorld = gameWorld.gameObject.AddComponent<FikaClientWorld>();
        clientWorld._clientGameWorld = gameWorld;
        clientWorld.WorldSpawns = new WorldSyncSpawnQueue(clientWorld);
        clientWorld.LootSyncPackets = new(8);
        clientWorld.SyncObjectPackets = new(16);
        clientWorld.RagdollPackets = new(8);
        clientWorld.WorldPacket = new()
        {
            ArtilleryPackets = new(8),
            SyncObjectPackets = new(8),
            GrenadePackets = new(8),
            LootSyncStructs = new(8),
            RagdollPackets = new(8)
        };
        clientWorld._client = FikaSingleton<FikaClient>.Instance;
        clientWorld._client.FikaClientWorld = clientWorld;
        // (analysis/transit-crash-unload) 从这里起一个新的 world 已经存在：WorldLifecycle 的
        // 拆除窗口结束，换图的 [LoadCost] 窗口以其汇总行关闭。
        WorldLifecycle.MarkWorldUp("FikaClientWorld.Create");
        // (analysis/cutscene-world-factory，已被 analysis/cutscene-native-registration 取代) 这两个
        // cutscene 控制器过去在这里创建，位于本移植对原生
        // GameWorld.Create<T> 方法体的替换实现内部。那个方法体从未创建它们 (4.0 EFT/GameWorld.cs:1111-1123) ——
        // 原生位置是 raid 启动协程，本移植用 CoopGame.InitPlayer 替换了它，现在它们在那里
        // 被创建并且在各自的每帧钩子上注册。此处调用只是回滚路径：
        return clientWorld;
    }

    public override void OnDestroy()
    {
        if (_disposed) return;
        _disposed = true;
        WorldSpawns?.Clear();
        LootSyncPackets?.Clear();
        SyncObjectPackets?.Clear();
        RagdollPackets?.Clear();
        WorldPacket?.Flush();
        if (ReferenceEquals(_client?.FikaClientWorld, this)) _client.FikaClientWorld = null;
        // 这个组件所属的 world 即将消失。它为它排队的一切都在此终结，因此
        // 被替换掉的 raid 的数据包绝不可能被应用到下一个 world (analysis/transit-crash-unload)。
        // 这些列表保持已分配状态（后续的处理逻辑必须找到空列表，而不是 null）；只清掉
        // 它们的内容以及 WorldSpawns 背后的原生对象队列。
        WorldSpawns?.Clear();
        NativeBaseCalls.Call75(this);
    }

    /// <summary>
    /// (analysis/perf-round2) world 组件自身的 Update 是帧耗时探针的一个槽位。
    /// 被测量的方法体是原方法，未做改动；探针关闭时包装层只是两次静态 bool 读取。
    /// 探针已关闭。
    /// </summary>
    public void Update()
    {
        
        try
        {
            UpdateMeasured();
        }
        finally
        {
            
        }
    }

    private void UpdateMeasured()
    {
        // 在 world 被使用之前就限界：这两个列表只有在数据包能被应用时才会
        // 收缩，而下面的遍历每一帧都会触及它们的每一个条目。
        TrimPending(LootSyncPackets, MaxPendingLootSyncPackets, "loot");
        TrimPending(RagdollPackets, MaxPendingRagdollPackets, "ragdoll");

        if (_clientGameWorld == null) return;

        // 战利品描述符在世界组件创建之后才经由网络到达。
        // 保留已排队的位置更新，直到该描述符生成了
        // 自己的原生 LootItem 集合。
        if (_clientGameWorld != null)
            UpdateLootItems(_clientGameWorld.LootItems);
        var processor = _clientGameWorld.ClientSynchronizableObjectLogicProcessor;
        if (processor != null) WorldSpawns.Process(SyncObjectPackets, processor);

        if (RagdollPackets.Count > 0)
        {
            var corpses = Singleton<GameWorld>.Instance?.ObservedPlayersCorpses;
            if (corpses != null)
            {
                for (var i = RagdollPackets.Count - 1; i >= 0; i--)
                {
                    var data = RagdollPackets[i];
                    if (!corpses.TryGetValue(data.Id, out var corpse) || corpse == null || !corpse.HasRagdoll)
                        continue;
                    corpse.ApplyNetPacket(data);
                    if (data.Done && data.TransformSyncs != null)
                        corpse.ForceApplyTransformSync(data.TransformSyncs);
                    RagdollPackets.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// 待处理列表超出上限后丢弃它最旧的条目，并只报告本 world 的第一次
    /// 裁剪。
    /// <para>
    /// 不会丢失 raid 仍会应用的内容：该列表每一帧都被遍历，因此一条在
    /// <paramref name="max"/> 个更新的到达之后仍未解析的条目，已经没有可被
    /// 应用的战利品物品或尸体 —— 它如今本就永远不会被应用，只是被保留着。
    /// </para>
    /// </summary>
    private int TrimPending<T>(List<T> pending, int max, string what)
    {
        var excess = pending.Count - max;
        if (excess <= 0) return 0;
        pending.RemoveRange(0, excess);
        if (what == "loot")
        {
            // 每次裁剪都计数。下面的一次性警告只说明上限曾被触达一次；
            // [LootDiag] 心跳携带累计总数，所以在战利品密集的地图上
            // 持续触发的上限无法隐藏。
            _lootDiagTrimmed += excess;
                    }
        if (!_pendingTrimLogged)
        {
            _pendingTrimLogged = true;
            FikaGlobals.LogWarning("[FikaClientWorld] the pending " + what + " list reached its cap of "
                + max + " entries: the oldest " + excess + " had no loot item or corpse to be applied to and were dropped");
        }

        return excess;
    }

    public void AddLootSyncStruct(EFT.LootSyncPacket syncStruct)
    {
        if (WorldPacket.LootSyncStructs.Count >= 8)
        {
            _client.SendReusable(WorldPacket,
                _hasCriticalData ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);

            _hasCriticalData = false;
        }

        WorldPacket.LootSyncStructs.Add(syncStruct);
    }

    /// <summary>
    /// 将当前的 <see cref="WorldPacket"/> 标记为关键
    /// </summary>
    internal void SetCritical()
    {
        _hasCriticalData = true;
    }

    protected void LateUpdate()
    {
        
        if (WorldPacket.HasData)
        {
            _client.SendReusable(WorldPacket,
                _hasCriticalData ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);

            _hasCriticalData = false;
        }

        
    }

    public void UpdateLootItems(DictionaryListHydra<int, LootItem> lootItems)
    {
        var applied = 0;
        if (lootItems != null && LootSyncPackets.Count > 0)
        {
            for (var i = LootSyncPackets.Count - 1; i >= 0; i--)
            {
                var lootSyncData = LootSyncPackets[i];
                // 值来自 IL2CPP 的 DictionaryListHydra，可能以 LootItem 基类
                // 包装的形式暴露。使用原生转换，以免在同步期间
                // 漏掉远程观察到的物品。
                if (lootItems.TryGetByKey(lootSyncData.Id, out var lootItem)
                    && lootItem?.TryCast<ObservedLootItem>() is { } observedLootItem)
                {
                    observedLootItem.ApplyNetPacket(lootSyncData);
                    LootSyncPackets.RemoveAt(i);
                    applied++;
                }
            }
        }

        DiagLootSync(applied);
    }

    /// <summary>
    /// 接收路径的周期性 [LootDiag] 心跳 (analysis/pickup-stuck-again)。
    /// 玩家无法拿取物品时待处理计数持续增长，就是失步的特征；
    /// 拾取被拒而待处理计数为 0 则是交互被拒，因此用一次 grep
    /// 就能区分这两种原因。
    /// </summary>
    private void DiagLootSync(int applied)
    {
        _lootDiagApplied += applied;
        if (LootSyncPackets.Count > _lootDiagPeak) _lootDiagPeak = LootSyncPackets.Count;
        if (_lootDiagApplied == 0 && _lootDiagTrimmed == 0 && LootSyncPackets.Count == 0) return;
        if (Time.time < _nextLootDiagTime) return;
        _nextLootDiagTime = Time.time + 10f;
            }

    /// <summary>
    /// 设置地图上所有的 <see cref="BorderZone"/>
    /// </summary>
    public override void SubscribeToBorderZones(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BorderZone> zones)
    {
        for (var i = 0; i < zones.Length; i++)
        {
            var borderZone = zones[i];
            borderZone.RemoveAuthority();
        }
    }
}
