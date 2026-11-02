using System.Reflection;
using EFT.Quests;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.ObservedClasses;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Quest;

/// <summary>
/// 被观察的玩家不得针对本地的
/// profile 完成任务条件。他们的控制器只用于显示，任务状态是通过
/// Fika 数据包接收的。
/// </summary>
public sealed class QuestControllerClientLocalGame_CompleteConditionExternal_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(QuestControllerClientLocalGame).GetMethod(
            "EFT.Quests.IActiveQuestController.CompleteConditionExternal",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

    [PatchPrefix]
    private static bool Prefix(QuestControllerClientLocalGame __instance) =>
        FikaQuestController.Find<ObservedQuestController>(__instance) == null;
}
