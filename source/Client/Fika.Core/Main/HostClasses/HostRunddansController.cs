using Fika.Core.Main.BaseClasses;
using Comfort.Common;
using EFT;
using Fika.Core.Main.Utils;
using Fika.Core.Main.Players;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Communication;
using JsonType;

namespace Fika.Core.Main.HostClasses;

public class HostRunddansController(RunddansGlobalSettings settings, LocationSettings.Location location)
    : FikaWorldController<LocalRunddansController>(new LocalRunddansController(settings, location))
{
    internal bool EventStarted { get; private set; }

    public void InteractWithEventObject(Player player, InteractWithEventObjectPacket packet) => HandleInteraction(player, packet);

    public void ObservedInteractWithEventObject(Player player, InteractWithEventObjectPacket packet, NetPeer peer) => HandleInteraction(player, packet);

    private void HandleInteraction(Player player, InteractWithEventObjectPacket packet)
    {
        // 对本地、远程和无头用户应用同样的资格与状态转换规则。
        if (player == null || player.HealthController?.IsAlive != true || !packet.hasInteraction) return;

        TransitController transit;
        if (FikaBackendUtils.IsHeadless)
        {
            // 无头安装注册的是 FikaHeadlessTransitController，它是
            // TransitController 的直接子类，并且有意没有本地
            // UI/事件转换历史。原生的 IsValid<T> 辅助方法
            // 需要那段历史，因此会拒绝合法的无头
            // 交互。保持旧的无头契约：只要求
            // 存在一个中转控制器。
            transit = Singleton<GameWorld>.Instance?.TransitController;
            if (transit == null || !Native.Objects.TryGetValue(packet.objectId, out var eventObject)) return;
            HandleInteractionCore(player, packet, transit, eventObject);
            return;
        }

        // 图形主机使用 LocalTransitController，并保留原生的
        // 事件历史资格检查。
        if (!Native.IsValid(player, out LocalTransitController localTransit, out var history) ||
            history?.events != true || !Native.Objects.TryGetValue(packet.objectId, out var localEventObject)) return;
        transit = localTransit;
        HandleInteractionCore(player, packet, transit, localEventObject);
    }

    private void HandleInteractionCore(Player player, InteractWithEventObjectPacket packet,
        TransitController transit, EventObject eventObject)
    {
        if ((packet.interaction == EventObject.EInteraction.Run && eventObject.State != EventObject.EState.Ready) ||
            (packet.interaction == EventObject.EInteraction.Repair && eventObject.State != EventObject.EState.Broken) ||
            (packet.interaction != EventObject.EInteraction.Run && packet.interaction != EventObject.EInteraction.Repair))
        {
            Notify(player, RunddansMessagesEvent.EType.NonInteractive);
            return;
        }

        string consumedItemId = null;
        if (packet.interaction == EventObject.EInteraction.Run)
        {
            if (!Native.TryGetConsumableItem(player, out var item))
            {
                Notify(player, RunddansMessagesEvent.EType.NoRequiredItem);
                return;
            }
            consumedItemId = item.Id;
            if (!Native.TryRemoveConsumableItem(item))
            {
                FikaGlobals.LogError($"Runddans could not remove item {consumedItemId} from {player.ProfileId}");
                return;
            }
        }
        if (consumedItemId != null)
        {
            var removal = new EventControllerEventPacket
            {
                NetId = player.TryCast<FikaPlayer>()?.NetId ?? player.Id,
                ItemId = consumedItemId, Type = EventControllerEventPacket.EEventType.RemoveItem
            };
            FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref removal, DeliveryMethod.ReliableOrdered);
        }
        EventStarted = true;
        Native.SetState(packet.objectId, EventObject.EState.Running);
        transit.EnablePoints(false);
        if (!FikaBackendUtils.IsHeadless && GamePlayerOwner.MyPlayer != null &&
            transit.TryCast<LocalTransitController>() is { } localTransit)
            localTransit.UpdateTimers();
        var started = new EventControllerEventPacket { Type = EventControllerEventPacket.EEventType.StartedEvent };
        FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref started, DeliveryMethod.ReliableOrdered);
    }

    internal EventControllerEventPacket CreateSnapshot()
    {
        var state = new RunddansStateEvent { PlayerRaidId = 0, Objects = new() };
        var durability = new System.Collections.Generic.Dictionary<int, float>();
        var dateTimes = new System.Collections.Generic.Dictionary<int, long>();
        foreach (var entry in Native.Objects)
        {
            state.Objects.Add(entry.Key, entry.Value.State);
            durability[entry.Key] = entry.Value._durability;
            dateTimes[entry.Key] = entry.Value._dateTime.Ticks;
        }
        return new EventControllerEventPacket
        {
            Type = EventControllerEventPacket.EEventType.StateEvent,
            Event = state, Durability = durability, DateTimeTicks = dateTimes
        };
    }

    public void OnTriggerStateChanged(EventObject.EState state)
    {
        var packet = CreateSnapshot();
        FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    private void Notify(Player player, RunddansMessagesEvent.EType type)
    {
        if (player.IsYourPlayer)
        {
            if (type == RunddansMessagesEvent.EType.NoRequiredItem) Native.NoRequiredItemNotification();
            else Native.NonInteractiveNotification();
            return;
        }
        var packet = new EventControllerEventPacket
        {
            Type = EventControllerEventPacket.EEventType.MessageEvent,
            Event = new RunddansMessagesEvent { PlayerRaidId = player.RaidId, Type = type }
        };
        FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    protected override void DisposeNative() => Native.Dispose();
}
