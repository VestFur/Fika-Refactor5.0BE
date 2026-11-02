using System;
using BepInEx.Configuration;
using EFT;
using EFT.UI.Matchmaker;
using EFT.UI.Screens;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.GameMode;

/// <summary>
/// 突袭的开场倒计时——也就是把部署时间倒数下去的那层覆盖界面——在倒计时
/// 主体被跳过的情况下重新补回来（analysis/countdown-restore）。
///
/// 它现在为何消失了：'Skip Countdown Body'（默认开启）会跳过
/// <see cref="BaseGameController.CountdownScreen"/> 中的每一条语句，而显示倒计时的那条语句正是其中
/// 第一条（语句 389，<c>new MatchmakerFinalCountdown.FinalCountdownScreenController(profile,
/// dateTime).ShowScreen(EScreenState.Root)</c>）。例程仍然会走到部署等待，于是玩家
/// 是从一个从未真正倒数过的界面里部署出去的。
///
/// 它现在补回来为何是安全的：总开关当初被加进来，是因为主机端的突袭死在了
/// "countdown -> deploy wait" 这个窗口内（0x80131506）。第 3 轮把最后几次这类死亡的
/// 轨迹重新读了一遍，发现事情发生时例程正*停在*部署等待里——主体里没有任何语句在
/// 栈上（analysis/countdown-bisect-round3 第 1.2 节）——而更早几轮确实归咎过的两个条件
/// 已经从这条路径上消失了：没有对引擎 <c>WaitForSeconds.m_Seconds</c> 的托管读取（部署等待是
/// 托管的 <see cref="WaitSeconds"/>），没有从托管代码启动的引擎协程，也没有引擎
/// 对注入方法的反射。这个类发起的每一次调用都是托管 -> 原生、作用在*原生*对象
/// （屏幕管理器、倒计时屏幕控制器）上，也就是合法的方向。
///
/// 补回的内容如下，按玩家看到的顺序：
/// <list type="number">
/// <item><description><b>1/4——覆盖界面</b>：与语句 389 完全相同的调用，因此玩家看到的
/// 倒计时就是游戏自己的界面（它从构造控制器时传入的 <c>gameStartTime</c> 读取剩余
/// 时间，而该值是 "now + TimeBeforeDeployLocal"——正是原版主体所传
/// 的那个值）。</description></item>
/// <item><description><b>部署等待</b>：语句 3/8，无论总开关是否开启它都会执行，
/// 因此它就是覆盖界面的时钟。这个类不会再额外加一次等待——倒计时和
/// 部署等待一样长，不会再长。</description></item>
/// <item><description><b>2/4——关闭</b>：把覆盖界面重新收掉（<c>CloseAllScreensForced</c>，
/// 由 "倒计时屏幕是否就是当前屏幕" 这个条件守护）。它有自己的开关，因为 "覆盖界面无法
/// 被收掉" 和 "突袭视图没有恢复" 是两种不同的故障。</description></item>
/// <item><description><b>3/4——突袭视图</b>：语句 4/8 的主体
/// （<see cref="BaseGameController.RestoreRaidViewAfterCountdown()"/>）。它存在的意义正是 "倒计时
/// 留下了它的模糊/特效/菜单相机"，因此直接复用而不是重新推导。</description></item>
/// <item><description><b>4/4——重新断言</b>：语句 5/8 的主体，挂在 Update 泵上启动
/// （绝不作为引擎协程）：原生屏幕可能在关闭后一帧才应用它的后处理。
/// </description></item>
/// </list>
///
/// 每个步骤都有自己的配置开关和自己的日志标签，因此下一次轨迹就能指出死掉的是
/// 哪一步；整个补回的总开关是配置文件里的一行。
/// </summary>
internal static class CountdownRestore
{
    /// <summary>总开关的日志标签：整个开场倒计时被补回。</summary>
    internal const string BoundaryPlay = "countdown restore 0/4: MASTER SWITCH 'Countdown: Play Opening Countdown' is ON - the opening countdown (the overlay and its teardown) runs while 'Skip Countdown Body' skips the body";

    /// <summary>步骤 1/4 的日志标签：原版倒计时覆盖界面被构造并显示。</summary>
    internal const string BoundaryOverlay = "countdown restore 1/4: FinalCountdownScreenController + ShowScreen(EScreenState.Root) (the countdown overlay, the same call as statement 389)";

    /// <summary>覆盖界面升起后立即写入的日志标签：随后的部署等待就是玩家看到的倒计时。</summary>
    internal const string BoundaryOverlayUp = "countdown restore 1/4: the countdown overlay is ON SCREEN NOW - the deploy wait that follows is the countdown the player sees, and its length is what 'Countdown: Deploy Wait' decides";

    /// <summary>收尾代码块（步骤 2/4-4/4）的日志标签，在部署等待返回后写入。</summary>
    internal const string BoundaryTeardown = "countdown restore: the deploy wait returned - the countdown's teardown follows (2/4 close, 3/4 raid view, 4/4 re-assert)";

    /// <summary>步骤 2/4 的日志标签：倒计时屏幕被关闭。</summary>
    internal const string BoundaryClose = "countdown restore 2/4: EftScreenManager.CloseAllScreensForced() while the FinalCountdown screen is the current screen";

    /// <summary>步骤 3/4 的日志标签：倒计时留下的突袭视图被恢复。</summary>
    internal const string BoundaryViewRestore = "countdown restore 3/4: RestoreRaidViewAfterCountdown() - the countdown's camera/effect/screen restore (the body of statement 4/8)";

    /// <summary>步骤 4/4 的日志标签：后处理重新断言例程在泵上启动。</summary>
    internal const string BoundaryReassert = "countdown restore 4/4: FikaRoutinePump.Start(RestoreRaidViewAfterCountdownRoutine) - the post-processing re-assert (the body of statement 5/8)";

    /// <summary>日志标签：补回流程已运行，倒计时已被收掉。</summary>
    internal const string BoundaryDone = "countdown restore: done - the countdown ran and was taken down (the labels after this one are in-raid)";

    /// <summary>本次突袭中开场倒计时被允许做什么。每场突袭读取一次。</summary>
    internal readonly struct Switches
    {
        internal Switches(bool play, bool overlay, bool close, bool viewRestore, bool reassert)
        {
            Play = play;
            Overlay = overlay;
            Close = close;
            ViewRestore = viewRestore;
            Reassert = reassert;
        }

        /// <summary>总开关：是否运行开场倒计时（配置项：'Countdown: Play Opening Countdown'）。</summary>
        internal bool Play { get; }

        /// <summary>步骤 1/4：显示覆盖界面（配置项：'Countdown: Opening Countdown Overlay'）。</summary>
        internal bool Overlay { get; }

        /// <summary>步骤 2/4：重新关闭它（配置项：'Countdown: Opening Countdown Close'）。</summary>
        internal bool Close { get; }

        /// <summary>步骤 3/4：恢复突袭视图（配置项：'Countdown: Opening Countdown View Restore'）。</summary>
        internal bool ViewRestore { get; }

        /// <summary>步骤 4/4：在有限帧数内重新断言清晰视图（配置项：'Countdown: Opening Countdown Reassert'）。</summary>
        internal bool Reassert { get; }

        internal string Describe()
        {
            return "play=" + Play + ", 1/4 overlay=" + Overlay + ", 2/4 close=" + Close
                + ", 3/4 viewRestore=" + ViewRestore + ", 4/4 reassert=" + Reassert;
        }
    }

    /// <summary>
    /// 读取这五个开关。尚未绑定的条目——无头模式，或在语言包加载完成之前就
    /// 开始的突袭——都按开启计算，这与突袭内 HUD 补回所用的约定一致：倒计时是
    /// 玩家看得见的东西，而每个开关都有自己的标签，因此未绑定的读取会出现在轨迹里
    /// 而不是悄无声息。无头的情况由 <see cref="FikaBackendUtils.IsHeadless"/> 决定，而
    /// 不是由这个默认值决定：无头客户端根本没有倒计时屏幕可以显示。
    /// </summary>
    internal static Switches Read()
    {
        try
        {
            var settings = FikaPlugin.Instance?.Settings;
            if (settings is null)
            {
                return Unbound();
            }

            // (analysis/config-audit-2, 2026-10-13) 四个步骤条目已并入总开关：
            // 这些步骤就是已发布的行为（全部开启），而 'Countdown: Play Opening Countdown' 是唯一
            // 能把整个开场倒计时关掉的一行。
            return new Switches(Statement(settings.CountdownPlayOpening), true, true, true, true);
        }
        catch (Exception)
        {
            return Unbound();
        }
    }

    /// <summary>所有步骤皆开启，见 <see cref="Read"/>。</summary>
    private static Switches Unbound()
    {
        return new Switches(true, true, true, true, true);
    }

    /// <summary>步骤开关：除非条目另有说明，否则为开启（未绑定的条目不算 "关闭"）。</summary>
    private static bool Statement(ConfigEntry<bool> entry)
    {
        try
        {
            return entry is null || entry.Value;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// 步骤 1/4：显示倒计时覆盖界面，即总开关所跳过的那条语句。当覆盖界面已升起
    /// 时返回 true，这同时也是部署等待之后执行收尾的条件（"没有东西可收" 与
    /// "覆盖界面无法显示" 不是同一条语句，轨迹会说明是哪一种
    /// 情况）。
    ///
    /// 该调用与原版主体所做的完全相同，参数也相同：当前档案，以及
    /// 例程已经算好的游戏开始时间 "now + TimeBeforeDeployLocal"。
    /// </summary>
    internal static bool ShowIfEnabled(Switches switches, Profile profile, Il2CppSystem.DateTime gameStartTime)
    {
        if (FikaBackendUtils.IsHeadless)
        {
            return false;
        }

        if (!switches.Play)
        {
            return false;
        }



        if (!switches.Overlay)
        {
            return false;
        }

        if (profile is null)
        {
            
            return false;
        }

        if (!FikaBoundaries.Run(BoundaryOverlay, () => Show(profile, gameStartTime)))
        {
            // 标签在调用之前就已写入，且其后没有 RETURNED 行：[FAIL]
            // [Boundary] 行在上面点出了异常。fail fast 根本无法被捕获——那样轨迹
            // 就停在这个标签上，这正是该步骤可读的原因。
            return false;
        }

        
        return true;
    }

    /// <summary>步骤 2/4：把覆盖界面重新收掉。仅当步骤 1/4 确实显示了它时才运行。</summary>
    internal static void CloseIfEnabled(Switches switches)
    {

        if (!switches.Close)
        {
            return;
        }

        if (FikaBoundaries.Run(BoundaryClose, CloseOverlay))
        {
        }
    }

    /// <summary>
    /// 倒计时屏幕按原版倒计时之后那条路径的方式来关闭，且仅当它确实是当前
    /// 屏幕时才关闭：原生屏幕可能在它自己的时钟到达游戏开始时间的那一刻就自行
    /// 关闭了（"ShowControls" 这类屏幕就是这样），此时无事可做，报告
    /// 行会如此说明。原版尾部中与这一配对相对应的玩家所有者重新就绪属于步骤
    /// 3/4，它调用的是主体所调用的同一条语句 4/8。
    /// </summary>
    private static void CloseOverlay()
    {
        var screens = EftScreenManager.Instance;
        if (screens is null)
        {
            
            return;
        }

        if (screens.CheckCurrentScreen(EEftScreenType.FinalCountdown))
        {
            screens.CloseAllScreensForced();
            
            return;
        }

        
    }

    /// <summary>
    /// 覆盖界面：与语句 389 完全相同的调用（保持为单个表达式，以免两个调用点
    /// 彼此漂移）。控制器从构造它时传入的游戏开始时间读取剩余时间，因此
    /// 覆盖界面自己就把部署时间倒数下去；这个移植版本只决定何时显示、何时收掉。
    /// </summary>
    private static void Show(Profile profile, Il2CppSystem.DateTime gameStartTime)
    {
        new MatchmakerFinalCountdown.FinalCountdownScreenController(profile, gameStartTime).ShowScreen(EScreenState.Root);
    }
}
