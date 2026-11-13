using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.World;

// BJ (analysis/terminal-rauf-card): TERMINAL-RAUF-CARD.
//
// The three patches of this file are the whole native surface of the rule 「the Russian soldiers at the
// terminal transfer point stop attacking a player who passed the card check」:
//
//   SentryBossLogic_OnTerminalAccessGained_Patch   - the write of the state, on the machine it happens on.
//       (native <c>SentryBossLogic.OnTerminalAccessGained(Player)</c>, subscribed to
//       <c>GlobalEventDispatcher.OnTerminalTransitAccessGained</c> in the sentry's constructor)
//   SentryBossLogic_HaveAccess_Patch              - the read of the state, every hostility recalculation.
//       (native <c>IAccessBossLogic.HaveAccess(Int32)</c>, called by <c>BossSentryHostilityRule</c>)
//   GlobalEventDispatcher_TerminalAccess_Emit_Patch - the emission of the native event. This is the only
//       surface a machine WITHOUT a sentry of its own has (every Fika client: it raises the event for its
//       own player and owns no bot at all, analysis/terminal-rauf-hostility-2), so it is where the fact is
//       routed to the authority - the machine that simulates the bots and therefore owns the answer.
//
// Nothing is written to a native value from the patches themselves: the first reports the native write and
// (on the authority) hands the same fact to the other machines so their sentries can apply it through the
// same native entry point; the second only writes lines; the third writes lines and, when this machine
// owns no sentry to apply the fact to, hands it to the authority / applies it to the group hostility data
// (both under the one switch family of FikaSentryAccess). All three pass something through untouched, so
// turning FikaSentryAccess.RepairEnabled off restores the previous behaviour without a rebuild.

/// <summary>
/// The native write of the grant (also the machine that owns the card reader emits it here). Reports the
/// set the player was added to and, on the authority machine, replicates the fact.
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
            // A diagnostic must never break the native chain that carries it.
        }
    }
}

/// <summary>
/// The native read of the grant: the one question <c>BossSentryHostilityRule</c> asks. Written only when
/// the answer would keep the player an enemy, so the log carries exactly the refusals the report is about
/// (one line per player on the host, and on a client the line that proves its sentry never learned the
/// fact).
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
            // The grant is in force on this machine: the first yes of a player is worth a line, the rest are
            // the per-frame noise the hostility recalculation would produce.
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
/// THE RULE ITSELF: the verdict of the sentry rule of one player on this machine. This is the line the
/// report is read from - it names the player, the verdict and whether he carried anything, and it makes
/// 「the sentry of this machine refused an unarmed player」 a fact of the log instead of an impression.
/// One line per (player, verdict) per raid, because the hostility of a group is recalculated every frame.
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

            // PlayerHostilityData carries an IPlayer; the rule only matters for a human of this raid, and the
            // concrete player is what the hands of the reported case are read from.
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
/// The emission of <c>GlobalEventDispatcher.OnTerminalTransitAccessGained</c> - the moment the native
/// chain decides that a player has the access. A line here on the host named session says the card was
/// accepted by the authority; the r32 client run proved the line appears on a client too (the client runs
/// its own player's interaction locally), which is why this patch is also the routing point of the fact:
/// <c>FikaSentryAccess.NoteNativeEmission</c> reports it to the authority when this machine owns no bots.
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
            // The line and the routing both live in FikaSentryAccess: this patch stays a pure observation
            // point of the native chain, and the switch family of the component governs what happens next.
            FikaSentryAccess.NoteNativeEmission(__0);
        }
        catch (System.Exception)
        {
        }
    }
}
