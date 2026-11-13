using System;
using System.Collections.Generic;
using EFT;
using Fika.Core.Main.Players;

namespace Fika.Core.Main.Patches.AI;

// 保留在 outgoing hands Process 期间发起的请求。单纯拒绝它
// 会让已经进入医疗状态的 AI 动作无以为继。
internal static class BotFirstAidPending
{
    private sealed class Request(BotFirstAid aid, FikaBot bot, AITaskManager manager,
        Il2CppSystem.Nullable<int> variant, Il2CppSystem.Action callback)
    {
        internal readonly BotFirstAid Aid = aid;
        internal readonly FikaBot Bot = bot;
        internal readonly AITaskManager Manager = manager;
        internal readonly Il2CppSystem.Nullable<int> Variant = variant;
        // 保持为 interop 自身的委托类型：请求恢复时会原样交回原生
        // TryApplyToCurrentPart(Nullable<int>, Il2CppSystem.Action) 重载，
        // 因此永远不会为它构建任何 CLR trampoline。
        internal readonly Il2CppSystem.Action Callback = callback;
        internal int TaskId;
    }

    private static readonly Dictionary<IntPtr, Request> Requests = new();

    /// <summary>
    /// 在世界销毁（analysis/transit-crash-unload）时丢弃所有待处理请求。一个请求持有
    /// 原生 bot、它的 AITaskManager 以及它被拒绝的那次医疗动作的原生回调；这三者
    /// 都属于刚刚结束的那个世界，因此事后恢复其中任何一个都会进入已释放的 bot。
    /// 返回被丢弃的请求数量。
    /// </summary>
    internal static int ClearForWorldTeardown()
    {
        var count = Requests.Count;
        if (count > 0)
        {
            Requests.Clear();
        }

        return count;
    }

    internal static bool Begin(BotFirstAid aid, Il2CppSystem.Nullable<int> variant, Il2CppSystem.Action callback)
    {
        if (BotFirstAidTimeouts.CanStart(aid))
        {
            Cancel(aid);
            return true;
        }
        var bot = aid._owner.GetPlayer.Cast<FikaBot>();
        var manager = aid._owner.BotsController.AiTaskManager;
        Cancel(aid);
        var request = new Request(aid, bot, manager, variant, callback);
        Requests.Add(aid.Pointer, request);
        Schedule(request);
        return false;
    }

    private static void Schedule(Request request) =>
        request.TaskId = request.Manager.RegisterDelayedTask(request.Aid._owner, 0.1f,
            (System.Action)(() => Resume(request)));

    private static void Resume(Request request)
    {
        if (!Requests.TryGetValue(request.Aid.Pointer, out var current) || !ReferenceEquals(current, request)) return;
        var aid = request.Aid;
        var bot = request.Bot;
        if (aid._owner?.GetPlayer?.Pointer != bot.Pointer || !bot.HealthController.IsAlive || aid.Using)
        {
            Cancel(aid);
            return;
        }
        if (bot.ProcessStatus != Player.EProcessStatus.None || !aid.CanUseByTime())
        {
            Schedule(request);
            return;
        }
        // 在重新进入原生代码之前先消费掉。它的常规可用性检查、
        // 回调与 watchdog 现在都属于真正的新治疗。
        Requests.Remove(aid.Pointer);
        aid.Refresh();
        aid.FindDamagedPart();
        aid.TryApplyToCurrentPart(request.Variant, request.Callback);
    }

    internal static void Cancel(BotFirstAid aid)
    {
        if (aid != null && Requests.Remove(aid.Pointer, out var request))
            request.Manager.CancelDelayedTask(request.TaskId);
    }
}
