namespace Fika.Core.Networking.Packets;

/// <summary>
/// 表示用于请求环境或服务器状态数据的数据包类型。
/// </summary>
public enum ERequestSubPacketType : byte
{
    /// <summary>
    /// 出生点请求。
    /// </summary>
    SpawnPoint,

    /// <summary>
    /// 天气更新请求。
    /// </summary>
    Weather,

    /// <summary>
    /// 撤离请求。
    /// </summary>
    Exfiltration,

    /// <summary>
    /// 请求商人服务信息。
    /// </summary>
    TraderServices,

    /// <summary>
    /// 请求角色同步。
    /// </summary>
    CharacterSync
}
