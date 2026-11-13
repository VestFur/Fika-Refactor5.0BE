using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Diz.LanguageExtensions;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.World;

/// <summary>
/// AU（analysis/plant-item-consumption）：针对"成功放置后电芯仍留在背包里"
/// 这一问题的修复。
///
/// 原生版本究竟在哪里把已放置的物品从物品栏中取出，已由 4.0 的反编译
/// 确定下来：
///
///   EFT.Player.PlantItemNetwork(string itemId, string zoneId, bool successful)   // 放置入口
///     -> EFT.Player.PlantItem(itemId, zoneId, successful)
///          -> Profile.ItemDroppedAtPlace(itemId, zoneId)                          // 仅是一条记录
///          -> EFT.Player.vmethod_1(operation, callback)                           // 物品操作
///               -> plant operation(id, controller, discardResult, zone, ...)       // GClass3498
///                    -> InteractionsHandlerClass.Discard(item, controller, true)
///                         => DiscardResult                                 // 缓存的移除结果
///
/// （入口点见 Player.cs:26159-26175，操作见 Player.cs:1396-1412，而同样的 Discard-plus-RemoveOperation
/// 组合在护身符路径上的形态见 Player.cs:23049-23055：
/// 先 ItemManipulator.Discard，再用其 DiscardResult 构建一个 RemoveOperation。）
///
/// AY 更正（analysis/plant-client-host-crash），依据同一份反编译：这个版本里根本没有可执行的
/// 放置操作。EFT.Player.PlantItem 只有一行——
/// Profile.ItemDroppedAtPlace(itemId, zoneId)（eft40-decompiled/EFT/Player.cs:26164-26170）——而
/// Player.PlantItemLocalOnly（Player.cs:26172-26175）只是转发给它；上面那条 vmethod_1/放置操作
/// 调用链存在于 GClass1979/Player.cs 中护身符路径附近，并不在 PlantItem 后面。所以在这里
/// 游戏确实从不移除那枚电芯，下面这对 Discard-plus-RemoveOperation 才是
/// 唯一会发生的移除；而在客户端上它只能通过普通的
/// 物品栏操作转发通道传递。该通道是有效的：主机对客户端首次转发的
/// RemoveOperation 回复了 Succeeded（logs-coop2-20261013-110333/LogOutput.log:2933）。
///
/// 这个钩子就是回答"电芯还在不在"的那道关卡——
/// EFT.GameTriggers.ItemTriggerRequirement.TryRedeem(InventoryController, SkillManager)——因为那
/// 正是放置被确定的时刻（地图的 HandlerRequirementsWithFailedTrigger 紧接着就会触发
/// 放置触发器），也因为实机 r16/cgok 日志证明在它回答 true 之后物品仍留在
/// 物品栏中（logs-cgok-20261013-004053/LogOutput.log:3322-3323，来自运行
/// 本补丁的同一版本）。无论放置自身的移除逻辑叫什么名字，有一件事是不变的：
/// 需求已被兑换，因此用于兑换它的物品必须离开物品栏。
///
/// 本补丁不臆造任何东西：它在本机玩家自己的物品栏中搜索需求所要求的那
/// 个模板，并且当恰好只有一件物品匹配时，它执行的正是上面那两步原生操作
/// ——ItemManipulator.Discard 加上用该 DiscardResult 构建的 RemoveOperation——通过
/// 玩家自己的 controller，即主机玩家在本地执行、客户端玩家则经由本移植版的
/// 物品栏操作转发通道，与其它所有物品栏动作一样。当物品已经不在时（原生
/// 已消耗它，或同一需求触发了第二个处理器）不执行任何操作，因此任何放置
/// 都不会消耗两次；而当模板匹配到多件物品时同样不执行任何操作——
/// 这套修复从不猜测。
///
/// AY："不会执行两次"在一个地方仍然是错的——对于单次放置，地图处理器会把
/// 这道关卡回答两次，而移除是异步分派的，所以物品是否存在于
/// 物品栏并不能当作"它是否已被消耗"的判据。因此一次放置发出了两次完全相同的移除
/// （LogOutput.log:2931-2932）；主机执行了第一次而拒绝了第二次，而这次拒绝
/// 又触发了客户端物品图的重新同步并中止了放置（LogOutput.log:2934-2938）。下面的
/// 台账让每个物品的分派恰好一次，这正是原生代码的行为。
///
/// 开关：Fika.cfg -> [Gameplay] -> "Plant Consumes Item"（默认开启）与 "Plant Consume On Client"
/// （默认开启；关闭后移除严格限制在主机上执行，此时客户端的放置不消耗任何东西、也不
/// 发送任何东西）。证据行：[PlantDiag] consume: ...
/// </summary>
internal sealed class ItemTriggerRequirement_TryRedeem_Consume_Patch : ModulePatch
{
    /// <summary>
    /// AY（analysis/plant-client-host-crash）：本机已经分派过的移除，这样一次
    /// 放置就绝不会分派两次。
    ///
    /// 对于单次放置，原生地图处理器会把这道关卡回答两次——主机运行中已证明
    /// （analysis/plant-item-consumption 引用了 logs-cgok-20261013-004053/LogOutput.log:3319-3324，
    /// "op=TryRedeem -> result=True (x2)"），logs-coop2-20261013-110333/LogOutput.log:2931-2932 中
    /// 两次完全相同的发送也再次证明了这一点。旧的防护（"物品已经不在了，
    /// 所以第二次调用什么也不做"）抓不住这种情况：controller.Execute 是异步的
    /// （ClientInventoryController.cs:119-131 将 RunClientOperation 入队后让出），所以第二次调用
    /// 仍然在物品栏中找到了那枚电芯，并为它发送了第二次移除。在客户端上，那
    /// 第二次移除被主机拒绝（"Could not find item with id"，LogOutput.log:2934），而
    /// 这次拒绝引发了一次权威的物品栏重新同步，导致放置在动画播放中途被释放
    /// （LogOutput.log:2936-2938）——所报告的整条崩溃链就是从这里开始的。
    ///
    /// 该台账以物品 id 为键，并在
    /// <see cref="DispatchLedgerTtlSeconds"/> 之后过期，因此之后的某次战局（或同一物品稍后
    /// 被真正移除）永远不会被一条旧记录挡住。
    /// </summary>
    private const float DispatchLedgerTtlSeconds = 60f;

    private const int DispatchLedgerLimit = 64;

    private static readonly Dictionary<string, float> _dispatched =
        new(StringComparer.Ordinal);

    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(EFT.GameTriggers.ItemTriggerRequirement)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "TryRedeem" && method.GetParameters().Length == 2);
        if (target == null)
        {
            throw new MissingMethodException("EFT.GameTriggers.ItemTriggerRequirement.TryRedeem(InventoryController, SkillManager)");
        }
        return target;
    }

    [PatchPostfix]
    public static void Postfix(EFT.GameTriggers.ItemTriggerRequirement __instance, InventoryController __0, ref bool __result)
    {
        try
        {
            if (!__result)
            {
                return;
            }

            ConsumeRedeemedItem(__instance, __0);
        }
        catch (Exception exception)
        {
                    }
    }

    private static void ConsumeRedeemedItem(EFT.GameTriggers.ItemTriggerRequirement requirement, InventoryController controller)
    {
        if (FikaRaidGate.Skip("ItemTriggerRequirement.TryRedeem consume"))
        {
            return;
        }

        if (!PlantDiag.ConsumesItem)
        {
            
            return;
        }

        // AY：侧别策略。修复归属于拥有该物品的物品栏，而在客户端上
        // 那个物品栏的权威在主机，因此移除必须被转发
        // （ClientInventoryController.RunClientOperation）——由主机执行，已由
        // logs-coop2-20261013-110333/LogOutput.log:2933 证明。"Plant Consume On Client" = false 会把
        // 移除严格限制在主机上：此时客户端的放置不消耗任何东西、也不发送任何东西。
        if (!PlantDiag.ConsumeOnClient && !PlantDiag.IsHostMachine)
        {
            
            return;
        }

        // 只有本机拥有的物品栏才可以被改动：需求所收到的 controller
        // 必须是本机玩家自己的，与需求诊断信息的处理方式完全一致。
        if (!PlantDiag.IsLocalController(controller))
        {
            
            return;
        }

        var player = PlantDiag.LocalPlayer;
        if (player == null || controller == null)
        {
            
            return;
        }

        var item = FindRedeemedItem(player, requirement);
        if (item == null)
        {
            // 原生调用链已经移除了它，或该模板匹配不到唯一的一件物品。绝不
            // 凭空臆造物品；
            return;
        }

        var reference = PlantDiag.ItemRef(item);
        if (AlreadyDispatched(item))
        {
            
            return;
        }

        

        // 第 1 步——与原生放置操作构建时所依据的同一套模拟。
        OperationResult<DiscardResult> discard;
        try
        {
            discard = ItemManipulator.Discard(item, controller, true);
        }
        catch (Exception exception)
        {
            
            return;
        }

        if (discard == null || discard.Failed || discard.Value == null)
        {
            var error = discard == null ? "null result" : DescribeDiscard(discard);
            
            return;
        }

        // 第 2 步——执行该 discard 的原生步骤：一个作用于放置
        // controller 的 RemoveOperation，通过 controller 执行，这样客户端的放置就会像
        // 其它所有物品栏操作一样被转发给主机。回调携带该次执行的结果。
        try
        {
            var operation = new RemoveOperation(controller.GetAndIncrementNextOperationId(), controller, discard.Value);
            var callback = (Comfort.Common.Callback)(result => ReportExecution(player, reference));
            controller.Execute(operation, callback);
            // AY：移除现在已在途中，所以同一次放置的第二次回答不得再发送
            // 第二次。只有成功返回的分派才计数；抛异常则保持通路开放。
            NoteDispatched(item);
        }
        catch (Exception exception)
        {
            
            return;
        }

        // 主机是异步执行操作的，因此这次初读只反映即时
        // 状态；判断该次执行的是上面那行 step=callback。
        var presentAfter = FindRedeemedItem(player, requirement) != null;
        
    }

    /// <summary>
    /// 已分派移除的回答，使一次运行能区分"已分派但稍后被拒绝"
    /// 与"已分派并已完成"。
    /// </summary>
    private static void ReportExecution(FikaPlayer player, string reference)
    {
        try
        {
            var present = player != null && PlantDiag.FindLocalByTemplate(PlantDiag.LastRequirementFilter) != null;
            
        }
        catch (Exception exception)
        {
            
        }
    }

    /// <summary>
    /// 需求被兑换时所用的物品：即它所要求的模板，但仅当该
    /// 模板在本机物品栏中恰好出现一次——这套修复从不在多个
    /// 候选中做选择。
    /// </summary>
    private static Item FindRedeemedItem(FikaPlayer player, EFT.GameTriggers.ItemTriggerRequirement requirement)
    {
        var filter = Filter(requirement);
        if (string.IsNullOrEmpty(filter))
        {
            return null;
        }

        try
        {
            var inventory = player.Profile?.Inventory;
            if (inventory == null)
            {
                return null;
            }

            var matches = inventory.GetAllItemByTemplate(filter);
            if (matches == null)
            {
                return null;
            }

            Item found = null;
            var count = 0;
            foreach (var candidate in matches)
            {
                if (candidate == null)
                {
                    continue;
                }

                count++;
                found = candidate;
            }

            if (count == 1)
            {
                return found;
            }

            
            return null;
        }
        catch (Exception exception)
        {
            
            return null;
        }
    }

    private static string Filter(EFT.GameTriggers.ItemTriggerRequirement requirement)
    {
        try
        {
            return requirement?._itemRequirementId ?? "none";
        }
        catch
        {
            return "none";
        }
    }

    /// <summary>本机是否已经分派过该物品的移除。</summary>
    private static bool AlreadyDispatched(Item item)
    {
        var key = ItemKey(item);
        if (key == null)
        {
            // 无法读取的 id 不得阻止一次原生侧本来会执行的移除。
            return false;
        }

        try
        {
            PruneDispatched();
            return _dispatched.ContainsKey(key);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>记录一次已分派的移除。仅在 controller 接受了它之后调用。</summary>
    private static void NoteDispatched(Item item)
    {
        var key = ItemKey(item);
        if (key == null)
        {
            return;
        }

        try
        {
            PruneDispatched();
            _dispatched[key] = Time.time;
        }
        catch
        {
            // 台账只用于抑制重复；写入失败不得中断修复。
        }
    }

    private static string ItemKey(Item item)
    {
        try
        {
            var id = item?.Id;
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch
        {
            return null;
        }
    }

    private static void PruneDispatched()
    {
        var now = Time.time;
        List<string> stale = null;
        foreach (var entry in _dispatched)
        {
            if (now - entry.Value > DispatchLedgerTtlSeconds)
            {
                (stale ??= []).Add(entry.Key);
            }
        }

        if (stale != null)
        {
            foreach (var key in stale)
            {
                _dispatched.Remove(key);
            }
        }

        if (_dispatched.Count <= DispatchLedgerLimit)
        {
            return;
        }

        List<KeyValuePair<string, float>> ordered = [.. _dispatched];
        ordered.Sort((left, right) => left.Value.CompareTo(right.Value));
        var excess = _dispatched.Count - DispatchLedgerLimit;
        for (var index = 0; index < excess; index++)
        {
            _dispatched.Remove(ordered[index].Key);
        }
    }

    private static string DescribeDiscard(OperationResult<DiscardResult> result)
    {
        try
        {
            return result.Error?.ToString() ?? "unknown";
        }
        catch
        {
            return "unreadable";
        }
    }
}
