using EFT;
using System.Reflection;
using System.Threading.Tasks;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches;

internal class DataPrepareOperation_ReloadBackendLocale_Patch : ModulePatch
{
    private static bool _hasBeenSet = false;

    protected override MethodBase GetTargetMethod()
    {
        // SPT 5 的启动预取会直接调用共享加载器，绕过
        // ClientBackendSession.LoadLocale。改为观察实际的语言包任务。
        return typeof(LocalizationLoader)
            .GetMethod(nameof(LocalizationLoader.LoadLocale));
    }

    [PatchPostfix]
    public static void Postfix(Il2CppSystem.Threading.Tasks.Task __result)
    {
        if (!_hasBeenSet)
        {
            FikaPlugin.Instance.WaitForLocales(__result);
            _hasBeenSet = true;
        }
    }
}
