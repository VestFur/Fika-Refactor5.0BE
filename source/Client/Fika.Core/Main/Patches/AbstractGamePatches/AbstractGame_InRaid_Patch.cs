using System.Reflection;
using EFT;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.AbstractGamePatches;

/// <summary>
/// 用于支持依赖 <see cref="AbstractGame.InRaid"/> 属性的 mod，该属性通常会强制转换为 <see cref="LocalGame"/>
/// </summary>
internal class AbstractGame_InRaid_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(AbstractGame).GetProperty(nameof(AbstractGame.InRaid)).GetGetMethod();
    }

    [PatchPrefix]
    public static bool Prefix(AbstractGame __instance, ref bool __result)
    {
        if (FikaBackendUtils.ClientType == EClientType.None) return true;
        // HeadlessGame 同样是一个 AbstractGame，但它仍然代表着
        // 一场正在进行的 Fika 战局。在这里返回 false 会让原生调用方跳过
        // 专用主机上仅属于战局的清理与重连逻辑。
        __result = __instance is CoopGame || FikaBackendUtils.IsHeadless || FikaBackendUtils.IsHeadlessGame;
        return false;
    }
}
