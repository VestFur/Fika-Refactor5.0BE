namespace Fika.Core.Networking.Packets.Player.Common;

/// <summary>
/// 表示与固定式设备或固定位置相关的命令。
/// </summary>
public enum EStationaryCommand : byte
{
    /// <summary>
    /// 占用固定式炮塔。
    /// </summary>
    Occupy,

    /// <summary>
    /// 离开固定式炮塔。
    /// </summary>
    Leave,

    /// <summary>
    /// 对炮塔的访问被拒绝。
    /// </summary>
    Denied
}
