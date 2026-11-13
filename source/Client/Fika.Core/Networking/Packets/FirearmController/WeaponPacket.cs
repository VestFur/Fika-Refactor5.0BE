using Fika.Core.Main.Players;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.FirearmController;

public sealed class WeaponPacket : INetReusable
{
    public int NetId;
    public EFirearmSubPacketType Type;
    public IPoolSubPacket SubPacket;

    public void Execute(FikaPlayer player = null)
    {
        SubPacket?.Execute(player);
    }

    public void Deserialize(NetDataReader reader)
    {
        NetId = reader.GetInt();
        Type = reader.GetEnum<EFirearmSubPacketType>();
        SubPacket = FirearmSubPacketPoolManager.Instance.GetPacket<IPoolSubPacket>(Type);
        // (2026-10-26 W24) 未知的子包类型（对端构建版本不同）：对象池会报告它并返回
        // null。跳过正文可让该包保持惰性，而不是在网络轮询循环内部抛出异常。
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
            FirearmSubPacketPoolManager.Instance.ReturnPacket(Type, SubPacket);
            SubPacket = null;
            Type = default;
        }
    }

    public void Flush()
    {
        FirearmSubPacketPoolManager.Instance.ReturnPacket(Type, SubPacket);
        SubPacket = null;
        Type = default;
    }
}
