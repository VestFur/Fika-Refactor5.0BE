using System;
using EFT;
using EFT.InventoryLogic.Operations;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 对只改动触发它的玩家任务数据的那些操作做分类与诊断：
/// 读取一个笔记、某一章的条件、单个条件或一个笔记链接，
/// 以及研究一盘磁带。
/// <para>
/// 任务状态的权威在拥有该档案的机器上。客户端运行自己的任务 controller，
/// raid 结束时由 <c>LocalRaidEnded</c> 上传的正是它自己的档案，
/// 而主机只保留一份对端任务记录的显示副本
/// （<c>ObservedQuestController</c>，由 <c>QuestSyncPacket</c> 喂入）。
/// 这些操作不移动物品、不发放奖励 —— 它们是纯粹的档案记账 ——
/// 因此必须在本地 controller 上运行，绝不能转发：
/// 转发会把读取延迟到主机确认之后，并把
/// 任何拒绝变成任务界面中途的一次库存重同步。
/// </para>
/// <para>
/// 它们不是 <c>BaseQuestOperation</c> 的子类，这也是本移植过去会把它们
/// 派发出去的原因，尽管它已经把其他每一个任务事务
/// 都在本地执行了。那次派发也正是真实 raid 中出现
/// "Serializer delegate missing for registered type" 失败的原因，
/// 涉及 <c>ReadChapterConditionsOperationDescriptor</c> 与
/// <c>ReadQuestNoteOperationDescriptor</c> 两类操作。
/// </para>
/// <para>
/// 每个访问器都被防护：这个分类结果会驱动执行，因此一个
/// 会抛出的诊断绝不能决定它。
/// </para>
/// </summary>
internal static class QuestOperations
{
    /// <summary>
    /// 对于只改动本地玩家任务记账的操作返回 true。
    /// <paramref name="detail"/> 为 <c>[QuestOp]</c> 日志描述目标。
    /// </summary>
    internal static bool IsLocalOnly(AbstractOperation operation, out string detail)
    {
        detail = null;
        if (operation == null)
        {
            return false;
        }

        if (operation.TryCast<ReadQuestNoteOperation>() is { } readNote)
        {
            detail = $"note {Safe(() => readNote._note?.Id?.ToString())}";
            return true;
        }

        if (operation.TryCast<ReadChapterConditionsOperation>() is { } readChapter)
        {
            detail = $"chapter {Safe(() => readChapter._chapter?.Template?.Id)} " +
                $"({Safe(() => readChapter._type.ToString())})";
            return true;
        }

        if (operation.TryCast<ReadQuestConditionOperation>() is { } readCondition)
        {
            detail = $"condition {Safe(() => readCondition._condition?.id?.ToString())}";
            return true;
        }

        if (operation.TryCast<ReadQuestLinkOperation>() is { } readLink)
        {
            detail = $"note link {Safe(() => readLink._link?.Id?.ToString())}";
            return true;
        }

        // StudyItemPlayerOperation 派生自 StudyItemBaseOperation，因此
        // 必须先检测它。
        if (operation.TryCast<StudyItemPlayerOperation>() is { } studyForPlayer)
        {
            detail = $"tape {Safe(() => studyForPlayer.Item?.Id)} (recorder)";
            return true;
        }

        if (operation.TryCast<StudyItemOperation>() is { } study)
        {
            detail = $"tape {Safe(() => study.Item?.Id)}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// 为 <c>[QuestOp]</c> 日志行描述一个任务族操作。
    /// 对每个不相关的操作返回 false。
    /// </summary>
    internal static bool TryDescribe(AbstractOperation operation, out string detail)
    {
        detail = null;
        if (operation == null)
        {
            return false;
        }

        if (operation.TryCast<BaseQuestOperation>() is { } questOperation)
        {
            detail = $"quest {Safe(() => questOperation.Quest?.Template?.Id)}";
            return true;
        }

        return IsLocalOnly(operation, out detail);
    }

    /// <summary>
    /// 为 <c>[QuestOp]</c> 日志行描述一个任务族操作
    /// 描述符。对每个不相关的描述符返回 false。
    /// </summary>
    internal static bool TryDescribe(InventoryOperationDescriptor descriptor, out string detail)
    {
        detail = null;
        if (descriptor == null)
        {
            return false;
        }

        // 玩家变体派生自普通的 study 描述符。
        if (descriptor.TryCast<StudyItemPlayerOperationDescriptor>() is { } studyForPlayer)
        {
            detail = $"tape {studyForPlayer.ItemId} (recorder)";
            return true;
        }

        if (descriptor.TryCast<StudyItemOperationDescriptor>() is { } study)
        {
            detail = $"tape {study.ItemId}";
            return true;
        }

        if (descriptor.TryCast<ReadQuestNoteOperationDescriptor>() is { } note)
        {
            detail = $"note {note.NoteId}";
            return true;
        }

        if (descriptor.TryCast<ReadChapterConditionsOperationDescriptor>() is { } chapter)
        {
            detail = $"chapter {chapter.ChapterId} ({chapter.Type})";
            return true;
        }

        if (descriptor.TryCast<ReadQuestConditionOperationDescriptor>() is { } condition)
        {
            detail = $"condition {condition.ConditionId}";
            return true;
        }

        if (descriptor.TryCast<ReadQuestLinkOperationDescriptor>() is { } link)
        {
            detail = $"note link {link.LinkId}";
            return true;
        }

        return false;
    }

    private static string Safe(Func<string> read)
    {
        try
        {
            return read() ?? "unknown";
        }
        catch (Exception exception)
        {
            return $"unknown ({exception.GetType().Name})";
        }
    }
}
