using Il2CppSystem.Collections.Generic;
using EFT.GlobalEvents;
using Fika.Core.Main.Utils;
using EFT;
using JsonType;
using System;

namespace Fika.Core.Networking.Packets.Communication;

public class TransitEventPacket : INetSerializable
{
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
        switch (EventType)
        {
            case ETransitEventType.Init:
                {
                    var data = reader.GetByteArray();
                    var nativeReader = NetworkUtils.EventDataReader;
                    nativeReader.Reset();
                    if (data.Length > nativeReader.Buffer.Length)
                        throw new InvalidOperationException("Transit initialization exceeds native buffer");
                    Array.Clear(nativeReader.Buffer);
                    Array.Copy(data, nativeReader.Buffer, data.Length);
                    var initEvent = new TransitInitEvent();
                    initEvent.Deserialize(ref nativeReader);
                    TransitEvent = initEvent;
                    var count = reader.GetInt();
                    if (count < 0 || count > 1024) throw new InvalidOperationException("Invalid transit point count");
                    EnabledPointIds = new int[count];
                    for (var i = 0; i < count; i++) EnabledPointIds[i] = reader.GetInt();
                    RegisteredPointId = reader.GetInt();
                    KeyId = reader.GetString();
                    SelectedTime = (EDateTime)reader.GetInt();
                }
                break;
            case ETransitEventType.GroupTimer:
                {
                    TransitGroupTimerEvent timerEvent = new()
                    {
                        PointId = reader.GetInt()
                    };
                    var timerAmount = reader.GetInt();
                    Dictionary<int, ushort> timers = new();
                    for (var i = 0; i < timerAmount; i++)
                    {
                        var key = reader.GetInt();
                        var value = reader.GetUShort();
#if DEBUG
                        FikaGlobals.LogWarning($"GroupTimer: int: {key}, ushort: {value}");
#endif
                        timers.Add(key, value);
                    }
                    timerEvent.Timers = timers;
                    TransitEvent = timerEvent;
                }
                break;
            case ETransitEventType.GroupSize:
                {
                    TransitGroupSizeEvent sizeEvent = new() { PointId = reader.GetInt() };
                    var sizeAmount = reader.GetInt();
                    Dictionary<int, byte> sizes = new();
                    for (var i = 0; i < sizeAmount; i++)
                    {
                        var key = reader.GetInt();
                        var value = reader.GetByte();
#if DEBUG
                        FikaGlobals.LogWarning($"GroupSize: int: {key}, byte: {value}");
#endif
                        sizes.Add(key, value);
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
                    TransitMessagesEvent messagesEvent = new();
                    var messagesAmount = reader.GetInt();
                    Dictionary<int, TransitMessagesEvent.EType> messages = new();
                    for (var i = 0; i < messagesAmount; i++)
                    {
                        var key = reader.GetInt();
                        var value = (TransitMessagesEvent.EType)reader.GetByte();
                        messages.Add(key, value);
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
                break;
        }
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put((byte)EventType);
        switch (EventType)
        {
            case ETransitEventType.Init:
                {
                    var nativeWriter = NetworkUtils.EventDataWriter;
                    nativeWriter.Reset();
                    TransitEvent.Cast<TransitInitEvent>().Serialize(ref nativeWriter);
                    nativeWriter.Flush();
                    var data = new byte[nativeWriter.BytesWritten];
                    Array.Copy(nativeWriter.Buffer, data, data.Length);
                    writer.PutByteArray(data);
                    writer.Put(EnabledPointIds.Length);
                    foreach (var id in EnabledPointIds) writer.Put(id);
                    writer.Put(RegisteredPointId);
                    writer.Put(KeyId ?? "");
                    writer.Put((int)SelectedTime);
                }
                break;
            case ETransitEventType.GroupTimer:
                {
                    if (TransitEvent?.TryCast<TransitGroupTimerEvent>() is { } timerEvent)
                    {
                        writer.Put(timerEvent.PointId);
                        writer.Put(timerEvent.Timers.Count);
                        foreach (var timer in timerEvent.Timers)
                        {
                            writer.Put(timer.Key);
                            writer.Put(timer.Value);
                        }
                    }
                }
                break;
            case ETransitEventType.GroupSize:
                {
                    if (TransitEvent?.TryCast<TransitGroupSizeEvent>() is { } sizeEvent)
                    {
                        writer.Put(sizeEvent.PointId);
                        writer.Put(sizeEvent.Sizes.Count);
                        foreach (var size in sizeEvent.Sizes)
                        {
                            writer.Put(size.Key);
                            writer.Put(size.Value);
                        }
                    }
                }
                break;
            case ETransitEventType.Interaction:
                {
                    if (TransitEvent?.TryCast<TransitInteractionEvent>() is { } interactionEvent)
                    {
                        writer.Put(interactionEvent.PlayerRaidId);
                        writer.Put(interactionEvent.PointId);
                        writer.Put((byte)interactionEvent.Type);
                    }
                }
                break;
            case ETransitEventType.Messages:
                {
                    if (TransitEvent?.TryCast<TransitMessagesEvent>() is { } messagesEvent)
                    {
                        writer.Put(messagesEvent.Messages.Count);
                        foreach (var message in messagesEvent.Messages)
                        {
                            writer.Put(message.Key);
                            writer.Put((byte)message.Value);
                        }
                    }
                }
                break;
            case ETransitEventType.Extract:
                writer.Put(TransitId);
                writer.Put(PlayerId);
                WriteDestination(writer, Destination);
                break;
            default:
                break;
        }
    }

    private static void WriteDestination(NetDataWriter writer, LocationTransit data)
    {
        writer.Put(data.hash);
        writer.Put(data.playersCount);
        writer.Put(data.ip ?? "");
        writer.Put(data.location);
        writer.Put(data.transitionRaidId);
        writer.Put((int)data.raidMode);
        writer.Put((int)data.side);
        writer.Put((int)data.dayTime);
        writer.Put(data.profiles.Count);
        foreach (var entry in data.profiles)
        {
            writer.Put(entry.Key);
            writer.Put(entry.Value._id);
            writer.Put(entry.Value.keyId ?? "");
            writer.Put(entry.Value.isSolo);
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
        if (count < 0 || count > 256) throw new InvalidOperationException("Invalid transit profile count");
        for (var i = 0; i < count; i++)
            data.profiles.Add(reader.GetString(), new ProfileKey
            {
                _id = reader.GetString(), keyId = reader.GetString(), isSolo = reader.GetBool()
            });
        return data;
    }

    public enum ETransitEventType
    {
        Init,
        GroupTimer,
        GroupSize,
        Interaction,
        Messages,
        Extract
    }
}
