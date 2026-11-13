using EFT;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.LiteNetLib;
using System;

namespace Fika.Core.Networking.Packets.Communication;

/// <summary>
/// 当一名客户端自己的玩家进入或离开一个中转点触发区域时，由该客户端发给主机；
/// 另外还有一个「交互请求本身就证明了在场」的
/// 变体，用来为触发钩子兜底。
/// <para>
/// 为什么需要这个包：原生的 <c>TransitController._playersInTransitZone</c>
/// 映射（以及每一处读取它的检查）只由
/// <c>ClientTransitController.PlayerEnter/PlayerExit</c> 写入，而它们只对
/// 该机器的本地玩家运行。在主机上这意味着主机自己的
/// 玩家；客户端的玩家永远不会被注册，所以
/// <c>FikaHostTransitController.CheckForPlayers</c> 一直回答
/// <c>TransitMessagesEvent.EType.NonAllTeammates</c> 并拒绝每一次中转。
/// 这个包把客户端的本地区域状态镜像到主机上。
/// </para>
/// <para>
/// 信任：主机从不听信客户端自称的身份 —— 只有在该包中的
/// <c>peer.Player.ProfileId</c> 与所声称的 profile 一致时才会应用它
/// （见 <see cref="TransitZonePresence.HostHandler"/>）—— 而且它从不授予
/// 对一个点位的访问权。在场只满足「所有人都在区域内」
/// 这道门禁；访问权、进入需求、已完成任务的需求以及
/// 队伍人数上限仍由主机按玩家逐一校验。
/// </para>
/// <para>
/// 线路兼容性：这是一个新的数据包类型。Fika 的 LiteNetLib 分发以数据包类型名的
/// CRC16 为键（<c>NetPacketProcessor.GetShortHash</c>），
/// 而未知的键会在对端的逻辑线程内部抛出 ParseException
/// （<c>NetPacketProcessor.cs:91</c>），这意味着一个不认识该类型
/// 的对端无法「安全地忽略」它。因此兼容性门禁是
/// 握手：<see cref="FikaProtocol.Revision"/> 随这个包一起被提升，所以
/// 两个对端只有在都理解它时才能交换数据包，而较旧的对端
/// 会在连接请求时就被拒绝，而不是在 raid 中途。
/// </para>
/// </summary>
public sealed class TransitZonePresencePacket : INetSerializable
{
    /// <summary>玩家所在的中转点（<see cref="EFT.Interactive.TransitPoint.parameters"/>.id）。</summary>
    public int PointId;

    /// <summary>客户端所上报玩家的 profile。在主机上会与发送该包的对端做交叉校验。</summary>
    public string ProfileId;

    /// <summary>客户端观察到的内容。</summary>
    public ETransitZonePresenceKind Kind;

    public void Deserialize(NetDataReader reader)
    {
        // 严格按下面写出的顺序读取字段。
        PointId = reader.GetInt();
        ProfileId = reader.GetString();
        Kind = (ETransitZonePresenceKind)reader.GetByte();
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(PointId);
        writer.Put(ProfileId ?? string.Empty);
        writer.Put((byte)Kind);
    }

    public enum ETransitZonePresenceKind : byte
    {
        /// <summary>玩家离开了该区域。</summary>
        Left = 0,
        /// <summary>玩家在区域内（原生 PlayerEnter）。</summary>
        In = 1,
        /// <summary>玩家可能位于某个区域内；交互请求用它作为漏掉触发器时的回退。</summary>
        InUncertain = 2
    }
}

/// <summary>
/// <see cref="TransitZonePresencePacket"/> 的主机侧管道：数据包与
/// 它的处理器住在一起，这样共享的网络文件各自只需要一行
/// 注册代码。
/// </summary>
public static class TransitZonePresence
{
    /// <summary>低频诊断：每个玩家每个点位一行。</summary>
    private static readonly System.Collections.Generic.HashSet<string> LoggedPresence = [];

    /// <summary>主机：在服务器的分发表上注册该数据包。</summary>
    public static void RegisterOnServer(FikaServer server)
    {
        server.RegisterPacket<TransitZonePresencePacket, NetPeer>(HostHandler);
    }

    /// <summary>
    /// 客户端：在分发表上注册该数据包，使一个被提升为主机的客户端
    /// （或回环测试）能够解码它。客户端只发送。
    /// </summary>
    public static void RegisterOnClient(FikaClient client)
    {
        client.RegisterPacket<TransitZonePresencePacket>(PeerHandler);
    }

    /// <summary>客户端侧接收：关于这张映射，主机是权威。</summary>
    private static void PeerHandler(TransitZonePresencePacket packet)
    {
        
    }

    /// <summary>
    /// 主机侧接收。以对端自己的玩家来决定该包可以代表哪个 profile；
    /// 不匹配则丢弃，因此客户端无法注册或驱逐
    /// 另一名玩家的在场状态。
    /// </summary>
    private static void HostHandler(TransitZonePresencePacket packet, NetPeer peer)
    {
        try
        {
            var server = FikaSingleton<IFikaNetworkManager>.Instance as FikaServer;
            if (server?.CoopHandler == null)
            {
                // 尚未在 raid 中；在世界存在之前，区域包没有任何意义。
                
                return;
            }

            var sender = peer?.Player?.ProfileId;
            if (string.IsNullOrEmpty(sender) || !string.Equals(sender, packet.ProfileId, StringComparison.Ordinal))
            {
                
                return;
            }

            var world = Comfort.Common.Singleton<GameWorld>.Instance;
            var controller = Fika.Core.Main.BaseClasses.FikaWorldControllers.Find<FikaHostTransitController>(world?.TransitController);
            if (controller == null)
            {
                
                return;
            }

            controller.OnRemotePlayerZonePresence(packet.ProfileId, packet.PointId, packet.Kind);
        }
        catch (Exception exception)
        {
            
        }
    }

    /// <summary>
    /// 在主机上把一名玩家记录为位于某个点位内，并在日志中
    /// 每个玩家每个点位恰好证明一次。本次调用改变了主机视图时
    /// 返回 true。
    /// </summary>
    public static bool RecordInZone(System.Collections.Generic.Dictionary<string, int> playersInTransitZone, string profileId,
        int pointId, string source)
    {
        var changed = !playersInTransitZone.TryGetValue(profileId, out var current) || current != pointId;
        playersInTransitZone[profileId] = pointId;
        if (changed && LoggedPresence.Add(profileId + "@" + pointId))
        {
            
        }
        return changed;
    }

    /// <summary>从主机的区域映射中移除一名玩家；移除了条目时返回 true。</summary>
    public static bool RemoveFromZone(System.Collections.Generic.Dictionary<string, int> playersInTransitZone, string profileId, string source)
    {
        if (!playersInTransitZone.Remove(profileId)) return false;
        
        return true;
    }

    /// <summary>地图/raid 变化时丢弃整个日志去重表。</summary>
    public static void ResetLog()
    {
        LoggedPresence.Clear();
    }
}
