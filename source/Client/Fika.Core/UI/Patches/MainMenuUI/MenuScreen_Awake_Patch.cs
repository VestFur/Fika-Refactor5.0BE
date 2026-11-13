using System.Reflection;
using EFT.UI;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Websocket;
using Fika.Core.UI.Custom;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.UI.Patches.MainMenuUI;

public class MenuScreen_Awake_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(MenuScreen)
            .GetMethod(nameof(MenuScreen.Awake));
    }

    /// <summary>
    /// 把注入的存在感/面板行为添加到引擎持有的菜单界面上。
    ///
    /// 在该行为运行任何东西之前先把它根住（并拆除上一个）：
    /// 引擎会在它上面调用 Start/OnEnable/OnDisable，所以它的托管侧必须从存在的
    /// 那一刻起就保持可达。一个唤醒两次的菜单界面（重新登录）绝不能最终留下
    /// 两个行为，各自带着自己的面板和自己注册到引擎上的监听器。
    /// </summary>
    [PatchPostfix]
    public static void Postfix(MenuScreen __instance)
    {
        if (__instance == null)
        {
            return;
        }

        var gameObject = __instance.gameObject;
        if (gameObject == null)
        {
            
            return;
        }

        // 存在感注册的兜底路径。这个面板显示的名册由
        // 服务器从其在线列表填充，而只有当
        // /fika/notification/ websocket 连接成功时玩家才会进入该列表。那个 socket 通常由
        // TarkovApplication_InitNotificationManager_Patch 打开；这次调用让面板即使在
        // 该补丁无法安装时（它针对的是构建中可能被改名的游戏方法）也能工作。
        FikaNotificationManager.EnsureCreated("MenuScreen.Awake");

        var existing = gameObject.GetComponent<MainMenuUIScript>();
        if (existing != null)
        {
            MainMenuUIScript.Attach(existing);
            return;
        }

        MainMenuUIScript.Attach(gameObject.AddComponent<MainMenuUIScript>());
    }
}
