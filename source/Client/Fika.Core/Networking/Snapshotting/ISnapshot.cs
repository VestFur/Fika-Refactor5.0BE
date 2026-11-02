namespace Fika.Core.Networking.Snapshotting;

/// <summary>
/// 定义同步网络快照所需的最小数据。
/// </summary>
public interface ISnapshot
{
    /// <summary> 捕获该状态时服务器的时间戳。 </summary>
    double RemoteTime { get; }
    /// <summary> 收到该数据包时的本地系统时间。 </summary>
    double LocalTime { get; }
}