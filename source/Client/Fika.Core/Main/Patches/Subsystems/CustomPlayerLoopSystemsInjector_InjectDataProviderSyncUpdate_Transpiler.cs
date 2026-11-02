using System.Reflection;
using CustomPlayerLoopSystem;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Subsystems;

/// <summary>
/// 阻止把从未使用的逻辑注入到 <see cref="UnityEngine.LowLevel.PlayerLoop"/> 中
/// </summary>
internal class CustomPlayerLoopSystemsInjector_InjectDataProviderSyncUpdate_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(CustomPlayerLoopSystemsInjector).GetMethod(nameof(CustomPlayerLoopSystemsInjector.InjectDataProviderSyncUpdate));
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }
}
