using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Fika.Core.Networking.Websocket;

/// <summary>
/// 应用正在退出时 Fika web socket 循环的模样。
///
/// 关闭客户端 socket（<c>Close</c>/<c>Dispose</c>/<c>Abort</c>）并不会静默结束等待在其上的
/// 读取：它们会以取消、被中止的流，或已经处置的对象的形式返回——
/// <c>OperationCanceledException</c>、<c>IOException</c>（"The read operation failed"）、
/// <c>WebSocketException</c>、<c>ObjectDisposedException</c>。这些以前都会穿过循环的
/// 通用 <c>catch</c>，变成一行警告，而
/// <c>Task.Delay(token)</c> 的取消则会完全逃出循环，变成一个故障的即发即忘任务
/// （2026-10-10，frame=21499：运行的最后 90 ms 内有 13 个异常块和 286 行诊断日志）。
///
/// 只需两个小辅助方法就能把它与真正的 socket 问题区分开：条件是
/// 「停止令牌已被取消」，并由调用方自行检查。
/// </summary>
internal static class WebSocketShutdown
{
    /// <summary>对于关闭或中止的 socket 释放出的异常（而非干净结束）返回 true。</summary>
    internal static bool IsTeardown(Exception exception) =>
        exception is OperationCanceledException or IOException or ObjectDisposedException or WebSocketException
            or SocketException;

    /// <summary>
    /// 在下一次重连尝试前等待，若等待期间停止令牌被取消则返回 false。
    /// <c>Task.Delay(delay, token)</c> 则会抛出 <c>TaskCanceledException</c>——它是在循环 try 块之外
    /// 被等待的，所以关闭时它会让循环自身的任务进入故障状态，而首次机会
    /// 探测则把它记录为拆除异常。
    /// </summary>
    internal static async Task<bool> WaitBeforeReconnectAsync(TimeSpan delay, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return false;
        }

        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => cancelled.TrySetResult(true)))
        {
            var completed = await Task.WhenAny(cancelled.Task, Task.Delay(delay)).ConfigureAwait(false);
            return completed != cancelled.Task && !token.IsCancellationRequested;
        }
    }
}
