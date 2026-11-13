using System.Reflection;
using EFT.Quests;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Quest;

/// <summary>
/// 原生的战局结束任务结算必须对每个任务都运行，而不只是
/// 失败的那些。玩家 scav 加入时带着的任务，其模板被 SPT 保存在
/// 另一侧的数据里，原生流程会对它们报怨，所以移植版
/// 过去曾用 <see cref="RaidQuestStatuses.Apply"/> 替换整个流程。
/// 那会丢掉每一次完成转换：像 "exit
/// &lt;location&gt;"（带 ExitStatus + Location 子节点的 CounterCreator）这样的条件再也
/// 不会被统计，这恰恰是只有部分目标看起来有进展的原因。
/// 移植版现在先运行自己的失败转换，然后让原生
/// 结算完成它的工作。
/// </summary>
public sealed class FikaRaidQuestStatusesPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(
        typeof(QuestControllerClientLocalGame), nameof(QuestControllerClientLocalGame.ManageQuestStatusesForPveOfflineGameEnd));

    [PatchPrefix]
    private static bool Prefix(QuestControllerClientLocalGame __instance)
    {
        if (FikaQuestController.Find<ClientQuestController>(__instance) == null) return true;
        RaidQuestStatuses.Apply(__instance.Profile.QuestsData, __instance.Profile.Side,
            id => FindByTemplate(__instance.Quests, id), message => UnityEngine.Debug.LogError(message));

        // 让原生结算运行：正是它把已完成的条件
        // （包括战局结束的 ExitStatus/Location 计数器）变成任务
        // 状态转换。替换它会静默丢掉那些完成。
        return true;
    }

    private static EFT.Quests.Quest FindByTemplate(QuestBook quests, string id)
    {
        foreach (var quest in quests)
            if (quest.Template.Id == id) return quest;
        return null;
    }
}
