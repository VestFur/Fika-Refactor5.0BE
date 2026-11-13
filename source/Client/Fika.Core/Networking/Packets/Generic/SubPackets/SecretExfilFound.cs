using EFT.GlobalEvents;
using EFT.Interactive.SecretExfiltrations;
using Fika.Core.Main.Players;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.Generic.SubPackets;

public sealed class SecretExfilFound : IPoolSubPacket
{
    public string GroupId;
    public string TeamId;
    public string ExitName;

    private SecretExfilFound() { }

    public static SecretExfilFound CreateInstance()
    {
        return new SecretExfilFound();
    }

    public static SecretExfilFound FromValue(string groupId, string teamId, string exitName)
    {
        var packet = GenericSubPacketPoolManager.Instance.GetPacket<SecretExfilFound>(EGenericSubPacketType.SecretExfilFound);
        packet.GroupId = groupId;
        packet.TeamId = teamId;
        packet.ExitName = exitName;
        return packet;
    }

    // 保留这个双参数辅助方法，以便与针对 Mono 客户端编译的
    // 扩展保持源码兼容。SPT 5.0 的原生事件区分
    // 队伍与小队，因此旧调用会回退到小队（group）的值。
    public static SecretExfilFound FromValue(string groupId, string exitName) =>
        FromValue(groupId, groupId, exitName);

    public void Execute(FikaPlayer player = null)
    {
        GlobalEventsController.Instance
            .CreateCommonEvent<SecretExfiltrationPointFoundShareEvent>()
            .Invoke(GroupId, TeamId, ExitName);
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(GroupId);
        writer.Put(TeamId);
        writer.Put(ExitName);
    }

    public void Deserialize(NetDataReader reader)
    {
        GroupId = reader.GetString();
        TeamId = reader.GetString();
        ExitName = reader.GetString();
    }

    public void Dispose()
    {
        GroupId = null;
        TeamId = null;
        ExitName = null;
    }
}
