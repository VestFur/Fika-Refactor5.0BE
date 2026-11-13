using System;
using System.Collections.Generic;
using EFT;
using Fika.Core.Main.Players;

namespace Fika.Core.Main.Patches.AI;

// Retain a request made during an outgoing hands Process. Merely declining it
// can strand an AI action that already entered its medical state.
internal static class BotFirstAidPending
{
    private sealed class Request(BotFirstAid aid, FikaBot bot, AITaskManager manager,
        Il2CppSystem.Nullable<int> variant, Il2CppSystem.Action callback)
    {
        internal readonly BotFirstAid Aid = aid;
        internal readonly FikaBot Bot = bot;
        internal readonly AITaskManager Manager = manager;
        internal readonly Il2CppSystem.Nullable<int> Variant = variant;
        internal readonly Il2CppSystem.Action Callback = callback;
        internal int TaskId;
    }

    private static readonly Dictionary<IntPtr, Request> Requests = new();

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
            (Il2CppSystem.Action)(() => Resume(request)));

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
        // Consume before re-entering native code. Its normal eligibility checks,
        // callback and watchdog now belong to the actual new treatment.
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
