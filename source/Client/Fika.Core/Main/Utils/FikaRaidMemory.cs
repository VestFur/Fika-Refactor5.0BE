using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Fika.Core.Networking;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 一次 transit 重载的内存侧处理，集中在一处。
///
/// 缘何存在（analysis/transit-reload-oom，客户端 Fika-diagnostics.log frame=48430 /
/// LogOutput.log:4880）：客户端在同一个进程内重载下一张地图。旧 world 被
/// 拆除了，但没有任何东西释放那个 world 加载过的资源。客户端在任何地方都
/// 没有调用过 UnityEngine.Resources.UnloadUnusedAssets() —— 整个产品源码中仅有的
/// 调用点是无头（headless）侧的那些（FikaHeadlessPlugin.cs:531、HeadlessGame.cs:350），而无头侧
/// 在 transit 时会刻意跳过它们（FikaHeadlessPlugin.cs:529）—— 而且游戏自身的内存
/// 管理机制要等到下一场 raid 已经起来之后才反应（LogOutput.log:4872 "EmptyWorkingSet"，
/// 4873 "Can't disable GC due to low memory size 15.24219GB, requested 25GB"，4880 那次失败的
/// 20 MB TempOverflow 分配）。因此第二张地图被叠加在第一张之上，
/// 而进程恰好在第二场 raid 的角色出生时崩溃。
///
/// 释放动作跑在唯一既可行又有用的那个边界上：新的 world 对象
/// 已经存在，但它的关卡（bundle、池、场景）尚未加载，也就是正好在
/// 本机 ClientLocalGameWorld.InitLevel 调用之前。此刻上一个 world 已经不在了，因此
/// 它持有的一切都不可达，资源清理过程可以将其释放；而新地图仍然
/// 未加载，因此这次清理可能释放的东西都不是正在启动的 raid 所需要的。
///
/// [MemDiag] 行让下一次运行可读：每个边界一行，带上托管堆、
/// 进程工作集与 private bytes，以及（在引擎暴露这些数值的地方）Unity 分配器的
/// 总计。第二次 transit 运行随后就能回答"旧地图从未被释放"（数值只因释放
/// 而下降，且新地图从很高的基线开始）还是"新地图本身就太
/// 大"（基线很低，而 raid 依然耗尽内存）。
/// </summary>
internal static class FikaRaidMemory
{
    /// <summary>
    /// [MemDiag] 各行的诊断 scope。前缀被列入 <c>FikaDiagnostics.QuietSteps</c>，
    /// 因此这些行会像 [LoadSync] 的那些行一样，在静默诊断模式下继续保留。
    /// </summary>
    private const string DiagScope = "LoadingScreen.MemDiag";


    /// <summary>
    /// 资源清理过程的帧数上限。它需要几帧时间；这个上限存在的唯一目的是让
    /// 一次始终不上报 <c>isDone</c> 的清理不会永远卡住 raid 的启动。
    /// </summary>
    private const int MaxUnloadFrames = 300;

    private static bool _worldSwapPending;
    private static int _worldSwaps;

    /// <summary>在某个 world 拆除之后、其替代者的关卡加载之前为 true。</summary>
    internal static bool WorldSwapPending => _worldSwapPending;

    /// <summary>
    /// 记录当前 world 已被拆除、下一个 world 正在创建。调用点位于
    /// <see cref="NetManagerUtils.DestroyNetManager"/> 的 transit 分支，且对两种角色都会调用（客户端
    /// 分支在那里装载 world 报文队列；主机分支在那里保活服务器），因此这两项事实
    /// 都从同一个日志位置读出。
    ///
    /// 特意用 <c>FikaBackendUtils.IsTransit</c> 加以门控：下面的释放只允许在真正
    /// 延续的 transit 中运行。在该窗口之外，刚刚结束的那场 raid 的资源
    /// 属于下一场 raid 自己的加载路径（而且进程即将返回菜单，而菜单
    /// 自己会释放它们），因此普通的 raid 启动绝不会被触及。
    /// </summary>
    internal static void NoteWorldSwap(string reason)
    {
        if (!FikaBackendUtils.IsTransit)
        {
            return;
        }

        _worldSwapPending = true;
        _worldSwaps++;
    }

    /// <summary>
    /// 在给定边界上释放上一场 raid 的资源，且只释放一次。没有记录到 world 换挡时是 no-op
    /// （进程的普通 raid 启动不为此付出任何代价）；而当到达边界时这次换挡
    /// 已经不再是 transit 时，同样是 no-op。
    ///
    /// 返回的 task 绝不会 fault，也绝不会把释放跑两遍：内部的每条失败路径都只记录一行
    /// WARN 然后继续。失败的释放不能阻止正在加载的 raid —— 因为这一处在两种角色
    /// （主机也包含在内）上都被 await 在本机关卡加载之前。
    /// </summary>
    internal static Task ReleaseWorldSwapAsync(string boundary)
    {
        if (!_worldSwapPending)
        {
            return Task.CompletedTask;
        }

        var wasTransit = FikaBackendUtils.IsTransit;
        _worldSwapPending = false;
        if (!wasTransit)
        {
            return Task.CompletedTask;
        }

        try
        {
            return MainThreadDispatcher.RunAsync(async () =>
            {
                try
                {
                    var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    var managedBefore = ManagedHeap();
                    var frames = await UnloadUnusedAssetsAsync();
                    var collect = ForceCollect();
                    var managedAfter = ManagedHeap();
                    var cost = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                catch (Exception exception)
                {
                    FikaGlobals.LogWarning("FikaRaidMemory: the swap release at '" + boundary
                        + "' failed, the load continues regardless: " + exception);
                }
            });
        }
        catch (Exception exception)
        {
            // 双重保险：调度器本身也不得让正在加载的 raid 失败。
            FikaGlobals.LogWarning("FikaRaidMemory: the swap release at '" + boundary
                + "' could not be scheduled, the load continues regardless: " + exception);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 一次 Unity 资源清理过程，在泵线程（pumping thread）上被驱动到完成。旧 world 持有的一切
    /// 在调用点都不可达，所以正是这次调用把旧地图的纹理、网格和
    /// 音频（Unity 的 ALLOC_CACHEOBJECTS，在报告的崩溃中保留了 3.89 GB / 4.18 GB）重新变回
    /// 空闲内存。仍然被引用的对象 —— 加载界面、菜单、网络管理器、
    /// 以及每个活对象所指向的 prefab —— 会被引擎刻意保留。
    /// </summary>
    private static async Task<int> UnloadUnusedAssetsAsync()
    {
        var frames = 0;
        try
        {
            var unload = UnityEngine.Resources.UnloadUnusedAssets();
            while (unload != null && !unload.isDone && frames < MaxUnloadFrames)
            {
                frames++;
                await Task.Yield();
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning("FikaRaidMemory: the asset pass failed: " + exception.Message);
        }

        return frames;
    }

    /// <summary>
    /// 一次强制的、阻塞式的、带压缩的托管回收。一次 transit 会把整场 raid 的托管
    /// 状态（profile、物品、报文、物品工厂的缓存）带进下一场；没有这一步，
    /// 旧 raid 的垃圾会一直留在堆上，而新 raid 正在被构建。它跑在加载
    /// 界面上，因此这一次性停顿不会让玩家看到任何代价。
    /// </summary>
    /// <summary>
    /// 用于释放动作昂贵形态的一键开关（analysis/transit-crash-unload）。
    ///
    /// 在 round-12 日志中测得，崩溃会话第一次 transit 的释放边界
    /// （Fika-diagnostics.log，frames 31414 -> 31415，"before the release" 330055 ms -> "after the release"
    /// 330571 ms）：资源清理过程在一帧内完成，而该窗口的全部 516 ms 都是那两次
    /// 带终结器等待的阻塞式 *压缩* 回收 —— 而清理过程本身只释放了 2.6 MiB 的
    /// Unity 内存，那些回收释放了 25 MiB 的托管垃圾。516 ms 的主线程冻结
    /// 正是"下一张地图加载很慢"这类抱怨，所以现在的默认是一次阻塞式、不压缩的
    /// 回收（它仍然会丢掉旧 raid 的垃圾），而压缩改为按需开启。
    /// </summary>
    private static readonly bool CompactAfterUnload = false;

    /// <summary>
    /// 释放动作所使用的托管回收。默认是一次不带压缩的第 2 代阻塞式回收；
    /// 在设置了 <see cref="CompactAfterUnload"/> 时则是原先的那一对，压缩也包含在内。
    /// 绝不抛出异常。
    /// </summary>
    private static string ForceCollect()
    {
        try
        {
            if (CompactAfterUnload)
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                return "forced+compacting";
            }

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            return "forced";
        }
        catch (Exception exception)
        {
            return "failed (" + exception.GetType().Name + ")";
        }
    }

    /// <summary>MEMORYSTATUSEX，由 <see cref="GlobalMemoryStatusEx"/> 填充。</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }

    private static long ManagedHeap()
    {
        try
        {
            return GC.GetTotalMemory(false);
        }
        catch (Exception)
        {
            return -1L;
        }
    }

    // ------------------------------------------------------------------ commit 监视
    // (AB: commit-watch) 缘何存在：2026-10-12 19:59:56 那次死亡是一次 CLR fail fast
    // (0x80131506, coreclr.dll)，而它之前最后两条 [MemDiag] 行读到的是
    // sysCommitAvail=820.7MiB / 883.9MiB，而本进程持有 committed=12.8GB —— 是整台机器的
    // commit 被打光了，而不是本进程的堆。页面文件是用户侧的设置
    // （Win32_PageFileUsage 只报告 C: 15608 MB，而注册表配置的是 d:/pagefile.sys），所以
    // 本插件对此唯二能做的事就是：在运行时喊出来之前先喊出来，以及
    // 把它被允许归还的东西归还回去。
    //
    // 该监视由 LoadCostMeter.Tick 驱动，即由插件 Update 每帧驱动，无论
    // 是否处于 world 窗口内 —— 上面那次死亡发生在一场进行中的 raid 里，而不是换挡窗口里。
    // 在不告警时，它的代价是一次 bool 判断、一次时间戳比较，以及（每秒一次）一次对
    // kernel32!GlobalMemoryStatusEx 的调用，而后者不分配任何内存。
    //
    // 级别（见 LevelFor）：
    //   normal   高于 WarnMiB 时的 sysCommitAvail（默认 1536 MiB）
    //   WARN     等于或低于 WarnMiB：最多每 5 秒一行 [MemWatch]，携带诊断所需的
    //            每一个数值（本进程的 committed / unityAllocated，以及我们的队列）
    //   CRITICAL 等于或低于 CriticalMiB（默认 1024 MiB）：同样的一行但标记为 CRITICAL，外加缓解动作
    // WARN 带只有高于 WarnMiB + 512 MiB 才会离开，因此 1.5 GiB 的带宽不会把 5 秒的限速
    // 变成每秒一行。
    //
    // 缓解动作（独立开关，仅在低于 CRITICAL 时布防）就是"归还"的那一半：它会暂停我们自己的
    // 低优先级工作，并最多每 30 秒运行一次 transit 路径已证明安全的释放。
    // 它放弃的每一项都是纯观测或可重建的；任何承载库存或
    // 同步状态的东西都不会被丢弃。清单、原因以及一键回退都在
    // analysis/commit-watch/REPORT.md 中。

    /// <summary>读取 commit 的频率。每秒一次读取不花代价，且足以画出曲线。</summary>
    private const long ProbeMillis = 1000L;

    /// <summary>告警行的硬性限速：任务书要求的"最多每 5 秒一行"。</summary>
    private const long WatchReportMillis = 5000L;

    /// <summary>机器持续低于 CRITICAL 时，两次释放之间的最短间隔。</summary>
    private const long ReleaseCooldownMillis = 30000L;

    /// <summary>离开告警带所需要的、高于告警阈值的距离（迟滞）。</summary>
    private const long ReleaseHysteresisMiB = 512L;

    private static long _nextProbeAt;
    private static int _level = -1;
    private static long _lastWatchAt;
    /// <summary>上次释放时的运行时长。用零（而非 long.MinValue），这样第一次冷却不会溢出。</summary>
    private static long _lastReleaseAt;
    private static bool _reliefActive;
    private static long _reliefArmedAt;
    private static int _reliefArmedFrame = -1;
    private static bool _releaseInFlight;
    private static long _releases;
    private static long _reliefSkips;
    private static long _everLow;

    private static bool _windowBaselineValid;
    private static long _baselineCommitted;
    private static long _baselineUnity;
    private static long _baselineManaged;
    private static long _baselineAvailable;
    private static string _baselineReason = "none";

}
