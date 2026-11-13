using System;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using Diz.LanguageExtensions;
using Diz.Utils;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.World;

/// <summary>
/// 客户端侧的地面物资拾取，跳过了原生交互状态机。
/// 原生拾取运行在 EFT.PickUpState 内部，一旦其完成回调丢失，
/// 玩家就会被锁在该状态中：移动状态机拒绝
/// 视角旋转、移动和蹲下，而 MovementContext.CanInteract 回答
/// “Can't interact in Pickup”，于是不出现交互提示，地面物资在
/// 本场战局余下的时间里再也无法被拾取。
/// 拦截 InteractionContextHelper.PickupAction 让我们可以执行与
/// 启动自检所使用的那套相同的背包操作（ItemManipulator 放置 +
/// ConvertOperationResultToOperation + Execute），并跳过进入该状态。
/// 主机始终保持权威：该操作仍然经由
/// ClientInventoryController 传输，因此战利品无法在本地被复制。
/// </summary>
internal sealed class InteractionContextHelper_PickupAction_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(InteractionContextHelper)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(x => x.Name == "PickupAction"
                && x.GetParameters().Length == 5
                && x.GetParameters()[0].ParameterType == typeof(Player)
                && x.GetParameters()[2].ParameterType == typeof(Item));
        if (target == null)
        {
            throw new MissingMethodException(
                "InteractionContextHelper.PickupAction(Player, IItemOperationResult, Item, IPlayer, Action)");
        }
        return target;
    }

    [PatchPrefix]
    public static bool Prefix(Player __0, Item __2)
    {
        // 2026-10-12（analysis/pickup-auto-fallback）：这里原本只对客户端生效，这使得
        // 下面的开关在主机上无法到达——而那场卡在
        // EFT.PickUpState 的第 12 轮战局就跑在主机上（该战局的日志中完全没有 [LootDiag]
        // 拾取请求行，因为这个方法在第一行之前就返回了）。
        // 现在的条件是“Fika 战局中本机自己的玩家”，主机或客户端都算。
        if (__0 == null || __2 == null || !GroundLootPickup.IsFikaLocalPlayer(__0))
        {
            return true;
        }

        // 2026-10-12（analysis/pickup-stuck-again + analysis/pickup-auto-fallback）：原生
        // 动作和其它背包动作一样经由背包控制器转发一次拾取，
        // 但它是在 EFT.PickUpState 内部这么做的，而该状态可能一直保持到
        // 战局结束。默认关闭；三种模式（见 GroundLootPickup）：
        //   native       - 运行原生动作（默认）
        //   bypass(cfg)  - Fika.cfg -> [Gameplay] -> "Ground Loot Pickup Bypass" = true
        //   bypass(auto) - 本场战局检测到过卡住的拾取，因此拾取现在会跳过
        //                  原生拾取状态（Fika.cfg -> [Gameplay] ->
        //                  "Ground Loot Pickup Auto Fallback" = false 会关闭该开关）
        if (!GroundLootPickup.BypassEnabled)
        {
            GroundLootPickup.DiagNativeTake(__0, __2);
            return true;
        }

        try
        {
            if (GroundLootPickup.TryTake(__0, __2))
            {
                return false;
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[PickupBypass] failed for {__2.Id}: {exception}");
        }

        return true;
    }
}

/// <summary>
/// 把地面物资放入对应的装备槽位。护甲、头盔、战术背心、背包和
/// 耳机只能放在装备槽位里，而通用的容器放置从不提供这种槽位，
/// 所以这些物品以前会被留在地上。使用经过验证的原生路径：
/// InventoryEquipment.GetSlot + Slot.CreateItemAddress + ItemManipulator.Move（dry run）+
/// ConvertOperationResultToOperation + Execute，因此主机始终保持权威。
/// </summary>
internal static class GroundLootEquip
{
    private static readonly EquipmentSlot[] CandidateSlots =
    {
        EquipmentSlot.Headwear, EquipmentSlot.Earpiece, EquipmentSlot.ArmorVest,
        EquipmentSlot.TacticalVest, EquipmentSlot.Backpack
    };

    internal static bool TryEquipFromGround(Player player, Item item, out string error)
    {
        error = null;
        if (player == null || item == null)
        {
            error = "no player or item";
            return false;
        }

        var controller = player.InventoryController;
        var equipment = player.Equipment;
        if (controller == null || equipment == null)
        {
            error = "no inventory controller or equipment";
            return false;
        }

        var preferred = PreferredSlot(item);
        var ordered = new System.Collections.Generic.List<EquipmentSlot>();
        if (preferred != null)
        {
            ordered.Add(preferred.Value);
        }
        foreach (var candidate in CandidateSlots)
        {
            if (preferred == null || candidate != preferred.Value)
            {
                ordered.Add(candidate);
            }
        }

        var reasons = new System.Collections.Generic.List<string>();
        foreach (var slotName in ordered)
        {
            Slot slot;
            try
            {
                slot = equipment.GetSlot(slotName);
            }
            catch (Exception slotException)
            {
                reasons.Add(slotName + ": lookup failed (" + slotException.Message + ")");
                continue;
            }
            if (slot == null)
            {
                continue;
            }
            if (slot.ContainedItem != null)
            {
                continue;
            }
            if (slot.Locked)
            {
                reasons.Add(slotName + ": locked");
                continue;
            }
            bool compatible;
            try
            {
                compatible = slot.CheckCompatibility(item);
            }
            catch (Exception compatibilityException)
            {
                reasons.Add(slotName + ": compatibility check failed (" + compatibilityException.Message + ")");
                continue;
            }
            if (!compatible)
            {
                continue;
            }

            try
            {
                var placement = ItemManipulator.Move(item, slot.CreateItemAddress(), controller, true);
                if (placement.Failed)
                {
                    reasons.Add(slotName + ": " + (placement.Error == null ? "rejected" : placement.Error.ToString()));
                    continue;
                }

                var operation = controller.ConvertOperationResultToOperation(placement.Value);
                if (operation == null)
                {
                    reasons.Add(slotName + ": no operation from the placement result");
                    continue;
                }

                controller.Execute(operation, (Callback)(result =>
                {
                    try
                    {
                        if (result?.Failed == true)
                        {
                            FikaGlobals.LogWarning($"[PickupBypass] equipping {item.Id} into {slotName} failed: {result.Error}");
                        }
                        player.ForceInteractionsChanged();
                    }
                    catch (Exception callbackException)
                    {
                        FikaGlobals.LogError($"[PickupBypass] equipment pickup callback failed: {callbackException}");
                    }
                }));
                return true;
            }
            catch (Exception moveException)
            {
                reasons.Add(slotName + ": " + moveException.Message);
            }
        }

        error = reasons.Count == 0 ? "no equipment slot accepted the item" : string.Join("; ", reasons);
        return false;
    }

    private static EquipmentSlot? PreferredSlot(Item item)
    {
        try
        {
            if (item.TryCast<Armor>() != null) return EquipmentSlot.ArmorVest;
            if (item.TryCast<Headwear>() != null) return EquipmentSlot.Headwear;
            if (item.TryCast<Vest>() != null) return EquipmentSlot.TacticalVest;
            if (item.TryCast<Backpack>() != null) return EquipmentSlot.Backpack;
            if (item.TryCast<Headphones>() != null) return EquipmentSlot.Earpiece;
        }
        catch
        {
            // 类型探测绝不能破坏拾取流程。
        }
        return null;
    }
}

/// <summary>旁路补丁所使用的直接地面物资拾取。</summary>
internal static class GroundLootPickup
{
    /// <summary>
    /// Fika 是否仍然旁路原生的地面物资拾取。自 2026-10-12 起默认关闭
    /// （analysis/pickup-stuck-again）：旁路跳过了原生拾取状态并手工构造背包操作，
    /// 而物品无法放置的每一条路径都会返回 true——交互被消耗、物品留在地上，
    /// 而且没有任何东西告诉玩家原因。原生动作经由
    /// ClientInventoryController 到达同一个以主机为权威的
    /// 路径。
    /// <para>
    /// 翻一个开关即可回到之前的行为，无需重新编译：
    /// Fika.cfg -> [Gameplay] -> "Ground Loot Pickup Bypass" = true。
    /// </para>
    /// </summary>
    internal static bool BypassEnabled
    {
        get
        {
            return CfgBypassEnabled || _autoSwitched;
        }
    }

    /// <summary>手动开关。Fika.cfg -> [Gameplay] -> "Ground Loot Pickup Bypass"。</summary>
    private static bool CfgBypassEnabled
    {
        get
        {
            try
            {
                var settings = FikaPlugin.Instance?.Settings;
                return settings?.GroundLootPickupBypass != null && settings.GroundLootPickupBypass.Value;
            }
            catch
            {
                // 缺失的插件或设置对象绝不能决定一次拾取：回退到
                // 原生路径，毕竟那本来就是默认。
                return false;
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // 拾取路径开关（analysis/pickup-auto-fallback, Z4）
    // ---------------------------------------------------------------------------------------
    // native       : 默认。地面物资拾取运行在 EFT.PickUpState（EPlayerState.Pickup）内部。
    // bypass(cfg)  : "Ground Loot Pickup Bypass" = true。每次拾取都走旁路。
    // bypass(auto) : 本场战局观测到过真实的卡住拾取（NoteStuckPickup），因此拾取
    //                在本场战局余下的时间里跳过 EFT.PickUpState。不写 cfg、不重启：
    //                每次拾取都会重新读取 BypassEnabled。
    internal const string ModeNative = "native";
    internal const string ModeBypassCfg = "bypass(cfg)";
    internal const string ModeBypassAuto = "bypass(auto)";

    private static bool _autoSwitched;
    private static int _switchCount;
    private static string _lastReleaseReason = "none";
    private static float _nextModeDiagTime;
    private static IntPtr _modePlayerPointer;

    /// <summary>
    /// 检测到的卡住拾取是否可以把本机切换到旁路（默认开启）。
    /// 一行 cfg 即可永久关闭自动切换，无需重新编译：
    /// Fika.cfg -> [Gameplay] -> "Ground Loot Pickup Auto Fallback" = false。
    /// </summary>
    internal static bool AutoFallbackEnabled
    {
        get
        {
            try
            {
                var settings = FikaPlugin.Instance?.Settings;
                return settings?.GroundLootPickupAutoFallback == null
                    || settings.GroundLootPickupAutoFallback.Value;
            }
            catch
            {
                // 该开关是安全网而非玩法特性：当设置对象
                // 无法读取时，保留自动恢复能力可用。
                return true;
            }
        }
    }

    /// <summary>本机此刻使用的拾取路径：native、bypass(cfg) 或 bypass(auto)。</summary>
    internal static string Mode
    {
        get
        {
            if (CfgBypassEnabled)
            {
                return ModeBypassCfg;
            }
            return _autoSwitched ? ModeBypassAuto : ModeNative;
        }
    }

    /// <summary>本机在这场战局中扮演哪一端，用于诊断行。</summary>
    internal static string Role
    {
        get
        {
            try
            {
                if (FikaBackendUtils.IsServer)
                {
                    return "host";
                }
                return FikaBackendUtils.IsClient ? "client" : "none";
            }
            catch
            {
                return "unknown";
            }
        }
    }

    /// <summary>对当前拾取路径的一段描述，附加到 [LootDiag] 行之后。</summary>
    internal static string DescribeSwitch()
    {
        return $"mode={Mode} switches={_switchCount} lastRelease={_lastReleaseReason} " +
            $"autoFallback={(AutoFallbackEnabled ? "on" : "off")} role={Role}";
    }

    /// <summary>
    /// 记录最后执行的是哪一次释放，这样“交互状态为何被丢弃”就可以从
    /// 任意一行 [LootDiag] 读出，而无需交叉比对整份日志。
    /// </summary>
    internal static void NoteRelease(string reason)
    {
        _lastReleaseReason = string.IsNullOrEmpty(reason) ? "unknown" : reason;
    }

    /// <summary>
    /// 本机自己的玩家是否就是拾取该物品的那一个。只有此时 Fika 才可以改动
    /// 拾取：bot 与远端玩家在每台机器上都保留原生动作。
    /// </summary>
    internal static bool IsFikaLocalPlayer(Player player)
    {
        try
        {
            if (player.IsYourPlayer != true)
            {
                return false;
            }
            if (!FikaBackendUtils.IsClient && !FikaBackendUtils.IsServer)
            {
                return false;
            }
            return player.TryCast<Fika.Core.Main.Players.FikaPlayer>() != null;
        }
        catch
        {
            // 抛出异常的玩家访问器绝不能决定一次拾取：保留原生动作。
            return false;
        }
    }

    /// <summary>
    /// 唯一会切换拾取路径的证据：<c>CoopGame</c> 的看门狗看到
    /// 本地玩家在 <see cref="EPlayerState.Pickup"/> 中待满了整整 5 秒的预算
    /// （<c>CoopGame.RepairLocalInteractionState</c>，<c>EPlayerState.Pickup =&gt; 5f</c>）。原生
    /// 拾取的动画远不到两秒，因此在该状态停留五秒不是一次正常的
    /// 拾取。没有其它状态会触发切换：Plant 有自己的 20 秒预算，且永远不会带着
    /// <see cref="EPlayerState.Pickup"/> 到达本方法。
    /// <para>
    /// 幂等：每场战局最多切换一次，因此重复的检测只会刷新
    /// 诊断状态。它前后那些 before/after 行就是给下一场战局看的
    /// “这次拾取确实翻转了路径”的证据。
    /// </para>
    /// </summary>
    internal static void NoteStuckPickup(Player player, EPlayerState stuckState, float heldForSeconds)
    {
        if (stuckState != EPlayerState.Pickup)
        {
            return;
        }
        if (CfgBypassEnabled)
        {
            // 已经在旁路上：无需切换，玩家只是处在一个奇怪的状态。
            return;
        }
        if (!AutoFallbackEnabled || _autoSwitched)
        {
            return;
        }

        _autoSwitched = true;
        _switchCount++;

                FikaGlobals.LogWarning("[PickupAutoFallback] 因为检测到卡手，已自动切到旁路 " +
            "(a stuck pickup was detected, so ground loot takes now skip the native pickup state; " +
            "no cfg change and no restart were needed)");
            }

    /// <summary>
    /// 原生拾取状态机的只读快照，在看门狗放弃它的那一刻采集
    /// （analysis/pickup-auto-fallback）。它回答下一场战局必须
    /// 弄清楚的两个问题：这次拾取是否抵达过它的背包操作（<c>MovementContext.PickupAction</c>
    /// 是拾取及其动画结束后运行的原生延续），以及交互
    /// 闸门是否被拾取状态本身关闭。
    /// </summary>
    internal static void DiagnoseStuckState(MovementContext movementContext, EPlayerState stuckState,
        float heldForSeconds)
    {
        try
        {
            var gate = "unreadable";
            try
            {
                var canInteract = movementContext.CanInteract;
                gate = canInteract == null ? "none" : canInteract.ToString();
            }
            catch (Exception gateException)
            {
                gate = "read failed (" + gateException.Message + ")";
            }

            var pending = "unreadable";
            try
            {
                var action = movementContext.PickupAction;
                pending = action == null || action.Pointer == IntPtr.Zero ? "pending=False" : "pending=True";
            }
            catch (Exception actionException)
            {
                pending = "read failed (" + actionException.Message + ")";
            }

            var animationTime = "n/a";
            var stateAge = "n/a";
            try
            {
                if (movementContext.CurrentState is MovementState currentState
                    && currentState.TryCast<PickUpState>() is { } pickUpState)
                {
                    animationTime = pickUpState._timeForPikupAnimation.ToString("0.00");
                    stateAge = (Time.time - pickUpState._enterTime).ToString("0.0");
                }
            }
            catch
            {
                // 这些字段只存在于 EFT.PickUpState 上；不是该状态时保持 "n/a"。
            }

                    }
        catch (Exception exception)
        {
                    }
    }

    /// <summary>
    /// 由 <c>CoopGame.RepairLocalInteractionState</c> 每帧驱动一次：战局进行时每 60 秒一行 [LootDiag]
    /// 心跳（这样“本机在哪条路径上、切换过几次”就不需要别的行），以及游戏一停止就执行的
    /// 每战局重置：
    /// 下一场战局重新从 native 开始。
    /// </summary>
    internal static void TickPickupModeWatchdog(Player player, bool raidRunning)
    {
        try
        {
            if (!raidRunning)
            {
                ResetForNewRaid("the raid ended");
                return;
            }

            NoteLocalPlayer(player);

            // （analysis/perf-diagnostics-cleanup）上面的重置与玩家记录是功能性的（它们
            // 会清除已结束战局的旁路状态）；只有下面的心跳是一个探针：战局进行时每 60 秒
            // 一行 [LootDiag] 拾取模式。总开关关闭时，它在读取移动状态之前就被跳过，
            // 因此一场战局不会从这里写出任何东西。
            

            if (Time.time < _nextModeDiagTime)
            {
                return;
            }
            _nextModeDiagTime = Time.time + 60f;

            var state = "none";
            try
            {
                var currentState = player?.MovementContext?.CurrentState;
                if (currentState != null)
                {
                    state = currentState.Name.ToString();
                }
            }
            catch
            {
                state = "unreadable";
            }

                    }
        catch (Exception exception)
        {
            _nextModeDiagTime = Time.time + 60f;
                    }
    }

    /// <summary>
    /// 切换预算属于某一场战局的某一个本地玩家：重连或重生让本机
    /// 拿到新的玩家对象后，会重新从 native 开始。
    /// </summary>
    private static void NoteLocalPlayer(Player player)
    {
        if (player == null)
        {
            return;
        }

        var pointer = player.Pointer;
        if (_modePlayerPointer == pointer)
        {
            return;
        }
        if (_modePlayerPointer != IntPtr.Zero)
        {
            ResetForNewRaid("the local player changed");
        }
        _modePlayerPointer = pointer;
    }

    /// <summary>
    /// 一次切换只存活一场战局：一旦不再有战局运行，下一场就从
    /// native 开始，因此一场坏掉的战局不会让之后每场战局都留在旁路上。
    /// </summary>
    internal static void ResetForNewRaid(string why)
    {
        _modePlayerPointer = IntPtr.Zero;
        if (!_autoSwitched && _switchCount == 0 && _lastReleaseReason == "none")
        {
            return;
        }

        var previous = DescribeSwitch();
        _autoSwitched = false;
        _switchCount = 0;
        _lastReleaseReason = "none";
        _nextModeDiagTime = 0f;
            }

    /// <summary>
    /// 报告原生拾取动作即将处理的这一次交互，这样下一场战局
    /// 就能说出某次拾取走了哪条路径，以及这个客户端究竟是否把该物品当作网络
    /// 对象看到（analysis/pickup-stuck-again）。按物品节流，因此按住交互键
    /// 也不会刷爆日志。
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, float> DiagSeen = new();

    internal static void DiagNativeTake(Player player, Item item)
    {
        try
        {
            var key = item.Id.ToString();
            lock (DiagSeen)
            {
                var now = Time.realtimeSinceStartup;
                if (DiagSeen.TryGetValue(key, out var last) && now - last < 1f)
                {
                    return;
                }
                if (DiagSeen.Count > 256)
                {
                    DiagSeen.Clear();
                }
                DiagSeen[key] = now;
            }

            var observed = item.TryCast<EFT.Interactive.ObservedLootItem>() != null;
                    }
        catch (Exception exception)
        {
                    }
    }

    // 按住按键时原生交互动作会反复触发，而过去每次
    // 调用都会派发自己的操作。第二次会被主机拒绝
    // （"Could not find item owner"），而该拒绝会触发权威的
    // 背包替换，导致背包界面在本场战局余下的时间里一直是空的。
    // 对同一物品的重复触发在几秒内被忽略；该映射是有界的、
    // 会自动过期，因此一次失败的拾取永远不会永久阻塞某个物品。
    private static readonly System.Collections.Generic.Dictionary<string, float> RecentTakes = new();

    private static bool IsRepeatedTake(string itemId)
    {
        lock (RecentTakes)
        {
            var now = Time.realtimeSinceStartup;
            if (RecentTakes.TryGetValue(itemId, out var last) && now - last < 1.5f)
            {
                return true;
            }
            if (RecentTakes.Count > 256)
            {
                RecentTakes.Clear();
            }
            RecentTakes[itemId] = now;
            return false;
        }
    }

    internal static bool TryTake(Player player, Item item)
    {
        var controller = player.InventoryController;
        var localRoot = controller?.RootItem?.TryCast<CompoundItem>();
        if (controller == null || localRoot == null)
        {
            return false;
        }

        var itemKey = item.Id.ToString();
        if (IsRepeatedTake(itemKey))
        {
            FikaGlobals.LogInfo($"[PickupBypass] {itemKey} is already being taken; ignoring the repeated action");
            return true;
        }

        // 护甲、头盔和战术背心只能装进装备槽位，而口袋、
        // 战术背心、背包和安全容器是各自独立的复合
        // 物品容器，所以要搜索玩家携带的每一个容器，而不只是
        // 背包根节点。否则一次拾取可能放置失败，物品被留在
        // 地上，尽管玩家其实有空间放它。
        var targets = new Il2CppSystem.Collections.Generic.List<CompoundItem>();
        var seen = new System.Collections.Generic.HashSet<IntPtr>();
        void AddTarget(CompoundItem candidate)
        {
            if (candidate != null && seen.Add(candidate.Pointer))
            {
                targets.Add(candidate);
            }
        }

        AddTarget(localRoot);
        if (player.Equipment != null && player.Equipment.TryCast<CompoundItem>() is { } equipment)
        {
            AddTarget(equipment);
        }

        try
        {
            var carried = controller.Inventory?.GetPlayerItems(EPlayerItems.NonQuestItems);
            if (carried != null)
            {
                foreach (var carriedItem in carried)
                {
                    if (carriedItem is CompoundItem compound)
                    {
                        AddTarget(compound);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[PickupBypass] could not enumerate the carried containers: {exception.Message}");
        }

        // 装备（护甲、头盔、战术背心、背包、耳机）只能装进装备槽位，
        // 而通用容器搜索从不提供这种槽位，所以先尝试专用的槽位
        // 移动。经过验证的原生路径：Slot.CreateItemAddress + ItemManipulator.Move
        // （dry run）+ ConvertOperationResultToOperation + Execute。
        if (GroundLootEquip.TryEquipFromGround(player, item, out var equipError))
        {
            FikaGlobals.LogInfo($"[PickupBypass] equip {item.Id} into its equipment slot " +
                "(native pickup state skipped)");
            return true;
        }

        var placement = ItemManipulator.QuickFindAppropriatePlace(item, controller, targets,
            ItemManipulator.EMoveItemOrder.IgnoreItemParent | ItemManipulator.EMoveItemOrder.TryEquip, true);
        if (placement.Failed)
        {
            // 这里绝不回退到原生拾取动作：那条路径会进入
            // EFT.PickUpState，并可能把玩家锁住直到本场战局结束。
            // 记录物品类别与本地化的错误，这样下一份报告就能说明放置
            // 为什么失败（装备需要专门的槽位操作）。
            var reason = "unknown";
            try
            {
                reason = placement.Error == null
                    ? "no error object"
                    : placement.Error.GetIl2CppType().Name + " / " + placement.Error.ToString();
            }
            catch (Exception errorException)
            {
                reason = "unreadable (" + errorException.Message + ")";
            }
            FikaGlobals.LogWarning($"[PickupBypass] cannot place {item.Id} template={item.TemplateId.ToString()} " +
                $"class={item.GetIl2CppType().Name}: {reason}; slot attempt: {equipError ?? "not equipment"}; " +
                "keeping it on the ground instead of entering the native pickup state");
            player.ForceInteractionsChanged();
            return true;
        }

        var operation = controller.ConvertOperationResultToOperation(placement.Value);
        if (operation == null)
        {
            FikaGlobals.LogWarning($"[PickupBypass] no operation for {item.Id}");
            return false;
        }

        FikaGlobals.LogInfo($"[PickupBypass] taking {item.Id} through {operation.GetIl2CppType().Name} " +
            "(native pickup state skipped)");
        controller.Execute(operation, (Callback)(result =>
        {
            try
            {
                if (result?.Failed == true)
                {
                    FikaGlobals.LogWarning($"[PickupBypass] pickup of {item.Id} failed: {result.Error}");
                }
                player.ForceInteractionsChanged();
            }
            catch (Exception exception)
            {
                FikaGlobals.LogError($"[PickupBypass] callback failed: {exception}");
            }
        }));
        return true;
    }
}
