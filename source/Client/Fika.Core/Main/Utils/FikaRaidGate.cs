using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using Fika.Core.Main.GameMode;

namespace Fika.Core.Main.Utils;

/// <summary>
/// AP (analysis/nonfika-raid-inertness)：本移植每个 raid 内钩子在动手之前都必须问的唯一问题 ——
/// 「此刻正在运行的 raid 是我们自己的吗？」。
///
/// 为什么新立一道门禁而不用 <see cref="FikaBackendUtils.ClientType"/>：那个字段在创建或加入
/// 一场 Fika 对局时被设置，且在 raid 结束时*不*被重置，所以会话中仅一场 Fika raid 就会
/// 让它在此后整个进程里一直停在 Host/Client。因此每一处写成
/// <c>ClientType != None</c> 的「这是不是一场 Fika raid」判断，在下一场原生单人 raid 内部
/// 同样为真 —— 而 2026-10-12 23:52 的崩溃正是在那种状态下发生的：raid 是从地图
/// 选择界面在没有 Fika 的情况下启动的（本移植自己的行写着 <c>side=single</c>），引擎运行的是原生
/// 本地游戏，而我们的 raid 内钩子仍然在它上面生效。
///
/// 三条规则，按此顺序：
///   1. 门禁由*我们自己游戏对象的创建*来武装，而不是由插件被加载，
///      也不是由一个 raid 可能遗留的字段；
///   2. 一场不属于我们的 raid 一开始，以及我们的 raid 结束时，它立即解除武装；
///   3. 它会自我纠正：在武装状态下，对引擎真正正在运行的游戏做一次限流的查看，
///      一旦能证明该游戏不是 <see cref="CoopGame"/>，就把它降级。一个过期的「是」
///      是唯一能害死我们的答案，所以它也是唯一会被反复复核的答案。
///
/// 开关 <see cref="InertOutsideFikaRaids"/>（配置 "Coop / Inert Outside Fika Raids"，
/// 默认 true）用一行就能关掉整套行为：关闭后 <see cref="Skip"/> 始终返回 false，
/// 每个调用方的行为都与本类存在之前完全一致。
/// </summary>
internal static class FikaRaidGate
{
    /// <summary>
    /// 这套修复的总开关。开启（默认）：在 Fika raid 之外，每个
    /// <see cref="Skip"/> 调用方都收手不干。关闭：修复之前的行为，一行即可回退。
    /// </summary>
    internal static bool InertOutsideFikaRaids = true;

    /// <summary>
    /// 一个不属于我们的游戏可以成为当前运行游戏多久，本门禁才会相信它。我们自己的
    /// 游戏对象是在门禁被武装*之后*才创建的（CoopGame.Create -> Singleton&lt;AbstractGame&gt;.Create），
    /// 而且一次中转重载会替换它，所以单次「不是我们的」观察并不构成判决。
    /// </summary>
    private const int MismatchGraceMillis = 3000;

    /// <summary>对运行中游戏做校验查看的限流间隔。</summary>
    private const int VerifyIntervalMillis = 500;

    /// <summary>两次「我们仍在跳过」汇总之间的秒数。</summary>
    private const int SummarySeconds = 60;

    private static readonly object Gate = new();
    private static readonly HashSet<string> FirstLineWritten = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> Skips = new(StringComparer.Ordinal);

    private static bool _armed;
    private static string _armedSource = "none";
    private static long _armedAt;
    private static long _nextVerify;
    private static long _nextSummary;
    private static long _mismatchSince;
    private static int _demotions;
    private static int _skipsSinceSummary;

    /// <summary>本类的判决：当本进程运行着一场由 Fika 创建的 raid 时为 true。</summary>
    internal static bool InFikaRaid
    {
        get
        {
            return _armed;
        }
    }

    /// <summary>
    /// 调用方不得行动时为 true。调用方只需按下面这样写，
    /// <c>if (FikaRaidGate.Skip("Owner.Method")) return true; // native</c>
    /// 就免费获得整套诊断。
    /// </summary>
    internal static bool Inert
    {
        get
        {
            return InertOutsideFikaRaids && !_armed;
        }
    }

    /// <summary>
    /// 本移植每个钩子都用的这一行。调用方必须退出时返回 true；每个成员的第一
    /// 次调用会写出<br/>
    /// <c>[Inert] skip=&lt;member&gt; reason=not-a-fika-raid side=single</c><br/>
    /// （一次写入游戏日志、一次写入诊断文件，之后只记计数，另加每分钟一次
    /// 汇总），因此一次 grep 就能回答「我们的任何东西有没有在一场不属于我们的 raid 里动过」。
    /// </summary>
    internal static bool Skip(string member)
    {
        if (InFikaRaid || !InertOutsideFikaRaids)
        {
            return false;
        }

        // 校验查看在报告之前运行：一道武装中的门禁如果真的在看别人的
        // raid，就必须在这里给自己降级，而不是在一行关于它的日志写完之后。
        VerifyArmed();
        if (InFikaRaid)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 由唯一创建 Fika 游戏的方法（<c>CreateFikaGameCore</c>）调用。这是唯一
    /// 知道我们的一场 raid 真正开始的地方。
    /// </summary>
    internal static void Arm(string source)
    {
        _armed = true;
        _armedSource = string.IsNullOrEmpty(source) ? "unspecified" : source;
        _armedAt = Environment.TickCount64;
        _mismatchSince = 0;
        _nextVerify = 0;
        _demotions = 0;
        // 一场 raid 的第一次汇总在一分钟时到来，而不是在第二个被跳过的钩子之后：每个成员的
        // 第一行才是证据，汇总只是为了让一场漫长的 raid 仍可读。
        _nextSummary = Environment.TickCount64 + SummarySeconds * 1000L;
    }

    /// <summary>
    /// 由 raid 启动补丁在把 raid 交给*原生*本地游戏时调用
    /// （<c>clientType=None</c>，即没有 Fika 的地图选择界面）。这是此后
    /// 一切的决定性时刻：从这里起，本移植在再次创建自己
    /// 的游戏之前不得行动。
    /// </summary>
    internal static void NativeRaidStarted(string source)
    {
        var wasArmed = _armed;
        _armed = false;
        _mismatchSince = 0;
        if (!wasArmed && _nativeStartReported)
        {
            // 每次状态转移一行，另加会话中第一场原生 raid 的一行：一个有
            // 五场单人 raid 的会话不能写出五行相同的内容，而*第一*行才是证据。
            return;
        }

        _nativeStartReported = true;
    }

    /// <summary>我们的一场 raid 结束时调用（启动失败，或游戏自身的清理）。</summary>
    internal static void Disarm(string reason)
    {
        if (!_armed)
        {
            return;
        }

        _armed = false;
        _mismatchSince = 0;
    }

    /// <summary>
    /// 本进程认为自己拥有的角色，写法与集结门禁的诊断完全一致，这样
    /// 本移植的每一行日志都使用同一套词汇。
    /// </summary>
    internal static string Side
    {
        get
        {
            try
            {
                if (FikaBackendUtils.IsHeadless)
                {
                    return "headless";
                }

                if (FikaBackendUtils.IsServer)
                {
                    return "host";
                }

                if (FikaBackendUtils.IsClient)
                {
                    return "client";
                }
            }
            catch (Exception)
            {
            }

            return "single";
        }
    }

    private static bool _nativeStartReported;

    /// <summary>
    /// 限流的校验，可由每帧驱动器（插件的 Update）调用，这样即使某一帧
    /// 没有任何钩子发问，门禁也会给自己降级。永不抛出异常。
    /// </summary>
    internal static void Verify() => VerifyArmed();

    /// <summary>
    /// 规则 3 中描述的校验查看。构造上就很廉价：一次限流检查，且在
    /// 限流之内只有一次单例读取。永不抛出异常，永不阻塞。
    /// </summary>
    private static void VerifyArmed()
    {
        if (!_armed)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now < _nextVerify)
        {
            return;
        }

        _nextVerify = now + VerifyIntervalMillis;
        try
        {
            var game = Singleton<AbstractGame>.Instantiated ? Singleton<AbstractGame>.Instance : null;
            if (game == null)
            {
                // 两场 raid 之间没有什么可判决的；下一次 raid 启动会武装或解除我们。
                return;
            }

            if (game.TryCast<CoopGame>() != null)
            {
                _mismatchSince = 0;
                return;
            }

            if (_mismatchSince == 0)
            {
                _mismatchSince = now;
                return;
            }

            if (now - _mismatchSince < MismatchGraceMillis)
            {
                return;
            }

            _armed = false;
            _mismatchSince = 0;
            _demotions++;
        }
        catch (Exception)
        {
            // 校验绝不能成为某个边界失败的原因。
        }
    }

}
