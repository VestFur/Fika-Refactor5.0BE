using System;
using System.Reflection;
using EFT.UI;
using SPTushonka.Reflection.Patching;
using UnityEngine.UI;

namespace Fika.Core.Main.FreeCamera.Patches;

[IgnoreAutoPatch]
internal class FadeBlackScreen_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(PreloaderUI)
            .GetMethod(nameof(PreloaderUI.FadeBlackScreen));
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }
}

[IgnoreAutoPatch]
internal class StartBlackScreenShow_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(PreloaderUI).GetMethod(nameof(PreloaderUI.StartBlackScreenShow));
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }

    [PatchPostfix]
    // PreloaderUI.StartBlackScreenShow(Single, Single, Action, Boolean) 声明的是 il2cpp
    // 委托，因此 postfix 的参数也必须如此。这个补丁带 [IgnoreAutoPatch]，目前
    // 并未启用，但那个 CLR 类型会是同一颗“按错误类型读取”的地雷，
    // 一旦启用就会踩到。
    public static void Postfix(Il2CppSystem.Action callback)
    {
        callback?.Invoke();
    }
}

[IgnoreAutoPatch]
internal class SetBlackImageAlpha_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(PreloaderUI).GetMethod(nameof(PreloaderUI.SetBlackImageAlpha));
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }

    [PatchPostfix]
    public static void Postfix(float alpha, EFT.UI.PreloaderUI __instance)
    {
        __instance._overlapBlackImage.gameObject.SetActive(value: true);
        __instance._overlapBlackImage.color = new Color(0f, 0f, 0f, 0f);
    }
}
