namespace Fika.Core.Networking.Packets.Player.Common;

/// <summary>
/// 表示通用玩家动作的常用子数据包类型。
/// </summary>
public enum ECommonSubPacketType : byte
{
    /// <summary>
    /// 玩家语音。
    /// </summary>
    Phrase,

    /// <summary>
    /// 与世界环境的交互。
    /// </summary>
    WorldInteraction,

    /// <summary>
    /// 与容器的交互。
    /// </summary>
    ContainerInteraction,

    /// <summary>
    /// Proceed 动作。
    /// </summary>
    Proceed,

    /// <summary>
    /// 头灯被切换或更新。
    /// </summary>
    HeadLights,

    /// <summary>
    /// 库存内容发生变化。
    /// </summary>
    InventoryChanged,

    /// <summary>
    /// 物品被丢弃。
    /// </summary>
    Drop,

    /// <summary>
    /// 驻留交互。
    /// </summary>
    Stationary,

    /// <summary>
    /// 翻越或攀爬动作。
    /// </summary>
    Vault,

    /// <summary>
    /// 通用交互事件。
    /// </summary>
    Interaction,

    /// <summary>
    /// 上车或下车动作。
    /// </summary>
    Mounting,

    /// <summary>
    /// 造成伤害
    /// </summary>
    Damage,

    /// <summary>
    /// 护甲耐久更新
    /// </summary>
    ArmorDamage,

    /// <summary>
    /// 同步生命值
    /// </summary>
    HealthSync,

    /// <summary>
    /// 正在使用可用物品
    /// </summary>
    UsableItem,

    /// <summary>
    /// 同步倒地状态
    /// </summary>
    DownedSync,

    /// <summary>
    /// 有玩家被救起
    /// </summary>
    RevivedPlayer,

    /// <summary>
    /// 有玩家正在被救起,或救援被取消
    /// </summary>
    RevivingPlayer,

    /// <summary>录音机手部动作。</summary>
    RecorderAction,

    /// <summary>快进本地已过时的手部操作。</summary>
    FastForwardOutdated
}
