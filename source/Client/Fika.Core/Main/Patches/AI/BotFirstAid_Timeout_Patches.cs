using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.AI;

// SPT 5 会在 30 秒后排定 StopUse，但丢弃了任务 ID。
// 完成闭包不会取消它，因此它可能中断之后的治疗，或
// 请求一件已经持有的武器。为每次尝试保留原生超时。
internal static class BotFirstAidTimeouts
{
    private sealed record Binding(BotFirstAid.__c__DisplayClass42_0 Closure,
        IntPtr Aid, AITaskManager Manager, int[] TaskIds);

    // 仅由游戏医疗生命周期在 Unity 线程上访问。
    private static readonly Dictionary<IntPtr, Binding> Bindings = new();

    /// <summary>
    /// 在世界拆解时丢弃所有被跟踪的超时（analysis/transit-crash-unload）。一个绑定持有医疗动作的
    /// 原生闭包及其 AITaskManager；在世界已不存在之后取消它会进入一个已释放的对象，
    /// 因此这里只丢弃引用，而不发起任何原生调用。返回数量。
    /// </summary>
    internal static int ClearForWorldTeardown()
    {
        var count = Bindings.Count;
        if (count > 0)
        {
            Bindings.Clear();
        }

        return count;
    }

    private static AITaskManager Manager(BotFirstAid aid) =>
        aid?._owner?.GetPlayer?.TryCast<FikaBot>() != null ? aid._owner.BotsController?.AiTaskManager : null;

    internal static bool CanStart(BotFirstAid aid)
    {
        var bot = aid?._owner?.GetPlayer?.TryCast<FikaBot>();
        // 上一次治疗会在它回到武器的 Process 之前完成。
        // 在这个窗口内再次开始，可能复用正在退出的药物 controller，
        // 或替换掉排队的武器请求，并在新治疗之上触发一次武器重试。
        // 让 AI 在 hands 切换结束后再重试。
        // 这里不要碰 Using、回调，或当前尝试的看门狗。
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
        // Dispose 可能在所有者已分离其 player/controller 之后运行。
        // 保留的绑定仍然知道这些确切 ID 由哪个调度器持有。
        foreach (var key in Bindings.Where(pair => pair.Value.Aid == aid.Pointer).Select(pair => pair.Key).ToArray())
        {
            var binding = Bindings[key];
            foreach (var id in binding.TaskIds) binding.Manager.CancelDelayedTask(id);
            Bindings.Remove(key);
        }
        var manager = Manager(aid);
        if (manager == null) return;
        // CancelDelayedTask 只标记条目；由原生调度器移除它们。
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
        // 已被取代的尝试的迟到回调，绝不能取消新尝试的
        // 看门狗。Begin/Stop/Dispose 会移除之前的绑定。
        if (!Bindings.Remove(closure.Pointer, out var binding)) return;
        foreach (var id in binding.TaskIds) binding.Manager.CancelDelayedTask(id);
    }

    internal static void AfterWeaponTaken(BotWeaponSelector selector)
    {
        // Using 的值是 (_using && !_lastIsCanceled)，并非完成标志。
        // 被打断的治疗可能不经过 b__1 就回到它的武器。
        // 只有在原生选择器确认已恢复之后，才退掉那个看门狗。
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

        // 参数类型必须与 interop 签名
        // BotFirstAid.ApplyToSelf(Nullable<int>, Il2CppSystem.Action) 完全一致。把回调声明为
        // System.Action 会让 Il2CppInterop 警告 "takes callback as System.Action (not an
        // il2cpp type) ... the patch will read it as the wrong type"，并把一个 il2cpp 委托
        // 对象交给无法容纳它的 CLR 槽位，这会在 bot 的医疗流程内部抛出异常。
        // 该委托会被存储，并原样交还给原生 TryApplyToCurrentPart。
        [PatchPrefix]
        public static bool Prefix(BotFirstAid __instance, Il2CppSystem.Nullable<int> varianAnim, Il2CppSystem.Action callback)
        {
            try
            {
                if (!BotFirstAidPending.Begin(__instance, varianAnim, callback)) return false;
                BotFirstAidTimeouts.CancelOwned(__instance);
                return true;
            }
            catch (Exception exception)
            {
                
                return true;
            }
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
        // 保持原生 Result<IQuickUseItem> 与所有完成逻辑不变。
        [PatchPrefix]
        public static void Prefix(BotFirstAid.__c__DisplayClass42_0 __instance) => BotFirstAidTimeouts.Complete(__instance);
    }

    public sealed class InterruptedWeaponTaken : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => typeof(BotWeaponSelector).GetMethod(nameof(BotWeaponSelector.OnWeaponTaken));
        [PatchPostfix]
        // 不要对原生 Result<IHandsController> 值做封送。
        public static void Postfix(BotWeaponSelector __instance) => BotFirstAidTimeouts.AfterWeaponTaken(__instance);
    }

    // 未完成的打断会保留它的看门狗，直到完成、
    // 被取代、StopUse 或释放。
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
        // 只丢弃尚未开始的治疗；活跃的看门狗
        // 仍会等待确认的武器恢复、完成或释放。
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
