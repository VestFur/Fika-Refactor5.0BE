// © 2026 Lacyway All Rights Reserved

using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.Generic;

/// <summary>
/// 用于承载多种不同内容以减少数据包膨胀的包
/// </summary>
public sealed class GenericPacket : INetReusable
{
    public int NetId;
    public EGenericSubPacketType Type;
    public IPoolSubPacket SubPacket;

    public void Execute()
    {
        SubPacket?.Execute();
    }

    public void Deserialize(NetDataReader reader)
    {
        NetId = reader.GetInt();
        Type = reader.GetEnum<EGenericSubPacketType>();
        SubPacket = GenericSubPacketPoolManager.Instance.GetPacket<IPoolSubPacket>(Type);
        // (2026-10-26 W22) 未知的子包类型（对端使用不同构建）：池会报告该情况并返回
        // null。跳过方法体可让该包保持惰性，而不会在网络轮询循环里抛出异常。
        if (SubPacket == null)
        {
            reader.SkipBytes(reader.AvailableBytes);
            Type = default;
            return;
        }

        SubPacket.Deserialize(reader);
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(NetId);
        writer.PutEnum(Type);
        SubPacket?.Serialize(writer);
    }

    public void Clear()
    {
        if (SubPacket != null)
        {
            GenericSubPacketPoolManager.Instance.ReturnPacket(Type, SubPacket);
            SubPacket = null;
            Type = default;
        }
    }

    public void Flush()
    {
        GenericSubPacketPoolManager.Instance.ReturnPacket(Type, SubPacket);
        SubPacket = null;
        Type = default;
    }
}
