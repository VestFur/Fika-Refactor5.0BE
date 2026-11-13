using System.Reflection;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.TransitController;

/// <summary>
/// TEMPORARY diagnostics for the in-raid transit interaction chain.
/// The two hooks are the native steps that run when the local player confirms a
/// transit point, so the log shows whether the native flow reaches the action at
/// all before the Fika host controller decides what to do. Remove or move behind
/// <see cref="DebugPatchAttribute"/> once the interaction path is verified again.
/// </summary>
public sealed class TransitEnsureConfirmedDiagPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.DeclaredMethod(typeof(EFT.ClientTransitController), "EnsureConfirmed");

    [PatchPrefix]
    private static void Prefix(EFT.ClientTransitController __instance, TransitPoint point, Player player) =>
        Logger.LogWarning($"[TransitDiag] EnsureConfirmed enter controller={__instance?.GetType().Name} point={point?.parameters?.id} player={player?.ProfileId} your={player?.IsYourPlayer}");

    [PatchPostfix]
    private static void Postfix(TransitPoint point, Player player) =>
        Logger.LogWarning($"[TransitDiag] EnsureConfirmed leave point={point?.parameters?.id} player={player?.ProfileId}");
}

/// <summary>
/// Logs the native interaction action that registers the player for a transit
/// transfer. <c>LocalTransitController</c> and <c>NetworkTransitController</c> do
/// not override it, so one hook covers host and client raids.
/// </summary>
public sealed class TransitInteractionActionDiagPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.DeclaredMethod(typeof(EFT.ClientTransitController), "InteractionAction");

    [PatchPrefix]
    private static void Prefix(EFT.ClientTransitController __instance, Player player, int pointId) =>
        Logger.LogWarning($"[TransitDiag] InteractionAction controller={__instance?.GetType().Name} point={pointId} player={player?.ProfileId} your={player?.IsYourPlayer}");
}
