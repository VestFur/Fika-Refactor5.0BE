using System.Collections.Generic;
using Comfort.Common;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.Generic.SubPackets;

public sealed class CharacterSyncPacket : IPoolSubPacket
{
    private CharacterSyncPacket()
    {

    }

    public static CharacterSyncPacket CreateInstance()
    {
        return new();
    }

    public static CharacterSyncPacket FromValue(Dictionary<int, FikaPlayer> players)
    {
        var packet = GenericSubPacketPoolManager.Instance.GetPacket<CharacterSyncPacket>(EGenericSubPacketType.CharacterSync);
        foreach ((var netid, _) in players)
        {
            packet.PlayerIds.Add(netid);
        }
        return packet;
    }

    public List<int> PlayerIds = new(32);

    public void Deserialize(NetDataReader reader)
    {
        var amount = reader.GetUShort();
        if (amount > 0)
        {
            for (var i = 0; i < amount; i++)
            {
                PlayerIds.Add(reader.GetInt());
            }
        }
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put((ushort)PlayerIds.Count);
        foreach (var netId in PlayerIds)
        {
            writer.Put(netId);
        }
    }

    /// <summary>
    /// 在接收客户端上应用主机下发的角色列表。
    ///
    /// 该包的方向由发送方决定，而不是由接收方上的某个标志决定：只有
    /// 主机会生成它（CoopHandler.SyncPlayersWithClients -> SendGenericPacket(CharacterSync, true)），
    /// 主机自身的接收路径拒绝转发它（FikaServer.IsHostOnlyGenericPacket 列出了
    /// CharacterSync），因此收到它并且持有 FikaClient 的机器就是被寻址的客户端。
    ///
    /// 这里过去用 FikaBackendUtils.IsClient（即 ClientType == EClientType.Client）作为门槛，
    /// 否则就记录一条错误。ClientType 是一个*会话*标志，突袭启动失败时
    /// NetManagerUtils.AbortFailedJoin 会清除它：在 2026-10-11 18:58 的那次客户端运行放弃中转
    /// 突袭之后，该标志已是 None，而仍在连接的客户端继续接收主机的
    /// 周期性同步，于是每个包都产生了这行错误
    /// "Received CharacterSyncPacket as server"（共 20 条，LogOutput.log:4401-4420），尽管
    /// 该包正是发给那个客户端的，并且只是被忽略而已。真正的问题在于是否
    /// 存在一个活的客户端，这正是下面这个检查所问的；标志只被上报，不被遵从。
    /// </summary>
    public void Execute(FikaPlayer player = null)
    {
        var client = FikaSingleton<FikaClient>.Instantiated ? FikaSingleton<FikaClient>.Instance : null;
        if (client == null)
        {
            
            return;
        }

        if (!FikaBackendUtils.IsClient)
        {
            
        }

        client.OnCharacterSyncPacketReceived(this);
    }

    public void Dispose()
    {
        PlayerIds.Clear();
    }
}
