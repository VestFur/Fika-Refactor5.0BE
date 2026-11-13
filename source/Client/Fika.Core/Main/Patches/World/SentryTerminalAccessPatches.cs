using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.World;

// BJ（analysis/terminal-rauf-card）：TERMINAL-RAUF-CARD。
//
// 本文件中的这四个补丁，构成了「终端转运点旁的俄国士兵不再攻击
// 通过卡片检查的玩家」这条规则的整套原生面：
//
//   Player_ExecuteInteraction_TerminalCard_Patch - 刷卡本身，发生在完成该交互的机器
//       上。（原生 <c>EFT.Player.ExecuteInteraction(WorldInteractiveObject, InteractionResult)</c>，
//       由客户端自己的 <c>FikaPlayer.ExecuteInteraction</c> 抵达；不拥有哨兵的机器从这里
//       取走已通过的卡片并上报它——这正是客户端此前缺失的一跳）
//   SentryBossLogic_OnTerminalAccessGained_Patch   - 状态写入，发生在它实际发生的那台机器上。
//       （原生 <c>SentryBossLogic.OnTerminalAccessGained(Player)</c>，在哨兵的构造函数中
//       订阅到 <c>GlobalEventDispatcher.OnTerminalTransitAccessGained</c>）
//   SentryBossLogic_HaveAccess_Patch              - 状态读取，每次敌对性重算都会发生。
//       （原生 <c>IAccessBossLogic.HaveAccess(Int32)</c>，由 <c>BossSentryHostilityRule</c> 调用）
//   GlobalEventDispatcher_TerminalAccess_Emit_Patch - 原生事件的发射。对于自身没有哨兵的
//       机器（每个 Fika 客户端：它为它自己的玩家触发该事件，且根本不负责任何 bot，
//       analysis/terminal-rauf-hostility-2），这是它唯一拥有的面，因此事实正是从这里
//       被路由到权威侧——即模拟这些 bot、因而持有答案的那台机器。
//
// 这些补丁本身不向任何原生值写入：刷卡读取该交互的原生结果，
// 并把事实交给上报入口（行为主体就是该调用所用的玩家对象，见那个
// 补丁）；授权上报原生写入，并（在权威侧）把同一事实交给其他机器，
// 好让它们的哨兵通过同一个原生入口点应用它；读取只写日志行；发射
// 写入日志行，并在本机没有哨兵可以应用该事实时，把它交给权威侧 / 将它应用
// 到组敌对性数据上（两者都归 FikaSentryAccess 的那一族开关管辖）。四个补丁都
// 原样放行了某些东西，因此把 FikaSentryAccess.RepairEnabled 关掉即可恢复先前的行为，
// 无需重新编译。

/// <summary>
/// 刷卡——拥有该玩家的机器所完成的交互：
/// <c>EFT.Player.ExecuteInteraction(WorldInteractiveObject, InteractionResult)</c>（interop :275004，public
/// virtual，参数为 <c>door</c> 与 <c>interactionResult</c>）。Fika 客户端在本地运行它自己玩家的
/// 交互——<c>FikaPlayer.ExecuteInteraction</c>（Main/Players/FikaPlayer.cs:1517）通过
/// <c>NativeBaseCalls.Call103</c> 调用本方法的原生基类，只把包作为主机的
/// 副本发送出去——所以这里就是本机玩家刷卡结束的地方，也是事实「卡片已被
/// 接受」在不拥有 bot 的机器上存在的地方。
///
/// <para>谁刷的卡不是推断出来的：<c>__instance</c> 就是执行该交互的玩家对象，而补丁只在
/// 它既是本机自己的玩家（<c>Player.IsYourPlayer</c>，:275673）、又位于 Fika 客户端上时
/// 才接受它。之所以给原生基类打补丁而不给 <c>FikaPlayer</c> 的重写打补丁，是因为那个重写是
/// 注入类的托管方法，本移植的 PatchManager 会拒绝它（对每一个 Il2CppObjectBase 声明类型，
/// 它都要求 IL2CPP detour 后端，PatchManager.cs:163-176）；这里的模式与
/// Main/Patches/ArmorSystem/FikaPlayer_OnItemAddedOrRemoved_Transpiler.cs:11 相同——一个原生方法，Fika 类型
/// 仅作为运行时实例出现。</para>
///
/// <para>是哪种交互：只认 <c>CardReader</c> 的一次 unlock。刷卡就是一次 unlock——
/// <c>CardReader.UnlockOperation(KeyComponent, Player, WorldInteractiveObject)</c>（:205017）构建出
/// <c>UnlockResult</c>，其 <c>Succeed</c>（:212752）就是读卡器对那张卡的回答——而读卡器
/// 自己的反应函数 <c>ResolveReaction(KeyComponent)</c>（:205015）决定
/// 同一次交互所播放的提示音与触发器。其他任何可交互对象，以及任何不带钥匙的结果，都不是
/// 「卡片检查已通过」这个事实，会被跳过。</para>
///
/// <para>这个事实不在这里被应用，也不从这里广播：它被交给
/// <see cref="FikaSentryAccess.NoteNativeEmission"/>，即那个把没有 bot 的机器上报给
/// 权威侧的入口（也是那一族开关决定接下来发生什么的地方）。</para>
/// </summary>
internal sealed class Player_ExecuteInteraction_TerminalCard_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(Player)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == nameof(Player.ExecuteInteraction)
                && method.GetParameters() is { Length: 2 } parameters
                && parameters[0].ParameterType == typeof(WorldInteractiveObject)
                && parameters[1].ParameterType == typeof(InteractionResult));
        if (target == null)
        {
            throw new MissingMethodException(
                "Player.ExecuteInteraction(WorldInteractiveObject, InteractionResult)");
        }

        return target;
    }

    [PatchPostfix]
    public static void Postfix(Player __instance, WorldInteractiveObject __0, InteractionResult __1)
    {
        try
        {
            Observe(__instance, __0, __1);
        }
        catch (System.Exception)
        {
            // 诊断绝不能打断承载它的那条原生链路。
        }
    }

    private static void Observe(Player player, WorldInteractiveObject target, InteractionResult interactionResult)
    {
        // 行为主体就是原生调用所作用的那个玩家对象：在客户端上，那正是本机自己的玩家，
        // 而原生基类只会被该玩家自己的交互所抵达（远程角色是一个
        // ObservedPlayer，它的重写从不调用它）。这是调用的事实，而不是我们的推断。
        if (player == null || player.IsYourPlayer != true)
        {
            return;
        }

        // 拥有哨兵的机器通过自己的原生链路应用该事实；只有没有哨兵的机器
        // （每个 Fika 客户端）才需要上报它。
        if (!FikaBackendUtils.IsClient || FikaSentryAccess.CountLocalSentries() != 0)
        {
            return;
        }

        var reader = target != null ? target.TryCast<CardReader>() : null;
        if (reader == null)
        {
            return;
        }

        var unlock = interactionResult != null ? interactionResult.TryCast<UnlockResult>() : null;
        if (unlock == null || unlock.Key == null)
        {
            // 不带 unlock 结果的读卡器交互不是刷卡。这行日志保留下来，因为一份能区分
            // "钩子从未触发" 与 "钩子触发了但没有钥匙" 的日志，才能决定
            // 下一轮怎么做；它对每个读卡器只写一次。
            FikaSentryAccess.Line("swipe-nokey:" + reader.Id,
                "card=swiped role=" + FikaSentryAccess.RoleOf(player) + " state=card-reader applied=no side="
                + FikaSentryAccess.Side + " source=native-execute reader=" + reader.Id
                + " -> result=the interaction of this machine's player carries no unlock result (nothing is reported)");
            return;
        }

        var key = KeyOf(unlock);

        if (!unlock.Succeed)
        {
            FikaSentryAccess.Line("swipe-denied:" + reader.Id + ":" + key,
                "card=swiped role=" + FikaSentryAccess.RoleOf(player) + " state=card-reader applied=no side="
                + FikaSentryAccess.Side + " source=native-execute reader=" + reader.Id + " key=" + key
                + " -> result=the reader refused this card on this machine (nothing is reported)");
            return;
        }

        FikaSentryAccess.Line("swipe:" + reader.Id + ":" + key,
            "card=swiped role=" + FikaSentryAccess.RoleOf(player) + " state=card-reader applied=- side="
            + FikaSentryAccess.Side + " source=native-execute reader=" + reader.Id + " key=" + key
            + " profile=" + player.ProfileId
            + " -> result=the swipe of this machine's player was accepted by the reader; the fact goes to NoteNativeEmission");

        // (2026-10-21) 本轮临时的 [SyncDiag] 行：唯一一行说明向上上报确实存在的日志。
        FikaGlobals.LogInfo("[SyncDiag] swipe-report profile=" + player.ProfileId,
            "Player_ExecuteInteraction_TerminalCard_Patch");

        FikaSentryAccess.NoteNativeEmission(player);
    }

    /// <summary>被使用的那张卡——只在日志行里点名，从不属于上报内容本身。</summary>
    private static string KeyOf(UnlockResult unlock)
    {
        try
        {
            var item = unlock?.Key?.Item;
            return item == null || string.IsNullOrEmpty(item.Id) ? "-" : item.Id;
        }
        catch (System.Exception)
        {
            return "-";
        }
    }
}

/// <summary>
/// 授权的原生写入（拥有读卡器的那台机器也会在这里发射它）。上报
/// 玩家被加入的集合，并在权威机器上复制该事实。
/// </summary>
internal sealed class SentryBossLogic_OnTerminalAccessGained_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(SentryBossLogic)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == nameof(SentryBossLogic.OnTerminalAccessGained));
        if (target == null)
        {
            throw new MissingMethodException("SentryBossLogic.OnTerminalAccessGained(Player)");
        }

        return target;
    }

    [PatchPostfix]
    public static void Postfix(SentryBossLogic __instance, Player __0)
    {
        try
        {
            var count = -1;
            try { count = __instance?._accessedPlayerIds?.Count ?? -1; } catch (System.Exception) { }
            FikaSentryAccess.NoteNativeGrant(__0, count, "native");
        }
        catch (System.Exception)
        {
            // 诊断绝不能打断承载它的那条原生链路。
        }
    }
}

/// <summary>
/// 授权的原生读取：<c>BossSentryHostilityRule</c> 所问的唯一一个问题。仅当
/// 该回答会让玩家继续保持敌对状态时才写入，因此日志里正好只承载上报所关注的那些拒绝
/// （主机上每名玩家一行，客户端上则是那一行——证明其哨兵从未得知该
/// 事实）。
/// </summary>
internal sealed class SentryBossLogic_HaveAccess_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(SentryBossLogic)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == nameof(SentryBossLogic.HaveAccess));
        if (target == null)
        {
            throw new MissingMethodException("SentryBossLogic.HaveAccess(Int32)");
        }

        return target;
    }

    [PatchPostfix]
    public static void Postfix(int __0, bool __result)
    {
        if (__result)
        {
            // 该授权在本机已生效：某名玩家的第一次「是」值得记一行，其余的都只是
            // 敌对性重算本来就会产生的每帧噪声。
            if (FikaSentryAccess.NoteAccessYes(__0))
            {
                FikaSentryAccess.Line("haveaccess-yes:" + __0,
                    "card=swiped role=- state=verify applied=yes side=" + FikaSentryAccess.Side
                    + " raidId=" + __0 + " -> result=the sentry of this machine accepts the player");
            }

            return;
        }

        FikaSentryAccess.NoteAccessNo(__0);
    }
}

/// <summary>
/// 规则本身：本机上某一名玩家的哨兵规则判定。这就是读取上报时所看的
/// 那一行——它点名玩家、判定结果以及他是否携带着东西，并把
/// 「本机的哨兵拒绝了一名手无寸铁的玩家」变成日志里的事实，而不是一种印象。
/// 每场突袭中每个（玩家、判定）组合一行，因为一个组的敌对性每帧都会被重算。
/// </summary>
internal sealed class BossSentryHostilityRule_EvaluateHostility_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(BossSentryHostilityRule)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == nameof(BossSentryHostilityRule.EvaluateHostility));
        if (target == null)
        {
            throw new MissingMethodException("BossSentryHostilityRule.EvaluateHostility(BotGroupInnerHostilityData, PlayerHostilityData)");
        }

        return target;
    }

    [PatchPostfix]
    public static void Postfix(BossSentryHostilityRule __instance, BotGroupInnerHostilityData __0,
        PlayerHostilityData __1, BotHostilityStatus __result)
    {
        try
        {
            var iplayer = __1?.Player;
            if (iplayer == null || iplayer.IsAI)
            {
                return;
            }

            // PlayerHostilityData 携带一个 IPlayer；该规则只对本场突袭中的真人有效，
            // 而具体玩家对象正是读取被上报案例双手信息的来源。
            var player = iplayer.TryCast<Player>();
            if (player == null)
            {
                return;
            }

            var raidId = -1;
            try { raidId = player.RaidId; } catch (System.Exception) { }
            var verdict = __result.ToString();
            FikaSentryAccess.RuleVerdict(player, raidId, verdict);
        }
        catch (System.Exception)
        {
        }
    }
}

/// <summary>
/// <c>GlobalEventDispatcher.OnTerminalTransitAccessGained</c> 的发射——原生链路判定
/// 某名玩家已获得通行权的那一刻。在主机命名会话里出现这里的一行，说明卡片已被
/// 权威侧接受；r32 的客户端运行证明这一行在客户端上也会出现（客户端在本地
/// 运行它自己玩家的交互），这就是为什么该补丁同时也是这个事实的路由点：
/// 当本机不拥有任何 bot 时，<c>FikaSentryAccess.NoteNativeEmission</c> 会把它上报给权威侧。
/// </summary>
internal sealed class GlobalEventDispatcher_TerminalAccess_Emit_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(GlobalEventDispatcher)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == nameof(GlobalEventDispatcher.ApplyOnTerminalTransitAccessGained));
        if (target == null)
        {
            throw new MissingMethodException("GlobalEventDispatcher.ApplyOnTerminalTransitAccessGained(Player)");
        }

        return target;
    }

    [PatchPostfix]
    public static void Postfix(Player __0)
    {
        try
        {
            // 日志行和路由都在 FikaSentryAccess 里：这个补丁只是一个纯粹的原生链路观测
            // 点，接下来发生什么由该组件的那一族开关决定。
            FikaSentryAccess.NoteNativeEmission(__0);
        }
        catch (System.Exception)
        {
        }
    }
}
