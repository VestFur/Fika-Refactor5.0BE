using System.Reflection;
using EFT;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.PlayerPatches;

/// <summary>
/// 本补丁会阻止 BSG 的狗牌处理，因为它的实现很糟糕
/// </summary>
public class Player_SetDogtagInfo_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player).GetMethod(nameof(Player.SetDogtagInfo));
    }

    // 保留 Fika 复制的狗牌细节，而不是用
    // 原版死亡数据包替换它们（原来的 Mono 补丁是直接返回的）。
    [PatchPrefix]
    public static bool Prefix() => FikaBackendUtils.ClientType == EClientType.None;
}
