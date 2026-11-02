using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 本移植所拥有的每个 native -> managed 边界的面包屑日志，以及阻止托管
/// 异常从其中逃逸出去的守卫。
///
/// 为什么要用日志簿而不只是一行日志：运行时可以在不执行任何托管处理器的情况下死亡。
/// 快速失败（COR_E_EXECUTIONENGINE，0x80131506，由 coreclr 自身以 EXCEPTION_NONCONTINUABLE 抛出）
/// 以及 STATUS_STACK_BUFFER_OVERRUN（0xC0000409）都会在导致它们的指令仍在栈上时
/// 杀死进程，因此 <c>AppDomain.FirstChanceException</c> 和所有 try/catch 都会被绕过。
/// 在这种死亡中幸存下来的只有已经写进磁盘的内容，因此本类在内存中保留一个小环形缓冲区，
/// 记录最近的边界（只保存常量字符串，每个条目不分配内存），并由
/// <see cref="Dump"/> 在故障探测器看到那些代码之一时把它刷出。
///
/// 该环形缓冲区会合并连续相同的标签，因此历史记录保留的是“跨越了哪些边界”，
/// 而不是“同一个帧 tick 重复了四千次”。
///
/// 第 2 轮（analysis/countdown-bisect-round2）：只把日志簿留在内存中，意味着整个
/// 埋点依赖写入线程，而在 11:17 时写入线程在死亡前 14 秒就停止了，
/// 而进程仍在运行——于是追踪记录里根本没有指出任何倒计时标签。因此日志簿
/// 现在会直接写入追踪记录：<see cref="Enter"/> 限制为每秒两次，
/// <see cref="Mark"/>/<see cref="Returned"/>/<see cref="Skipped"/> 则立即且不缓冲地写入。
/// </summary>
internal static class FikaBoundaries
{
    private const int Capacity = 64;
    private const int RepeatSummariesOnDump = 12;

    private struct Entry
    {
        internal string Label;
        internal long Millis;
        internal int Repeats;
    }

    private static readonly object _gate = new();
    private static readonly Entry[] _ring = new Entry[Capacity];
    private static readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> _failureQuietUntil = new(StringComparer.Ordinal);
    private static int _index = -1;
    private static int _count;
    private static bool _dumped;

    /// <summary>
    /// 记录控制流进入了一个边界。只接受常量标签字符串（不会被复制）、
    /// 不分配内存、不调用 Unity API——可安全地每帧每边界调用一次。
    /// </summary>
    internal static void Enter(string label)
    {
        try
        {
            var now = Environment.TickCount64;
            if (_index >= 0 && ReferenceEquals(_ring[_index].Label, label))
            {
                _ring[_index].Repeats++;
                _ring[_index].Millis = now;
                return;
            }

            _index = (_index + 1) % Capacity;
            _ring[_index].Label = label;
            _ring[_index].Millis = now;
            _ring[_index].Repeats = 1;
            if (_count < Capacity)
            {
                _count++;
            }

            
        }
        catch (Exception)
        {
            // 日志簿绝不应成为边界失败的原因。
        }
    }

    /// <summary>
    /// 记录一个 *语句*——每个战局只运行一次、而非每帧一次的标签——并在返回前把它写入磁盘：
    /// 环形缓冲区用于致命转储，另外通过崩溃追踪的标签句柄
    /// 在调用线程上做一次不缓冲的 WriteFile。这正是二分法所读取的埋点：追踪记录的
    /// 最后一行 "[Label] ..." 指出了进程死于哪个语句，即使追踪记录的
    /// 写入线程已经停止（11:17 那次故障）。从不限流，从不批处理。
    /// </summary>
    internal static void Mark(string label)
    {
        Enter(label);
        
    }

    /// <summary>记录某个语句已返回。追踪记录会立即得到 "&lt;label&gt; - RETURNED"。</summary>
    internal static void Returned(string label)
    {
        try
        {
            
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 记录某个语句因其开关关闭而没有运行。写入的即时性与
    /// <see cref="Mark"/> 相同：以 SKIPPED 行结束的追踪记录绝不能被误认为“它运行了”。
    /// </summary>
    internal static void Skipped(string label)
    {
        Skipped(label, "its switch is off");
    }

    /// <summary>
    /// 与 <see cref="Skipped(string)"/> 相同，但会写出原因。第 3 轮在
    /// 等待后代码块中需要它：该块可能因其自身总开关被跳过，也可能因 'Skip Countdown Body' 被跳过，
    /// 追踪记录必须说明是哪一个，这样读者就不会把一个误当成另一个。
    /// </summary>
    internal static void Skipped(string label, string reason)
    {
        try
        {
            
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 环形缓冲区中最新的边界，格式为 "&lt;label&gt; (&lt;ms&gt; ms ago)"，或 "&lt;none&gt;"。其行为有界，
    /// 与 <see cref="SnapshotBounded"/> 一样——它从不等待日志簿锁——因此从主线程写入的报告
    /// （泵的帧停顿标签）不会因为某个卡住的写入者而被堵住。
    /// </summary>
    internal static string LastLabel()
    {
        var taken = false;
        try
        {
            taken = Monitor.TryEnter(_gate, 25);
            if (_count == 0 || _index < 0 || _ring[_index].Label == null)
            {
                return "<none>";
            }

            var entry = _ring[_index];
            return "'" + entry.Label + "' " + (Environment.TickCount64 - entry.Millis) + " ms ago";
        }
        catch (Exception)
        {
            return "<none>";
        }
        finally
        {
            if (taken)
            {
                try
                {
                    Monitor.Exit(_gate);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    /// <summary>
    /// 运行一个边界主体。原本会逃逸到引擎（或逃逸到运行时，
    /// 而正是运行时把它变成崩溃）的托管异常，会按站点上报一次并被吞掉，
    /// 因此该帧——以及其他所有边界——能继续运行。
    /// </summary>
    internal static bool Run(string label, Action body)
    {
        try
        {
            body();
            return true;
        }
        catch (Exception exception)
        {
            Fail(label, exception);
            return false;
        }
    }

    /// <summary>带守卫的工厂：当边界主体抛出时返回回退值。</summary>
    internal static T Run<T>(string label, Func<T> body, T fallback)
    {
        try
        {
            return body();
        }
        catch (Exception exception)
        {
            Fail(label, exception);
            return fallback;
        }
    }

    /// <summary>针对单个边界站点的有界、限流失败上报。</summary>
    internal static void Fail(string label, Exception exception)
    {
        try
        {
            var now = Environment.TickCount64;
            var count = 0;
            var report = false;

            lock (_gate)
            {
                _failures.TryGetValue(label, out count);
                count++;
                _failures[label] = count;
                _failureQuietUntil.TryGetValue(label, out var quietUntil);
                if (count == 1 || now >= quietUntil)
                {
                    _failureQuietUntil[label] = now + 5000;
                    report = true;
                }
            }

            if (report)
            {
                
                return;
            }

            if (count == 10 || count == 100 || count == 1000)
            {
                
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 按从旧到新的顺序写出已记录的边界，并向首次机会记录器索取它自己的
    /// 摘要，每个进程一次。可安全地从故障探测器的向量化处理器中调用：它只会追加
    /// 到诊断文件。
    /// </summary>
    internal static void Dump(string reason)
    {
        lock (_gate)
        {
            if (_dumped)
            {
                return;
            }

            _dumped = true;
        }

        try
        {
            
            if (_count == 0)
            {
                
            }
            else
            {
                var text = Snapshot(Capacity);
                var start = 0;
                while (start < text.Length)
                {
                    var end = text.IndexOf('\n', start);
                    if (end < 0)
                    {
                        end = text.Length;
                    }

                    if (end > start)
                    {
                        
                    }

                    start = end + 1;
                }
            }

            var failures = DescribeFailures();
            if (failures != null)
            {
                
            }
        }
        catch (Exception)
        {
        }

        
    }

    /// <summary>
    /// 渲染最近 <paramref name="maxEntries"/> 条已记录的边界，从旧到新，每行一条。
    /// 纯托管代码：崩溃路径会在运行时垂死时调用它，因此它既不触碰
    /// Unity，也不触碰诊断输出端，而且从不抛出异常。
    /// </summary>
    internal static string Snapshot(int maxEntries)
    {
        var builder = new StringBuilder(2048);
        try
        {
            lock (_gate)
            {
                AppendJournal(builder, maxEntries);
            }
        }
        catch (Exception)
        {
        }

        return builder.Length == 0 ? "no boundary was journalled" : builder.ToString();
    }

    /// <summary>
    /// 渲染结果与 <see cref="Snapshot"/> 相同，但阻塞时间绝不会超过
    /// <paramref name="waitMillis"/>：若在该时间内拿不到日志簿锁，则在不持锁的情况下
    /// 读取环形缓冲区。撕裂的条目（标签配上上一时间戳）在诊断行中只是
    /// 外观瑕疵；而 11:17 的故障恰恰相反——写入线程调用了会阻塞的
    /// <see cref="Snapshot"/>，且当那把锁被占用时，追踪记录就永久停止了。
    /// </summary>
    internal static string SnapshotBounded(int maxEntries, int waitMillis)
    {
        var builder = new StringBuilder(2048);
        var taken = false;
        try
        {
            taken = Monitor.TryEnter(_gate, waitMillis);
        }
        catch (Exception)
        {
            taken = false;
        }

        try
        {
            AppendJournal(builder, maxEntries);
        }
        catch (Exception)
        {
        }
        finally
        {
            if (taken)
            {
                try
                {
                    Monitor.Exit(_gate);
                }
                catch (Exception)
                {
                }
            }
        }

        return builder.Length == 0 ? "no boundary was journalled" : builder.ToString();
    }

    private static void AppendJournal(StringBuilder builder, int maxEntries)
    {
        var wanted = Math.Min(_count, Math.Max(1, maxEntries));
        for (var i = _count - wanted; i < _count; i++)
        {
            var index = (_index - _count + 1 + i + Capacity * 4) % Capacity;
            var entry = _ring[index];
            if (entry.Label == null)
            {
                continue;
            }

            builder.Append("[Boundary]   ").Append(entry.Label);
            if (entry.Repeats > 1)
            {
                builder.Append(" (x").Append(entry.Repeats).Append(')');
            }

            builder.Append('\n');
        }
    }

    private static string DescribeFailures()
    {
        var builder = new StringBuilder(160);
        lock (_gate)
        {
            var shown = 0;
            foreach (var pair in _failures)
            {
                if (shown++ == RepeatSummariesOnDump)
                {
                    break;
                }

                if (builder.Length != 0)
                {
                    builder.Append(", ");
                }

                builder.Append(pair.Key).Append('x').Append(pair.Value);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
