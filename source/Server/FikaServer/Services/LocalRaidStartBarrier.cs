#nullable enable

namespace FikaServer.Services;

/// <summary>
/// 跟踪在战局开始收尾之前被准入的 StartLocalRaid 请求。
/// 持有者在战局锁的保护下关闭准入，然后等待此
/// 屏障，再进行保险结算。将计数器与
/// 保险状态分离，还能让请求在原生 EndLocalRaid
/// 已经执行完毕的情况下仍完成 token 注册。
/// </summary>
public sealed class LocalRaidStartBarrier
{
    private readonly object _gate = new();
    private int _inFlight;
    private TaskCompletionSource<bool>? _completed;

    public int InFlight
    {
        get
        {
            lock (_gate) return _inFlight;
        }
    }

    /// <summary>
    /// 在准入仍开放时预留一个 StartLocalRaid 请求。
    /// </summary>
    public bool TryReserve(bool admissionOpen)
    {
        if (!admissionOpen) return false;

        lock (_gate)
        {
            // 调用方只能在其持有 FikaMatch 锁期间进行预留。
            // 若已有另一个请求在飞行中，则保留现有的等待者；
            // EndMatch 会在等待之前关闭准入。
            _inFlight++;
            return true;
        }
    }

    /// <summary>
    /// 释放一个预留，并在最后一个请求完成时唤醒
    /// 等待者。对未匹配/重复释放返回 false。
    /// </summary>
    public bool Release()
    {
        TaskCompletionSource<bool>? completed = null;
        lock (_gate)
        {
            if (_inFlight <= 0) return false;

            _inFlight--;
            if (_inFlight == 0)
            {
                completed = _completed;
                _completed = null;
            }
        }

        completed?.TrySetResult(true);
        return true;
    }

    /// <summary>
    /// 等待关闭准入时已存在的所有预留。
    /// 超时机制可避免出错的请求永久阻塞拆除流程。
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan timeout)
    {
        Task? pending;
        lock (_gate)
        {
            if (_inFlight == 0) return true;
            _completed ??= new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pending = _completed.Task;
        }

        var completed = await Task.WhenAny(pending, Task.Delay(timeout))
            .ConfigureAwait(false);
        return completed == pending;
    }
}
