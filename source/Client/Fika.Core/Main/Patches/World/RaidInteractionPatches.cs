using System.Reflection;
using CommonAssets.Scripts.Cutscenes;
using EFT;
using EFT.Interactive;
using EFT.Passcodes;
using EFT.GlobalEvents;
using EFT.UI;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.World;

internal sealed class PasscodeResultSyncPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ClientLocalPasscodeController),
        nameof(ClientLocalPasscodeController.HandleSubmitPasscode));

    [PatchPrefix]
    public static void Prefix(ClientLocalPasscodeController __instance, PasscodeTerminal terminal, string passcode, out bool __state)
    {
        // 本机返回值表示"尝试已被处理"，包括密码错误的情况。
        // 在本机方法改变门的状态之前，先把密码比较的结果取出来。
        __state = terminal != null && terminal.IsInteractable &&
            __instance._generatedPasscodes.TryGetValue(terminal.PasscodeId, out var expected) &&
            string.Equals(expected, passcode, System.StringComparison.Ordinal);
    }

    [PatchPostfix]
    public static void Postfix(Player player, PasscodeTerminal terminal, bool __result, bool __state)
    {
        // AP (analysis/nonfika-raid-inertness)：密码终端是每场 raid 都有的世界交互。
        // RaidInteractionSync 只在我们的 raid 内被绑定，因此在 raid 之外本来就已经是 no-op；
        // 这个守卫把这一点显式化，并在实际发生时于日志里点明。
        if (FikaRaidGate.Skip("ClientLocalPasscodeController.HandleSubmitPasscode postfix"))
        {
            return;
        }

        var handled = __result && __state;
        // BB (analysis/icebreaker-keypad-door)：密码门在主机日志里必须留下的那一行 —— 它宣告了第几次
        // 尝试，以及它宣告的是否为成功。
        // BU (analysis/icebreaker-keypad-3)：每个 world 里持有游戏自身本地控制器的机器都会执行
        // 这项检查，因此同一个方法也是客户端那边的本机裁决结果 —— 该行会说明它是哪一方，
        // 而且只有主机会宣告任何东西（BroadcastPasscodeResult 在其他所有角色上都会拒绝）。
        var server = FikaBackendUtils.IsServer;
        
        RaidInteractionSync.BroadcastPasscodeResult(player, terminal, handled);
    }
}

internal sealed class RaidDialogClaimPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(RaidDialogEntryPoint),
        nameof(RaidDialogEntryPoint.StartDialogOffline));

    [PatchPrefix]
    public static bool Prefix(RaidDialogEntryPoint __instance, ref Il2CppSystem.Threading.Tasks.Task __result)
    {
        if (!FikaBackendUtils.IsClient || RaidInteractionSync.World == null ||
            DialogEntryOccupancy.HasLocalClaim(__instance.PointId)) return true;
        __result = NativeTask.FromManaged(MainThreadDispatcher.RunAsync(() => DialogEntryOccupancy.StartDialog(__instance)));
        return false;
    }
}

internal sealed class CutsceneStartSyncPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(CutscenesServerController),
        nameof(CutscenesServerController.StartCutscene));

    [PatchPrefix]
    public static bool Prefix(ref CutsceneProcess __result, string __0,
        Il2CppSystem.Collections.Generic.List<int> __1)
    {
        if (RaidInteractionSync.World == null) return true;
        // (2026-10-18) 主机把本机座位表收窄成会话真正拥有的玩家，否则
        // "waiting for players" 计数器会一直等待无人拥有的座位，进程永远不结束。
        if (FikaBackendUtils.IsServer) RaidInteractionSync.FilterViewersToSession(__1);
        if (!FikaBackendUtils.IsClient) return true;
        // (analysis/cutscene-client-native-trigger) 唯一被放行的一次本地启动：本机自己的
        // 触发器请求了主机从未派发的过场，因此桥把那一场过场授予了
        // 这次本机调用。这个 return 之后的一切都是本机行为 —— 进程、它的座位表以及本地观看者的
        // 播放 —— 这正是重点：客户端运行的触发链与主机完全相同。
        if (FikaLocalCutsceneTrigger.GrantNativeStart(__0, __1)) return true;
        // 本地的任务/触发器处理仍可能调用离线服务器控制器。
        // 只有主机报文 —— 或上面被授予的本地触发器 —— 才会启动客户端的本机播放。
        // 被丢弃的请求被交给桥，当主机保持沉默时由桥以本机方式重新运行它
        // （analysis/cutscene-client-native-trigger）。
        FikaLocalCutsceneTrigger.NoteNativeRequest(__0);
        __result = null;
        
        return false;
    }

    [PatchPostfix]
    public static void Postfix(CutsceneProcess __result, bool forceMode, float initTime)
    {
        RaidInteractionSync.StartCutscene(__result, forceMode, initTime);
        
        // (analysis/cutscene-client-native-trigger) 被授予的本地启动的后半部分：本机自己
        // 创建的进程必须交给本机自己的客户端控制器。在主机上这一步是
        // 上面的广播；在客户端上，这一步以前归离线模式所有，而 Fika 把它关掉了
        // （RaidInteractionSync.ConfigureControllers），因此桥会抛出与主机报文本会抛出的
        // 相同的本机事件 —— 并带上本机自己的进程 id。
        if (FikaBackendUtils.IsClient && __result != null)
            RaidInteractionSync.PlayLocalNativeProcess(__result);
    }
}

internal sealed class CutsceneViewCompleteSyncPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(CutscenesClientController),
        nameof(CutscenesClientController.TimelineShowed));

    [PatchPrefix]
    public static void Prefix(CutscenesClientController __instance, ref bool skip, out int __state)
    {
        __state = 0;
        if (RaidInteractionSync.World == null) return;
        if (__instance._currentCutsceneClientOnly)
        {
            // 在此构建中，仅客户端侧的收尾仍会走到 ClientPlayer 的转换。
            // 它们没有需要确认的服务器进程，但仍需要本机清理。
            skip = true;
            return;
        }
        if (!skip && !__instance._currentCutsceneViewerOnly)
        {
            __state = __instance._cutsceneProcessId;
            
        }
        // 本机网络分支会把本地玩家转换成 ClientPlayer，而
        // FikaPlayer 派生自 LocalPlayer。保留它的 UI/特效清理，只替换
        // 完成通知（skip=true 时用的是同一套清理）。
        skip = true;
    }

    [PatchPostfix]
    public static void Postfix(string timelineId, int __state)
    {
        if (__state != 0) GlobalEventsController.CreateEvent<SendEndCutsceneOnServerEvent>().Invoke(timelineId, __state);
        FikaFinalMissionDirector.LocalTimelineComplete(timelineId);
    }
}

/// <summary>
/// (2026-10-21) 过场唯一的结束点，位于每一台机器上：<c>EndCutscene(cutsceneId, cutsceneProcessId,
/// process, playersId, skip)</c>（interop，5 个参数）是两种角色结束进程时共同走的调用 —— 主机经由
/// 自己的 <c>OnUpdate</c>，客户端经由 <see cref="FikaLocalCutsceneTrigger"/>。因此它的 postfix 承担了
/// 每台机器在进程结束时都必须做的两件事，并且是在任何角色专属的
/// 记账逻辑能提前返回<b>之前</b>完成它们：
/// <list type="bullet">
/// <item>启动时授予的狙击区免疫，会为本次进程的观众以及本机
/// 自己的玩家解除 —— 主机以前会在 raid 余下的时间里留着它的条目，因为旧的解除逻辑是按
/// 会话报文的 NetId 映射的，而主机自己的 raid id 并不在其中；</item>
/// <item>主机向各 peer 宣告结束（见 <see cref="RaidInteractionSync.EndCutscene"/>）。</item>
/// </list>
/// 参数名用的是 interop 的名字（<c>cutsceneProcessId</c>、<c>playersId</c>、<c>skip</c>），取自
/// 本构建的本机堆栈回溯。
/// </summary>
internal sealed class CutsceneEndSyncPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(CutscenesServerController),
        nameof(CutscenesServerController.EndCutscene));

    [PatchPostfix]
    public static void Postfix(int cutsceneProcessId, Il2CppSystem.Collections.Generic.List<int> playersId, bool skip) =>
        RaidInteractionSync.EndCutscene(cutsceneProcessId, playersId, skip);
}

/// <summary>
/// (2026-10-21) 地图触发器触发的过场：<c>CutscenesServerController.StartCutsceneWithTime</c>（interop
/// :94633，<c>cutsceneId, playersId, fixedTime, forceMode</c>）是位置触发器所用的入口，而它
/// 不经过会话广播所挂靠的 <c>StartCutscene</c> 调用 —— 因此触发器在主机上启动的过场
/// 从未到达客户端。它刚刚创建的进程会从控制器自己的表
/// （<c>_cutsceneProcesses</c>，:94638）中、按 cutscene id 在本会话尚未广播过的那些里取出，然后
/// 交给 <see cref="RaidInteractionSync.StartCutscene"/> 的那一次广播 —— 同样的报文、同样的座位
/// 映射，没有第二套协议。
/// </summary>
internal sealed class CutsceneStartWithTimeSyncPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(CutscenesServerController),
        nameof(CutscenesServerController.StartCutsceneWithTime));

    [PatchPrefix]
    public static void Prefix(Il2CppSystem.Collections.Generic.List<int> __1)
    {
        // (2026-10-27 W49 / TARGETS N2 根治) 地图触发器起的过场（落地/世界类）在主机上会等到"每个观众
        // 都确认"为止；合作局里对端没有被登记成本地进程，就永远不回确认，主机因此黑屏卡在过场里
        // （用户实测：客机退出游戏后主机立刻正常）。这类过场每个机器都能自己播，所以主机只保留自己
        // 一个座位，不再等对端；对端由它自己的触发器/兜底起本地进程。rally 那类走的是 StartCutscene，
        // 不经过这里，仍然保留全队座位。
        if (!FikaBackendUtils.IsServer || __1 == null) return;
        try
        {
            var local = RaidInteractionSync.LocalPlayerId;
            if (local <= 0 || __1.Count <= 1) return;
            var before = __1.Count;
            var kept = false;
            for (var i = __1.Count - 1; i >= 0; i--)
            {
                if (__1[i] == local) { kept = true; continue; }
                __1.RemoveAt(i);
            }
            if (!kept) __1.Add(local);
            FikaGlobals.LogInfo($"[CgDiag] host-seats-narrowed from={before} to={__1.Count} " +
                "reason=map-trigger cutscenes are played locally by every machine", "RaidInteractionSync");
        }
        catch (System.Exception exception)
        {
            FikaGlobals.LogWarning($"[CgDiag] host-seats-narrowed threw: {exception.GetType().Name}", "RaidInteractionSync");
        }
    }

    [PatchPostfix]
    public static void Postfix(CutscenesServerController __instance, string cutsceneId, bool forceMode)
    {
        RaidInteractionSync.StartCutsceneFromWithTime(__instance, cutsceneId, forceMode);
    }
}

/// <summary>
/// (2026-10-21) 本地观看者的结束点：<c>CutscenesClientController.TimelineShowed</c>（interop :94368）是本机
/// 的 timeline 播放完毕的时刻，也就是本机自己创建的某个进程生命周期中的"已观看"那一半
/// （<see cref="FikaLocalCutsceneTrigger"/>）。进程 id 在本机方法体执行之前就被捕获
/// （本文件的各个 postfix 已经改写了它宣告的内容），因此该通知指出的是
/// 真正在运行的那个进程。
/// </summary>
internal sealed class CutsceneViewedLocalProcessPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(CutscenesClientController),
        nameof(CutscenesClientController.TimelineShowed));

    [PatchPrefix]
    public static void Prefix(CutscenesClientController __instance, out int __state)
    {
        __state = 0;
        try { __state = __instance == null ? 0 : __instance._cutsceneProcessId; }
        catch (System.Exception) { __state = 0; }
    }

    [PatchPostfix]
    public static void Postfix(CutscenesClientController __instance, string timelineId, int __state)
    {
        var cutscene = timelineId;
        try { if (string.IsNullOrEmpty(cutscene) && __instance != null) cutscene = __instance._cutsceneId; }
        catch (System.Exception) { }
        FikaLocalCutsceneTrigger.NoteViewed(__state, cutscene);
        // (2026-10-21) 免疫解除的过场结束兜底网：本机自己的观看者刚刚
        // 播放完 timeline，而这是两种角色都保证会执行的唯一结束事件。它在这里只是被
        // 布防 —— 解除本身稍后在 RaidInteractionSync.Tick 中发生，而主解除路径
        // （本机 EndCutscene 的 postfix）仍然优先。
        RaidInteractionSync.NoteLocalTimelineShown(__state);
    }
}

// (analysis/cutscene-view-and-end) 整个过场时钟所依赖的两个逐帧方法。round-16
// 主机日志证明没有人调用服务器那一个（进程存活期间它的已用时间一动不动），而对客户端控制器的
// 同一个问题 —— 它的逐帧步骤正是启动和结束本机自己那场
// 场景的东西 —— 只能从外部回答：它从未被进入（哪里都不存在 phase=view 行），
// 也从未渲染过任何东西。这些补丁只做计数：不触碰返回值、不触碰参数、也不触碰实例，
// 因此游戏的行为与原来完全一致。它们的行是 "phase=gate:ticks ... server engine=N bridge=M ..."，
// 它把每次调用拆成"本机调用者"和"本桥"。
// Fika 在主机与各 peer 上都替换了单人驱动。在未绑定的 Fika world 之外
// 保留 SPT 的实现。
internal sealed class FinalMissionClientStartGuardPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(
        typeof(SPTushonka.SinglePlayer.Patches.Cutscenes.FinalMissionDirectorPatches.StartPatch), "Postfix");

    [PatchPrefix]
    public static bool Prefix() => !FikaFinalMissionDirector.Bound;
}

internal sealed class FinalMissionClientTickGuardPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(
        typeof(SPTushonka.SinglePlayer.Patches.Cutscenes.FinalMissionDirectorPatches.TickPatch), "Postfix");

    [PatchPrefix]
    public static bool Prefix() => !FikaFinalMissionDirector.Bound;
}
// ICEBREAKER-CUTSCENE-BRIDGE (analysis/icebreaker-cutscene)：客户端控制器的 timeline bank
// 就在 FikaClient.ApplyPendingRaidInteractions 的开头被修补，早于排队的过场主机报文
// 被消费 —— 也就是本文件以前用一个 Harmony prefix 放置的那次调用。那个 prefix 从未
// 安装成功：补丁管理器是通过 IL2CPP 做 detour 的，而 Fika 程序集自身的方法没有 IL2CPP
// detour（"CutsceneClientBankTickPatch: NotSupportedException ... requires an IL2CPP detour"，round-15 主机
// 日志，analysis/cutscene-play-and-end/REPORT.md）。调用点现在位于 FikaClient.RaidInteractions.cs。

// =============================================================================================================
// BB (analysis/icebreaker-keypad-door)：一场 raid 的键盘（keypad）链路，被点名并被修补。
//
// 本轮的反馈是"破冰船键盘在客机上无法使用"。游戏自己的提交守卫在任何 Fika 报文
// 出现之前，就用 "Tried to enter code on location without passcodes" 拒绝这样的密码，
// 因此主机永远不知道这次尝试，门也永远不会打开，尽管主机知道那个密码。下面三个
// 补丁都不过滤、不移动、也不替换任何本机交互：
//   * PasscodeMenuDiagPatch   postfix，只读   - PasscodeTerminal 的交互菜单（offered/empty）；
//   * PasscodePanelShowDiagPatch prefix，void    - 本机上的本机密码面板被打开；
//   * PasscodeSubmitRelayPatch postfix，ref __result - 游戏拒绝的密码会经由本机事件路径所用的
//     那个报文交给主机。仅在客户端上、仅在已绑定的 Fika raid 内，且仅在该主机
//     至少应答过本客户端一次之后；当 KeypadDiag.RepairEnabled=false 时它永不动作。
// =============================================================================================================

/// <summary>键盘各行的共用文本：门、控制器种类，以及守卫据以判断的两个状态。</summary>
internal static class PasscodeText
{
    internal static string Door(PasscodeTerminal terminal)
    {
        try
        {
            return terminal == null ? "-" : (terminal.PasscodeId ?? "-") + "#" + terminal.NetId;
        }
        catch (System.Exception)
        {
            return "?";
        }
    }

    internal static string Controller(ClientPasscodeController controller)
    {
        try
        {
            if (controller == null) return "none";
            if (controller.TryCast<ClientNetworkPasscodeController>() != null) return "network";
            if (controller.TryCast<ClientLocalPasscodeController>() != null) return "local";
            return "client";
        }
        catch (System.Exception)
        {
            return "?";
        }
    }

    /// <summary>该控制器所在位置携带多少个密码条目（null = 完全没有）。</summary>
    internal static string Settings(ClientPasscodeController controller)
    {
        try
        {
            var entries = controller?._passcodeLocationSettings?.Entries;
            return entries == null ? "null" : entries.Length.ToString();
        }
        catch (System.Exception)
        {
            return "?";
        }
    }

    internal static string Terminals(ClientPasscodeController controller)
    {
        try
        {
            return controller?._passcodeTerminals == null ? "-" : controller._passcodeTerminals.Count.ToString();
        }
        catch (System.Exception)
        {
            return "?";
        }
    }
}

/// <summary>
/// 某个键盘的本机交互菜单：一个只读取本机调度器所返回状态的 postfix。
/// 关于动作、它们的顺序或它们的委托，没有任何东西被触碰 —— 本机的菜单
/// 不会因为这个补丁而改变，它只是给它命名。"result=empty" 这一行证明了反馈中所说的
/// "这里键盘没有提供交互"，而 "result=offered" 则排除了菜单是原因。
/// </summary>
internal sealed class PasscodeMenuDiagPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var methods = typeof(InteractionContextHelper).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (var i = 0; i < methods.Length; i++)
        {
            var method = methods[i];
            if (method.Name != nameof(InteractionContextHelper.GetAvailableActions)) continue;
            var parameters = method.GetParameters();
            if (parameters.Length != 2) continue;
            if (parameters[1].ParameterType != typeof(PasscodeTerminal)) continue;
            return method;
        }

        throw new System.MissingMethodException(
            "EFT.InteractionContextHelper.GetAvailableActions(GamePlayerOwner, PasscodeTerminal)");
    }

    [PatchPostfix]
    public static void Postfix(AvailableInteractionState __result, PasscodeTerminal __1)
    {
        try
        {
            var actions = __result?.Actions;
            var names = new System.Text.StringBuilder();
            var count = 0;
            names.Append('[');
            if (actions != null)
            {
                for (var i = 0; i < actions.Count; i++)
                {
                    var action = actions[i];
                    if (action == null) continue;
                    if (count > 0) names.Append(',');
                    names.Append(string.IsNullOrEmpty(action.Name) ? "<unnamed>" : action.Name);
                    count++;
                }
            }

            names.Append(']');
            
        }
        catch (System.Exception)
        {
        }
    }
}

/// <summary>
/// 本机的本机键盘界面：它的 Show 正是 PasscodeTerminal 的交互动作所调用的东西，
/// 因此这里的一行就回答了"本次反馈中的输入界面是否在本机打开过，用的是
/// 哪个控制器"。void prefix 无法跳过或改变这次调用。
/// </summary>
internal sealed class PasscodePanelShowDiagPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(PasscodeInputPanel).GetMethod(nameof(PasscodeInputPanel.Show),
            BindingFlags.Instance | BindingFlags.Public, null,
            new[] { typeof(Player), typeof(PasscodeTerminal), typeof(ClientPasscodeController) }, null);
        if (target == null)
        {
            throw new System.MissingMethodException(
                "EFT.UI.PasscodeInputPanel.Show(Player, PasscodeTerminal, ClientPasscodeController)");
        }

        return target;
    }

    [PatchPrefix]
    public static void Prefix(Player __0, PasscodeTerminal __1, ClientPasscodeController __2)
    {
        try
        {
            
        }
        catch (System.Exception)
        {
        }
    }
}

/// <summary>
/// 两个本机控制器（本地那一个，以及 Fika 在客户端上安装的网络那一个）的共用入口，
/// 也是把"游戏处理了这个密码"与"游戏拒绝了它"区分开的唯一地方。prefix 点出
/// 守卫据以判断的状态；postfix —— 仅在游戏拒绝了某个密码时、仅在已绑定
/// Fika raid 且其主机至少应答过本机一次的客户端上 —— 会把那个密码发给主机，并把这次尝试
/// 报为已处理：与本机网络路径相同的契约（面板等待主机的应答，
/// 并在应答到达时播放它的动画）。在该情形之外，不做任何改变。
/// </summary>
internal sealed class PasscodeSubmitRelayPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(ClientPasscodeController).GetMethod(nameof(ClientPasscodeController.SubmitPasscode),
            BindingFlags.Instance | BindingFlags.Public, null,
            new[] { typeof(Player), typeof(PasscodeTerminal), typeof(string) }, null);
        if (target == null)
        {
            throw new System.MissingMethodException(
                "EFT.Passcodes.ClientPasscodeController.SubmitPasscode(Player, PasscodeTerminal, string)");
        }

        return target;
    }

    [PatchPrefix]
    public static void Prefix(ClientPasscodeController __instance, PasscodeTerminal terminal, string passcode)
    {
        try
        {
            
        }
        catch (System.Exception)
        {
        }
    }

    [PatchPostfix]
    public static void Postfix(ClientPasscodeController __instance, Player player, PasscodeTerminal terminal,
        string passcode, ref bool __result)
    {
        try
        {
            if (__result)
            {
                // BU (analysis/icebreaker-keypad-3)：当本机的 world 自己校验密码时（即游戏自身的
                // 本地控制器位于主机的表上），答案已经产生 —— 而本机检查所
                // 接受（ACCEPTED）的密码会被上报给主机，主机对它自己那份键盘
                // 重复同样的本机检查，并把它宣告给每个 peer。Fika 只是传输这个事实，并不对它做判断。
                if (KeypadCodeSync.MatchesLocal(__instance, terminal?.PasscodeId, passcode))
                {
                    var detail = "door=" + PasscodeText.Door(terminal) + " input=" + (passcode?.Length ?? 0)
                        + " chars controller=" + PasscodeText.Controller(__instance)
                        + " settings=" + PasscodeText.Settings(__instance) + " terminals=" + PasscodeText.Terminals(__instance);
                    if (FikaBackendUtils.IsClient)
                    {
                        var synced = RaidInteractionSync.TrySyncLocalSuccess(player, terminal, passcode, out var syncReason);
                        
                    }
                    else
                    {
                        
                    }

                    return;
                }

                
                return;
            }

            // 本机本地检查所拒绝的密码是一个已完成的决定：下面的转发只为那种回退形态
            // 而存在（即某个 world 因为自己没有表而通过主机来校验）。
            if (KeypadCodeSync.IsLocal(__instance))
            {
                
                return;
            }

            if (!RaidInteractionSync.TryRelayPasscodeAttempt(player, terminal, passcode, out var reason))
            {
                
                return;
            }

            // 这次尝试现在归主机所有：把它报为已处理，好让本机面板等待结果事件，
            // 而不是重复提交一个游戏一直拒绝的请求（本轮日志里这个循环出现了 13 次）。
            __result = true;
            
        }
        catch (System.Exception)
        {
        }
    }
}
