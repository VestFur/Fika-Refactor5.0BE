// © 2026 Lacyway All Rights Reserved

using EFT.InventoryLogic;
using Fika.Core.Main.Players;
using Fika.Core.Networking.Packets.FirearmController;
using Fika.Core.Networking.Packets.FirearmController.SubPackets;

namespace Fika.Core.Main.ClientClasses.HandsControllers;

/// <summary>
/// 仅由 AI 使用
/// </summary>
public class FikaClientQuickGrenadeController : EFT.Player.QuickGrenadeThrowHandsController
{
    public FikaClientQuickGrenadeController(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<EFT.Player.QuickGrenadeThrowHandsController>(this);
    }


    public FikaClientQuickGrenadeController() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClientQuickGrenadeController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    protected FikaPlayer _fikaPlayer;
    private WeaponPacket _packet;

    public static FikaClientQuickGrenadeController Create(FikaPlayer player, ThrowWeap item)
    {
        var controller = CreateController<FikaClientQuickGrenadeController>(player, item);
        controller._fikaPlayer = player;
        controller._packet = new()
        {
            NetId = player.NetId
        };
        return controller;
    }

    public override void Destroy()
    {
        _packet = null;
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call50(this);
    }

    public override void ThrowGrenade(float timeSinceSafetyLevelRemoved, Vector3 position, Quaternion rotation, Vector3 force, bool lowThrow)
    {
        _packet.Type = EFirearmSubPacketType.Grenade;
        _packet.SubPacket = GrenadePacket.FromValue(
            rotation,
            position,
            force,
            EGrenadePacketType.None,
            true,
            lowThrow,
            false,
            false,
            false
        );
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);

        global::Fika.Core.Main.Utils.NativeBaseCalls.Call57(this, timeSinceSafetyLevelRemoved, position, rotation, force, lowThrow);
    }
}
