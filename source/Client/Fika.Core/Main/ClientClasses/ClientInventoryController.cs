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
    /// How long a client waits for the host to acknowledge a sent inventory
    /// operation before failing it locally.
    /// </summary>
    private const int OperationAcknowledgementTimeoutMs = 30000;

    /// <summary>
    /// The acknowledgement only means "the host received the operation". A
    /// missing terminal status afterwards keeps the operation in progress,
    /// which locks the requesting player in the interaction state (ground loot
    /// pickup) until the raid ends, so the same watchdog releases it locally.
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
    /// Paid exfiltration point ("car extract") payments this client dispatched
    /// and whose operation has not finished yet, keyed by exfil point name.
    /// The native interaction keeps offering the handover until the point
    /// reports the player as queued, so without this every extra click would
    /// queue another payment.
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
    /// Gets an inventory handler
    /// </summary>
    /// <returns>A pooled handler</returns>
    public ClientInventoryOperationHandler GetHandler()
    {
        return _clientInventoryOperationHandlerPool.Get();
    }

    /// <summary>
    /// Returns a handler
    /// </summary>
    /// <param name="handler">The handler to return</param>
    public void ReturnHandler(ClientInventoryOperationHandler handler)
    {
        // A late completion from an earlier operation must not remove the
        // current handler when the native ushort operation ID wraps around.
        if (_activeOperationHandlers.TryGetValue(handler.CallbackId, out var current) &&
            ReferenceEquals(current, handler))
        {
            _activeOperationHandlers.Remove(handler.CallbackId);
            // The server callback registration has to leave with its handler. It
            // is normally removed by FikaPlayer.HandleCallbackFromServer when a
            // terminal status arrives, but every FailLocally path (30 s
            // acknowledgement watchdog, 50 s completion watchdog, resync decode
            // failure, "still in progress after the server reported X") ends the
            // operation WITHOUT an answer, and its registration stayed in
            // FikaPlayer.OperationCallbacks for the rest of the raid.
            // FikaPlayer.WaitingForCallback reads that dictionary and the client
            // firearm/grenade controllers return false from CanPressTrigger and
            // CanStartReload while it is non-empty, so one failed operation kept
            // the local player unable to shoot.
            FikaPlayer.OperationCallbacks.Remove(handler.CallbackId);
        }
        _clientInventoryOperationHandlerPool.ReturnHandler(handler);
    }

    internal void ReceiveInventoryResync(InventoryResyncPacket packet)
    {
        if (_activeOperationHandlers.TryGetValue(packet.CallbackId, out var handler))
        {
            // Decoding the authoritative snapshot must never abort the packet
            // handler: the pending operation would then wait for a callback that
            // can no longer arrive, leaving the player stuck in the interaction
            // state the rejection was supposed to release.
            try
            {
                // A descriptor that decodes to nothing must never reach
                // ReplaceInventory: replacing the tree with null empties the whole
                // inventory for the rest of the raid. Fail the operation instead
                // so its watchdog releases the caller.
                var authoritative = packet.Descriptor.ToInventory();
                if (authoritative == null)
                {
                    FikaGlobals.LogError($"{ID} - The host's authoritative inventory snapshot for operation " +
                        $"{packet.CallbackId} decoded to nothing");
                    handler.FailWithoutServerStatus("The host's authoritative inventory snapshot was empty.");
                }
                else
                {
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
        // A snapshot without its operation can be a late duplicate after a
        // successful newer operation. Replacing the entire inventory here
        // would roll back that newer state.
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

            // CanExecute is the native gate (PlayerInventoryController delegates
            // it to the hands controller, and CheckItemAction refuses everything
            // while ItemController.Locked is set). A refusal is the evidence the
            // inventory watchdog needs, and the operation may never have started,
            // so no callback of ours will ever release the interaction state.
            // [LootDiag] (analysis/pickup-stuck-again): a take whose operation never left this
            // client. With the pickup-request line next to it, this is the "refused here, not
            // lost on the wire" evidence.
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
                // Reporting a refused operation must never fail on its own item lookup.
            }
                        NoteGateRefusal($"operation {operation.Id} ({operation.GetIl2CppType().Name}) refused by CanExecute");
            NativeInteractionRelease.Release(FikaPlayer, "local-operation-refused");
            try
            {
                operation.Dispose();
            }
            catch (Exception disposeException)
            {
                // Disposing an operation that is still "Started" raises the
                // terminal event and can throw; when it does, the Begin events it
                // registered stay in ItemController.ActiveEvents and keep the item
                // busy (PlayerIsBusyError) for the rest of the raid.
                FikaGlobals.LogError($"[InventoryRepair] {ID} - disposing the refused operation {operation.Id} threw, " +
                    $"its item events may stay registered: {disposeException}");
            }
            callback?.Fail("LOCAL: hands controller can't perform this operation");
            return;
        }

        // Quest transactions are authoritative locally; their committed inventory
        // effects are sent by the quest-operation hooks, after successful execution.
        if (operation.TryCast<BaseQuestOperation>() != null)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

        // Quest reading (note, chapter conditions, condition, note link) and tape
        // study are not BaseQuestOperation subclasses, but they change nothing but
        // the local player's own quest and item data - the profile this machine
        // owns and uploads when the raid ends. They run here and are never
        // relayed; see QuestOperations for why.
        if (QuestOperations.IsLocalOnly(operation, out var questDetail))
        {
            FikaGlobals.LogInfo($"[QuestOp] {operation.GetIl2CppType().Name} executed locally ({questDetail}); " +
                "local quest data, not relayed");
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

        // Do not replicate picking up quest items, throws an error on the other clients            
        if (operation.TryCast<MoveOperation>() is { } moveOperation)
        {
            var lootedItem = moveOperation.Item;
            if (lootedItem.QuestItem)
            {
                global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
                return;
            }
        }

        // Do not replicate stashing quest items
        if (operation.TryCast<RemoveOperation>() is { } discardOperation && discardOperation.Item.QuestItem)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

        // Search and dialog progress require local dialog state.
        if (operation.TryCast<SinglePlayerSearchContentOperation>() != null
            || operation.TryCast<SetDialogProgressOperation>() != null)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

        // [CarExtract] A paid exfiltration point hands its cash over through
        // EFT.InventoryLogic.Operations.TransferExfiltrationPaymentOperation. The
        // native interaction tells the player the money was transferred as soon
        // as TransferExitItem returns, so a second click while the first payment
        // is still unanswered must be refused here instead of queuing another
        // handover the host would happily apply a second time.
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
            // Registration, the active table and the watchdog are all inside the
            // guarded block: a throw from any of them used to leave the caller's
            // callback uninvoked and the operation without a watchdog, which locks
            // the player in the native pickup interaction state for the rest of the
            // raid.
            operationNum = AddOperationCallback(operation, handler.ServerStatusDelegate);
            _activeOperationHandlers[operationNum] = handler;
            if (carExtractPoint != null)
            {
                _carExtractPayments[carExtractPoint] = operationNum;
            }
            WatchOperationAcknowledgement(handler, operationNum);

            var packet = InventoryPacket.FromValue(FikaPlayer.NetId, operation);
            // Defensive: every quest operation takes the local branch above, so this
            // only fires if one ever reaches the relay again.
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
                    // Drops (and every other non-move operation) were invisible in the
                    // log, which made "the dropped backpack vanished" untraceable.
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
                // The native interaction already reported the handover as done, so
                // the player has to be told that the host never received it.
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
            // The native pickup flow waits for this callback; without it the player
            // stays locked in the PickUpState and the interaction queue.
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
    /// Returns the exfil point name of a paid exfiltration point ("car extract")
    /// payment operation, or null when the operation is not a payment. Reading
    /// the name is purely diagnostic and must never stop a payment from being
    /// dispatched, so every failure is swallowed and reported as "not a payment".
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
    /// The host acknowledges an inventory operation before executing it. If that
    /// acknowledgement never arrives, the operation is failed locally so the
    /// requesting player is released instead of being stuck in the interaction
    /// state waiting for a callback that can no longer come.
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

    /// <summary>Operations the port dispatched whose handler is still registered.</summary>
    internal override int PendingOperationCount => _activeOperationHandlers.Count;

    /// <summary>Server callback registrations without a handler behind them.</summary>
    internal override int ReleaseStaleServerCallbacks(string reason) => ReleaseStaleOperationCallbacks(reason);

    /// <inheritdoc/>
    internal override int CountStaleServerCallbacks() => CountStaleOperationCallbacks();

    /// <summary>
    /// Server callback registrations without a handler behind them. They are the
    /// leftovers of an operation that failed locally, and
    /// <see cref="FikaPlayer.WaitingForCallback"/> counts them: while there is
    /// one, the client hands controllers refuse to press the trigger or start a
    /// reload.
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
    /// Drops every server callback registration that no registered handler owns.
    /// A live operation always owns one, so this can only remove leftovers.
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
    /// Recovery for a client inventory pipeline that stopped answering. Only
    /// called by the conservative watchdog in <c>CoopGame</c>, which requires a
    /// player attempt (open inventory or a refused operation) AND a gate that
    /// stayed closed while the player was idle.
    /// <para>
    /// It clears the port's own bookkeeping first (handlers that are registered
    /// although they are no longer active, callback registrations without a
    /// handler), then the native gate, and finally releases the native
    /// interaction state. <paramref name="escalate"/> additionally drops the item
    /// events the native runtime itself reports as outdated, which is what keeps
    /// a single item unmovable after an aborted operation.
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
                // Only the table entry is dropped: an inactive handler was already
                // returned to the pool once, and returning it again would hand the
                // same instance to two operations.
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
    }

    /// <summary>
    /// (2026-10-17) The local half of a relayed operation runs through Operation.ExecuteWithoutDispose, which
    /// bypasses the native InventoryController.Execute wrapper. That wrapper is what registers every item a
    /// move touched as known at its new address in the player search controller; without it an item shows as
    /// unsearched the moment it lands in a container (the reported magazine after 'unload magazine'). The same
    /// bookkeeping is repeated here: one dictionary entry per item, no item field and no profile field changes.
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

            // Every operation that carries items implements IOneItemOperation (the item it moves) and the
            // two-item ones add ITwoItemOperation (the second item of a swap or transfer). Both are marked
            // with their whole subtree: the magazine that lands in a backpack grid and everything inside it.
            if (operation.TryCast<IOneItemOperation>() is { } one)
            {
                MarkKnownTree(search, one.Item1);
            }

            if (operation.TryCast<ITwoItemOperation>() is { } two)
            {
                MarkKnownTree(search, two.Item2);
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"{ID} - could not mark the items of operation {operation?.Id} as known: {exception.Message}");
        }
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
                // raiseEvents: false - the address entry is what the UI reads; a 'found item' event for
                // an item the player is only moving would be a visible side effect of this bookkeeping.
                search.SetItemAsKnown(candidate, false);
            }
        }
    }
}
