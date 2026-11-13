using System.Runtime.CompilerServices;

namespace Fika.Core.Networking.Snapshotting;

/// <summary>
/// 用于为复制同步快照
/// </summary>
public static class NetworkTimeSync
{
    /// <summary>
    /// 以 <see cref="double"/> 返回游戏自启动以来的当前时间
    /// </summary>
    public static double NetworkTime
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            return Time.unscaledTimeAsDouble;
        }
    }
}
