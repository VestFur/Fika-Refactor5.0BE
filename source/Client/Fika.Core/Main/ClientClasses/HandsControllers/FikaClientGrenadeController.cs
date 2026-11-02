// © 2026 Lacyway All Rights Reserved

using System;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.FirearmController;
using Fika.Core.Networking.Packets.FirearmController.SubPackets;

namespace Fika.Core.Main.ClientClasses.HandsControllers;

public class FikaClientGrenadeController : Player.GrenadeHandsController
{
    public FikaClientGrenadeController(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.GrenadeHandsController>(this);
    }


    public FikaClientGrenadeController() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClientGrenadeController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    protected FikaPlayer _fikaPlayer;
    private bool _isClient;
    private WeaponPacket _packet;

    public static FikaClientGrenadeController Create(FikaPlayer player, ThrowWeap item)
    {
        var controller = CreateController<FikaClientGrenadeController>(player, item);
        controller._fikaPlayer = player;
        controller._isClient = FikaBackendUtils.IsClient;
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

    public override void CompassStateHandler(bool isActive)
    {
        //SendCompassState(CompassChangePacket.FromValue(isActive));
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call8(this, isActive);
    }

    public void SendCompassState(CompassChangePacket packet)
    {
#if DEBUG
        FikaGlobals.LogInfo("Sending CompassPacket");
#endif
        _packet.Type = EFirearmSubPacketType.CompassChange;
        _packet.SubPacket = packet;
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
    }

    public override bool CanThrow()
    {
        if (_isClient)
        {
            return !_fikaPlayer.WaitingForCallback && global::Fika.Core.Main.Utils.NativeBaseCalls.Call51(this);
        }

        return global::Fika.Core.Main.Utils.NativeBaseCalls.Call51(this);
    }

    public override void ExamineWeapon()
    {
        _packet.Type = EFirearmSubPacketType.Grenade;
        _packet.SubPacket = GrenadePacket.FromValue(
            default,
            default,
            default,
            EGrenadePacketType.ExamineWeapon,
            false,
            false,
            false,
            false,
            false
        );
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call52(this);
    }

    public override void HighThrow()
    {
        _packet.Type = EFirearmSubPacketType.Grenade;
        _packet.SubPacket = GrenadePacket.FromValue(
            default,
            default,
            default,
            EGrenadePacketType.HighThrow,
            false,
            false,
            false,
            false,
            false
        );
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call53(this);
    }

    public override void LowThrow()
    {
        _packet.Type = EFirearmSubPacketType.Grenade;
        _packet.SubPacket = GrenadePacket.FromValue(
            default,
            default,
            default,
            EGrenadePacketType.LowThrow,
            false,
            false,
            false,
            false,
            false
        );
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call54(this);
    }

    public override void PullRingForHighThrow()
    {
        _packet.Type = EFirearmSubPacketType.Grenade;
        _packet.SubPacket = GrenadePacket.FromValue(
            default,
            default,
            default,
            EGrenadePacketType.PullRingForHighThrow,
            false,
            false,
            false,
            false,
            false
        );
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call55(this);
    }

    public override void PullRingForLowThrow()
    {
        _packet.Type = EFirearmSubPacketType.Grenade;
        _packet.SubPacket = GrenadePacket.FromValue(
            default,
            default,
            default,
            EGrenadePacketType.PullRingForLowThrow,
            false,
            false,
            false,
            false,
            false
        );
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call56(this);
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

    public override void PlantTripwire()
    {
        _packet.Type = EFirearmSubPacketType.Grenade;
        _packet.SubPacket = GrenadePacket.FromValue(
            default,
            default,
            default,
            EGrenadePacketType.None,
            false,
            false,
            true,
            false,
            false
        );
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call58(this);
    }

    public override void ChangeFireMode(Weapon.EFireMode fireMode)
    {
        if (CurrentOperation == null || !CurrentOperation.CanChangeFireMode(fireMode))
        {
            return;
        }

        // 检查 GClass 的递增
        var currentOperation = CurrentOperation;
        if (currentOperation != null)
        {
            if (currentOperation.TryCast<Player.GrenadeHandsController.Idling>() == null)
            {
                if (currentOperation.TryCast<Player.GrenadeHandsController.PlantTripwireOperation>() != null)
                {
                    _packet.Type = EFirearmSubPacketType.Grenade;
                    _packet.SubPacket = GrenadePacket.FromValue(
                        default,
                        default,
                        default,
                        EGrenadePacketType.None,
                        false,
                        false,
                        false,
                        true,
                        false
                    );
                    _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
                }
            }
            else
            {
                _packet.Type = EFirearmSubPacketType.Grenade;
                _packet.SubPacket = GrenadePacket.FromValue(
                    default,
                    default,
                    default,
                    EGrenadePacketType.None,
                    false,
                    false,
                    false,
                    false,
                    true
                );
                _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
            }
        }
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call59(this, fireMode);
    }

    public override void ActualDrop(Result<IGrenadeController> controller, float animationSpeed, Il2CppSystem.Action callback, bool fastDrop)
    {
        _packet.Type = EFirearmSubPacketType.CancelGrenade;
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call60(this, controller, animationSpeed, callback ?? FikaGlobals.EmptyNativeAction, fastDrop);
    }
}
