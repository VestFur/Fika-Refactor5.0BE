using Il2CppSystem.Collections.Generic;
using EFT.GlobalEvents;
using Fika.Core.Main.Utils;
using EFT;
using JsonType;
using System;

namespace Fika.Core.Networking.Packets.Communication;

public class TransitEventPacket : INetSerializable
{
    /// <summary>
    /// 没有任何接收方会映射到载荷的事件类型字节。<see cref="Serialize"/> 在声明的类型载荷
    /// 无法构建时会写入它:一个只被部分写入的主体
    /// 会让 NetPacketProcessor.ReadAllPackets 把剩余字节解析成另一个
    /// 数据包,而这种误解析会报告出一个随机的包类型。
    /// </summary>
    private const byte NoPayloadEventType = byte.MaxValue;

    /// <summary>
    /// 原生转发初始化视作「任意玩家」的 raid id
    /// (EFT TransitInteractionControllerAbstractClass.IsTargetPlayer)。一次没有可用
    /// 载荷的初始化会带上这个 id 发出,因此没有任何对端会把它应用到自己
    /// 的转发状态上,同时数据包流保持对齐。
    /// </summary>
    internal const int AnyPlayerRaidId = -1;

    private const int MaxTransitPointCount = 1024;
    private const int MaxTransitProfileCount = 256;
    private const int MaxTransitEntryCount = 1024;

    public ETransitEventType EventType;
    public SyncEventFromServer TransitEvent;
    public int TransitId;
    public int PlayerId;
    public int[] EnabledPointIds = [];
    public int RegisteredPointId = -1;
    public string KeyId;
    public EDateTime SelectedTime;
    public LocationTransit Destination;

    public void Deserialize(NetDataReader reader)
    {
        EventType = (ETransitEventType)reader.GetByte();
        try
        {
            DeserializeBody(reader);
        }
        catch (Exception ex)
        {
            // Deserialize 运行在 FikaClient.Update 内部(NetManager.PollEvents)。一个我们
            // 无法读完的数据包会让 NetPacketProcessor.ReadAllPackets 继续
            // 把它的剩余字节解析成更多数据包,因此本数据包的其余部分被丢弃。
            FikaGlobals.LogError($"[Transit] Dropping a malformed {EventType} transit packet: {ex}");
            TransitEvent = null;
            DrainRemaining(reader);
        }
    }

    private void DeserializeBody(NetDataReader reader)
    {
        switch (EventType)
        {
            case ETransitEventType.Init:
                {
                    ReadInitializationPayload(reader.GetByteArray());
                    var count = reader.GetInt();
                    if (count < 0 || count > MaxTransitPointCount)
                    {
                        FikaGlobals.LogError($"[Transit] Init packet reported {count} transit points; the rest of the packet is dropped");
                        EnabledPointIds = [];
                        RegisteredPointId = -1;
                        KeyId = string.Empty;
                        SelectedTime = default;
                        DrainRemaining(reader);
                        break;
                    }
                    var ids = new int[count];
                    for (var i = 0; i < count; i++)
                    {
                        ids[i] = reader.GetInt();
                    }
                    EnabledPointIds = ids;
                    RegisteredPointId = reader.GetInt();
                    KeyId = reader.GetString();
                    SelectedTime = (EDateTime)reader.GetInt();
                }
                break;
            case ETransitEventType.GroupTimer:
                {
                    var pointId = reader.GetInt();
                    var timerAmount = reader.GetInt();
                    if (timerAmount < 0 || timerAmount > MaxTransitEntryCount)
                    {
                        FikaGlobals.LogError($"[Transit] GroupTimer packet reported {timerAmount} timers; the rest of the packet is dropped");
                        DrainRemaining(reader);
                        break;
                    }
                    TransitGroupTimerEvent timerEvent = new()
                    {
                        PointId = pointId
                    };
                    Dictionary<int, ushort> timers = new();
                    for (var i = 0; i < timerAmount; i++)
                    {
                        var key = reader.GetInt();
                        var value = reader.GetUShort();
#if DEBUG
                        FikaGlobals.LogWarning($"GroupTimer: int: {key}, ushort: {value}");
#endif
                        if (!timers.ContainsKey(key)) timers.Add(key, value);
                    }
                    timerEvent.Timers = timers;
                    TransitEvent = timerEvent;
                }
                break;
            case ETransitEventType.GroupSize:
                {
                    var pointId = reader.GetInt();
                    var sizeAmount = reader.GetInt();
                    if (sizeAmount < 0 || sizeAmount > MaxTransitEntryCount)
                    {
                        FikaGlobals.LogError($"[Transit] GroupSize packet reported {sizeAmount} sizes; the rest of the packet is dropped");
                        DrainRemaining(reader);
                        break;
                    }
                    TransitGroupSizeEvent sizeEvent = new() { PointId = pointId };
                    Dictionary<int, byte> sizes = new();
                    for (var i = 0; i < sizeAmount; i++)
                    {
                        var key = reader.GetInt();
                        var value = reader.GetByte();
#if DEBUG
                        FikaGlobals.LogWarning($"GroupSize: int: {key}, byte: {value}");
#endif
                        if (!sizes.ContainsKey(key)) sizes.Add(key, value);
                    }
                    sizeEvent.Sizes = sizes;
                    TransitEvent = sizeEvent;
                }
                break;
            case ETransitEventType.Interaction:
                {
                    TransitEvent = (TransitInteractionEvent)new()
                    {
                        PlayerRaidId = reader.GetInt(),
                        PointId = reader.GetInt(),
                        Type = (TransitInteractionEvent.EType)reader.GetByte()
                    };
                }
                break;
            case ETransitEventType.Messages:
                {
                    var messagesAmount = reader.GetInt();
                    if (messagesAmount < 0 || messagesAmount > MaxTransitEntryCount)
                    {
                        FikaGlobals.LogError($"[Transit] Messages packet reported {messagesAmount} messages; the rest of the packet is dropped");
                        DrainRemaining(reader);
                        break;
                    }
                    TransitMessagesEvent messagesEvent = new();
                    Dictionary<int, TransitMessagesEvent.EType> messages = new();
                    for (var i = 0; i < messagesAmount; i++)
                    {
                        var key = reader.GetInt();
                        var value = (TransitMessagesEvent.EType)reader.GetByte();
                        if (!messages.ContainsKey(key)) messages.Add(key, value);
                    }
                    messagesEvent.Messages = messages;
                    TransitEvent = messagesEvent;
                }
                break;
            case ETransitEventType.Extract:
                TransitId = reader.GetInt();
                PlayerId = reader.GetInt();
                Destination = ReadDestination(reader);
                break;
            default:
                // NoPayloadEventType 以及本构建不认识的每一种类型:发送方
                // 声明了一个它无法构建的载荷,因此没有可读的内容。
                break;
        }
    }

    /// <summary>
    /// 重建 Init 包的原生 <see cref="TransitInitEvent"/>。缺失的载荷,或对共享原生缓冲区
    /// 来说过大的载荷,都会让 <see cref="TransitEvent"/> 保持为 null:
    /// 接收方会检查载荷是否可用(ClientTransitController.ReceiveSnapshot),并忽略
    /// 该快照,而不是应用一个未初始化的快照。
    /// </summary>
    private void ReadInitializationPayload(byte[] data)
    {
        var nativeReader = NetworkUtils.EventDataReader;
        var bufferLength = nativeReader?.Buffer?.Length ?? 0;
        if (data.Length == 0 || bufferLength == 0 || data.Length > bufferLength)
        {
            FikaGlobals.LogError($"[Transit] Init packet carried {data.Length} native bytes for a {bufferLength} byte buffer; the native initialization is ignored");
            TransitEvent = null;
            return;
        }

        nativeReader.Reset();
        Array.Clear(nativeReader.Buffer);
        Array.Copy(data, nativeReader.Buffer, data.Length);
        var initEvent = new TransitInitEvent();
        try
        {
            initEvent.Deserialize(ref nativeReader);
            TransitEvent = initEvent;
        }
        catch (Exception ex)
        {
            FikaGlobals.LogError($"[Transit] The native transit initialization could not be deserialized; it is ignored: {ex}");
            TransitEvent = null;
        }
    }

    public void Serialize(NetDataWriter writer)
    {
        switch (EventType)
        {
            case ETransitEventType.Init:
                {
                    var initEvent = TransitEvent?.TryCast<TransitInitEvent>();
                    if (initEvent == null)
                    {
                        FikaGlobals.LogError($"[Transit] Init packet had no usable TransitInitEvent payload ({(TransitEvent == null ? "null" : TransitEvent.GetType().Name)}); an empty initialization is sent instead");
                        initEvent = CreateEmptyInitialization();
                    }
                    writer.Put((byte)ETransitEventType.Init);
                    writer.PutByteArray(SerializeInitialization(initEvent));
                    var enabledPointIds = EnabledPointIds;
                    writer.Put(enabledPointIds?.Length ?? 0);
                    if (enabledPointIds != null)
                    {
                        foreach (var id in enabledPointIds)
                        {
                            writer.Put(id);
                        }
                    }
                    writer.Put(RegisteredPointId);
                    writer.Put(KeyId ?? string.Empty);
                    writer.Put((int)SelectedTime);
                }
                break;
            case ETransitEventType.GroupTimer:
                {
                    if (TransitEvent?.TryCast<TransitGroupTimerEvent>() is not { } timerEvent || timerEvent.Timers == null)
                    {
                        WriteNoPayload(writer, nameof(ETransitEventType.GroupTimer));
                        break;
                    }
                    writer.Put((byte)ETransitEventType.GroupTimer);
                    writer.Put(timerEvent.PointId);
                    writer.Put(timerEvent.Timers.Count);
                    foreach (var timer in timerEvent.Timers)
                    {
                        writer.Put(timer.Key);
                        writer.Put(timer.Value);
                    }
                }
                break;
            case ETransitEventType.GroupSize:
                {
                    if (TransitEvent?.TryCast<TransitGroupSizeEvent>() is not { } sizeEvent || sizeEvent.Sizes == null)
                    {
                        WriteNoPayload(writer, nameof(ETransitEventType.GroupSize));
                        break;
                    }
                    writer.Put((byte)ETransitEventType.GroupSize);
                    writer.Put(sizeEvent.PointId);
                    writer.Put(sizeEvent.Sizes.Count);
                    foreach (var size in sizeEvent.Sizes)
                    {
                        writer.Put(size.Key);
                        writer.Put(size.Value);
                    }
                }
                break;
            case ETransitEventType.Interaction:
                {
                    if (TransitEvent?.TryCast<TransitInteractionEvent>() is not { } interactionEvent)
                    {
                        WriteNoPayload(writer, nameof(ETransitEventType.Interaction));
                        break;
                    }
                    writer.Put((byte)ETransitEventType.Interaction);
                    writer.Put(interactionEvent.PlayerRaidId);
                    writer.Put(interactionEvent.PointId);
                    writer.Put((byte)interactionEvent.Type);
                }
                break;
            case ETransitEventType.Messages:
                {
                    if (TransitEvent?.TryCast<TransitMessagesEvent>() is not { } messagesEvent || messagesEvent.Messages == null)
                    {
                        WriteNoPayload(writer, nameof(ETransitEventType.Messages));
                        break;
                    }
                    writer.Put((byte)ETransitEventType.Messages);
                    writer.Put(messagesEvent.Messages.Count);
                    foreach (var message in messagesEvent.Messages)
                    {
                        writer.Put(message.Key);
                        writer.Put((byte)message.Value);
                    }
                }
                break;
            case ETransitEventType.Extract:
                writer.Put((byte)ETransitEventType.Extract);
                writer.Put(TransitId);
                writer.Put(PlayerId);
                WriteDestination(writer, Destination);
                break;
            default:
                writer.Put((byte)EventType);
                break;
        }
    }

    /// <summary>
    /// 序列化一次初始化的原生载荷。共享的原生写入器与一个
    /// 原生异常都会在这里被收住:HostGameController.SetupEventsAndExfils
    /// 在这次发送之后还会继续做撤离点设置、游戏状态和机器人激活,
    /// 因此一个逃逸出去的异常会中止整个 raid 启动。
    /// </summary>
    private static byte[] SerializeInitialization(TransitInitEvent initEvent)
    {
        var nativeWriter = NetworkUtils.EventDataWriter;
        var bufferLength = nativeWriter?.Buffer?.Length ?? 0;
        if (bufferLength == 0)
        {
            FikaGlobals.LogError("[Transit] The native event writer is unavailable; the initialization is sent without a native payload");
            return [];
        }

        try
        {
            if (initEvent.Points == null || initEvent.CompletedQuestRequirementMetByPointId == null)
            {
                // 原生字段为 null 正是让 TransitInitEvent.Serialize 在写入中途抛出原生
                // NullReferenceException 的原因。发送一个格式良好的空初始化。
                FikaGlobals.LogError($"[Transit] The initialization of raid id {initEvent.PlayerRaidId} had no point or requirement dictionary; an empty initialization is sent instead");
                initEvent = CreateEmptyInitialization();
            }

            nativeWriter.Reset();
            initEvent.Serialize(ref nativeWriter);
            nativeWriter.Flush();
            var written = nativeWriter.BytesWritten;
            if (written <= 0 || written > bufferLength)
            {
                FikaGlobals.LogError($"[Transit] The native initialization writer reported {written} bytes for a {bufferLength} byte buffer; the native payload is dropped");
                return [];
            }

            var data = new byte[written];
            Array.Copy(nativeWriter.Buffer, data, written);
            return data;
        }
        catch (Exception ex)
        {
            FikaGlobals.LogError($"[Transit] The native transit initialization could not be serialized; the native payload is dropped: {ex}");
            return [];
        }
    }

    /// <summary>
    /// 一个格式良好但没有内容的初始化。raid id 用的是原生的「任意玩家」
    /// 哨兵值,因此原生转发控制器绝不会把它应用到本地玩家身上。
    /// </summary>
    private static TransitInitEvent CreateEmptyInitialization()
    {
        return new TransitInitEvent
        {
            PlayerRaidId = AnyPlayerRaidId, Points = new Dictionary<int, LocationSettings.Location.TransitParameters>(),
            CompletedQuestRequirementMetByPointId = new Dictionary<int, bool>(),
            TransitionCount = 0, EventPlayer = false
        };
    }

    /// <summary>
    /// 所声明的事件类型没有载荷。写入一个过短的主体会让接收方
    /// 把该主体剩下的字节解析成另一个数据包,因此该包会写成一个
    /// 每个接收方都会忽略的事件类型。
    /// </summary>
    private static void WriteNoPayload(NetDataWriter writer, string eventName)
    {
        FikaGlobals.LogError($"[Transit] {eventName} packet had no usable payload; it is sent as an event the receiver ignores");
        writer.Put(NoPayloadEventType);
    }

    private static void DrainRemaining(NetDataReader reader)
    {
        var available = reader.AvailableBytes;
        if (available > 0)
        {
            reader.SkipBytes(available);
        }
    }

    private static void WriteDestination(NetDataWriter writer, LocationTransit data)
    {
        if (data == null)
        {
            // 对端会用自己那些转发点来校验目的地
            // (ClientTransitController.HandleClientExtract),并丢弃空目的地。
            FikaGlobals.LogError("[Transit] Extract packet had no destination; an empty destination is sent instead");
            writer.Put(string.Empty);
            writer.Put(0);
            writer.Put(string.Empty);
            writer.Put(string.Empty);
            writer.Put(string.Empty);
            writer.Put(0);
            writer.Put(0);
            writer.Put(0);
            writer.Put(0);
            return;
        }

        writer.Put(data.hash ?? string.Empty);
        writer.Put(data.playersCount);
        writer.Put(data.ip ?? string.Empty);
        writer.Put(data.location ?? string.Empty);
        writer.Put(data.transitionRaidId ?? string.Empty);
        writer.Put((int)data.raidMode);
        writer.Put((int)data.side);
        writer.Put((int)data.dayTime);
        var profiles = data.profiles;
        writer.Put(profiles?.Count ?? 0);
        if (profiles == null)
        {
            return;
        }
        foreach (var entry in profiles)
        {
            var profileKey = entry.Value;
            writer.Put(entry.Key ?? string.Empty);
            writer.Put(profileKey?._id ?? string.Empty);
            writer.Put(profileKey?.keyId ?? string.Empty);
            writer.Put(profileKey?.isSolo ?? false);
        }
    }

    private static LocationTransit ReadDestination(NetDataReader reader)
    {
        var data = new LocationTransit
        {
            hash = reader.GetString(), playersCount = reader.GetInt(), ip = reader.GetString(),
            location = reader.GetString(), transitionRaidId = reader.GetString(),
            raidMode = (ERaidMode)reader.GetInt(), side = (ESideType)reader.GetInt(),
            dayTime = (EDateTime)reader.GetInt(), profiles = new()
        };
        var count = reader.GetInt();
        if (count < 0 || count > MaxTransitProfileCount)
        {
            // 目的地是本数据包的最后一个字段,因此不可用的尾部会
            // 就在这里被丢弃,而不会被解析成另一个数据包。
            FikaGlobals.LogError($"[Transit] Extract packet reported {count} transit profiles; the destination is treated as empty");
            DrainRemaining(reader);
            return data;
        }
        for (var i = 0; i < count; i++)
        {
            var profileId = reader.GetString();
            var id = reader.GetString();
            var keyId = reader.GetString();
            var isSolo = reader.GetBool();
            if (!data.profiles.ContainsKey(profileId))
            {
                data.profiles.Add(profileId, new ProfileKey
                {
                    _id = id, keyId = keyId, isSolo = isSolo
                });
            }
        }
        return data;
    }

    public enum ETransitEventType
    {
        Init,
        GroupTimer,
        GroupSize,
        Interaction,
        Messages,
        Extract,

        /// <summary>
        /// 「主机已开始加载转发重载的下一张地图」(analysis/transit-reload-sync)。
        /// 本数据包刻意不携带载荷:仅类型字节就是信号。构建不认识该值的
        /// 接收方读到那个字节,发现是未知类型,于是忽略这个包——
        /// 这正是序列化器的 default 分支为它写下的内容。该契约的两半都必须
        /// 保持原样:<see cref="Serialize"/> 的 default 分支继续只写裸类型字节,而
        /// <see cref="DeserializeBody"/> 的 default 分支继续不读任何东西。
        /// </summary>
        ReloadGo
    }
}
