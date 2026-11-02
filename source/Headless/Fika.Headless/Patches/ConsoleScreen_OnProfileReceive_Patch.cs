using EFT;
using EFT.UI;
using SPTushonka.Custom.Utils;
using SPTushonka.Reflection.Patching;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace Fika.Headless.Patches;

public class ConsoleScreen_OnProfileReceive_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ConsoleScreen)
            .GetMethod(nameof(ConsoleScreen.OnProfileReceive));
    }

    /// <summary>
    /// 它是观察者，不是门禁：只报告客户端登录时用的是
    /// 哪个 profile。正常游玩的 profile 会让游戏原样继续运行 —— 没有
    /// 严重错误界面，没有 <c>Application.Quit</c>，也没有图形相关的工作。
    /// </summary>
    [PatchPostfix]
    public static void Prefix(Profile profile)
    {
        var nickname = profile?.Nickname;
        var isHeadlessProfile = nickname?.Contains("headless_", StringComparison.OrdinalIgnoreCase) == true;

        FikaHeadlessPlugin.FikaHeadlessLogger.LogInfo($"[Headless] Profile: received from the login flow " +
            $"(nickname={nickname ?? "<null>"}, isHeadlessProfile={isHeadlessProfile})");

        if (!isHeadlessProfile)
        {
            FikaHeadlessPlugin.Instance?.NoteNormalPlay("ConsoleScreen.OnProfileReceive", nickname);
            return;
        }

        FikaHeadlessPlugin.Instance.MarkProfileVerified("ConsoleScreen.OnProfileReceive", nickname);
    }
}

