using System.Reflection;
using EFT.CameraControl;
using FikaFreeCamera = Fika.Core.Main.FreeCamera.FreeCamera;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Camera;

// 对正常的第一人称玩法，保留 SPT 5 的原生摄像机 LateUpdate。
// Fika 旧的 patch 用 ManualLateUpdate 替换了整个方法；在 SPT 5 上
// 这会让 FPS 摄像机停留在部署位置。仅在显式的 Fika 自由摄像机
// 处于激活状态时抑制原生更新。
public class PlayerCameraController_LateUpdate_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(PlayerCameraController), nameof(PlayerCameraController.LateUpdate));

    [PatchPrefix]
    public static bool Prefix(PlayerCameraController __instance)
    {
        var camera = CameraManager.Instance?.Camera;
        var freeCamera = camera?.GetComponent<FikaFreeCamera>();
        if (freeCamera != null && freeCamera.IsActive)
        {
            __instance._currentCameraState?.ManualLateUpdate(Time.deltaTime);
            return false;
        }

        // 让 IL2CPP 原生实现每次 LateUpdate 更新 FPS 摄像机
        // 并将其绑定到本地玩家的头部/武器上。
        return true;
    }
}
