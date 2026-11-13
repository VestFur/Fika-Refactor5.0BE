using System;
using System.Threading.Tasks;
using Comfort.Common;

namespace Fika.Core.Main.Utils;

/// <summary>只有在其托管部分的工作完成后才结束原生任务。</summary>
public static class NativeTask
{
    public static async Task AsManaged(this EFT.AssetsManager.LoadSceneOperation operation)
    {
        await operation;
    }

    // 生成的 IL2CPP awaiter 会把 MoveNext 直接转发给它的原生
    // 回调。它无法恢复 CLR 的 SynchronizationContext。返回一个
    // CLR Task 可以让 C# 的 awaiter 在原生完成以及之后任何托管的
    // HTTP/delay await 之间都保留调用方的 Unity 调度器。
    public static Task AsManaged(this Il2CppSystem.Threading.Tasks.Task task)
    {
        var source = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var awaiter = task.GetAwaiter();
        void Finish()
        {
            try
            {
                if (task.IsCanceled) { source.TrySetCanceled(); return; }
                awaiter.GetResult();
                source.TrySetResult(null);
            }
            catch (Exception exception) { source.TrySetException(exception); }
        }
        if (awaiter.IsCompleted) Finish();
        else awaiter.OnCompleted((Action)Finish);
        return source.Task;
    }

    public static Task<T> AsManaged<T>(this Il2CppSystem.Threading.Tasks.Task<T> task)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var awaiter = task.GetAwaiter();
        void Finish()
        {
            try
            {
                if (task.IsCanceled) { source.TrySetCanceled(); return; }
                source.TrySetResult(awaiter.GetResult());
            }
            catch (Exception exception) { source.TrySetException(exception); }
        }
        if (awaiter.IsCompleted) Finish();
        else awaiter.OnCompleted((Action)Finish);
        return source.Task;
    }

    public static Il2CppSystem.Threading.Tasks.Task FromManaged(Task task)
    {
        var source = new Il2CppSystem.Threading.Tasks.TaskCompletionSource<Il2CppSystem.Object>();
        Complete(task, source);
        return source.Task;
    }

    public static Il2CppSystem.Threading.Tasks.Task<T> FromManaged<T>(Task<T> task)
    {
        var source = new Il2CppSystem.Threading.Tasks.TaskCompletionSource<T>();
        Complete(task, source);
        return source.Task;
    }

    private static async void Complete(Task task, Il2CppSystem.Threading.Tasks.TaskCompletionSource<Il2CppSystem.Object> source)
    {
        try
        {
            await task;
            MainThreadDispatcher.Post(() => source.TrySetResult(null));
        }
        catch (OperationCanceledException) when (task.IsCanceled)
        {
            MainThreadDispatcher.Post(() => source.TrySetCanceled());
        }
        catch (Exception exception)
        {
            MainThreadDispatcher.Post(() => source.TrySetException(new Il2CppSystem.Exception(exception.ToString())));
        }
    }

    private static async void Complete<T>(Task<T> task, Il2CppSystem.Threading.Tasks.TaskCompletionSource<T> source)
    {
        try
        {
            var result = await task;
            MainThreadDispatcher.Post(() => source.TrySetResult(result));
        }
        catch (OperationCanceledException) when (task.IsCanceled)
        {
            MainThreadDispatcher.Post(() => source.TrySetCanceled());
        }
        catch (Exception exception)
        {
            MainThreadDispatcher.Post(() => source.TrySetException(new Il2CppSystem.Exception(exception.ToString())));
        }
    }
}
