namespace Fika.Core.Networking.Packets.FirearmController;

/// <summary>
/// 描述与枪械和战斗动作相关的子包类型。
/// </summary>
public enum EFirearmSubPacketType : byte
{
    /// <summary>
    /// 一次开火的信息。
    /// </summary>
    ShotInfo,

    /// <summary>
    /// 切换枪械的射击模式。
    /// </summary>
    ChangeFireMode,

    /// <summary>
    /// 切换瞄准状态。
    /// </summary>
    ToggleAim,

    /// <summary>
    /// 检视武器。
    /// </summary>
    ExamineWeapon,

    /// <summary>
    /// 检查弹药数量。
    /// </summary>
    CheckAmmo,

    /// <summary>
    /// 检查枪膛状态。
    /// </summary>
    CheckChamber,

    /// <summary>
    /// 检查当前的射击模式。
    /// </summary>
    CheckFireMode,

    /// <summary>
    /// 切换枪械上的灯具状态。
    /// </summary>
    ToggleLightStates,

    /// <summary>
    /// 切换枪械上的瞄具状态。
    /// </summary>
    ToggleScopeStates,

    /// <summary>
    /// 切换榴弹发射器配件。
    /// </summary>
    ToggleLauncher,

    /// <summary>
    /// 切换背包视图或状态。
    /// </summary>
    ToggleInventory,

    /// <summary>
    /// 执行搜刮动作。
    /// </summary>
    Loot,

    /// <summary>
    /// 重新装填弹匣。
    /// </summary>
    ReloadMag,

    /// <summary>
    /// 对弹匣执行快速重装。
    /// </summary>
    QuickReloadMag,

    /// <summary>
    /// 使用弹药重新装填。
    /// </summary>
    ReloadWithAmmo,

    /// <summary>
    /// 重新装填转轮弹巢。
    /// </summary>
    CylinderMag,

    /// <summary>
    /// 重新装填榴弹发射器。
    /// </summary>
    ReloadLauncher,

    /// <summary>
    /// 重新装填枪管。
    /// </summary>
    ReloadBarrels,

    /// <summary>
    /// 手雷相关动作。
    /// </summary>
    Grenade,

    /// <summary>
    /// 取消手雷动作。
    /// </summary>
    CancelGrenade,

    /// <summary>
    /// 切换指南针。
    /// </summary>
    CompassChange,

    /// <summary>
    /// 刀具相关动作。
    /// </summary>
    Knife,

    /// <summary>
    /// 已发射信号弹。
    /// </summary>
    FlareShot,

    /// <summary>
    /// 已发射火箭弹。
    /// </summary>
    RocketShot,

    /// <summary>
    /// 重新装填栓动式枪械。
    /// </summary>
    ReloadBoltAction,

    /// <summary>
    /// 转动转轮弹巢。
    /// </summary>
    RollCylinder,

    /// <summary>
    /// 增大枪管下挂瞄具的标定距离。
    /// </summary>
    UnderbarrelSightingRangeUp,

    /// <summary>
    /// 减小枪管下挂瞄具的标定距离。
    /// </summary>
    UnderbarrelSightingRangeDown,

    /// <summary>
    /// 切换两脚架的展开状态。
    /// </summary>
    ToggleBipod,

    /// <summary>
    /// 左侧持枪姿态发生改变。
    /// </summary>
    LeftStanceChange
}
