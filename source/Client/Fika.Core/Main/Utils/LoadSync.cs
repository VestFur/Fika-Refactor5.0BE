using System;
using Comfort.Common;
using EFT;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 唯一写出加载界面 / 会话等待路径上 "[LoadSync]" 判定行的地方。
///
/// 为什么要单独做一个辅助类：测试者必须能用单一模式在两种日志里 grep 到这些行
/// （"[LoadSync]"）—— 加载界面旁边的游戏日志，以及崩溃追踪诊断文件 ——
/// 并且它们必须在安静诊断模式下仍然保留，
/// 该模式只保留 FikaDiagnostics.QuietSteps 中列出的作用域。那个列表是前缀
/// 匹配的，因此作用域 "LoadingScreen"（已在列表中）也会一并保留
/// "LoadingScreen.LoadSync"，而无需改动诊断基础设施本身。
///
/// 一行的形态，刻意固定下来（测试者会在两台机器上对照着读这些行）：
///   [LoadSync] &lt;正在发生什么&gt;: role=host|client, ... =&gt; &lt;判定&gt;
/// 每一行都会写明本地状态、对端的关键状态，以及在适用时
/// 什么样的情况会解除等待。
/// </summary>
internal static class LoadSync
{
    /// <summary>诊断作用域：以前缀匹配安静模式的白名单（"LoadingScreen"）。</summary>
    internal const string Scope = "LoadingScreen.LoadSync";

    /// <summary>
    /// 主机 raid 前屏障的兜底值（秒），来自 <see cref="LoadSyncGate"/>（有一个配置项
    /// 绑定到那个字段；为什么该值必须可调，见那里的类注释）。
    /// </summary>
    internal static float PeerLoadTimeoutSeconds => LoadSyncGate.HostPeerLoadTimeoutSeconds;

    // ------------------------------------------------------------------ 对端存活时间戳
    //
    // 两条事实，[LoadSync] 行需要在 *两种* 结果下都可读（"一切正常" 与
    // "什么都没发生"）：对端最后一个包何时到达，以及在客户端上 —— 传输层是否
    // 仍处于连接状态。传输层由 LiteNetLib 自己的线程维护（ack 与
    // keepalive），因此当对端的 *主线程* 正忙于加载地图时它依然存活，而这
    // 恰恰是绝不能被误判为对端已死的情形。

    private static float _lastPeerPacketAt;
    private static string _lastPeerPacketSource = "none";
    private static int _peerPacketCount;

    /// <summary>统计会话的一个入站包（从接收入口调用）。</summary>
    internal static void NotePeerPacket(string source)
    {
        try
        {
            _lastPeerPacketAt = UnityEngine.Time.realtimeSinceStartup;
            _lastPeerPacketSource = source;
            _peerPacketCount++;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>距最后一个入站包的秒数；尚未收到任何包时为 -1。</summary>
    internal static int SecondsSincePeerPacket()
    {
        var last = _lastPeerPacketAt;
        if (last <= 0f)
        {
            return -1;
        }

        return (int)(UnityEngine.Time.realtimeSinceStartup - last);
    }

    /// <summary>关于入站流量的一句话，用于判定行。</summary>
    internal static string DescribePeerTraffic()
    {
        return "inbound=" + _peerPacketCount + " sinceLastInbound=" + SecondsSincePeerPacket() + "s (last="
            + _lastPeerPacketSource + ")";
    }

    /// <summary>
    /// 测试者必须在两种日志里都能看到的一行：游戏日志（就在加载界面旁边）和
    /// 诊断文件。调用方名称被显式传入，因此游戏日志读作
    /// "[WaitForOtherPlayersToLoad]: [LoadSync] ..." 而不是 "[Report]: ..."。
    /// </summary>
    internal static void Report(string caller, string message)
    {
        // (2026-10-18 refactor W1 / tracker A16) 仅诊断：加载等待不再每次轮询写一行。
        // 入口保留，以免任何调用点改动；必须保留下来的失败行
        // （超时、对端始终未到）是下面的 Warn。
    }

    /// <summary>一行指明降级结果（等待超时、对端始终未到）。</summary>
    internal static void Warn(string caller, string message)
    {
        FikaGlobals.LogWarning("[LoadSync] " + message, caller);
        
    }

    /// <summary>仅诊断：每次轮询的细节，它会把游戏日志刷爆。</summary>
    internal static void Trace(string message)
    {
        
    }

    /// <summary>
    /// 本地加载界面当前持有的行，作为一个字符串："screen=2 row(s)
    /// [netId(nickname)=progress, ...]"。这是每条判定行中「本地进度 / 对端进度」的那一半，
    /// 也是从日志中看出引擎驱动的界面是否真的
    /// 收到了这些行与进度值的唯一途径。
    /// </summary>
    internal static string DescribeScreen()
    {
        try
        {
            var screen = LoadingScreenUI.Instance;
            return screen == null ? "screen=absent" : "screen=" + screen.DescribeRows();
        }
        catch (System.Exception exception)
        {
            return "screen=unreadable (" + exception.GetType().Name + ")";
        }
    }

    /// <summary>当本机存在游戏世界时为真（每个绑定世界的包都需要的状态）。</summary>
    internal static bool WorldReady
    {
        get
        {
            try
            {
                return Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance != null;
            }
            catch (System.Exception)
            {
                return false;
            }
        }
    }
}
