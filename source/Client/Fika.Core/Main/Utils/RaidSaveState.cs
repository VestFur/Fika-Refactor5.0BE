using System;
using System.Threading.Tasks;

namespace Fika.Core.Main.Utils;

/// <summary>跟踪那一次早死的存档；正常的突袭结束存档仍是兜底。</summary>
internal sealed class RaidSaveState
{
    private TaskCompletionSource<object> _completion;

    internal Task Completion => _completion?.Task ?? Task.CompletedTask;
    internal bool HasSaved => _completion?.Task.Status == TaskStatus.RanToCompletion;

    // 在 Unity 调度器上调用。先发布待处理任务，再调用
    // 该操作，这样重入的死亡/退出回调观察到的是同一次存档。
    internal Task SaveOnce(Func<Task> operation)
    {
        if (_completion != null) return _completion.Task;
        _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Complete(operation, _completion);
        return _completion.Task;
    }

    private static async Task Complete(Func<Task> operation, TaskCompletionSource<object> completion)
    {
        try
        {
            await operation();
            completion.TrySetResult(null);
        }
        catch (OperationCanceledException exception) { completion.TrySetCanceled(exception.CancellationToken); }
        catch (Exception exception) { completion.TrySetException(exception); }
    }
}
