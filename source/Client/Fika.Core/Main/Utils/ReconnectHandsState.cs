using System;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.ClientClasses.HandsControllers;
using Fika.Core.Main.Players;
using Fika.Core.Networking.Packets.Player.Common;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Main.Utils;

/// <summary>Owns a copy of the hands snapshot, independently of the reusable network packet.</summary>
internal sealed class ReconnectHandsState
{
    private readonly EHandsControllerType _type;
    private readonly MongoID _itemId;
    private readonly MongoID _lastItemId;
    private readonly bool _stationary;
    private readonly Vector2 _rotation;
    private readonly Quaternion _bodyRotation;

    internal ReconnectHandsState(ReconnectPacket packet)
    {
        _type = packet.ControllerType;
        _itemId = packet.ItemId;
        _lastItemId = packet.LastEquippedItemId;
        _stationary = packet.IsStationary;
        _rotation = packet.PlayerRotation;
        _bodyRotation = packet.PlayerBodyRotation;
    }

    internal void RestoreLastItem(FikaPlayer player)
    {
        // MongoID overloads ==/!= for value comparison.  Passing a null native
        // wrapper through that operator is unsafe under IL2CPP, so test the
        // reference directly before checking the empty/default ID.
        if (ReferenceEquals(_lastItemId, null) || _lastItemId.Equals(new MongoID())) return;
        var result = player.FindItemById(_lastItemId, false, false);
        if (result.Succeeded) player._lastEquippedWeaponOrKnifeItem = result.Value;
    }

    internal async Task Restore(FikaPlayer player)
    {
        if (_type == EHandsControllerType.None) return;
        var type = _type;
        // Timed actions cannot be resumed from a controller type alone.
        // Return to the saved weapon instead of replaying consumption/throws.
        var interrupted = type is EHandsControllerType.Meds or EHandsControllerType.QuickGrenade
            or EHandsControllerType.QuickKnife or EHandsControllerType.QuickUseItem or EHandsControllerType.RecorderItem;
        Item item = null;
        if (interrupted)
        {
            item = player.LastEquippedWeaponOrKnifeItem;
            // A reconnect can be captured between the native drop callback and
            // Fika's last-item hook. Fall back to the current equipment tree so
            // an interrupted med/throw action still returns to a usable weapon.
            item ??= FindEquippedFallback(player);
            type = item?.TryCast<Weapon>() != null ? EHandsControllerType.Firearm
                : item?.GetItemComponent<KnifeComponent>() != null ? EHandsControllerType.Knife : EHandsControllerType.Empty;
            if (type == EHandsControllerType.Empty) item = null;
        }
        else if (type != EHandsControllerType.Empty)
        {
            if (ReferenceEquals(_itemId, null) || _itemId.Equals(new MongoID())) throw new InvalidOperationException($"Missing reconnect item for {type}");
            if (_stationary)
                item = player.GameWorld.FindStationaryWeaponByItemId(_itemId)?.Item;
            else
            {
                var result = player.FindItemById(_itemId, false, false);
                if (result.Succeeded) item = result.Value;
            }
            if (item == null) throw new InvalidOperationException($"Reconnect hands item {_itemId} is unavailable");
        }

        Player.AbstractHandsController controller;
        // Construct the regular local controller without issuing a new Proceed
        // transaction against the host's already-equipped inventory.
        switch (type)
        {
            case EHandsControllerType.Empty:
                if (player.HandsController?.TryCast<Player.EmptyHandsController>() != null)
                {
                    if (interrupted) PublishInterruptedAction(player, type, null);
                    return;
                }
                controller = Player.EmptyHandsController.CreateController<Player.EmptyHandsController>(player);
                break;
            case EHandsControllerType.Firearm:
                controller = FikaClientFirearmController.Create(player, item.TryCast<Weapon>()
                    ?? throw new InvalidOperationException("Reconnect firearm item is not a weapon"));
                break;
            case EHandsControllerType.Knife:
                controller = FikaClientKnifeController.Create(player, item.GetItemComponent<KnifeComponent>()
                    ?? throw new InvalidOperationException("Reconnect knife component is missing"));
                break;
            case EHandsControllerType.Grenade:
                controller = FikaClientGrenadeController.Create(player, item.TryCast<ThrowWeap>()
                    ?? throw new InvalidOperationException("Reconnect grenade item is invalid"));
                break;
            case EHandsControllerType.UsableItem:
                controller = item.TryCast<PortableRangeFinder>() != null
                    ? FikaClientPortableRangeFinderController.Create(player, item)
                    : FikaClientUsableItemController.Create(player, item);
                break;
            default:
                throw new InvalidOperationException($"Unsupported reconnect hands type {type}");
        }

        var operation = item != null ? player.BeginSetInHands(item) : null;
        try
        {
            operation?.Confirm(true);
            var oldController = player.HandsController;
            if (oldController != null)
            {
                oldController.Destroy();
                UnityEngine.Object.Destroy(oldController);
                player.HandsController = null;
            }
            await player.SpawnController(controller).AsManaged();
            if (player == null || player.GameWorld == null) return;
            player.Cast<ISetInHandsHandler>().OnSetInHands(new(item, CommandStatus.Succeed, player.InventoryController));
            if (_stationary && !interrupted)
                player.FastForwardToStationaryWeapon(item, _rotation, _bodyRotation, _bodyRotation);
            if (interrupted) PublishInterruptedAction(player, type, item);
        }
        finally
        {
            operation?.Dispose();
        }
    }

    private static Item FindEquippedFallback(FikaPlayer player)
    {
        foreach (var slotType in FikaGlobals.WeaponSlots)
        {
            var item = player.Equipment.GetSlot(slotType)?.ContainedItem;
            if (item?.TryCast<Weapon>() != null || item?.GetItemComponent<KnifeComponent>() != null)
                return item;
        }

        return null;
    }

    private static void PublishInterruptedAction(FikaPlayer player, EHandsControllerType type, Item item)
    {
        player.CommonPacket.Type = ECommonSubPacketType.Proceed;
        player.CommonPacket.SubPacket = ProceedPacket.FromValue(null, item?.Id, 0f, 0,
            type == EHandsControllerType.Firearm ? EProceedType.Weapon :
            type == EHandsControllerType.Knife ? EProceedType.Knife : EProceedType.EmptyHands, false);
        player.PacketSender.NetworkManager.SendNetReusable(ref player.CommonPacket, DeliveryMethod.ReliableOrdered, true);
    }
}
