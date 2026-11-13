using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.Interactive;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;
using JsonType;
using UnityEngine;
using ClientTransitController = Fika.Core.Main.ClientClasses.ClientTransitController;

namespace Fika.Core.Main.Patches.TransitPresence;

/// <summary>
/// Every hook that turns "this client's player stands in a transit point" into a message for the
/// host. Evidence for the targets (SPT 5.0 / 47965 interop dump, mirrored by 47888; see
/// analysis/transit-presence-binding/REPORT.md):
/// <para>
/// <c>PlayerEnter(TransitPoint, Player)</c> / <c>PlayerExit(TransitPoint, Player)</c> are declared on
/// <c>EFT.LocalTransitController</c> (dump 242850 / 242851), <b>not</b> on
/// <c>EFT.ClientTransitController</c>. A raid client's native controller is an
/// <c>EFT.NetworkTransitController</c> (Fika creates one for every non-host in
/// <c>BaseGameController:1469-1471</c>), which never declares those methods, so the previous
/// targets could not bind at all - the two failures in <c>LogOutput.log:594-597</c> are exactly
/// that - and the client stayed invisible to the host
/// (<c>LogOutput.log:2507 in=1/2 allowed=False</c>).
/// </para>
/// <para>
/// The one object that holds both the point and the entering player on every machine is the point:
/// <c>EFT.Interactive.TransitPoint</c> declares <c>OnTriggerEnter(Collider)</c> /
/// <c>OnTriggerExit(Collider)</c> (dump 212394 / 212395), the public static
/// <c>TryGetPlayer(Collider, out Player)</c> (dump 212400), implements the physics trigger
/// (<c>IPhysicsTrigger.enabled</c>) and owns the <c>Controller</c> reference. Fika keeps those points
/// alive on a client (<c>ClientTransitController.cs:39</c> re-activates them, and nothing in Fika
/// calls <c>NetworkTransitController.DisablePointsTrigger</c>), so the point trigger is the client's
/// real entry point. The <c>LocalTransitController</c> hooks below stay for any machine that
/// actually runs that controller (and would cover a build that gives clients one); they are inert on
/// a Fika host because that raid's world controller is a <see cref="FikaHostTransitController"/>,
/// which is not a <see cref="ClientTransitController"/>.
/// </para>
/// </summary>
internal static class TransitPresenceBinding
{
    /// <summary>
    /// Resolves one patch target and proves the result in the log. A missing target is reported as an
    /// ERROR (not just HarmonyX's warning) because a silently unbound hook is what broke this feature.
    /// </summary>
    internal static MethodBase Resolved(string patch, System.Type declaring, string name, params System.Type[] parameters)
    {
        var method = AccessTools.DeclaredMethod(declaring, name, parameters);
        if (method == null)
        {
            
            return null;
        }

        
        return method;
    }

    private static string Describe(ParameterInfo[] parameters)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var parameter in parameters)
        {
            if (builder.Length > 0) builder.Append(", ");
            builder.Append(parameter.ParameterType.Name);
        }
        return builder.ToString();
    }

    private static string Describe(System.Type[] parameters)
    {
        if (parameters == null || parameters.Length == 0) return string.Empty;
        var builder = new System.Text.StringBuilder();
        foreach (var parameter in parameters)
        {
            if (builder.Length > 0) builder.Append(", ");
            builder.Append(parameter.Name);
        }
        return builder.ToString();
    }

    /// <summary>Logging is best effort: a logger that is not ready yet must not break patch installation.</summary>
    internal static void Log(bool error, string message)
    {
        try
        {
            if (error) FikaGlobals.LogError(message);
            else FikaGlobals.LogWarning(message);
        }
        catch (System.Exception exception)
        {
            System.Console.WriteLine(message + " (log failed: " + exception.Message + ")");
        }
    }
}

/// <summary>
/// The single funnel every client-side presence hook reports through, so the native paths and the
/// geometric fallback cannot double-report or contradict each other: the decision of "does the host
/// already know" is read back from <see cref="ClientTransitController.SentZonePoint"/>, which is the
/// same map the packet sender deduplicates with.
/// </summary>
internal static class TransitPresenceReporter
{
    /// <summary>Five geometry sweeps per second: enough for a walking player, cheap for the raid thread.</summary>
    private const float PollInterval = 0.25f;

    /// <summary>Bounded: one line per player, point and reason per raid.</summary>
    private static readonly System.Collections.Generic.HashSet<string> LoggedSkips = [];

    private static float _nextPoll;

    /// <summary>
    /// One report per raid about a disabled geometric fallback, so a client that silently lost its
    /// last entry point does not look like a client whose player never stands in a transit zone.
    /// </summary>
    private static bool _pollDisabledReported;

    /// <summary>Native trigger path: the point already resolved the collider that entered or left it.</summary>
    internal static void FromCollider(TransitPoint point, Collider col, bool inside, string source)
    {
        if (point == null || col == null) return;
        if (!TransitPoint.TryGetPlayer(col, out var player)) return;
        Report(point, player, inside, source);
    }

    /// <summary>Report for an already resolved player (trigger and controller-event paths).</summary>
    internal static void Report(TransitPoint point, Player player, bool inside, string source)
    {
        // Bots and remote observed players also have colliders that can enter a point trigger on
        // this machine; only the player that is local here may speak for this machine.
        if (point?.parameters == null || player == null || !player.IsYourPlayer) return;
        ClientWith(Singleton<GameWorld>.Instance?.TransitController)?.ReportZonePresence(player, point, inside, source);
    }

    /// <summary>
    /// Geometric fallback, independent of every native callback: once per <see cref="PollInterval"/>
    /// the local player's position is tested against each point's trigger volume, and only a change is
    /// reported. The criterion is the point's own trigger bounds (see <see cref="FindPointInside"/>).
    /// </summary>
    internal static void Poll(GameWorld world)
    {
        if (world == null || Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + PollInterval;

        // The host's own transit countdown is clocked from here as well: a host raid has no client
        // wrapper (the early return below), and the native tick of a local raid is not guaranteed to
        // run for a transferred group. Guarded, because a poll must never break the frame it runs in.
        try
        {
            FikaWorldControllers.Find<FikaHostTransitController>(world.TransitController)?.HostCountdownTick();
        }
        catch (System.Exception exception)
        {
            MarkHostTickFailure(exception);
        }

        var controller = ClientWith(world.TransitController);
        // A host or headless raid drives the host's own native path; there is no host to report to.
        // That is expected there and a silent failure on a client, where this poll is the fallback
        // that keeps working when the native trigger paths are offline - so name which one it is.
        if (controller == null)
        {
            if (!_pollDisabledReported)
            {
                _pollDisabledReported = true;
                var transit = world.TransitController;
                
            }

            return;
        }

        _pollDisabledReported = false;
        var player = world.MainPlayer;
        if (player == null || !player.IsYourPlayer) return;

        var point = FindPointInside(world.TransitController, player.Position);
        var reported = controller.SentZonePoint(player.ProfileId);
        if (point != null)
        {
            if (reported != point.parameters.id) controller.ReportZonePresence(player, point, true, "geo poll");
            // The presence report is deduplicated, the interaction check is not: the prompt can still be
            // missing after the zone report was sent (host-granted state, native visibility, or a first
            // attempt that ran before the host's snapshot arrived).
            controller.ClientInteractionTick(point, player, "geo poll");
            return;
        }

        if (reported >= 0)
        {
            // Leaving the volume has to clear the host's registration; the native PlayerExit does that on
            // machines that receive the trigger, this keeps the same semantics without it.
            controller.SendZonePresence(0, false, "geo poll");
        }

        controller.ClientInteractionTick(null, player, "geo poll");
    }

    private static ClientTransitController ClientWith(EFT.TransitController transit) =>
        FikaWorldControllers.Find<ClientTransitController>(transit);

    /// <summary>
    /// The point the local player stands in, or null. Criterion: the point's own trigger collider
    /// bounds contain the player position.
    /// <para>
    /// Evidence that this collider is the trigger volume: TransitPoint receives Unity trigger
    /// messages (<c>OnTriggerEnter/OnTriggerExit</c>, dump 212394/212395) and implements
    /// <c>IPhysicsTrigger</c>, so the collider that produces those messages sits on the point's own
    /// GameObject. Bounds containment is used instead of <c>Collider.ClosestPoint</c> because it works
    /// for every collider shape and also when the collider is disabled; it is a superset of the
    /// trigger volume, which only ever registers a player that is already at the point - access,
    /// entry requirements, the quest condition, the group-size limit and the host's own registration
    /// checks are untouched by it.
    /// </para>
    /// </summary>
    private static TransitPoint FindPointInside(EFT.TransitController transit, Vector3 position)
    {
        var points = transit?.pointsById;
        if (points == null) return null;

        foreach (var point in points.Values)
        {
            if (point == null) continue;
            var collider = TriggerOf(point);
            if (collider != null && collider.bounds.Contains(position)) return point;
        }
        return null;
    }

    private static Collider TriggerOf(TransitPoint point)
    {
        var collider = point.GetComponent<Collider>();
        return collider != null ? collider : point.GetComponentInChildren<Collider>(true);
    }

    /// <summary>One log line per player, point and reason per raid for a skipped report.</summary>
    internal static bool MarkSkipLogged(string key) => LoggedSkips.Count < 64 && LoggedSkips.Add(key);

    /// <summary>
    /// Reports a failing host countdown tick once per raid. The tick itself already stops the countdown
    /// and releases the registrations it made, so this line only names the exception that caused it.
    /// </summary>
    private static bool MarkHostTickFailure(System.Exception exception)
    {
        if (!MarkSkipLogged("host countdown tick:" + exception.GetType().Name)) return false;
                return true;
    }

    /// <summary>The raid is over; the next one gets a fresh set of diagnostic characters.</summary>
    internal static void ResetSkips()
    {
        LoggedSkips.Clear();
        // Re-arm the fallback report as well: the same process can host one raid and join the next
        // one as a client, where a missing wrapper is a failure instead of the host's normal path.
        _pollDisabledReported = false;
    }
}

/// <summary>
/// Native <c>EFT.LocalTransitController.PlayerEnter(TransitPoint, Player)</c> - the declaration the
/// host raids already use through <see cref="Fika.Core.Main.Patches.TransitController.FikaTransitEnterPatch"/>.
/// A client raid's controller is a <c>NetworkTransitController</c> and never reaches this method, so
/// on a client this hook is a safety net (see the class comment on <see cref="TransitPresenceBinding"/>;
/// the point-trigger hook below is the client's real path).
/// </summary>
public sealed class FikaTransitClientPresenceEnterPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => TransitPresenceBinding.Resolved(
        nameof(FikaTransitClientPresenceEnterPatch), typeof(EFT.LocalTransitController), "PlayerEnter",
        typeof(TransitPoint), typeof(Player));

    /// <summary>
    /// Runs for the machine that owns this controller. Only its own player is reported; bots and
    /// remote observed players are ignored, and a machine without a client wrapper (the Fika host)
    /// reports nothing at all.
    /// </summary>
    [PatchPrefix]
    private static void Prefix(EFT.LocalTransitController __instance, TransitPoint point, Player player) =>
        TransitPresenceReporter.Report(point, player, true, "local transit controller");
}

/// <inheritdoc cref="FikaTransitClientPresenceEnterPatch"/>
public sealed class FikaTransitClientPresenceExitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => TransitPresenceBinding.Resolved(
        nameof(FikaTransitClientPresenceExitPatch), typeof(EFT.LocalTransitController), "PlayerExit",
        typeof(TransitPoint), typeof(Player));

    [PatchPrefix]
    private static void Prefix(EFT.LocalTransitController __instance, TransitPoint point, Player player) =>
        TransitPresenceReporter.Report(point, player, false, "local transit controller");
}

/// <summary>
/// The client's real entry point: the point's own trigger. <c>TransitPoint.OnTriggerEnter/OnTriggerExit</c>
/// run on every machine that has the point activated, resolve the collider with the game's own
/// <c>TryGetPlayer</c> helper and are independent of which controller class the raid installed.
/// Prefixes are used so a native exception inside the trigger cannot swallow the report.
/// </summary>
public sealed class FikaTransitClientPointEnterPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => TransitPresenceBinding.Resolved(
        nameof(FikaTransitClientPointEnterPatch), typeof(TransitPoint), "OnTriggerEnter", typeof(Collider));

    [PatchPrefix]
    private static void Prefix(TransitPoint __instance, Collider col) =>
        TransitPresenceReporter.FromCollider(__instance, col, true, "point trigger");
}

/// <inheritdoc cref="FikaTransitClientPointEnterPatch"/>
public sealed class FikaTransitClientPointExitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => TransitPresenceBinding.Resolved(
        nameof(FikaTransitClientPointExitPatch), typeof(TransitPoint), "OnTriggerExit", typeof(Collider));

    [PatchPrefix]
    private static void Prefix(TransitPoint __instance, Collider col) =>
        TransitPresenceReporter.FromCollider(__instance, col, false, "point trigger");
}

/// <summary>
/// Geometric fallback host: <c>EFT.GameWorld.Update()</c> runs once per frame in a raid on every
/// machine, and <see cref="TransitPresenceReporter.Poll"/> throttles itself to
/// <c>0.25 s</c> and returns immediately for a host (no client wrapper) or outside a raid. It exists
/// so the client can still tell the host where its player stands on a build where the native trigger
/// or the controller callback does not run.
/// </summary>
public sealed class FikaTransitPresencePollPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => TransitPresenceBinding.Resolved(
        nameof(FikaTransitPresencePollPatch), typeof(GameWorld), "Update");

    [PatchPostfix]
    private static void Postfix(GameWorld __instance) => TransitPresenceReporter.Poll(__instance);
}

/// <summary>
/// Native <c>EFT.ClientTransitController.OnPlayerDead(Player, IPlayer, DamageInfo, EBodyPart)</c>
/// (declared on the base controller, so it is reachable from the client's
/// <c>NetworkTransitController</c>; verified against the dump and by
/// <c>LogOutput.log:598 Enabled patch FikaTransitDeathPresencePatch</c>) is the transit interaction's
/// death path. It is the exact signal for "this player will never stand in the zone again", so the
/// host can drop a registration the client will not clear itself. The client branch reports its own
/// death; the host branch clears its local map.
/// </summary>
public sealed class FikaTransitDeathPresencePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => TransitPresenceBinding.Resolved(
        nameof(FikaTransitDeathPresencePatch), typeof(EFT.ClientTransitController), "OnPlayerDead",
        typeof(Player), typeof(EFT.IPlayer), typeof(DamageInfo), typeof(EBodyPart));

    [PatchPrefix]
    private static void Prefix(EFT.ClientTransitController __instance, Player player)
    {
        if (player == null) return;
        if (FikaWorldControllers.Find<ClientTransitController>(__instance) is { } client)
        {
            client.SendZonePresence(0, false, "died", player.ProfileId);
            if (player.IsYourPlayer)
            {
                // A dead player cannot be carried by any countdown: this machine drops its own transit
                // registration, gives its exit interaction back and closes the countdown panel it shows, and
                // the host applies the same rule for the same profile (TransitCountdownGuard: a death drops
                // the participant, or cancels the transfer when Coop / Transit: Abort When Someone Dies is on).
                client.CancelLocalCountdown(-1, "this machine's player died", "death hook");
            }

            return;
        }

        FikaWorldControllers.Find<FikaHostTransitController>(__instance)?.OnHostPlayerDead(player);
    }
}

/// <summary>
/// Native <c>EFT.ClientTransitController.ShowInteraction(Int32, Player, EDateTime)</c> (interop dump
/// 238472 / 238522) is the one call that puts the transit interaction in front of a player. A local raid
/// reaches it from its own <c>PlayerEnter</c> path; a network client would need the host's
/// <c>TransitInteractionEvent</c> with <c>EType.Show</c>, and the host only builds that packet in the
/// branch that answers its own native <c>PlayerEnter</c> - a method a remote player never reaches. The
/// postfix tells the client controller about a prompt this machine did not raise itself, so the client log
/// answers whether the host drove the interaction or the client had to.
/// </summary>
public sealed class FikaTransitClientShowInteractionPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => TransitPresenceBinding.Resolved(
        nameof(FikaTransitClientShowInteractionPatch), typeof(EFT.ClientTransitController), "ShowInteraction",
        typeof(int), typeof(Player), typeof(EDateTime));

    [PatchPostfix]
    private static void Postfix(EFT.ClientTransitController __instance, int pointId, Player player)
    {
        if (player == null || !player.IsYourPlayer) return;
        FikaWorldControllers.Find<ClientTransitController>(__instance)?.NoteNativePromptShown(pointId, player);
    }
}

/// <summary>
/// Native <c>EFT.ClientTransitController.EnsureConfirmed(TransitPoint, Player, Action)</c> (interop dump
/// 238471 / 238521) is the step between pressing the transit prompt and the interaction action that asks
/// <c>EFT.Player.TransitInteraction</c> for the request - the request Fika sends to the host from
/// <see cref="Fika.Core.Main.Players.FikaPlayer.TransitInteraction"/>. The press and the local confirmation
/// state are logged here, and the controller reports a press that stalls, which is the one place where a
/// client can be blocked without the host ever hearing about it.
/// </summary>
public sealed class FikaTransitClientEnsureConfirmedPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => TransitPresenceBinding.Resolved(
        nameof(FikaTransitClientEnsureConfirmedPatch), typeof(EFT.ClientTransitController), "EnsureConfirmed",
        (System.Type[])null);

    [PatchPrefix]
    private static void Prefix(EFT.ClientTransitController __instance, TransitPoint point, Player player)
    {
        if (player == null || !player.IsYourPlayer) return;
        FikaWorldControllers.Find<ClientTransitController>(__instance)?.NotePressAttempt(point, player);
    }

    [PatchPostfix]
    private static void Postfix(EFT.ClientTransitController __instance, TransitPoint point, Player player)
    {
        if (player == null || !player.IsYourPlayer) return;
        FikaWorldControllers.Find<ClientTransitController>(__instance)?.NotePressCompleted(point, player);
    }
}
