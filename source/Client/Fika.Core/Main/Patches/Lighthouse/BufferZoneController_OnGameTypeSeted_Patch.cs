using EFT.BufferZone;
using System;
using System.Reflection;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Lighthouse;

public class BufferZoneController_OnGameTypeSeted_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BufferZoneController)
            .GetMethod(nameof(BufferZoneController.OnGameTypeSeted));
    }

    [PatchPrefix]
    public static void Prefix(ref EGameType gameType)
    {
        if (FikaBackendUtils.IsClient)
        {
            // 让原生初始化器负责自身的订阅与清理，
            // 客户端则以在线对端的身份使用主机的缓冲区状态。
            gameType = EGameType.Online;
        }
    }
}
