using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.World;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.ClientClasses;

/// <summary>
/// <see cref="World"/> used for the client to synchronize game logic
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
    /// Caps of the two receive-path lists that only the apply paths shrink. Both are appended by
    /// FikaClient's world-packet callback and both are walked in full on every frame below, so a
    /// packet whose loot item or corpse never materialises would otherwise be re-checked for the
    /// rest of the raid. The caps are the same order as FikaClient's MaxPendingWorldPackets, far
    /// above what a raid queues, so a normal raid never reaches them
    /// (analysis/midraid-clr-failfast/REPORT.md).
    /// </summary>
    internal const int MaxPendingLootSyncPackets = 256;
    internal const int MaxPendingRagdollPackets = 256;

    private bool _pendingTrimLogged;

    // [LootDiag] counters (analysis/pickup-stuck-again). This list is the only place that
    // sees how many remote loot position updates reached this client, how many are still
    // waiting for their item and whether the cap ever dropped one, which is what a "cannot
    // take ground loot" report needs to tell a desync apart from a refused interaction.
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
        // (analysis/transit-crash-unload) A fresh world exists from here on: the teardown window of
        // WorldLifecycle ends, the [LoadCost] window of the swap is closed with its summary line.
        WorldLifecycle.MarkWorldUp("FikaClientWorld.Create");
        // (analysis/cutscene-world-factory, superseded by analysis/cutscene-native-registration) The two
        // cutscene controllers used to be created here, inside the port's replacement of the native
        // GameWorld.Create<T> body. That body never created them (4.0 EFT/GameWorld.cs:1111-1123) - the native
        // position is the raid-start coroutine, which this port replaces with CoopGame.InitPlayer, and there
        // they are now created AND registered on their per-frame hook. This call is the rollback path only:
        // CutsceneBoundary.CreateControllerIfMissing == false switches it off, one const switches it back on.
        global::Fika.Core.Main.Components.CutsceneBoundary.EnsureControllers(gameWorld, "FikaClientWorld.Create");
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
        // The world this component belongs to is going away. Everything it queued for it dies here, so a
        // packet of the replaced raid can never be applied to the next world (analysis/transit-crash-unload).
        // The lists stay allocated (a later handler must find an empty list, not a null one); only their
        // content and the native-object queue behind WorldSpawns go.
        WorldSpawns?.Clear();
        NativeBaseCalls.Call75(this);
    }

    /// <summary>
    /// (analysis/perf-round2) The world component's own Update is one slot of the frame-cost probe. The
    /// measured body is the original method unchanged; the wrapper is two static bool reads while the
    /// probe is off.
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
        // Bounded before the world is used: these two lists only shrink when a packet could be
        // applied, and the walks below touch every entry of them on every frame.
        TrimPending(LootSyncPackets, MaxPendingLootSyncPackets, "loot");
        TrimPending(RagdollPackets, MaxPendingRagdollPackets, "ragdoll");

        if (_clientGameWorld == null) return;

        // Loot descriptors arrive over the network after the world component
        // is created. Keep queued position updates until the descriptor has
        // produced its native LootItem collection.
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
    /// Drops the oldest entries of a pending list once it is over its cap, and reports the first
    /// trim of this world once.
    /// <para>
    /// Nothing is lost that the raid would still have applied: the list is walked every frame, so
    /// an entry that is still unresolved after <paramref name="max"/> newer arrivals has no loot
    /// item or corpse to be applied to - it is already never applied today, only kept.
    /// </para>
    /// </summary>
    private int TrimPending<T>(List<T> pending, int max, string what)
    {
        var excess = pending.Count - max;
        if (excess <= 0) return 0;
        pending.RemoveRange(0, excess);
        if (what == "loot")
        {
            // Every trim is counted. The one-shot warning below only says that a cap was
            // reached once; the [LootDiag] heartbeat carries the running total, so a cap
            // that keeps firing in a loot-dense map cannot hide.
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
    /// Marks the current <see cref="WorldPacket"/> as critical
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
                // Values come from the IL2CPP DictionaryListHydra and may be
                // exposed as the LootItem base wrapper. Use the native cast so a
                // remote observed item is not missed during synchronization.
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
    /// Periodic [LootDiag] heartbeat for the receive path (analysis/pickup-stuck-again).
    /// A pending count that keeps growing while the player cannot take items is the desync
    /// signature; a pending count of 0 next to a refused pickup is an interaction refusal
    /// instead, so the two causes are told apart by one grep.
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
    /// Sets up all the <see cref="BorderZone"/>s on the map
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
