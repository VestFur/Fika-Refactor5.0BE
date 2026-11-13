using System;
using Fika.Core.Networking.LiteNetLib.Utils;

namespace Fika.Core.Networking;

/// <summary>在接受任何玩家或世界数据包之前拒绝不兼容的线路布局。</summary>
internal static class FikaProtocol
{
    // 每当数据包布局或原生描述符 schema 变化时就递增此值。
    // v18 为 SPT 5 的圣诞寒冷事件加入 Kolotun 状态同步。
    // 把它保留在连接键中，使得仍为上一线路布局的对端
    // 在能用新字段消费数据包之前就被拒绝。
    // v19 允许玩家 scav 进入一个已经在运行中的战局。
    // v20 加入 TransitZonePresencePacket，客户端用它告知主机
    // 自己的玩家进入了或离开了某个中转点。Fika 的数据包分发以类型名的
    // CRC16 为键，并对未知键抛异常，因此不认识该类型的对端
    // 无法解出它；握手必须改为拒绝这样的对端。
    // v21 加入 FlareExfilActivationPacket，客户端用它报告在 FlareShootDetectorZone
    // 内出现的绿色（"ExitActivate"）信号弹，使主机能把信号弹撤离点
    // 授予开枪者（默认还包括其小队），而不是
    // 让该点对所有人开放。
    // v22 加入 FlareExfilActivationPacket.FlareDirection（开枪者的位置）以及
    // 无检测区地图（Icebreaker 的直升机出口）的场景触发器解析。
    // 该数据包多了一个字段，因此仍为 v21 布局的对端
    // 必须在解码之前被拒绝。
    // v23 移除了断开重连功能（analysis/remove-reconnect）：重连
    // 连接键已删除，敲门握手也不再携带其重连标志，因此
    // 仍为 v22 布局的对端必须在读取更短的握手之前被拒绝。
    internal const int Revision = 23;
    internal const string JoinKey = "fika.spt50.il2cpp.v23.join";
    internal const string ScavJoinKey = "fika.spt50.il2cpp.v23.scavjoin";

    internal static string ConnectionKey(bool scavLateJoin = false) =>
        scavLateJoin ? ScavJoinKey : JoinKey;

    internal static bool TryReadConnection(NetDataReader data, bool raidStarted, out string rejection) =>
        TryReadConnection(data, raidStarted, out rejection, out _);

    internal static bool TryReadConnection(NetDataReader data, bool raidStarted, out string rejection, out bool scavLateJoin)
    {
        rejection = "Incompatible Fika protocol. All peers must use the same SPT 5 IL2CPP build.";
        scavLateJoin = false;
        if (!data.TryGetString(out var key) || data.AvailableBytes != 0) return false;
        if (!string.Equals(key, JoinKey, StringComparison.Ordinal) &&
            !string.Equals(key, ScavJoinKey, StringComparison.Ordinal)) return false;

        scavLateJoin = string.Equals(key, ScavJoinKey, StringComparison.Ordinal);

        // 玩家 scav 可以进入已在运行中的战局；普通加入
        // 仍需等待主机启动战局。
        if (raidStarted && string.Equals(key, JoinKey, StringComparison.Ordinal))
        {
            rejection = "Raid already started";
            return false;
        }
        rejection = null;
        return true;
    }
}