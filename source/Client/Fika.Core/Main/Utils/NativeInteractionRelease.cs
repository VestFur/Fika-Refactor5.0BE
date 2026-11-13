using System;
using EFT;
using Fika.Core.Main.GameMode;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Releases the native presentation state a ground loot pickup can leave behind.
/// The pickup runs inside EFT.PickUpState (EPlayerState.Pickup) plus the
/// animator's interaction queue. When the driving inventory operation never
/// reports a terminal status that state stays entered, and the player keeps its
/// restrictions: no view rotation, no movement, no crouch and no interaction
/// prompts, while the inventory and firing keep working. Prone temporarily
/// clears it because it forces a state change, and the queued TakeItem
/// interaction then denies every later pickup for the rest of the raid.
/// Every step is guarded: this only restores presentation state and never
/// touches items, addresses or operations, so loot cannot be duplicated or
/// rolled back by it.
/// </summary>
internal static class NativeInteractionRelease
{
    internal static void Release(Player player, string reason)
    {
        if (player == null)
        {
            return;
        }

        try
        {
            var movementContext = player.MovementContext;
            if (movementContext == null)
            {
                return;
            }

            // Only touch anything when the player really is stuck in a pickup or
            // interaction state. A healthy operation failure must not cancel a
            // legitimate reload, door, examine or throw animation.
            var stuckState = movementContext.CurrentState is MovementState currentState
                && currentState.Name is EPlayerState.Pickup or EPlayerState.Loot or EPlayerState.DoorInteraction
                    or EPlayerState.BreachDoor or EPlayerState.Plant;
            var blockedInteractions = false;
            var blockReason = string.Empty;
            try
            {
                var gate = movementContext.CanInteract;
                blockedInteractions = gate != null;
                if (blockedInteractions)
                {
                    blockReason = gate.ToString();
                }
            }
            catch (Exception exception)
            {
                FikaGlobals.LogWarning($"NativeInteractionRelease[{reason}]: CanInteract read failed: {exception.Message}");
            }
            var animatorStuck = false;
            try
            {
                var animatedInteractions = movementContext.PlayerAnimator?.AnimatedInteractions;
                animatorStuck = animatedInteractions != null
                    && (!animatedInteractions.CanInteract || animatedInteractions.IsScheduleDenied);
            }
            catch (Exception exception)
            {
                FikaGlobals.LogWarning($"NativeInteractionRelease[{reason}]: animator state read failed: {exception.Message}");
            }

            if (!stuckState && !blockedInteractions && !animatorStuck)
            {
                return;
            }

            FikaGlobals.LogWarning($"NativeInteractionRelease[{reason}]: releasing the interaction state " +
                $"(state={movementContext.CurrentState?.Name}, blocked={blockedInteractions}, animatorStuck={animatorStuck}, " +
                $"gate={(string.IsNullOrEmpty(blockReason) ? "none" : blockReason)})");

            if (movementContext != null)
            {
                Try(reason, "ForceStopInteractions", () =>
                    movementContext.PlayerAnimator?.AnimatedInteractions?.ForceStopInteractions());
                Try(reason, "StopAnyInteractions", () => movementContext.StopAnyInteractions());
                Try(reason, "state cancel", () =>
                {
                    if (movementContext.CurrentState is MovementState state
                        && state.Name is EPlayerState.Pickup or EPlayerState.Loot or EPlayerState.DoorInteraction
                            or EPlayerState.BreachDoor or EPlayerState.Plant)
                    {
                        state.Cancel();

                        // Cancel() only leaves the state once its own callback finishes;
                        // when that callback was lost the player stays locked forever (no
                        // rotation, no movement, no interaction prompts). Force the state
                        // machine back to Idle, exactly like a pose change does.
                        var states = movementContext.States;
                        if (states != null && movementContext.CurrentState?.Name != EPlayerState.Idle
                            && states.TryGetValue(EPlayerState.Idle, out var idleState) && idleState != null)
                        {
                            var stuckName = movementContext.CurrentState?.Name;
                            Try(reason, "state exit", () => state.Exit(false));
                            movementContext.ProcessStateEnter(idleState);
                            FikaGlobals.LogWarning($"NativeInteractionRelease[{reason}]: forced the movement state " +
                                $"from {stuckName} to Idle");
                        }
                    }
                });
                Try(reason, "SetInteractInHands", () => movementContext.SetInteractInHands(EInteraction.None));
                Try(reason, "PlayerAnimatorSetInteraction", () => movementContext.PlayerAnimatorSetInteraction(false));
                Try(reason, "ToggleBlockInputPlayerRotation", () => movementContext.ToggleBlockInputPlayerRotation(false));
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"NativeInteractionRelease[{reason}]: movement context release failed: {exception.Message}");
        }

        Try(reason, "interaction refresh", () =>
        {
            player.InteractableObject = null;
            player.ForceInteractionsChanged();
        });

        try
        {
            if (FikaSingleton<IFikaGame>.Instance is CoopGame coopGame)
            {
                coopGame.DiagnosticPlayerOwner?.ClearInteractionState();
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"NativeInteractionRelease[{reason}]: owner release failed: {exception.Message}");
        }
    }

    private static void Try(string reason, string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"NativeInteractionRelease[{reason}]: {step} failed: {exception.Message}");
        }
    }
}
