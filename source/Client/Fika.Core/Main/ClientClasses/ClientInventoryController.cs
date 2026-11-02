using EFT.Settings;
using System;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using EFT.UI;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets;
using Fika.Core.Networking.Packets.Communication;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.World;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Main.ClientClasses;

public sealed class ClientInventoryController : BaseInventoryController
{

    public ClientInventoryController(System.IntPtr pointer) : base(pointer) { }


    /// <summary>
    /// 客户端在本地判定一次已发送的库存操作失败之前，
    /// 等待主机确认的时长。
    /// </summary>
    private const int OperationAcknowledgementTimeoutMs = 30000;

    /// <summary>
    /// 确认只意味着 "主机收到了这次操作"。
    /// 此后若没有最终状态到达，操作就一直处于进行中，
    /// 从而把发起请求的玩家锁在交互状态（地面拾取）
    /// 里直到突袭结束，所以同一个看门狗会在本地释放它。
    /// </summary>
    private const int OperationCompletionTimeoutMs = 20000;

    public FikaPlayer FikaPlayer { get; }

    public override bool HasDiscardLimits
    {
        get
        {
            return false;
        }
    }
    private readonly Player _player;
    private readonly ClientInventoryOperationHandlerPool _clientInventoryOperationHandlerPool;
    private readonly System.Collections.Generic.Dictionary<ushort, ClientInventoryOperationHandler> _activeOperationHandlers = [];

    /// <summary>
    /// 本客户端已派发、且操作尚未完成的付费撤离点（"car
    /// extract"）付款，以撤离点名称为键。
    /// 原生交互在该点报告玩家已入队之前会一直提供交接机会，
    /// 所以没有这张表，
    /// 每多一次点击都会再排入一笔付款。
    /// </summary>
    private readonly System.Collections.Generic.Dictionary<string, ushort> _carExtractPayments = [];

    public ClientInventoryController(Player player, Profile profile, bool examined, bool strictSync)
        : base(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ClientInventoryController>(), player, profile, examined, strictSync)
    {
        _player = player;
        FikaPlayer = player.TryCast<FikaPlayer>()
            ?? throw new InvalidOperationException("Client inventory controller received a non-Fika player");
        IdSource = MongoID.Generate(true);
        PlayerSearchController = new ActiveSearchController(profile, this);
        _clientInventoryOperationHandlerPool = new ClientInventoryOperationHandlerPool(8, ClientInventoryOperationHandler.CreateInstance);
    }

    public override IPlayerSearchController PlayerSearchController { get; }

    public override void GetTraderServicesDataFromServer(string traderId)
    {
        if (FikaBackendUtils.IsClient)
        {
            RequestPacket request = new()
            {
                Type = ERequestSubPacketType.TraderServices,
                RequestSubPacket = new RequestSubPackets.TraderServicesRequest()
                {
                    NetId = FikaPlayer.NetId,
                    TraderId = traderId
                }
            };

            FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref request, DeliveryMethod.ReliableOrdered);
            return;
        }

        FikaPlayer.UpdateTradersServiceData(traderId).HandleExceptions();
    }

    public override void CallMalfunctionRepaired(Weapon weapon)
    {
        if (Singleton<SettingsManager>.Instance.Game.Settings.MalfunctionVisability)
        {
            MonoBehaviourSingleton<PreloaderUI>.Instance.MalfunctionGlow.ShowGlow(BattleUIMalfunctionGlow.EGlowType.Repaired, true, GetGlowAlphaMultiplier());
        }
    }

    public override void ExecuteStationaryOperation(StationaryWeapon stationaryWeapon, Callback callback = null)
    {
        FikaPlayer.OperationStationaryCallbackId = NextOperationId;
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call2(this, stationaryWeapon, callback);
    }

    public override void Execute(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
        MainThreadDispatcher.RunAsync(() => HandleOperation(operation, callback)).Forget();
    }

    private async Task HandleOperation(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
        if (_player.HealthController.IsAlive)
        {
            await Task.Yield();
        }
        RunClientOperation(operation, callback);
    }

    /// <summary>
    /// 获取一个库存处理器
    /// </summary>
    /// <returns>一个池化的处理器</returns>
    public ClientInventoryOperationHandler GetHandler()
    {
        return _clientInventoryOperationHandlerPool.Get();
    }

    /// <summary>
    /// 归还一个处理器
    /// </summary>
    /// <param name="handler">要归还的处理器</param>
    public void ReturnHandler(ClientInventoryOperationHandler handler)
    {
        // 当原生的 ushort 操作 ID 回绕时，
        // 来自更早操作的迟到完成不得移除当前处理器。
        if (_activeOperationHandlers.TryGetValue(handler.CallbackId, out var current) &&
            ReferenceEquals(current, handler))
        {
            _activeOperationHandlers.Remove(handler.CallbackId);
            // 服务器回调注册必须随它的处理器一起离开。正常情况下，
            // 当最终状态到达时它由 FikaPlayer.HandleCallbackFromServer
            // 移除，但每一条 FailLocally 路径（30
            // 秒确认看门狗、50 秒完成看门狗、重同步解码失败、"服务器报告
            // X 之后仍在进行中"）都在没有应答的情况下结束了操作，
            // 而它的注册就留在 FikaPlayer.OperationCallbacks
            // 里直到突袭结束。FikaPlayer.WaitingForCallback
            // 会读取那个字典，而客户端枪械/手雷控制器在该字典非空时会从
            // CanPressTrigger 和
            // CanStartReload 返回 false，
            // 因此一次失败的操作就让本地玩家无法开枪。
            FikaPlayer.OperationCallbacks.Remove(handler.CallbackId);
        }
        _clientInventoryOperationHandlerPool.ReturnHandler(handler);
    }

    internal void ReceiveInventoryResync(InventoryResyncPacket packet)
    {
        if (_activeOperationHandlers.TryGetValue(packet.CallbackId, out var handler))
        {
            // 解码权威快照绝不能使数据包处理器中止：
            // 待处理的操作随后会等待
            // 一个再也不会到来的回调，
            // 把玩家卡在本次拒绝本应释放的交互状态里。
            try
            {
                // 解码结果为空的东西绝不能进入
                // ReplaceInventory：用 null
                // 替换整棵树会让整个库存清空直到突袭结束。
                // 改为让该操作失败，好让它的看门狗释放调用方。
                var authoritative = packet.Descriptor.ToInventory();
                if (authoritative == null)
                {
                    FikaGlobals.LogError($"{ID} - The host's authoritative inventory snapshot for operation " +
                        $"{packet.CallbackId} decoded to nothing");
                    handler.FailWithoutServerStatus("The host's authoritative inventory snapshot was empty.");
                }
                else
                {
                    // [SyncDiag] 在解码处计数：这就是替换本地那棵树的新树。
                    FikaGlobals.LogInfo($"[SyncDiag] op={packet.CallbackId} resync-descriptor " +
                        $"items={CountSnapshotItems(authoritative)}");
                    handler.ReceiveAuthoritativeInventory(authoritative);
                }
            }
            catch (Exception exception)
            {
                FikaGlobals.LogError($"{ID} - Failed to decode the authoritative inventory for " +
                    $"operation {packet.CallbackId}: {exception}");
                try
                {
                    handler.FailWithoutServerStatus("The host's authoritative inventory snapshot could not be applied.");
                }
                catch (Exception failException)
                {
                    FikaGlobals.LogError($"{ID} - Failed to release operation {packet.CallbackId}: {failException}");
                }
            }
        }
        // 没有对应操作的快照，
        // 可能是某次成功的新操作之后的迟到重复包。
        // 在这里替换整个库存会回滚那更新的状态。
    }

    private void RunClientOperation(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
        if (!CanExecute(operation))
        {
            try
            {
                var itemId = operation.TryCast<MoveOperation>() is { } moving ? moving.Item?.Id?.ToString() : null;
                FikaGlobals.LogWarning($"{ID} - Cannot execute local operation {operation.Id} " +
                    $"({operation.GetIl2CppType().Name}{(itemId == null ? string.Empty : ", item " + itemId)}); " +
                    "failing it locally so the native pickup interaction can finish");
            }
            catch (Exception diagnosticException)
            {
                FikaGlobals.LogError($"{ID} - Failed to describe the rejected operation: {diagnosticException}");
            }

            // CanExecute 是原生的闸门（PlayerInventoryController
            // 把它委托给手部控制器，而 CheckItemAction 在
            // ItemController.Locked 置位时拒绝一切）。
            // 拒绝正是库存看门狗所需的证据，而这次操作可能从未开始过，
            // 所以我们不会有任何回调来释放该交互状态。
            // [LootDiag]（analysis/pickup-stuck-again）：
            // 一次操作从未离开本客户端的拿取。配合它旁边那行拾取请求记录来看，
            // 这就是 "在这里被拒绝，而不是在链路上丢失" 的证据。
            var refusedItemId = "n/a";
            try
            {
                if (operation.TryCast<MoveOperation>() is { } refusedMove && refusedMove.Item != null)
                {
                    refusedItemId = refusedMove.Item.Id.ToString();
                }
            }
            catch
            {
                // 报告一次被拒绝的操作，绝不能在自己查找物品时失败。
            }
                        NoteGateRefusal($"operation {operation.Id} ({operation.GetIl2CppType().Name}) refused by CanExecute");
            NativeInteractionRelease.Release(FikaPlayer, "local-operation-refused");
            try
            {
                operation.Dispose();
            }
            catch (Exception disposeException)
            {
                // 处置一个仍处于 "Started" 的操作会抛出最终事件，
                // 而且可能抛异常；一旦如此，它注册的 Begin 事件会留在
                // ItemController.ActiveEvents 里，
                // 让该物品在余下的突袭时间里一直忙碌（PlayerIsBusyError）。
                FikaGlobals.LogError($"[InventoryRepair] {ID} - disposing the refused operation {operation.Id} threw, " +
                    $"its item events may stay registered: {disposeException}");
            }
            callback?.Fail("LOCAL: hands controller can't perform this operation");
            return;
        }

        // 任务交易在本地即具权威性；
        // 它们提交的库存效果由任务操作钩子在成功执行之后发送。
        if (operation.TryCast<BaseQuestOperation>() != null)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

        // 任务阅读（笔记、章节条件、条件、笔记链接）与磁带研读并不是
        // BaseQuestOperation 的子类，
        // 但它们改变的只是本地玩家自己的任务与物品数据——也就是本机拥有、
        // 并在突袭结束时上传的档案。它们在这里运行，
        // 且从不中继；原因见 QuestOperations。
        if (QuestOperations.IsLocalOnly(operation, out var questDetail))
        {
            FikaGlobals.LogInfo($"[QuestOp] {operation.GetIl2CppType().Name} executed locally ({questDetail}); " +
                "local quest data, not relayed");
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

        // 不要复制拾取任务物品，这会在其他客户端上抛错
        if (operation.TryCast<MoveOperation>() is { } moveOperation)
        {
            var lootedItem = moveOperation.Item;
            if (lootedItem.QuestItem)
            {
                global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
                return;
            }
        }

        // 不要复制存放任务物品
        if (operation.TryCast<RemoveOperation>() is { } discardOperation && discardOperation.Item.QuestItem)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

        // 搜索与对话进度需要本地对话状态。
        if (operation.TryCast<SinglePlayerSearchContentOperation>() != null
            || operation.TryCast<SetDialogProgressOperation>() != null)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

        // [CarExtract] 付费撤离点通过
        // EFT.InventoryLogic.Operations.TransferExfiltrationPaymentOperation
        // 交出现金。原生交互在 TransferExitItem
        // 返回的瞬间就告诉玩家钱已转移，因此当第一笔付款仍未获应答时，
        // 第二次点击必须在这里被拒绝，
        // 而不是再排一次交接——主机会乐于第二次照样执行。
        var carExtractPoint = GetCarExtractPointName(operation);
        if (carExtractPoint != null
            && _carExtractPayments.TryGetValue(carExtractPoint, out var pendingPayment)
            && _activeOperationHandlers.ContainsKey(pendingPayment))
        {
            FikaGlobals.LogWarning($"[CarExtract] {ID} - refusing a second payment for '{carExtractPoint}', " +
                $"operation {pendingPayment} is still awaiting the host");
            FikaNotifications.DisplayMessageNotification(
                $"[CarExtract] A payment for '{carExtractPoint}' is already being processed.",
                iconType: ENotificationIconType.Alert);
            try
            {
                operation.Dispose();
            }
            catch (Exception disposeException)
            {
                FikaGlobals.LogError($"[CarExtract] {ID} - disposing the duplicate payment {operation.Id} threw: {disposeException}");
            }
            callback?.Fail("A payment for this extraction point is already being processed.");
            return;
        }

        var handler = _clientInventoryOperationHandlerPool.Get();
        var operationNum = operation.Id;
        try
        {
            handler.Set(this, operation, callback);
            // 注册、活动表和看门狗全都在这个受保护块里：
            // 以前其中任何一步抛出都会
            // 让调用方的回调不被调用、
            // 操作没有看门狗，
            // 从而把玩家锁在原生的拾取交互状态里直到突袭结束。
            operationNum = AddOperationCallback(operation, handler.ServerStatusDelegate);
            _activeOperationHandlers[operationNum] = handler;
            if (carExtractPoint != null)
            {
                _carExtractPayments[carExtractPoint] = operationNum;
            }
            WatchOperationAcknowledgement(handler, operationNum);

            var packet = InventoryPacket.FromValue(FikaPlayer.NetId, operation);
            // 防御性措施：每个任务操作都会走上方的本地分支，
            // 所以只有当真有一个再次到达中继时这里才会触发。
            if (QuestOperations.TryDescribe(packet.Descriptor, out var relayedQuestDetail))
            {
                FikaGlobals.LogInfo($"[QuestOp] relaying {operation.GetIl2CppType().Name} to the host ({relayedQuestDetail})");
            }
            if (packet.Descriptor.TryCast<TransferExfiltrationPaymentOperationDescriptor>() is { } paymentDescriptor)
            {
                FikaGlobals.LogInfo($"[CarExtract] client payment request: point '{paymentDescriptor.PointName}', " +
                    $"{FikaExfilManager.DescribePaymentItems(paymentDescriptor)}, operation {operation.Id}, player {FikaPlayer.ProfileId}");
            }
            try
            {
                if (operation.TryCast<MoveOperation>() is { } moving)
                {
                    FikaGlobals.LogInfo($"Sending inventory operation {operation.Id} ({operation.GetIl2CppType().Name}) " +
                        $"for item {moving.Item?.Id} from {moving.Item?.CurrentAddress} descriptor: {packet.Descriptor}");
                }
                else
                {
                    // 丢弃（以及其它所有非移动操作）此前在日志中不可见，
                    // 这使得 "被丢弃的背包消失了" 无从追查。
                    FikaGlobals.LogInfo($"Sending inventory operation {operation.Id} " +
                        $"({operation.GetIl2CppType().Name}) descriptor: {packet.Descriptor}");
                }
            }
            catch (Exception diagnosticException)
            {
                FikaGlobals.LogError($"Failed to describe outgoing operation {operation.Id}: {diagnosticException}");
            }

            FikaPlayer.PacketSender.NetworkManager.SendGenericPacket(EGenericSubPacketType.InventoryOperation, packet);
        }
        catch (Exception dispatchException)
        {
            FikaGlobals.LogError($"{ID} - Failed to dispatch inventory operation {operationNum}: {dispatchException}");
            if (carExtractPoint != null)
            {
                // 原生交互已经把交接报告为完成，
                // 所以必须告诉玩家主机从未收到它。
                FikaGlobals.LogError($"[CarExtract] the payment for '{carExtractPoint}' (operation {operationNum}) " +
                    $"never reached the host: {dispatchException.Message}");
                FikaNotifications.DisplayMessageNotification(
                    $"[CarExtract] The payment for '{carExtractPoint}' could not be sent to the host.",
                    iconType: ENotificationIconType.Alert);
            }
            try
            {
                FikaPlayer.OperationCallbacks.Remove(operationNum);
            }
            catch (Exception removeException)
            {
                FikaGlobals.LogError($"{ID} - Failed to remove the callback of operation {operationNum}: {removeException}");
            }
            ReturnHandler(handler);
            // 原生拾取流程在等这个回调；没有它，玩家会一直锁在
            // PickUpState 和交互队列里。
            global::Fika.Core.Main.Utils.NativeInteractionRelease.Release(FikaPlayer, "dispatch-failed");
            try
            {
                operation.Dispose();
            }
            catch (Exception disposeException)
            {
                FikaGlobals.LogError($"{ID} - Failed to dispose operation {operationNum}: {disposeException}");
            }
            try
            {
                callback?.Invoke(new FailedResult($"The inventory operation {operationNum} could not be sent to the host."));
            }
            catch (Exception callbackException)
            {
                FikaGlobals.LogError($"{ID} - Failed to fail operation {operationNum} locally: {callbackException}");
            }
            return;
        }
#if DEBUG
        ConsoleScreen.Log($"InvOperation: {operation.GetType().Name}, Id: {operation.Id}");
#endif
    }

    /// <summary>
    /// 返回一笔付费撤离点（"car extract"）
    /// 付款操作的撤离点名称，当该操作不是付款时返回
    /// null。读取名称纯粹是诊断用途，绝不能阻止一笔付款被派发，
    /// 因此任何失败都被吞掉并报告为 "不是付款"。
    /// </summary>
    private static string GetCarExtractPointName(EFT.InventoryLogic.Operations.AbstractOperation operation)
    {
        try
        {
            if (operation.TryCast<TransferExfiltrationPaymentOperation>() is not { } payment)
            {
                return null;
            }

            var pointName = payment._result?.PointName;
            return string.IsNullOrEmpty(pointName) ? null : pointName;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[CarExtract] failed to read the payment operation {operation?.Id}: {exception}");
            return null;
        }
    }

    /// <summary>
    /// 主机在执行库存操作之前先确认它。
    /// 如果该确认始终不来，操作就在本地失败，
    /// 好让发起请求的玩家被释放，
    /// 而不是卡在交互状态里等一个再也不会到来的回调。
    /// </summary>
    private async void WatchOperationAcknowledgement(ClientInventoryOperationHandler handler, ushort operationNum)
    {
        try
        {
            await Task.Delay(OperationAcknowledgementTimeoutMs);
            if (!handler.IsActive) return;
            if (!handler.ReceivedServerStatus)
            {
                if (!_activeOperationHandlers.TryGetValue(operationNum, out var pending) || !ReferenceEquals(pending, handler)) return;
                FikaGlobals.LogError($"{ID} - No acknowledgement for inventory operation {operationNum} " +
                    $"after {OperationAcknowledgementTimeoutMs} ms, failing it locally: {handler.Operation}");
                handler.FailWithoutServerStatus("The host never acknowledged the inventory operation.");
                return;
            }

            await Task.Delay(OperationCompletionTimeoutMs);
            if (!handler.IsActive) return;
            if (!_activeOperationHandlers.TryGetValue(operationNum, out var unfinished) || !ReferenceEquals(unfinished, handler)) return;
            FikaGlobals.LogError($"{ID} - Inventory operation {operationNum} was acknowledged but never finished " +
                $"after {OperationAcknowledgementTimeoutMs + OperationCompletionTimeoutMs} ms, failing it locally: {handler.Operation}");
            handler.FailWithoutServerStatus("The host did not finish the inventory operation.");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"{ID} - Inventory operation acknowledgement watchdog failed: {exception}");
        }
    }

    public override bool HasCultistAmulet(out CultistAmulet amulet)
    {
        amulet = null;
        var slots = new Il2CppSystem.Collections.Generic.List<EquipmentSlot>();
        slots.Add(EquipmentSlot.Pockets);
        var enumerator = Inventory.GetItemsInSlots(slots).GetEnumerator();

        while (enumerator.MoveNext())
        {
            if (enumerator.Current?.TryCast<CultistAmulet>() is { } cultistAmuletClass)
            {
                amulet = cultistAmuletClass;
                return true;
            }
        }
        return false;
    }

    public ushort AddOperationCallback(EFT.InventoryLogic.Operations.AbstractOperation operation, Action<ServerOperationStatus> callback)
    {
        var id = operation.Id;
        FikaPlayer.OperationCallbacks.Add(id, callback);
        return id;
    }

    /// <summary>端口已派发、且处理器仍处于注册中的操作。</summary>
    internal override int PendingOperationCount => _activeOperationHandlers.Count;

    /// <summary>背后没有处理器的服务器回调注册。</summary>
    internal override int ReleaseStaleServerCallbacks(string reason) => ReleaseStaleOperationCallbacks(reason);

    /// <inheritdoc/>
    internal override int CountStaleServerCallbacks() => CountStaleOperationCallbacks();

    /// <summary>
    /// 背后没有处理器的服务器回调注册。
    /// 它们是在本地失败的那次操作留下的残余，而
    /// <see cref="FikaPlayer.WaitingForCallback"/>
    /// 会把它们计入：只要存在一个，
    /// 客户端手部控制器就拒绝扣动扳机或开始换弹。
    /// </summary>
    internal int CountStaleOperationCallbacks()
    {
        var stale = 0;
        foreach (var pair in FikaPlayer.OperationCallbacks)
        {
            if (!_activeOperationHandlers.ContainsKey((ushort)pair.Key))
            {
                stale++;
            }
        }
        return stale;
    }

    /// <summary>
    /// 丢弃每一条没有任何已注册处理器持有的服务器回调注册。
    /// 活动中的操作总会持有一个，因此这只可能移除残余。
    /// </summary>
    internal int ReleaseStaleOperationCallbacks(string reason)
    {
        System.Collections.Generic.List<uint> stale = null;
        foreach (var pair in FikaPlayer.OperationCallbacks)
        {
            if (_activeOperationHandlers.ContainsKey((ushort)pair.Key))
            {
                continue;
            }
            (stale ??= []).Add(pair.Key);
        }

        if (stale == null)
        {
            return 0;
        }

        foreach (var id in stale)
        {
            FikaPlayer.OperationCallbacks.Remove(id);
        }

        FikaGlobals.LogWarning($"[InventoryRepair] {ID} - dropped {stale.Count} server callback registration(s) " +
            $"that had no handler left (reason={reason})");
        return stale.Count;
    }

    /// <summary>
    /// 针对停止应答的客户端库存流水线的恢复。只由
    /// <c>CoopGame</c> 里的保守看门狗调用，
    /// 它要求有玩家的尝试（打开库存或一次被拒绝的操作）
    /// 且闸门在玩家空闲期间一直关闭。
    /// <para>
    /// 它先清除端口自己的簿记（仍然注册但已不再活动的处理器、
    /// 背后没有处理器的回调注册），然后是原生闸门，
    /// 最后释放原生交互状态。
    /// <paramref name="escalate"/>
    /// 还会额外丢弃原生运行时自己报告为过期的物品事件，
    /// 正是这一点让某个物品在一次中止的操作之后不再无法移动。
    /// </para>
    /// </summary>
    internal override void RepairBlockedInventory(string reason, bool escalate)
    {
        var deadHandlers = 0;
        System.Collections.Generic.List<ushort> deadKeys = null;
        foreach (var pair in _activeOperationHandlers)
        {
            if (pair.Value == null || !pair.Value.IsActive)
            {
                (deadKeys ??= []).Add(pair.Key);
            }
        }

        if (deadKeys != null)
        {
            foreach (var key in deadKeys)
            {
                // 只丢弃表项：
                // 一个已不活动的处理器已经归还过一次池，
                // 再次归还会把同一个实例交给两个操作。
                _activeOperationHandlers.Remove(key);
                FikaPlayer.OperationCallbacks.Remove(key);
                deadHandlers++;
            }
        }

        if (deadHandlers > 0)
        {
            FikaGlobals.LogWarning($"[InventoryRepair] {ID} - dropped {deadHandlers} inventory handler(s) " +
                "that were still registered although they were no longer active");
        }

        base.RepairBlockedInventory(reason, escalate);
    }

    public override SearchContentOperation CreateSearchOperation(SearchableItem item)
    {
        return new SinglePlayerSearchContentOperation(GetAndIncrementNextOperationId(), this, PlayerSearchController, Profile, item);
    }

    public readonly struct ServerOperationStatus(EOperationStatus status, string error)
    {
        public readonly EOperationStatus Status = status;
        public readonly string Error = error;
    }

    public void ClearPool()
    {
        foreach (var callbackId in _activeOperationHandlers.Keys)
            FikaPlayer.OperationCallbacks.Remove(callbackId);
        _activeOperationHandlers.Clear();
        _clientInventoryOperationHandlerPool.Dispose();
        // [InvDiag]（2026-10-27，T4）簿记报告预算原本是静态的、从不重置，
        // 于是在一个游戏进程里中继 40 次操作之后，物品验收所依据的那行 "known=a->b"
        // 就不再出现——之后的突袭无法对它做任何证明。ClearPool 每次突袭清理时运行一次。
        ClientInventoryOperationHandler.ResetBookkeepingReport();
    }

    /// <summary>
    /// （2026-10-17）一次中继操作的本地那一半经由 Operation.ExecuteWithoutDispose
    /// 运行，它绕过了原生的 InventoryController.Execute 包装。
    /// 那个包装正是把移动所触及的每个物品在其新地址登记为玩家搜索控制器中的已知项；没有它，
    /// 物品一落进容器就显示为未搜索（报告中 'unload magazine' 之后的那只弹匣）。
    /// 这里重复同样的簿记：每个物品一个字典条目，不改任何物品字段、也不改任何档案字段。
    /// </summary>
    internal void MarkOperationItemsKnown(EFT.InventoryLogic.Operations.AbstractOperation operation)
    {
        try
        {
            var search = PlayerSearchController;
            if (search == null || operation == null)
            {
                return;
            }

            MarkOperationItemsKnown(search, operation, 0);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"{ID} - could not mark the items of operation {operation?.Id} as known: {exception.Message}");
        }
    }

    /// <summary>
    /// [InvDiag]（2026-10-27，T4）<see cref="MarkOperationItemsKnown(EFT.InventoryLogic.Operations.AbstractOperation)"/> 的递归那一半。
    /// <para>
    /// 包装的形状正是这里需要它的原因。<c>LoadMagOperation</c> 与 <c>UnloadMagOperation</c>
    /// 派生自 <c>AbstractMagOperation&lt;TDescriptor&gt;</c>，
    /// 并用内层操作为参数构造（它们自己的构造函数是 <c>(ushort id, ItemController
    /// controller, AbstractOperation internalOperation)</c>），
    /// 所以真正移动的弹匣、弹药堆和拆分只能通过 <c>IParentOperation.InternalOperation</c>
    /// 抵达。扁平版本读取的是包装自己的访问器，什么也没找到就返回了，于是恰好这些物品未被检查，而普通移动却没问题。
    /// 本机每卸载一批就派发一个这样的包装：<c>BaseInventoryController.CustomAmmoUnloader.DoUnloadLoopAsync</c>
    /// 构造一个 <c>UnloadMagOperation</c> 并用它调用 <c>Execute</c>。
    /// <see cref="MarkOperationItemsSearched"/> 与
    /// <see cref="CaptureResyncItems"/> 已经遍历过这棵树；这里是对已知那一半的同样遍历。
    /// </para>
    /// </summary>
    private void MarkOperationItemsKnown(IPlayerSearchController search,
        EFT.InventoryLogic.Operations.AbstractOperation operation, int depth)
    {
        if (operation == null || depth > 4)
        {
            return;
        }

        // 包装只是真正触及这些物品的那次操作的外壳，而被中继的那次操作
        // 本身就是这个包装（见 RunClientOperation）。
        if (operation.TryCast<IParentOperation>() is { } parent && parent.InternalOperation != null)
        {
            MarkOperationItemsKnown(search, parent.InternalOperation, depth + 1);
        }

        // 一次操作中一个坏掉的访问器，绝不能阻止其它操作的登记。
        try
        {
            MarkOperationOwnItemsKnown(search, operation);

            // 每个携带物品的操作都实现 IOneItemOperation（它所移动的那个物品），
            // 而涉及两个物品的操作还会加上 ITwoItemOperation（交换或转移的第二个物品）。
            // 两者都连同其整棵子树一起登记：落进背包格子的弹匣，以及它内部的一切。
            if (operation.TryCast<IOneItemOperation>() is { } one)
            {
                MarkKnownTree(search, one.Item1);
            }

            if (operation.TryCast<ITwoItemOperation>() is { } two)
            {
                MarkKnownTree(search, two.Item2);
            }

            // 一次拆分产生的物品实例比上面这些访问器所暴露的要多一个：SplitOperation
            // 把它保存在 _splitItem 里。此前只登记了通过 Item1 可达的那一堆，
            // 所以另一个渲染成未检查。两个都登记是幂等的（每个物品实例一个字典条目）。
            if (operation.TryCast<SplitOperation>() is { } split)
            {
                MarkKnownTree(search, split._splitItem);
            }

            // 同一套原生簿记的后一半：这次操作在玩家自己的库存内部产出、
            // 移动或填充的容器。见 MarkOperationItemsSearched。
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"{ID} - could not mark the items of operation {operation?.Id} as known: {exception.Message}");
        }
    }

    /// <summary>
    /// [InvDiag]（2026-10-27，T4）把操作自己的物品集合（<c>AbstractOperation.Items</c>
    /// 加上 <c>IMultipleItemsOperation.Items</c>）
    /// 登记为已知。某些操作类型只在这个集合里保存它们的物品，而扁平访问器读法从未看过它。
    /// <para>
    /// 这里只登记物品本身，绝不登记它的子树：一个仍装着玩家未见战利品的容器，
    /// 不得因这次簿记而被揭示。位于 <c>IOneItemOperation</c>
    /// 背后的物品保留成功路径一贯有的子树登记。
    /// </para>
    /// </summary>
    private void MarkOperationOwnItemsKnown(IPlayerSearchController search,
        EFT.InventoryLogic.Operations.AbstractOperation operation)
    {
        var items = operation.Items;
        if (items != null)
        {
            foreach (var item in items)
            {
                MarkItemKnown(search, item);
            }
        }

        if (operation.TryCast<IMultipleItemsOperation>() is { } multiple)
        {
            var multipleItems = multiple.Items;
            if (multipleItems != null)
            {
                foreach (var item in multipleItems)
                {
                    MarkItemKnown(search, item);
                }
            }
        }
    }

    private static void MarkItemKnown(IPlayerSearchController search, Item item)
    {
        if (item == null)
        {
            return;
        }

        try
        {
            // raiseEvents: false —— 与 MarkKnownTree 使用它的原因相同：
            // 视图读取的是地址条目，为玩家只是挪动了一下位置的东西发一个 "found item" 事件会成为可见的副作用。
            search.SetItemAsKnown(item, false);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[InvDiag] could not mark item {item.Id} as known: {exception.Message}");
        }
    }

    /// <summary>
    /// （2026-10-21）一次中继的客户端操作的真正应用路径，只需一次调用：
    /// 原生的 <c>InventoryController.Execute</c>
    /// 包装——也就是 <see cref="Execute"/> 所替换的那个——才是把
    /// 移动触及的每个物品登记为已知、把触及的每个容器登记为已搜索的地方。中继的操作经由
    /// <c>Operation.ExecuteWithoutDispose</c> 运行，所以这些簿记一件都没发生，
    /// 而一次成功操作产出的物品在下次打开库存时回来变成了 "未检查 / 未搜索"。两半都在这里重放，
    /// 并按操作报告前后结果，因此日志无需再跑一次就能回答 "登记是否到达了玩家看到的那件物品"。
    /// <para>
    /// 这份报告没有放宽任何东西：内容仍然未知的容器保持未搜索（
    /// <see cref="MarkContainerSearched"/>），正是这一点保住了搜刮机制。
    /// </para>
    /// </summary>
    /// <returns>操作及其两个计数的一行描述，供调用方的日志行使用。</returns>
    internal string ApplyRelayedOperationBookkeeping(EFT.InventoryLogic.Operations.AbstractOperation operation)
    {
        var name = operation == null ? "null" : operation.GetIl2CppType().Name;
        try
        {
            var search = PlayerSearchController;
            if (search == null || operation == null)
            {
                return $"op={name} skipped={(search == null ? "no-search-controller" : "no-operation")}";
            }

            var items = CollectOperationItems(operation);
            var knownBefore = CountKnown(search, items);
            var searchedBefore = CountSearched(search, items);

            MarkOperationItemsKnown(operation);
            MarkOperationItemsSearched(search, operation, 0);

            var knownAfter = CountKnown(search, items);
            var searchedAfter = CountSearched(search, items);

            // [InvDiag]（T4）`wrapped` 指的是扁平访问器读法看不见的那种形状：一个包装操作（装/卸弹匣）
            // 把真正移动的物品保存在 IParentOperation.InternalOperation 里。
            // 因此 "op=UnloadMagOperation wrapped=True items=4
            // known=4->4" 可以读作 "包装被遍历了，其内层物品已登记"；而在仍然报告 known=0
            // 的操作上 wrapped=False 会把排查引向别处（主机拒绝 / 重同步），而不是回到这个方法。
            var wrapped = operation.TryCast<IParentOperation>() is { } parent && parent.InternalOperation != null;
            // 包装的计数只覆盖包装自己的访问器（scope=outer-only）：
            // 真正移动的物品在更深一层，所以对它来说这些数字是下限，不是结论。
            return $"op={name} id={operation.Id} items={items.Count} wrapped={wrapped}" +
                $"{(wrapped ? " scope=outer-only" : string.Empty)} " +
                $"known={knownBefore}->{knownAfter} containers={searchedBefore}->{searchedAfter}";
        }
        catch (Exception exception)
        {
            return $"op={name} failed={exception.GetType().Name}:{exception.Message}";
        }
    }

    /// <summary>一次操作所触及的物品，以每个访问器整棵子树合并成的一个扁平列表呈现。</summary>
    private static System.Collections.Generic.List<Item> CollectOperationItems(
        EFT.InventoryLogic.Operations.AbstractOperation operation)
    {
        var items = new System.Collections.Generic.List<Item>();
        if (operation == null) return items;
        try
        {
            var own = operation.Items;
            if (own != null)
            {
                foreach (var item in own)
                {
                    if (item != null) items.Add(item);
                }
            }
        }
        catch (Exception) { }

        try
        {
            if (operation.TryCast<IOneItemOperation>() is { } one) AddTree(items, one.Item1);
            if (operation.TryCast<ITwoItemOperation>() is { } two) AddTree(items, two.Item2);
            if (operation.TryCast<SplitOperation>() is { } split) AddTree(items, split._splitItem);
        }
        catch (Exception) { }

        return items;
    }

    private static void AddTree(System.Collections.Generic.List<Item> items, Item item)
    {
        if (item == null) return;
        try
        {
            foreach (var candidate in item.GetAllItems())
            {
                if (candidate != null) items.Add(candidate);
            }
        }
        catch (Exception) { }
    }

    private static int CountKnown(IPlayerSearchController search, System.Collections.Generic.List<Item> items)
    {
        var known = 0;
        for (var i = 0; i < items.Count; i++)
        {
            try
            {
                if (search.IsItemKnown(items[i], items[i].Parent)) known++;
            }
            catch (Exception) { }
        }

        return known;
    }

    private static int CountSearched(IPlayerSearchController search, System.Collections.Generic.List<Item> items)
    {
        var searched = 0;
        for (var i = 0; i < items.Count; i++)
        {
            try
            {
                if (items[i].TryCast<SearchableItem>() is { } container && search.IsSearched(container)) searched++;
            }
            catch (Exception) { }
        }

        return searched;
    }

    private static void MarkKnownTree(IPlayerSearchController search, Item item)
    {
        if (item == null)
        {
            return;
        }

        foreach (var candidate in item.GetAllItems())
        {
            if (candidate != null)
            {
                // raiseEvents: false —— UI 读取的是地址条目；
                // 为玩家只是挪动位置的物品发一个 'found item' 事件会成为这次簿记的可见副作用。
                search.SetItemAsKnown(candidate, false);
            }
        }
    }

    /// <summary>
    /// 在客户端搜索控制器中，把一次中继操作的容器登记为已搜索。
    /// <para>
    /// 客户端用 <c>new ActiveSearchController(profile, this)</c>
    /// （上面的构造函数）构建自己的控制器，而原生的 <c>PlayerSearchController</c>
    /// 构造函数只揭示那一刻存在于 <c>profile.Inventory.Equipment</c>
    /// 内的容器。因此玩家之后产出或移动的每个容器——存放武器的背包、
    /// 重新装填的弹匣落进的挂具、重新捡起的护甲——在控制器的已搜索集合里都缺失，而库存视图和
    /// <c>Player.InventoryController.GetAcceptableItemsNonAlloc</c>
    /// 所读取的原生 <c>ISearchController.IsSearched</c> 会把它们报告为未搜索。
    /// 原生的 <c>InventoryController.Execute</c> 包装维护那个集合；中继的操作经由
    /// <c>ExecuteWithoutDispose</c> 运行并绕过它，所以簿记在这里重复一遍。
    /// </para>
    /// <para>
    /// 只能登记容器：原生的入口是 <c>SetItemAsSearched&lt;TItem&gt;(TItem)
    /// where TItem : SearchableItem</c>，而在 1.2.0.47965 中只有
    /// Backpack/Vest/Pockets/LootContainer/MobContainer 派生自 <c>SearchableItem</c>（弹匣、
    /// 武器和配件不是，它们由 <see cref="MarkOperationItemsKnown"/> 里的已知登记覆盖）。
    /// </para>
    /// </summary>

    /// <summary>
    /// <see cref="MarkOperationItemsSearched(AbstractOperation)"/> 的递归那一半。
    /// <para>
    /// 装/卸弹匣操作只是一个包装：<c>IParentOperation.InternalOperation</c>
    /// 持有真正触及物品的移动与拆分操作，而下面的访问器能读到的正是它们。因此每一层都会被访问，
    /// 并且读取的是 <c>AbstractOperation.Items</c>
    /// ——操作自己的物品集合——而不是去猜某种操作类型实现了哪个访问器。
    /// </para>
    /// </summary>
    private void MarkOperationItemsSearched(IPlayerSearchController search,
        EFT.InventoryLogic.Operations.AbstractOperation operation, int depth)
    {
        if (operation == null || depth > 4)
        {
            return;
        }

        if (operation.TryCast<IParentOperation>() is { } parent && parent.InternalOperation != null)
        {
            MarkOperationItemsSearched(search, parent.InternalOperation, depth + 1);
        }

        // 一次操作中一个坏掉的访问器，绝不能阻止其它操作的标记。
        try
        {
            MarkOperationContainersSearched(search, operation);

            if (operation.TryCast<IOneItemOperation>() is { } one)
            {
                // 当操作产出或移动的是本来就属于我们的容器时，物品本身。
                if (IsOwnItemAddress(one.From1))
                {
                    MarkSearchedTree(search, one.Item1);
                }

                // 接收该物品的容器。对弹匣、武器或拆分出来的弹药堆来说，
                // 这才是关键的那个条目：它们本身不是容器，但它们落进的挂具/背包是，
                // 而库存视图正是依据它的已搜索状态来决定是否显示内容。
                MarkReceivingContainerSearched(search, one.To1);
            }

            if (operation.TryCast<ITwoItemOperation>() is { } two)
            {
                if (IsOwnItemAddress(two.From2))
                {
                    MarkSearchedTree(search, two.Item2);
                }

                MarkReceivingContainerSearched(search, two.To2);
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"{ID} - could not read the items of operation {operation.Id} ({operation.GetIl2CppType().Name}): {exception.Message}");
        }
    }

    /// <summary>把操作自己的物品集合中属于该玩家库存的每个容器都标记出来。</summary>
    private void MarkOperationContainersSearched(IPlayerSearchController search,
        EFT.InventoryLogic.Operations.AbstractOperation operation)
    {
        var items = operation.Items;
        if (items != null)
        {
            foreach (var item in items)
            {
                MarkOwnContainerSearched(search, item);
            }
        }

        if (operation.TryCast<IMultipleItemsOperation>() is { } multiple)
        {
            var multipleItems = multiple.Items;
            if (multipleItems != null)
            {
                foreach (var item in multipleItems)
                {
                    MarkOwnContainerSearched(search, item);
                }
            }
        }
    }

    /// <summary>
    /// 把操作中的一个物品标记为已搜索，前提是它是一个当前位于该玩家自己库存内的容器。
    /// <see cref="MarkContainerSearched"/>
    /// 里的内容检查会让仍装有未见物品（也就是玩家必须先搜的战利品）的容器保持原样。
    /// </summary>
    private void MarkOwnContainerSearched(IPlayerSearchController search, Item item)
    {
        if (item == null || item.TryCast<SearchableItem>() is not { } searchable)
        {
            return;
        }

        try
        {
            if (!TryFindOwnSearchableContainer(searchable.CurrentAddress, out _))
            {
                return;
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"{ID} - could not resolve the container {searchable.Id}: {exception.Message}");
            return;
        }

        MarkContainerSearched(search, searchable);
    }

    /// <summary>
    /// 在确认某容器不再装有玩家从未见过的物品之后，
    /// 把该玩家自己库存中的这个容器登记为已搜索。正是这道守卫保住了搜刮机制：
    /// 仍含未知内容的容器就是玩家必须先搜的战利品容器，它被保持原样。
    /// </summary>
    private void MarkReceivingContainerSearched(IPlayerSearchController search, ItemAddress destination)
    {
        if (destination == null || !TryFindOwnSearchableContainer(destination, out var container) || container == null)
        {
            return;
        }

        MarkContainerSearched(search, container);
    }

    private void MarkSearchedTree(IPlayerSearchController search, Item item)
    {
        if (item == null)
        {
            return;
        }

        foreach (var candidate in item.GetAllItems())
        {
            if (candidate != null && candidate.TryCast<SearchableItem>() is { } searchable)
            {
                MarkContainerSearched(search, searchable);
            }
        }
    }

    private void MarkContainerSearched(IPlayerSearchController search, SearchableItem container)
    {
        if (container == null)
        {
            return;
        }

        try
        {
            // 内容尚未完全已知的容器必须保持未搜索，无论它是被什么产出的。
            if (search.ContainsUnknownItems(container))
            {
                return;
            }

            search.SetItemAsSearched<SearchableItem>(container);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"{ID} - could not mark container {container.Id} as searched: {exception.Message}");
        }
    }

    /// <summary>
    /// 该地址归属于本玩家自己的库存（装备、仓库、任务物品、整理台），
    /// 而不是地面、战利品或尸体容器。来自外部的物品必须保持未检查状态。
    /// </summary>
    private bool IsOwnItemAddress(ItemAddress address)
    {
        return TryFindOwnSearchableContainer(address, out _);
    }

    /// <summary>
    /// 沿地址链向上走，直到抵达本玩家库存的某个根。
    /// 对任何不属于玩家自己这棵树的东西返回 false，且不返回容器。
    /// 这个遍历是有界的，因为一条断裂的地址链绝不能把主线程转死。
    /// </summary>
    private bool TryFindOwnSearchableContainer(ItemAddress address, out SearchableItem container)
    {
        container = null;

        try
        {
            var inventory = Inventory;
            if (address == null || inventory == null)
            {
                return false;
            }

            var current = address.Container;
            for (var depth = 0; current != null && depth < 32; depth++)
            {
                var owner = current.ParentItem;
                if (owner == null)
                {
                    break;
                }

                if (IsOwnInventoryRoot(inventory, owner))
                {
                    return true;
                }

                if (container == null && owner.TryCast<SearchableItem>() is { } searchable)
                {
                    container = searchable;
                }

                var parentAddress = owner.CurrentAddress;
                if (parentAddress == null)
                {
                    break;
                }

                current = parentAddress.Container;
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"{ID} - could not resolve an inventory address of an operation item: {exception.Message}");
        }

        container = null;
        return false;
    }

    private static bool IsOwnInventoryRoot(EFT.InventoryLogic.Inventory inventory, Item item)
    {
        return IsSameItem(item, inventory.Equipment)
            || IsSameItem(item, inventory.Stash)
            || IsSameItem(item, inventory.QuestRaidItems)
            || IsSameItem(item, inventory.QuestStashItems)
            || IsSameItem(item, inventory.SortingTable);
    }

    private static bool IsSameItem(Item left, Item right)
    {
        return left != null && right != null && left.Pointer == right.Pointer;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 权威重同步：主机拒绝了该操作，并把它自己的库存作为描述符发了回来。
    // 客户端据此重建每一个 Item，所以搜索控制器里的条目（基于实例和基于地址的）
    // 都无法在重建后存活。下面的一切在重建之前捕获被拒操作的物品、
    // 决定本地实例究竟能否保留，并在之后重新登记簿记。
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 被拒操作的一个物品，在权威快照替换本地物品图之前被捕获。
    /// 重建之后实例本身就没有了，因此只有它的 id 和两个搜索标志能存活。
    /// </summary>
    internal readonly struct ResyncItemSnapshot
    {
        public readonly string Id;
        public readonly IntPtr Pointer;
        public readonly bool KnownBefore;
        public readonly bool SearchedBefore;

        internal ResyncItemSnapshot(string id, IntPtr pointer, bool knownBefore, bool searchedBefore)
        {
            Id = id;
            Pointer = pointer;
            KnownBefore = knownBefore;
            SearchedBefore = searchedBefore;
        }
    }

    /// <summary>在 <c>Operation.Dispose()</c> 与重建之前，收集被拒操作所触及的物品。</summary>
    internal System.Collections.Generic.List<ResyncItemSnapshot> CaptureResyncItems(
        EFT.InventoryLogic.Operations.AbstractOperation operation)
    {
        var captured = new System.Collections.Generic.List<ResyncItemSnapshot>();
        try
        {
            var search = PlayerSearchController;
            if (search == null || operation == null)
            {
                return captured;
            }

            CaptureResyncItems(search, operation, captured, new System.Collections.Generic.HashSet<string>(), 0);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[SyncDiag] item-mark-failed op=capture item=none stage=capture error={exception.Message}");
        }

        return captured;
    }

    private void CaptureResyncItems(IPlayerSearchController search,
        EFT.InventoryLogic.Operations.AbstractOperation operation,
        System.Collections.Generic.List<ResyncItemSnapshot> captured,
        System.Collections.Generic.HashSet<string> seen, int depth)
    {
        if (operation == null || depth > 4)
        {
            return;
        }

        if (operation.TryCast<IParentOperation>() is { } parent && parent.InternalOperation != null)
        {
            CaptureResyncItems(search, parent.InternalOperation, captured, seen, depth + 1);
        }

        var items = operation.Items;
        if (items != null)
        {
            foreach (var item in items)
            {
                CaptureResyncItem(search, captured, seen, item);
            }
        }

        if (operation.TryCast<IMultipleItemsOperation>() is { } multiple)
        {
            var multipleItems = multiple.Items;
            if (multipleItems != null)
            {
                foreach (var item in multipleItems)
                {
                    CaptureResyncItem(search, captured, seen, item);
                }
            }
        }

        if (operation.TryCast<IOneItemOperation>() is { } one)
        {
            CaptureResyncItem(search, captured, seen, one.Item1);
        }

        if (operation.TryCast<ITwoItemOperation>() is { } two)
        {
            CaptureResyncItem(search, captured, seen, two.Item2);
        }
    }

    private void CaptureResyncItem(IPlayerSearchController search,
        System.Collections.Generic.List<ResyncItemSnapshot> captured,
        System.Collections.Generic.HashSet<string> seen, Item item)
    {
        if (item == null)
        {
            return;
        }

        string id;
        try
        {
            id = item.Id;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[SyncDiag] item-mark-failed op=capture item=unknown stage=capture-id error={exception.Message}");
            return;
        }

        if (string.IsNullOrEmpty(id) || !seen.Add(id))
        {
            return;
        }

        var knownBefore = false;
        var searchedBefore = false;
        try
        {
            knownBefore = search.IsItemKnown(item, item.Parent);
            if (item.TryCast<SearchableItem>() is { } searchable)
            {
                searchedBefore = search.IsSearched(searchable);
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[SyncDiag] item-mark-failed op=capture item={id} stage=capture-state error={exception.Message}");
        }

        captured.Add(new ResyncItemSnapshot(id, item.Pointer, knownBefore, searchedBefore));
    }

    /// <summary>
    /// 决定是否可以忽略权威快照、改用本地的物品图。
    /// <para>
    /// 保留实例可以保住控制器的按实例搜索条目和已打开的库存视图，
    /// 但只有在快照可证与本地树完全一致时才是安全的（相同的物品 id、
    /// 相同的地址哈希——<c>ItemAddress.GetHashSum</c>
    /// 覆盖格子位置与槽位——以及相同的堆叠数量）<b>且</b>被拒操作只可能改动位置与数量。
    /// 其它任何情况都会重建，因为这个比较看不见的差异（耐久、已装配件……）否则会一直偏差下去。
    /// </para>
    /// </summary>
    internal bool TryKeepLocalInstances(Inventory snapshot,
        EFT.InventoryLogic.Operations.AbstractOperation operation, out string reason)
    {
        try
        {
            var inventory = Inventory;
            if (snapshot == null)
            {
                reason = "no-snapshot";
                return false;
            }

            if (inventory == null)
            {
                reason = "no-local-inventory";
                return false;
            }

            if (operation == null)
            {
                reason = "no-operation";
                return false;
            }

            if (!IsPlacementOnlyOperation(operation, 0))
            {
                reason = "operation-not-placement-only";
                return false;
            }

            if (!FingerprintsEqual(inventory, snapshot))
            {
                reason = "structure-differs";
                return false;
            }

            reason = "identical-structure";
            return true;
        }
        catch (Exception exception)
        {
            reason = "keep-check-failed:" + exception.Message;
            return false;
        }
    }

    /// <summary>
    /// 该操作是只挪动物品位置，还是还会改动堆叠数量。它们是 <see cref="FingerprintsEqual"/>
    /// 里的指纹唯一能看到的改动，所以只有它们才允许保留本地实例。
    /// </summary>
    private static bool IsPlacementOnlyOperation(EFT.InventoryLogic.Operations.AbstractOperation operation, int depth)
    {
        if (operation == null || depth > 4)
        {
            return false;
        }

        if (operation.TryCast<IParentOperation>() is { } parent && parent.InternalOperation != null)
        {
            return IsPlacementOnlyOperation(parent.InternalOperation, depth + 1);
        }

        return operation.TryCast<MoveOperation>() != null
            || operation.TryCast<SwapOperation>() != null
            || operation.TryCast<MergeOperation>() != null
            || operation.TryCast<SplitOperation>() != null
            || operation.TryCast<TransferOperation>() != null
            || operation.TryCast<ThrowOperation>() != null
            || operation.TryCast<RemoveOperation>() != null
            || operation.TryCast<LoadMagOperation>() != null
            || operation.TryCast<UnloadMagOperation>() != null;
    }

    /// <summary>把整棵玩家库存映射为 "物品 id → 地址哈希:堆叠数量:模板"。</summary>
    private static System.Collections.Generic.Dictionary<string, string> BuildInventoryFingerprint(Inventory inventory)
    {
        var fingerprint = new System.Collections.Generic.Dictionary<string, string>();
        var items = inventory.GetPlayerItems(EPlayerItems.All);
        if (items == null)
        {
            return fingerprint;
        }

        foreach (var item in items)
        {
            if (item == null)
            {
                continue;
            }

            var address = item.Parent;
            var addressHash = address == null ? 0 : address.GetHashSum();
            fingerprint[item.Id] = string.Concat(addressHash.ToString(), ":", item.StackObjectsCount.ToString(),
                ":", item.StringTemplateId);
        }

        return fingerprint;
    }

    private static bool FingerprintsEqual(Inventory left, Inventory right)
    {
        var first = BuildInventoryFingerprint(left);
        var second = BuildInventoryFingerprint(right);
        if (first.Count != second.Count)
        {
            return false;
        }

        foreach (var pair in first)
        {
            if (!second.TryGetValue(pair.Key, out var value) || value != pair.Value)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 在被捕获操作物品所对应的（可能已重建的）库存中重新登记它们：
    /// 每个物品在其当前地址被标记为已知，而在重同步之前就已搜索过的容器会被再次标记为已搜索。
    /// 内容被刻意不碰（见调用处的注释）。每个物品写一行 <c>[SyncDiag]</c>，
    /// 这样一份突袭日志就能显示这些 id 是否在重建中存活。
    /// </summary>
    internal void RestoreOperationItemsAfterResync(System.Collections.Generic.List<ResyncItemSnapshot> captured,
        bool keptLocalInstances, ushort operationId)
    {
        if (captured == null || captured.Count == 0)
        {
            return;
        }

        try
        {
            var search = PlayerSearchController;
            if (search == null)
            {
                return;
            }

            var lookup = new System.Collections.Generic.Dictionary<string, Item>();
            var items = Inventory?.GetPlayerItems(EPlayerItems.All);
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item != null && !string.IsNullOrEmpty(item.Id))
                    {
                        lookup[item.Id] = item;
                    }
                }
            }

            foreach (var snapshot in captured)
            {
                var found = lookup.TryGetValue(snapshot.Id, out var item) && item != null;
                var sameInstance = found && item.Pointer == snapshot.Pointer;
                var knownAfter = false;

                if (found)
                {
                    try
                    {
                        // 只登记物品本身：它的内容要么已由
                        // ReplaceInventoryFromNetwork
                        // 恢复（此前已知的一切），要么必须保持未知——在这
                        // 里深入一个刚捡起的容器会揭示玩家尚未搜索的战利品。
                        search.SetItemAsKnown(item, false);
                        // [InvDiag]（2026-10-27，T4）
                        // 拒绝路径只在容器于重同步之前就已搜索过时才恢复已搜索标志。
                        // CaptureResyncItems 在本地执行之后、
                        // 任何标记之前运行（标记只发生在成功路径上），
                        // 所以对一个刚被这次操作移进玩家自己库存的容器来说，SearchedBefore
                        // 在结构上永远是 false——重建后的树于是显示它 "未搜索"，
                        // 而同样一次操作走成功路径却回来是已搜索。因此位于玩家自己库存内的容器，
                        // 其标记方式与成功路径完全一致；MarkContainerSearched
                        // 仍会拒绝装有玩家未见内容的容器，所以搜刮机制未被改动。
                        if (item.TryCast<SearchableItem>() is { } searchable
                            && (snapshot.SearchedBefore || IsOwnItemAddress(item.CurrentAddress)))
                        {
                            MarkContainerSearched(search, searchable);
                        }

                        knownAfter = search.IsItemKnown(item, item.Parent);
                    }
                    catch (Exception exception)
                    {
                        FikaGlobals.LogWarning($"[SyncDiag] item-mark-failed op={operationId} item={snapshot.Id} " +
                            $"stage=restore error={exception.Message}");
                    }
                }

                FikaGlobals.LogInfo($"[SyncDiag] op={operationId} item={snapshot.Id} sameId={found} " +
                    $"sameInstance={sameInstance} knownBefore={snapshot.KnownBefore} knownAfter={knownAfter} " +
                    $"searchedBefore={snapshot.SearchedBefore} keptInstances={keptLocalInstances}");
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[SyncDiag] item-mark-failed op={operationId} item=none stage=restore-all " +
                $"error={exception.Message}");
        }
    }

    /// <summary>一个已解码快照的物品数量，供重建处的重同步诊断使用。</summary>
    internal static int CountSnapshotItems(Inventory inventory)
    {
        try
        {
            var items = inventory?.GetPlayerItems(EPlayerItems.All);
            if (items == null)
            {
                return 0;
            }

            var count = 0;
            foreach (var item in items)
            {
                if (item != null)
                {
                    count++;
                }
            }

            return count;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[SyncDiag] item-mark-failed op=descriptor item=none stage=count error={exception.Message}");
            return -1;
        }
    }
}
