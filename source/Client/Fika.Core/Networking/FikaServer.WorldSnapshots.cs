using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Airdrop;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.SynchronizableObjects;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Networking;

public sealed partial class FikaServer
{
    private static SpawnSyncObjectPacket CaptureAirdrop(AirdropSynchronizableObject obj, Item item = null)
    {
        var container = obj.GetComponentInChildren<LootableContainer>(true);
        item = container?.ItemOwner?.RootItem ?? item;
        if (container == null || item == null) return null;
        if (string.IsNullOrEmpty(container.Id)) container.Id = item.Id;
        Singleton<GameWorld>.Instance.RegisterWorldInteractionObject(container);
        if (container.NetId == 0) return null;
        return new SpawnSyncObjectPacket
        {
            ObjectType = SynchronizableObjectType.AirDrop,
            SubPacket = new SpawnSyncObjectSubPackets.SpawnAirdrop
            {
                ObjectId = obj.ObjectId,
                IsStatic = obj.IsStatic,
                Position = obj.transform.position,
                Rotation = obj.transform.rotation,
                AirdropType = obj.AirdropType,
                AirdropItem = item,
                ContainerId = new MongoID(container.Id),
                NetId = container.NetId
            }
        };
    }

    private async Task SendAirdropWhenReady(EAirdropType type, Item item, int id)
    {
        var world = Singleton<GameWorld>.Instance;
        for (var attempt = 0; attempt < 40 && world != null && Singleton<GameWorld>.Instance?.Pointer == world.Pointer; attempt++)
        {
            var obj = WorldSyncSpawnQueue.Find(world.SynchronizableObjectLogicProcessor, SynchronizableObjectType.AirDrop, id)
                ?.TryCast<AirdropSynchronizableObject>();
            var packet = obj == null ? null : CaptureAirdrop(obj, item);
            if (packet != null)
            {
                SendData(ref packet, DeliveryMethod.ReliableOrdered);
                return;
            }
            await Task.Delay(250);
        }
        if (world != null && Singleton<GameWorld>.Instance?.Pointer == world.Pointer)
            _logger.LogError($"Airdrop {id} ({type}) never acquired a registered loot container");
    }

    private async Task SendWorldObjectsForReconnect(NetPeer peer, string profileId)
    {
        var world = Singleton<GameWorld>.Instance;
        if (!IsCurrentReconnectWorld(world, peer)) return;
        var processor = world.SynchronizableObjectLogicProcessor;
        var objects = new List<SynchronizableObject>();
        foreach (var obj in processor.GetSynchronizableObjects()) objects.Add(obj);
        var updates = new List<SynchronizableObjectPacket>();
        foreach (var obj in objects)
        {
            if (!IsCurrentReconnectWorld(world, peer)) return;
            if (obj?.TryCast<TripwireSynchronizableObject>() is { } tripwire)
            {
                if (tripwire.TripwireState == ETripwireState.Exploded) continue;
                var spawn = new SpawnSyncObjectPacket
                {
                    ObjectType = SynchronizableObjectType.Tripwire,
                    SubPacket = new SpawnSyncObjectSubPackets.SpawnTripwire
                    {
                        ObjectId = tripwire.ObjectId,
                        IsStatic = tripwire.IsStatic,
                        GrenadeTemplate = tripwire.GrenadeTemplateId,
                        GrenadeId = tripwire.GrenadeItemId,
                        PlayerRaidId = tripwire.PlacerRaidId,
                        Position = tripwire.FromPosition,
                        ToPosition = tripwire.ToPosition,
                        Rotation = tripwire.transform.rotation,
                        State = tripwire.TripwireState
                    }
                };
                SendDataToPeer(ref spawn, DeliveryMethod.ReliableOrdered, peer);
            }
            else if (obj?.TryCast<AirdropSynchronizableObject>() is { } airdrop)
            {
                var id = airdrop.ObjectId;
                SpawnSyncObjectPacket spawn = null;
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    if (!IsCurrentReconnectWorld(world, peer)) return;
                    if (airdrop == null || airdrop.ObjectId != id || WorldSyncSpawnQueue.Find(processor, SynchronizableObjectType.AirDrop, id) == null) break;
                    spawn = CaptureAirdrop(airdrop);
                    if (spawn != null) break;
                    await Task.Delay(250);
                }
                if (spawn == null)
                {
                    if (WorldSyncSpawnQueue.Find(processor, SynchronizableObjectType.AirDrop, id) == null) continue;
                    throw new InvalidOperationException($"Reconnect could not capture loot for airdrop {id}");
                }
                if (!IsCurrentReconnectWorld(world, peer)) return;
                SendDataToPeer(ref spawn, DeliveryMethod.ReliableOrdered, peer);
                var source = airdrop.Logic?.TryCast<ServerAirDrop>()?._offlineSyncPacket;
                var update = new SynchronizableObjectPacket
                {
                    ObjectType = SynchronizableObjectType.AirDrop,
                    ObjectId = id,
                    Position = airdrop.transform.position,
                    Rotation = airdrop.transform.rotation.eulerAngles,
                    IsStatic = airdrop.IsStatic,
                    PacketData = source?.PacketData ?? new SynchronizableObjectPacket.Data()
                };
                var data = update.PacketData;
                var dropData = data.AirdropDataPacket;
                dropData.AirdropType = airdrop.AirdropType;
                dropData.UniqueId = airdrop.UniqueId;
                if (airdrop.IsStatic) dropData.FallingStage = EAirdropFallingStage.Landed;
                data.AirdropDataPacket = dropData;
                update.PacketData = data;
                updates.Add(update);
            }
            else if (obj?.TryCast<AirplaneSynchronizableObject>() is { } plane &&
                plane.Logic?.TryCast<ServerPlane>() is { } planeLogic)
            {
                var source = planeLogic._offlineSyncPacket;
                // Copy the boxed native value rather than mutating the server's packet.
                updates.Add(new SynchronizableObjectPacket
                {
                    ObjectType = SynchronizableObjectType.AirPlane,
                    ObjectId = plane.ObjectId,
                    Position = plane.transform.position,
                    Rotation = plane.transform.rotation.eulerAngles,
                    IsStatic = plane.IsStatic,
                    Outdated = source.Outdated,
                    PacketData = source.PacketData
                });
            }
        }
        if (updates.Count > 0 && IsCurrentReconnectWorld(world, peer))
        {
            var packet = new ReconnectPacket { Type = ReconnectPacket.EReconnectDataType.WorldObjects,
                ProfileId = profileId, WorldObjectUpdates = updates };
            SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
        }
    }
}
