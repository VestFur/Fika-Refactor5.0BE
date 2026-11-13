using System;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 加载界面 / 会话等待路径的阈值，作为 *静态字段*，这样配置项就能绑定到它们，
/// 而无需在此处再改一次（与 TransitStartGate 相同的模式：FikaConfig 在启动时
/// 以及每次 SettingChanged 时给这些字段赋值，调用点只读取它们）。
///
/// 每个值都是兜底，而非正常路径：等待会在其所等条件满足时结束，
/// 这些数字只决定一个无法被满足的等待何时放弃，以及何时 *如实地报告这一点*
/// （一行 [LoadSync]，见 LoadSync）。
///
/// 为什么它们必须能在现场调整：整个类的存在就是因为一个写死的值是错的 ——
/// 客户端过去会在固定的 40 s 之后就放弃等待主机的地点确认
/// （TarkovApplication_LocalGameCreator_Patch，2026-10-11 18:58 那一轮：主机需要 145 s 才能加载
/// TarkovStreets），而主机的 raid 前屏障完全没有上限，于是它会为一台早已放弃的对端
/// 等上好几分钟。两者都取决于机器与地图，也就是说正是那种
/// 必须能在不重新构建的前提下调整的数字。
/// </summary>
public static class LoadSyncGate
{
    /// <summary>
    /// 主机侧：raid 前屏障（<c>HostGameController.WaitForOtherPlayersToLoad</c>）
    /// 要为一台仍处于连接但尚未宣告角色的对端等待多久。已断开的对端
    /// 不消耗这份预算 —— <c>FikaServer.OnPeerDisconnected</c> 会递减
    /// PlayerAmount，屏障随之立即收缩。默认 300 s。
    /// </summary>
    public static int HostPeerLoadTimeoutSeconds = 300;

    /// <summary>
    /// 客户端侧：在一次正常 raid 启动中，客户端等待主机握手步骤（地点确认
    /// 以及主机自身的 raid 初始化）的时长。这个等待
    /// 还额外受传输层约束：一旦与主机的连接不再是 Connected，
    /// 它就立即失败，而不会用掉这份预算。默认 600 s。
    /// </summary>
    public static int ClientHostWaitCeilingSeconds = 600;

    /// <summary>
    /// 客户端侧：中转重载窗口（<c>FikaBackendUtils.IsTransit</c>）内的同一个上限。
    /// 这个窗口里两台机器都刚拆掉各自的世界、正在加载下一张
    /// 地图，因此仅凭更慢的磁盘，主机就可能要几分钟才会作答。它与
    /// 常规上限分开，因为中转重载恰恰就是固定 40 s 上限搞错的那一情形。
    /// 默认 900 s。
    /// </summary>
    public static int ClientTransitReloadCeilingSeconds = 900;

    /// <summary>
    /// 客户端侧：在与主机之间连续这么多秒没有应用层响应之后，
    /// [LoadSync] 行会升级为警告。它本身从不结束等待（那由传输层决定），
    /// 它只是让状态可见：一个主线程忙碌（忙于自己的地图加载）的主机会停止
    /// 作答，而它的 socket 线程仍在维持连接存活。默认 30 s。
    /// </summary>
    public static int ClientSilentWarnSeconds = 30;

    /// <summary>客户端在当前机器所处状态下的等待上限。</summary>
    public static int ClientCeilingSeconds(bool transit)
    {
        var ceiling = transit
            ? Math.Max(ClientHostWaitCeilingSeconds, ClientTransitReloadCeilingSeconds)
            : ClientHostWaitCeilingSeconds;
        return Math.Max(30, ceiling);
    }

    /// <summary>当前生效的值，用于一次运行的 [LoadSync] 头部行。</summary>
    public static string Describe()
    {
        return "hostPeerLoadTimeout=" + HostPeerLoadTimeoutSeconds + "s, clientHostWaitCeiling="
            + ClientHostWaitCeilingSeconds + "s, clientTransitReloadCeiling=" + ClientTransitReloadCeilingSeconds
            + "s, clientSilentWarn=" + ClientSilentWarnSeconds + "s";
    }
}
