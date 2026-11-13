using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Fika.Core.Main.Utils;

/// <summary>Managed producers never need to enter IL2CPP to schedule Unity work.</summary>
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
    /// Whether the caller runs on the managed thread that pumps the queue.
    /// <para>
    /// Native reverse P/Invoke callbacks (Unity UI button delegates, UnityEvent
    /// listeners) can be delivered with a different managed thread identity than
    /// the one captured in <see cref="Initialize"/>, so this is a question, never
    /// an assumption. Callers that need Unity must go through
    /// <see cref="RunAsync(Func{Task})"/>, which marshals instead of failing.
    /// </para>
    /// </summary>
    public static bool IsOnMainThread => _threadId != 0 && Environment.CurrentManagedThreadId == _threadId;

    public static void Post(Action action, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!token.IsCancellationRequested) Pending.Enqueue((action, token));
    }

    // Only Fika operations opt into this context. Do not replace Unity's or
    // another plugin's context globally. Nested awaits inherit it naturally.
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
    /// Runs an operation on the pumping thread when the caller is a different
    /// managed thread. The body still executes on Unity's main thread (inside the
    /// queue drain), so this only defers the work by a frame instead of aborting
    /// the caller - a UI button that cannot dispatch would otherwise silently do
    /// nothing.
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

    public static void Drain(Action<Exception> onError)
    {
        VerifyThread();

        // A continuation posted while draining (including Task.Yield) belongs
        // to the next frame, rather than spinning repeatedly in this Update.
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
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
    }

    private static void VerifyThread()
    {
        if (_threadId == 0)
        {
            throw new InvalidOperationException("The Unity dispatcher was never initialized.");
        }

        if (Environment.CurrentManagedThreadId == _threadId) return;

        // The queue is only pumped from the plugin's Unity Update. If that pump
        // reports a different identity, follow the pump: otherwise every queued
        // main-thread action (including the ones just marshalled above) would be
        // stuck behind a guard that no longer describes reality.
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

            // The runtime wants a synchronous continuation on Unity's thread.
            // Hand it to the pump and wait instead of failing the caller.
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
