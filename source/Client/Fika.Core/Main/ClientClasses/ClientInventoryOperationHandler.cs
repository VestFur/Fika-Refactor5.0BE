using EFT.InventoryLogic.Operations;
using System;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.Communication;
using static Fika.Core.Main.ClientClasses.ClientInventoryController;

namespace Fika.Core.Main.ClientClasses;

public sealed class ClientInventoryOperationHandler : IDisposable
{
    public ClientInventoryController InventoryController;
    public EFT.InventoryLogic.Operations.AbstractOperation Operation;
    public Callback Callback;

    public IResult OperationResult;
    public ServerOperationStatus ServerStatus;

    public Callback ExecuteResultDelegate;
    public Callback HandleResultDelegate;
    public Action<ServerOperationStatus> ServerStatusDelegate;
    public ushort CallbackId { get; private set; }
    private bool _awaitingInventoryResync;
    private bool _inventoryResyncApplied;
    private Inventory _pendingAuthoritativeInventory;
    private uint _generation;
    private bool _active;
    private bool _receivedServerStatus;
    private bool _completed;

    /// <summary>这个池化的处理器是否仍在驱动一个操作。</summary>
    internal bool IsActive => _active;

    /// <summary>服务器是否已经用任意状态作出过应答。</summary>
    internal bool ReceivedServerStatus => _receivedServerStatus;

    /// <summary>
    /// 当服务器始终没有应答时，以本地失败结束该操作。
    /// 避免一次丢失的确认把玩家永久锁在
    /// 交互状态中。
    /// </summary>
    internal void FailWithoutServerStatus(string reason) => FailLocally(reason);

    public void Set(ClientInventoryController inventoryController, EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
        InventoryController = inventoryController;
        Operation = operation;
        Callback = callback;
        CallbackId = operation.Id;
        _active = true;
        _receivedServerStatus = false;
        _completed = false;
        var generation = unchecked(++_generation);
        ExecuteResultDelegate = (Callback)(result => ExecuteResult(generation, result));
        HandleResultDelegate = (Callback)(result => HandleResult(generation, result));
        ServerStatusDelegate = status => ReceiveStatusFromServer(generation, status);
    }

    public static ClientInventoryOperationHandler CreateInstance()
    {
        return new ClientInventoryOperationHandler();
    }

    private ClientInventoryOperationHandler()
    {
    }

    private void ReceiveStatusFromServer(uint generation, ServerOperationStatus serverStatus)
    {
        if (!_active || generation != _generation) return;
        // 重连期间可靠投递可能重复一个终态状态。
        // 操作结束后绝不再执行同一个原生操作。
        if (_receivedServerStatus)
        {
            if (ServerStatus.Status is EOperationStatus.Succeeded or EOperationStatus.Failed) return;
            if (serverStatus.Status == EOperationStatus.Started && ServerStatus.Status == EOperationStatus.Started) return;
        }
        _receivedServerStatus = true;
        ServerStatus = serverStatus;
        switch (serverStatus.Status)
        {
            case EOperationStatus.Started:
                // 抛异常的原生执行绝不能把玩家留在交互状态中、并伴随一个
                // 永不触发的回调。改为在本地让该操作失败，
                // 由调用方去释放它的状态。
                try
                {
                    Operation.ExecuteWithoutDispose(ExecuteResultDelegate);
                }
                catch (Exception exception)
                {
                    FikaGlobals.LogError($"{InventoryController?.ID} - Client operation threw during local execution: " +
                        $"{Operation?.Id} - {Operation}\r\nError: {exception}");
                    FailLocally(exception.Message);
                }
                return;
            case EOperationStatus.Succeeded:
                if (Operation.TryCast<TransferExfiltrationPaymentOperation>() is { } paidOperation)
                {
                    ConfirmCarExtractPayment(paidOperation);
                }
                // [QuestOp] 任务操作通常在本地执行，主机从不应答它，
                // 因此这行只在某个任务操作被转发时才会出现。
                if (QuestOperations.TryDescribe(Operation, out var appliedQuestDetail))
                {
                    FikaGlobals.LogInfo($"[QuestOp] host applied {Operation.GetIl2CppType().Name} ({appliedQuestDetail})");
                }
                InventoryController?.SyncInventoryToProfile();
                // （2026-10-21）玩家真正看到的路径：一次*成功*的操作永远走不到下面的
                // 重新同步（那一半只在主机拒绝时才运行），所以原生 Execute 包装本应完成的
                // 簿记从未为它重放过——这就是 W10 的物品诊断
                // 全都是零的原因。注册及其前后计数就在这里完成，在 profile 同步之后，
                // 作用在玩家接下来要打开的那棵树上。
                if (InventoryController != null)
                {
                    ReportRelayedOperationBookkeeping(InventoryController.ApplyRelayedOperationBookkeeping(Operation));
                }
                // （2026-10-20）profile 同步会重新读取物品栏；本地执行所注册的 "known"
                // 簿记并不属于那份 profile，因此本次操作创建出来的物品在下次打开物品栏界面时
                // 又会变回未检查状态。上面那次注册就是这笔
                // 簿记（W8/W10 都没把它接到任何地方——直到 2026-10-21 它都没有调用方）。
                HandleResultDelegate.Invoke(SuccessfulResult.New);
                return;
            case EOperationStatus.Failed:
                FikaGlobals.LogError($"{InventoryController.ID} - Client operation rejected by server: {Operation.Id} - {LogSafe.Ref(Operation)}\r\nReason: {serverStatus.Error}");
                // [CarExtract] 付费撤离点交互会根据 TransferExitItem 的返回值
                // 显示它的 "handed over" 消息，而这远在主机应答之前。
                // 当主机拒绝这笔付款时，必须纠正那个
                // 预测，否则玩家会以为司机已经收到钱，
                // 而钱其实还在物品栏里。
                if (Operation.TryCast<TransferExfiltrationPaymentOperation>() is { } rejectedPayment)
                {
                    var rejectedPoint = rejectedPayment._result?.PointName;
                    FikaGlobals.LogError($"[CarExtract] the host rejected the payment for '{rejectedPoint}' " +
                        $"(operation {Operation.Id}): {serverStatus.Error}");
                    FikaNotifications.DisplayMessageNotification(
                        $"[CarExtract] The host rejected the payment for '{rejectedPoint}': {serverStatus.Error}",
                        iconType: ENotificationIconType.Alert);
                }
                if (QuestOperations.TryDescribe(Operation, out var rejectedQuestDetail))
                {
                    FikaGlobals.LogError($"[QuestOp] host rejected {Operation.GetIl2CppType().Name} " +
                        $"({rejectedQuestDetail}): {serverStatus.Error}");
                }
                ServerStatus = serverStatus;
                // Terminate 可能同步调用 HandleResult。先标记
                // 重新同步，这样该回调就无法在快照到达之前
                // 释放并把本处理器归还到池中。
                _awaitingInventoryResync = true;
                if (Operation.Status.InProgress()
                    && Operation.TryCast<IServerDependentOperation>() is { } serverDependentOperation)
                {
                    serverDependentOperation.Terminate();
                }
                if (_pendingAuthoritativeInventory != null)
                {
                    var authoritativeInventory = _pendingAuthoritativeInventory;
                    _pendingAuthoritativeInventory = null;
                    ApplyAuthoritativeInventory(authoritativeInventory);
                }
                return;
            default:
                FikaGlobals.LogError("ReceiveStatusFromServer: Status was missing?");
                break;
        }
    }

    /// <summary>
    /// [CarExtract] 主机已确认付款，因此本客户端必须把自己
    /// 的玩家报告为已在撤离点排队。在本地执行同一个
    /// 操作通常就能做到；当那次执行被拒绝或
    /// 被中止时，该撤离点对付款方始终处于未满足状态——它会显示倒计时但
    /// 从不撤离，因此这里重放原生的移交处理。
    /// </summary>
    private void ConfirmCarExtractPayment(TransferExfiltrationPaymentOperation payment)
    {
        try
        {
            var pointName = payment._result?.PointName;
            var player = InventoryController?.Player;
            if (string.IsNullOrEmpty(pointName) || player == null)
            {
                return;
            }

            var point = FikaExfilManager.FindExfiltrationPoint(pointName);
            if (point == null)
            {
                FikaGlobals.LogError($"[CarExtract] the host confirmed a payment for '{pointName}' but this client " +
                    "has no such exfiltration point");
                return;
            }

            // 主机已从这个物品栏它自己的副本里取走了钱。当同一操作的
            // 本地预测没有生效时，双方在该堆叠上的认知会不一致，
            // 直到下一次物品栏重新同步——而那只会在拒绝时发生——
            // 所以至少让它可见。
            var localStatus = payment.Status;
            if (localStatus.Finished() && localStatus != EOperationStatus.Succeeded)
            {
                FikaGlobals.LogError($"[CarExtract] the local execution of the payment for '{pointName}' ended as " +
                    $"{localStatus} while the host succeeded; the money stack stays out of sync until the next resync");
            }

            if (FikaExfilManager.HasPlayerPaid(point, player))
            {
                FikaGlobals.LogInfo($"[CarExtract] payment confirmed by the host: point '{pointName}' status " +
                    $"{point.Status}, queued {point.QueuedPlayers?.Count ?? 0}");
                return;
            }

            FikaGlobals.LogWarning($"[CarExtract] the host confirmed the payment for '{pointName}' but the local " +
                $"point had not queued {player.ProfileId}; applying the handover locally");
            point.OnItemTransferred(player);
            FikaGlobals.LogInfo($"[CarExtract] local point '{pointName}' is now status {point.Status}, " +
                $"queued {point.QueuedPlayers?.Count ?? 0}");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[CarExtract] failed to confirm the payment locally: {exception}");
        }
    }

    /// <summary>
    /// 以本地失败结束一个仍待处理的操作，使调用方的回调
    /// 总能被执行。用于原生执行在服务器来得及报告
    /// 终态状态之前就抛出异常的情况。
    /// </summary>
    private void FailLocally(string error)
    {
        if (!_active || _completed) return;
        _completed = true;
        try
        {
            if (Operation != null && Operation.Status.InProgress()
                && Operation.TryCast<IServerDependentOperation>() is { } serverDependentOperation)
            {
                serverDependentOperation.Terminate();
            }
        }
        catch (Exception terminateException)
        {
            FikaGlobals.LogWarning($"[InvDiag] terminate-on-failure threw {terminateException.GetType().Name}: {terminateException.Message} " +
                "- continuing with the local failure so the interaction state is still released", "ClientInventoryOperationHandler");
                    }

                ServerStatus = new ServerOperationStatus(EOperationStatus.Failed, error);
        OperationResult = new FailedResult(error ?? "The inventory operation failed locally.");
        // 地面拾取在原生 PickUpState/交互队列内部运行。
        // 失败的操作必须释放那份呈现状态，否则
        // 玩家会一直被锁住：在剩余的战局中无法转身、无法移动、
        // 也无法获得交互提示。
        Fika.Core.Main.Utils.NativeInteractionRelease.Release(InventoryController?.Player, "inventory-operation-failed");

        // （2026-10-26 W26）看门狗失败意味着主机从未应答——但本次操作的本地执行
        // 已经运行过（Started 分支会执行它，然后才等待终态状态）。Succeeded
        // 分支会在那次执行之后重放原生的 known/searched 簿记；这条路径过去会
        // 跳过它，于是该操作产生的每个物品在整个剩余战局中都停留在 "unexamined / unsearched"
        // （即所报告的"物品消失，然后又回来却是未搜索"）。仅在完全没有任何状态到达时才
        // 这样做：真正拒绝了该操作的主机会替换整棵树，而那条路径拥有它自己的簿记。
        // （2026-10-26 W27）拒绝路径会等待主机的权威快照（_awaitingInventoryResync）；
        // 如果那份快照始终不到达，完成看门狗就会走到这里，而被拒绝操作的本地
        // 执行仍在树中，且关于缺失快照什么都没记录。要把这一点说出来：沉默地
        // 放弃正是让这条链在许多轮里都无法诊断的那类盲点。
        if (_receivedServerStatus && _awaitingInventoryResync)
        {
            FikaGlobals.LogError($"{InventoryController?.ID} - [SyncDiag] op={CallbackId} resync-timeout: the host " +
                "rejected this operation but its authoritative snapshot never arrived; local changes are NOT rolled " +
                "back and the next full resync has to fix them (" + error + ')');
        }
        if (!_receivedServerStatus && InventoryController != null)
        {
            try
            {
                InventoryController.SyncInventoryToProfile();
                ReportRelayedOperationBookkeeping(InventoryController.ApplyRelayedOperationBookkeeping(Operation));
            }
            catch (Exception bookkeepingException)
            {
                FikaGlobals.LogError($"{InventoryController.ID} - bookkeeping after a local failure threw: {bookkeepingException}");
            }
        }

        try
        {
            Operation?.Dispose();
        }
        catch (Exception disposeException)
        {
            FikaGlobals.LogError($"{InventoryController?.ID} - Failed to dispose the failed operation: {disposeException}");
        }

        try
        {
            Callback?.Invoke(OperationResult);
        }
        catch (Exception callbackException)
        {
            FikaGlobals.LogError($"{InventoryController?.ID} - Client operation callback threw: {callbackException}");
        }
        finally
        {
            InventoryController?.ReturnHandler(this);
        }
    }

    private void ExecuteResult(uint generation, IResult executeResult)
    {
        if (!_active || generation != _generation) return;
        if (!executeResult.Succeed)
        {
            FikaGlobals.LogError($"{InventoryController.ID} - Client operation critical failure: {Operation.Id} server status:  - {LogSafe.Ref(Operation)}\r\nError: {executeResult.Error}");
        }
        else
        {
            // 原生 Execute 包装会把一次移动的物品注册为已知；而转发的本地
            // 执行绕过了它（见 MarkOperationItemsKnown）。
        }

        HandleResultDelegate.Invoke(executeResult);
    }

    private void HandleResult(uint generation, IResult result)
    {
        if (!_active || _completed || generation != _generation) return;
        var result2 = OperationResult;
        if (result2?.Failed != true)
        {
            OperationResult = result;
        }
        var serverStatus = ServerStatus.Status;
        if (!serverStatus.Finished())
        {
            return;
        }

        if (_awaitingInventoryResync)
        {
            return;
        }
        var localStatus = Operation.Status;
        if (localStatus.InProgress())
        {
            // 一次丢弃是 EFT.InventoryLogic.Operations.ThrowOperation：一个异步操作，
            // 它先把物品从物品栏中取出，并在最后才调用 GameWorld.ThrowItem。
            // 主机把该操作转发给除源以外的每个对等端，所以
            // 这次本地执行是唯一能为丢弃者创建地面对象的东西。
            // 在这里 Dispose 它会把物品移除却从不生成它（被丢弃的
            // 背包只有主机能看到）。让它跑完；确认
            // 看门狗仍然是兜底手段。
            if (Operation.TryCast<ThrowOperation>() != null)
            {
                FikaGlobals.LogWarning($"{InventoryController.ID} - The host reported {serverStatus} while the local " +
                    $"discard {Operation.Id} is still running; letting it finish so the ground item is created");
                return;
            }
            if (Operation.TryCast<IServerDependentOperation>() is { } ginterface)
            {
                ginterface.Terminate();
            }
            if (_completed || !Operation.Status.InProgress()) return;
            // 主机报告了终态状态，而本地执行从未完成。
            // 在这里返回会让调用方的回调不被执行，而它会把
            // 玩家锁在交互状态中直到战局结束（此后每一次
            // 地面拾取都会被拒绝）。在本地释放调用方。
            FikaGlobals.LogError($"{InventoryController.ID} - Operation {Operation.Id} is still in progress " +
                $"after the server reported {serverStatus}, completing it locally to release the interaction state");
            FailLocally($"The host reported {serverStatus} before the local operation finished.");
            return;
        }
        _completed = true;
        if (serverStatus != localStatus && localStatus.Finished())
        {
            FikaGlobals.LogError($"{InventoryController.ID} - Operation critical failure - status mismatch: {Operation.Id} server status: {serverStatus} client status: {localStatus} - {LogSafe.Ref(Operation)}");
        }

        try
        {
            Operation.Dispose();
        }
        catch (Exception disposeException)
        {
            FikaGlobals.LogError($"{InventoryController.ID} - Failed to dispose the completed operation: {disposeException}");
        }

        try
        {
            Callback?.Invoke(OperationResult);
        }
        catch (Exception callbackException)
        {
            FikaGlobals.LogError($"{InventoryController.ID} - Client operation callback threw: {callbackException}");
        }
        finally
        {
            InventoryController.ReturnHandler(this);
        }
    }

    public void Dispose()
    {
        _active = false;
        _receivedServerStatus = false;
        _completed = false;
        Operation = null;
        Callback = null;
        InventoryController = null;
        OperationResult = null;
        ServerStatus = default;
        CallbackId = default;
        _awaitingInventoryResync = false;
        _inventoryResyncApplied = false;
        _pendingAuthoritativeInventory = null;
        ExecuteResultDelegate = null;
        HandleResultDelegate = null;
        ServerStatusDelegate = null;
    }

    internal void ReceiveAuthoritativeInventory(Inventory inventory)
    {
        if (!_awaitingInventoryResync)
        {
            _pendingAuthoritativeInventory = inventory;
            return;
        }

        ApplyAuthoritativeInventory(inventory);
    }

    private void ApplyAuthoritativeInventory(Inventory inventory)
    {
        Fika.Core.Main.Utils.NativeInteractionRelease.Release(InventoryController?.Player, "authoritative-inventory");
        if (_inventoryResyncApplied)
        {
            return;
        }

        _inventoryResyncApplied = true;
        try
        {
            if (inventory == null)
            {
                // ReplaceInventory(null) 会让控制器完全没有物品图。
                // 改为释放调用方，而不是把整场战局弄坏。
                FikaGlobals.LogError($"{InventoryController?.ID} - The authoritative inventory snapshot was empty; " +
                    "keeping the local inventory");
                OperationResult = new FailedResult(ServerStatus.Error ?? "The host sent no inventory snapshot.");
                Callback?.Invoke(OperationResult);
                return;
            }

            // [SyncDiag] 快照会重建每一个 Item，这会使搜索控制器的条目失效（它们
            // 以实例和地址为键）。趁被拒绝操作的物品还存在时把它们捕获下来。
            var capturedItems = InventoryController.CaptureResyncItems(Operation);
            var keptLocalInstances = InventoryController.TryKeepLocalInstances(inventory, Operation, out var keepReason);
            FikaGlobals.LogInfo($"[SyncDiag] op={CallbackId} resync-begin items={capturedItems.Count} " +
                $"snapshotItems={ClientInventoryController.CountSnapshotItems(inventory)} " +
                $"keepLocal={keptLocalInstances} reason={keepReason}");

            Operation?.Dispose();
            // 这次替换会装入一棵全新的物品图，因此已打开的搜刮或
            // 物品栏界面可能仍持有旧物品并渲染成空白（即所报告的
            // 黑屏物品栏）。曾尝试强制关闭这些界面，结果更糟：
            // 一次由把弹药拖到武器上触发的重新同步把玩家
            // 踢出了物品栏，留下悬空的拖拽状态，隐藏了所有 UI 元素
            // 并阻塞了全部输入。改为报告这次重新同步，不要去动这些界面。
            FikaGlobals.LogWarning($"{InventoryController.ID} - Applying the authoritative inventory " +
                "(an open screen may need a refresh)");
            if (keptLocalInstances)
            {
                // [SyncDiag] 结构与 id 完全一致：重建只会替换实例、丢掉
                // 搜索簿记并让任何已打开的界面变空。保留本地物品图。
                FikaGlobals.LogInfo($"[SyncDiag] op={CallbackId} resync-kept-local reason={keepReason}");
                InventoryController.SyncInventoryToProfile();
            }
            else
            {
                InventoryController.ReplaceInventoryFromNetwork(inventory);
            }

            // 在当前生效的那棵物品图中重新注册该操作的物品，然后逐物品报告结果。
            InventoryController.RestoreOperationItemsAfterResync(capturedItems, keptLocalInstances, CallbackId);
            OperationResult = new FailedResult(ServerStatus.Error ?? "The server rejected the inventory operation.");
            Callback?.Invoke(OperationResult);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"{InventoryController.ID} - Failed to apply authoritative inventory: {exception}");
            Callback?.Fail(exception.Message);
        }
        finally
        {
            InventoryController.ReturnHandler(this);
        }
    }

    /// <summary>
    /// （2026-10-21）对一个已应用操作的物品簿记做有界报告。一个会话中前
    /// <see cref="MaxBookkeepingLines"/> 个操作各得到一行（"我刚移动的物品未检查"
    /// 就是从这读出来的），其余的都归入每次战局一行汇总，这样一个满背包的
    /// 搜刮也不会把日志埋掉。
    /// </summary>
    private void ReportRelayedOperationBookkeeping(string detail)
    {
        try
        {
            if (string.IsNullOrEmpty(detail)) return;
            if (_bookkeepingLines < MaxBookkeepingLines)
            {
                _bookkeepingLines++;
                FikaGlobals.LogInfo($"[SyncDiag] op={CallbackId} bookkeeping {detail}", "ClientInventoryOperationHandler");
                return;
            }

            _bookkeepingSuppressed++;
            if (_bookkeepingSuppressed % 32 == 0)
            {
                FikaGlobals.LogInfo($"[SyncDiag] bookkeeping lines suppressed after {MaxBookkeepingLines}: " +
                    $"{_bookkeepingSuppressed} more operation(s) applied", "ClientInventoryOperationHandler");
            }
        }
        catch (Exception)
        {
            // 诊断绝不能破坏操作回调。
        }
    }

    private const int MaxBookkeepingLines = 40;
    private static int _bookkeepingLines;
    private static int _bookkeepingSuppressed;

    /// <summary>
    /// [InvDiag]（2026-10-27，T4）为下一场战局重新开启这份有界簿记报告。计数器是静态的，
    /// 所以没有它，报告在 <see cref="MaxBookkeepingLines"/> 个操作之后就会在进程剩余时间里彻底沉默，
    /// 之后的战局根本无法评判。由 <c>ClientInventoryController.ClearPool</c> 调用。
    /// </summary>
    internal static void ResetBookkeepingReport()
    {
        _bookkeepingLines = 0;
        _bookkeepingSuppressed = 0;
    }

    private string FindOperationItemId()
    {
        try
        {
            if (Operation.TryCast<MoveOperation>() is { } move)
                return move.Item?.Id;
            if (Operation.TryCast<RemoveOperation>() is { } remove)
                return remove.Item?.Id;
        }
        catch
        {
            // 诊断绝不能影响操作回调。
        }
        return null;
    }
}
