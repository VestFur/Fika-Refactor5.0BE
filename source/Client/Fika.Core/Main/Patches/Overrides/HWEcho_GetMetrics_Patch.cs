using System.Reflection;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Overrides;

/// <summary>
/// SPT 在 HWEcho_Json 处禁用了硬件遥测，但本 IL2CPP 构建的
/// GetMetrics 直接内联了 P/Invoke，绕过了那个 detour。
/// 因此在解析 hwecho.dll 之前，先在调用方套用 SPT 的离线行为。
/// </summary>
internal sealed class HWEcho_GetMetrics_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => typeof(HWEcho).GetMethod(nameof(HWEcho.GetMetrics));

    [PatchPrefix]
    private static bool Prefix(ref Il2CppSystem.ValueTuple<string, string> __result)
    {
        __result = new Il2CppSystem.ValueTuple<string, string>(string.Empty, string.Empty);
        return false;
    }
}
