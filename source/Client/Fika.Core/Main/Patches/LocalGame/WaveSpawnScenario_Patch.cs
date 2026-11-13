using System.Reflection;
using EFT;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.LocalGame;

internal class WaveSpawnScenario_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => typeof(WavesSpawnScenario).GetMethod(nameof(WavesSpawnScenario.Run));

    [PatchPrefix]
    public static bool PatchPrefix(WavesSpawnScenario __instance)
    {
        // 单人 SPT 教程没有 Fika 大厅，也没有网络管理器。
        if (FikaBackendUtils.ClientType == EClientType.None) return true;
        var result = FikaBackendUtils.IsServer;
        typeof(WavesSpawnScenario).GetProperty(nameof(WavesSpawnScenario.Enabled)).SetValue(__instance, result);
        return result;
    }
}
