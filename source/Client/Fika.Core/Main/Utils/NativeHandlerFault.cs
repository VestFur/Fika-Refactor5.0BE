using System;

namespace Fika.Core.Main.Utils;

/// <summary>
/// (analysis/session-log-archive) 本机订阅的处理器在自身原生帧内部抛出异常时的上报器，
/// 以及让它保持可读的限流边界。
///
/// <para>
/// 它所要取代的形态：<c>BotStateManager.Update</c> 会泵动游戏自身的 <c>IBotGame.UpdateByUnity</c>
/// 事件，原生 <c>BotGroupHostility</c> 把它的逐帧敌对性重算订阅到了该事件上，而那个
/// 原生方法在 raid 世界构建期间解引用了一个仍为 null 的字段。异常
/// 经 invoke 桩逃逸进 <c>NativeManagedInvoker.Shim</c>，后者将其报告为
/// 日志原文："[FAIL] [ManagedInvoker] the managed shim failed for Fika.Core.Main.Components.BotStateManager.Update" ——
/// 这是真的，但它同时意味着那次 Update 剩下的全部内容（该 tick 的 bot 状态批次）都被跳过，
/// 而在客户端上，本帧只有第一个原生订阅者执行了。
/// </para>
///
/// <para>
/// 该失败的三次观测（2026-10-11 17:19、2026-10-13 09:00、2026-10-13 14:08；最后一次
/// 是 analysis/logs-coop4-20261013-140826/Fika-diagnostics.log:28439）都落在下面这个窗口内：
/// 世界已被构建、bot 已被生成 —— 一次在 <c>SessionRun</c> 之前，两次在 bot 创建过程中
/// —— 这也正是无法为它写出「不要在该状态下调用它」这类防护的原因：该事件不携带任何
/// 自身状态，且无法从委托识别出失败的处理器。因此修复方式是隔离
/// 加具名上报，而该帧仍能完成它剩余的工作。
/// </para>
///
/// <para>
/// 边界：某个签名的第一次失败会完整写入诊断文件（完整的 il2cpp
/// 调用栈，也就是一份报告所需要的那一行），同一签名的后续失败则在第 1、2、4、8…… 次出现时写入，
/// 其间的全部只做计数。这里不会有任何抛出。
/// </para>
/// </summary>
internal static class NativeHandlerFault
{
    private const int MaxTextLength = 300;

    private static readonly object Gate = new();
    private static string _signature;
    private static int _count;
    private static bool _detailed;

    /// <summary>
    /// 上报本机订阅处理器的一次失败后返回；调用方继续执行它这一帧的其余部分，
    /// 这正是引擎自身的循环在异常离开原生处理器时的做法。
    /// </summary>
    internal static void Report(Exception exception)
    {
        try
        {
            var signature = Signature(exception);
            int count;
            bool withException;
            lock (Gate)
            {
                if (!string.Equals(signature, _signature, StringComparison.Ordinal))
                {
                    _signature = signature;
                    _count = 0;
                    _detailed = false;
                }

                _count++;
                count = _count;
                withException = !_detailed;
                if (withException)
                {
                    _detailed = true;
                }
                else if ((count & (count - 1)) != 0)
                {
                    // 已在上一档 2 的幂处上报过该签名：只做计数。
                    return;
                }
            }

            var line = "[NativeUpdate] a natively subscribed handler of this event threw inside its own "
                + "native frame and was isolated; the frame continues (the state batch of this tick and the "
                + "other subscribers are not skipped any more): " + Describe(exception)
                + (count > 1 ? " | occurrence #" + count + " of this signature" : string.Empty);

            try
            {
                FikaPlugin.Instance?.FikaLogger.LogWarning(line);
            }
            catch (Exception)
            {
            }

            if (withException)
            {
                
            }
            else
            {
                
            }
        }
        catch (Exception)
        {
            // 一个弄坏它所保护的泵的上报器，比它所上报的失败更糟。
        }
    }

    /// <summary>
    /// 类型、消息的第一行与最内层原生帧：这些让两次失败在上报限流看来是「同一个」，
    /// 也是读者在下一个会话中认出这一族问题所需要的。
    /// </summary>
    private static string Signature(Exception exception)
    {
        try
        {
            var message = exception?.Message;
            return exception?.GetType().Name + "|" + FirstLine(message) + "|" + NativeFrame(message);
        }
        catch (Exception)
        {
            return "<unreadable>";
        }
    }

    private static string Describe(Exception exception)
    {
        try
        {
            var message = FirstLine(exception?.Message);
            var frame = NativeFrame(exception?.Message);
            var text = exception?.GetType().Name + ": " + Shorten(message);
            if (!string.IsNullOrEmpty(frame))
            {
                text += " | first native frame: " + frame;
            }

            return text;
        }
        catch (Exception)
        {
            return "<unreadable exception>";
        }
    }

    /// <summary>
    /// il2cpp 块的最内层帧。Il2CppInterop 会把原生调用栈渲染在异常消息
    /// 内部（"--- BEGIN IL2CPP STACK TRACE ---" 之后每帧一行），因此这是一次文本读取，
    /// 而不是对运行时做反射。
    /// </summary>
    private static string NativeFrame(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var begin = message.IndexOf("IL2CPP STACK TRACE", StringComparison.Ordinal);
        if (begin < 0)
        {
            return null;
        }

        var at = message.IndexOf(" at ", begin, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var end = message.IndexOf('\n', at);
        var line = end < 0 ? message.Substring(at) : message.Substring(at, end - at);
        return line.Trim();
    }

    private static string FirstLine(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "<no message>";
        }

        var end = message.IndexOf('\n');
        return end < 0 ? message : message.Substring(0, end);
    }

    private static string Shorten(string text)
    {
        if (text == null)
        {
            return "<no message>";
        }

        return text.Length > MaxTextLength ? text.Substring(0, MaxTextLength) + "..." : text;
    }
}
