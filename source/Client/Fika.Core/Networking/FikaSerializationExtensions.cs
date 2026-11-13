using Diz.Jobs;
using EFT.Weather;
using JsonType;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Airdrop;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.SynchronizableObjects;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets;
using Fika.Core.Networking.Packets.FirearmController;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;
using static PhysicalBase;
using static Fika.Core.Networking.Packets.World.RequestSubPackets;

namespace Fika.Core.Networking;

/// <summary>
/// Unity/EFT 类的序列化扩展，用于简化 Fika 中的封包写入
/// </summary>
public static class FikaSerializationExtensions
{
    /// <summary>
    /// 将 <see cref="PhysicalBase.StaminaStruct"/> 结构体序列化到 <paramref name="writer"/>
    /// </summary>
    /// <param name="writer">用于写入数据的 <see cref="NetDataWriter"/></param>
    /// <param name="physical">要序列化的 <see cref="PhysicalBase.StaminaStruct"/></param>
    public static void PutPhysical(this NetDataWriter writer, PhysicalBase.StaminaStruct physical)
    {
        byte flags = 0;

        if (physical.StaminaExhausted)
        {
            flags |= 1 << 0;
        }

        if (physical.OxygenExhausted)
        {
            flags |= 1 << 1;
        }

        if (physical.HandsExhausted)
        {
            flags |= 1 << 2;
        }

        writer.Put(flags);
    }

    /// <summary>
    /// 从 <paramref name="reader"/> 反序列化 <see cref="PhysicalBase.StaminaStruct"/> 结构体
    /// </summary>
    /// <param name="reader">用于读取数据的 <see cref="NetDataReader"/></param>
    /// <returns>反序列化后的 <see cref="PhysicalBase.StaminaStruct"/></returns>
    public static PhysicalBase.StaminaStruct GetPhysical(this NetDataReader reader)
    {
        var flags = reader.GetByte();
        return new()
        {
            StaminaExhausted = (flags & (1 << 0)) != 0,
            OxygenExhausted = (flags & (1 << 1)) != 0,
            HandsExhausted = (flags & (1 << 2)) != 0
        };
    }

    /// <summary>
    /// 序列化 <see cref="byte"/> 数组
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="bytes"></param>
    public static void PutByteArray(this NetDataWriter writer, byte[] bytes)
    {
        var length = bytes.Length;
        writer.Put(length);
        if (length > 0)
        {
            writer.Put(bytes);
        }
    }

    /// <summary>
    /// 序列化由 <see cref="byte"/> 组成的 <see cref="ReadOnlySpan{T}"/> 数组
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="bytes"></param>
    public static void PutByteArray(this NetDataWriter writer, ReadOnlySpan<byte> bytes)
    {
        var length = bytes.Length;
        writer.Put(length);
        if (length > 0)
        {
            writer.Put(bytes);
        }
    }

    /// <summary>
    /// 反序列化 <see cref="byte"/> 数组
    /// </summary>
    /// <param name="reader"></param>
    /// <returns>一个 <see cref="byte"/> 数组</returns>
    public static byte[] GetByteArray(this NetDataReader reader)
    {
        var length = reader.GetInt();
        if (length <= 0)
        {
            return [];
        }

        var bytes = new byte[length];
        reader.GetBytes(bytes, length);
        return bytes;
    }

    /// <summary>
    /// 压缩给定的字节数组，并把原始长度作为前缀写入 writer
    /// </summary>
    /// <param name="writer">用于写入压缩数据的 <see cref="NetDataWriter"/></param>
    /// <param name="bytes">要压缩并写入的字节数组</param>
    public static void CompressAndPutByteArray(this NetDataWriter writer, byte[] bytes)
    {
        writer.Put(bytes.Length);
        if (bytes.Length == 0)
        {
            return;
        }
        writer.PutByteArray(NetworkUtils.CompressBytes(bytes));
    }

    /// <summary>
    /// 从 reader 读取压缩的字节数组，解压后返回原始字节数组
    /// </summary>
    /// <param name="reader">用于读取压缩数据的 <see cref="NetDataReader"/></param>
    /// <returns>解压后的原始字节数组；若长度为零则返回空数组</returns>
    public static byte[] DecompressAndGetByteArray(this NetDataReader reader)
    {
        var originalLength = reader.GetInt();
        if (originalLength == 0)
        {
            return [];
        }
        return NetworkUtils.DecompressBytes(reader.GetByteArray(), originalLength);
    }

    /// <summary>
    /// 将 <see cref="Item"/> 序列化到 <paramref name="writer"/>。<br/>
    /// 为保持一致，转换到继承类型的操作应在封包内部处理。
    /// </summary>
    /// <param name="writer">用于写入数据的 <see cref="NetDataWriter"/></param>
    /// <param name="item">要序列化的 <see cref="Item"/></param>
    public static void PutItem(this NetDataWriter writer, Item item)
    {
        var descriptor = ItemBinarySerializer.SerializeItem(item, FikaGlobals.SearchControllerSerializer);
        writer.PutEFTItemDescriptor(descriptor);
    }

    /// <summary>
    /// 从 <paramref name="reader"/> 反序列化 <see cref="Item"/>。<br/>
    /// 返回的 <see cref="Item"/> 会在封包内部转换为合适的类型。
    /// </summary>
    /// <param name="reader">用于读取数据的 <see cref="NetDataReader"/></param>
    /// <returns>反序列化后的 <see cref="Item"/></returns>
    public static Item GetItem(this NetDataReader reader)
    {
        return ItemBinarySerializer.DeserializeItem(reader.GetEFTItemDescriptor(), Singleton<ItemFactory>.Instance,
            new Il2CppSystem.Collections.Generic.Dictionary<MongoID, Item>());
    }


    /// <summary>
    /// 读取由 <see cref="PutItem(NetDataWriter, Item)"/> 序列化的 <see cref="InventoryEquipment"/>，并将其转换为 <see cref="Inventory"/>
    /// </summary>
    /// <returns>一个 <see cref="Inventory"/></returns>
    public static Inventory GetInventoryFromEquipment(this NetDataReader reader)
    {
        return new InventoryDescriptor()
        {
            Equipment = reader.GetEFTItemDescriptor()
        }.ToInventory();
    }

    /// <summary>
    /// 写入 <see cref="ItemDescriptor"/>
    /// </summary>
    /// <param name="writer">用于写入数据的 writer</param>
    /// <param name="descriptor">要序列化的 <see cref="ItemDescriptor"/> 实例</param>
    public static void PutItemDescriptor(this NetDataWriter writer, ItemDescriptor descriptor)
    {
        var buffer = new NetDataWriter(true, 1024);
        buffer.PutEFTItemDescriptor(descriptor);
        writer.CompressAndPutByteArray(buffer.CopyData());
    }

    /// <summary>
    /// 读取并返回 <see cref="ItemDescriptor"/>
    /// </summary>
    /// <param name="reader">用于读取序列化物品描述符数据的 reader</param>
    /// <returns>反序列化后的 <see cref="ItemDescriptor"/> 实例</returns>
    public static ItemDescriptor GetItemDescriptor(this NetDataReader reader)
    {
        var buffer = new NetDataReader(reader.DecompressAndGetByteArray());
        return buffer.GetEFTItemDescriptor();
    }

    /// <summary>
    /// 反序列化并获取 <see cref="Item"/>
    /// </summary>
    /// <param name="reader">用于读取序列化物品数据的 reader</param>
    /// <returns>表示空投物品的反序列化 <see cref="Item"/> 实例</returns>
    public static Item GetAirdropItem(this NetDataReader reader)
    {
        var item = ItemBinarySerializer.DeserializeItem(reader.GetEFTItemDescriptor(), Singleton<ItemFactory>.Instance,
            new Il2CppSystem.Collections.Generic.Dictionary<MongoID, Item>());

        var lootItems = new Il2CppSystem.Collections.Generic.List<JsonLootItem>();
        lootItems.Add(new JsonLootItem());
        LootData enumerable = new LootData(lootItems);
        enumerable[0].Item = item;
        var array = new List<Item>();
        foreach (var loot in enumerable) array.Add(FikaGlobals.GetLootItemPositionItem(loot));
        var resources = new Il2CppSystem.Collections.Generic.List<ResourceKey>();
        foreach (var lootItem in array)
        {
            foreach (var resource in lootItem.Template.AllResources) resources.Add(resource);
        }
        Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid, ObjectsFactory.AssemblyType.Online,
            resources, JobYieldPriority.Immediate, null, new Il2CppSystem.Threading.CancellationToken()).HandleExceptions();

        return item;
    }

    /// <summary>
    /// 序列化 <see cref="SmokeGrenadeNetworkData"/> 的 <see cref="List{T}"/>
    /// </summary>
    /// <param name="writer">用于写入数据的 <see cref="NetDataWriter"/></param>
    /// <param name="throwables">要序列化的 <see cref="SmokeGrenadeNetworkData"/> 列表</param>
    public static void PutThrowableData(this NetDataWriter writer, List<SmokeGrenadeNetworkData> throwables)
    {
        writer.Put(throwables.Count);
        foreach (var data in throwables)
        {
            writer.Put(data.Id);
            writer.PutUnmanaged(data.Position);
            writer.Put(data.Template);
            writer.Put(data.Time);
            writer.PutUnmanaged(data.Orientation);
            writer.Put(data.PlatformId);
        }
    }

    /// <summary>
    /// 反序列化 <see cref="SmokeGrenadeNetworkData"/> 的 <see cref="List{T}"/>
    /// </summary>
    /// <param name="reader">用于读取数据的 <see cref="NetDataReader"/></param>
    /// <returns>一个 <see cref="SmokeGrenadeNetworkData"/> 的 <see cref="List{T}"/></returns>
    public static List<SmokeGrenadeNetworkData> GetThrowableData(this NetDataReader reader)
    {
        var amount = reader.GetInt();
        List<SmokeGrenadeNetworkData> throwables = new(amount);
        for (var i = 0; i < amount; i++)
        {
            SmokeGrenadeNetworkData data = new()
            {
                Id = reader.GetString(),
                Position = reader.GetUnmanaged<Vector3>(),
                Template = reader.GetString(),
                Time = reader.GetInt(),
                Orientation = reader.GetUnmanaged<Quaternion>(),
                PlatformId = reader.GetShort()
            };
            throwables.Add(data);
        }

        return throwables;
    }

    /// <summary>
    /// 序列化 <see cref="Profile"/>
    /// </summary>
    /// <param name="writer">用于写入数据的 <see cref="NetDataWriter"/></param>
    /// <param name="profile">要序列化的 <see cref="Profile"/></param>
    public static void PutProfile(this NetDataWriter writer, Profile profile)
    {
        var buffer = new NetDataWriter(true, 1024);
        // 本战局中的真人玩家共享 Fika 的 "Fika" 分组哨兵值（参见
        // 本地玩家的 FikaPlayer.SetupMainPlayer，以及
        // 其他所有人的 ObservedPlayer.InitObservedPlayer）：该共享分组是
        // 第三方使用方唯一能获得的「这是自己人」信号（例如 "SPT Dynamic Maps"
        // 在 Player.GroupId 等于主玩家时会把该玩家标记为 "Friendly Player"），并且
        // 它也是游戏判定某个玩家为队伍成员的依据。因此它必须随
        // 广播的存档一同传输；只有战局结束时的 scav 存档会再次将它剥离（参见
        // FixSavageInventoryScreenPatch_Override.RaidEndProfile）。
        var descriptor = new ProfileDescriptor(profile, FikaGlobals.SearchControllerSerializer);
        // 观察方仅依据该描述符构建玩家：其名牌读取
        // Info.MainProfileNickname，地图类 mod 读取 Info.Nickname，因此绝不要广播一个
        // 名字为空的存档。
        FikaGlobals.EnsureProfileNames(descriptor);
        buffer.PutEFTProfileDescriptor(descriptor);
        writer.CompressAndPutByteArray(buffer.CopyData());
    }

    /// <summary>
    /// 反序列化 <see cref="Profile"/>
    /// </summary>
    /// <param name="reader">用于读取数据的 <see cref="NetDataReader"/></param>
    /// <returns>反序列化后的 <see cref="Profile"/></returns>
    public static Profile GetProfile(this NetDataReader reader)
    {
        var buffer = new NetDataReader(reader.DecompressAndGetByteArray());
        return new Profile(buffer.GetEFTProfileDescriptor());
    }

    /// <summary>
    /// 序列化 <see cref="WorldInteractiveObject"/> 数据的 <see cref="List{WorldInteractiveObject.InteractiveObjectStatusInfo}"/>
    /// </summary>
    /// <param name="interactiveObjectsData">要序列化的交互状态</param>
    public static void PutInteractivesStates(this NetDataWriter writer, List<WorldInteractiveObject.InteractiveObjectStatusInfo> interactiveObjectsData)
    {
        writer.Put(interactiveObjectsData.Count);
        for (var i = 0; i < interactiveObjectsData.Count; i++)
        {
            writer.Put(interactiveObjectsData[i].NetId);
            writer.Put(interactiveObjectsData[i].State);
            writer.Put(interactiveObjectsData[i].IsBroken);
        }
    }

    /// <summary>
    /// 反序列化 <see cref="WorldInteractiveObject"/> 数据的 <see cref="List{WorldInteractiveObject.InteractiveObjectStatusInfo}"/>
    /// </summary>
    /// <returns>一个 <see cref="WorldInteractiveObject.InteractiveObjectStatusInfo"/> 的 <see cref="List{T}"/></returns>
    public static List<WorldInteractiveObject.InteractiveObjectStatusInfo> GetInteractivesStates(this NetDataReader reader)
    {
        var amount = reader.GetInt();
        List<WorldInteractiveObject.InteractiveObjectStatusInfo> interactivesStates = new(amount);
        for (var i = 0; i < amount; i++)
        {
            WorldInteractiveObject.InteractiveObjectStatusInfo data = new()
            {
                NetId = reader.GetInt(),
                State = reader.GetByte(),
                IsBroken = reader.GetBool()
            };
            interactivesStates.Add(data);
        }

        return interactivesStates;
    }

    /// <summary>
    /// 序列化 <see cref="LampController"/> 信息的 <see cref="Dictionary{int, byte}"/>
    /// </summary>
    /// <param name="lampStates">要序列化的灯光状态</param>
    public static void PutLampStates(this NetDataWriter writer, Dictionary<int, byte> lampStates)
    {
        var amount = lampStates.Count;
        writer.Put(amount);
        foreach (var lampState in lampStates)
        {
            writer.Put(lampState.Key);
            writer.Put(lampState.Value);
        }
    }

    /// <summary>
    /// 反序列化 <see cref="LampController"/> 信息的 <see cref="Dictionary{int, byte}"/>
    /// </summary>
    /// <returns>包含 <see cref="LampController"/> 信息的 <see cref="Dictionary{TKey, TValue}"/></returns>
    public static Dictionary<int, byte> GetLampStates(this NetDataReader reader)
    {
        var amount = reader.GetInt();
        Dictionary<int, byte> states = new(amount);
        for (var i = 0; i < amount; i++)
        {
            states.Add(reader.GetInt(), reader.GetByte());
        }

        return states;
    }

    /// <summary>
    /// 序列化 <see cref="WindowBreaker"/> 信息的 <see cref="Dictionary{int, Vector3}"/>
    /// </summary>
    /// <param name="windowBreakerStates">要序列化的窗户破碎状态</param>
    public static void PutWindowBreakerStates(this NetDataWriter writer, Dictionary<int, Vector3> windowBreakerStates)
    {
        var amount = windowBreakerStates.Count;
        writer.Put(amount);
        foreach (var windowBreakerState in windowBreakerStates)
        {
            writer.Put(windowBreakerState.Key);
            writer.PutUnmanaged(windowBreakerState.Value);
        }
    }

    /// <summary>
    /// 反序列化 <see cref="WindowBreaker"/> 信息的 <see cref="Dictionary{int, Vector3}"/>
    /// </summary>
    /// <returns>包含 <see cref="WindowBreaker"/> 信息的 <see cref="Dictionary{TKey, TValue}"/></returns>
    public static Dictionary<int, Vector3> GetWindowBreakerStates(this NetDataReader reader)
    {
        var amount = reader.GetInt();
        Dictionary<int, Vector3> states = new(amount);
        for (var i = 0; i < amount; i++)
        {
            states.Add(reader.GetInt(), reader.GetUnmanaged<Vector3>());
        }

        return states;
    }

    /// <summary>
    /// 序列化 <see cref="MongoID"/>
    /// </summary>
    /// <param name="mongoId">要序列化的 <see cref="MongoID"/></param>
    public static void PutMongoID(this NetDataWriter writer, MongoID mongoId)
    {
        writer.Put(mongoId._timeStamp);
        writer.Put(mongoId._counter);
    }

    /// <summary>
    /// 反序列化 <see cref="MongoID"/>
    /// </summary>
    /// <returns>一个新的 <see cref="MongoID"/></returns>
    public static MongoID GetMongoID(this NetDataReader reader)
    {
        MongoID id = new()
        {
            _timeStamp = reader.GetUInt(),
            _counter = reader.GetULong()
        };

        id._stringID = NetworkUtils.FormatMongoId(id._timeStamp, id._counter);
        id.Init();

        return id;
    }

    /// <summary>
    /// 序列化 <see cref="MongoID"/>?（可空）
    /// </summary>
    /// <param name="mongoId">要序列化的可空 <see cref="MongoID"/></param>
    public static void PutNullableMongoID(this NetDataWriter writer, MongoID? mongoId)
    {
        writer.Put(mongoId is not null);
        if (mongoId is not null)
        {
            writer.PutMongoID(mongoId);
        }
    }

    /// <summary>
    /// 反序列化 <see cref="MongoID"/>?（可空）
    /// </summary>
    /// <returns>一个新的 <see cref="MongoID"/>?（可空）</returns>
    public static MongoID? GetNullableMongoID(this NetDataReader reader)
    {
        if (!reader.GetBool())
        {
            return null;
        }

        return reader.GetMongoID();
    }

    /// <summary>
    /// 尝试从流中读取可空的 <see cref="MongoID"/>。
    /// </summary>
    /// <param name="reader">正在读取数据的 <see cref="NetDataReader"/> 实例。</param>
    /// <param name="mongoId">
    /// 当此方法返回时，若存在，则包含从流中读取的 <see cref="MongoID"/>；
    /// 否则为 <see langword="null"/>。此参数传入时未初始化。
    /// </param>
    /// <returns>
    /// <see langword="true"/> 表示成功读取到 <see cref="MongoID"/>；否则为 <see langword="false"/>。
    /// </returns>
    public static bool TryGetNullableMongoId(this NetDataReader reader, out MongoID? mongoId)
    {
        if (!reader.GetBool())
        {
            mongoId = null;
            return false;
        }

        mongoId = reader.GetMongoID();
        return true;
    }

    /// <summary>
    /// 序列化 <see cref="TraderServiceAvailabilityData"/>
    /// </summary>
    /// <param name="traderService">要序列化的交易商服务数据</param>
    public static void PutTraderService(this NetDataWriter writer, TraderServiceAvailabilityData traderService)
    {
        writer.PutMongoID(traderService.TraderId);
        writer.PutEnum(traderService.ServiceType);
        writer.Put(traderService.CanAfford);
        writer.Put(traderService.WasPurchasedInThisRaid);
        writer.Put(traderService.ItemsToPay.Count);
        foreach ((var id, var amount) in traderService.ItemsToPay)
        {
            writer.PutMongoID(id);
            writer.Put(amount);
        }
        var uniqueAmount = traderService.UniqueItems.Length;
        writer.Put(uniqueAmount);
        for (var i = 0; i < uniqueAmount; i++)
        {
            writer.PutMongoID(traderService.UniqueItems[i]);
        }
        writer.Put(traderService.SubServices.Count);
        foreach ((var key, var value) in traderService.SubServices)
        {
            writer.Put(key);
            writer.Put(value);
        }
    }

    /// <summary>
    /// 反序列化 <see cref="TraderServiceAvailabilityData"/>
    /// </summary>
    /// <returns>一个 <see cref="TraderServiceAvailabilityData"/></returns>
    public static TraderServiceAvailabilityData GetTraderService(this NetDataReader reader)
    {
        TraderServiceAvailabilityData traderService = new()
        {
            TraderId = reader.GetMongoID(),
            ServiceType = reader.GetEnum<ETraderServiceType>(),
            CanAfford = reader.GetBool(),
            WasPurchasedInThisRaid = reader.GetBool()
        };
        var toPayAmount = reader.GetInt();
        traderService.ItemsToPay = new(toPayAmount);
        for (var i = 0; i < toPayAmount; i++)
        {
            traderService.ItemsToPay[reader.GetMongoID()] = reader.GetInt();
        }
        var uniqueAmount = reader.GetInt();
        traderService.UniqueItems = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<MongoID>(uniqueAmount);
        for (var i = 0; i < uniqueAmount; i++)
        {
            traderService.UniqueItems[i] = reader.GetMongoID();
        }
        var subAmount = reader.GetInt();
        traderService.SubServices = new(subAmount);
        for (var i = 0; i < subAmount; i++)
        {
            traderService.SubServices[reader.GetString()] = reader.GetInt();
        }
        return traderService;
    }

    /// <summary>
    /// 将 <see cref="Profile.HealthInfo"/> 写入原始 <see cref="byte"/>[]
    /// </summary>
    /// <param name="health">要序列化的健康信息</param>
    /// <returns><see cref="byte"/>[]</returns>
    public static byte[] SerializeHealthInfo(this Profile.HealthInfo health)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.WriteValueInfo(health.Energy);
        writer.WriteValueInfo(health.Hydration);
        writer.WriteValueInfo(health.Temperature);
        writer.WriteValueInfo(health.Poison);
        const float standard = 1f;
        // 治疗速率
        writer.Write(standard);
        // 伤害速率
        writer.Write(standard);
        // 伤害倍率
        writer.Write(standard);
        // 能量速率
        writer.Write(standard);
        // 水分速率
        writer.Write(standard);
        // 温度速率
        writer.Write(standard);
        // 伤害系数
        writer.Write(standard);
        // 耐力系数
        writer.Write(standard);

        foreach ((var part, var healthClass) in health.BodyParts)
        {
            var bodyPartInfo = healthClass.Health;
            writer.Write(bodyPartInfo.Current <= bodyPartInfo.Minimum);
            writer.Write(bodyPartInfo.Current);
            writer.Write(bodyPartInfo.Maximum);
        }

        // 效果量 - 设为 0，因为这是全新存档
        const short effectAmount = 0;
        writer.Write(effectAmount);
        const byte end = 42;
        writer.Write(end);

        return stream.ToArray();
    }

    /// <summary>
    /// 将 <see cref="Profile.HealthInfo.ValueInfo"/> 写入 <see cref="byte"/> 序列
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="valueInfo"></param>
    public static void WriteValueInfo(this BinaryWriter writer, Profile.HealthInfo.ValueInfo valueInfo)
    {
        writer.Write(valueInfo.Current);
        writer.Write(valueInfo.Minimum);
        writer.Write(valueInfo.Maximum);
    }

    /// <summary>
    /// 序列化 <see cref="ArtilleryProjectileSyncPacket"/>
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="artilleryStruct"></param>
    public static void PutArtilleryStruct(this NetDataWriter writer, ArtilleryProjectileSyncPacket artilleryStruct)
    {
        writer.Put(artilleryStruct.id);
        writer.PutUnmanaged(artilleryStruct.position);
        writer.Put(artilleryStruct.explosion);
    }

    /// <summary>
    /// 反序列化 <see cref="ArtilleryProjectileSyncPacket"/>
    /// </summary>
    /// <param name="reader"></param>
    /// <returns>带数据的 <see cref="ArtilleryProjectileSyncPacket"/></returns>
    public static ArtilleryProjectileSyncPacket GetArtilleryStruct(this NetDataReader reader)
    {
        return new()
        {
            id = reader.GetInt(),
            position = reader.GetUnmanaged<Vector3>(),
            explosion = reader.GetBool()
        };
    }

    /// <summary>
    /// 序列化 <see cref="GrenadeSyncPacket"/>
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="grenadeStruct"></param>
    public static void PutGrenadeStruct(this NetDataWriter writer, GrenadeSyncPacket grenadeStruct)
    {
        writer.Put(grenadeStruct.Id);
        writer.PutUnmanaged(grenadeStruct.Position);
        writer.PutUnmanaged(grenadeStruct.Rotation);
        writer.Put(grenadeStruct.CollisionNumber);
        writer.Put(grenadeStruct.Done);
        if (!grenadeStruct.Done)
        {
            writer.PutUnmanaged(grenadeStruct.Velocity);
            writer.PutUnmanaged(grenadeStruct.AngularVelocity);
        }
    }

    /// <summary>
    /// 反序列化 <see cref="GrenadeSyncPacket"/>
    /// </summary>
    /// <param name="reader"></param>
    /// <returns>带数据的 <see cref="GrenadeSyncPacket"/></returns>
    public static GrenadeSyncPacket GetGrenadeStruct(this NetDataReader reader)
    {
        GrenadeSyncPacket grenadeStruct = new()
        {
            Id = reader.GetInt(),
            Position = reader.GetUnmanaged<Vector3>(),
            Rotation = reader.GetUnmanaged<Quaternion>(),
            CollisionNumber = reader.GetByte()
        };

        if (!reader.GetBool())
        {
            grenadeStruct.Velocity = reader.GetUnmanaged<Vector3>();
            grenadeStruct.AngularVelocity = reader.GetUnmanaged<Vector3>();
            return grenadeStruct;
        }

        grenadeStruct.Done = true;
        return grenadeStruct;
    }

    /// <summary>
    /// 序列化 <see cref="SynchronizableObjectPacket"/>
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="airplaneDataPacketStruct"></param>
    public static void PutAirplaneDataPacketStruct(this NetDataWriter writer, SynchronizableObjectPacket airplaneDataPacketStruct)
    {
        writer.PutEnum(airplaneDataPacketStruct.ObjectType);
        writer.Put((byte)airplaneDataPacketStruct.ObjectId);

        switch (airplaneDataPacketStruct.ObjectType)
        {
            case SynchronizableObjectType.AirDrop:
                writer.PutUnmanaged(airplaneDataPacketStruct.Position);
                writer.PutUnmanaged(airplaneDataPacketStruct.Rotation);
                writer.Put(airplaneDataPacketStruct.Outdated);
                writer.Put(airplaneDataPacketStruct.IsStatic);
                writer.PutEnum(airplaneDataPacketStruct.PacketData.AirdropDataPacket.AirdropType);
                writer.PutEnum(airplaneDataPacketStruct.PacketData.AirdropDataPacket.FallingStage);
                writer.Put(airplaneDataPacketStruct.PacketData.AirdropDataPacket.SignalFire);
                writer.Put(airplaneDataPacketStruct.PacketData.AirdropDataPacket.UniqueId);
                return;
            case SynchronizableObjectType.AirPlane:
                writer.PutUnmanaged(airplaneDataPacketStruct.Position);
                writer.PutUnmanaged(airplaneDataPacketStruct.Rotation);
                writer.Put(airplaneDataPacketStruct.PacketData.AirplaneDataPacket.AirplanePercent);
                writer.Put(airplaneDataPacketStruct.Outdated);
                writer.Put(airplaneDataPacketStruct.IsStatic);
                return;
            case SynchronizableObjectType.Tripwire:
                writer.PutEnum(airplaneDataPacketStruct.PacketData.TripwireDataPacket.State);
                writer.PutUnmanaged(airplaneDataPacketStruct.Position);
                writer.PutUnmanaged(airplaneDataPacketStruct.Rotation);
                writer.Put(airplaneDataPacketStruct.IsActive);
                return;
        }
    }

    /// <summary>
    /// 反序列化 <see cref="SynchronizableObjectPacket"/>
    /// </summary>
    /// <param name="reader"></param>
    /// <returns></returns>
    public static SynchronizableObjectPacket GetAirplaneDataPacketStruct(this NetDataReader reader)
    {
        SynchronizableObjectPacket packet = new()
        {
            ObjectType = reader.GetEnum<SynchronizableObjectType>(),
            ObjectId = reader.GetByte(),
            PacketData = new()
        };

        var data = packet.PacketData;
        switch (packet.ObjectType)
        {
            case SynchronizableObjectType.AirDrop:
                packet.Position = reader.GetUnmanaged<Vector3>();
                packet.Rotation = reader.GetUnmanaged<Vector3>();
                packet.Outdated = reader.GetBool();
                packet.IsStatic = reader.GetBool();
                data.AirdropDataPacket = new()
                {
                    AirdropType = reader.GetEnum<EAirdropType>(),
                    FallingStage = reader.GetEnum<EAirdropFallingStage>(),
                    SignalFire = reader.GetBool(),
                    UniqueId = reader.GetInt()
                };
                break;
            case SynchronizableObjectType.AirPlane:
                packet.Position = reader.GetUnmanaged<Vector3>();
                packet.Rotation = reader.GetUnmanaged<Vector3>();
                data.AirplaneDataPacket = new()
                {
                    AirplanePercent = reader.GetInt()
                };
                packet.Outdated = reader.GetBool();
                packet.IsStatic = reader.GetBool();
                break;
            case SynchronizableObjectType.Tripwire:
                data.TripwireDataPacket = new()
                {
                    State = reader.GetEnum<ETripwireState>()
                };
                packet.Position = reader.GetUnmanaged<Vector3>();
                packet.Rotation = reader.GetUnmanaged<Vector3>();
                packet.IsActive = reader.GetBool();
                break;
        }

        packet.PacketData = data;
        return packet;
    }

    /// <summary>
    /// 序列化 <see cref="PlayerInfoPacket"/>
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="packet"></param>
    public static void PutPlayerInfoPacket(this NetDataWriter writer, PlayerInfoPacket packet)
    {
        writer.PutProfile(packet.Profile);
        writer.PutMongoID(packet.ControllerId);
        writer.PutNullableMongoID(packet.ItemId);
        writer.PutByteArray(packet.HealthByteArray ?? []);

        writer.Put(packet.FirstOperationId);
        writer.PutEnum(packet.ControllerType);

        byte flags = 0;
        if (packet.IsStationary)
        {
            flags |= 1;
        }

        if (packet.IsZombie)
        {
            flags |= 2;
        }

        writer.Put(flags);
    }

    /// <summary>
    /// 反序列化 <see cref="PlayerInfoPacket"/>
    /// </summary>
    /// <param name="reader"></param>
    /// <returns>带数据的 <see cref="PlayerInfoPacket"/></returns>
    public static PlayerInfoPacket GetPlayerInfoPacket(this NetDataReader reader)
    {
        PlayerInfoPacket packet = new()
        {
            Profile = reader.GetProfile(),
            ControllerId = reader.GetMongoID(),
            ItemId = reader.GetNullableMongoID(),
            HealthByteArray = reader.GetByteArray(),

            FirstOperationId = reader.GetUShort(),
            ControllerType = reader.GetEnum<EHandsControllerType>(),
        };

        var flags = reader.GetByte();
        packet.IsStationary = (flags & 1) != 0;
        packet.IsZombie = (flags & 2) != 0;

        return packet;
    }

    /// <summary>
    /// 序列化 <see cref="WeatherNode"/>
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="weatherClass"></param>
    public static void PutWeatherClass(this NetDataWriter writer, WeatherNode weatherClass)
    {
        writer.Put(weatherClass.Time);

        writer.PutUnmanaged(weatherClass.MainWindDirection);
        writer.PutUnmanaged(weatherClass.MainWindPosition);
        writer.PutUnmanaged(weatherClass.TopWindDirection);
        writer.PutUnmanaged(weatherClass.TopWindPosition);

        writer.Put(weatherClass.AtmospherePressure);
        writer.Put(weatherClass.Cloudness);
        writer.Put(weatherClass.GlobalFogDensity);
        writer.Put(weatherClass.GlobalFogHeight);
        writer.Put(weatherClass.LyingWater);
        writer.Put(weatherClass.Rain);
        writer.Put(weatherClass.RainRandomness);
        writer.Put(weatherClass.ScaterringFogDensity);
        writer.Put(weatherClass.ScaterringFogHeight);
        writer.Put(weatherClass.Temperature);
        writer.Put(weatherClass.Turbulence);
        writer.Put(weatherClass.Wind);

        writer.Put(weatherClass.WindDirection);
    }

    /// <summary>
    /// 反序列化 <see cref="WeatherNode"/>
    /// </summary>
    /// <param name="reader"></param>
    /// <returns>带数据的 <see cref="WeatherNode"/></returns>
    public static WeatherNode GetWeatherClass(this NetDataReader reader)
    {
        return new WeatherNode()
        {
            Time = reader.GetLong(),

            MainWindDirection = reader.GetUnmanaged<Vector2>(),
            MainWindPosition = reader.GetUnmanaged<Vector2>(),
            TopWindDirection = reader.GetUnmanaged<Vector2>(),
            TopWindPosition = reader.GetUnmanaged<Vector2>(),

            AtmospherePressure = reader.GetFloat(),
            Cloudness = reader.GetFloat(),
            GlobalFogDensity = reader.GetFloat(),
            GlobalFogHeight = reader.GetFloat(),
            LyingWater = reader.GetFloat(),
            Rain = reader.GetFloat(),
            RainRandomness = reader.GetFloat(),
            ScaterringFogDensity = reader.GetFloat(),
            ScaterringFogHeight = reader.GetFloat(),
            Temperature = reader.GetFloat(),
            Turbulence = reader.GetFloat(),
            Wind = reader.GetFloat(),

            WindDirection = reader.GetInt(),
        };
    }

    /// <summary>
    /// 序列化 <see cref="CorpseSyncPackets"/>
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="packet"></param>
    public static void PutCorpseSyncPacket(this NetDataWriter writer, CorpseSyncPackets packet)
    {
        writer.PutEFTItemDescriptor(packet.InventoryDescriptor);

        writer.PutUnmanaged(packet.Direction);
        writer.PutUnmanaged(packet.Point);
        writer.Put(packet.Force);

        writer.PutEnum(packet.BodyPartColliderType);
        writer.PutEnum(packet.ItemSlot);
    }

    /// <summary>
    /// 反序列化 <see cref="CorpseSyncPackets"/>
    /// </summary>
    /// <param name="reader"></param>
    /// <returns>带数据的 <see cref="CorpseSyncPackets"/></returns>
    public static CorpseSyncPackets GetCorpseSyncPacket(this NetDataReader reader)
    {
        return new CorpseSyncPackets()
        {
            InventoryDescriptor = reader.GetEFTItemDescriptor(),

            Direction = reader.GetUnmanaged<Vector3>(),
            Point = reader.GetUnmanaged<Vector3>(),
            Force = reader.GetFloat(),

            BodyPartColliderType = reader.GetEnum<EBodyPartColliderType>(),
            ItemSlot = reader.GetEnum<EquipmentSlot>()
        };
    }

    /// <summary>
    /// 写入 <see cref="CorpseSyncPacket"/>
    /// </summary>
    /// <param name="writer">用于写入数据的 writer</param>
    /// <param name="packet">要序列化的 <see cref="CorpseSyncPacket"/> 实例</param>
    public static void PutRagdollStruct(this NetDataWriter writer, CorpseSyncPacket packet)
    {
        writer.Put(packet.Id);
        writer.PutUnmanaged(packet.Position);
        writer.Put(packet.Done);

        if (packet.Done && packet.TransformSyncs != null)
        {
            var transforms = packet.TransformSyncs;
            for (var i = 0; i < 12; i++)
            {
                writer.PutUnmanaged(transforms[i].Position);
                writer.PutUnmanaged(transforms[i].Rotation);
            }
        }
    }

    /// <summary>
    /// 读取 <see cref="CorpseSyncPacket"/>
    /// </summary>
    /// <param name="reader">用于读取数据的 reader。</param>
    /// <returns>反序列化后的 <see cref="CorpseSyncPacket"/></returns>
    public static CorpseSyncPacket GetRagdollStruct(this NetDataReader reader)
    {
        CorpseSyncPacket packet = new()
        {
            Id = reader.GetInt(),
            Position = reader.GetUnmanaged<Vector3>(),
            Done = reader.GetBool()
        };

        if (packet.Done)
        {
            packet.TransformSyncs = new TransformSync[12];
            for (var i = 0; i < 12; i++)
            {
                packet.TransformSyncs[i] = new()
                {
                    Position = reader.GetUnmanaged<Vector3>(),
                    Rotation = reader.GetUnmanaged<Quaternion>()
                };
            }
        }

        return packet;
    }

    /// <summary>
    /// 写入 <see cref="EFT.LootSyncPacket"/>
    /// </summary>
    /// <param name="writer">用于写入数据的 writer</param>
    /// <param name="packet">要序列化的 <see cref="EFT.LootSyncPacket"/></param>
    public static void PutLootSyncStruct(this NetDataWriter writer, EFT.LootSyncPacket packet)
    {
        writer.Put(packet.Id);
        writer.PutUnmanaged(packet.Position);
        writer.PutUnmanaged(packet.Rotation);
        writer.Put(packet.Done);

        if (!packet.Done)
        {
            writer.PutUnmanaged(packet.Velocity);
            writer.PutUnmanaged(packet.AngularVelocity);
        }
    }

    /// <summary>
    /// 读取 <see cref="EFT.LootSyncPacket"/>
    /// </summary>
    /// <param name="reader">用于读取数据的 reader</param>
    /// <returns>反序列化后的 <see cref="EFT.LootSyncPacket"/></returns>
    public static EFT.LootSyncPacket GetLootSyncStruct(this NetDataReader reader)
    {
        EFT.LootSyncPacket data = new()
        {
            Id = reader.GetInt(),
            Position = reader.GetUnmanaged<Vector3>(),
            Rotation = reader.GetUnmanaged<Quaternion>(),
            Done = reader.GetBool()
        };

        if (!data.Done)
        {
            data.Velocity = reader.GetUnmanaged<Vector3>();
            data.AngularVelocity = reader.GetUnmanaged<Vector3>();
        }

        return data;
    }

    /// <summary>
    /// 将一个 float 值压缩为固定位宽的整数并写入 NetDataWriter。<br/>
    /// 为提升网络效率支持 8 位或 16 位压缩。
    /// </summary>
    /// <param name="writer">网络数据 writer</param>
    /// <param name="value">要压缩的 float 值</param>
    /// <param name="min">预期的 float 最小值（钳制下界）</param>
    /// <param name="max">预期的 float 最大值（钳制上界）</param>
    /// <param name="compression">用于压缩的位数（8 或 16）</param>
    public static void PutPackedFloat(this NetDataWriter writer, float value, float min, float max, EFloatCompression compression = EFloatCompression.Low)
    {
        // 将输入值钳制到预期范围内
        var clamped = Mathf.Clamp(value, min, max);

        // 计算该位宽下的最大整数值（例如 8 位为 255，16 位为 65535）
        var maxInt = (1 << (int)compression) - 1;

        // 归一化后用四舍五入量化，以获得更好的精度
        var quantized = Mathf.RoundToInt((clamped - min) / (max - min) * maxInt);

        if (compression is EFloatCompression.High)
        {
            writer.Put((byte)quantized);
        }
        else
        {
            writer.Put((ushort)quantized);
        }
    }

    /// <summary>
    /// 从 NetDataReader 读取由固定位宽整数打包的压缩 float 值。<br/>
    /// 支持 8 位或 16 位解压。
    /// </summary>
    /// <param name="reader">网络数据 reader</param>
    /// <param name="min">预期的 float 最小值（用于确定解压区间）</param>
    /// <param name="max">预期的 float 最大值（用于确定解压区间）</param>
    /// <param name="compression">压缩时使用的位数（8 或 16）</param>
    /// <returns>解压后的 float 值。</returns>
    public static float GetPackedFloat(this NetDataReader reader, float min, float max, EFloatCompression compression = EFloatCompression.Low)
    {
        var maxInt = (1 << (int)compression) - 1;
        int quantized;

        if (compression is EFloatCompression.High)
        {
            quantized = reader.GetByte();
        }
        else
        {
            quantized = reader.GetUShort();
        }

        var normalized = (float)quantized / maxInt;
        return min + (normalized * (max - min));
    }

    /// <summary>
    /// 将整数值从指定的输入区间缩放至目标字节区间并写入
    /// </summary>
    /// <param name="writer">要写入的 NetDataWriter</param>
    /// <param name="value">要缩放的整数值</param>
    /// <param name="minValue">输入整数区间的最小值</param>
    /// <param name="maxValue">输入整数区间的最大值</param>
    public static void PutPackedInt(this NetDataWriter writer, int value, int minValue, int maxValue)
    {
        const int minTarget = 0;
        const int maxTarget = byte.MaxValue;

        var clampedValue = Mathf.Clamp(value, minValue, maxValue) - minValue;
        var rangeInput = maxValue - minValue;
        const int rangeTarget = maxTarget - minTarget;

        var normalized = (float)clampedValue / rangeInput;
        var scaled = (int)(minTarget + (normalized * rangeTarget));

        var result = (byte)Mathf.Clamp(scaled, minTarget, maxTarget);
        writer.Put(result);
    }

    /// <summary>
    /// 将字节值从指定的输入区间缩放至目标整数区间
    /// </summary>
    /// <param name="reader">要从中读取的 NetDataReader</param>
    /// <param name="minTarget">目标整数区间的最小值</param>
    /// <param name="maxTarget">目标整数区间的最大值</param>
    /// <returns>目标区间内缩放后的整数值</returns>
    public static int GetPackedInt(this NetDataReader reader, int minTarget, int maxTarget)
    {
        const int minValue = 0;
        const int maxValue = byte.MaxValue;

        var value = reader.GetByte();

        const int rangeInput = maxValue - minValue;
        var clampedValue = Mathf.Clamp(value, minValue, maxValue) - minValue;
        float rangeTarget = maxTarget - minTarget;

        var normalized = (float)clampedValue / rangeInput;
        return (int)(minTarget + (normalized * rangeTarget));
    }

    /// <summary>
    /// 写入头部旋转角度，使用高压缩率压缩每个 <see cref="float"/>。
    /// </summary>
    /// <param name="writer">要写入的网络数据 writer</param>
    /// <param name="rotation">头部旋转</param>
    public static void PutHeadRotation(this NetDataWriter writer, Vector2 rotation)
    {
        writer.PutPackedFloat(rotation.x, -50f, 20f, EFloatCompression.High);
        writer.PutPackedFloat(rotation.y, -40f, 40f, EFloatCompression.High);
    }

    /// <summary>
    /// 读取头部旋转角度，使用高压缩率解压每个 <see cref="float"/>。
    /// </summary>
    /// <param name="reader">要从中读取的网络数据 reader</param>
    /// <returns>表示头部旋转的 <see cref="Vector2"/></returns>
    public static Vector2 GetHeadRotation(this NetDataReader reader)
    {
        return new()
        {
            x = reader.GetPackedFloat(-50f, 20f, EFloatCompression.High),
            y = reader.GetPackedFloat(-40f, 40f, EFloatCompression.High)
        };
    }

    /// <summary>
    /// 写入移动方向 <see cref="Vector2"/>，使用高压缩率压缩每个 <see cref="float"/>。
    /// </summary>
    /// <param name="writer">要写入的网络数据 writer</param>
    /// <param name="movementDirection">移动方向</param>
    public static void PutMovementDirection(this NetDataWriter writer, Vector2 movementDirection)
    {
        writer.PutPackedFloat(movementDirection.x, -1f, 1f, EFloatCompression.High);
        writer.PutPackedFloat(movementDirection.y, -1f, 1f, EFloatCompression.High);
    }

    /// <summary>
    /// 读取移动方向 <see cref="Vector2"/>，使用高压缩率解压每个 <see cref="float"/>。
    /// </summary>
    /// <param name="reader">要从中读取的网络数据 reader</param>
    /// <returns>表示移动方向的 <see cref="Vector2"/></returns>
    public static Vector2 GetMovementDirection(this NetDataReader reader)
    {
        return new()
        {
            x = reader.GetPackedFloat(-1f, 1f, EFloatCompression.High),
            y = reader.GetPackedFloat(-1f, 1f, EFloatCompression.High)
        };
    }

    /// <summary>
    /// 写入玩家旋转的 <see cref="Vector2"/>
    /// </summary>
    /// <param name="writer">要写入的 <see cref="NetDataWriter"/> 实例</param>
    /// <param name="rotation">
    /// 表示旋转的 <see cref="Vector2"/>，其中：
    /// <list type="bullet">
    /// <item><description><c>x</c> 以原始 float 存储</description></item>
    /// <item><description><c>y</c> 使用 <see cref="EFloatCompression.High"/> 以 [-90, 90] 范围内的压缩 float 存储</description></item>
    /// </list>
    /// </param>
    public static void PutRotation(this NetDataWriter writer, Vector2 rotation)
    {
        writer.Put(rotation.x);
        writer.PutPackedFloat(rotation.y, -90f, 90f, EFloatCompression.High);
    }

    /// <summary>
    /// 序列化 <see cref="GameDateTime"/>
    /// </summary>
    /// <param name="writer">用于写入的 writer</param>
    /// <param name="gameDateTime">要序列化的 <see cref="GameDateTime"/></param>
    public static void PutGameDateTime(this NetDataWriter writer, GameDateTime gameDateTime)
    {
        writer.Put(gameDateTime.StatedRealDateTime.ToBinary());
        writer.Put(gameDateTime.StatedGameDateTime.ToBinary());
        writer.Put(gameDateTime.TimeFactor);
        writer.Put(gameDateTime.TimeFactorMod);
    }

    /// <summary>
    /// 反序列化 <see cref="GameDateTime"/>
    /// </summary>
    /// <param name="reader">用于读取的 reader</param>
    /// <returns>带数据的新 <see cref="GameDateTime"/></returns>
    public static GameDateTime GetGameDateTime(this NetDataReader reader)
    {
        return new GameDateTime(new Il2CppSystem.DateTime(DateTime.FromBinary(reader.GetLong()).Ticks), new Il2CppSystem.DateTime(DateTime.FromBinary(reader.GetLong()).Ticks), reader.GetFloat())
        {
            TimeFactorMod = reader.GetFloat()
        };
    }

    /// <summary>
    /// 读取玩家旋转的 <see cref="Vector2"/>
    /// </summary>
    /// <param name="reader">要从中读取的 <see cref="NetDataReader"/> 实例</param>
    /// <returns>
    /// 一个 <see cref="Vector2"/>，其中：
    /// <list type="bullet">
    /// <item><description><c>x</c> 以原始 float 读取。</description></item>
    /// <item><description><c>y</c> 使用 <see cref="EFloatCompression.High"/> 以 [-90, 90] 范围内的压缩 float 读取。</description></item>
    /// </list>
    /// </returns>
    public static Vector2 GetRotation(this NetDataReader reader)
    {
        return new()
        {
            x = reader.GetFloat(),
            y = reader.GetPackedFloat(-90f, 90f, EFloatCompression.High)
        };
    }

    /// <summary>
    /// 将 TimeSpan 结构序列化为 64 位整数 tick 计数写入 writer 流。
    /// </summary>
    public static void PutTimeSpan(this NetDataWriter writer, TimeSpan target)
    {
        writer.Put(target.Ticks);
    }

    /// <summary>
    /// 从 reader 流反序列化 64 位整数 tick 计数并重建 TimeSpan 结构。
    /// </summary>
    public static TimeSpan GetTimeSpan(this NetDataReader reader)
    {
        var ticks = reader.GetLong();
        return new TimeSpan(ticks);
    }

    public static void PutFirearmSubPacket(this NetDataWriter writer, ISubPacket packet, EFirearmSubPacketType type)
    {
        switch (type)
        {
            case EFirearmSubPacketType.ShotInfo:
            case EFirearmSubPacketType.ChangeFireMode:
            case EFirearmSubPacketType.ToggleAim:
            case EFirearmSubPacketType.ToggleLightStates:
            case EFirearmSubPacketType.ToggleScopeStates:
            case EFirearmSubPacketType.ToggleInventory:
            case EFirearmSubPacketType.LeftStanceChange:
            case EFirearmSubPacketType.ReloadMag:
            case EFirearmSubPacketType.QuickReloadMag:
            case EFirearmSubPacketType.ReloadWithAmmo:
            case EFirearmSubPacketType.CylinderMag:
            case EFirearmSubPacketType.ReloadLauncher:
            case EFirearmSubPacketType.ReloadBarrels:
            case EFirearmSubPacketType.Grenade:
            case EFirearmSubPacketType.CompassChange:
            case EFirearmSubPacketType.Knife:
            case EFirearmSubPacketType.FlareShot:
            case EFirearmSubPacketType.RocketShot:
            case EFirearmSubPacketType.RollCylinder:
                packet.Serialize(writer);
                break;

            case EFirearmSubPacketType.ToggleLauncher:
            case EFirearmSubPacketType.CancelGrenade:
            case EFirearmSubPacketType.ReloadBoltAction:
            case EFirearmSubPacketType.UnderbarrelSightingRangeUp:
            case EFirearmSubPacketType.UnderbarrelSightingRangeDown:
            case EFirearmSubPacketType.ToggleBipod:
            case EFirearmSubPacketType.ExamineWeapon:
            case EFirearmSubPacketType.CheckAmmo:
            case EFirearmSubPacketType.CheckChamber:
            case EFirearmSubPacketType.CheckFireMode:
            case EFirearmSubPacketType.Loot:
                break;
            default:
                FikaGlobals.LogError("Type was outside of bounds!");
                break;
        }
    }
    public static IRequestPacket GetRequestSubPacket(this NetDataReader reader, ERequestSubPacketType type)
    {
        switch (type)
        {
            case ERequestSubPacketType.SpawnPoint:
                return new SpawnPointRequest(reader);
            case ERequestSubPacketType.Weather:
                return new WeatherRequest(reader);
            case ERequestSubPacketType.Exfiltration:
                return new ExfiltrationRequest(reader);
            case ERequestSubPacketType.TraderServices:
                return new TraderServicesRequest(reader);
            case ERequestSubPacketType.CharacterSync:
                return new RequestCharactersPacket(reader);
            default:
                FikaGlobals.LogError("Type was outside of bounds!");
                return null;
        }
    }
}

/// <summary>
/// 指定打包浮点值时使用的位数。
/// </summary>
/// <remarks>
/// 此枚举用于定义位打包 float 表示的精度。<br/>
/// 位数越高精度越高，但占用更多带宽。
/// </remarks>
public enum EFloatCompression : byte
{
    /// <summary>
    /// 使用 8 位（1 字节）打包该 float。<br/>
    /// 精度较低，但可减少带宽占用。
    /// </summary>
    High = 8,

    /// <summary>
    /// 使用 16 位（2 字节）打包该 float。<br/>
    /// 精度高于 8 位，但占用更多带宽。
    /// </summary>
    Low = 16
}
