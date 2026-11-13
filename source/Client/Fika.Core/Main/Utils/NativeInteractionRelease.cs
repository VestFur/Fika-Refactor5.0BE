using System;
using EFT;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Patches.World;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 释放地面战利品拾取可能遗留的原生表现状态。
/// 拾取过程运行在 EFT.PickUpState(EPlayerState.Pickup)以及
/// 动画器的交互队列之中。当驱动的库存操作始终
/// 没有报告终止状态时,该状态会一直保持进入态,玩家也就继续受其
/// 限制:不能转视角、不能移动、不能蹲下、没有交互
/// 提示,而库存与开火仍然可用。趴下会暂时
/// 清除它,因为趴下强制触发状态切换,而排队的 TakeItem
/// 交互会在本场 raid 的剩余时间里拒绝之后每一次拾取。
/// 每一步都有守卫:这里只恢复表现状态,绝不
/// 触碰物品、地址或操作,因此无法借它复制或回滚
/// 战利品。
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

            // 只有在玩家确实卡在拾取或交互状态中时才做任何处理。
            // 一次正常的操作失败不得取消合法的
            // 换弹、开门、检视或投掷动画。
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

            // [LootDiag](analysis/pickup-stuck-again):这一行就是「检测到卡住的拾取
            // 并已恢复」。与拾取请求行和操作结果行一起阅读
            // 即可判断恢复是否释放了一次仍在进行中的
            // 拿取——而这正是被报告的那个「它恢复了,但我仍然
            // 拿不起这件物品」。
            // analysis/pickup-auto-fallback (Z4):现在还同时记录本机所处的拾取
            // 路径(native / bypass(cfg) / bypass(auto))以及它切换过多少次,因此一次 grep
            // 就能回答「自动切换是否发生过,以及它释放了哪一次拿取」。
            GroundLootPickup.NoteRelease(reason);
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

                        // Cancel() 只有在其自身回调结束后才会离开该状态;
                        // 一旦那个回调丢失,玩家就会永久被锁住(不能
                        // 转视角、不能移动、没有交互提示)。把状态机
                        // 强制拉回 Idle,完全和一次姿态切换所做的一样。
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

    /// <summary>
    /// [InvDiag](2026-10-27, T3)轮询两个其他任何地方都不轮询的交互锁定信号。
    /// <para>
    /// <see cref="Release(Player, string)"/> 把三种信号视为「玩家卡住」:拾取/战利品/门/种植的
    /// 移动状态、<c>MovementContext.CanInteract</c> 被持续占用,以及动画器的交互调度被拒绝
    /// (<c>AnimatedInteractions.CanInteract</c> 为 false 或 <c>IsScheduleDenied</c>)。其中只有第一种会被
    /// 定时轮询(<c>CoopGame.RepairLocalInteractionState</c>,且仅针对 Pickup/Plant),而
    /// <c>CoopGame.RepairLocalInventoryState</c> 把「阻塞」定义为 <c>ItemController.Locked</c> /
    /// <c>IsChangingWeapon</c>——这两个都不是。于是一次地面战利品拿取如果其库存操作从未
    /// 得到应答,就可能让动画器队列在玩家其余方面空闲时仍被拒绝:交互提示不再出现,
    /// 之后每次拿取都被原生交互门拒绝,而且没有任何看门狗调用过 Release。
    /// 这就是被报告的那个「客户端在拿起一件物品之后就锁死」。
    /// </para>
    /// <para>
    /// 只在移动状态为 Idle 时运行:这两个信号各自的合法持有者都是某个
    /// 自身的移动状态(门、靠近、破门、静止、拾取、种植),因此一个处于空闲却仍置着其中之一的
    /// 玩家没有任何持有者。预算用的是与拾取预算相同的推理——原生的拿取会在
    /// 两秒内很好地完成其动画,因此空闲状态下八秒的拒绝调度并不属于正在进行的交互。
    /// </para>
    /// </summary>
    internal static void WatchLostInteractionSchedule(Player player, int pendingOperations, float now)
    {
        if (player == null)
        {
            ResetLostScheduleWatch();
            return;
        }

        if (_lostSchedulePlayer != player.Pointer)
        {
            _lostSchedulePlayer = player.Pointer;
            _lostScheduleSince = 0f;
        }

        var movementContext = player.MovementContext;
        var currentState = movementContext?.CurrentState;
        if (currentState is not MovementState state)
        {
            _lostScheduleSince = 0f;
            return;
        }

        // 这两个信号各自的合法持有者都是某个自身的移动状态,因此一个处于 Idle 却仍
        // 置着其中之一的玩家没有任何持有者。
        if (state.Name != EPlayerState.Idle)
        {
            _lostScheduleSince = 0f;
            return;
        }

        // 可用物品(药品、食物、手术包、测距仪)会在移动状态保持 Idle 期间通过手部控制器
        // 运行自己的动画,并会合法地拒绝交互调度许多秒。Release()
        // 会取消它,因此该守视从不触碰这些手部状态——这与库存门
        // 看门狗所做的排除相同。正在进行的手部切换属于手部看门狗的事,不归这里管。
        try
        {
            var hands = player.HandsController;
            var usableInHands = hands != null
                && (hands.TryCast<Player.UsableItemController>() != null
                    || hands.TryCast<Player.MedsController>() != null);
            if (usableInHands || player.ProcessStatus != Player.EProcessStatus.None)
            {
                _lostScheduleSince = 0f;
                return;
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[InvDiag] hands state read failed, not watching the interaction schedule: {exception.Message}");
            _lostScheduleSince = 0f;
            return;
        }

        var gateHeld = false;
        try
        {
            gateHeld = movementContext.CanInteract != null;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[InvDiag] CanInteract read failed, not watching the interaction schedule: {exception.Message}");
            _lostScheduleSince = 0f;
            return;
        }

        var scheduleDenied = false;
        try
        {
            var animatedInteractions = movementContext.PlayerAnimator?.AnimatedInteractions;
            scheduleDenied = animatedInteractions != null
                && (!animatedInteractions.CanInteract || animatedInteractions.IsScheduleDenied);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[InvDiag] animator state read failed, not watching the interaction schedule: {exception.Message}");
            _lostScheduleSince = 0f;
            return;
        }

        if (!gateHeld && !scheduleDenied)
        {
            _lostScheduleSince = 0f;
            return;
        }

        if (_lostScheduleSince == 0f)
        {
            _lostScheduleSince = now;
            return;
        }

        var heldFor = now - _lostScheduleSince;
        if (heldFor < LostScheduleBudgetSeconds)
        {
            return;
        }

        // 保留时间戳,使未能奏效的释放按同样的预算之后再跑一次,而不是每帧都跑。
        _lostScheduleSince = now;
        FikaGlobals.LogWarning($"[InvDiag] lost-interaction-schedule: idle for {heldFor:0.0}s with " +
            $"gate={(gateHeld ? "held" : "open")}, animatorDenied={scheduleDenied}, " +
            $"pendingOperations={pendingOperations} - releasing the presentation state");
        Release(player, "lost-interaction-schedule");
    }

    /// <summary>[InvDiag] 调度/预算永远只属于一场 raid 中的一名本地玩家。</summary>
    internal static void ResetLostScheduleWatch()
    {
        _lostSchedulePlayer = IntPtr.Zero;
        _lostScheduleSince = 0f;
    }

    private const float LostScheduleBudgetSeconds = 8f;
    private static IntPtr _lostSchedulePlayer;
    private static float _lostScheduleSince;

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
