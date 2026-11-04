using System;
using EFT;

namespace Fika.Core.Main.Utils;

/// <summary>
/// A stuck EFT.PickUpState keeps the native continuation (MovementContext.PickupAction) that the
/// pickup animation should invoke when its take-item animation event fires. If that event is lost,
/// the continuation is saved but never invoked: no inventory operation is created, the item stays on
/// the ground and the state remains entered until CoopGame's 5 s interaction watchdog releases it.
/// Invoke the saved continuation once, right before that release.
/// </summary>
internal static class PickupContinuationRepair
{
    private static IntPtr _lastPlayer;
    private static float _lastRepairAt;
    private static int _repairCount;

    internal static void TryRepair(Player player, MovementContext movementContext, EPlayerState stuckState,
        float heldForSeconds)
    {
        try
        {
            if (player == null || movementContext == null || stuckState != EPlayerState.Pickup)
            {
                return;
            }

            var action = movementContext.PickupAction;
            if (action == null || action.Pointer == IntPtr.Zero)
            {
                FikaGlobals.LogWarning($"[LootFix] stuck Pickup has no saved continuation " +
                    $"(player={player.PlayerId}, held={heldForSeconds:0.0}s)", "PickupRepair");
                return;
            }

            var now = UnityEngine.Time.unscaledTime;
            if (player.Pointer == _lastPlayer && now - _lastRepairAt < 1f)
            {
                return;
            }

            _lastPlayer = player.Pointer;
            _lastRepairAt = now;
            _repairCount++;

            // Detach first: a late animation event must not run the same continuation twice.
            movementContext.PickupAction = null;
            FikaGlobals.LogWarning($"[LootFix] found the lost pickup continuation " +
                $"(player={player.PlayerId}, held={heldForSeconds:0.0}s, repairs={_repairCount}); invoking it",
                "PickupRepair");
            try
            {
                action.Invoke();
                FikaGlobals.LogWarning($"[LootFix] pickup continuation invoked (repairs={_repairCount})",
                    "PickupRepair");
            }
            catch (Exception exception)
            {
                FikaGlobals.LogError($"[LootFix] pickup continuation threw: {exception}", "PickupRepair");
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[LootFix] pickup repair failed: " +
                $"{exception.GetType().Name}: {exception.Message}", "PickupRepair");
        }
    }
}
