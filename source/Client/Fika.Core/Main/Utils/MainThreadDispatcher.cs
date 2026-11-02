using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Fika.Core.Main.Utils;

/// <summary>托管生产者无需进入 IL2CPP 即可调度 Unity 工作。</summary>
internal static class MainThreadDispatcher
{
    private static readonly ConcurrentQueue<(Action Action, CancellationToken Token)> Pending = new();
    private static int _threadId;
    private static int _marshalledCalls;
    private static int _threadIdConflicts;
    private static readonly SynchronizationContext Context = new UnityContext();

    public static void Initialize()
    {
        var threadId = Environment.CurrentManagedThreadId;
        var previous = Interlocked.CompareExchange(ref _threadId, threadId, 0);
        if (previous != 0 && previous != threadId)
            throw new InvalidOperationException("The Unity dispatcher is already owned by another thread.");
    }

    /// <summary>
    /// 调用方是否运行在泵送该队列的那个托管线程上。
    /// <para>
    /// 原生反向 P/Invoke 回调（Unity UI 按钮委托、UnityEvent
    /// 监听器）投递时带来的托管线程标识可能与
    /// <see cref="Initialize"/> 中捕获的不同，
    /// 因此这是一个待查询的问题，而绝非假设。需要 Unity 的调用方
    /// 必须走 <see cref="RunAsync(Func{Task})"/>，它会进行编组而不是直接失败。
    /// </para>
    /// </summary>
    public static bool IsOnMainThread => _threadId != 0 && Environment.CurrentManagedThreadId == _threadId;

    public static void Post(Action action, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!token.IsCancellationRequested) Pending.Enqueue((action, token));
    }

    // 只有 Fika 的操作会选用此上下文。不要全局替换 Unity 或
    // 其他插件的上下文。嵌套的 await 会自然继承它。
    public static Task RunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!IsOnMainThread) return MarshalAsync(operation);

        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(Context);
            return operation();
        }
        catch (Exception exception) { return Task.FromException(exception); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    public static Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!IsOnMainThread) return MarshalAsync(operation);

        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(Context);
            return operation();
        }
        catch (Exception exception) { return Task.FromException<T>(exception); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    /// <summary>
    /// 当调用方位于另一个托管线程时，在泵送线程上运行该操作。
    /// 方法体仍在 Unity 主线程上执行（在队列排空过程中），
    /// 因此这只是把工作推迟一帧，而不是中止调用方——
    /// 否则一个无法派发的 UI 按钮会静默地
    /// 什么都不做。
    /// </summary>
    private static Task MarshalAsync(Func<Task> operation)
    {
        ReportMarshalledCall();
        var completion = new TaskCompletionSource<bool>();
        Post(() =>
        {
            try
            {
                var task = operation();
                if (task == null) { completion.TrySetResult(true); return; }
                if (task.IsCompleted) { Complete(completion, task); return; }
                task.ContinueWith(completed => Complete(completion, completed), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        return completion.Task;
    }

    private static Task<T> MarshalAsync<T>(Func<Task<T>> operation)
    {
        ReportMarshalledCall();
        var completion = new TaskCompletionSource<T>();
        Post(() =>
        {
            try
            {
                var task = operation();
                if (task == null) { completion.TrySetResult(default); return; }
                if (task.IsCompleted) { Complete(completion, task); return; }
                task.ContinueWith(completed => Complete(completion, completed), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        return completion.Task;
    }

    private static void Complete(TaskCompletionSource<bool> completion, Task task)
    {
        if (task.IsCanceled) { completion.TrySetCanceled(); return; }
        if (task.IsFaulted) { completion.TrySetException(task.Exception.InnerExceptions); return; }
        completion.TrySetResult(true);
    }

    private static void Complete<T>(TaskCompletionSource<T> completion, Task<T> task)
    {
        if (task.IsCanceled) { completion.TrySetCanceled(); return; }
        if (task.IsFaulted) { completion.TrySetException(task.Exception.InnerExceptions); return; }
        completion.TrySetResult(task.Result);
    }

    private static void ReportMarshalledCall()
    {
        if (Interlocked.Exchange(ref _marshalledCalls, 1) != 0) return;
        FikaGlobals.LogWarning($"MainThreadDispatcher: work arrived on managed thread " +
            $"{Environment.CurrentManagedThreadId} while Unity's main thread is {_threadId}; marshalling it.");
    }

    /// <summary>
    /// (analysis/stutter-frame-skip) 超过该值的单个排队操作会单独输出一行日志：
    /// 2026-10-13 20:01 的战局在一个窗口测到 dispatcherDrain=2045.91ms max=1883.05ms，
    /// 在另一个窗口测到 4138.66ms max=3934.25ms，即该队列中的某个操作占住了数帧，
    /// 而仅凭时隙无法说明是哪一个续体。
    /// </summary>
    private const double SlowActionMillis = 25d;

    /// <summary>一秒内慢操作日志行的上限。</summary>
    private const int MaxSlowLinesPerSecond = 8;

    private static int _slowLines;
    private static long _slowWindowStart;

    public static void Drain(Action<Exception> onError)
    {
        VerifyThread();

        // 在排空过程中投递的续体（包括 Task.Yield）属于
        // 下一帧，而不是在本 Update 中反复空转。
        var budget = Math.Min(Pending.Count, 256);
        for (var count = 0; count < budget && Pending.TryDequeue(out var work); count++)
        {
            if (work.Token.IsCancellationRequested) continue;
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(Context);
                work.Action();
            }
            catch (Exception exception) { onError(exception); }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
    }

    private static string Describe(Action action)
    {
        try
        {
            var method = action.Method;
            var type = method?.DeclaringType?.FullName;
            var target = action.Target?.GetType().FullName;
            return (type ?? "<unknown>") + "." + (method?.Name ?? "<unknown>")
                + (target == null ? " (static)" : " target=" + target);
        }
        catch (Exception)
        {
            return "<unreadable delegate>";
        }
    }

    private static void VerifyThread()
    {
        if (_threadId == 0)
        {
            throw new InvalidOperationException("The Unity dispatcher was never initialized.");
        }

        if (Environment.CurrentManagedThreadId == _threadId) return;

        // 队列只从插件的 Unity Update 中泵送。如果该泵报告了
        // 不同的标识，就跟随该泵：否则所有已排队的主线程操作
        // （包括上面刚编组过来的那些）都会被一道不再符合
        // 实际情况的守卫挡住。
        if (Interlocked.Exchange(ref _threadIdConflicts, 1) == 0)
        {
            FikaGlobals.LogWarning($"MainThreadDispatcher: the queue is now pumped by managed thread " +
                $"{Environment.CurrentManagedThreadId} instead of {_threadId}; following the pump.");
        }

        Interlocked.Exchange(ref _threadId, Environment.CurrentManagedThreadId);
    }

    private sealed class UnityContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object state) =>
            MainThreadDispatcher.Post(() => callback(state));

        public override SynchronizationContext CreateCopy() => this;

        public override void Send(SendOrPostCallback callback, object state)
        {
            if (MainThreadDispatcher.IsOnMainThread)
            {
                callback(state);
                return;
            }

            // 运行时需要在 Unity 线程上同步续体。
            // 把它交给泵并等待，而不是让调用方失败。
            using var completed = new ManualResetEventSlim(false);
            Exception error = null;
            MainThreadDispatcher.Post(() =>
            {
                try { callback(state); }
                catch (Exception exception) { error = exception; }
                finally { completed.Set(); }
            });

            if (!completed.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Timed out waiting for Unity's main thread to run a dispatched continuation.");
            }

            if (error != null) throw error;
        }
    }
}
