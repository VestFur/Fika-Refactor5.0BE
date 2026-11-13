using Fika.Core.Main.Components;
using Fika.Core.Networking.LiteNetLib;
using System;

namespace Fika.Core.Networking.Packets.Communication;

/// <summary>
/// 绿色信号弹撤离点(「signal flare」出口)的按玩家激活。
/// <para>
/// 原生模型(已对照 47888 互操作成员转储验证,见
/// analysis/flare-exfil-verification):绿色信号弹对应
/// <c>EFT.InventoryLogic.FlareEventType.ExitActivate</c>。信号弹成功会在本地触发
/// <c>EFT.GlobalEvents.FlareSuccessEvent</c>,该机器上每一个
/// <c>EFT.Interactive.FlareShootDetectorZone</c> 都会消费它:它把
/// <b>开火玩家的 profile id</b> 加进 <c>_shotPlayerProfiles</c>,并触发其
/// 输出触发器,随后撤离点的按玩家条件
/// (<c>ExfiltrationRequirement.Met(Player, ExfiltrationPoint)</c>,即手动激活那一条)
/// 便只对那名玩家给出满足。由于客户端信号弹的成功事件从不会在主机上
/// 被重放,客户端的信号弹无法激活任何东西,
/// 而 Fika 确实同步过的点级状态却让整个小队都能用这个
/// 撤离点。
/// </para>
/// <para>
/// 本数据包通过线路恢复这种按玩家的绑定:
/// <list type="bullet">
/// <item><see cref="EKind.Report"/>:客户端 -&gt; 主机,「我在检测区 ZoneId、位置 FlarePosition
/// 打出了一发 ExitActivate 信号弹,那里的撤离点是 PointName」。</item>
/// <item><see cref="EKind.Grant"/>:主机 -&gt; 所有对端,「ProfileId 在 PointName」
/// 被允许」。非 ProfileId 的接收方绝不会应用它(并会在日志里记下这次忽略),
/// 因此撤离点绝不会为没有开火的玩家开放。</item>
/// </list>
/// </para>
/// <para>
/// 线路兼容性:这是一个新的数据包类型。Fika 的 LiteNetLib 派发以
/// 包类型名的 CRC16 作为键(<c>NetPacketProcessor.GetShortHash</c>),而未知的键
/// 会在反序列化时抛出,因此不认识该类型的对端无法忽略它。
/// 兼容性闸门就是握手:<see cref="FikaProtocol.Revision"/> 会与本数据包
/// 一起提升,从而在连接请求阶段就拒绝较旧的对端,
/// 而不是在 raid 中途。
/// </para>
/// </summary>
public sealed class FlareExfilActivationPacket : INetSerializable
{
    /// <summary>Report(客户端到主机)或 Grant(主机到每个对端)。</summary>
    public EKind Kind;

    /// <summary>打出信号弹的玩家的 profile id。主机上会与发送对端做交叉核对。</summary>
    public string ProfileId = string.Empty;

    /// <summary>该信号弹所激活的撤离点名称(<c>ExfiltrationPoint.Settings.Name</c>)。</summary>
    public string PointName = string.Empty;

    /// <summary>信号弹发射所在检测区的 <c>FlareShootDetectorZone.zoneID</c>。</summary>
    public string ZoneId = string.Empty;

    /// <summary>
    /// 小队共享激活时开火者的组 id(原版小队通道,
    /// <c>EZoneEventType.PlayerByPartyAddedInShotList</c>)。当激活只绑定到开火者本人时为
    /// 空:此时接收方只有在 <see cref="ProfileId"/> 是自己时才应用它。
    /// </summary>
    public string SquadId = string.Empty;

    /// <summary>原生信号弹簿记使用数值 raid id(<c>FlareShootZoneEvent.PlayerRaidID</c>)。</summary>
    public int RaidId;

    /// <summary>信号弹发射的世界坐标,主机用它重新推导并校验撤离点。</summary>
    public Vector3 FlarePosition;

    /// <summary>
    /// 信号弹为开火者燃烧的位置,也就是退出链被启动的那一点。只在 grant 中设置;
    /// <see cref="Vector3.zero"/> 表示「没有携带信号弹位置」,此时接收方不会重新应用
    /// 任何东西。整个小队都需要它,因为信号弹撤离的原生场景链
    /// (<c>EFT.GameTriggers.TriggerFlareSuccess</c> -&gt; <c>TriggersEmitter</c> -&gt; 地图的处理器)会在
    /// 触发信号弹事件的那台机器上播放动画,而那台机器只有开火者有。
    /// </summary>
    public Vector3 FlareDirection;

    public void Serialize(NetDataWriter writer)
    {
        // 按 Deserialize 读取字段的完全相同顺序写入字段。
        writer.Put((byte)Kind);
        writer.Put(ProfileId ?? string.Empty);
        writer.Put(PointName ?? string.Empty);
        writer.Put(ZoneId ?? string.Empty);
        writer.Put(SquadId ?? string.Empty);
        writer.Put(RaidId);
        writer.PutUnmanaged(FlarePosition);
        writer.PutUnmanaged(FlareDirection);
    }

    public void Deserialize(NetDataReader reader)
    {
        Kind = (EKind)reader.GetByte();
        ProfileId = reader.GetString();
        PointName = reader.GetString();
        ZoneId = reader.GetString();
        SquadId = reader.GetString();
        RaidId = reader.GetInt();
        FlarePosition = reader.GetUnmanaged<Vector3>();
        FlareDirection = reader.GetUnmanaged<Vector3>();
    }

    public enum EKind : byte
    {
        /// <summary>客户端到主机:本地玩家在某个检测区内打出了一发 ExitActivate 信号弹。</summary>
        Report = 1,

        /// <summary>主机到各对端:指定 profile 在指定撤离点被允许。</summary>
        Grant = 2
    }

    public static void RegisterOnServer(FikaServer server)
    {
        server.RegisterPacket<FlareExfilActivationPacket, NetPeer>(HostHandler);
    }

    public static void RegisterOnClient(FikaClient client)
    {
        client.RegisterPacket<FlareExfilActivationPacket>(PeerHandler);
    }

    private static void HostHandler(FlareExfilActivationPacket packet, NetPeer peer)
    {
        if (packet == null)
        {
            return;
        }

        FikaFlareExfil.HostHandleReport(packet, peer);
    }

    private static void PeerHandler(FlareExfilActivationPacket packet)
    {
        if (packet == null)
        {
            return;
        }

        FikaFlareExfil.ClientHandlePacket(packet);
    }
}