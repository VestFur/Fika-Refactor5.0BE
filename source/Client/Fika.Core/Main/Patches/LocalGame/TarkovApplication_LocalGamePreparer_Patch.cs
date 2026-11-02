using JsonType;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.Communications;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using Fika.Core.UI.Models;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.LocalGame;

public sealed class TarkovApplication_LocalGamePreparer_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(TarkovApplication).GetMethod(nameof(TarkovApplication.LocalGameMatching));
    }

    [PatchPrefix]
    public static async void Prefix(TarkovApplication __instance)
    {
        // 保持原生 LocalGameMatching 状态机完整。在 IL2CPP 中，
        // 替换它返回的 Task 并调用复制出来的 trampoline 会改变
        // LoadMapAndData 内部的初始化顺序（地图卸载
        // 控制器不再被走到）。官方 5.0 插件使用的是
        // async-void 形式的准备前置，让原生方法继续执行。
        if (FikaBackendUtils.ClientType == EClientType.None)
            return;

        // 新的突袭启动拥有干净的失败状态：没有这一点，先前某次尝试
        // 记录的原因会不断中止之后每一次启动，卡在 CreateFikaGameCore（“The raid could not be
        // started: <old reason>”），包括主机突袭。本次尝试的失败仍然优先。
        FikaBackendUtils.ClearLeftoverJoinFailure();

        var isServer = FikaBackendUtils.IsServer;
        
        try
        {
            await Prepare(__instance, isServer);
            
        }
        catch (Exception exception)
        {
            // 这个前置是 async void，原生方法体在它等待期间会
            // 继续运行，所以这里吞掉一次失败曾让突袭启动带着一个
            // 从未创建、从未连接的网络管理器继续下去：Fika 加载界面
            // 随后停在 0 %，突袭启动在更后面抛异常。把这
            // 次尝试标记为失败，好让突袭启动能以真实原因中止。
            FikaBackendUtils.MarkJoinFailed("The Fika network preparation failed: " + exception.Message, exception);
            Logger.LogError($"LocalGameMatching preparation failed: {exception}");
            
        }
    }

    private static async Task Prepare(TarkovApplication __instance, bool isServer)
    {
        Logger.LogDebug("TarkovApplication_LocalGamePreparer_Patch:Prefix");
        

        FikaBackendUtils.RequestFikaWorld = true;

        if (!isServer && !string.IsNullOrEmpty(FikaBackendUtils.HostLocationId))
        {
            if (string.Equals(__instance._raidSettings.LocationId, "sandbox", StringComparison.OrdinalIgnoreCase)
                && string.Equals(FikaBackendUtils.HostLocationId, "sandbox_high", StringComparison.OrdinalIgnoreCase))
            {
                __instance._raidSettings.SelectedLocation = __instance.Session.LocationSettings.locations.Values.AsManaged()
                    .FirstOrDefault(IsSandboxHigh);

                FikaNotifications.DisplayMessageNotification("Notification/HighLevelQueue".Localized(null),
                    ENotificationDurationType.Default, ENotificationIconType.Default, null);
            }

            if (string.Equals(__instance._raidSettings.LocationId, "sandbox_high", StringComparison.OrdinalIgnoreCase)
                && string.Equals(FikaBackendUtils.HostLocationId, "sandbox", StringComparison.OrdinalIgnoreCase))
            {
                __instance._raidSettings.SelectedLocation = __instance.Session.LocationSettings.locations.Values.AsManaged()
                    .FirstOrDefault(IsSandbox);
            }
        }

        if (!FikaBackendUtils.IsTransit)
        {
#if DEBUG
            FikaGlobals.LogInfo("Creating net manager");
#endif
            NetManagerUtils.CreateNetManager(isServer);
            if (isServer)
            {
                NetManagerUtils.StartPinger();
            }

            
            await NetManagerUtils.InitNetManager(isServer);
            

            if (isServer)
            {
                var status = new SetStatusModel(FikaBackendUtils.GroupId, LobbyEntry.ELobbyStatus.COMPLETE);
                await FikaRequestHandler.UpdateSetStatus(status);
            }
        }
    }

    private static bool IsSandboxHigh(LocationSettings.Location location)
    {
        return string.Equals(location.Id, "sandbox_high", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSandbox(LocationSettings.Location location)
    {
        return string.Equals(location.Id, "sandbox", StringComparison.OrdinalIgnoreCase);
    }
}
