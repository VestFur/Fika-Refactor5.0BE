using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.UI;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;


#if GOLDMASTER
using Fika.Core.UI; 
#endif
using Fika.Core.UI.Patches;
using static Fika.Core.FikaPlugin;
using static Fika.Core.Networking.IFikaNetworkManager;

namespace Fika.Core;

public sealed class FikaConfig(ConfigFile config)
{
    private readonly ConfigFile _config = config;

    #region config values

    // 隐藏（Hidden）
    public ConfigEntry<string> LastVersion { get; set; }

    //高级（Advanced）
    public ConfigEntry<bool> OfficialVersion { get; set; }
    public ConfigEntry<bool> DevMode { get; set; }
    public ConfigEntry<bool> NoAI { get; set; }
    public ConfigEntry<bool> NoLoot { get; set; }
    public ConfigEntry<ELoadPriority> LoadPriority { get; set; }
    public ConfigEntry<int> MaxBundleLock { get; set; }

    // 联机（Coop）
    public ConfigEntry<bool> UseHeadlessIfAvailable { get; set; }
    public ConfigEntry<bool> ShowNotifications { get; set; }
    public ConfigEntry<bool> AutoExtract { get; set; }
    public ConfigEntry<bool> ShowExtractMessage { get; set; }
    public ConfigEntry<KeyboardShortcut> ExtractKey { get; set; }
    public ConfigEntry<bool> ShowPlayerList { get; set; }
    public ConfigEntry<bool> EnableChat { get; set; }
    public ConfigEntry<KeyboardShortcut> ChatKey { get; set; }
    public ConfigEntry<bool> EnableOnlinePlayers { get; set; }

    // Coop | 信号弹撤离点（需要绿色信号弹的出口）。
    // 开启（默认）保留原版队伍通道：一发信号弹为开火的整个
    // 小队打开该点（EZoneEventType.PlayerByPartyAddedInShotList）。关闭则只把激活
    // 绑定到开火者本人，因此每个玩家都必须自己打信号弹。
    public ConfigEntry<bool> FlareExtractSquadSharesActivation { get; set; }

    public ConfigEntry<bool> FlareReplicateExitVisual { get; set; }

    // Coop | 信号弹成功横幅的显示时长（"Flare signal received. You can extract
    // safely!" / 中文 "道路已开放"）。原版收集器在自己的等待结束后会撤下该通知的
    // 副本；Fika 显示的横幅不属于任何控制器，所以在这里隐藏它，而不是
    // 让它一直留在屏幕上直到战局结束。每次弹窗都会读取，因此该开关是热生效的。
    public ConfigEntry<float> FlareExfilFeedbackSeconds { get; set; }

    // Coop | 游戏自身锁定时钟时间的地图（analysis/map-fixed-time）。原版
    // 匹配面板为每张这类地图携带一个固定的 "<map>FixedTime" 值 - Icebreaker 就是
    // 已安装的 5.0 案例 - 因此实时游戏在那里不提供时间选择。Fika 从不运行
    // 那个面板，所以战局改用玩家的战局设置（通常是战局设置界面留下的
    // -1 "跟随世界时钟"）。开启（默认）：战局按
    // 地图自己的时间开始。关闭：用玩家自己的设置，即修复前的行为。战局
    // 创建时读取，因此改动无需重启即可作用于下一场战局。
    public ConfigEntry<bool> HonorMapFixedTime { get; set; }

    // Coop | 转移启动闸门。开启（默认，由主机要求）：只有当每位
    // 队友都与该点交互并站在点内时，倒计时才开始。关闭：主机一旦
    // 自己的玩家完成交互且所有存活的人类玩家都在点内就开始。
    public ConfigEntry<bool> TransitRequireAllTeammates { get; set; }
    public ConfigEntry<int> TransitCountdownFallbackSeconds { get; set; }

    // Coop | 2026-10-12 的 Factory 案例：与撤离点共用触发器的转移点，
    // 其原版传输分支可能在 Fika 倒计时仍在运行时被拆除，于是倒计时结束但
    // 什么也没发生。开启（默认）：从转移交互被接受的那一刻起压制同位置的
    // 出口，直到转移结束或中止。关闭：只记录日志，不做处理（一次切换＝旧行为）。
    public ConfigEntry<bool> TransitSuppressCoLocatedExits { get; set; }

    // Coop | 参与者集合变化时倒计时的行为（2026-10-12，由主机要求）：
    // 有人离开该点即取消倒计时和整个转移。有人死亡则只移除该
    // 玩家并让其余人继续（原版形态），除非下面那个可选变体开启。
    public ConfigEntry<bool> TransitAbortWhenSomeoneLeaves { get; set; }

    // (analysis/config-audit-2, 2026-10-13) 'Transit: Abort When Someone Dies' 已退役：其默认值
    // （false）就是原版形态 - 死亡把死者移出小组，幸存者继续 - 而
    // 可选的"死亡也取消"变体从未被使用。TransitCountdownGuard.AbortWhenSomeoneDies
    // 是保留该行为的静态字段，所以消失的只是开关，不是规则。

    // Coop | 战局加载同步闸门。2026-10-11 的死锁：主机仍在加载转移重载时，
    // 客户端在固定的 40 秒位置确认上放弃，随后主机永远等待
    // 该对端。现在这些等待由这些值界定（另有传输层，连接断开时
    // 立即失败），因此必须能在现场调整。
    public ConfigEntry<int> LoadSyncHostPeerTimeout { get; set; }
    public ConfigEntry<int> LoadSyncClientHostWaitCeiling { get; set; }
    public ConfigEntry<int> LoadSyncClientTransitCeiling { get; set; }
    public ConfigEntry<int> LoadSyncSilentWarnSeconds { get; set; }

    // Coop | 已结束自身战局的客户端在等待主机"重载开始"信号时，最多可以
    // 压住自己的地图加载多久。该信号让两台机器在一个往返内进入关卡加载；
    // 这个上限只覆盖从不发送它的主机（旧版本、连接已死、没有该
    // 通告的无头模式）。保持它高于主机的实际提前量（实测 44 秒）并低于客户端的（130 秒）。
    public ConfigEntry<int> TransitReloadHoldSeconds { get; set; }

    // Coop | 网络超时判定行的总开关（analysis/transit-timeout-crash）。每一条
    // 本来就记录日志的超时路径，也会写一行 "[NetTimeout] ..."，携带超时
    // 了什么、等了多久、那一刻机器还剩多少提交内存，以及
    // 本次会话自身的队列状态有多少；一次 grep 一次运行，就能为
    // 每次运行而不只是某一次回答"死亡发生时是否伴随网络超时"。关闭＝调用点
    // 的行为与该行存在之前完全一致。

    public ConfigEntry<float> OnlinePlayersScale { get; set; }

    // Coop | 主菜单面板向服务器请求在线列表的频率（analysis/online-players-refresh）。
    // 该列表是实时名册，所以面板可见时按短周期轮询，不可见时按它一直
    // 使用的十秒周期轮询。'Online Players Fast Refresh' 关闭＝之前的行为：
    // 一个固定的十秒周期，且每个周期完全重建各行（一次切换＝旧路径）。
    public ConfigEntry<bool> OnlinePlayersFastRefresh { get; set; }
    public ConfigEntry<int> OnlinePlayersRefreshSeconds { get; set; }
    public ConfigEntry<int> OnlinePlayersIdleRefreshSeconds { get; set; }

    // Coop | 主菜单装饰。每个装饰都有自己的开关，以便下一次崩溃能
    // 直接从配置文件做二分排查：面板是 'Enable Online Players'，计数器
    // 文本是 'Write Online Player Counter'，任务栏按钮是 'Download Profile
    // Button'。'Main Menu Ref Trace' 控制原始指针日志的开关。
    public ConfigEntry<bool> WriteOnlinePlayerCounter { get; set; }
    public ConfigEntry<bool> DownloadProfileButton { get; set; }

    // Coop | 性能。整个诊断通道共用一个开关：关闭（默认）时保留失败、
    // 警告、崩溃轨迹和低频会话日志行，并静默逐调用类的日志族
    // （InvokerTrace、WarmTable、PumpTrace、NativeEntryWarmup、MainMenuUI.trace）。参见 FikaDiagnostics.Verbose。

    // Coop | 性能（analysis/perf-diagnostics-cleanup）。本移植所有*探针*的总开关：
    // 逐帧与逐秒采样器，以及探针标签（[CutsceneDiag]、[LootDiag]、[NvgDiag]、
    // [RallyDiag]、[ExtractDiag]、[PlantDiag]、[MapMarker]、[TransitDiag]、[MemDiag]、[BotOwner]、[Inert]、
    // [LoadCost]），外加崩溃轨迹的预写部分。关闭（默认）时它们全部零成本：
    // 不建字符串、不分配、不采样 il2cpp、不写磁盘。开启＝清理前的行为，一次切换
    // 即可，因此需要重新判定的运行仍能打印它的判定行。
    // 参见 FikaDiagnostics.ProbesEnabled。

    // Coop | 崩溃取证（analysis/crash-dump-20261013）。独立于 'Diagnostics'，同样
    // 默认关闭：它恰好重新启用三种在运行时通过 RaiseFailFastException 杀死进程时
    // 仍能作证的仪器（没有任何向量化异常处理器能看到它）- 崩溃
    // 轨迹的预写部分、它的逐边界环境标签，以及托管的首轮异常记录器。它
    // 刻意不重新启用逐帧探针族；那些仍留在 'Diagnostics' 后面。
    // 参见 FikaDiagnostics.ForensicsEnabled。

    // Coop | 内存守护（analysis/commit-watch）。2026-10-12 19:59:56 的死亡是一次 CLR fail fast
    // （0x80131506，coreclr.dll），当时 sysCommitAvail 为 820.7MiB - 这是*机器*的提交量，而不是
    // 本进程的堆；页面文件是用户侧的设置（当时只有 C: 处于活动状态）。缓解在
    // 1024 MiB 就武装，而不是用 768 MiB 举例，因为死亡线在约 820 MiB。这
    // 五个开关决定
    // Fika 在这件事上叫得多响（观察），以及它在运行时死亡前放弃多少自己的低优先级
    // 工作（缓解）。阈值与档位见 FikaRaidMemory。

    // Coop | 战局开始倒计时，由 analysis/config-audit-2（2026-10-13）收敛。原先的
    // 十二个二分开关（analysis/countdown-bisect、-round2、-round3）和五个开局倒计时步骤
    // 开关，现在是四个条目：
    //   * 'Skip Countdown Body' 和 'Skip Post Wait Block' - 两个保证战局仍可启动的总开关；
    //   * 'Countdown: Play Opening Countdown'  - 玩家看到的倒计时（各步骤已并入其中）；
    //   * 'Countdown: Restore In-Raid HUD'     - 被跳过语句本会打开的 HUD。
    // 退役条目的语句在发布配置下本来就不可达（两个总开关都是
    // 开启），而二分追查的两起死亡被定位为：例程停在部署等待时发生的
    // 原生 -> 托管回调（analysis/countdown-bisect-round3 第 1.2 节），而不是
    // 主体中的某条语句。CountdownBisect.Read()/CountdownRestore.Read() 把退役的默认值保留为常量，所以
    // 回滚只需重新绑定一个键。
    public ConfigEntry<bool> CountdownRestoreHud { get; set; }
    public ConfigEntry<bool> CountdownPlayOpening { get; set; }

    // Coop | 姓名牌
    public ConfigEntry<bool> UseNamePlates { get; set; }
    public ConfigEntry<bool> HideHealthBar { get; set; }
    public ConfigEntry<bool> UseHealthNumber { get; set; }
    public ConfigEntry<bool> UsePlateFactionSide { get; set; }
    public ConfigEntry<bool> HideNamePlateInOptic { get; set; }
    public ConfigEntry<bool> NamePlateUseOpticZoom { get; set; }
    public ConfigEntry<bool> DecreaseOpacityNotLookingAt { get; set; }
    public ConfigEntry<float> NamePlateScale { get; set; }
    public ConfigEntry<float> OpacityInADS { get; set; }
    public ConfigEntry<float> MaxDistanceToShow { get; set; }
    public ConfigEntry<float> MinimumOpacity { get; set; }
    public ConfigEntry<bool> ShowEffects { get; set; }
    public ConfigEntry<bool> UseOcclusion { get; set; }
    public ConfigEntry<Color> FullHealthColor { get; set; }
    public ConfigEntry<Color> LowHealthColor { get; set; }
    public ConfigEntry<Color> NamePlateTextColor { get; set; }
    public ConfigEntry<bool> ShowBrokenLimbs { get; set; }

    // Coop | 任务共享
    public ConfigEntry<EQuestSharingTypes> QuestTypesToShareAndReceive { get; set; }
    public ConfigEntry<bool> QuestSharingNotifications { get; set; }
    public ConfigEntry<bool> EasyKillConditions { get; set; }
    public ConfigEntry<bool> SharedKillExperience { get; set; }
    public ConfigEntry<bool> SharedBossExperience { get; set; }

    // Coop | 标记（Ping）
    public ConfigEntry<bool> UsePingSystem { get; set; }
    public ConfigEntry<KeyboardShortcut> PingButton { get; set; }
    public ConfigEntry<Color> PingColor { get; set; }
    public ConfigEntry<float> PingSize { get; set; }
    public ConfigEntry<int> PingTime { get; set; }
    public ConfigEntry<bool> PlayPingAnimation { get; set; }
    public ConfigEntry<bool> ShowPingDuringOptics { get; set; }
    public ConfigEntry<bool> PingUseOpticZoom { get; set; }
    public ConfigEntry<bool> PingScaleWithDistance { get; set; }
    public ConfigEntry<float> PingMinimumOpacity { get; set; }
    public ConfigEntry<bool> ShowPingRange { get; set; }
    public ConfigEntry<EPingSound> PingSound { get; set; }

    // Coop | 复活
    public ConfigEntry<KeyboardShortcut> GiveUpKey { get; set; }

    // Coop | 调试
    public ConfigEntry<KeyboardShortcut> FreeCamButton { get; set; }
    public ConfigEntry<bool> AZERTYMode { get; set; }
    public ConfigEntry<bool> DroneMode { get; set; }
    public ConfigEntry<bool> KeybindOverlay { get; set; }

    // 网络（Network）
    public ConfigEntry<string> ForceIP { get; set; }
    public ConfigEntry<string> ForceBindIP { get; set; }
    public ConfigEntry<ushort> UDPPort { get; set; }
    public ConfigEntry<bool> UseUPnP { get; set; }
    public ConfigEntry<bool> UseNATPunching { get; set; }
    public ConfigEntry<int> ConnectionTimeout { get; set; }
    public ConfigEntry<ESendRate> SendRate { get; set; }
    public ConfigEntry<bool> UseFikaNATPunchServer { get; set; }
    public ConfigEntry<bool> AllowVOIP { get; set; }
    public ConfigEntry<bool> UseIPv6 { get; set; }
    public ConfigEntry<bool> PreferIPv6 { get; set; }
    public ConfigEntry<int> SimulatedLatencyMs { get; set; }
    public ConfigEntry<int> SimulatedPacketLossPercent { get; set; }

    // 玩法（Gameplay）
    public ConfigEntry<bool> DisableBotMetabolism { get; set; }
    public ConfigEntry<bool> StrictInventorySync { get; set; }
    public ConfigEntry<bool> GroundLootPickupBypass { get; set; }
    public ConfigEntry<bool> GroundLootPickupAutoFallback { get; set; }
    public ConfigEntry<bool> PlantInteractionFix { get; set; }

    // Gameplay | analysis/plant-item-consumption：把已放置物品从背包中取走的原版放置移除
    // （EFT.InventoryLogic.ItemManipulator.Discard -> RemoveOperation），在本移植的
    // 放置路径上从未被执行。开启（默认）时，放置结果会在放置机器自己的控制器上
    // 用这个原版步骤完成。
    public ConfigEntry<bool> PlantConsumesItem { get; set; }

    // AY（analysis/plant-client-host-crash）：原版的放置闸门会触发两次，而它触发的移除
    // 是异步派发的，所以客户端过去会把同一次移除发送两次；第二次
    // 被主机拒绝，而这次拒绝又触发了权威背包重同步，把放置交互
    // 在动画中途释放掉。开启（默认）时，客户端仍然只转发自己的一次放置
    // 移除（由主机应用 - 由
    // logs-coop2-20261013-110333/LogOutput.log:2931-2933 证明）；关闭则把移除严格留在主机侧，
    // 即客户端的放置不在本地消耗任何东西，也从不向主机发送移除。
    public ConfigEntry<bool> PlantConsumeOnClient { get; set; }

    // AY：同一修复的主机侧一半。开启（默认）时，主机已经能判断自己无法完成的
    // 移除类操作（刚接受过的操作之重复、畸形的描述符、无法读取的
    // 预检），会在进入原版重建之前于托管代码中被拒绝，并给出一条 [PlantGuard] 警告和
    // 通常的 Failed + 重同步应答。
    public ConfigEntry<bool> PlantRelayGuard { get; set; }

    // analysis/icebreaker-keypad-door：客户端的密码提交在任何 Fika 数据包之前就被游戏自己的守卫
    // 拒绝（"Tried to enter code on location without passcodes"），所以主机从不知道
    // 这次尝试。这两个开关承载修复它的转发逻辑，以及它自己的判定行。
    public ConfigEntry<bool> KeypadRelayClientSubmit { get; set; }

    // analysis/rally-gate-deadlock，由 analysis/config-audit-2 收敛：剧情地图（Icebreaker）的
    // 集合触发器按本场战局真正拥有的玩家数等待，而从未发过信号的观看者不再
    // 扣住跳过投票 - 两者现在都是发布行为（玩家数为原生时
    // FikaRallyGate.UseFikaPlayerCount = false，AllowSkipWithoutAllViewers = true）。唯一留下的旋钮，是判定
    // 一个沉默观看者何时算作"不在此过场中"的超时（0 = 等待每个观看者，即旧的
    // 死锁）。

    // AP（analysis/nonfika-raid-inertness）：战局类型闸门的总开关。开启（默认）时，每个
    // 询问 FikaRaidGate.Skip 的战局内钩子都会避开非 Fika 战局（不经过 Fika、直接从
    // 地图选择界面开始的战局）。关闭：修复前的行为，即本移植在
    // 原生单人战局里也同样生效 - 一行即可回退本轮改动的所有内容。
    public ConfigEntry<bool> InertOutsideFikaRaids { get; set; }

    // (analysis/config-audit-2, 2026-10-13) 过场家族收敛成一条路径和一个开关。用户
    // 要的是一个功能 - 主机播过场、客户端也播、剧情
    // 触发 - 所以让这件事成立的各项修复（本地场景接管、桥接播放修复、
    // 跳过提示撤下、派发/席位修复、客户端的原地触发、实时 tick
    // 源）现在都是组件的发布行为，不再各带一个开关。只留下
    // 'Cutscene: Native Tick Hook'：它是决定由哪条路径驱动这两个
    // 控制器的那个开关。CutsceneBoundary / FikaLocalCutsceneTrigger / FikaRallyGate 中的静态字段保留目标
    // 默认值，所以回滚只需重新绑定一个键。
    // analysis/cutscene-native-registration：本移植从未有过的注册 - 两个过场控制器
    // 在原版战局开始协程把它们交给它的地方拿到自己的逐帧调用者，而不是由
    // 桥接来 tick。一个键就能切回仅桥接的状态。

    // Coop | Boss & Spawns - 主机战局准备的三项开关
    // （Main.GameMode.HostGameController）。三项都是主机侧、从静态字段读取，所以
    // 它们在下面的 SetupConfig 中应用并保持热生效。逐开关的分析见报告
    // analysis/config-grouping-i18n/REPORT.md。

    // （已退役）'Boss: One Boss Per Trigger Point' 按用户要求移除：折叠让每个触发点
    // 只保留一个 boss 波次，但在实际联机战局中 boss 因此完全不再生成，所以
    // 重新使用原版机制（HostGameController.CollapseBossWavesPerTrigger 为 false，
    // 旧键由 RetireRemovedEntries 从文件中删除）。

    /// <summary>
    /// 开启（默认）：镜像原版离线准备（OfflineOldSpawn/OfflineNewSpawn 以及
    /// WAVE_COEF_* 缩放），使跳过/放行的判定与无头主机和原版游戏一致。
    /// 关闭：使用未改动的发布标志。诊断用；除非需要判定某个生成
    /// 决策，否则保持开启。
    /// </summary>
    public ConfigEntry<bool> MirrorOfflineSpawnFlags { get; set; }

    /// <summary>
    /// 关闭（默认）：由地图自己的 BotMax 决定，因此发布值为 0 的地图（Icebreaker）不生成
    /// 任何环境 scavenger - 这是用户 2026-10-13 的规则。开启：配置了环境窗口
    /// 却发布上限 0 的地图会得到一个兜底预算（SPT 自己的 18，仍受战局 bot 预算约束）。
    /// </summary>
    public ConfigEntry<bool> EnableAmbientBotCapFallback { get; set; }

    /// <summary>
    /// 关闭（默认）：帧开销探针自身保持静默。开启：每 5 秒一行 [FrameCost]，包含
    /// 帧时间分布、卡顿次数，以及窗口中有多少被 Fika 自己的步骤占用
    /// （Main.Utils.FrameCostProbe.On 把本开关与 'Diagnostics' 做 OR，所以总开关也会运行
    /// 探针 - 这个条目是"只要这一行"的开关）。诊断用；关闭时零成本。
    /// </summary>

    /// <summary>
    /// (analysis/stutter-pose-reset) 'Coop / Pose Write Probe'：按写入点和按帧统计本移植的
    /// 姿态/位移/动画器写入，并在同一窗口的 [FrameCost] 行下面打印一行 [PoseWrite]。
    /// 默认关闭。它为「动作被重置」这类形态回答「是不是我方写入造成的」，同时会一并打开帧
    /// 开销探针，因为这些计数只有和帧分布、以及同一批帧的卡顿
    /// 次数放在一起才可读。
    /// </summary>

    /// <summary>
    /// (analysis/stutter-pose-reset) 'Coop / Pose Write Guards'：被观察玩家姿态/位移/动画器写入的
    /// 「值未变就不写」守卫，默认开启。关闭会在不重新构建的情况下恢复守卫前的行为，这既是
    /// 回滚，也是战局内 A/B 的另一半。
    /// </summary>
    public ConfigEntry<bool> PoseWriteGuards { get; set; }


    /// <summary>
    /// (analysis/stutter-frame-skip) 'Coop / Stutter Probe'：「被抽帧」形态的掉帧日志 -
    /// 连续长帧的段、两段之间的节奏、其间是否跑过托管回收，
    /// 以及本移植自身主线程文件 IO 的实测开销。默认关闭。它也会一并打开
    /// 帧开销探针，因为一段长帧只有和同一批帧的帧分布放在一起才可读
    /// （Main.Utils.StutterJournal.Enabled OR 'Diagnostics'）。
    /// </summary>

    /// <summary>
    /// (analysis/stutter-frame-skip) 'Coop / Log Archive Async'：把会话日志复制放到归档自己的
    /// 线程上执行，而不是在帧内，默认开启。关闭既是回滚也是
    /// 战局内 A/B：此时复制在帧边界上发生，与本轮之前完全一样。
    /// </summary>

    /// <summary>
    /// (analysis/stutter-frame-skip) 'Coop / Net Poll Budget'：单帧最多投递多少个已到达的
    /// 网络事件的上限。0（默认）保留本移植一直有的无界排空；像
    /// 96 这样的值会把一次突发中剩余的事件按顺序推迟到下一帧，不丢弃任何东西。该开关是
    /// 20:01 探针运行指向的唯一候选的战局内 A/B（在 103 个战局内窗口中有 101 个的最差帧落在
    /// clientUpdate，而一帧的全部网络投递都在它里面完成）。
    /// </summary>
    public ConfigEntry<int> NetPollBudget { get; set; }
    public ConfigEntry<bool> FinalMissionEndingRepair { get; set; }
    public ConfigEntry<bool> SentrySyncCardAccess { get; set; }
    public ConfigEntry<bool> TransitClientPressFallback { get; set; }

    // (analysis/config-audit-2) 'Transit: Press Fallback Limit' 已退役（TransitRecovery
    // .PressFallbackLimit 保持 8，即发布边界），而 'Transit: Client Transfer Stash' 已合并进
    // 'Transit: Per-Player Transfer Stash'：现在一个开关驱动容器修复的两半
    // （TransitRecovery.EnsureTransferStash 和 .ClientEnsureTransferStash）。

    // (analysis/config-audit-2) 客户端的原地触发器（Main.Components.FikaLocalCutsceneTrigger）是
    // 单一过场功能的一部分，所以它的三个开关已退役：组件自己的静态字段就是
    // 发布行为（本地触发 = true、单观看者 = true、保持 = 2 秒）。

    // Coop | Transit - 转移恢复轮次的四个开关（Main.Utils.TransitRecovery）。
    // 四个都发布为 true：它们是修复而不是诊断，只有那条判定日志行例外。

    /// <summary>
    /// 开启（默认）：每个转移判定都会写一行 [TransitDiag]（谁在点内、是否
    /// 所有人都已交互、为什么被拒）。每场战局只有几行。关闭：判定日志族为空操作。
    /// </summary>

    /// <summary>
    /// 开启（默认）：收到主机拒绝的客户端会清除本地的按下锁存，因此玩家
    /// 可以立刻再按一次（修复前提示会一直留着、无法第二次按下，这给人
    /// "卡在交互里"的印象）。关闭：修复前的行为。
    /// </summary>
    public ConfigEntry<bool> TransitRearmAfterRefusal { get; set; }

    /// <summary>
    /// 开启（默认）：被拒绝的客户端会重新声明一次"我在点内"的存在状态，这样主机
    /// 不会把它当作已离开该点。关闭：修复前的行为。
    /// </summary>

    /// <summary>
    /// 开启（默认）：进入目标地图时为每位玩家（含客户端）建立转移暂存箱，
    /// 只在缺失或大小为 0 时创建 - 从不清空。这正是原版 OnPlayerEnter
    /// 所做的事。关闭：修复前的行为（客户端拿不到转移暂存箱）。
    /// </summary>
    /// <summary>
    /// 开启（默认）：战局中的每位玩家 - 含客户端 - 都会在缺失时获得逐玩家的转移
    /// 容器（这正是原版 OnPlayerEnter 所做的事），并且客户端会自己构建一份副本。
    /// 与已退役的 'Transit: Client Transfer Stash' 合并（analysis/config-audit-2）：两半都是
    /// 同一项修复在两台机器上的实现，所以一个开关同时覆盖它们。
    /// </summary>
    public ConfigEntry<bool> TransitPerPlayerTransferStash { get; set; }

    /// <summary>
    /// (analysis/terminal-transit-mechanism) 客户端从未看到转移倒计时，因为转移面板
    /// （<c>EFT.UI.LocationTransitTimerPanel</c>）是不同于撤离面板的另一个 <c>GameUI</c> 字段，且
    /// 不在 HUD 恢复例程之中，所以从来没有东西激活它；它自己的 <c>Display()+Show()</c> 在面板上以
    /// 协程运行，在面板不活动时保持静默。开启（默认）会在每次倒计时时唤醒那个面板一次；
    /// 关闭保留本轮之前的行为，只写判定行。
    /// </summary>
    public ConfigEntry<bool> TransitActivateCountdownPanel { get; set; }
    #endregion

    #region client config values
    public bool UseBTR { get; set; }
    public bool FriendlyFire { get; set; }
    public bool DynamicVExfils { get; set; }
    public bool AllowFreeCam { get; set; }
    public bool AllowSpectateFreeCam { get; set; }
    public bool AllowItemSending { get; set; }
    public string[] BlacklistedItems { get; set; }
    public bool ForceSaveOnDeath { get; set; }
    public bool UseInertia { get; set; }
    public bool SharedQuestProgression { get; set; }
    public bool CanEditRaidSettings { get; set; }
    public bool EnableTransits { get; set; }
    public bool AnyoneCanStartRaid { get; set; }
    public bool AllowNamePlates { get; set; }
    public bool RandomLabyrinthSpawns { get; set; }
    public bool PMCFoundInRaid { get; set; }
    public bool AllowSpectateBots { get; set; }
    public bool InstantLoad { get; set; }
    public bool FastLoad { get; set; }
    public ClientReviveConfig ReviveConfig { get; set; }
    #endregion

    private ConfigEntry<T> SetupSetting<T>(string section, string key, T defValue, ConfigDescription configDescription, string fallback, ref bool failed, List<string> error)
    {
        try
        {
            return _config.Bind(section, key, defValue, configDescription);
        }
        catch (Exception ex)
        {
            FikaGlobals.LogError($"Could not set up section {fallback}! Exception:\n{ex.Message}");
            failed = true;
            error.Add(fallback);

            return _config.Bind(section, fallback, defValue, configDescription);
        }
    }

    #region display categories

    // （2026-10-13，配置分组）仅显示层。每个条目的 Section 和 Key 都刻意保持不动：
    // 现有的 com.fika.core.cfg 继续可用，也不迁移任何用户值。改变的只有
    // ConfigurationManagerAttributes.Category/DispName/Order 和提示文字，因此 F12 菜单
    // 重新分组而行为零变化。'Debug | Diagnostics' 是所有唯一目的为判定战局的条目
    // （探针、取证、提交监视、倒计时二分、主菜单二分）所在的组；
    // 它按名称排在最后，并在组内携带最大的 Order 值，即它位于
    // 列表底部，正是指挥员期望的位置。游戏内呈现为英文 + 中文。
    private const string DisplayCoop = "Coop | 联机总览";
    private const string DisplayCoopCore = "Coop | Core";
    private const string DisplayCoopTransit = "Coop | Transit";
    private const string DisplayCoopLoadSync = "Coop | Load Sync";
    private const string DisplayCoopCutscene = "Coop | Cutscene";
    private const string DisplayCoopRally = "Coop | Rally";
    private const string DisplayCoopExfil = "Coop | Exfil & Flare";
    private const string DisplayCoopNamePlates = "Coop | Name Plates";
    private const string DisplayCoopPinging = "Coop | Pinging";
    private const string DisplayCoopQuestSharing = "Coop | Quest Sharing";
    private const string DisplayCoopRevival = "Coop | Revival";
    private const string DisplayCoopDebug = "Coop | Debug";
    private const string DisplayCoopBossSpawns = "Coop | Boss & Spawns";
    private const string DisplayDebugDiagnostics = "Debug | Diagnostics";
    private const string DisplayGameplay = "Gameplay";
    private const string DisplayNetwork = "Network";
    private const string DisplayAdvanced = "Advanced";

    #endregion

    public void SetupConfig()
    {
        // (analysis/crash-dump-20261013) begin/end 这一对是区分"死在 SetupConfig 里面"
        // 与"死在它之后"的唯一办法：2026-10-13 的几次运行既没有记录 "Locales and configuration are ready"
        // （FikaPlugin），也没有记录 "Fika initialization failed"（它的 catch），所以进程是死在
        // 这个方法*内部*、两条语句之间的。两端各一行、总是写入（'Config' 在静默模式下仍然生效），
        // 无逐帧开销。
        // 退役的键最先处理，因此下面的每一行 - 以及 'begin' 步骤的条目计数 -
        // 描述的正是这个版本真正拥有的文件。
        // (2026-10-20) Fika 初始化失败，抛出的 NullReferenceException 来自这个方法内部
        // （"Fika initialization failed; cooperative play is unavailable"）。任何绑定之前运行的两个步骤
        // 现在都是尽力而为：两个都不允许把联机一起拖垮，而且每个在抛异常时都会
        // 报出自己的名字，这样日志指向的是那个步骤而不是这个方法。
        try { RetireRemovedEntries(); }
        catch (Exception exception) { FikaGlobals.LogError($"RetireRemovedEntries failed: {exception}"); }
        
        try { ColorConfigConverter.Register(); }
        catch (Exception exception) { FikaGlobals.LogError($"ColorConfigConverter.Register failed: {exception}"); }
        var failed = false;
        List<string> headers = [];
        
        var disabledMessage = LocaleUtils.UI_DISABLED_BY_HOST.Localized();
        

        // 隐藏（Hidden）

        LastVersion = _config.Bind("Hidden", "Last Version", "0",
            new ConfigDescription("Last loaded version of Fika", tags: new ConfigurationManagerAttributes { Browsable = false }));

#if GOLDMASTER
        if (LastVersion.Value != FikaVersion)
        {
            Singleton<PreloaderUI>.Instance.ShowFikaMessage("FIKA", LocaleUtils.UI_TOS_LONG.Localized(), ErrorScreen.EButtonType.QuitButton, 15f,
                null, () =>
                {
                    LastVersion.Value = FikaVersion;
                });
        }
#endif

        // 高级（Advanced）

        const string advancedDefaultHeader = "Advanced";

        OfficialVersion = SetupSetting(advancedDefaultHeader, "显示正式版本", false,
                new ConfigDescription(LocaleUtils.BEPINEX_OFFICIAL_VERSION_D.Localized(), tags: new ConfigurationManagerAttributes
                {
                    IsAdvanced = true,
                    Category = DisplayAdvanced,
                    DispName = LocaleUtils.BEPINEX_OFFICIAL_VERSION_T.Localized(),
                    Order = 6
                }),
                "Official Version", ref failed, headers);

        DevMode = SetupSetting(advancedDefaultHeader, "开发者模式", false,
            new ConfigDescription(
                "开：启用开发者功能。关（默认）：保持关闭。仅影响本机界面与调试入口。", tags: new ConfigurationManagerAttributes
            {
                IsAdvanced = true,
                Category = DisplayAdvanced,
                DispName = "Developer Mode | 开发者模式",
                Order = 50
            }), "开发者模式", ref failed, headers);

        NoAI = SetupSetting(advancedDefaultHeader, "No AI", false,
            new ConfigDescription(
                "开：不生成任何 AI（Scav/Boss 等）。关（默认）：正常生成。用于加快加载速度与排查 AI 相关问题。", tags: new ConfigurationManagerAttributes
            {
                IsAdvanced = true,
                Category = DisplayAdvanced,
                DispName = "No AI | 不刷 AI",
                Order = 40
            }), "No AI", ref failed, headers);

        NoLoot = SetupSetting(advancedDefaultHeader, "不生成战利品", false,
            new ConfigDescription(
                "开：不生成地面物资。关（默认）：正常生成。用于加快加载速度、排查物资相关问题时使用。", tags: new ConfigurationManagerAttributes
            {
                IsAdvanced = true,
                Category = DisplayAdvanced,
                DispName = "No Loot | 不刷物资",
                Order = 30
            }), "不生成战利品", ref failed, headers);

        LoadPriority = SetupSetting(advancedDefaultHeader, "玩家加载优先级", ELoadPriority.Low,
            new ConfigDescription(
                "加载其他玩家（作客机时含 AI）时使用的线程优先级。默认 Low。效果可能不明显，卡加载时可试 Medium/High。", tags: new ConfigurationManagerAttributes
            {
                IsAdvanced = true,
                Category = DisplayAdvanced,
                DispName = "Player Load Priority | 玩家加载优先级",
                Order = 20
            }), "玩家加载优先级", ref failed, headers);

        MaxBundleLock = SetupSetting(advancedDefaultHeader, "最大打包锁定", 5,
            new ConfigDescription(
                "同时并行加载的资源包数量上限（1-10，默认 5）。作客机加载 bot 很慢时可调高；游戏默认值是 1，这里调高是为了缓解原版资源包加载过慢的问题。",
            new AcceptableValueRange<int>(1, 10), new ConfigurationManagerAttributes
            {
                IsAdvanced = true,
                Category = DisplayAdvanced,
                DispName = "Max Bundle Lock | 并行加载包数量",
                Order = 10
            }), "最大打包锁定", ref failed, headers);

        // 联机（Coop）

        const string coopDefaultHeader = "Coop";

        UseHeadlessIfAvailable = SetupSetting(coopDefaultHeader, "自动使用无头主机", false,
            new ConfigDescription(LocaleUtils.BEPINEX_USE_HEADLESS_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_USE_HEADLESS_T.Localized(),
                Order = 9
            }), "自动使用无头主机", ref failed, headers);

        ShowNotifications = SetupSetting(coopDefaultHeader, "显示击杀信息", true,
            new ConfigDescription(LocaleUtils.BEPINEX_SHOW_FEED_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_SHOW_FEED_T.Localized(),
                Order = 8
            }),
            "显示击杀信息", ref failed, headers);

        AutoExtract = SetupSetting(coopDefaultHeader, "自动撤离", false,
            new ConfigDescription(LocaleUtils.BEPINEX_AUTO_EXTRACT_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_AUTO_EXTRACT_T.Localized(),
                Order = 7
            }),
            "自动撤离", ref failed, headers);

        ShowExtractMessage = SetupSetting(coopDefaultHeader, "显示撤离提示", true,
            new ConfigDescription(LocaleUtils.BEPINEX_SHOW_EXTRACT_MESSAGE_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_SHOW_EXTRACT_MESSAGE_T.Localized(),
                Order = 6
            }),
            "显示撤离提示", ref failed, headers);

        ExtractKey = SetupSetting(coopDefaultHeader, "撤离按键", new KeyboardShortcut(KeyCode.F8),
            new ConfigDescription(LocaleUtils.BEPINEX_EXTRACT_KEY_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_EXTRACT_KEY_T.Localized(),
                Order = 5
            }),
            "撤离按键", ref failed, headers);

        ShowPlayerList = SetupSetting(coopDefaultHeader, "显示局内玩家列表", true,
            new ConfigDescription(LocaleUtils.BEPINEX_SHOW_PLAYER_LIST_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_SHOW_PLAYER_LIST_T.Localized(),
                Order = 4
            }),
            "显示局内玩家列表", ref failed, headers);

        EnableChat = SetupSetting(coopDefaultHeader, "启用聊天", false,
            new ConfigDescription(LocaleUtils.BEPINEX_ENABLE_CHAT_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_ENABLE_CHAT_T.Localized(),
                Order = 3
            }),
            "启用聊天", ref failed, headers);

        ChatKey = SetupSetting(coopDefaultHeader, "聊天按键", new KeyboardShortcut(KeyCode.RightControl),
            new ConfigDescription(LocaleUtils.BEPINEX_CHAT_KEY_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_CHAT_KEY_T.Localized(),
                Order = 2
            }),
            "聊天按键", ref failed, headers);

        // 发布默认为开启（2026-10-10）。该设置过去会触发的主菜单访问违例已经
        // 修复：故障是引擎在处理 Fika 通过 StartCoroutine(IEnumerator) 交给它的
        // 协程包装器时发生的（首轮异常探针在引擎自己的帧里抓到该包装器
        // 在解引用伪对象 0xA），而面板现在不再使用引擎驱动的协程 -
        // 画布轮询、十秒刷新和看门狗标记都由注入行为的 Update 驱动。已在面板启用的情况下
        // 验证：主菜单、一场完整战局（LocalGameMatching 无故障完成）以及回到
        // 重建后的主菜单，全程无崩溃、无新的
        // 崩溃转储。
        FlareExtractSquadSharesActivation = SetupSetting(coopDefaultHeader, "信号弹撤离：小队共享激活", true,
            new ConfigDescription(
                "开（默认，原版队伍通道）：一发绿烟开启射手所属小队的撒离点，队友不用自己开枪。关：只有开枪者本人能用，每人必须自己打一发。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopExfil,
                    DispName = "Flare Extract: Squad Shares Activation | 绿烟对友共享",
                    Order = 100
                }),
            "信号弹撤离：小队共享激活", ref failed, headers);

        FlareReplicateExitVisual = SetupSetting(coopDefaultHeader, "信号弹撤离：复制撤离视觉", true,
            new ConfigDescription(
                "开（默认）：开枪者那台机器跑过的本地链（直升机落地、旋翼气流、撒离点活过来）在全队各机同步重放，队友能看到同一动画并离场。关：只有开枪机器播放动画（修复前行为）；它不会为未被激活的玩家打开撒离点。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopExfil,
                    DispName = "Flare Extract: Replicate Exit Visual | 同步撒离点动画",
                    Order = 95
                }),
            "信号弹撤离：复制撤离视觉", ref failed, headers);

        FlareExfilFeedbackSeconds = SetupSetting(coopDefaultHeader, "信号弹撤离：反馈秒数", 5f,
            new ConfigDescription(
                "绿烟把本玩家加入狙击手免疫名单后，“道路已开放”横幅在屏幕上停留的秒数（默认 5）。≤ 0 表示一直留到战局结束。每次弹出时读取，不需重启。",
                new AcceptableValueRange<float>(0f, 60f), new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopExfil,
                    DispName = "Flare Extract: Feedback Seconds | 成功提示显示秒数",
                    Order = 90
                }),
            "Flare Extract: Feedback Seconds", ref failed, headers);

        HonorMapFixedTime = SetupSetting(coopDefaultHeader, "遵从地图固定时间", true,
            new ConfigDescription(
                "开（默认）：主机开局时用地图自带的固定时间（如破冰船），忽略玩家自己设的小时；日志行 'Raid time: ... fixed=HH:mm' 会记下来源。关：回到玩家自己的设置（旧行为）。重连与转移都保持当前时钟。主机开局时读取。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopCore,
                    DispName = "Honor Map Fixed Time | 遵从地图固定时间",
                    Order = 20
                }),
            "Honor Map Fixed Time", ref failed, headers);

        TransitRequireAllTeammates = SetupSetting(coopDefaultHeader, "转移：需要所有队友就绪", true,
            new ConfigDescription(
                "Strict transit start gate (default on). The countdown only starts once every teammate of this raid " +
                "has interacted with the transit point and is standing inside it. Off: it starts as soon as the host " +
                "interacted and every alive human player is inside the point.",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopTransit,
                    DispName = "Transit: Require All Teammates Ready",
                    Order = 100
                }),
            "Transit: Require All Teammates Ready", ref failed, headers);

        TransitCountdownFallbackSeconds = SetupSetting(coopDefaultHeader, "转移：倒计时兜底", 30,
            new ConfigDescription(
                "Countdown length in seconds used when the transit point itself does not define one (TransitParameters.time == 0).",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopTransit,
                    DispName = "Transit: Countdown Fallback",
                    Order = 80
                }),
            "Transit: Countdown Fallback", ref failed, headers);

        // 闸门读取静态字段，这样主机和无头控制器不必持有配置对象就能看到该值；
        // 在这里以及每次变更时保持同步（热切换，无需重启）。
        Fika.Core.Main.HostClasses.TransitStartGate.RequireAllTeammatesInteracted = TransitRequireAllTeammates.Value;
        Fika.Core.Main.HostClasses.TransitStartGate.CountdownFallbackSeconds = TransitCountdownFallbackSeconds.Value;
        TransitRequireAllTeammates.SettingChanged += (_, _) =>
            Fika.Core.Main.HostClasses.TransitStartGate.RequireAllTeammatesInteracted = TransitRequireAllTeammates.Value;
        TransitCountdownFallbackSeconds.SettingChanged += (_, _) =>
            Fika.Core.Main.HostClasses.TransitStartGate.CountdownFallbackSeconds = TransitCountdownFallbackSeconds.Value;

        LoadSyncHostPeerTimeout = SetupSetting(coopDefaultHeader, "加载同步：主机等待同伴上限（秒）", 300,
            new ConfigDescription(
                "主机等已连接但始终未声明角色的同伴的上限（秒，默认 300）；超时后仍会放行赛前闸门并丢弃该同伴。连接已断的同伴不消耗这个预算。只影响主机。",
                new AcceptableValueRange<int>(30, 1800),
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopLoadSync,
                    DispName = "Load Sync: Host Peer Timeout | 主机等同伴上限（秒）",
                    Order = 100
                }),
            "Load Sync: Host Peer Timeout", ref failed, headers);

        LoadSyncClientHostWaitCeiling = SetupSetting(coopDefaultHeader, "加载同步：客机等待主机上限（秒）", 600,
            new ConfigDescription(
                "客机入局后等待主机握手的上限（秒，默认 600）。连接已断会立即失败；这个上限只用于“连接还在但主机永远不推进”。只影响客机。",
                new AcceptableValueRange<int>(60, 3600),
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopLoadSync,
                    DispName = "Load Sync: Client Host-Wait Ceiling | 客机等主机上限（秒）",
                    Order = 95
                }),
            "Load Sync: Client Host-Wait Ceiling", ref failed, headers);

        LoadSyncClientTransitCeiling = SetupSetting(coopDefaultHeader, "加载同步：客机转移重载上限（秒）", 900,
            new ConfigDescription(
                "同上，但用于转移重载：双方同时加载新地图，较慢的一方曾被旧的固定 40 秒上限切断（秒，默认 900）。只影响客机。",
                new AcceptableValueRange<int>(60, 3600),
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopLoadSync,
                    DispName = "Load Sync: Client Transit Reload Ceiling | 客机转移重载上限（秒）",
                    Order = 90
                }),
            "Load Sync: Client Transit Reload Ceiling", ref failed, headers);

        LoadSyncSilentWarnSeconds = SetupSetting(coopDefaultHeader, "加载同步：无包警告间隔（秒）", 30,
            new ConfigDescription(
                "多久没收到对方数据包就把 [LoadSync] 行升为 warning（秒，默认 30）。仅影响日志可读性，不会自己结束等待。",
                new AcceptableValueRange<int>(5, 300),
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopLoadSync,
                    DispName = "Load Sync: Silent Warning Seconds | 无包警告间隔（秒）",
                    Order = 85
                }),
            "Load Sync: Silent Warning Seconds", ref failed, headers);

        // 与转移闸门相同的模式：等待方读取静态字段，所以在这里以及实时变更时保持同步。
        LoadSyncGate.HostPeerLoadTimeoutSeconds = LoadSyncHostPeerTimeout.Value;
        LoadSyncGate.ClientHostWaitCeilingSeconds = LoadSyncClientHostWaitCeiling.Value;
        LoadSyncGate.ClientTransitReloadCeilingSeconds = LoadSyncClientTransitCeiling.Value;
        LoadSyncGate.ClientSilentWarnSeconds = LoadSyncSilentWarnSeconds.Value;
        LoadSyncHostPeerTimeout.SettingChanged += (_, _) =>
            LoadSyncGate.HostPeerLoadTimeoutSeconds = LoadSyncHostPeerTimeout.Value;
        LoadSyncClientHostWaitCeiling.SettingChanged += (_, _) =>
            LoadSyncGate.ClientHostWaitCeilingSeconds = LoadSyncClientHostWaitCeiling.Value;
        LoadSyncClientTransitCeiling.SettingChanged += (_, _) =>
            LoadSyncGate.ClientTransitReloadCeilingSeconds = LoadSyncClientTransitCeiling.Value;
        LoadSyncSilentWarnSeconds.SettingChanged += (_, _) =>
            LoadSyncGate.ClientSilentWarnSeconds = LoadSyncSilentWarnSeconds.Value;

        TransitReloadHoldSeconds = SetupSetting(coopDefaultHeader, "转移：客机重载等待上限（秒）", 240,
            new ConfigDescription(
                "客机已结束本局后，等待主机重载信号时持住地图加载的上限（秒，默认 240）。正常情况信号一个往返就到，该上限只保护“主机永远不发信号”（旧版本、连接已断、无通知的 headless 主机）。只影响客机。",
                new AcceptableValueRange<int>(5, 600),
                tags: new ConfigurationManagerAttributes
                {
                    // (analysis/config-audit-2) 一个加载同步等待，而非转移语义：归入其家族中那四个
                    // 等待旁边，这样 'Coop | Transit' 只承载转移规则，别无其他。
                    Category = DisplayCoopLoadSync,
                    DispName = "Transit: Client Reload Hold | 客机重载等待上限（秒）",
                    Order = 80
                }),
            "Transit: Client Reload Hold", ref failed, headers);

        TransitReloadGate.ClientHoldSeconds = TransitReloadHoldSeconds.Value;
        TransitReloadHoldSeconds.SettingChanged += (_, _) =>
            TransitReloadGate.ClientHoldSeconds = TransitReloadHoldSeconds.Value;

        // Coop | 网络超时判定行（analysis/transit-timeout-crash）。开启（默认）：本次会话做出的每个
        // 超时 / 等待超时决定，也会写一行可 grep 的 "[NetTimeout] ..."，携带超时了什么、
        // 等了多久、还剩多少机器提交内存，以及那一刻本次会话自身的队列
        // 状态有多少。于是一次 grep 一次运行，就能为每次运行
        // 回答"死亡发生时是否伴随网络超时"。关闭：调用点的行为与该行存在之前完全
        // 一致（一次切换＝旧行为，无需重新构建）。

        // (analysis/config-binding-order-regression, 2026-10-13) 'Diagnostics'、'Diagnostics
        // Verbose' 和 'Crash Forensics' 的接线*曾经就在这里* - 即在三个条目于本方法更下方
        // 绑定之前（'Diagnostics Verbose' 约 872 行、'Diagnostics' 约 889 行、'Crash Forensics' 约 919 行）。
        // 读取仍为 null 的 ConfigEntry 属性会抛 NullReferenceException，调用方的 catch 把它变成
        // "Fika initialization failed; cooperative play is unavailable"，而且 - 这才是关键 -
        // 这一行之后的每一个绑定都没有执行。现在接线位于三个绑定中的最后一个之后、
        // 就在 'Crash Forensics' 条目下方。开关的其他方面没有任何变化。

        EnableOnlinePlayers = SetupSetting(coopDefaultHeader, "启用在线玩家", true,
            new ConfigDescription(LocaleUtils.BEPINEX_ENABLE_ONLINE_PLAYER_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_ENABLE_ONLINE_PLAYER_T.Localized(),
                Order = 100
            }),
            "Enable Online Players", ref failed, headers);

        OnlinePlayersScale = SetupSetting(coopDefaultHeader, "在线玩家缩放", 1f,
            new ConfigDescription(LocaleUtils.BEPINEX_ONLINE_PLAYERS_SCALE_D.Localized(),
            new AcceptableValueRange<float>(0.5f, 1.5f), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_ONLINE_PLAYERS_SCALE_T.Localized(),
                Order = 95
            }),
            "Online Players Scale", ref failed, headers);

        // Coop | 在线玩家面板的周期（analysis/online-players-refresh）。该面板过去按一个固定的
        // 十秒周期向服务器请求名册，所以加入或离开的玩家只能等到下一个周期才出现 -
        // 而整个测试日的服务器日志显示轮询只出现在 10 秒的突发里
        // （logs-r13test-20261012-200405、spt-server-20261012.log）。三秒让一次加入或离开在几秒内可见，
        // 而未变化的周期只花一次 HTTP GET（实测约 12 毫秒），因为只有名册真的变化时才重建
        // 各行。把这个值改回 10 可恢复旧的节奏；下面的
        // 开关同时恢复旧的代码路径。
        OnlinePlayersFastRefresh = SetupSetting(coopDefaultHeader, "在线玩家快速刷新", true,
            new ConfigDescription(
                "开（默认）：面板可见时按下方的短周期拉取名单，面板重开时立即刷新，名单没变则不重建行。关：回到早前行为——固定 10 秒一轮，每轮重建全部行。仅影响本机。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = "Online Players Fast Refresh | 在线玩家快速刷新",
                Order = 90
            }),
            "Online Players Fast Refresh", ref failed, headers);

        OnlinePlayersRefreshSeconds = SetupSetting(coopDefaultHeader, "可见刷新间隔（秒）", 3,
            new ConfigDescription(
                "面板可见时两次刷新的间隔（秒），默认 3。改回 10 就恢复原来的节奏；只影响本机面板。", new AcceptableValueRange<int>(1, 60), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = "Online Players Refresh Seconds | 可见刷新间隔（秒）",
                Order = 85
            }),
            "Online Players Refresh Seconds", ref failed, headers);

        OnlinePlayersIdleRefreshSeconds = SetupSetting(coopDefaultHeader, "不可见刷新间隔（秒）", 10,
            new ConfigDescription(
                "面板不可见时的刷新间隔（秒），默认 10，即面板一直在用的旧周期。战局或加载界面期间引擎不会调用面板，此时不发任何请求。", new AcceptableValueRange<int>(1, 300), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = "Online Players Idle Refresh Seconds | 不可见刷新间隔（秒）",
                Order = 80
            }),
            "Online Players Idle Refresh Seconds", ref failed, headers);

        // Coop | 主菜单装饰。面板本身是 'Enable Online Players'；下面两个
        // 开关分别隔离该面板在 prefab 实例化之后发生的两处写入，
        // 以及注册到实时游戏 UI 的任务栏按钮。
        WriteOnlinePlayerCounter = SetupSetting(coopDefaultHeader, "写入在线玩家计数", true,
            new ConfigDescription(
                "开（默认）：把在线玩家数写进主菜单面板的文本。关：面板照常运行，但不碰动 prefab 里的 TextMeshProUGUI——主菜单崩溃时用来排除这一步。仅影响本机显示。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = "Write Online Player Counter | 写入在线玩家计数",
                Order = 40
            }),
            "Write Online Player Counter", ref failed, headers);

        DownloadProfileButton = SetupSetting(coopDefaultHeader, "下载档案按钮", true,
            new ConfigDescription(
                "开（默认）：在主菜单任务栏创建“下载档案”按钮，并向游戏 MenuTaskBar 注册其切换与提示。关：主菜单不再碰任务栏。仅影响本机。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = "Download Profile Button | 下载档案按钮",
                Order = 35
            }),
            "下载档案按钮", ref failed, headers);

        // 自 3.0.3 起默认关闭：该跟踪对每个原生引用做一次 VirtualQuery，并与
        // 菜单面板的 10 秒刷新对齐，导致菜单打开期间每 10 秒出现约 2 秒的卡死。
        // 只在追查主菜单崩溃时才打开。


        // Coop | 性能（analysis/perf-diagnostics-cleanup）。诊断探针的总开关。
        // 默认关闭：用户的反馈是"掉帧严重 + 规律性卡顿"，而 2026-10-13 00:40 那场战局的
        // 测量把探针指为负载 - 每秒一次全场景扫描（CutsceneBoundary.Diagnose）、
        // 逐帧世界步骤上的十一个计数补丁（CutsceneTickPathProbe）、两个过场控制器上
        // 每秒约 9,400 次计数调用、每场战局约 89,000 次无缓冲的崩溃轨迹写入，以及
        // 拾取看门狗、内存表和夜视采样器各一行的每秒日志。
        // 开启＝这些日志族的全部判定行都回来（与清理前同样形态），因此
        // 失败的战局仍可一次切换就判定，而不必重新构建。

        // Coop | 崩溃取证（analysis/crash-dump-20261013）。2026-10-13 08:58:52 与 08:59:52
        // 两次死亡的全内存转储显示 coreclr 在 coreclr+0x2C7085 处 fail fast - "Invalid Program:
        // attempted to call a UnmanagedCallersOnly method from managed code" - 而主线程上的调用链
        // 是 EEPolicy::HandleFatalError -> LogFatalError -> RaiseFailFastException。最后一次调用
        // 绕过所有向量化异常处理器，因此 FirstChanceFaultProbe 对它永不运行，
        // 轨迹的 [Fatal] 块也永远不会被写入；两次运行只留下一个 1 KB 的 Fika-crash.log，
        // 里面除了表头什么都没有。能在这种死亡中幸存的唯一仪器，是轨迹的
        // 预写部分，而这一半原本只由 'Diagnostics' 关闭。本开关单独控制这一半，
        // 因此复现可以在不为逐帧探针付费的情况下判定。
        // （2026-10-13，主代理：为一次 0x80131506 fail-fast 的复现运行，默认值曾被临时
        // 提高到 true。那次运行没有以死亡结束 - 崩溃是当时那个版本已修复的配置绑定回归 -
        // 所以这里发布的默认值又改回 false。）

        // (analysis/config-binding-order-regression, 2026-10-13) 三个诊断开关的接线，从原先紧跟在
        // 'Net Timeout Diagnostic' 条目之后的位置移到这里。它必须留在全部三个绑定
        // （'Diagnostics Verbose'、'Diagnostics'、'Crash Forensics'）之下，因为它解引用全部三个
        // 属性：当它位于它们之上时抛出 NullReferenceException，调用方的 catch 报告
        // "Fika initialization failed"，而这个方法从那一行到结尾的每个绑定都被跳过 -
        // 这就是磁盘上的配置文件在 'Net Timeout Diagnostic' 之后不再增加条目的原因。
        //
        // (analysis/perf-diagnostics-cleanup) [NetTimeout] 行是一个探针，因此也在
        // 总开关之后：它自己的条目保留为子开关，所以 'Net Timeout Diagnostic' 不再能
        // 在 'Diagnostics' 关闭时打开该行。两个开关都开启，该行才会出现。

        // Coop | 所有诊断探针的总开关（analysis/perf-diagnostics-cleanup）。它和下面
        // 逐调用的 'Diagnostics Verbose' 都在这里应用，因此代码读取的两个开关都

        // Coop | 内存守护（analysis/commit-watch）。'Commit Watch' 只做观察：[MemWatch]
        // WARN/CRITICAL 行及其阈值，每 5 秒最多一行。下面的缓解是一个单独的
        // 开关，因为只有它改变行为 - 它在临界阈值（1024 MiB）以下武装，
        // 放弃 Fika 自己的低优先级工作，绝不放弃任何承载背包或
        // 同步状态的东西。把三个都关掉，即本轮改动的一键回退。




        // (analysis/config-audit-2) 'Commit Watch Hard Release' 作为子开关已退役：它只修剪
        // 上面的缓解（FikaRaidMemory.HardReleaseEnabled = true 即发布行为）。

        // (analysis/config-audit-2) 这里退役：'Show Final Countdown Screen' 和 'Raid Start Menu Music'
        // （两者都只在下面的总开关关闭时才可达）以及 'Raid Start Audio Fades' - 最后一个
        // 已经不再门控任何东西：自 2026-10-10 的修复起它的语句无条件运行
        // （BaseGameController.cs:481-494，'it runs unconditionally now; neither bisect switch gates it'）。
        // Coop | 倒计时二分第 2 轮（analysis/countdown-bisect-round2）。总开关发布为开启，所以
        // 默认配置是即便保留的某条语句就是中止者、战局也能启动；八个语句开关发布为开启
        // （它们就是上游运行的配置），只在总开关关闭时才有意义。重新启用的顺序见
        // analysis/countdown-bisect-round2/REPORT.md 第 5 节。

        // Coop | 倒计时二分第 3 轮（analysis/countdown-bisect-round3）：等待之后的代码块有了自己的
        // 总开关。它发布为开启，即发布默认先执行部署等待，然后直接返回
        // 会话序列（步骤 2/3 完成 -> 步骤 3/3，即原版会话协程），
        // 语句 4/8-8/8 一条都不执行。把它关闭才会再次运行那些语句（每一条仍然有
        // 自己的开关，见上面）。它开启时的开销见 analysis/countdown-bisect-round3/REPORT.md。

        // Coop | 战局内 HUD 恢复（analysis/inraid-ui）。上面两个总开关（'Skip Countdown
        // Body'、'Skip Post Wait Block'）发布为开启，因此语句 1/8（GameUi.gameObject.SetActive(true)）和
        // 2/8（TimerPanel.ProfileId）- 也就是打开战局 HUD 层级的这两条语句 - 在默认战局中
        // 永不执行。整个 HUD 层级（GameUI 根节点、EFT.UI.ExtractionTimersPanel、
        // EFT.UI.BattleUIScreen）于是整场战局都保持不活动：计时键什么都不做、
        // 撤离列表和撤离倒计时永不出现，而游戏在这些面板上启动的每个协程
        // 都被 Unity 拒绝。本条目在部署等待之后、在 Update 泵上恢复 HUD，
        // 每一步都有自己的 try/catch - 它不会重新启用被跳过的语句。
        CountdownRestoreHud = SetupSetting(coopDefaultHeader, "倒计时：恢复战局内 HUD", true,
            new ConfigDescription(
                "开（默认）：部署等待后，把战局 HUD（撤离点列表 + 剩余时间、撤离倒计时）重新激活并绑定到本档案。它只重复函数体里的两条 HUD 语句（1/8、2/8），不会重新打开任何被跳过的语句。关：整个战局 HUD 不可见。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayDebugDiagnostics,
                DispName = "Countdown: Restore In-Raid HUD | 恢复战局内 HUD",
                Order = 26
            }),
            "Countdown: Restore In-Raid HUD", ref failed, headers);

        // Coop | 开局倒计时（analysis/countdown-restore）。一个伞形开关外加每步一个开关，
        // 全部发布为开启：玩家要求把倒计时拿回来，每一步在运行前都会在崩溃轨迹里
        // 报出自己的名字，而伞形开关就是让战局精确回到本条目存在之前行为的
        // 那一行。这里没有任何东西会关掉总开关 - 主体仍然保持被跳过，
        // 这条路径只是原先位于其中的那个倒计时。
        CountdownPlayOpening = SetupSetting(coopDefaultHeader, "倒计时：播放开局倒计时", true,
            new ConfigDescription(
                "开（默认）：在跳过倒计时函数体的同时，用一条自己的流程把玩家要看的倒计时放回来，部署等待结束再收回（倒计时时长正好等于本局部署时间）。关：完全没有倒计时遮罩。遮罩/关闭/视图恢复/再确认四步已并入本条（config-audit-2），不再各自占一个开关。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayDebugDiagnostics,
                DispName = "Countdown: Play Opening Countdown | 播放开局倒计时",
                Order = 25
            }),
            "Countdown: Play Opening Countdown", ref failed, headers);

        // Coop | 姓名牌

        const string coopDefaultNamePlatesHeader = "Coop | Name Plates";

        UseNamePlates = SetupSetting(coopDefaultNamePlatesHeader, "显示玩家名牌", true,
            new ConfigDescription(AllowNamePlates ? LocaleUtils.BEPINEX_USE_NAME_PLATES_D.Localized() : disabledMessage, tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_USE_NAME_PLATES_T.Localized(),
                Order = 16,
                ReadOnly = !AllowNamePlates
            }),
            "显示玩家名牌", ref failed, headers);

        HideHealthBar = SetupSetting(coopDefaultNamePlatesHeader, "隐藏血条", false,
            new ConfigDescription(LocaleUtils.BEPINEX_HIDE_HEALTH_BAR_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_HIDE_HEALTH_BAR_T.Localized(),
                Order = 15
            }),
            "隐藏血条", ref failed, headers);

        UseHealthNumber = SetupSetting(coopDefaultNamePlatesHeader, "显示血量百分比而非血条", false,
            new ConfigDescription(LocaleUtils.BEPINEX_USE_PERCENT_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_USE_PERCENT_T.Localized(),
                Order = 14
            }),
            "显示血量百分比而非血条", ref failed, headers);

        ShowEffects = SetupSetting(coopDefaultNamePlatesHeader, "显示特效", true,
            new ConfigDescription(LocaleUtils.BEPINEX_SHOW_EFFECTS_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_SHOW_EFFECTS_T.Localized(),
                Order = 13
            }),
            "显示特效", ref failed, headers);

        UsePlateFactionSide = SetupSetting(coopDefaultNamePlatesHeader, "显示玩家阵营图标", true,
            new ConfigDescription(LocaleUtils.BEPINEX_SHOW_FACTION_ICON_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_SHOW_FACTION_ICON_T.Localized(),
                Order = 12
            }),
            "显示玩家阵营图标", ref failed, headers);

        HideNamePlateInOptic = SetupSetting(coopDefaultNamePlatesHeader, "开镜时隐藏名牌", true,
            new ConfigDescription(LocaleUtils.BEPINEX_HIDE_IN_OPTIC_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_HIDE_IN_OPTIC_T.Localized(),
                Order = 11
            }),
            "开镜时隐藏名牌", ref failed, headers);

        NamePlateUseOpticZoom = SetupSetting(coopDefaultNamePlatesHeader, "名牌使用开镜缩放", true,
            new ConfigDescription(LocaleUtils.BEPINEX_OPTIC_USE_ZOOM_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_OPTIC_USE_ZOOM_T.Localized(),
                Order = 10,
                IsAdvanced = true
            }),
            "名牌使用开镜缩放", ref failed, headers);

        DecreaseOpacityNotLookingAt = SetupSetting(coopDefaultNamePlatesHeader, "周边视野降低不透明度", true,
            new ConfigDescription(LocaleUtils.BEPINEX_DEC_OPAC_PERI_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_DEC_OPAC_PERI_T.Localized(),
                Order = 9
            }),
            "周边视野降低不透明度", ref failed, headers);

        NamePlateScale = SetupSetting(coopDefaultNamePlatesHeader, "名牌缩放", 1f,
            new ConfigDescription(LocaleUtils.BEPINEX_NAME_PLATE_SCALE_D.Localized(),
            new AcceptableValueRange<float>(0.5f, 1.5f), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_NAME_PLATE_SCALE_T.Localized(),
                Order = 8
            }),
            "名牌缩放", ref failed, headers);

        OpacityInADS = SetupSetting(coopDefaultNamePlatesHeader, "开镜时不透明度", 0.75f,
            new ConfigDescription(LocaleUtils.BEPINEX_ADS_OPAC_D.Localized(),
            new AcceptableValueRange<float>(0.1f, 1f), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_ADS_OPAC_T.Localized(),
                Order = 7
            }),
            "开镜时不透明度", ref failed, headers);

        MaxDistanceToShow = SetupSetting(coopDefaultNamePlatesHeader, "显示的最大距离", 500f,
            new ConfigDescription(LocaleUtils.BEPINEX_MAX_DISTANCE_D.Localized(),
            new AcceptableValueRange<float>(10f, 1000f), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_MAX_DISTANCE_T.Localized(),
                Order = 6
            }),
            "显示的最大距离", ref failed, headers);

        MinimumOpacity = SetupSetting(coopDefaultNamePlatesHeader, "最小不透明度", 0.1f,
            new ConfigDescription(LocaleUtils.BEPINEX_MIN_OPAC_D.Localized(),
            new AcceptableValueRange<float>(0.0f, 1f), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_MIN_OPAC_T.Localized(),
                Order = 5
            }),
            "最小不透明度", ref failed, headers);

        UseOcclusion = SetupSetting(coopDefaultNamePlatesHeader, "使用遮挡剔除", false,
            new ConfigDescription(LocaleUtils.BEPINEX_USE_OCCLUSION_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_USE_OCCLUSION_T.Localized(),
                Order = 4
            }),
            "使用遮挡剔除", ref failed, headers);

        FullHealthColor = SetupSetting(coopDefaultNamePlatesHeader, "满血颜色", Color.green,
            new ConfigDescription(LocaleUtils.BEPINEX_HEALTHCOLOR_FULL_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_HEALTHCOLOR_FULL_T.Localized(),
                Order = 3
            }),
            "满血颜色", ref failed, headers);

        LowHealthColor = SetupSetting(coopDefaultNamePlatesHeader, "低血颜色", Color.red,
            new ConfigDescription(LocaleUtils.BEPINEX_HEALTHCOLOR_LOW_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_HEALTHCOLOR_LOW_T.Localized(),
                Order = 2
            }),
            "低血颜色", ref failed, headers);

        NamePlateTextColor = SetupSetting(coopDefaultNamePlatesHeader, "名牌文字颜色", Color.white,
            new ConfigDescription(LocaleUtils.BEPINEX_NAMEPLATECOLOR_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_NAMEPLATECOLOR_T.Localized(),
                Order = 1
            }),
            "名牌文字颜色", ref failed, headers);

        ShowBrokenLimbs = SetupSetting(coopDefaultNamePlatesHeader, "显示骨折", failed,
            new ConfigDescription(LocaleUtils.BEPINEX_SHOWBROKENLIMBS_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_SHOWBROKENLIMBS_T.Localized(),
                Order = 15
            }),
            "显示骨折", ref failed, headers);

        // Coop | 任务共享

        const string coopDefaultQuestSharingHeader = "Coop | Quest Sharing";
        var questSharingEnabled = SharedQuestProgression;

        QuestTypesToShareAndReceive = SetupSetting(coopDefaultQuestSharingHeader, "任务类型", EQuestSharingTypes.All,
            new ConfigDescription(questSharingEnabled ? LocaleUtils.BEPINEX_QUEST_TYPES_D.Localized() : disabledMessage, tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopQuestSharing,
                DispName = LocaleUtils.BEPINEX_QUEST_TYPES_T.Localized(),
                Order = 4,
                ReadOnly = !questSharingEnabled
            }),
            "任务类型", ref failed, headers);

        QuestSharingNotifications = SetupSetting(coopDefaultQuestSharingHeader, "显示通知", true,
            new ConfigDescription(questSharingEnabled ? LocaleUtils.BEPINEX_QS_NOTIFICATIONS_D.Localized() : disabledMessage, tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopQuestSharing,
                DispName = LocaleUtils.BEPINEX_QS_NOTIFICATIONS_T.Localized(),
                Order = 3,
                ReadOnly = !questSharingEnabled
            }),
            "显示通知", ref failed, headers);

        EasyKillConditions = SetupSetting(coopDefaultQuestSharingHeader, "简化击杀条件", false,
            new ConfigDescription(questSharingEnabled ? LocaleUtils.BEPINEX_EASY_KILL_CONDITIONS_D.Localized() : disabledMessage, tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopQuestSharing,
                DispName = LocaleUtils.BEPINEX_EASY_KILL_CONDITIONS_T.Localized(),
                Order = 2,
                ReadOnly = !questSharingEnabled
            }),
            "简化击杀条件", ref failed, headers);

        SharedKillExperience = SetupSetting(coopDefaultQuestSharingHeader, "共享击杀经验", false,
            new ConfigDescription(questSharingEnabled ? LocaleUtils.BEPINEX_SHARED_KILL_XP_D.Localized() : disabledMessage, tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopQuestSharing,
                DispName = LocaleUtils.BEPINEX_SHARED_KILL_XP_T.Localized(),
                Order = 1,
                ReadOnly = !questSharingEnabled
            }),
            "共享击杀经验", ref failed, headers);

        SharedBossExperience = SetupSetting(coopDefaultQuestSharingHeader, "共享首领经验", false,
            new ConfigDescription(questSharingEnabled ? LocaleUtils.BEPINEX_SHARED_BOSS_XP_D.Localized() : disabledMessage, tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopQuestSharing,
                DispName = LocaleUtils.BEPINEX_SHARED_BOSS_XP_T.Localized(),
                Order = 0,
                ReadOnly = !questSharingEnabled
            }),
            "共享首领经验", ref failed, headers);

        // Coop | 标记（Ping）

        const string coopDefaultPingingHeader = "Coop | Pinging";

        UsePingSystem = SetupSetting(coopDefaultPingingHeader, "标记系统", true,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_SYSTEM_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_SYSTEM_T.Localized(),
                Order = 11
            }),
            "标记系统", ref failed, headers);

        PingButton = SetupSetting(coopDefaultPingingHeader, "标记按键", new KeyboardShortcut(KeyCode.Semicolon),
            new ConfigDescription(LocaleUtils.BEPINEX_PING_BUTTON_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_BUTTON_T.Localized(),
                Order = 10
            }),
            "标记按键", ref failed, headers);

        PingColor = SetupSetting(coopDefaultPingingHeader, "标记颜色", Color.white,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_COLOR_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_COLOR_T.Localized(),
                Order = 9
            }),
            "标记颜色", ref failed, headers);

        PingSize = SetupSetting(coopDefaultPingingHeader, "标记大小", 1f,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_SIZE_D.Localized(),
            new AcceptableValueRange<float>(0.1f, 2f), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_SIZE_T.Localized(),
                Order = 8
            }),
            "标记大小", ref failed, headers);

        PingTime = SetupSetting(coopDefaultPingingHeader, "标记持续时间", 3,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_TIME_D.Localized(),
            new AcceptableValueRange<int>(2, 10), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_TIME_T.Localized(),
                Order = 7
            }),
            "标记持续时间", ref failed, headers);

        PlayPingAnimation = SetupSetting(coopDefaultPingingHeader, "播放标记动画", false,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_ANIMATION_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_ANIMATION_T.Localized(),
                Order = 6
            }),
            "播放标记动画", ref failed, headers);

        ShowPingDuringOptics = SetupSetting(coopDefaultPingingHeader, "开镜时显示标记", false,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_OPTICS_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_OPTICS_T.Localized(),
                Order = 5
            }),
            "开镜时显示标记", ref failed, headers);

        PingUseOpticZoom = SetupSetting(coopDefaultPingingHeader, "标记使用开镜缩放", true,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_OPTIC_ZOOM_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_OPTIC_ZOOM_T.Localized(),
                Order = 4,
                IsAdvanced = true
            }),
            "标记使用开镜缩放", ref failed, headers);

        PingScaleWithDistance = SetupSetting(coopDefaultPingingHeader, "标记随距离缩放", true,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_SCALE_DISTANCE_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_SCALE_DISTANCE_T.Localized(),
                Order = 3,
                IsAdvanced = true
            }),
            "标记随距离缩放", ref failed, headers);

        PingMinimumOpacity = SetupSetting(coopDefaultPingingHeader, "标记最小不透明度", 0.05f,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_MIN_OPAC_D.Localized(),
            new AcceptableValueRange<float>(0f, 0.5f), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_MIN_OPAC_T.Localized(),
                Order = 2,
                IsAdvanced = true
            }),
            "标记最小不透明度", ref failed, headers);

        ShowPingRange = SetupSetting(coopDefaultPingingHeader, "显示标记距离", false,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_RANGE_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_RANGE_T.Localized(),
                Order = 1
            }),
            "显示标记距离", ref failed, headers);

        PingSound = SetupSetting(coopDefaultPingingHeader, "标记音效", EPingSound.SubQuestComplete,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_SOUND_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_SOUND_T.Localized(),
                Order = 0
            }),
            "标记音效", ref failed, headers);

        // Coop | 复活

        const string coopDefaultRevivalHeader = "Coop | Revival";

        GiveUpKey = SetupSetting(coopDefaultRevivalHeader, "放弃按键", new KeyboardShortcut(KeyCode.End),
            new ConfigDescription(CleanConfigString(LocaleUtils.BEPINEX_REVIVE_GIVE_UP_KEY_D.Localized()), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopRevival,
                DispName = LocaleUtils.BEPINEX_REVIVE_GIVE_UP_KEY_T.Localized(),
                Order = 0
            }),
            "放弃按键", ref failed, headers);

        // Coop | 调试

        const string coopDefaultDebugHeader = "Coop | Debug";

        FreeCamButton = SetupSetting(coopDefaultDebugHeader, "自由视角按键", new KeyboardShortcut(KeyCode.F9),
            new ConfigDescription(CleanConfigString(LocaleUtils.BEPINEX_FREE_CAM_BUTTON_D.Localized()), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopDebug,
                DispName = LocaleUtils.BEPINEX_FREE_CAM_BUTTON_T.Localized(),
                Order = 4
            }),
            "自由视角按键", ref failed, headers);

        AZERTYMode = SetupSetting(coopDefaultDebugHeader, "AZERTY 键位", false,
            new ConfigDescription(CleanConfigString(LocaleUtils.BEPINEX_AZERTY_MODE_D.Localized()), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopDebug,
                DispName = LocaleUtils.BEPINEX_AZERTY_MODE_T.Localized(),
                Order = 2
            }),
            "AZERTY 键位", ref failed, headers);

        DroneMode = SetupSetting(coopDefaultDebugHeader, "无人机模式", false,
            new ConfigDescription(LocaleUtils.BEPINEX_DRONE_MODE_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopDebug,
                DispName = LocaleUtils.BEPINEX_DRONE_MODE_T.Localized(),
                Order = 1
            }),
            "无人机模式", ref failed, headers);

        KeybindOverlay = SetupSetting(coopDefaultDebugHeader, "按键提示层", true,
            new ConfigDescription(LocaleUtils.BEPINEX_KEYBIND_OVERLAY_T.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopDebug,
                DispName = LocaleUtils.BEPINEX_KEYBIND_OVERLAY_T.Localized(),
                Order = 0
            }),
            "按键提示层", ref failed, headers);

        // 网络（Network）

        const string networkDefaultHeader = "Network";

        ForceIP = SetupSetting(networkDefaultHeader, "强制 IP", "",
            new ConfigDescription(LocaleUtils.BEPINEX_FORCE_IP_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_FORCE_IP_T.Localized(),
                Order = 8
            }),
            "强制 IP", ref failed, headers);

        ForceBindIP = SetupSetting(networkDefaultHeader, "强制绑定 IP", "Disabled",
            new ConfigDescription(LocaleUtils.BEPINEX_FORCE_BIND_IP_D.Localized(),
            new AcceptableValueList<string>(FikaPlugin.Instance.GetLocalAddresses()), new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_FORCE_BIND_IP_T.Localized(),
                Order = 7
            }),
            "强制绑定 IP", ref failed, headers);

        UseIPv6 = SetupSetting(networkDefaultHeader, "启用 IPv6", true,
            new ConfigDescription(
                "开（默认）：主机同时提供 IPv4+IPv6 双栈并接受 IPv6 加入地址。关：回到仅 IPv4 的旧行为。影响联机连通方式，主机侧决定。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = "Enable IPv6 | 启用 IPv6",
                Order = 50
            }),
            "启用 IPv6", ref failed, headers);

        PreferIPv6 = SetupSetting(networkDefaultHeader, "优先 IPv6", true,
            new ConfigDescription(
                "开（默认）：主机同时通告两族地址时优先选 IPv6。IPv6 没有 NAT，直连通常延迟更低。仅影响本机选路。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = "Prefer IPv6 | 优先 IPv6",
                Order = 45
            }),
            "优先 IPv6", ref failed, headers);

        SimulatedLatencyMs = SetupSetting(networkDefaultHeader, "模拟延迟（毫秒）", 0,
            new ConfigDescription(
                "给每个数据包人为加上的延迟（毫秒），0 表示关闭模拟（默认 0）。用于在本机复现依赖延迟的问题，平时保持 0。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = "Simulated Latency (ms) | 模拟延迟（毫秒）",
                Order = 20
            }),
            "模拟延迟（毫秒）", ref failed, headers);

        SimulatedPacketLossPercent = SetupSetting(networkDefaultHeader, "模拟丢包（%）", 0,
            new ConfigDescription(
                "人为丢包百分比，0 表示关闭模拟（默认 0）。用于本机复现丢包相关问题，平时保持 0。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = "Simulated Packet Loss (%) | 模拟丢包（%）",
                Order = 15
            }),
            "模拟丢包（%）", ref failed, headers);

        UDPPort = SetupSetting(networkDefaultHeader, "UDP 端口", (ushort)25565,
            new ConfigDescription(LocaleUtils.BEPINEX_UDP_PORT_D.Localized(), new AcceptableValueRange<ushort>(ushort.MinValue, ushort.MaxValue),
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_UDP_PORT_T.Localized(),
                Order = 6
            }),
            "UDP 端口", ref failed, headers);

        UseUPnP = SetupSetting(networkDefaultHeader, "使用端口映射", false,
            new ConfigDescription(LocaleUtils.BEPINEX_USE_UPNP_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_USE_UPNP_T.Localized(),
                Order = 5,
                IsAdvanced = true
            }),
            "Use UPnP", ref failed, headers);

        UseNATPunching = SetupSetting(networkDefaultHeader, "使用 NAT 穿透", false,
            new ConfigDescription(LocaleUtils.BEPINEX_USE_NAT_PUNCH_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_USE_NAT_PUNCH_T.Localized(),
                Order = 4,
                IsAdvanced = true
            }),
            "使用 NAT 穿透", ref failed, headers);

        UseFikaNATPunchServer = SetupSetting(networkDefaultHeader, "使用 Fika NAT 穿透服务器", false,
            new ConfigDescription(LocaleUtils.BEPINEX_USE_FIKA_NAT_PUNCH_SERVER_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_USE_FIKA_NAT_PUNCH_SERVER_T.Localized(),
                Order = 3,
                IsAdvanced = true
            }),
            "使用 Fika NAT 穿透服务器", ref failed, headers);

        ConnectionTimeout = SetupSetting(networkDefaultHeader, "连接超时", 30,
            new ConfigDescription(LocaleUtils.BEPINEX_CONNECTION_TIMEOUT_D.Localized(),
            new AcceptableValueRange<int>(5, 60), new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_CONNECTION_TIMEOUT_T.Localized(),
                Order = 2
            }),
            "Connection Timeout", ref failed, headers);

        SendRate = SetupSetting(networkDefaultHeader, "发送频率", ESendRate.Medium,
            new ConfigDescription(LocaleUtils.BEPINEX_SEND_RATE_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_SEND_RATE_T.Localized(),
                Order = 1
            }),
            "发送频率", ref failed, headers);

        AllowVOIP = SetupSetting(networkDefaultHeader, "允许语音", false,
            new ConfigDescription(LocaleUtils.BEPINEX_NET_VOIP_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_NET_VOIP_T.Localized(),
                Order = 0
            }),
            "允许语音", ref failed, headers);

        // 玩法（Gameplay）

        DisableBotMetabolism = SetupSetting("Gameplay", "Disable Bot Metabolism",
            false, new ConfigDescription(LocaleUtils.BEPINEX_DISABLE_BOT_METABOLISM_D.Localized(),
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = LocaleUtils.BEPINEX_DISABLE_BOT_METABOLISM_T.Localized(),
                Order = 1
            }),
            "Disable Bot Metabolism", ref failed, headers);

        StrictInventorySync = SetupSetting("Gameplay", "Strict Inventory Sync",
            true, new ConfigDescription(LocaleUtils.BEPINEX_STRICT_INVENTORY_SYNC_D.Localized(),
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = LocaleUtils.BEPINEX_STRICT_INVENTORY_SYNC_T.Localized(),
                Order = 2
            }),
            "Strict Inventory Sync", ref failed, headers);

        GroundLootPickupBypass = SetupSetting("Gameplay", "Ground Loot Pickup Bypass",
            false, new ConfigDescription(
                "关（2026-10-12 起的默认）：地面拾取走原生交互路径，像其他背包操作一样转发给主机。开：恢复旧的 Fika 行为——跳过原生拾取状态、自己构造背包操作（analysis/pickup-stuck-again），一次翻转即可回到旧行为、无需重编。自 2026-10-12 起也覆盖主机自己的玩家。诊断标签：[LootDiag]。",
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = "Ground Loot Pickup Bypass | 地面物资绕过原生拾取",
                Order = 60
            }),
            "Ground Loot Pickup Bypass", ref failed, headers);

        // analysis/pickup-auto-fallback（Z4）：报告的 bug 是"玩家一直停在 EFT.PickUpState
        // （state=Pickup，gate=Can not interact in Pickup），每次拾取都得靠
        // 看门狗解锁"，而且它在原版路径上也会发生。本开关是自动恢复：
        // 一旦看门狗证明拾取卡住（在 EPlayerState.Pickup 中 5 秒，而原版
        // 拾取动画远不到两秒），本机的地面战利品拾取会为这场战局剩余时间切换到
        // 旁路 - 不用改 cfg、不用重启，每场战局一次。关闭则完全是本开关存在之前
        // 的行为：只走原版拾取，卡住的状态只由看门狗释放。每次切换都会记录 [LootDiag]
        // pickup-mode 前后行。
        GroundLootPickupAutoFallback = SetupSetting("Gameplay", "Ground Loot Pickup Auto Fallback",
            true, new ConfigDescription(
                "开（默认）：一旦看门狗确认真的卡住拾取（本地玩家整个 5 秒预算都停在拾取状态，远长于原生拾取动画），本机本局剩余时间的地面拾取自动切到 Fika 绕过路径——不用改配置、不用重启，每局最多一次。绕过路径跳过原生拾取状态，所以卡住不会再发生。关：永不自动切换，拾取一直走原生路径，卡住只能等看门狗释放（2026-10-12 之前的行为）。切换不会带到下一局。诊断标签：[LootDiag] pickup-mode。",
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = "Ground Loot Pickup Auto Fallback | 卡拾取自动回退",
                Order = 55
            }),
            "Ground Loot Pickup Auto Fallback", ref failed, headers);

        // analysis/plant-explosive-door：Icebreaker 的门上放置即使炸药在
        // 背包里，也答复"Obtain an explosive charge to plant on the door"
        // （Interactives/need_bomb_terminal）。闸门是 EFT.GameTriggers.ItemTriggerRequirement.Check，
        // 它接收一个背包控制器和一个字符串 id；修复只在该 id 于本地玩家自己的背包
        // 中找到时放宽这个答复 - 它绝不凭空造出物品。关闭：只做诊断，
        // 每一条 [PlantDiag] 行仍然出现。
        PlantInteractionFix = SetupSetting("Gameplay", "Plant Interaction Fix",
            true, new ConfigDescription(
                "开（默认）：当安放需求判定在本地背包（含背包内）确实有该物品却回答“不能安放”时，把答案强制改为“可以”，让安放能开始、门能被炸开。关：原样转发原生答案，只保留 [PlantDiag] 行（本版本之前的行为）。",
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = "Plant Interaction Fix | 安放交互修复",
                Order = 50
            }),
            "Plant Interaction Fix", ref failed, headers);

        // analysis/plant-item-consumption：放置本身答复"是"、门也炸开了，但
        // 炸药留在背包里。原版的消耗挂在放置结果上
        // （Player.PlantItemNetwork -> PlantItem -> 放置操作，其缓存的 DiscardResult 通过
        // ItemManipulator.Discard -> RemoveOperation 执行），而本移植从不为放置机器
        // 自己的控制器运行那个操作。开启（默认）时 Fika 在放置成功后完成这
        // 一个原版步骤：与原版代码相同的 ItemManipulator.Discard、包在相同的 RemoveOperation
        // 中、通过放置控制器执行（客户端像其他所有背包操作一样把它
        // 转发给主机）。关闭：背包保持
        // 不动，只出现 [PlantDiag] 消耗行。
        PlantConsumesItem = SetupSetting("Gameplay", "Plant Consumes Item",
            true, new ConfigDescription(
                "开（默认）：安放成功后执行原生消耗步骤（与游戏自己同样的移除操作），让被安放的炸药离开背包。关：背包不变，只保留 [PlantDiag] consume 行。一次翻转即可回退（analysis/plant-item-consumption）。",
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = "Plant Consumes Item | 安放消耗物品",
                Order = 45
            }),
            "Plant Consumes Item", ref failed, headers);

        // AY（analysis/plant-client-host-crash）：放置闸门（EFT.GameTriggers.ItemTriggerRequirement
        // .Check / .TryRedeem）会被地图处理器对一次放置答复两次 - 在主机
        // 运行中得到证明（analysis/plant-item-consumption 引用 logs-cgok-20261013-004053/LogOutput.log:3319-3324，
        // "op=TryRedeem -> result=True (x2)"），并由
        // logs-coop2-20261013-110333/LogOutput.log:2931-2932 中两次完全相同的发送再次证明。消耗修复通过
        // 控制器派发移除，而这是异步的，所以两次调用过去都发现炸药仍在
        // 背包里并都发送了它：主机应用第一次、拒绝第二次，而这次
        // 拒绝重同步了客户端的整个物品图，并在动画中途中止了放置。开启
        // （默认）时客户端仍转发自己的移除 - 恰好一次，走主机已被证明会应用的
        // 普通背包操作转发 - 所以客户端的放置又能消耗掉它的
        // 炸药。关闭：移除严格留在主机侧（本机自己的放置）；客户端
        // 从不发送移除，这是即使将来的版本发现无法处理转发来的移除，
        // 也能让主机的转发流量保持为零的办法。
        PlantConsumeOnClient = SetupSetting("Gameplay", "Plant Consume On Client",
            true, new ConfigDescription(
                "开（默认）：客机安放成功后，把自己的消耗移除精确转发一次给主机，炸药离开背包、两端一致。关：消耗只在主机自己做；客机安放后炸药留在背包、也不发移除（种植修复前的行为）。证据：[PlantDiag] consume / [PlantGuard] host-accept-removal。",
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = "Plant Consume On Client | 客机转发消耗",
                Order = 44
            }),
            "Plant Consume On Client", ref failed, headers);

        // AY：同一修复的主机侧 - 主机已经能判断自己无法完成的移除，
        // 会在进入该操作的原版重建之前于托管代码中被拒绝，并给出一条 [PlantGuard] 警告和
        // 通常的 Failed + 重同步应答。
        PlantRelayGuard = SetupSetting("Gameplay", "Plant Relay Guard",
            true, new ConfigDescription(
                "开（默认）：主机在重建客机发来的移除操作前先自检能否完成（描述符可读、且不是刚接受过的重复），失败则按普通拒绝回答（Failed + 权威背包）并写一行 [PlantGuard]。关：所有描述符原样进入原生重建（本版本之前的行为）。证据：[PlantGuard] 行，analysis/plant-client-host-crash。",
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = "Plant Relay Guard | 主机中继守卫",
                Order = 43
            }),
            "Plant Relay Guard", ref failed, headers);

        KeypadRelayClientSubmit = SetupSetting("Gameplay", "Keypad Relay Client Submit",
            true, new ConfigDescription(
                "开（默认）。统管整条密码门链：① 由主机按原生方式在战局内生成码表并推给客机（主机自己没有控制器时也按原生构造一个，否则主机自己也会报 Tried to enter code on location without passcodes）；② 客机装一个本地原生控制器、用主机那套码在本地判定对错（不再让 Fika 决定对错）；③ 万一表还没到、客机就先提交了，才用既有的密码尝试包转给主机兜底。关：整条链停（等同于改动前：客机无法输入密码，主机输码也可能报错）。判据：[KeypadDiag] 的 stage=codes/keys/submit/broadcast 行，analysis/icebreaker-keypad-3。",
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = "Keypad Relay Client Submit | 密码门提交中继",
                Order = 41 // (config-audit-2) 原为 44，该槽位已由 Plant Consume On Client 占用
            }),
            "Keypad Relay Client Submit", ref failed, headers);


        Fika.Core.Main.Utils.KeypadDiag.RepairEnabled = KeypadRelayClientSubmit.Value;
        KeypadRelayClientSubmit.SettingChanged += (_, _) =>
            Fika.Core.Main.Utils.KeypadDiag.RepairEnabled = KeypadRelayClientSubmit.Value;

        TransitSuppressCoLocatedExits = SetupSetting(coopDefaultHeader, "转移：抑制同位撤离点", true,
            new ConfigDescription(
                "开（默认）：当转移点与撒离点重叠在同一触发器上时，从转移交互被接受到它完成或取消期间屏蔽该撒离点，避免倒计时跑完却什么都没发生（Factory 案例）。关：只写日志，不做任何处理。主客机都生效，改完立即生效。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopTransit,
                    DispName = "Transit: Suppress Co-Located Exfiltration | 镇压同位撒离点",
                    Order = 95
                }),
            "Transit: Suppress Co-Located Exfiltration", ref failed, headers);

        // 静态字段，与其他转移开关相同的模式：启动时以及每次变更时同步。
        Fika.Core.Main.HostClasses.TransitCoLocatedExits.SuppressCoLocatedExits = TransitSuppressCoLocatedExits.Value;
        TransitSuppressCoLocatedExits.SettingChanged += (_, _) =>
            Fika.Core.Main.HostClasses.TransitCoLocatedExits.SuppressCoLocatedExits = TransitSuppressCoLocatedExits.Value;

        TransitAbortWhenSomeoneLeaves = SetupSetting(coopDefaultHeader, "转移：有人离开则取消", true,
            new ConfigDescription(
                "开（默认）：倒计时期间已登记的队友走出转移点，则取消倒计时并终止转移（原生行为）。关：倒计时继续跑，与修复前一致。作用于战局内转移逻辑，主机侧生效。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopTransit,
                    DispName = "Transit: Abort When Someone Leaves | 有人离开则取消",
                    Order = 90
                }),
            "Transit: Abort When Someone Leaves", ref failed, headers);

        Fika.Core.Main.HostClasses.TransitCountdownGuard.AbortWhenSomeoneLeaves = TransitAbortWhenSomeoneLeaves.Value;
        TransitAbortWhenSomeoneLeaves.SettingChanged += (_, _) =>
            Fika.Core.Main.HostClasses.TransitCountdownGuard.AbortWhenSomeoneLeaves = TransitAbortWhenSomeoneLeaves.Value;
        // (analysis/config-audit-2) 'Transit: Abort When Someone Dies' 已退役：字段自身的默认值
        // （false = 原版形态，死者退出、幸存者继续）就是下面运行的东西。
        Fika.Core.Main.HostClasses.TransitCountdownGuard.AbortWhenSomeoneDies = false;

        // (analysis/config-audit-2) 'Rally: Use Fika Player Count' 已退役：真实玩家数就是
        // 发布行为（FikaRallyGate.UseFikaPlayerCount = true），因为按原生记账数字等待的
        // 剧情触发会让队伍死锁（analysis/rally-gate-deadlock）。

        // (analysis/config-audit-2) 过场家族已收敛：下面的条目已退役，
        // 行为留在组件中（见属性声明上的说明）。

        // (analysis/config-audit-2) 收敛后的过场路径作为一个静态块。下面每个值都是本轮退役条目
        // 的发布默认值，因此组件的行为与之前完全一致，而
        // F12 列表只保留一个过场开关。任何单行的回滚＝重新绑定那个键。
        // 退役的实验 'Cutscene: Forward AfterPlayerTick' 保持关闭：它自己的说明指出，它转发到的
        // 原版主体会重跑本移植已经接管的工作（空投点、可同步对象）。

        // (analysis/config-audit-2) 'Rally: Allow Skip Without All Viewers' 也退役了：从未发信号的
        // 观看者不能永远扣住跳过投票（那正是同一分析中的主机死锁），所以
        // 发布行为是开启，而下面的超时是决定它何时生效的唯一旋钮。



        InertOutsideFikaRaids = SetupSetting(coopDefaultHeader, "非 Fika 战局中不生效", true,
            new ConfigDescription(
                "开（默认）：本端口所有战局内钩子（物资掉落、弹匣卸载、世界与集合点交互、过场诊断、逐帧采样）在非 Fika 战局（从地图选择界面直接开的单人战局）里完全不介入，每个被挡回的钩子写一行 [Inert] skip=... reason=not-a-fika-raid side=single。关：回到修复前的行为——补丁在原生单人战局里也会动作（2026-10-12 23:52 那次崩溃就是在这种状态下打的）。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopCore,
                    DispName = "Inert Outside Fika Raids | 非 Fika 战局内静默",
                    Order = 50
                }),
            "非 Fika 战局中不生效", ref failed, headers);

        Fika.Core.Main.Utils.FikaRaidGate.InertOutsideFikaRaids = InertOutsideFikaRaids.Value;
        InertOutsideFikaRaids.SettingChanged += (_, _) =>
            Fika.Core.Main.Utils.FikaRaidGate.InertOutsideFikaRaids = InertOutsideFikaRaids.Value;

        // Coop | Boss & Spawns。主机战局准备读取的三个开关
        // （Main.GameMode.HostGameController）；它们是静态字段，所以在这里以及每次
        // 变更时保持同步，与转移闸门使用的模式相同。三者都在战局准备时决定，
        // 即改动作用于下一场战局；这里没有任何东西是逐帧读取的。
        // （已退役，analysis/boss-wedge-vanilla）'Boss: One Boss Per Trigger Point' 已移除：boss 在联机中
        // 生成失败后，用户要求重新使用原版机制。主机现在原样上报
        // 发布的波次列表（HostGameController.CollapseBossWavesPerTrigger = false）。

        MirrorOfflineSpawnFlags = SetupSetting(coopDefaultHeader, "出生：镜像离线标志", true,
            new ConfigDescription(
                "开：按原生离线准备镜像 OfflineOldSpawn/OfflineNewSpawn 与 WAVE_COEF_* 缩放，让“是否放行刷新”的判断与无头主机及原版游戏一致（它本身不决定刷不刷）。关：使用未镜像的原始标志。排障用，平时保持开启。主机侧开关，准备战局时读取。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopBossSpawns,
                    DispName = "Spawn: Mirror Offline Flags | 镜像离线刷新标志",
                    Order = 90
                }),
            "Spawn: Mirror Offline Flags", ref failed, headers);

        EnableAmbientBotCapFallback = SetupSetting(coopDefaultHeader, "出生：环境杂兵兜底上限", false,
            new ConfigDescription(
                "关（默认）：地图数据的 BotMax 保持原值，破冰船 BotMax=0 因此不刷常规杂兵（符合“不动原版刷新”）。开：仅当 BotMax<=0 且 BotStop>0 时兜底 SPT 风格的上限（18，仍受本局机器人预算限制），该图会开始刷环境杂兵；一般不要开。排障用，主机侧开关。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopBossSpawns,
                    DispName = "Spawn: Ambient Bot Cap Fallback | 环境杂兵兜底上限",
                    Order = 80
                }),
            "Spawn: Ambient Bot Cap Fallback", ref failed, headers);

        Fika.Core.Main.GameMode.HostGameController.CollapseBossWavesPerTrigger = false;
        Fika.Core.Main.GameMode.HostGameController.MirrorOfflineSpawnFlags = MirrorOfflineSpawnFlags.Value;
        Fika.Core.Main.GameMode.HostGameController.EnableAmbientBotCapFallback = EnableAmbientBotCapFallback.Value;
        MirrorOfflineSpawnFlags.SettingChanged += (_, _) =>
            Fika.Core.Main.GameMode.HostGameController.MirrorOfflineSpawnFlags = MirrorOfflineSpawnFlags.Value;
        EnableAmbientBotCapFallback.SettingChanged += (_, _) =>
            Fika.Core.Main.GameMode.HostGameController.EnableAmbientBotCapFallback = EnableAmbientBotCapFallback.Value;

        // Debug | Diagnostics（analysis/coop-perf）。FrameCostProbe 测量 Fika 自己的帧工作；该
        // 探针是本开关与 'Diagnostics' 总开关的 OR（FrameCostProbe.On），所以这个
        // 条目是"只要这一行、不要整批探针"的开关。该字段在每个入口点都会被
        // 读取，因此在这里以及每次变更时应用，就能让配置与探针保持
        // 同步而无需重启。


        // (analysis/stutter-pose-reset) 同一调查的写入侧探针。契约与帧开销探针相同：
        // 自己的开关，或 'Diagnostics' 总开关，且变更即时生效 - 因此可以在战局中途
        // 无需重启就切换，下一个窗口就会带上该行。


        // (analysis/stutter-frame-skip) 网络投递候选的唯一旋钮：0 保留无界
        // 排空（默认，行为不变），正值限定单帧投递多少个已到达的事件，并把其余事件
        // 按顺序推迟到下一帧。它在客户端启动时读取，因此
        // 下一场战局生效。
        NetPollBudget = SetupSetting(coopDefaultHeader, "每帧最大网络事件数", 96,
            new ConfigDescription(
                "默认 96（实测一帧曾一次投递 185 条事件）。一帧最多投递这么多条已到达的网络事件（含每包的解析与回调），这一帧没处理完的留在队列里，下一帧按原顺序继续投递——不丢包、不乱序，只是把「一次网络突发全压在一帧」摊到多帧，去掉那个 100ms 级的大帧。判据：开「Stutter Probe」看 clientUpdate 的 max（应从 ~164ms 降到 <60ms）与 [Stutter] 串时长、[NetPoll] 行的 bursts/max/deferredTotal。**设为 0 = 完全回到改动前行为**（一键回退）；觉得还卡可以再降到 48 试。下一个战局生效。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayDebugDiagnostics,
                    DispName = "Net Maximum Events Per Frame | 单帧网络事件上限",
                    Order = 12
                }),
            "每帧最大网络事件数", ref failed, headers);

        // (analysis/stutter-frame-skip) 「被抽帧」形态的日志：连续长帧的段，
        // 它们的节奏、段内的回收，以及我们自己主线程文件 IO 的实测代价。
        // 契约与其他探针相同：自己的开关，或 'Diagnostics' 总开关。


        // (analysis/stutter-pose-reset) 守卫本身，默认开启。这就是用户要求的一键回滚：
        // 关掉它会在不重新构建的情况下精确恢复守卫前的行为（每个写入点又变成
        // 逐帧写入），并且让 A/B 能在单场战局中度量 - 关守卫、
        // 开守卫、同一地图、同样 bot 数量、同一行 [PoseWrite]。





        FinalMissionEndingRepair = SetupSetting(coopDefaultHeader, "终局任务：结局修复", true,
            new ConfigDescription(
                "默认开。终点站结局（救世主结局等）的修复：完成信号两种 id 都接受（原生送的是场景名，不是 cutsceneId）、结局场景的采样/救援/收尾看门狗、完成确认重发，以及把 CoopGame.Stop 的 DeferStop 在 90 秒后强制放行——否则结局界面会永久卡住、不结算。关：回到改动前行为（仍会写 [FinalDiag] 判据）。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayDebugDiagnostics,
                    DispName = "Final Mission: Ending Repair | 结局结算修复",
                    Order = 21
                }),
            "终局任务：结局修复", ref failed, headers);

        // (analysis/stutter-frame-skip) 会话日志复制是本移植在游戏自身线程上执行的唯一周期性
        // 文件 IO；开启（默认）时它在归档自己的线程上运行，帧内只
        // 立一个标志。关闭即一键回滚，也是该改动的战局内 A/B。

        // (analysis/config-audit-2) 这个探针现在也需要总开关，与 [NetTimeout] 行完全一样：
        // 'Diagnostics' 关闭意味着每个探针族都静默（这是性能清理写下的契约），
        // 而不是某一个族不管怎样都每秒写一行。
        // 退役的 'Cutscene: Real Time Ticks' 保持其默认值的行为（BF 慢动作修复）。
        Fika.Core.Main.Components.FikaFinalMissionDirector.EndingRepair = FinalMissionEndingRepair.Value;
        FinalMissionEndingRepair.SettingChanged += (_, _) => Fika.Core.Main.Components.FikaFinalMissionDirector.EndingRepair = FinalMissionEndingRepair.Value;

        // (2026-10-26 W15) Coop / Pose Write Guards 接回（上游 3.0.4 同款开关）：关掉即完全恢复"每帧都写"的改前行为，
        // 方便同一局里做 A/B；默认开。开关只写本机静态字段，不参与任何联机协议。
        PoseWriteGuards = SetupSetting(coopDefaultHeader, "Pose Write Guards", true,
            new ConfigDescription(
                "默认开。开：观察者玩家的姿态/移动/动画写入加「值未变就不写」守卫，避免每帧重复推 animator。关：完全恢复改前行为（每个写入点每帧都写），只多一次静态 bool 读取。本机生效。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayDebugDiagnostics,
                    DispName = "Pose Write Guards | 姿态写入守卫",
                    Order = 15
                }),
            "Pose Write Guards", ref failed, headers);

        Fika.Core.Main.Utils.PoseWriteGuards.Enabled = PoseWriteGuards.Value;
        PoseWriteGuards.SettingChanged += (_, _) => Fika.Core.Main.Utils.PoseWriteGuards.Enabled = PoseWriteGuards.Value;

        SentrySyncCardAccess = SetupSetting(coopDefaultHeader, "哨兵：同步刷卡放行", true,
            new ConfigDescription(
                "默认开。终点站哨兵（RAUF）的「刷卡放行」原本只存在于主机那台机器的哨兵集合里，客机永远被当成敌人——本条把这条事实按原生形状同步给每台机器（对每台机器的哨兵调用原生 OnTerminalAccessGained，并用原生 HaveAccess 复核）。关：只写 [SentryDiag] 判据，不做同步（改动前行为）。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayDebugDiagnostics,
                    DispName = "Sentry: Sync Card Access | 哨兵刷卡放行同步",
                    Order = 22
                }),
            "Sentry: Sync Card Access", ref failed, headers);

        Fika.Core.Main.Components.FikaSentryAccess.RepairEnabled = SentrySyncCardAccess.Value;
        SentrySyncCardAccess.SettingChanged += (_, _) =>
            Fika.Core.Main.Components.FikaSentryAccess.RepairEnabled = SentrySyncCardAccess.Value;

        // (analysis/client-transit-terminal) 联机转移卡死：客户端的按下没有在自己机器上
        // 产生请求，所以主机从未收到它，而它的"所有人都已交互"闸门拒绝了整个小队。
        TransitClientPressFallback = SetupSetting(coopDefaultHeader, "转移：客机按下兜底", true,
            new ConfigDescription(
                "默认开。客机在转移点按下后 2.5 秒内本机没有产出、主机也没有受理时，由客机用同一个原生构造器重发一次等价请求（前提与原生一致：在点内、有访问权、满足入场条件、处于 idle 状态），每次按下最多一次、每局最多 8 次；主机的全部门照旧，不做任何绕过。关：只写判据行（改动前行为）。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopTransit,
                    DispName = "Transit: Client Press Fallback | 客机转移请求补发",
                    Order = 56
                }),
            "转移：客机按下兜底", ref failed, headers);

        // (analysis/config-audit-2) 退役的 'Transit: Press Fallback Limit' 保持其发布边界
        // （TransitRecovery.PressFallbackLimit = 8）。合并后的客户端暂存箱开关接在
        // 下面绑定 'Transit: Per-Player Transfer Stash' 的地方 - 绝不在这里，该条目在 SetupConfig
        // 的这个位置仍为 null（analysis/config-binding-order-regression 的绑定顺序回归）。
        TransitClientPressFallback.SettingChanged += (_, _) =>

        // Coop | Cutscene。客户端的原地触发器（Main.Components.FikaLocalCutsceneTrigger）是
        // "客户端也播过场"的客户端那一半：它在本地播放被丢弃的原版开始调用，并
        // 对主机派发的任何过场让位（该数据包在到达时就会被记录，早于查看本地
        // 请求）。压后延迟刻意设为 0（2026-10-27 T5）：等待的客户端并不能
        // 避免重复启动，它只会推迟那些仅存在于本机的过场。
        // 已退役的条目（analysis/config-audit-2），发布行为作为一个静态块。
        Fika.Core.Main.Components.FikaLocalCutsceneTrigger.UseNativeClientTrigger = true;
        Fika.Core.Main.Components.FikaLocalCutsceneTrigger.SingleLocalViewer = true;
        Fika.Core.Main.Components.FikaLocalCutsceneTrigger.LocalTriggerDelaySeconds = 0f;

        // Coop | Transit，转移恢复轮次。三个客户端开关在处理拒绝的地方读取，暂存箱开关
        // 在玩家进入目标地图时读取，所以在这里以及每次变更时应用，
        // 能让运行中的会话保持同步。

        TransitRearmAfterRefusal = SetupSetting(coopDefaultHeader, "转移：被拒后重新就绪", true,
            new ConfigDescription(
                "开（默认）：客机被主机拒绝后，把这一轮被拒的后果一次收拾干净——复位本机按键闩锁（可以立刻再按一次；修复前提示不清也无法再按，观感就是「卡在互动」），并重报一次「我在点内」，避免主机把客机当成已经离开转移点。两件事都由这一个开关控制（config-audit-2 把重复的「Reassert Presence After Refusal」并了进来）。关：两件事都不做（修复前行为）。客机侧开关。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopTransit,
                    DispName = "Transit: Rearm After Refusal | 被拒后恢复（按键 + 在点内重报）",
                    Order = 54
                }),
            "Transit: Rearm After Refusal", ref failed, headers);

        TransitPerPlayerTransferStash = SetupSetting(coopDefaultHeader, "转移：按玩家转移容器", true,
            new ConfigDescription(
                "开（默认）：进入终点站时给每个玩家（含客机）补建“装备转移容器”——仅在缺失或尺寸为 0×0 时补建，绝不清空已有内容，也就是原生 OnPlayerEnter 该做的事；关：回到旧行为（客机拿不到转移容器）。主机侧开关。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopTransit,
                    DispName = "Transit: Per-Player Transfer Stash | 每人补建转移容器",
                    Order = 52
                }),
            "Transit: Per-Player Transfer Stash", ref failed, headers);

        TransitActivateCountdownPanel = SetupSetting(coopDefaultHeader, "转移：唤醒倒计时面板", true,
            new ConfigDescription(
                "默认开。转移倒计时开始时，主动把转移计时面板（LocationTransitTimerPanel）自己打开一次（只开面板本身；祖先没开就只写判据、不强行打开），让客机也能看到倒计时。关：只写判据行（改动前行为）。离开点仍然隐藏并暂停，回来继续。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopTransit,
                    DispName = "Transit: Activate Countdown Panel | 唤醒转移倒计时面板",
                    Order = 59
                }),
            "Transit: Activate Countdown Panel", ref failed, headers);


        // (analysis/config-audit-2) 退役的 'Transit: Reassert Presence After Refusal' 是同一项"拒绝后
        // 清理"修复的后半部分，所以它跟随这一个开关。
        // (analysis/config-audit-2) 退役的 'Transit: Client Transfer Stash' 是客户端机器上的同一项容器
        // 修复，所以两半现在都跟随这一个开关（热生效，与本块其余部分一样）。

        if (failed)
        {
            var headerString = string.Join(", ", headers);
            Singleton<PreloaderUI>.Instance.ShowErrorScreen(LocaleUtils.UI_LOCALE_ERROR_HEADER.Localized(),
                string.Format(LocaleUtils.UI_LOCALE_ERROR_DESCRIPTION.Localized(), headerString));
            FikaGlobals.LogError("SetupConfig: Headers failed: " + headerString);
        }

        // (analysis/crash-dump-20261013) begin/end 这一对的另一半。有 'begin' 却缺少
        // 'end' = 进程死在绑定过程中； 
        SetupConfigEventHandlers();
    }

    /// <summary>
    /// (analysis/config-audit-2, 2026-10-13) 本轮退役的设置：每一个要么与另一个键重复、
    /// 要么已没有能改变战局的读取点、要么只在二分配置中可达。ConfigFile.Remove
    /// 还会把键从已存在的 com.fika.core.cfg 中删除 - 没有这一步，文件（以及列出文件
    /// 所有条目的 F12 菜单）会留下一个看起来仍是活开关的孤儿。每个退役键的行为
    /// 都作为读取它的组件的静态默认值存续下来，所以战局行为没有任何变化；重新绑定
    /// 一个键就是回滚。build：旧键 -> 新键对照表见
    /// analysis/config-audit-2/REPORT.md。
    /// </summary>
    private void RetireRemovedEntries()
    {
        string[][] retired =
        [
            ["Coop", "Show Final Countdown Screen"],
            ["Coop", "Raid Start Audio Fades"],
            ["Coop", "Raid Start Menu Music"],
            ["Coop", "Countdown: Activate Game UI"],
            ["Coop", "Countdown: Bind Extraction Timer Panel"],
            ["Coop", "Countdown: Deploy Wait"],
            ["Coop", "Countdown: Restore Raid View"],
            ["Coop", "Countdown: Start Raid View Routine"],
            ["Coop", "Countdown: Sync Transit Controllers"],
            ["Coop", "Countdown: Dispatch Raid Started Event"],
            ["Coop", "Countdown: VOIP Audio Fix"],
            ["Coop", "Countdown: Opening Countdown Overlay"],
            ["Coop", "Countdown: Opening Countdown Close"],
            ["Coop", "Countdown: Opening Countdown View Restore"],
            ["Coop", "Countdown: Opening Countdown Reassert"],
            ["Coop", "Cutscene: Rescue Local Scene"],
            ["Coop", "Cutscene: Forward AfterPlayerTick"],
            ["Coop", "Cutscene: Repair Bridge Playback"],
            ["Coop", "Cutscene: Dismiss Skip Overlay"],
            ["Coop", "Cutscene: Join Cutscenes Started By The Host"],
            ["Coop", "Cutscene: Accept Signals From Unlisted Viewers"],
            ["Coop", "Cutscene: Every Participant Joins The Cutscene"],
            ["Coop", "Cutscene: Client Triggers Locally"],
            ["Coop", "Cutscene: Client Trigger Single Viewer"],
            ["Coop", "Cutscene: Client Trigger Delay Seconds"],
            ["Coop", "Cutscene: Real Time Ticks"],
            ["Coop", "Rally: Use Fika Player Count"],
            ["Coop", "Rally: Allow Skip Without All Viewers"],
            ["Coop", "Transit: Abort When Someone Dies"],
            ["Coop", "Transit: Press Fallback Limit"],
            ["Coop", "Transit: Client Transfer Stash"],
            ["Coop", "Transit: Reassert Presence After Refusal"],
            ["Coop", "Commit Watch Hard Release"],
            // (analysis/remove-reconnect, 2026-10-14) 断线重连功能已移除：它的两个
            // 键会从现有配置文件中删除，以免 F12 菜单里留下孤儿开关。
            ["Coop", "Reconnect: Rejoin After Disconnect"],
            ["Coop", "Reconnect: Settle Expired Rejoin"],
            // (analysis/boss-wedge-vanilla) 用户要求重新使用原版 boss 机制：折叠开启时，
            // boss 在联机中完全不再生成，所以发布的波次列表被原样使用。
            ["Coop", "Boss: One Boss Per Trigger Point"],
            // (analysis/stutter-frame-skip) 该键已改名，以便新的默认值（96）能到达每个现有
            // 配置文件：旧键存的是 0，即"无预算"行为，帧转储显示它正是
            // 100 毫秒以上 clientUpdate 尖峰的原因。
            ["Coop", "Net Poll Budget"],
            // (2026-10-14，移除诊断) 本移植的所有诊断/取证开关都已移除：移植不再写
            // Fika-diagnostics.log，也不再写崩溃轨迹，所以这些键也从现有配置文件中
            // 删除（F12 菜单和以后每个 cfg 都不会再有死开关）。
            ["Coop", "Net Timeout Diagnostic"],
            ["Coop", "Main Menu Ref Trace"],
            ["Coop", "Diagnostics Verbose"],
            ["Coop", "Diagnostics"],
            ["Coop", "Crash Forensics"],
            ["Coop", "Commit Watch"],
            ["Coop", "Commit Watch Warn MiB"],
            ["Coop", "Commit Watch Critical MiB"],
            ["Coop", "Commit Watch Relief"],
            ["Coop", "Skip Countdown Body"],
            ["Coop", "Skip Post Wait Block"],
            ["Coop", "Frame Cost Probe"],
            ["Coop", "Pose Write Probe"],
            ["Coop", "Stutter Probe"],
            ["Coop", "Log Archive"],
            ["Coop", "Mirror Diagnostics"],
            ["Coop", "Cutscene: Sync Probe"],
            ["Coop", "Log Archive Async"],
            ["Coop", "Transit: Verdict Log Lines"],
            ["Gameplay", "Keypad Diagnostics"],
        ];

        var removed = 0;
        foreach (var entry in retired)
        {
            try
            {
                if (_config.Remove(new ConfigDefinition(entry[0], entry[1])))
                {
                    removed++;
                }
            }
            catch (Exception exception)
            {
                FikaGlobals.LogWarning($"Could not retire the setting {entry[0]} / {entry[1]}: {exception.Message}");
            }
        }

        if (removed > 0)
        {
            
        }
    }

    private void SetupConfigEventHandlers()
    {
        OfficialVersion.SettingChanged += OfficialVersion_SettingChanged;
    }

    private void OfficialVersion_SettingChanged(object sender, EventArgs e)
    {
        FikaVersionLabel_Patch.UpdateVersionLabel();
    }

    private string CleanConfigString(string header)
    {
        var original = string.Copy(header);
        var foundForbidden = false;
        foreach (var character in (char[])['\n', '\t', '\\', '\"', '\'', '[', ']'])
        {
            if (header.Contains(character))
            {
                FikaGlobals.LogWarning($"Header '{original}' contains an illegal character: {character}\nReport this to the developers!");
                header = header.Replace(character, char.MinValue);
                foundForbidden = true;
            }
        }

        if (foundForbidden)
        {
            FikaGlobals.LogWarning($"Header '{original}' was changed to '{header}'");
        }
        return header;
    }

    internal void GetClientConfig()
    {
        var clientConfig = FikaRequestHandler.GetClientConfig();

        UseBTR = clientConfig.UseBTR;
        FriendlyFire = clientConfig.FriendlyFire;
        DynamicVExfils = clientConfig.DynamicVExfils;
        AllowFreeCam = clientConfig.AllowFreeCam;
        AllowSpectateFreeCam = clientConfig.AllowSpectateFreeCam;
        AllowItemSending = clientConfig.AllowItemSending;
        BlacklistedItems = clientConfig.BlacklistedItems;
        ForceSaveOnDeath = clientConfig.ForceSaveOnDeath;
        UseInertia = clientConfig.UseInertia;
        SharedQuestProgression = clientConfig.SharedQuestProgression;
        CanEditRaidSettings = clientConfig.CanEditRaidSettings;
        EnableTransits = clientConfig.EnableTransits;
        AnyoneCanStartRaid = clientConfig.AnyoneCanStartRaid;
        AllowNamePlates = clientConfig.AllowNamePlates;
        RandomLabyrinthSpawns = clientConfig.RandomLabyrinthSpawns;
        PMCFoundInRaid = clientConfig.PMCFoundInRaid;
        AllowSpectateBots = clientConfig.AllowSpectateBots;
        InstantLoad = clientConfig.InstantLoad;
        FastLoad = clientConfig.FastLoad && !InstantLoad; // 只能用其一，优先 InstantLoad
        ReviveConfig = clientConfig.ReviveConfig;

        // (2026-10-20) 本代码树中不存在复活补丁；配置块也随之移除。

        clientConfig.LogValues();
    }
}
