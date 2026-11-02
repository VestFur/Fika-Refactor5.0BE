using System.Reflection;
using EFT.Vehicle;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.BTR;

/// <summary>在原生武器对齐之前，先移动 Fika 基于 LocalPlayer 的远程射手。</summary>
public class BTRTurretView_AttachBot_Postfix : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BTRTurretView), nameof(BTRTurretView.AttachBot));

    [PatchPrefix]
    public static void Prefix(BTRTurretView __instance, int btrBotId)
    {
        if (!FikaBackendUtils.IsClient) return;
        var bot = __instance._offlineBot;
        if (!bot.Item2)
        {
            if (!__instance.TryBotInitialization(btrBotId)) return;
            bot = __instance._offlineBot;
        }
        if (bot.Item1 == null) return;
        // 原生 AttachBot 只在其在线游戏模式下执行这一步。Fika 的
        // observed player 继承自 LocalPlayer，因此会走离线分支。
        bot.Item1.PlayerBones.BodyTransform.Original.position = __instance.BotRoot.position;
    }
}
