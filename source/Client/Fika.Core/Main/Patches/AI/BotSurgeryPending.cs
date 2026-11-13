using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.AI;

// 手术可能就在急救的完成回调中被请求，此时其返回武器的 Process
// 还没有结束。保留该请求，而不要替换那个武器
// Process，以免它延迟的重试打断新的治疗。
internal static class BotSurgeryPending
{
    private sealed class Request(BotSurgicalKit surgery, FikaBot bot, AITaskManager manager,
        Il2CppSystem.Action callback)
    {
        internal readonly BotSurgicalKit Surgery = surgery;
        internal readonly FikaBot Bot = bot;
        internal readonly AITaskManager Manager = manager;
        // interop 自己的委托类型：它被原样传回原生的
        // ApplyToCurrentPart(Il2CppSystem.Action) 重载。
        internal readonly Il2CppSystem.Action Callback = callback;
        internal int TaskId;
    }

    private static readonly Dictionary<IntPtr, Request> Requests = new();

    /// <summary>
    /// 在世界拆除时丢弃所有待处理的手术请求（analysis/transit-crash-unload）：每一个
    /// 都持有原生 bot，以及已结束战局中被拒绝动作的原生回调。
    /// 返回丢弃的数量。
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

    internal static bool Begin(BotSurgicalKit surgery, Il2CppSystem.Action callback)
    {
        var bot = surgery?._owner?.GetPlayer?.TryCast<FikaBot>();
        if (bot == null || bot.ProcessStatus == Player.EProcessStatus.None)
        {
            Cancel(surgery);
            return true;
        }
        Cancel(surgery);
        var request = new Request(surgery, bot, surgery._owner.BotsController.AiTaskManager, callback);
        Requests.Add(surgery.Pointer, request);
        Schedule(request);
        return false;
    }

    private static void Schedule(Request request) =>
        request.TaskId = request.Manager.RegisterDelayedTask(request.Surgery._owner, 0.1f,
            (System.Action)(() => Resume(request)));

    private static void Resume(Request request)
    {
        var surgery = request.Surgery;
        if (!Requests.TryGetValue(surgery.Pointer, out var current) || !ReferenceEquals(current, request)) return;
        var bot = request.Bot;
        if (surgery._owner?.GetPlayer?.Pointer != bot.Pointer || !bot.HealthController.IsAlive || surgery.Using)
        {
            Cancel(surgery);
            return;
        }
        if (bot.ProcessStatus != Player.EProcessStatus.None || surgery._owner.Medecine.Using ||
            surgery._nextPosibleUseTime > UnityEngine.Time.time)
        {
            Schedule(request);
            return;
        }
        // 在过渡之后重新评估真实库存、受伤肢体和原生
        // CanApplyItem。保留原有的完成回调；资格变化时
        // 不做 Using／生命值编辑，也不做合成完成。
        Requests.Remove(surgery.Pointer);
        surgery.Refresh();
        surgery.FindDamagedPart();
        surgery.ApplyToCurrentPart(request.Callback);
    }

    internal static void Cancel(BotSurgicalKit surgery)
    {
        if (surgery != null && Requests.Remove(surgery.Pointer, out var request))
            request.Manager.CancelDelayedTask(request.TaskId);
    }
}

public sealed class BotSurgicalKit_ApplyToCurrentPart_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(BotSurgicalKit).GetMethod(nameof(BotSurgicalKit.ApplyToCurrentPart));

    // 与 interop 签名 BotSurgicalKit.ApplyToCurrentPart(Il2CppSystem.Action) 保持一致。用
    // System.Action 参数会产生 Il2CppInterop 警告 "takes callbackEndUse as
    // System.Action (not an il2cpp type) ... the patch will read it as the wrong type"。
    [PatchPrefix]
    public static bool Prefix(BotSurgicalKit __instance, Il2CppSystem.Action callbackEndUse)
    {
        try
        {
            return BotSurgeryPending.Begin(__instance, callbackEndUse);
        }
        catch (Exception exception)
        {
            
            return true;
        }
    }
}
