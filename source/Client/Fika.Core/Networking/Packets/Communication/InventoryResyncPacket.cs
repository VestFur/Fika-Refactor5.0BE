using EFT;
using Fika.Core.Main.Utils;

namespace Fika.Core.Networking.Packets.Communication;

/// <summary>
/// 服务器拒绝一次库存操作后发送的权威库存快照。回调 id 将它
/// 与本地操作关联起来。
/// </summary>
public struct InventoryResyncPacket : INetSerializable
{
    public int NetId;
    public ushort CallbackId;
    public InventoryDescriptor Descriptor;

    public void Deserialize(NetDataReader reader)
    {
        NetId = reader.GetInt();
        CallbackId = reader.GetUShort();
        Descriptor = reader.GetEFTInventoryDescriptor();
    }

    public readonly void Serialize(NetDataWriter writer)
    {
        writer.Put(NetId);
        writer.Put(CallbackId);
        writer.PutEFTInventoryDescriptor(Descriptor);
    }
}
