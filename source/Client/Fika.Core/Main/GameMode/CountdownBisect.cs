using System;
using BepInEx.Configuration;

namespace Fika.Core.Main.GameMode;

/// <summary>
/// raid 启动倒计时的二分（bisect）开关
/// （<see cref="BaseGameController.CountdownScreen"/>）。
///
/// 第 1 轮（analysis/countdown-bisect）把三条纯装饰性语句放到一个开关后面，并让它们以关闭状态
/// 发布。11:17 那次运行证明这是必要但不充分的：在
/// screen=False, audioFades=False, menuMusic=False 的情况下，主体仍被进入，进程仍在
/// 几秒后死亡，而且日志也无法指出是哪条语句 —— 追踪轨迹已经失效
/// （analysis/countdown-bisect-round2、Fika-crash.log）。
///
/// 因此第 2 轮把*保留*的语句也一起二分：
///   * 剩下的八条语句各自拥有自己的日志标签和自己的开关，全部以开启状态
///     发布；标签会立刻写入崩溃追踪（在调用线程上的一次 WriteFile），因此
///     即使追踪的持久化线程再也不写入，下一次运行的追踪也能指出是哪条语句；
///   * 一个总开关 —— 'Skip Countdown Body'，以开启状态发布 —— 会完全跳过主体（装饰性与保留的
///     语句一视同仁；它直接跳到部署等待然后返回），这样无论哪条语句是
///     罪魁，raid 都能启动。重新启用的顺序记录在 analysis/countdown-bisect-round2/REPORT.md 中。
///
/// 11:17 运行之后的嫌疑顺序（死亡发生在主体被进入后几秒，也就是在
/// 部署等待仍处于挂起状态时，因此主体里的东西都不在栈上）：1. TimerPanel.ProfileId（一次
/// 对本机 ExtractionTimersPanel setter 的托管调用），2. GameUi.gameObject.SetActive(true)
/// （激活一个本机层级，即每个组件的 OnEnable），3. VOIP 块
/// （FixVOIPAudioDevice -> Dissonance 本机库），然后是 raid-view 协程的泵启动、
/// RestoreRaidViewAfterCountdown 本身、SyncTransitControllers 以及 raid-started 事件。总开关
/// 的存在就是为了不必在 raid 能玩之前去猜这个顺序。
/// </summary>
internal static class CountdownBisect
{
    /// <summary>日志标签：倒计时主体已开始，即加载界面已被隐藏。</summary>
    internal const string BoundaryBody = "countdown: body entered (the loading screen was hidden)";

    /// <summary>日志标签：主体已结束（部署等待已返回，且每条被允许的语句都已执行）。</summary>
    internal const string BoundaryBodyDone = "countdown: body done (the deploy wait returned; the labels after this one are post-countdown)";

    /// <summary>总开关的日志标签：主体跳过了它拥有的每一条语句。</summary>
    internal const string BoundarySkipBody = "countdown stmt 0/8: MASTER SWITCH 'Skip Countdown Body' is ON - the whole body is skipped (cosmetic and kept statements alike) and only the deploy wait runs";

    /// <summary>
    /// 第 3 轮第二个总开关的日志标签：整个等待后区块被跳过，因此 raid
    /// 不执行 4/8 至 8/8 的任何一条语句就进入会话序列（步骤 3/3）。
    /// </summary>
    internal const string BoundaryPostWait = "countdown post-wait block (stmts 4/8-8/8: restore raid view, raid view routine, transit sync, raid-started event, VOIP)";

    /// <summary>日志标签：协程让出并进入部署等待（泵把它停在这里）。</summary>
    internal const string BoundaryDeployWaitYield = "countdown 399: yielding into the deploy wait NOW - the pump parks this routine, nothing of it runs until it is resumed";

    /// <summary>日志标签：部署等待已返回 —— 等待之后执行的第一行。</summary>
    internal const string BoundaryDeployWaitResumed = "countdown 399: the deploy wait RETURNED - the routine resumed (the next label is the first statement after the wait)";

    // ------------------------------------------------------------------------------------------------
    // 第 3 轮 —— 会话序列的步骤 1/3（FinishRaidSetup）。
    // 13:45 那次运行在步骤 1/3 内让帧计数器冻结了 3.1 秒：泵为
    // frame 17849 写下了它的 tick 标签，之后主线程在 3.1 秒内什么都没写，因此进程所在的帧
    // 只是被括定而没有名字。这些标签正是把该协程的语句逐条标出，方式与倒计时
    // 主体完全一致 —— 调用之前一个标签、调用之后一个 RETURNED —— 这样下一份追踪就能指出那个窗口。
    // 仅用于诊断：它们都没有开关，也都不改变行为。
    // ------------------------------------------------------------------------------------------------

    /// <summary>日志标签：会话序列的步骤 1/3 已进入。</summary>
    internal const string BoundaryFinishSetup = "FinishRaidSetup (session sequence step 1/3): entered";

    /// <summary>日志标签：步骤 1/3 的加载界面广播。</summary>
    internal const string BoundaryFinishLoading90 = "FinishRaidSetup: LoadingScreenUI.UpdateAndBroadcast(90f)";

    /// <summary>日志标签：音乐淡出及其逐帧等待。</summary>
    internal const string BoundaryFinishMusicWait = "FinishRaidSetup: FadeBackgroundMusicAsync + wait for the music task";

    /// <summary>日志标签：音频缓冲区重置。</summary>
    internal const string BoundaryFinishAudioBuffer = "FinishRaidSetup: AudioUtils.ResetAudioBuffer()";

    /// <summary>日志标签：triggers 模块被添加到游戏对象上。</summary>
    internal const string BoundaryFinishTriggers = "FinishRaidSetup: AddComponent<LocalFikaTriggersModule>()";

    /// <summary>日志标签：lamp 控制器已填充。</summary>
    internal const string BoundaryFinishLamps = "FinishRaidSetup: _gameWorld.FillLampControllers()";

    /// <summary>日志标签：seasons 控制器运行，并等待它的 task。</summary>
    internal const string BoundaryFinishSeasonRun = "FinishRaidSetup: SeasonsController.Run + wait for the season task";

    /// <summary>日志标签：无线电广播已启动。</summary>
    internal const string BoundaryFinishBroadcast = "FinishRaidSetup: RadioBroadcastController.StartBroadcast()";


    /// <summary>倒计时第 389 行的日志标签：最终倒计时浮层（装饰性）。</summary>
    internal const string BoundaryScreen = "countdown 389: FinalCountdownScreenController + ShowScreen (cosmetic)";

    /// <summary>倒计时第 390-394 行的日志标签：音频淡出（装饰性）。</summary>
    internal const string BoundaryAudioFades = "countdown 390-394: AmbientAudioSystem.Initialize + BetterAudio.FadeInVolumeBeforeRaid (cosmetic)";

    /// <summary>倒计时第 395-396 行的日志标签：菜单音乐调用（装饰性）。</summary>
    internal const string BoundaryMenuMusic = "countdown 395-396: GUISounds.BackgroundMusicActive + StopMenuBackgroundMusicWithDelay (cosmetic)";

    /// <summary>保留语句 1/8 的日志标签：game-UI 开关（本机层级激活）。</summary>
    internal const string BoundaryGameUi = "countdown 397 (kept stmt 1/8): _abstractGame.GameUi.gameObject.SetActive(true)";

    /// <summary>保留语句 2/8 的日志标签：撤离计时面板的 profile 绑定。</summary>
    internal const string BoundaryTimerPanel = "countdown 398 (kept stmt 2/8): _abstractGame.GameUi.TimerPanel.ProfileId = profileId";

    /// <summary>保留语句 3/8 的日志标签：部署等待。</summary>
    internal const string BoundaryDeployWait = "countdown 399 (kept stmt 3/8): WaitForSeconds(timeBeforeDeployLocal)";

    /// <summary>保留语句 4/8 的日志标签：raid-view 还原（本机相机与屏幕操作）。</summary>
    internal const string BoundaryRestoreRaidView = "countdown post-wait (kept stmt 4/8): RestoreRaidViewAfterCountdown()";

    /// <summary>保留语句 5/8 的日志标签：raid-view 协程的泵启动。</summary>
    internal const string BoundaryRestoreRoutine = "countdown post-wait (kept stmt 5/8): FikaRoutinePump.Start(RestoreRaidViewAfterCountdownRoutine)";

    /// <summary>保留语句 6/8 的日志标签：transit 控制器同步。</summary>
    internal const string BoundarySyncTransits = "countdown post-wait (kept stmt 6/8): SyncTransitControllers(profileId)";

    /// <summary>保留语句 7/8 的日志标签：raid-started 事件。</summary>
    internal const string BoundaryRaidStartedEvent = "countdown post-wait (kept stmt 7/8): FikaEventDispatcher.DispatchEvent(FikaRaidStartedEvent)";

    /// <summary>保留语句 8/8 的日志标签：VOIP 设备修复。</summary>
    internal const string BoundaryVoipFix = "countdown post-wait (kept stmt 8/8): AllowVOIP -> FikaRoutinePump.Start(FixVOIPAudioDevice)";

    // ------------------------------------------------------------------------------------------------
    // raid 内的 HUD 还原（analysis/inraid-ui）。
    //
    // 上面的总开关以开启状态发布，因此语句 1/8（GameUi.gameObject.SetActive(true)）和 2/8
    // （TimerPanel.ProfileId）—— 这两条语句负责打开 raid HUD 层级 —— 在默认的 raid 中
    // 从不执行。于是整个 HUD 层级在整场 raid 中都保持未激活：计时按键
    // 毫无作用、撤离列表与撤离倒计时从不出现、游戏自身的 HUD 代码在已关闭的面板上
    // 被 Unity 拒绝启动协程（"Coroutine couldn't be started because
    // the the game object 'TimerPanel' is inactive!"，analysis/red-errors-coroutines），而该面板的
    // 出生期 SetTime 被驱动守卫跳过（analysis/ui-log-noise）—— 因此也永远没有任何东西去填充
    // 这个面板。
    //
    // 这两个标签括定的是部署等待之后运行的还原动作，它位于自己的一个泵协程中
    // （BaseGameController.RestoreInRaidHud），因此追踪会说明 HUD 是否被重新打开、
    // 以及撤离列表是否被重新推入其中。仅用于诊断：决定这些的开关是
    // 那些总开关以及 'Countdown: Restore In-Raid HUD' 这一项。
    // ------------------------------------------------------------------------------------------------

    /// <summary>日志标签：倒计时之后的 HUD 还原协程已在泵上启动。</summary>
    internal const string BoundaryHudRestore = "countdown post-wait: in-raid HUD restore routine started (analysis/inraid-ui) - GameUi activation + extraction timer panel";

    /// <summary>日志标签：HUD 还原协程写下了它的终态并返回。</summary>
    internal const string BoundaryHudRestoreDone = "countdown post-wait: in-raid HUD restore done (the next labels are in-raid)";

    /// <summary>本场 raid 中倒计时主体被允许做什么。每场 raid 读取一次。</summary>
    internal readonly struct Switches
    {
        internal Switches(bool screen, bool audioFades, bool menuMusic, bool skipBody, bool skipPostWait,
            bool gameUi, bool timerPanel, bool deployWait, bool restoreRaidView, bool restoreRoutine,
            bool syncTransits, bool raidStartedEvent, bool voipFix)
        {
            Screen = screen;
            AudioFades = audioFades;
            MenuMusic = menuMusic;
            SkipBody = skipBody;
            SkipPostWait = skipPostWait;
            GameUi = gameUi;
            TimerPanel = timerPanel;
            DeployWait = deployWait;
            RestoreRaidView = restoreRaidView;
            RestoreRoutine = restoreRoutine;
            SyncTransits = syncTransits;
            RaidStartedEvent = raidStartedEvent;
            VoipFix = voipFix;
        }

        /// <summary>显示最终倒计时浮层（配置项：'Show Final Countdown Screen'）。</summary>
        internal bool Screen { get; }

        /// <summary>执行 ambient/BetterAudio 淡出（配置项：'Raid Start Audio Fades'）。</summary>
        internal bool AudioFades { get; }

        /// <summary>停止菜单背景音乐（配置项：'Raid Start Menu Music'）。</summary>
        internal bool MenuMusic { get; }

        /// <summary>总开关：跳过整个主体（配置项：'Skip Countdown Body'）。</summary>
        internal bool SkipBody { get; }

        /// <summary>
        /// 第 3 轮针对等待后区块的总开关：把 4/8 至 8/8 作为一组跳过
        /// （配置项：'Countdown: Skip Post Wait Block' = 'Skip Post Wait Block'）。
        /// </summary>
        internal bool SkipPostWait { get; }

        /// <summary>语句 1/8：激活游戏 UI（配置项：'Countdown: Activate Game UI'）。</summary>
        internal bool GameUi { get; }

        /// <summary>语句 2/8：绑定撤离计时面板（配置项：'Countdown: Bind Extraction Timer Panel'）。</summary>
        internal bool TimerPanel { get; }

        /// <summary>语句 3/8：等待部署时间（配置项：'Countdown: Deploy Wait'）。</summary>
        internal bool DeployWait { get; }

        /// <summary>语句 4/8：还原 raid 视图（配置项：'Countdown: Restore Raid View'）。</summary>
        internal bool RestoreRaidView { get; }

        /// <summary>语句 5/8：启动 raid-view 协程（配置项：'Countdown: Start Raid View Routine'）。</summary>
        internal bool RestoreRoutine { get; }

        /// <summary>语句 6/8：同步 transit 控制器（配置项：'Countdown: Sync Transit Controllers'）。</summary>
        internal bool SyncTransits { get; }

        /// <summary>语句 7/8：派发 raid-started 事件（配置项：'Countdown: Dispatch Raid Started Event'）。</summary>
        internal bool RaidStartedEvent { get; }

        /// <summary>语句 8/8：执行 VOIP 音频修复（配置项：'Countdown: VOIP Audio Fix'）。</summary>
        internal bool VoipFix { get; }

    }

    /// <summary>
    /// 读取两个总开关（'Skip Countdown Body'、'Skip Post Wait Block'）；装饰性语句与
    /// 八条保留语句由常量决定，<see cref="Read"/> 中有记录（analysis/config-audit-2）。两个
    /// 总开关在配置尚未绑定时（无头运行，或在本 locales 加载之前就已启动的 raid）都默认为开启：
    /// 被跳过的语句不会破坏一场 raid —— 主体那时根本不接触任何本机对象 ——
    /// 而运行中的语句可以。发布版配置用的也正是这个默认值。
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

            // (analysis/config-audit-2, 2026-10-13) 那十一项按语句/按装饰项的配置项已被
            // 移除：在两个总开关都为开启（即发布版配置）的情况下，那些值一个都
            // 不可达；而二分所追查的那些死亡，是在本协程停驻于
            // 部署等待时抛出的（analysis/countdown-bisect-round3 第 1.2 节），而不是由主体中的某条语句抛出。下面
            // 这些常量正是被移除配置项的发布默认值：三条装饰性语句保持关闭
            // （总开关存在的目的就是避免运行中的装饰性语句），各项保留语句
            // 保持开启，因此再次关掉总开关就能精确还原第 3 轮的行为。
            // (2026-10-14, diagnostics removal) 两个二分总开关随其余的
            // 移植诊断一起被移除：发布版配置把它们两个都设为开启，也就是被移植的倒计时主体
            // 及其等待后区块被跳过，而玩家看到的倒计时由
            // CountdownRestore 播放。那些不可达的语句路径留在 BaseGameController 中供参考。
            _ = settings;
            return new Switches(false, false, false, true, true,
                true, true, true, true, true, true, true, true);
        }
        catch (Exception)
        {
            return Unbound();
        }
    }

    /// <summary>
    /// 装饰性语句关闭、两个总开关开启（跳过主体与等待后区块）、各项语句开启 ——
    /// 详见 <see cref="Read"/>。两个总开关保持开启，因为被跳过的语句不会破坏一场 raid。
    /// </summary>
    private static Switches Unbound()
    {
        return new Switches(false, false, false, true, true, true, true, true, true, true, true, true, true);
    }

}
