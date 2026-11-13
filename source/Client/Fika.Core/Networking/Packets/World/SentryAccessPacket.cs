namespace Fika.Core.Networking.Packets.World;

/// <summary>
/// BJ (analysis/terminal-rauf-card)：原生链路每台机器各自维护的 RAUF 访问决策中的一行——
/// 「该玩家已通过终端的刷卡检查」。
///
/// 原生：刷卡结束于 <c>GlobalEventDispatcher.OnTerminalTransitAccessGained</c>，其唯一的
/// 消费者是 <c>SentryBossLogic.OnTerminalAccessGained(Player)</c>，它把玩家 id 写入
/// 哨兵的私有 <c>_accessedPlayerIds</c>。哨兵的敌意在每台机器上都会重新计算
/// （<c>BotGroupHostility.UpdateHostilityStatusForAllPlayers</c> -> <c>BotHostilityService</c> ->
/// <c>BossSentryHostilityRule</c> -> <c>IAccessBossLogic.HaveAccess</c>），因此哨兵从未见过该事件的
/// 机器会持续攻击。此包改为把同一事实传达到每台机器。
/// </summary>
public struct SentryAccessPacket() : INetSerializable
{
    /// <summary>检查通过的那个玩家的档案 id（跨机器稳定）。</summary>
    public string ProfileId;
    /// <summary>玩家在发送方机器上的战局 id，未知时为 -1。</summary>
    public int RaidId = -1;
    /// <summary>true = 检查通过，false = 该状态已丢失（离开区域／死亡）。</summary>
    public bool Granted = true;
    /// <summary>该行的来源机器：0 = 主机，1 = 客户端。</summary>
    public byte FromClient;

    /// <summary>
    /// BT (analysis/terminal-hostility-graph)：权威端从自身原生状态读回的判定
    /// （<c>IAccessBossLogic.HaveAccess</c> 对多少哨兵回答是、对多少回答否），以及它的多少个
    /// 哨兵组通过 <c>AlliesBySentryAcces</c> 接收了该事实。客户端发送零值；
    /// 由权威端填充它们，因此回显携带的是拥有这些 bot 的机器的答案。
    /// </summary>
    public int HostAccessYes;
    public int HostAccessNo;
    public int HostAllies;

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(ProfileId);
        writer.Put(RaidId);
        writer.Put(Granted);
        writer.Put(FromClient);
        writer.Put(HostAccessYes);
        writer.Put(HostAccessNo);
        writer.Put(HostAllies);
    }

    public void Deserialize(NetDataReader reader)
    {
        ProfileId = reader.GetString();
        RaidId = reader.GetInt();
        Granted = reader.GetBool();
        FromClient = reader.GetByte();
        HostAccessYes = reader.GetInt();
        HostAccessNo = reader.GetInt();
        HostAllies = reader.GetInt();
    }
}
