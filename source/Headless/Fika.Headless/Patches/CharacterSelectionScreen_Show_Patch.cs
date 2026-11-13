using EFT;
using EFT.UI;
using SPTushonka.Reflection.Patching;
using System;
using System.Reflection;

namespace Fika.Headless.Patches;

public class CharacterSelectionScreen_Show_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(CharacterSelectionScreen).GetMethod(nameof(CharacterSelectionScreen.Show),
            [typeof(CharacterSelectionScreen.CharacterSelectionScreenController)]);
    }

    [PatchPostfix]
    public static void Postfix(CharacterSelectionScreen.CharacterSelectionScreenController controller)
    {
        if (controller == null)
        {
            Logger.LogError("[Headless] Profile: character selection controller was null");
            return;
        }

        if (!controller.Data.TryGetValue(EGameMode.Pve, out var profileData))
        {
            Logger.LogError("[Headless] Profile: no PVE profile to select");
            return;
        }

        var nickname = profileData?.Nickname;
        var isHeadlessProfile = nickname?.Contains("headless_", StringComparison.OrdinalIgnoreCase) == true;

        Logger.LogInfo($"[Headless] Profile: character selection (id={profileData?.ProfileId}, nickname={nickname ?? "<null>"}, " +
            $"hasProfile={profileData?.HasProfile}, selectable={profileData?.IsSelectable}, locked={profileData?.IsLocked}, isHeadlessProfile={isHeadlessProfile})");

        // 正常游玩：绝不提交、绝不选择、绝不触碰界面。
        // 玩家挑选档案的过程与本插件未被加载时完全一致。
        if (!isHeadlessProfile)
        {
            FikaHeadlessPlugin.Instance?.NoteNormalPlay("CharacterSelectionScreen.Show", nickname);
            return;
        }

        // 在某些 SPT 5 IL2CPP 启动路径上 ConsoleScreen.OnProfileReceive
        // 从不会被派发，尽管角色选择数据已经
        // 填充好了。生成的无头档案在这里是可以识别的，
        // 所以就用这份权威数据完成就绪握手。
        FikaHeadlessPlugin.Instance.MarkProfileVerified("CharacterSelectionScreen.Show", nickname);

        // Harmony 在 CharacterSelectionScreen.Show 返回之后
        // 才会调用 postfix。因此在这里调用 Submit 已经处于 Show 的
        // 原生调用帧之外。把这件事经由 Fika 的托管派发器排队
        // 在批处理模式下可能卡住——当菜单生命周期在第一帧之前
        // 就停止 tick 时，无头客户端会永久停在档案
        // 选择界面。直接 Submit，并上报任何原生异常。
        try
        {
            controller.Submit(EGameMode.Pve, profileData);
            Logger.LogInfo("[Headless] Profile: PVE profile submit invoked");
        }
        catch (Exception exception)
        {
            Logger.LogError($"[Headless] Profile: PVE profile submit failed: {exception}");
        }
    }
}

