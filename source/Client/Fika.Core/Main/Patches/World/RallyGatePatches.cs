using System.Reflection;
using CommonAssets.Scripts.Cutscenes;
using EFT.GameTriggers;
using EFT.GlobalEvents;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.World;

// ICEBREAKER-RALLY-GATE (analysis/rally-gate-deadlock).
//
// 本文件中的每个补丁都属于 FikaRallyGate：原生
// EFT.GameTriggers.TriggerRallyZone 的两个 rally 计数器（门等待的是哪个数字，以及区域在
// 等待期间报告什么）、把该数字传给客户端的广播，以及观看者发出的三个跳过信号。
// 这里的任何东西都不改变原生调用、其参数或其顺序：一个计数会被替换为
// 本次 raid 真实拥有的玩家数，而其他一切都只是写一行 [RallyDiag]。当
// FikaRallyGate.UseFikaPlayerCount 关闭时补丁仍然装载——此时它们透传原生值并保留
// 诊断，这就是报告中描述的单开关回滚。

/// <summary>
/// rally 门等待的数字。在主机和每个对等端上写入，因此两台机器等待
/// 相同的玩家：原生答案正是 "waiting for players N/M" 文本所计数到的数，而在 Fika
/// raid 上它并不是本次 raid 的玩家数（该行会同时给出这两个值）。
/// </summary>
internal sealed class RallyZonePlayerCountPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(TriggerRallyZone), "GetAliveHumanPlayerCount");

    [PatchPostfix]
    public static void Postfix(TriggerRallyZone __instance, ref int __result)
    {
        // AP (analysis/nonfika-raid-inertness)：证明本移植仍在一次不属于我们的 raid 内部行动的
        // 那行日志正是来自这里（[RallyDiag] phase=gate:count ... side=single）。
        // 在 Fika raid 之外，原生计数就是事实，会原样交回。
        if (FikaRaidGate.Skip("TriggerRallyZone.GetAliveHumanPlayerCount postfix"))
        {
            return;
        }

        var native = __result;
        __result = FikaRallyGate.ExpectedParticipants();
        FikaRallyGate.CountLine(__instance, native, __result);
    }
}

/// <summary>
/// 该区域的逐次变化检查（原生 <c>CheckAllPlayersInZone(Int32)</c>）：参数正是
/// 门用来比较的那个数字，而区域仍持有它当前看到的玩家——两者合起来就是
/// 唯一说明门是否打开、以及还有谁缺席的那一行。
/// </summary>
internal sealed class RallyZoneCheckPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(TriggerRallyZone), "CheckAllPlayersInZone");

    [PatchPostfix]
    public static void Postfix(TriggerRallyZone __instance, int __0)
    {
        if (FikaRaidGate.Skip("TriggerRallyZone.CheckAllPlayersInZone postfix"))
        {
            return;
        }

        FikaRallyGate.ZoneLine(__instance, "check");
    }
}

/// <summary>
/// 门在本机打开的那一刻（原生 <c>EmitTrigger(Int32)</c>）。主机就是
/// 掌握该发送的机器，而该行给出发送时使用的计数以及发生在多久之前——这就是
/// 紧随其后创建的进程如何被归因到这个触发器。
/// </summary>
internal sealed class RallyZoneEmitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(TriggerRallyZone), "EmitTrigger");

    [PatchPostfix]
    public static void Postfix(TriggerRallyZone __instance, int __0)
    {
        if (FikaRaidGate.Skip("TriggerRallyZone.EmitTrigger postfix"))
        {
            return;
        }

        string id = null;
        try { id = __instance?._triggerId; } catch (System.Exception) { }
        FikaRallyGate.NoteEmit(id ?? "-", __0);
    }
}

/// <summary>
/// 客户端从中读取 "waiting for players" 面板的那次广播
/// （原生 <c>RallyZoneUpdateEvent.Invoke(Int32 zoneId, Byte playersInZone, Byte totalAlive)</c>）。前缀
/// 把 <c>TotalAlive</c> 替换为门所使用的同一计数，而该行记录它所替换的
/// 原生值——即证明类似报告中 8 这样的数字是哪一侧产生的。
/// </summary>
internal sealed class RallyZoneEventCountPatch : ModulePatch
{
    // (2026-10-27 W46) 原来直接 patch typeof(RallyZoneUpdateEvent).Invoke —— 而在 47965 的 interop 里
    // `Invoke` 只是**声明**（BaseEvent 实现），Harmony 会抛
    // "You can only patch implemented methods/constructors. Patch the declared method virtual void
    //  EFT.GlobalEvents.BaseEvent::Invoke() instead."，整条补丁装不上（日志里是一条 PatchException，
    // 而且它就在无头崩溃那份日志里被看到）。现在改 patch 声明处 BaseEvent.Invoke，并用实例类型过滤，
    // 只对 RallyZoneUpdateEvent 生效。
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(EFT.GlobalEvents.BaseEvent), nameof(EFT.GlobalEvents.BaseEvent.Invoke));

    [PatchPrefix]
    public static void Prefix(EFT.GlobalEvents.BaseEvent __instance, int __0, int __1, ref byte __2)
    {
        if (__instance is not RallyZoneUpdateEvent)
        {
            return;
        }
        // 这是本文件中唯一的行为性补丁：它替换客户端读取其
        // "waiting for players" 面板所用的 TotalAlive。在 Fika raid 之外，该广播保持完全不变。
        if (FikaRaidGate.Skip("RallyZoneUpdateEvent.Invoke prefix"))
        {
            return;
        }

        __2 = FikaRallyGate.CorrectTotalAlive(__0, __1, __2);
    }
}

/// <summary>
/// 从主机到达的一次跳过投票。在每个对等端上写入，因此仅有客户端的日志能显示该投票
/// 是否到达过本机、以及它计入了多少观看者——正是这两个事实决定了
/// 某个对等端是否本应投票。
/// </summary>
internal sealed class CutsceneSkipIntentionReceivePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(CutscenesClientController), "OnSkipIntentionEvent");

    [PatchPostfix]
    public static void Postfix(CutsceneSkipIntentionEventFromServer __0) => FikaRallyGate.SkipReceived(__0);
}

/// <summary>
/// 本机完成了跳过长按（原生 <c>SendSkipIntention(Boolean)</c>）：该行给出
/// 进程，以及该意图是 "跳过" 还是其撤回。
/// </summary>
internal sealed class CutsceneSkipSendPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(CutscenesClientController), "SendSkipIntention");

    [PatchPrefix]
    public static void Prefix(CutscenesClientController __instance, bool __0) => FikaRallyGate.SkipSent(__instance, __0);
}

/// <summary>
/// 主机以跳过方式结束了一段过场，且本机应用了它（原生 <c>OnSkipEvent</c>）。
/// </summary>
internal sealed class CutsceneSkipApplyPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(CutscenesClientController), "OnSkipEvent");

    [PatchPostfix]
    public static void Postfix(CutsceneSkipEventFromServer __0) => FikaRallyGate.SkipApplied(__0);
}
