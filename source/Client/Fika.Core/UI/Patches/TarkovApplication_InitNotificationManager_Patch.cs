using EFT.Communications;
using System.Reflection;
using EFT;
using Fika.Core.Networking.Websocket;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.UI.Patches;

/// <summary>
/// 应用启动后创建 <see cref="FikaNotificationManager"/>。
///
/// 这是唯一打开 SPT 的 <c>/fika/notification/</c> websocket 的地方，而正是该
/// socket 在服务器上登记玩家的在线状态
/// （<c>FikaServer.WebSockets.NotificationWebSocket.OnConnectionAsync</c> 是
/// <c>PresenceService.AddPlayerPresence</c> 的唯一调用方）。在此补丁未生效时无人宣告
/// 自身在线，<c>/fika/presence/get</c> 返回空数组，主菜单面板无法
/// 显示任何一行——缺失的自己这一条就在这里，不在面板里，也不在
/// presence provider 里。
///
/// 47888 重命名了被钩住的方法：<c>TarkovApplication.InitNotificationManager</c>（本补丁
/// 一直用到 47242 的这个名字）变成了 <c>TarkovApplication.EnsureNotificationManagerAsync</c>，
/// 为 private 且返回 <c>Task</c>。该重命名已在 1.2.0.47965 上确认：interop 只暴露
/// <c>EnsureNotificationManagerAsync()</c>，而两个构建中都没有成员名为
/// <c>InitNotificationManager</c>，因此旧名字不作为查找保留——一个永远无法
/// 匹配的分支只会把将来的重命名藏到第二次静默 null 之后。
/// </summary>
public class TarkovApplication_InitNotificationManager_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        // 该成员在游戏构建中是 private，因此查找必须包含非公开的
        // 实例方法；否则 47888 的重命名会让补丁静默失效（加载器
        // 随后会报「target method does not exist in this game build」）。
        return typeof(TarkovApplication)
            .GetMethod("EnsureNotificationManagerAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    /// <summary>
    /// 在原生初始化器返回其 task 时运行。管理器本身不依赖
    /// 通知 UI：<see cref="FikaNotificationManager"/> 只在其
    /// 构造函数中打开 socket，它渲染的每条通知都经由
    /// 主线程上的 <c>Singleton&lt;PreloaderUI&gt;.Instance?.NotifierView</c>，因此
    /// 提前几个 await 创建它不会出错——而更早连接正是让服务器
    /// 在主菜单请求 presence 列表之前就认识该玩家的原因。
    /// </summary>
    [PatchPostfix]
    internal static void Postfix()
    {
        FikaNotificationManager.EnsureCreated("TarkovApplication notification manager");
    }
}
