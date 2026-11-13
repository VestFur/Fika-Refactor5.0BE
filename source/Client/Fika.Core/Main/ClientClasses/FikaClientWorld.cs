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
        NativeBaseCalls.Call75(this);
    }

    public void Update()
    {
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
        if (lootItems == null || LootSyncPackets.Count == 0) return;

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
            }
        }
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
