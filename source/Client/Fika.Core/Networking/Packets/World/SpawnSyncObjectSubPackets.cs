using System;
using Comfort.Common;
using EFT;
using EFT.Airdrop;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.SynchronizableObjects;
using Fika.Core.Main.Players;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Utils;

namespace Fika.Core.Networking.Packets.World;

public class SpawnSyncObjectSubPackets
{
    public class SpawnTripwire : ISubPacket
    {
        public int ObjectId;
        public bool IsStatic;
        public string GrenadeTemplate;
        public string GrenadeId;
        public int PlayerRaidId;
        public Vector3 ToPosition;
        public Vector3 Position;
        public Quaternion Rotation;
        public ETripwireState State = ETripwireState.Wait;

        public SpawnTripwire()
        {

        }

        public SpawnTripwire(NetDataReader reader)
        {
            ObjectId = reader.GetInt();
            IsStatic = reader.GetBool();
            GrenadeTemplate = reader.GetString();
            GrenadeId = reader.GetString();
            PlayerRaidId = reader.GetInt();
            Position = reader.GetUnmanaged<Vector3>();
            ToPosition = reader.GetUnmanaged<Vector3>();
            Rotation = reader.GetUnmanaged<Quaternion>();
            State = reader.GetEnum<ETripwireState>();
        }

        public void Execute(FikaPlayer player)
        {
            Singleton<GameWorld>.Instance.World.TryCast<FikaClientWorld>()?.WorldSpawns.Enqueue(this, SynchronizableObjectType.Tripwire, ObjectId);
        }

        internal bool TrySpawn(ThrowWeap grenadeClass)
        {
            var processor = Singleton<GameWorld>.Instance.SynchronizableObjectLogicProcessor;
            if (processor == null)
            {
                return false;
            }

            if (WorldSyncSpawnQueue.Find(processor, SynchronizableObjectType.Tripwire, ObjectId) != null) return true;
            if (State == ETripwireState.Exploded) return true;
            if (grenadeClass == null)
            {
                FikaGlobals.LogError("OnSpawnSyncObjectPacketReceived: Item with id " + GrenadeId + " is not a grenade!");
                throw new InvalidOperationException("Tripwire item was not a grenade");
            }

            var syncObject = processor.TakeFromPool(SynchronizableObjectType.Tripwire).Cast<TripwireSynchronizableObject>();
            try
            {
                syncObject.ObjectId = ObjectId;
                syncObject.IsStatic = IsStatic;
                syncObject.transform.SetPositionAndRotation(Position, Rotation);
                processor.InitSyncObject(syncObject, syncObject.transform.position, syncObject.transform.rotation.eulerAngles, syncObject.ObjectId);

                syncObject.SetupGrenade(grenadeClass, PlayerRaidId, Position, ToPosition);
                processor.TripwireManager ??= new TripwireManager(Singleton<GameWorld>.Instance);
                if (processor.TripwireManager.GetTripwireById(ObjectId) == null)
                    processor.TripwireManager.AddTripwire(syncObject);
                // 为迟到的加入者保留未激活/已触发状态，而不是重新把它设为待触发。
                if (State != ETripwireState.Wait && State != ETripwireState.None)
                {
                    var update = new SynchronizableObjectPacket
                    {
                        ObjectType = SynchronizableObjectType.Tripwire,
                        ObjectId = ObjectId,
                        Position = Position,
                        Rotation = Rotation.eulerAngles,
                        IsActive = true,
                        PacketData = new SynchronizableObjectPacket.Data { TripwireDataPacket = new TripwireDataPacket { State = State } }
                    };
                    syncObject.UpdateSyncObjectData(ref update);
                }
                return true;
            }
            catch
            {
                processor.TripwireManager?.RemoveTripwire(syncObject);
                processor.ReturnToPool(syncObject);
                throw;
            }
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ObjectId);
            writer.Put(IsStatic);
            writer.Put(GrenadeTemplate);
            writer.Put(GrenadeId);
            writer.Put(PlayerRaidId);
            writer.PutUnmanaged(Position);
            writer.PutUnmanaged(ToPosition);
            writer.PutUnmanaged(Rotation);
            writer.PutEnum(State);
        }
    }

    [Obsolete("Not used in client", true)]
    public class SpawnAirplane : ISubPacket
    {
        public int ObjectId;
        public Vector3 Position;
        public Quaternion Rotation;

        public SpawnAirplane()
        {

        }

        public SpawnAirplane(NetDataReader reader)
        {
            ObjectId = reader.GetInt();
            Position = reader.GetUnmanaged<Vector3>();
            Rotation = reader.GetUnmanaged<Quaternion>();
        }

        public void Execute(FikaPlayer player)
        {
            var processor = Singleton<GameWorld>.Instance.SynchronizableObjectLogicProcessor;
            if (processor == null)
            {
                return;
            }

            var syncObject = processor.TakeFromPool(SynchronizableObjectType.AirPlane).Cast<AirplaneSynchronizableObject>();
            syncObject.ObjectId = ObjectId;
            syncObject.transform.SetPositionAndRotation(Position, Rotation);
            processor.InitSyncObject(syncObject, Position, Rotation.eulerAngles, ObjectId);
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ObjectId);
            writer.PutUnmanaged(Position);
            writer.PutUnmanaged(Rotation);
        }
    }

    public class SpawnAirdrop : ISubPacket
    {
        public int ObjectId;
        public bool IsStatic;
        public Vector3 Position;
        public Quaternion Rotation;
        public EAirdropType AirdropType;
        public Item AirdropItem;
        public MongoID ContainerId;
        public int NetId;

        public SpawnAirdrop()
        {

        }

        public SpawnAirdrop(NetDataReader reader)
        {
            ObjectId = reader.GetInt();
            IsStatic = reader.GetBool();
            Position = reader.GetUnmanaged<Vector3>();
            Rotation = reader.GetUnmanaged<Quaternion>();
            AirdropType = reader.GetEnum<EAirdropType>();
            AirdropItem = reader.GetItem();
            ContainerId = reader.GetMongoID();
            NetId = reader.GetInt();
        }

        public void Execute(FikaPlayer player)
        {
            Singleton<GameWorld>.Instance.World.TryCast<FikaClientWorld>()?.WorldSpawns.Enqueue(this, SynchronizableObjectType.AirDrop, ObjectId);
        }

        internal bool TrySpawn()
        {
            var processor = Singleton<GameWorld>.Instance.SynchronizableObjectLogicProcessor;
            if (processor == null)
            {
                return false;
            }

#if DEBUG
            FikaGlobals.LogWarning($"Spawning airdrop at {Position} with id {ObjectId}");
#endif

            var existing = WorldSyncSpawnQueue.Find(processor, SynchronizableObjectType.AirDrop, ObjectId)?.TryCast<AirdropSynchronizableObject>();
            if (existing?.GetComponentInChildren<LootableContainer>(true)?.ItemOwner != null) return true;
            var syncObject = existing ?? processor.TakeFromPool(SynchronizableObjectType.AirDrop).Cast<AirdropSynchronizableObject>();
            var complete = false;
            try
            {
                syncObject.ObjectId = ObjectId;
                syncObject.IsStatic = IsStatic;
                syncObject.transform.position = Position;
                syncObject.transform.rotation = Rotation;
                if (syncObject.Logic?.TryCast<ClientAirDrop>() is { } airdropLogicClass)
                {
                    airdropLogicClass._destinationPosition = Position;
                }
                else
                {
                    FikaGlobals.LogWarning("AirdropSynchronizableObject logic was not of type ClientAirDrop!");
                }
                syncObject.AirdropType = AirdropType;
                syncObject.Logic?.TryCast<ClientAirDrop>()?.UpdateContainerType(AirdropType);
                // SPT 5 在物体下落时会把战利品碰撞体/容器置于未激活的 view 之下。
                var container = syncObject.GetComponentInChildren<LootableContainer>(true);
                if (container == null)
                {
                    return false;
                }
                container.enabled = true;
                container.Id = ContainerId;
                if (NetId > 0)
                {
                    container.NetId = NetId;
                }
                LootItem.CreateLootContainer(container, AirdropItem, AirdropItem.ShortName.Localized(null),
                        Singleton<GameWorld>.Instance, null);
                Singleton<GameWorld>.Instance.RegisterWorldInteractionObject(container);
                if (existing == null && !syncObject.IsStatic)
                {
                    processor.InitSyncObject(syncObject, syncObject.transform.position, syncObject.transform.rotation.eulerAngles, syncObject.ObjectId);
                }
                else if (existing == null) processor.InitStaticObject(syncObject);
                complete = true;
                return true;
            }
            finally
            {
                if (!complete && existing == null) processor.ReturnToPool(syncObject);
            }
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ObjectId);
            writer.Put(IsStatic);
            writer.PutUnmanaged(Position);
            writer.PutUnmanaged(Rotation);
            writer.PutEnum(AirdropType);
            writer.PutItem(AirdropItem);
            writer.PutMongoID(ContainerId);
            writer.Put(NetId);
        }
    }
}
