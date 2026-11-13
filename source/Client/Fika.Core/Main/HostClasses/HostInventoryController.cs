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
        if (_player.HealthController.IsAlive)
        {
            await Task.Yield();
        }
        RunHostOperation(operation, callback);
    }

    /// <summary>
    /// Gets an inventory handler
    /// </summary>
    /// <returns>A pooled handler</returns>
    public HostInventoryOperationHandler GetHandler()
    {
        return _hostInventoryOperationHandlerPool.Get();
    }

    /// <summary>
    /// Returns a handler
    /// </summary>
    /// <param name="handler">The handler to return</param>
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

        // Quest reading (note, chapter conditions, condition, note link) and tape
        // study belong to the player who triggered them; the host owns its own
        // copy of that quest data, so they run here and are never relayed to the
        // peers (see QuestOperations).
        if (QuestOperations.IsLocalOnly(operation, out var questDetail))
        {
            FikaGlobals.LogInfo($"[QuestOp] {operation.GetIl2CppType().Name} executed locally on the host ({questDetail}); " +
                "not relayed");
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
        if (operation.TryCast<RemoveOperation>() is { } discardOperation)
        {
            if (discardOperation.Item.QuestItem)
            {
                global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
                return;
            }
        }

        // Search operations use local dialog state that remote players do not have.
        if (operation.TryCast<SinglePlayerSearchContentOperation>() != null)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

#if DEBUG
        ConsoleScreen.Log($"InvOperation: {operation.GetType().Name}, Id: {operation.Id}");
#endif
        // Check for GClass increments, TraderServices
        if (operation.TryCast<PurchaseTraderServiceOperation>() != null)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

        var handler = _hostInventoryOperationHandlerPool.Get();
        handler.Set(this, operation, callback);
        bool canExecute;
        try { canExecute = CanExecute(handler.Operation); }
        catch
        {
            handler.CancelDispatch();
            throw;
        }
        if (!canExecute)
        {
            // [LootDiag] (analysis/pickup-stuck-again): the host refused an operation a client
            // sent. This refusal is the one the client cannot otherwise read the reason of.
            global::Fika.Core.Main.Utils.FikaGlobals.LogWarning($"[LootDiag] host-refuse player={FikaPlayer?.ProfileId} " +
                $"op={operation.Id} class={operation.GetIl2CppType().Name}");
            try
            {
                handler.Operation.Dispose();
                // The host can be inside the same native pickup state as a client;
                // releasing it here keeps the failing pickup from locking the host
                // player in EPlayerState.Pickup.
                global::Fika.Core.Main.Utils.NativeInteractionRelease.Release(FikaPlayer, "host-canexecute-failed");
                handler.Callback?.Fail($"Can't execute {operation}", 1);
            }
            finally { handler.CancelDispatch(); }
            return;
        }

        try
        {
            // ToDescriptor can inspect an operation that a synchronous native
            // completion has already disposed. Capture it before Execute.
            handler.SetPendingPacket(InventoryPacket.FromValue(FikaPlayer.NetId, operation));
        }
        catch
        {
            handler.CancelDispatch();
            throw;
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
                handler.FinishDispatch(); // An asynchronous callback returns it later.
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
