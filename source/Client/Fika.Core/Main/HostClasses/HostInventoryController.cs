using System;
using EFT.Settings;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using EFT.UI;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets;
using Fika.Core.Networking.Packets.Communication;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.World;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Main.HostClasses;

public sealed class HostInventoryController : BaseInventoryController
{

    public HostInventoryController(System.IntPtr pointer) : base(pointer) { }


    public FikaPlayer FikaPlayer { get; }

    public override bool HasDiscardLimits
    {
        get
        {
            return false;
        }
    }
    private readonly Player _player;
    private readonly HostInventoryOperationHandlerPool _hostInventoryOperationHandlerPool;

    public HostInventoryController(Player player, Profile profile, bool examined, bool strictSync)
        : base(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<HostInventoryController>(), player, profile, examined, strictSync)
    {
        _player = player;
        FikaPlayer = player.TryCast<FikaPlayer>()
            ?? throw new System.InvalidOperationException("Host inventory controller received a non-Fika player");
        PlayerSearchController = new ActiveSearchController(profile, this);

        _hostInventoryOperationHandlerPool = new HostInventoryOperationHandlerPool(8, HostInventoryOperationHandler.CreateInstance);
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
            FikaSingleton<FikaClient>.Instance.SendData(ref request, DeliveryMethod.ReliableOrdered);
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

    public override void Execute(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
        MainThreadDispatcher.RunAsync(() => HandleOperation(operation, callback)).Forget();
    }

    private async Task HandleOperation(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
        try
        {
            if (_player.HealthController.IsAlive)
            {
                await Task.Yield();
            }
            RunHostOperation(operation, callback);
        }
        catch (Exception exception)
        {
            // Execute is dispatched fire-and-forget; an exception that reaches this method used to be
            // swallowed by Forget(). The native EFT.PickUpState only leaves when this callback is
            // completed, so every failure path has to fail it and release the interaction state.
            FikaGlobals.LogError($"[LootFix] host-operation threw class={operation?.GetIl2CppType().Name} " +
                $"id={operation?.Id}: {exception}", "HandleOperation");
            // Use the callback captured by this method, not handler.Callback: RunHostOperation may
            // already have returned the pooled handler, and CancelDispatch/Dispose can clear its field.
            try { callback?.Fail($"host operation failed: {exception.Message}", 1); } catch { }
            try { NativeInteractionRelease.Release(_player, "host-operation-exception"); } catch { }
        }
    }

    /// <summary>
    /// 获取一个库存处理器
    /// </summary>
    /// <returns>一个池化的处理器</returns>
    public HostInventoryOperationHandler GetHandler()
    {
        return _hostInventoryOperationHandlerPool.Get();
    }

    /// <summary>
    /// 归还一个处理器
    /// </summary>
    /// <param name="handler">要归还的处理器</param>
    public void ReturnHandler(HostInventoryOperationHandler handler)
    {
        _hostInventoryOperationHandlerPool.ReturnHandler(handler);
    }

    private void RunHostOperation(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
        if (operation.TryCast<BaseQuestOperation>() != null)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

        // 任务读取（note、chapter conditions、condition、note link）以及磁带
        // 研究都属于触发它们的玩家；主机拥有自己的一份
        // 任务数据副本，所以它们在这里执行，并且从不转发给
        // 其他节点（参见 QuestOperations）。
        if (QuestOperations.IsLocalOnly(operation, out var questDetail))
        {
            FikaGlobals.LogInfo($"[QuestOp] {operation.GetIl2CppType().Name} executed locally on the host ({questDetail}); " +
                "not relayed");
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

        // 不要同步拾取任务物品，这会在其他客户端上抛错            
        if (operation.TryCast<MoveOperation>() is { } moveOperation)
        {
            var lootedItem = moveOperation.Item;
            if (lootedItem.QuestItem)
            {
                global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
                return;
            }
        }

        // 不要同步存放任务物品
        if (operation.TryCast<RemoveOperation>() is { } discardOperation)
        {
            if (discardOperation.Item.QuestItem)
            {
                global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
                return;
            }
        }

        // 搜索操作使用本地对话框状态，远程玩家没有该状态。
        if (operation.TryCast<SinglePlayerSearchContentOperation>() != null)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

#if DEBUG
        ConsoleScreen.Log($"InvOperation: {operation.GetType().Name}, Id: {operation.Id}");
#endif
        // 检查 GClass 增量、TraderServices
        if (operation.TryCast<PurchaseTraderServiceOperation>() != null)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

        var handler = _hostInventoryOperationHandlerPool.Get();
        handler.Set(this, operation, callback);
        HostOperationWatchdog.Track(handler, operation.GetIl2CppType().Name + "#" + operation.Id);
        bool canExecute;
        try { canExecute = CanExecute(handler.Operation); }
        catch
        {
            handler.CancelDispatch();
            throw;
        }
        if (!canExecute)
        {
            // [LootDiag] (analysis/pickup-stuck-again)：主机拒绝了客户端发送的
            // 一个操作。这次拒绝正是客户端在其他情况下无法读到原因的那一次。
            global::Fika.Core.Main.Utils.FikaGlobals.LogWarning($"[LootDiag] host-refuse player={FikaPlayer?.ProfileId} " +
                $"op={operation.Id} class={operation.GetIl2CppType().Name}");
            try
            {
                handler.Operation.Dispose();
                // 主机可能处在与客户端相同的原生拾取状态中；
                // 在这里释放它，可以避免失败的拾取把主机
                // 玩家锁在 EPlayerState.Pickup 中。
                global::Fika.Core.Main.Utils.NativeInteractionRelease.Release(FikaPlayer, "host-canexecute-failed");
                handler.Callback?.Fail($"Can't execute {operation}", 1);
            }
            finally { handler.CancelDispatch(); }
            return;
        }

        try
        {
            // ToDescriptor 可能检查一个已被同步原生
            // 完成处置过的操作。要在 Execute 之前捕获它。
            handler.SetPendingPacket(InventoryPacket.FromValue(FikaPlayer.NetId, operation));
        }
        catch (Exception exception)
        {
            // Relaying the operation to peers is optional; executing it (and completing the native
            // pickup callback) is mandatory. A descriptor failure must not cancel the operation.
            FikaGlobals.LogWarning($"[LootFix] relay-descriptor-failed class={operation.GetIl2CppType().Name} " +
                $"id={operation.Id}: {exception.GetType().Name}: {exception.Message}", "RunHostOperation");
        }

        var executeFailed = false;
        try
        {
            try
            {
                handler.Operation.Execute(handler.HandleResultDelegate);
            }
            catch
            {
                executeFailed = true;
                throw;
            }
        }
        finally
        {
            if (executeFailed && !handler.Completed)
                handler.CancelDispatch();
            else
                handler.FinishDispatch(); // 稍后由异步回调归还它。
        }
    }

    public override bool HasCultistAmulet(out CultistAmulet amulet)
    {
        amulet = null;
        using var enumerator = Inventory.GetItemsInSlots(new[] { EquipmentSlot.Pockets }.ToNativeList()).AsManaged().GetEnumerator();
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

    public override SearchContentOperation CreateSearchOperation(SearchableItem item)
    {
        return new SinglePlayerSearchContentOperation(GetAndIncrementNextOperationId(), this, PlayerSearchController, Profile, item);
    }

    public void ClearPool()
    {
        _hostInventoryOperationHandlerPool.Dispose();
    }
}
