using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Comfort.Common;
using Diz.Jobs;
using EFT;
using EFT.InventoryLogic;
using EFT.SynchronizableObjects;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Main.Utils;

/// <summary>负责一场战局中的异步世界生成，包括先于其资源到达的更新。</summary>
internal sealed class WorldSyncSpawnQueue(FikaClientWorld owner)
{
    private readonly HashSet<(SynchronizableObjectType, int)> _pending = [];
    private readonly Dictionary<(SynchronizableObjectType, int), SynchronizableObjectPacket> _updates = [];
    private readonly HashSet<(SynchronizableObjectType, int)> _removed = [];
    internal bool HasPending => _pending.Count > 0;
    internal void Clear()
    {
        _pending.Clear();
        _updates.Clear();
        _removed.Clear();
        Failure = null;
    }
    internal string Failure { get; private set; }

    private bool IsCurrent => owner != null && !owner.IsDisposed && Singleton<GameWorld>.Instantiated &&
        Singleton<GameWorld>.Instance.World?.Pointer == owner.Pointer;

    internal static SynchronizableObject Find(SynchronizableObjectLogicProcessor processor, SynchronizableObjectType type, int id)
    {
        if (processor == null) return null;
        foreach (var obj in processor.GetSynchronizableObjects())
            if (obj.Type == type && obj.ObjectId == id) return obj;
        return null;
    }

    internal void Enqueue(ISubPacket packet, SynchronizableObjectType type, int id)
    {
        var key = (type, id);
        if (!IsCurrent || !_pending.Add(key)) return;
        // 显式的可靠生成可以复用原生按字节大小的对象 id。
        // 保留在待处理生成期间收到的移除事件。完成之后新的可靠生成
        // 可能复用该 id，因此丢弃上一代记录。
        _removed.Remove(key);
        if (_updates.TryGetValue(key, out var prior) && prior.Outdated) _updates.Remove(key);
        MainThreadDispatcher.RunAsync(async () =>
        {
            try
            {
                Item root = packet switch
                {
                    SpawnSyncObjectSubPackets.SpawnAirdrop airdrop => airdrop.AirdropItem,
                    SpawnSyncObjectSubPackets.SpawnTripwire tripwire => Singleton<ItemFactory>.Instance.CreateItem(tripwire.GrenadeId, tripwire.GrenadeTemplate, null),
                    _ => null
                };
                if (root == null) throw new InvalidOperationException($"Missing item for {type} {id}");
                var resources = new Il2CppSystem.Collections.Generic.List<ResourceKey>();
                var paths = new HashSet<string>();
                foreach (var item in root.GetAllItems())
                    foreach (var resource in item.Template.AllResources)
                        if (paths.Add(resource.path)) resources.Add(resource);
                if (resources.Count > 0)
                    await Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid,
                        ObjectsFactory.AssemblyType.Online, resources, JobYieldPriority.General, null,
                        new Il2CppSystem.Threading.CancellationToken()).AsManaged().WaitAsync(TimeSpan.FromMinutes(2));

                // 世界处理器/容器可能在数据包之后才完成初始化。
                for (var attempt = 0; attempt < 40 && IsCurrent && !_removed.Contains(key); attempt++)
                {
                    var complete = packet switch
                    {
                        SpawnSyncObjectSubPackets.SpawnAirdrop airdrop => airdrop.TrySpawn(),
                        SpawnSyncObjectSubPackets.SpawnTripwire tripwire => tripwire.TrySpawn(root.TryCast<ThrowWeap>()),
                        _ => true
                    };
                    if (complete) return;
                    await Task.Delay(250);
                }
                if (IsCurrent && !_removed.Contains(key))
                    throw new InvalidOperationException($"World object {type} {id} did not become ready for initialization");
            }
            catch (Exception ex)
            {
                if (IsCurrent)
                {
                    Failure = $"World spawn {type}/{id} failed: {ex.Message}";
                    FikaGlobals.LogError($"{Failure}\n{ex}");
                }
            }
            finally { _pending.Remove(key); }
        }).Forget();
    }

    internal void Process(List<SynchronizableObjectPacket> incoming, ClientSynchronizableObjectLogicProcessor processor)
    {
        foreach (var packet in incoming)
        {
            var key = (packet.ObjectType, packet.ObjectId);
            if (_removed.Contains(key) && packet.ObjectType != SynchronizableObjectType.AirPlane) continue;
            if (_updates.TryGetValue(key, out var previous) && (previous.Outdated ||
                previous.ObjectType == SynchronizableObjectType.Tripwire &&
                previous.PacketData.TripwireDataPacket.State == ETripwireState.Exploded)) continue;
            _updates[key] = packet;
        }
        incoming.Clear();
        var ready = new Il2CppSystem.Collections.Generic.List<SynchronizableObjectPacket>();
        var consumed = new List<(SynchronizableObjectType, int)>();
        foreach (var (key, packet) in _updates)
        {
            var obj = Find(processor, packet.ObjectType, packet.ObjectId);
            var removed = packet.Outdated || packet.ObjectType == SynchronizableObjectType.Tripwire &&
                packet.PacketData.TripwireDataPacket.State == ETripwireState.Exploded;
            if (removed)
            {
                if (packet.ObjectType != SynchronizableObjectType.AirPlane) _removed.Add(key);
                if (obj != null) ready.Add(packet);
                consumed.Add(key);
                continue;
            }
            if (packet.ObjectType != SynchronizableObjectType.AirPlane && (obj == null || _pending.Contains(key))) continue;
            ready.Add(packet);
            consumed.Add(key);
        }
        foreach (var key in consumed) _updates.Remove(key);
        // 在没有网络更新的帧上，处理器同样会推进原生对象逻辑。
        // 收到的数据包只被消费一次。
        processor.ProcessSyncObjectPackets(ready);
    }
}
