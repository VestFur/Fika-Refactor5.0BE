using System.Collections.Generic;
using ComponentAce.Compression.Libs.zlib;
using EFT;
using EFT.Interactive;
using Fika.Core.Networking.Packets.Communication;
using Fika.Core.Main.Utils;

namespace Fika.Core.Networking.Packets.World;

public sealed class ReconnectPacket : INetSerializable
{
    public bool IsRequest;
    public bool InitialRequest;
    public EReconnectDataType Type;

    public string ProfileId;
    public Profile Profile;
    public Profile.HealthInfo ProfileHealthClass;
    public Vector3 PlayerPosition;
    public Vector2 PlayerRotation;
    public MongoID ControllerId;
    public ushort FirstOperationId;
    public EHandsControllerType ControllerType;
    public MongoID ItemId;
    public bool IsStationary;
    public MongoID LastEquippedItemId;
    public Quaternion PlayerBodyRotation;
    public RevivalState RevivalState;
    public string Error;

    public List<SmokeGrenadeNetworkData> ThrowableData;
    public List<WorldInteractiveObject.InteractiveObjectStatusInfo> InteractivesData;
    public Dictionary<int, byte> LampStates;
    public Dictionary<int, Vector3> WindowBreakerStates;
    public List<QuestSyncPacket> QuestSyncPackets;
    public List<SynchronizableObjectPacket> WorldObjectUpdates;
    public List<BtrPassengerReconnectState> BtrPassengers;
    public List<TraderServiceAvailabilityData> BtrServices;

    public void Deserialize(NetDataReader reader)
    {
        IsRequest = reader.GetBool();
        InitialRequest = reader.GetBool();
        ProfileId = reader.GetString();
        if (!IsRequest)
        {
            Type = reader.GetEnum<EReconnectDataType>();
            switch (Type)
            {
                case EReconnectDataType.Throwable:
                    ThrowableData = reader.GetThrowableData();
                    break;
                case EReconnectDataType.Interactives:
                    InteractivesData = reader.GetInteractivesStates();
                    break;
                case EReconnectDataType.LampControllers:
                    LampStates = reader.GetLampStates();
                    break;
                case EReconnectDataType.Windows:
                    WindowBreakerStates = reader.GetWindowBreakerStates();
                    break;
                case EReconnectDataType.OwnCharacter:
                    Profile = reader.GetProfile();
                    ProfileHealthClass = SimpleZlib.Decompress(reader.GetByteArray()).ParseJsonTo<Profile.HealthInfo>();
                    PlayerPosition = reader.GetUnmanaged<Vector3>();
                    PlayerRotation = reader.GetUnmanaged<Vector2>();
                    ControllerId = reader.GetMongoID();
                    FirstOperationId = reader.GetUShort();
                    ControllerType = reader.GetEnum<EHandsControllerType>();
                    ItemId = reader.GetNullableMongoID();
                    IsStationary = reader.GetBool();
                    LastEquippedItemId = reader.GetNullableMongoID();
                    PlayerBodyRotation = reader.GetUnmanaged<Quaternion>();
                    RevivalState.Deserialize(reader);
                    break;
                case EReconnectDataType.Finished:
                    RevivalState.Deserialize(reader);
                    var passengerCount = reader.GetByte();
                    BtrPassengers = new List<BtrPassengerReconnectState>(passengerCount);
                    for (var i = 0; i < passengerCount; i++)
                        BtrPassengers.Add(BtrPassengerReconnectState.Deserialize(reader));
                    var serviceCount = reader.GetByte();
                    BtrServices = new List<TraderServiceAvailabilityData>(serviceCount);
                    for (var i = 0; i < serviceCount; i++)
                        BtrServices.Add(reader.GetTraderService());
                    break;
                case EReconnectDataType.Failed:
                    Error = reader.GetString();
                    break;
                case EReconnectDataType.WorldObjects:
                    var objectCount = reader.GetUShort();
                    WorldObjectUpdates = new List<SynchronizableObjectPacket>(objectCount);
                    for (var i = 0; i < objectCount; i++) WorldObjectUpdates.Add(reader.GetAirplaneDataPacketStruct());
                    break;
                case EReconnectDataType.Quests:
                    var count = reader.GetUShort();
                    QuestSyncPackets = new List<QuestSyncPacket>(count);
                    for (var i = 0; i < count; i++)
                    {
                        QuestSyncPackets.Add(reader.Get<QuestSyncPacket>());
                    }
                    break;
            }
        }
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(IsRequest);
        writer.Put(InitialRequest);
        writer.Put(ProfileId);
        if (!IsRequest)
        {
            writer.PutEnum(Type);
            switch (Type)
            {
                case EReconnectDataType.Throwable:
                    writer.PutThrowableData(ThrowableData);
                    break;
                case EReconnectDataType.Interactives:
                    writer.PutInteractivesStates(InteractivesData);
                    break;
                case EReconnectDataType.LampControllers:
                    writer.PutLampStates(LampStates);
                    break;
                case EReconnectDataType.Windows:
                    writer.PutWindowBreakerStates(WindowBreakerStates);
                    break;
                case EReconnectDataType.OwnCharacter:
                    writer.PutProfile(Profile);
                    writer.PutByteArray(SimpleZlib.CompressToBytes(ProfileHealthClass.ToJson(), 4));
                    writer.PutUnmanaged(PlayerPosition);
                    writer.PutUnmanaged(PlayerRotation);
                    writer.PutMongoID(ControllerId);
                    writer.Put(FirstOperationId);
                    writer.PutEnum(ControllerType);
                    writer.PutNullableMongoID(ItemId);
                    writer.Put(IsStationary);
                    writer.PutNullableMongoID(LastEquippedItemId);
                    writer.PutUnmanaged(PlayerBodyRotation);
                    RevivalState.Serialize(writer);
                    break;
                case EReconnectDataType.Finished:
                    RevivalState.Serialize(writer);
                    var passengerCount = BtrPassengers?.Count ?? 0;
                    writer.Put(checked((byte)passengerCount));
                    if (BtrPassengers != null)
                        foreach (var passenger in BtrPassengers) passenger.Serialize(writer);
                    var serviceCount = BtrServices?.Count ?? 0;
                    writer.Put(checked((byte)serviceCount));
                    if (BtrServices != null)
                        foreach (var service in BtrServices) writer.PutTraderService(service);
                    break;
                case EReconnectDataType.Failed:
                    writer.Put(Error);
                    break;
                case EReconnectDataType.WorldObjects:
                    writer.Put(checked((ushort)WorldObjectUpdates.Count));
                    foreach (var update in WorldObjectUpdates) writer.PutAirplaneDataPacketStruct(update);
                    break;
                case EReconnectDataType.Quests:
                    var count = QuestSyncPackets.Count;
                    writer.Put((ushort)count);
                    for (var i = 0; i < count; i++)
                    {
                        writer.Put(QuestSyncPackets[i]);
                    }
                    break;
            }
        }
    }

    public enum EReconnectDataType
    {
        Throwable,
        Interactives,
        LampControllers,
        Windows,
        OwnCharacter,
        Quests,
        Finished,
        WorldObjects,
        Failed
    }

    public sealed class BtrPassengerReconnectState
    {
        public int PlayerRaidId;
        public byte SideId;
        public byte SlotId;
        public EPlayerBtrState State;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PlayerRaidId);
            writer.Put(SideId);
            writer.Put(SlotId);
            writer.PutEnum(State);
        }

        public static BtrPassengerReconnectState Deserialize(NetDataReader reader) => new()
        {
            PlayerRaidId = reader.GetInt(),
            SideId = reader.GetByte(),
            SlotId = reader.GetByte(),
            State = reader.GetEnum<EPlayerBtrState>()
        };
    }
}
