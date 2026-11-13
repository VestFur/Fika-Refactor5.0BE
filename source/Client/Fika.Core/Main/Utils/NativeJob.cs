using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Diz.Jobs;

namespace Fika.Core;

internal static class NativeJob
{
    public static Awaitable AsManaged(this IJobAwaitable job) => new(job);

    internal readonly struct Awaitable(IJobAwaitable job)
    {
        public Awaiter GetAwaiter() => new(job.GetAwaiter(), SynchronizationContext.Current);
    }

    internal readonly struct Awaiter(IJobAwaiter awaiter, SynchronizationContext context) : INotifyCompletion
    {
        public bool IsCompleted => awaiter.IsCompleted;
        public void GetResult() => awaiter.GetResult();
        public void OnCompleted(Action continuation)
        {
            var captured = context;
            Action resume = captured == null ? continuation : () => captured.Post(_ => continuation(), null);
            awaiter.OnCompleted((Il2CppSystem.Action)resume);
        }
    }
}
