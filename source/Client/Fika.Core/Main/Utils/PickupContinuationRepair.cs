using System;
using EFT;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 卡住的 EFT.PickUpState 会保留原生续接（MovementContext.PickupAction）：拾取动画本应在其
/// take-item 动画事件触发时调用它。若该事件丢失，续接虽被保存却从未被调用：不会创建任何
/// 库存操作，物品留在原地，而该状态会一直保持为已进入状态，直到 CoopGame 的 5 秒交互
/// 看门狗将其释放。
/// 在那次释放之前，把保存的续接调用一次。
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

            // 先解除引用：迟到的动画事件不得让同一个续接被执行两次。
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
