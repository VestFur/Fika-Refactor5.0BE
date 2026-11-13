namespace Fika.Core.Networking.Snapshotting;

public enum EBufferState : byte
{
    /// <summary>找到了有效的 'from' 与 'to' 边界。</summary>
    Interpolating,
    /// <summary>渲染时间超过最新的数据包。需要做投影。</summary>
    Extrapolating,
    /// <summary>缓冲区为空，或数据太旧无法投影。</summary>
    Stale
}