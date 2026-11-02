namespace Fika.Core.Networking.Packets.FirearmController;

/// <summary>
/// 表示手雷数据包动作的类型。
/// </summary>
public enum EGrenadePacketType : byte
{
    /// <summary>
    /// 无手雷动作。
    /// </summary>
    None,

    /// <summary>
    /// 握着手雷时检视武器。
    /// </summary>
    ExamineWeapon,

    /// <summary>
    /// 执行高抛投掷手雷。
    /// </summary>
    HighThrow,

    /// <summary>
    /// 执行低抛投掷手雷。
    /// </summary>
    LowThrow,

    /// <summary>
    /// 拉出保险环，准备高抛投掷。
    /// </summary>
    PullRingForHighThrow,

    /// <summary>
    /// 拉出保险环，准备低抛投掷。
    /// </summary>
    PullRingForLowThrow
}
