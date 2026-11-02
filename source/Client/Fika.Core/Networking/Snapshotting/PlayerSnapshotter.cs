using System;
using System.Runtime.CompilerServices;
#if DEBUG
using Fika.Core.Main.Utils;
#endif

namespace Fika.Core.Networking.Snapshotting;

/// <summary>
/// 面向 <see cref="ISnapshot"/>（<typeparamref name="T"/>）插值的高性能、零分配环形缓冲区。<br/>
/// 使用位掩码实现 O(1) 插入，使用二分查找实现 O(log N) 采样。
/// </summary>
/// <remarks>
/// 深受 id Tech 3 网络模型（用于 Quake III Arena）启发 <br/>
/// 作者：<b>Lacyway</b>
/// </remarks>
public sealed class PlayerSnapshotter<T> where T : struct, ISnapshot
{
    /// <summary> 容量必须是 2 的幂，以便通过 <see cref="_mask"/> 做位掩码回绕。 </summary>
    private const int _capacity = 16;
    private const int _mask = _capacity - 1;

    /// <summary> 连续的快照内存块，以最大化 CPU L1/L2 缓存命中。 </summary>
    private readonly T[] _buffer = new T[_capacity];

    /// <summary> 时钟同步管理器。 </summary>
    private TimeSyncEMA _timeSync;

    /// <summary> 管理该实体的动态插值延迟。 </summary>
    private AdaptiveJitterBuffer _adaptiveJitterBuffer;

    /// <summary> 本对象生命周期内加入的快照总数。用于计算环形索引。 </summary>
    private long _totalAdded;
    private double _lastLocalTime;
    private double _lastRemoteTime;

    /// <summary>
    /// 把新的快照插入环形缓冲区，并更新时钟同步偏移。
    /// </summary>
    /// <param name="snapshot">从网络收到的状态数据。以 <see langword="in"/> 传入，以避免复制这个大结构体。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddSnapshot(in T snapshot)
    {
        if (_totalAdded > 0)
        {
            var newestIdx = (int)((_totalAdded - 1) & _mask);
            ref readonly var newestSnap = ref _buffer[newestIdx];

            // 序列校验：丢弃乱序或重复的包
            if (snapshot.RemoteTime <= newestSnap.RemoteTime)
            {
                return;
            }

            // 计算该包到达所耗费的实际时间与预期服务器间隔之间的差
            var localDelta = snapshot.LocalTime - _lastLocalTime;
            var remoteDelta = snapshot.RemoteTime - _lastRemoteTime;

            // 更新抖动方差
            _adaptiveJitterBuffer.Update(localDelta, remoteDelta);
        }

        // O(1) 插入：直接写入被掩码的索引
        _buffer[_totalAdded & _mask] = snapshot;
        _totalAdded++;

        // 更新 EMA 偏移（ServerTime - LocalTime）以同步播放时间线
        _timeSync.Update(snapshot.RemoteTime, snapshot.LocalTime);

        _lastLocalTime = snapshot.LocalTime;
        _lastRemoteTime = snapshot.RemoteTime;
    }

    /// <summary>
    /// 定位包围计算出的渲染时间的两个快照。<br/>
    /// 返回索引而非结构体副本，以优先保证 CPU 速度。
    /// </summary>
    /// <param name="localTime">当前本地系统时间（通常是 <see cref="Time.unscaledTimeAsDouble"/>）。</param>
    /// <param name="fromIdx">渲染时间之前那个快照的索引。</param>
    /// <param name="toIdx">渲染时间之后那个快照的索引。</param>
    /// <param name="t">两个快照之间 0-1 的插值系数（lerp 量）。</param>
    /// <returns>表示缓冲区当前处于插值、外推还是过期状态的 <see cref="EBufferState"/>。</returns>
    public EBufferState GetInterpolationIndices(double localTime, out int fromIdx, out int toIdx, out float t)
    {
        fromIdx = toIdx = -1;
        t = 0;

        var count = (int)Math.Min(_totalAdded, _capacity);
        if (count < 2)
        {
            return EBufferState.Stale;
        }

        // 从 AdaptiveJitterBuffer 取出动态延迟
        var renderTime = localTime + _timeSync.SmoothOffset - _adaptiveJitterBuffer.CurrentDelay;
        var offset = Math.Max(0, _totalAdded - _capacity);

        // 在搜索之前检查是否需要外推
        var newestIdx = (int)((offset + count - 1) & _mask);
        if (renderTime > _buffer[newestIdx].RemoteTime)
        {
            var timeSinceNewest = renderTime - _buffer[newestIdx].RemoteTime;

            // 把外推硬性限制在 100ms 以内。超过这个值，预测会偏离现实太远。
            if (timeSinceNewest > 0.1d)
            {
                return EBufferState.Stale;
            }

            fromIdx = newestIdx; // 使用该索引的数据 + velocity * timeSinceNewest
            t = (float)timeSinceNewest;
            return EBufferState.Extrapolating;
        }

        var low = 0;
        var high = count - 1;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (_buffer[(offset + mid) & _mask].RemoteTime < renderTime)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        // 如果 low 为 0，说明 renderTime 比我们缓冲的最旧快照还要老
        if (low == 0)
        {
            return EBufferState.Stale;
        }

        fromIdx = (int)((offset + low - 1) & _mask);
        toIdx = (int)((offset + low) & _mask);

        // 通过 ref readonly 访问，以避免在栈上复制大的 ISnapshot 结构体
        ref readonly var snapFrom = ref _buffer[fromIdx];
        ref readonly var snapTo = ref _buffer[toIdx];

        var range = snapTo.RemoteTime - snapFrom.RemoteTime;
        t = range > 0 ? (float)((renderTime - snapFrom.RemoteTime) / range) : 0f;

        return EBufferState.Interpolating;
    }

    /// <summary>
    /// 获取缓冲区中某个快照的引用。
    /// </summary>
    /// <param name="index">从 <see cref="GetInterpolationIndices"/> 取得的被掩码索引。</param>
    /// <returns>对快照的 <see langword="readonly"/> 引用，可避免结构体复制。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T GetSnapshot(int index)
    {
        return ref _buffer[index];
    }

    /// <summary>
    /// 清空 snapshotter 并把它重置为默认状态
    /// </summary>
    public void Clear()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _totalAdded = 0;
        _timeSync = default;
        _adaptiveJitterBuffer = default;
        _lastLocalTime = 0;
        _lastRemoteTime = 0;

#if DEBUG
        FikaGlobals.LogWarning("Cleared snapshotter");
#endif
    }
}