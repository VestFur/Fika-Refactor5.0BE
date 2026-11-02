namespace Fika.Core.Networking.Packets.Player.Common;

/// <summary>
/// 表示上下文相关交互（proceed 动作）的类型。
/// </summary>
public enum EProceedType : byte
{
    /// <summary>
    /// 未持有物品（空手）。
    /// </summary>
    EmptyHands,

    /// <summary>
    /// 食物类物品。
    /// </summary>
    FoodClass,

    /// <summary>
    /// 手雷类物品。
    /// </summary>
    GrenadeClass,

    /// <summary>
    /// 药品类物品。
    /// </summary>
    MedsClass,

    /// <summary>
    /// 快速投掷手雷动作。
    /// </summary>
    QuickGrenadeThrow,

    /// <summary>
    /// 快速刀击动作。
    /// </summary>
    QuickKnifeKick,

    /// <summary>
    /// 物品的快速使用动作。
    /// </summary>
    QuickUse,

    /// <summary>
    /// 可使用物品交互。
    /// </summary>
    UsableItem,

    /// <summary>
    /// 持有武器。
    /// </summary>
    Weapon,

    /// <summary>
    /// 持有固定武器（炮塔）。
    /// </summary>
    Stationary,

    /// <summary>
    /// 持有刀。
    /// </summary>
    Knife,

    /// <summary>
    /// 尝试执行 proceed 动作。
    /// </summary>
    TryProceed
}
