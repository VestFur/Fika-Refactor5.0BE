// © 2026 Lacyway All Rights Reserved

using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.Patches.World;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Pooling;
using Diz.LanguageExtensions;
using static EFT.Player;

namespace Fika.Core.Main.BotClasses;

public sealed class BotInventoryController : BaseInventoryController
{

    public BotInventoryController(System.IntPtr pointer) : base(pointer) { }


    public override bool HasDiscardLimits
    {
        get
        {
            return false;
        }
    }
    private readonly FikaBot _fikaBot;
    private readonly BotInventoryOperationHandlerPool _botInventoryOperationHandlerPool;

    public BotInventoryController(Player player, Profile profile, bool examined, MongoID currentId, ushort nextOperationId)
        : base(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<BotInventoryController>(), player, profile, examined, false)
    {
        _fikaBot = player.TryCast<FikaBot>()
            ?? throw new System.InvalidOperationException("Bot inventory controller received a non-Fika player");
        IdSource = currentId;
        _nextOperationId = nextOperationId;
        PlayerSearchController = new BotSearchController(profile);
        _botInventoryOperationHandlerPool = BotInventoryOperationHandlerPool.Instance;
    }

    public override IPlayerSearchController PlayerSearchController { get; }

    public override void CallMalfunctionRepaired(Weapon weapon)
    {
        // 不做任何事
    }

    /// <summary>
    /// 获取一个库存处理器
    /// </summary>
    /// <returns>一个池化的处理器</returns>
    public BotInventoryOperationHandler GetHandler()
    {
        return _botInventoryOperationHandlerPool.Get();
    }

    /// <summary>
    /// 归还一个处理器
    /// </summary>
    /// <param name="handler">要归还的处理器</param>
    public void ReturnHandler(BotInventoryOperationHandler handler)
    {
        _botInventoryOperationHandlerPool.ReturnHandler(handler);
    }

    public override void Execute(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
#if DEBUG
        FikaGlobals.LogInfo($"Sending bot operation {operation.GetType()} from {_fikaBot.Profile.Nickname}");
#endif
        _fikaBot.PacketSender.NetworkManager.SendGenericPacket(EGenericSubPacketType.InventoryOperation,
            InventoryPacket.FromValue(_fikaBot.NetId, operation), true);
        MainThreadDispatcher.RunAsync(() => HandleOperation(operation, callback)).Forget();
    }

    /// <summary>
    /// 重写以不复制绊线
    /// </summary>
    public override void PlantTripwire(ThrowWeap grenade, PlantingKit plantingKit, Vector3 fromPosition, Vector3 toPosition, Callback callback = null)
    {
        // [PlantDiag] (analysis/plant-explosive-door)：原生放置结果总会携带一个
        // 针对手雷的 DiscardResult，因此当 AI 交出一枚已不再
        // 附着于任何容器的手雷时，ItemManipulator.Discard 会解引用 Item.Parent，并从
        // PlantTripwireResult.Execute 抛出（"Trying to get parent of an item that doesn't have a parent: ..."）
        // ——每次尝试一个 IL2CPP 异常，在
        // 2026-10-12 的战局中出现了五次。对于已脱离的物品该操作不可能成功，
        // 因此在构建之前就拒绝，并改为记录一次拒绝日志。
        if (!PlantDiag.HasParent(grenade) || (plantingKit != null && !PlantDiag.HasParent(plantingKit)))
        {
            
            return;
        }

        try
        {
            var gstruct = ItemManipulator.SimulatePlantTripwire(this, grenade, plantingKit);
            if (!gstruct.Failed)
            {
                MainThreadDispatcher.RunAsync(() => HandleOperation(new PlantTripwireOperation(GetAndIncrementNextOperationId(), this, gstruct.Value, fromPosition, toPosition, _fikaBot), callback))
                .Forget();
                return;
            }
            callback?.Invoke(gstruct.ToResult());
        }
        catch (System.Exception exception)
        {
            
        }
    }

    private async Task HandleOperation(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
        if (_fikaBot.HealthController.IsAlive)
        {
            await Task.Yield();
        }
        RunBotOperation(operation, callback);
    }

    private void RunBotOperation(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
        var handler = _botInventoryOperationHandlerPool.Get();
        handler.Set(this, operation, callback);
        try
        {
            if (CanExecute(operation))
            {
                handler.Operation.Execute((Callback)handler.HandleResult);
                return;
            }
            handler.Operation.Dispose();
            handler.Callback?.Fail($"Can't execute {handler.Operation}", 1);
        }
        catch (System.Exception exception)
        {
            // [PlantDiag] (analysis/plant-explosive-door)：原生操作可能从
            // Execute 抛出（PlantTripwireResult.Execute 对已脱离的手雷就会这样）。让
            // 回调失败可以保持 AI 记账完整，并阻止异常逃逸到
            // Fika.Core.TaskExtensions.Forget 变成日志风暴。
            try
            {
                handler.Operation.Dispose();
            }
            catch
            {
                // 二次 Dispose 不值得再引发一个异常。
            }
            handler.Callback?.Fail($"{operation?.GetType().Name} threw: {exception.Message}", 1);
        }
        finally
        {
            _botInventoryOperationHandlerPool.ReturnHandler(handler);
        }
    }

    public override SearchContentOperation CreateSearchOperation(SearchableItem item)
    {
        return new SinglePlayerSearchContentOperation(GetAndIncrementNextOperationId(), this, PlayerSearchController, Profile, item);
    }
}
