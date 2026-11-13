using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.AI;

// SPT 5 schedules StopUse after 30 seconds but discards the task ID. The
// completion closure does not cancel it, so it can stop a later treatment or
// request an already held weapon. Keep the native timeout for each attempt.
internal static class BotFirstAidTimeouts
{
    private sealed record Binding(BotFirstAid.__c__DisplayClass42_0 Closure,
        IntPtr Aid, AITaskManager Manager, int[] TaskIds);

    // Accessed only by the game's medical lifecycle on the Unity thread.
    private static readonly Dictionary<IntPtr, Binding> Bindings = new();

    private static AITaskManager Manager(BotFirstAid aid) =>
        aid?._owner?.GetPlayer?.TryCast<FikaBot>() != null ? aid._owner.BotsController?.AiTaskManager : null;

    internal static bool CanStart(BotFirstAid aid)
    {
        var bot = aid?._owner?.GetPlayer?.TryCast<FikaBot>();
        // The previous treatment completes before its return-to-weapon Process.
        // Starting again in that window can reuse the outgoing meds controller
        // or replace the queued weapon request and trigger a weapon retry over
        // the new treatment. Let the AI retry after the hands transition ends.
        // Do not touch Using, callbacks or the current attempt's watchdog here.
        return bot == null || bot.ProcessStatus == Player.EProcessStatus.None;
    }

    private static IEnumerable<int> Pending(AITaskManager manager, BotFirstAid aid)
    {
        foreach (var task in manager._simpleTasks)
        {
            var action = task.Task;
            if (!task.IsCancelRequested && action?.Target?.Pointer == aid.Pointer &&
                action.Method.Name == nameof(BotFirstAid.StopUse))
                yield return task.Id;
        }
    }

    internal static void CancelOwned(BotFirstAid aid)
    {
        if (aid == null) return;
        // Dispose can run after the owner has detached its player/controller.
        // Retained bindings still know which scheduler owns these exact IDs.
        foreach (var key in Bindings.Where(pair => pair.Value.Aid == aid.Pointer).Select(pair => pair.Key).ToArray())
        {
            var binding = Bindings[key];
            foreach (var id in binding.TaskIds) binding.Manager.CancelDelayedTask(id);
            Bindings.Remove(key);
        }
        var manager = Manager(aid);
        if (manager == null) return;
        // CancelDelayedTask marks entries; the native scheduler removes them.
        foreach (var id in Pending(manager, aid)) manager.CancelDelayedTask(id);
    }

    internal static void Bind(Player player, Callback<IMedsController> callback)
    {
        var closure = callback?.Target?.TryCast<BotFirstAid.__c__DisplayClass42_0>();
        var aid = closure?.__4__this;
        var manager = Manager(aid);
        if (manager == null || aid._owner.GetPlayer.Pointer != player.Pointer) return;
        Bindings[closure.Pointer] = new Binding(closure, aid.Pointer, manager, Pending(manager, aid).ToArray());
    }

    internal static void Complete(BotFirstAid.__c__DisplayClass42_0 closure)
    {
        // A late callback from a superseded attempt must not cancel the new
        // attempt's watchdog. Begin/Stop/Dispose remove the previous binding.
        if (!Bindings.Remove(closure.Pointer, out var binding)) return;
        foreach (var id in binding.TaskIds) binding.Manager.CancelDelayedTask(id);
    }

    internal static void AfterWeaponTaken(BotWeaponSelector selector)
    {
        // Using is (_using && !_lastIsCanceled), not a completion flag.
        // An interrupted treatment can return to its weapon without b__1.
        // Retire that watchdog only after the native selector confirms recovery.
        var bot = selector?._owner?.GetPlayer?.TryCast<FikaBot>();
        var aid = selector?._owner?.Medecine?.FirstAid;
        var hands = bot?.HandsController?.TryCast<Player.FirearmController>();
        if (aid?._lastIsCanceled == true && !selector.IsChanging && selector.IsWeaponReady &&
            hands != null && !hands.Destroyed) CancelOwned(aid);
    }
}

public static class BotFirstAid_Timeout_Patches
{
    public sealed class Begin : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => typeof(BotFirstAid).GetMethod(nameof(BotFirstAid.ApplyToSelf));
        [PatchPrefix]
        public static bool Prefix(BotFirstAid __instance, Il2CppSystem.Nullable<int> varianAnim, Il2CppSystem.Action callback)
        {
            if (!BotFirstAidPending.Begin(__instance, varianAnim, callback)) return false;
            BotFirstAidTimeouts.CancelOwned(__instance);
            return true;
        }
    }

    public sealed class Bind : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => typeof(Player).GetMethod(nameof(Player.SetInHands),
            new[] { typeof(Meds), typeof(EBodyPart), typeof(int), typeof(Callback<IMedsController>) });
        [PatchPrefix]
        public static void Prefix(Player __instance, Callback<IMedsController> callback) => BotFirstAidTimeouts.Bind(__instance, callback);
    }

    public sealed class Complete : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => typeof(BotFirstAid.__c__DisplayClass42_0).GetMethod("_ApplyToSelf_b__1");
        // Leave the native Result<IQuickUseItem> and all completion logic intact.
        [PatchPrefix]
        public static void Prefix(BotFirstAid.__c__DisplayClass42_0 __instance) => BotFirstAidTimeouts.Complete(__instance);
    }

    public sealed class InterruptedWeaponTaken : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => typeof(BotWeaponSelector).GetMethod(nameof(BotWeaponSelector.OnWeaponTaken));
        [PatchPostfix]
        // Do not marshal the native Result<IHandsController> value.
        public static void Postfix(BotWeaponSelector __instance) => BotFirstAidTimeouts.AfterWeaponTaken(__instance);
    }

    // An unfinished interruption retains its watchdog until completion,
    // supersession, StopUse or disposal.
    public sealed class Stop : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => typeof(BotFirstAid).GetMethod(nameof(BotFirstAid.StopUse));
        [PatchPrefix]
        public static void Prefix(BotFirstAid __instance)
        {
            BotFirstAidPending.Cancel(__instance);
            BotFirstAidTimeouts.CancelOwned(__instance);
        }
    }

    public sealed class CancelPending : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => typeof(BotAbstractMeds).GetMethod(nameof(BotAbstractMeds.CancelCurrent));
        [PatchPrefix]
        // Only discard a treatment that has not started; the active watchdog
        // still waits for confirmed weapon recovery, completion or disposal.
        public static void Prefix(BotAbstractMeds __instance)
        {
            BotFirstAidPending.Cancel(__instance.TryCast<BotFirstAid>());
            BotSurgeryPending.Cancel(__instance.TryCast<BotSurgicalKit>());
        }
    }

    public sealed class Dispose : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => typeof(BotAbstractMeds).GetMethod(nameof(BotAbstractMeds.Dispose));
        [PatchPrefix]
        public static void Prefix(BotAbstractMeds __instance)
        {
            var aid = __instance.TryCast<BotFirstAid>();
            BotFirstAidPending.Cancel(aid);
            BotFirstAidTimeouts.CancelOwned(aid);
            BotSurgeryPending.Cancel(__instance.TryCast<BotSurgicalKit>());
        }
    }
}
