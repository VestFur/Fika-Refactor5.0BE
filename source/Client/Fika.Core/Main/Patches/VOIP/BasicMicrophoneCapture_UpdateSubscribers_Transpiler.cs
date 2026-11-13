using System;
using System.Reflection;
using Dissonance.Audio.Capture;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.VOIP;

/// <summary>抑制重复的空音频片段警告，同时保留原生的采集/重启行为。</summary>
public class BasicMicrophoneCapture_UpdateSubscribers_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Dissonance.Log),
        nameof(Dissonance.Log.WriteLog), new[] { typeof(Dissonance.LogLevel), typeof(string) });

    [PatchPrefix]
    public static bool Prefix(Dissonance.Log __instance, string message)
    {
        // 保留其他所有录制错误以及原生的采样处理路径。
        return __instance.Pointer != BasicMicrophoneCapture.Log.Pointer ||
            !string.Equals(message, "Unknown microphone capture error (zero length clip) - restarting mic", StringComparison.Ordinal);
    }
}
