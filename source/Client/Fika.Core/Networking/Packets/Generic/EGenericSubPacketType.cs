namespace Fika.Core.Networking.Packets.Generic;

/// <summary>
/// 表示与具体系统无关的通用游戏级数据包事件。
/// </summary>
public enum EGenericSubPacketType : byte
{
    /// <summary>
    /// 客户端撤离事件。
    /// </summary>
    ClientExtract,

    /// <summary>
    /// 客户端已连接到服务器。
    /// </summary>
    ClientConnected,

    /// <summary>
    /// 客户端已从服务器断开连接。
    /// </summary>
    ClientDisconnected,

    /// <summary>
    /// 撤离倒计时事件。
    /// </summary>
    ExfilCountdown,

    /// <summary>
    /// 更新后端数据。
    /// </summary>
    UpdateBackendData,

    /// <summary>
    /// 发现秘密撤离点事件。
    /// </summary>
    SecretExfilFound,

    /// <summary>
    /// 边界区域事件。
    /// </summary>
    BorderZone,

    /// <summary>
    /// 地雷被触发。
    /// </summary>
    Mine,

    /// <summary>
    /// 解除绊线事件。
    /// </summary>
    DisarmTripwire,

    /// <summary>
    /// 玩家消音状态发生变化。
    /// </summary>
    MuffledState,

    /// <summary>
    /// 生成 BTR 载具。
    /// </summary>
    SpawnBTR,

    /// <summary>
    /// 与服务器同步角色
    /// </summary>
    CharacterSync,

    /// <summary>
    /// 库存操作
    /// </summary>
    InventoryOperation,

    /// <summary>
    /// 操作的回调状态
    /// </summary>
    OperationCallback,

    /// <summary>
    /// 发送 ping
    /// </summary>
    Ping,

    /// <summary>
    /// 发送角色数据
    /// </summary>
    SendCharacter,

    /// <summary>
    /// 可同步物品，例如窗户或灯
    /// </summary>
    SyncableItem,

    /// <summary>
    /// 所有客户端已加载 AI
    /// </summary>
    SpawnAI,

    /// <summary>商人对话入口占用。</summary>
    DialogEntry

    /// <summary>
    /// 火车同步事件（已注释掉）。
    /// </summary>
    // TrainSync,

    /// <summary>
    /// 商人服务通知事件（已注释掉）。
    /// </summary>
    // TraderServiceNotification,
}
