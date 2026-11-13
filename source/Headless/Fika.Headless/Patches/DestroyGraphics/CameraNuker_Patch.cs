using EFT.CameraControl;
using SPTushonka.Reflection.Patching;
using System.Collections.Generic;
using System.Reflection;

namespace Fika.Headless.Patches.DestroyGraphics;

/// <summary>
/// 本补丁会删除玩家相机上 headless 永远看不到的各种效果。
/// </summary>
public class CameraNuker : ModulePatch
{
    // 允许从相机中删除、且不会导致其抛出异常的白名单类型。
    private static List<string> _deleteTypes = [
        "HBAO",
        "TOD_Scattering",
        "MBOIT_Scattering",
        "Undithering",
        "ContactShadows",
        "NightVision",
        "ThermalVision",
        "Bloom",
        "UltimateBloom",
        "AmbientOcclusion",
        "PrismEffects",
        "BloomAndFlares",
        "DesaturateEffect",
        "Antialiasing",
        "SceneCameraFollow",
        "ChromaticAberration",
        "FastBlur",
        "TextureMask",
        "Tonemapping",
        "RainScreenDrops",
        "ScreenWater",
        "SSAOMask",
        "DepthOfField",
        "InfectionEffect",
        "InventoryBlur",
        "CC_BleachBypass",
        "CC_ContrastVignette",
        "CC_DoubleVision",
        "CC_HueFocus",
        "CC_RadialBlur",
        "CC_Sharpen",
        "CC_Technicolor",
        "CC_FastVignette",
        "CC_Blend",
        "CC_Wiggle",
        "CC_Vintage",
        "CC_BrightnessContrastGamma",
        "PostprocessGrayscale",
        "MotionBlur",
        "HysteresisFilter",
        "DistortCameraFX",
        ];

    protected override MethodBase GetTargetMethod()
    {
        return typeof(CameraManager).GetMethod(nameof(CameraManager.InitCamera));
    }

    [PatchPostfix]
    public static void Postfix(CameraManager __instance)
    {
        var components = __instance.Camera.GetComponents(Il2CppInterop.Runtime.Il2CppType.Of<MonoBehaviour>());

        foreach (var component in components)
        {
            var type = component.GetIl2CppType().Name;

            if (_deleteTypes.Contains(type))
            {
                Object.Destroy(component);
            }
        }
    }
}
