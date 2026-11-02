using EFT.Rendering.Clouds;
using SPTushonka.Reflection.Patching;
using System.Reflection;

namespace Fika.Headless.Patches.DestroyGraphics;

// 保留 CloudController：即使在 Headless 主机不渲染天空的情况下，
// WeatherController.ControlClouds 在室外地图上仍会使用它。
public class CloudController_UpdateAmbient_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(CloudController).GetMethod(nameof(CloudController.UpdateAmbient));
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }
}
