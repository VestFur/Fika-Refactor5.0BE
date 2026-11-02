using System;
using EFT;
using EFT.CameraControl;
using UnityEngine;

namespace Fika.Core.Main.Utils;

/// <summary>
/// (2026-10-18 refactor W1b / tracker A11) 夜视景深是由 NVG 效果
/// 自身施加的，而该组件没有 OnDisable：当效果在没有经过
/// NightVision.Switch 的情况下被关闭时（自由相机直接写 NightVision.On），没有任何东西移除模糊，
/// 相机就会在整局突袭剩下的时间里保留“雾蒙蒙、看起来像景深”的画面。这是
/// 这个类仅剩的职责——过去与头戴设备对抗的逐 tick 自愈已经移除了。
/// </summary>
internal static class NvgDiagnostics
{
    private static float _nextTick;

    internal static void Tick()
    {
        // (2026-10-18 refactor W2a) 由逐帧的插件更新调用；检查本身只是两次读取，
        // 其余部分最多每秒运行两次——协调只需比玩家察觉
        // 卡住的模糊更快，而不必每帧运行。
        var now = Time.unscaledTime;
        if (now < _nextTick)
        {
            return;
        }

        _nextTick = now + 0.5f;

        try
        {
            if (!CameraManager.Exist)
            {
                return;
            }

            var cameraManager = CameraManager.Instance;
            if (cameraManager == null)
            {
                return;
            }

            var zBlur = cameraManager.NightVisionZBlur;
            if (zBlur == null || !zBlur.IsApplied)
            {
                return;
            }

            var nightVision = cameraManager.NightVision;
            if (nightVision == null || nightVision.On || nightVision.InProcessSwitching)
            {
                return;
            }

            zBlur.Apply(false);
        }
        catch (Exception)
        {
        }
    }
}
