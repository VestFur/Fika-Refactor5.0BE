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
// Every patch in this file belongs to FikaRallyGate: the two rally counters of the native
// EFT.GameTriggers.TriggerRallyZone (which number the gate waits for, and what the zone reports while it
// waits), the broadcast that carries that number to a client, and the three skip signals a viewer sends.
// Nothing here changes a native call, its arguments or its order: a count is replaced by the count of the
// players this raid really has, and everything else only writes a [RallyDiag] line. The patches stay
// installed when FikaRallyGate.UseFikaPlayerCount is off - they then pass the native value through and keep
// the diagnosis, which is the one-toggle rollback described in the report.

/// <summary>
/// The number the rally gate waits for. Written on the host and on every peer, so both machines wait for the
/// same players: the native answer is what the "waiting for players N/M" text counts up to, and on a Fika
/// raid it is not this raid's player count (the line names both values).
/// </summary>
internal sealed class RallyZonePlayerCountPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(TriggerRallyZone), "GetAliveHumanPlayerCount");

    [PatchPostfix]
    public static void Postfix(TriggerRallyZone __instance, ref int __result)
    {
        // AP (analysis/nonfika-raid-inertness): the line that proved this port was still acting inside a
        // raid that was not ours came from exactly here ([RallyDiag] phase=gate:count ... side=single).
        // Outside a Fika raid the native count is the truth and is handed back untouched.
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
/// The per-change check of the zone (native <c>CheckAllPlayersInZone(Int32)</c>): the argument is the number
/// the gate compares against, and the zone still holds the players it currently sees - together they are the
/// one line that says whether the gate is open and who is still missing.
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
/// The moment the gate opens on this machine (native <c>EmitTrigger(Int32)</c>). The host is the machine that
/// owns the emission, and the line names the count it was emitted with and how long ago - which is how a
/// process created right afterwards is attributed to this trigger.
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
/// The broadcast a client reads its "waiting for players" panel from
/// (native <c>RallyZoneUpdateEvent.Invoke(Int32 zoneId, Byte playersInZone, Byte totalAlive)</c>). The prefix
/// replaces <c>TotalAlive</c> with the same count the gate uses, and the line records the native value it
/// replaced - the proof of which side produced a number like the reported 8.
/// </summary>
internal sealed class RallyZoneEventCountPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(RallyZoneUpdateEvent), nameof(RallyZoneUpdateEvent.Invoke));

    [PatchPrefix]
    public static void Prefix(int __0, int __1, ref byte __2)
    {
        // This is the one behavioural patch of the file: it replaces the TotalAlive a client reads its
        // "waiting for players" panel from. Outside a Fika raid the broadcast is left exactly as it was.
        if (FikaRaidGate.Skip("RallyZoneUpdateEvent.Invoke prefix"))
        {
            return;
        }

        __2 = FikaRallyGate.CorrectTotalAlive(__0, __1, __2);
    }
}

/// <summary>
/// A skip vote arriving from the host. Written on every peer, so a client-only log shows whether the vote of
/// the raid ever reached this machine and how many viewers it counted - the two facts that decide whether a
/// peer was expected to vote at all.
/// </summary>
internal sealed class CutsceneSkipIntentionReceivePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(CutscenesClientController), "OnSkipIntentionEvent");

    [PatchPostfix]
    public static void Postfix(CutsceneSkipIntentionEventFromServer __0) => FikaRallyGate.SkipReceived(__0);
}

/// <summary>
/// This machine completed the skip hold (native <c>SendSkipIntention(Boolean)</c>): the line names the
/// process and whether the intention was "skip" or its retraction.
/// </summary>
internal sealed class CutsceneSkipSendPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(CutscenesClientController), "SendSkipIntention");

    [PatchPrefix]
    public static void Prefix(CutscenesClientController __instance, bool __0) => FikaRallyGate.SkipSent(__instance, __0);
}

/// <summary>
/// The host ended a cutscene as skipped and this machine applied it (native <c>OnSkipEvent</c>).
/// </summary>
internal sealed class CutsceneSkipApplyPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(CutscenesClientController), "OnSkipEvent");

    [PatchPostfix]
    public static void Postfix(CutsceneSkipEventFromServer __0) => FikaRallyGate.SkipApplied(__0);
}
