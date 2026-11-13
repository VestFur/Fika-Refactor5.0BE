namespace Fika.Core.Networking.Snapshotting;

/// <summary>
/// 使用指数移动平均（EMA）管理服务器与本地时钟之间的同步。 <br/>
/// 它会平滑网络抖动与时钟漂移，为插值提供稳定的时间偏移。
/// </summary>
public struct TimeSyncEMA
{
    /// <summary>
    /// EMA 计算的平滑因子。
    /// </summary>
    /// <remarks>
    /// 建议使用 0.1f 以获得稳定性；更高的值对网络变化反应更快，但可能引入抖动。
    /// </remarks>
    private const float _alpha = 0.1f;

    /// <summary> 服务器时间与本地时间之间当前的平滑差值。 </summary>
    private double _emaOffset;

    /// <summary> 跟踪是否已取得第一个样本以初始化基准偏移。 </summary>
    private bool _initialized;

    /// <summary>
    /// 获取当前平滑后的偏移。
    /// </summary>
    /// <remarks>
    /// 将其加到 <see cref="Time.unscaledTimeAsDouble"/> 上即可估算当前服务器时间。
    /// </remarks>
    public readonly double SmoothOffset
    {
        get
        {
            return _emaOffset;
        }
    }

    /// <summary>
    /// 用来自网络包的新时间样本更新移动平均。
    /// </summary>
    /// <param name="serverTime">服务器／发送方提供的远端时间戳。</param>
    /// <param name="localTime">收到该包时的本地系统时间。</param>
    public void Update(double serverTime, double localTime)
    {
        // 计算两个时钟之间的原始差值
        var currentOffset = serverTime - localTime;

        // 用第一个样本初始化 EMA，避免从零开始
        if (!_initialized)
        {
            _emaOffset = currentOffset;
            _initialized = true;
            return;
        }

        // 指数移动平均公式：Sn = αY + (1-α)Sn-1
        // 即用 alpha 加权新样本，用 (1-alpha) 加权历史
        _emaOffset = (_alpha * currentOffset) + ((1f - _alpha) * _emaOffset);
    }
}