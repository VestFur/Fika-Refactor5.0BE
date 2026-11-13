using System;
using EFT;
using EFT.Quests;

namespace Fika.Core.Main.Utils;

internal static class RaidQuestStatuses
{
    internal static void Apply(Il2CppSystem.Collections.Generic.List<QuestStatusData> statuses,
        EPlayerSide side, Func<string, Quest> find, Action<string> missing)
    {
        foreach (var status in statuses)
        {
            var template = status.Template;
            if (template == null) continue;
            var quest = find(template.Id);
            if (quest == null)
            {
                // SPT 把 Scav 的可重复任务存储共享的 PMC 任务数据里，而
                // 战局任务簿会有意排除对立阵营的那部分。
                var otherSide = (side is EPlayerSide.Bear or EPlayerSide.Usec && template.PlayerGroup == EPlayerGroup.Scav) ||
                    (side == EPlayerSide.Savage && template.PlayerGroup == EPlayerGroup.Pmc);
                if (!otherSide) missing($"Quest with id {template.Id} wasn't found! Side = {template.PlayerGroup}");
                continue;
            }
            // 保留原生的战局结束失败转换。不要在枚举过程中移除
            // 共享状态或修改这个正在使用的集合。
            if (quest.StatusHasBeenChangedInPveOffline && quest.QuestStatus is EQuestStatus.Fail or EQuestStatus.FailRestartable)
                status.Status = EQuestStatus.MarkedAsFailed;
        }
    }
}
