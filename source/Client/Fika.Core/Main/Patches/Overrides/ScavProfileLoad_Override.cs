using System.Reflection;
using EFT;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Overrides;

internal class ScavProfileLoad_Override : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(TarkovApplication).GetMethod(nameof(TarkovApplication.OnGameEnd));
    }

    [PatchPrefix]
    private static void PatchPrefix(ref string profileId, Profile savageProfile, EFT.TarkovApplication __instance)
    {
        // SPT 可能在次要 Scav 档案创建之前，就从被取消/失败的战局
        // 进入 OnGameEnd。在该路径上保留原生 PMC id，
        // 而不是去解引用一个缺失的档案并中止
        // 结果/存档协程。GameWorld.Dispose 也可能在该回调之前
        // 重置 ClientType，因此把这个重写限定在
        // 本次 Fika 战局捕获的档案上，而不是网络状态上。
        var descriptor = RaidEndProfile.ProfileDescriptor;
        if (__instance?._raidSettings != null &&
            !__instance._raidSettings.IsPmc &&
            savageProfile != null &&
            !string.IsNullOrEmpty(savageProfile.Id) &&
            descriptor != null &&
            string.Equals(descriptor.Id.ToString(), savageProfile.Id, System.StringComparison.Ordinal))
        {
            profileId = savageProfile.Id;
        }
    }
}
