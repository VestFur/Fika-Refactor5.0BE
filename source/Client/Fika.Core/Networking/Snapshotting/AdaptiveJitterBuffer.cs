using System;

namespace Fika.Core.Networking.Snapshotting;

/// <summary>
/// 根据网络抖动动态计算所需的插值延迟。 <br/>
/// 旨在稳定连接时尽量降低延迟，在抖动尖峰时扩大缓冲区以避免卡顿。
/// </summary>
/// <remarks>
/// 采用非对称 EMA 策略：快速扩张以吸收延迟尖峰，但收缩缓慢 
/// 以在恢复期间保持视觉稳定。
/// </remarks>
public struct AdaptiveJitterBuffer
{
    /// <summary> 最小插值延迟（50ms）。即使在完美连接下，我们也需要一个小缓冲区来在两帧之间插值。 </summary>
    private const double _baseDelay = 0.05d;

    /// <summary> 允许的最大插值延迟（250ms）。用于限制延迟，避免玩家落后于真实状态太远。 </summary>
    private const double _maxDelay = 0.25d;

    /// <summary> 数据包到达时间的平滑方差。 </summary>
    private double _currentJitter;

    /// <summary>
    /// 获取用于 Snapshotter 的计算延迟。
    /// </summary>
    /// <remarks>
    /// （<see cref="_baseDelay"/> + <see cref="_currentJitter"/>），并钳制在 <see cref="_baseDelay"/> 与 <see cref="_maxDelay"/> 之间。
    /// </remarks>
    public readonly double CurrentDelay
    {
        get
        {
            return Math.Clamp(_baseDelay + _currentJitter, _baseDelay, _maxDelay);
        }
    }

    /// <summary>
    /// 根据数据包到达之间的差值更新抖动估计。
    /// </summary>
    /// <param name="packetArrivalDeltaTime">自上一个数据包到达以来实际经过的本地时间。</param>
    /// <param name="expectedTickDelta">服务器快照之间的预期时间（例如 20Hz 时为 0.05）。</param>
    public void Update(double packetArrivalDeltaTime, double expectedTickDelta)
    {
        // 计算与预期到达间隔的绝对偏差
        // 方差高说明网络路径不稳定（抖动）
        var variance = Math.Abs(packetArrivalDeltaTime - expectedTickDelta);

        // 非对称指数移动平均
        // 若方差在增大（尖峰），使用较高的 alpha（0.2）快速扩大缓冲区
        // 若方差在减小（恢复），使用较低的 alpha（0.01）缓慢收缩缓冲区
        var alpha = variance > _currentJitter ? 0.2d : 0.01d;

        _currentJitter = (alpha * variance) + ((1d - alpha) * _currentJitter);
    }
}