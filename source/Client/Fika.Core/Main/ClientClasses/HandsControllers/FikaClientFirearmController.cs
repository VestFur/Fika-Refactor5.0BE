// © 2026 Lacyway All Rights Reserved

using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets;
using Fika.Core.Networking.Packets.FirearmController;
using Fika.Core.Networking.Packets.FirearmController.SubPackets;
using Fika.Core.Networking.Packets.Player.Common;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;
using Fika.Core.Networking.Pooling;
using ReloadMagPacket = Fika.Core.Networking.Packets.FirearmController.SubPackets.ReloadMagPacket;
using RollCylinderPacket = Fika.Core.Networking.Packets.FirearmController.SubPackets.RollCylinderPacket;

namespace Fika.Core.Main.ClientClasses.HandsControllers;

public class FikaClientFirearmController : Player.FirearmController
{
    public FikaClientFirearmController(System.IntPtr pointer) : base(pointer) {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.FirearmController>(this);
    }


    public FikaClientFirearmController() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClientFirearmController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    protected FikaPlayer _fikaPlayer;
    protected WeaponPacket _packet;
    private bool _isClient;
    protected bool _isGrenadeLauncher;

    public static FikaClientFirearmController Create(FikaPlayer player, Weapon weapon)
    {
        var controller = CreateController<FikaClientFirearmController>(player, weapon);
        controller._fikaPlayer = player;
        controller._isClient = FikaBackendUtils.IsClient;
        controller._isGrenadeLauncher = weapon.IsGrenadeLauncher;
        controller._packet = new()
        {
            NetId = player.NetId
        };
        return controller;
    }

    public void SendLightStates(LightStatesPacket packet)
    {
        _packet.Type = EFirearmSubPacketType.ToggleLightStates;
        _packet.SubPacket = packet;
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
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

    public override void Destroy()
    {
        _packet = null;
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call9(this);
    }

    public override void SetWeaponOverlapValue(float overlap)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call10(this, overlap);
        _fikaPlayer.ObservedOverlap = overlap;
    }

    public override void WeaponOverlapping()
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call11(this);
        _fikaPlayer.LeftStanceDisabled = DisableLeftStanceByOverlap;
    }

    public override Il2CppSystem.Collections.Generic.Dictionary<Il2CppSystem.Type, OperationFactoryDelegate> GetOperationFactoryDelegates()
    {
        var operationFactoryDelegates = global::Fika.Core.Main.Utils.NativeBaseCalls.Call12(this);
        operationFactoryDelegates[Il2CppInterop.Runtime.Il2CppType.Of<Player.FirearmController.ReloadInternalMagBase>()] = (OperationFactoryDelegate)Weapon1;
        operationFactoryDelegates[Il2CppInterop.Runtime.Il2CppType.Of<Player.FirearmController.ReloadCylinderMagOperation>()] = (OperationFactoryDelegate)Weapon2;
        operationFactoryDelegates[Il2CppInterop.Runtime.Il2CppType.Of<Player.FirearmController.FireOperation>()] = (OperationFactoryDelegate)Weapon3;
        return operationFactoryDelegates;
    }

    public override void OnPlayerDead()
    {
        if (IsAiming)
        {
            SetAim(false);
        }
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call13(this);
    }

    public override bool CanStartReload(bool checkWeaponMalfunctions = true)
    {
        if (_isClient)
        {
            return !_fikaPlayer.WaitingForCallback && global::Fika.Core.Main.Utils.NativeBaseCalls.Call14(this, checkWeaponMalfunctions);
        }

        return global::Fika.Core.Main.Utils.NativeBaseCalls.Call14(this, checkWeaponMalfunctions);
    }

    public override bool CanPressTrigger()
    {
        if (_isClient)
        {
            return !_fikaPlayer.WaitingForCallback && global::Fika.Core.Main.Utils.NativeBaseCalls.Call15(this);
        }

        return global::Fika.Core.Main.Utils.NativeBaseCalls.Call15(this);
    }

    public override void FastForwardCurrentOutdatedState()
    {
        var operation = CurrentOperation;
        if (operation == null || !operation.IsOutdate()) return;
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call119(this);
        _fikaPlayer.CommonPacket.Type = ECommonSubPacketType.FastForwardOutdated;
        _fikaPlayer.CommonPacket.SubPacket = FastForwardOutdatedPacket.FromValue();
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _fikaPlayer.CommonPacket,
            DeliveryMethod.ReliableOrdered, true);
    }

    public Player.ObjectInHandsOperation Weapon1()
    {
        if (Item.ReloadMode is Weapon.EReloadMode.InternalMagazine && Item.Chambers.Length == 0)
        {
            return new AmmoPackReloadInternalOneChamberOperation(this);
        }
        if (Item.MustBoltBeOpennedForInternalReload)
        {
            return new AmmoPackReloadInternalBoltOpenOperation(this);
        }
        return new AmmoPackReloadInternalOneChamberOperation(this);
    }

    public Player.ObjectInHandsOperation Weapon2()
    {
        return new CylinderReloadOperation(this);
    }

    public Player.ObjectInHandsOperation Weapon3()
    {
        // Item 是一个生成的 Weapon 包装器；先查询它的原生类，
        // 再选择负责弹膛/转轮仓库更新的操作。
        if (Item.TryCast<RocketLauncher>() != null)
        {
            return new Player.FirearmController.RocketLauncherFire(this);
        }
        if (Item.IsFlareGun)
        {
            return new Player.FirearmController.FlareGunFire(this);
        }
        if (Item.IsOneOff)
        {
            return new Player.FirearmController.OneOffGunFire(this);
        }
        if (Item.ReloadMode == Weapon.EReloadMode.OnlyBarrel)
        {
            return new FireOnlyBarrelFireOperation(this);
        }
        if (Item.TryCast<Revolver>() != null)
        {
            return new Player.FirearmController.FireCylinderMagOperation(this);
        }
        if (!Item.BoltAction)
        {
            return new Player.FirearmController.FireOperation(this);
        }
        return new DefaultFireOperation(this);
    }

    public override bool ToggleBipod()
    {
        var success = global::Fika.Core.Main.Utils.NativeBaseCalls.Call16(this);
        if (success)
        {
            _packet.Type = EFirearmSubPacketType.ToggleBipod;
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        }
        return success;
    }

    public override bool CheckChamber()
    {
        var flag = global::Fika.Core.Main.Utils.NativeBaseCalls.Call17(this);
        if (flag)
        {
            _packet.Type = EFirearmSubPacketType.CheckChamber;
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        }
        return flag;
    }

    public override bool CheckAmmo()
    {
        var flag = global::Fika.Core.Main.Utils.NativeBaseCalls.Call18(this);
        if (flag)
        {
            _packet.Type = EFirearmSubPacketType.CheckAmmo;
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        }
        return flag;
    }

    public override bool ChangeFireMode(Weapon.EFireMode fireMode)
    {
        // 原生 AI 模式请求的寿命可能超过手部状态转换。Destroy
        // 会清除我们的包，而非空闲的原生操作仍可能
        // 返回 true。只拒绝被明确标记为 destroyed 的控制器；
        // 存活控制器上的初始化失败仍然保持可观测。
        if (Destroyed)
        {
            return false;
        }
        var flag = global::Fika.Core.Main.Utils.NativeBaseCalls.Call19(this, fireMode);
        if (flag)
        {
            _packet.Type = EFirearmSubPacketType.ChangeFireMode;
            _packet.SubPacket = ChangeFireModePacket.FromValue(fireMode);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        }
        return flag;
    }

    public override void ChangeAimingMode()
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call20(this);
        _packet.Type = EFirearmSubPacketType.ToggleAim;
        _packet.SubPacket = FirearmSubPacketPoolManager.Instance.GetPacket<IPoolSubPacket>(EFirearmSubPacketType.ToggleAim);
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
    }

    public override void SetAim(bool value)
    {
        var isAiming = IsAiming;
        var aimingInterruptedByOverlap = AimingInterruptedByOverlap;
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call21(this, value);
        if (IsAiming != isAiming || (aimingInterruptedByOverlap && _fikaPlayer.HealthController.IsAlive))
        {
            _packet.Type = EFirearmSubPacketType.ToggleAim;
            _packet.SubPacket = ToggleAimPacket.FromValue(IsAiming ? Item.AimIndex.Value : -1);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        }
    }

    public override void AimingChanged(bool newValue)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call22(this, newValue);
        if (!IsAiming && _fikaPlayer.HealthController.IsAlive)
        {
            _packet.Type = EFirearmSubPacketType.ToggleAim;
            _packet.SubPacket = ToggleAimPacket.FromValue(-1);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        }
    }

    public override bool CheckFireMode()
    {
        var flag = global::Fika.Core.Main.Utils.NativeBaseCalls.Call23(this);
        if (flag && _fikaPlayer.HealthController.IsAlive)
        {
            _packet.Type = EFirearmSubPacketType.CheckFireMode;
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);

        }
        return flag;
    }

    public override void DryShot(int chamberIndex = 0, bool underbarrelShot = false)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call24(this, chamberIndex, underbarrelShot);
        _packet.Type = EFirearmSubPacketType.ShotInfo;
        _packet.SubPacket = ShotInfoPacket.FromDryShot(chamberIndex, underbarrelShot, EShotType.DryFire);
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
    }

    public override bool ExamineWeapon()
    {
        var flag = global::Fika.Core.Main.Utils.NativeBaseCalls.Call25(this);
        if (flag && _fikaPlayer.HealthController.IsAlive)
        {
            _packet.Type = EFirearmSubPacketType.ExamineWeapon;
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        }
        return flag;
    }

    internal void SendInitiatedShot(IWeapon weapon, Ammo ammo, Vector3 shotPosition, Vector3 shotDirection,
        Vector3 fireportPosition, int chamberIndex, float overheat)
    {
        EShotType shotType = default;

        switch (weapon.MalfState.State)
        {
            case Weapon.EMalfunctionState.None:
                shotType = EShotType.RegularShot;
                break;
            case Weapon.EMalfunctionState.Misfire:
                shotType = EShotType.Misfire;
                break;
            case Weapon.EMalfunctionState.Jam:
                shotType = EShotType.JamedShot;
                break;
            case Weapon.EMalfunctionState.HardSlide:
                shotType = EShotType.HardSlidedShot;
                break;
            case Weapon.EMalfunctionState.SoftSlide:
                shotType = EShotType.SoftSlidedShot;
                break;
            case Weapon.EMalfunctionState.Feed:
                shotType = EShotType.Feed;
                break;
        }

        _packet.Type = EFirearmSubPacketType.ShotInfo;
        _packet.SubPacket = ShotInfoPacket.FromShot(shotPosition, shotDirection, ammo.TemplateId, overheat,
            weapon.MalfState.LastShotOverheat, weapon.MalfState.LastShotTime, Weapon.Repairable.Durability,
            chamberIndex, Weapon.IsUnderBarrelDeviceActive || _isGrenadeLauncher,
            weapon.MalfState.SlideOnOverheatReached, shotType);
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        _fikaPlayer.StatisticsManager.OnShot(Weapon, ammo);

    }

    public override void QuickReloadMag(Magazine magazine, Callback callback)
    {
        if (CanStartReload() && CurrentOperation != null && magazine != null)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call26(this, magazine, callback);
            _packet.Type = EFirearmSubPacketType.QuickReloadMag;
            _packet.SubPacket = QuickReloadMagPacket.FromValue(magazine.Id, true);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
            return;
        }

        callback?.Fail("Can't start QuickReloadMag");
    }

    public override void ReloadBarrels(AmmoPack ammoPack, ItemAddress placeToPutContainedAmmoMagazine, Callback callback)
    {
        if (CanStartReload() && CurrentOperation != null && ammoPack != null && ammoPack.AmmoCount > 0)
        {
            ReloadBarrelsHandler handler = new(_fikaPlayer, this, placeToPutContainedAmmoMagazine, ammoPack);
            CurrentOperation.ReloadBarrels(ammoPack, placeToPutContainedAmmoMagazine, callback, (Callback)handler.Process);
            return;
        }

        callback?.Fail("Can't start ReloadBarrels");
    }

    public override void ReloadCylinderMagazine(AmmoPack ammoPack, Callback callback, bool quickReload = false)
    {
        if (Blindfire)
        {
            return;
        }
        if (ammoPack == null || CurrentOperation == null || Item.GetCurrentMagazine() == null)
        {
            callback?.Fail("Cannot start ReloadCylinderMagazine");
            return;
        }
        if (CanStartReload() && CurrentOperation != null)
        {
            ReloadCylinderMagazineHandler handler = new(_fikaPlayer, this, quickReload, ammoPack.GetReloadingAmmoIds(),
            new Il2CppSystem.Collections.Generic.List<int>(), Item.GetCurrentMagazine().Cast<CylinderMagazine>());
            Weapon.GetShellsIndexes(handler.ShellsIndexes);
            CurrentOperation.ReloadCylinderMagazine(ammoPack, callback, (Callback)handler.Process, handler.QuickReload);
            return;
        }

        callback?.Fail("Can't start ReloadCylinderMagazine");
    }

    public override void ReloadGrenadeLauncher(AmmoPack ammoPack, Callback callback)
    {
        // SPT 5 的发射器路径有意跳过枪械故障
        // 门槛；这与原生发射器换弹操作以及
        // 官方 dev-5.0 客户端实现一致。
        if (CanStartReload(false) && CurrentOperation != null && ammoPack != null)
        {
            var reloadingAmmoIds = ammoPack.GetReloadingAmmoIds();
            _packet.Type = EFirearmSubPacketType.ReloadLauncher;
            _packet.SubPacket = ReloadLauncherPacket.FromValue(true, reloadingAmmoIds);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);

            CurrentOperation.ReloadGrenadeLauncher(ammoPack, callback);
            return;
        }

        callback?.Fail("Can't start ReloadGrenadeLauncher");
    }

    public override void ReloadMag(Magazine magazine, ItemAddress itemAddress, Callback callback)
    {
        if (!CanStartReload() || Blindfire || CurrentOperation == null || magazine == null)
        {
            return;
        }

        _player.MovementContext.PlayerAnimator.AnimatedInteractions.ForceStopInteractions();
        if (!_player.MovementContext.PlayerAnimator.AnimatedInteractions.IsInteractionPlaying)
        {
            ReloadMagHandler handler = new(_fikaPlayer, this, itemAddress, magazine);
            CurrentOperation.ReloadMag(magazine, itemAddress, callback, (Callback)handler.Process);
            return;
        }

        callback?.Fail("Can't start ReloadMag");
    }

    public override void ReloadWithAmmo(AmmoPack ammoPack, Callback callback)
    {
        if (ammoPack == null || Item.GetCurrentMagazine() == null || CurrentOperation == null)
        {
            callback?.Fail("Cannot start ReloadWithAmmo");
            return;
        }
        if (CanStartReload())
        {
            if (Item.TryCast<Revolver>() != null)
            {
                ReloadCylinderMagazine(ammoPack, callback);
                return;
            }

            ReloadWithAmmoHandler handler = new(_fikaPlayer, this, ammoPack.GetReloadingAmmoIds());
            CurrentOperation.ReloadWithAmmo(ammoPack, callback, (Callback)handler.Process);
            return;
        }

        callback?.Fail("Can't start ReloadWithAmmo");
    }

    public override bool SetLightsState(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<LightsState> lightsStates, bool force = false, bool animated = true)
    {
        if (CurrentOperation != null && (force || CurrentOperation.CanChangeLightState(lightsStates)))
        {
            _packet.Type = EFirearmSubPacketType.ToggleLightStates;
            _packet.SubPacket = LightStatesPacket.FromValue(lightsStates.Length, lightsStates);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        }

        return global::Fika.Core.Main.Utils.NativeBaseCalls.Call27(this, lightsStates, force, animated);
    }

    public override void SetScopeMode(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ScopeState> scopeStates)
    {
        SendScopeStates(scopeStates);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call28(this, scopeStates);
    }
    public override void OpticCalibrationSwitchUp(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ScopeState> scopeStates)
    {
        SendScopeStates(scopeStates);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call29(this, scopeStates);
    }

    public override void OpticCalibrationSwitchDown(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ScopeState> scopeStates)
    {
        SendScopeStates(scopeStates);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call30(this, scopeStates);
    }

    private void SendScopeStates(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ScopeState> scopeStates)
    {
        if (CurrentOperation == null || !CurrentOperation.CanChangeScopeStates(scopeStates))
        {
            return;
        }

        _packet.Type = EFirearmSubPacketType.ToggleScopeStates;
        _packet.SubPacket = ScopeStatesPacket.FromValue(scopeStates.Length, scopeStates);
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
    }

    public override void SmoothScopeZoomValueChanged(SightComponent sightComponent, float scopeZoomValue)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call118(this, sightComponent, scopeZoomValue);

        if (sightComponent == null || sightComponent.ScopesCount == 0)
        {
            return;
        }

        var sightItem = sightComponent.Item;
        if (sightItem == null)
        {
            return;
        }

        _packet.Type = EFirearmSubPacketType.ToggleScopeStates;
        _packet.SubPacket = ScopeStatesPacket.FromZoom(sightItem.Id, sightComponent.SelectedScope, scopeZoomValue);
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
    }

    public override void ShotMisfired(Ammo ammo, Weapon.EMalfunctionState malfunctionState, float overheat)
    {
        EShotType shotType = new();

        switch (malfunctionState)
        {
            case Weapon.EMalfunctionState.Misfire:
                shotType = EShotType.Misfire;
                break;
            case Weapon.EMalfunctionState.Jam:
                shotType = EShotType.JamedShot;
                break;
            case Weapon.EMalfunctionState.HardSlide:
                shotType = EShotType.HardSlidedShot;
                break;
            case Weapon.EMalfunctionState.SoftSlide:
                shotType = EShotType.SoftSlidedShot;
                break;
            case Weapon.EMalfunctionState.Feed:
                shotType = EShotType.Feed;
                break;
        }

        _packet.Type = EFirearmSubPacketType.ShotInfo;
        _packet.SubPacket = ShotInfoPacket.FromMisfire(ammo.TemplateId, overheat, shotType);
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);

        global::Fika.Core.Main.Utils.NativeBaseCalls.Call31(this, ammo, malfunctionState, overheat);
    }

    public override bool ToggleLauncher(Il2CppSystem.Action callback = null)
    {
        var flag = global::Fika.Core.Main.Utils.NativeBaseCalls.Call32(this, callback ?? FikaGlobals.EmptyNativeAction);
        if (flag)
        {
            _packet.Type = EFirearmSubPacketType.ToggleLauncher;
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        }
        return flag;
    }

    public override void Loot(bool p)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call33(this, p);
        _packet.Type = EFirearmSubPacketType.Loot;
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
    }

    public override void SetInventoryOpened(bool opened)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call34(this, opened);
        _packet.Type = EFirearmSubPacketType.ToggleInventory;
        _packet.SubPacket = ToggleInventoryPacket.FromValue(opened);
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
    }

    public override void ChangeLeftStance()
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call35(this);
        _packet.Type = EFirearmSubPacketType.LeftStanceChange;
        _packet.SubPacket = LeftStanceChangePacket.FromValue(_fikaPlayer.MovementContext.LeftStanceEnabled);
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
    }

    public override void SendStartOneShotFire()
    {
        _packet.Type = EFirearmSubPacketType.FlareShot;
        _packet.SubPacket = FlareShotPacket.FromValue(default, default, default, true);
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
    }

    public override void CreateFlareShot(Ammo flareItem, Vector3 shotPosition, Vector3 forward)
    {
        _packet.Type = EFirearmSubPacketType.FlareShot;
        _packet.SubPacket = FlareShotPacket.FromValue(shotPosition, forward, flareItem.TemplateId, false);
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call36(this, flareItem, shotPosition, forward);
        // 绿色（"ExitActivate"）信号弹会为发射者激活一次信号弹撤离点，
        // 默认情况下还包括其小队。原生侦测区域只把信号弹记录在
        // 亲眼看到它成功的那台机器上，所以客户端在这里把它上报给主机。
        global::Fika.Core.Main.Components.FikaFlareExfil.OnLocalFlareShot(flareItem, shotPosition);
    }

    public override void CreateRocketShot(Ammo rocketItem, Vector3 shotPosition, Vector3 forward, Transform smokeport = null)
    {
        _packet.Type = EFirearmSubPacketType.RocketShot;
        _packet.SubPacket = RocketShotPacket.FromValue(shotPosition, forward, rocketItem.TemplateId);
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call37(this, rocketItem, shotPosition, forward, smokeport);
    }

    private void SendAbortReloadPacket(int amount)
    {
        _packet.Type = EFirearmSubPacketType.ReloadWithAmmo;
        _packet.SubPacket = ReloadWithAmmoPacket.FromValue(EReloadWithAmmoStatus.AbortReload, amount);
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
    }

    public override void RollCylinder(bool rollToZeroCamora)
    {
        if (Blindfire || IsAiming)
        {
            return;
        }

        _packet.Type = EFirearmSubPacketType.RollCylinder;
        _packet.SubPacket = RollCylinderPacket.FromValue(rollToZeroCamora);
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);

        CurrentOperation?.RollCylinder(null, rollToZeroCamora);
    }

    private void SendEndReloadPacket(int amount)
    {
        if (_fikaPlayer.HealthController.IsAlive)
        {
            _packet.Type = EFirearmSubPacketType.ReloadWithAmmo;
            _packet.SubPacket = ReloadWithAmmoPacket.FromValue(EReloadWithAmmoStatus.EndReload, amount);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        }
    }

    private void SendBoltActionReloadPacket()
    {
        _packet.Type = EFirearmSubPacketType.ReloadBoltAction;
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
    }

    private class CylinderReloadOperation : Player.FirearmController.ReloadCylinderMagOperation
    {
        private readonly Player.FirearmController controller;

        public CylinderReloadOperation(Player.FirearmController controller) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<CylinderReloadOperation>())
        {
            this.controller = controller;
            coopClientFirearmController = (FikaClientFirearmController)controller;
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
            Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.FirearmController.ReloadCylinderMagOperation>(this, new object[] { controller });
        }


        public CylinderReloadOperation(System.IntPtr pointer) : base(pointer) { }


        public override void SetTriggerPressed(bool pressed)
        {
            var bool_ = ReloadAborted;
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call38(this, pressed);
            if (ReloadAborted && !bool_)
            {
                coopClientFirearmController.SendAbortReloadPacket(AmmoToLoadIntoMag);
            }
        }

        public override void SwitchToIdle()
        {
            coopClientFirearmController.SendEndReloadPacket(AmmoToLoadIntoMag);
            EndReload();
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call39(this);
        }

        private readonly FikaClientFirearmController coopClientFirearmController ;
    }

    private class AmmoPackReloadInternalOneChamberOperation : Player.FirearmController.ReloadInternalMagOperation
    {
        private readonly Player.FirearmController controller;

        public AmmoPackReloadInternalOneChamberOperation(Player.FirearmController controller) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<AmmoPackReloadInternalOneChamberOperation>())
        {
            this.controller = controller;
            _coopClientFirearmController = (FikaClientFirearmController)controller;
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
            Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.FirearmController.ReloadInternalMagOperation>(this, new object[] { controller });
        }


        public AmmoPackReloadInternalOneChamberOperation(System.IntPtr pointer) : base(pointer) { }


        public override void SetTriggerPressed(bool pressed)
        {
            var bool_ = ReloadAborted;
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call38(this, pressed);
            if (ReloadAborted && !bool_)
            {
                _coopClientFirearmController.SendAbortReloadPacket(AmmoToLoadIntoMag);
            }
        }

        public override void SwitchToIdle()
        {
            _coopClientFirearmController.SendEndReloadPacket(AmmoToLoadIntoMag);
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call40(this);
        }

        private readonly FikaClientFirearmController _coopClientFirearmController ;
    }

    private class AmmoPackReloadInternalBoltOpenOperation : Player.FirearmController.ReloadInternalMagWithOpenBoltOperation
    {
        private readonly Player.FirearmController controller;

        public AmmoPackReloadInternalBoltOpenOperation(Player.FirearmController controller) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<AmmoPackReloadInternalBoltOpenOperation>())
        {
            this.controller = controller;
            _coopClientFirearmController = (FikaClientFirearmController)controller;
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
            Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.FirearmController.ReloadInternalMagWithOpenBoltOperation>(this, new object[] { controller });
        }


        public AmmoPackReloadInternalBoltOpenOperation(System.IntPtr pointer) : base(pointer) { }


        public override void SetTriggerPressed(bool pressed)
        {
            var bool_ = ReloadAborted;
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call38(this, pressed);
            if (ReloadAborted && !bool_)
            {
                _coopClientFirearmController.SendAbortReloadPacket(AmmoToLoadIntoMag);
            }
        }

        public override void SwitchToIdle()
        {
            _coopClientFirearmController.SendEndReloadPacket(AmmoToLoadIntoMag);
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call41(this);
        }

        private readonly FikaClientFirearmController _coopClientFirearmController ;
    }

    private class DefaultFireOperation : Player.FirearmController.BoltActionFireOperation
    {
        private readonly Player.FirearmController controller;

        public DefaultFireOperation(Player.FirearmController controller) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<DefaultFireOperation>())
        {
            this.controller = controller;
            _coopClientFirearmController = (FikaClientFirearmController)controller;
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
            Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.FirearmController.BoltActionFireOperation>(this, new object[] { controller });
        }


        public DefaultFireOperation(System.IntPtr pointer) : base(pointer) { }


        public override void Start()
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call42(this);
            SendBoltActionReloadPacket(!Controller.IsTriggerPressed);
        }

        public override void SetTriggerPressed(bool pressed)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call43(this, pressed);
            SendBoltActionReloadPacket(!Controller.IsTriggerPressed);
        }

        public override void SetInventoryOpened(bool opened)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call44(this, opened);
            SendBoltActionReloadPacket(true);
        }

        public override void ReloadMag(Magazine magazine, ItemAddress gridItemAddress, Callback finishCallback, Callback startCallback)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call45(this, magazine, gridItemAddress, finishCallback, startCallback);
            SendBoltActionReloadPacket(true);
        }

        public override void QuickReloadMag(Magazine magazine, Callback finishCallback, Callback startCallback)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call46(this, magazine, finishCallback, startCallback);
            SendBoltActionReloadPacket(true);
        }

        public override void ReloadWithAmmo(AmmoPack ammoPack, Callback finishCallback, Callback startCallback)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call47(this, ammoPack, finishCallback, startCallback);
            SendBoltActionReloadPacket(true);
        }

        public override void ReloadCylinderMagazine(AmmoPack ammoPack, Callback finishCallback, Callback startCallback, bool quickReload = false)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call48(this, ammoPack, finishCallback, startCallback, quickReload);
            SendBoltActionReloadPacket(true);
        }

        private void SendBoltActionReloadPacket(bool value)
        {
            if (!_hasSent && value)
            {
                _hasSent = true;
                _coopClientFirearmController.SendBoltActionReloadPacket();
            }
        }

        public override void Reset()
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call49(this);
            _hasSent = false;
        }

        private readonly FikaClientFirearmController _coopClientFirearmController ;
        private bool _hasSent;
    }

    private sealed class ReloadMagHandler(FikaPlayer fikaPlayer, FikaClientFirearmController coopClientFirearmController, ItemAddress gridItemAddress, Magazine magazine)
    {
        private readonly FikaPlayer _fikaPlayer = fikaPlayer;
        private readonly FikaClientFirearmController _coopClientFirearmController = coopClientFirearmController;
        private readonly ItemAddress _gridItemAddress = gridItemAddress;
        private readonly Magazine _magazine = magazine;

        public void Process(IResult _)
        {
            if (_fikaPlayer.HealthController.IsAlive)
            {
                _coopClientFirearmController._packet.Type = EFirearmSubPacketType.ReloadMag;
                _coopClientFirearmController._packet.SubPacket = ReloadMagPacket.FromValue(_magazine.Id, _gridItemAddress);
                _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _coopClientFirearmController._packet, DeliveryMethod.ReliableOrdered, true);
            }
        }
    }

    private sealed class ReloadCylinderMagazineHandler(FikaPlayer fikaPlayer, FikaClientFirearmController coopClientFirearmController, bool quickReload, string[] ammoIds, Il2CppSystem.Collections.Generic.List<int> shellsIndexes, CylinderMagazine cylinderMagazine)
    {
        private readonly FikaPlayer _fikaPlayer = fikaPlayer;
        private readonly FikaClientFirearmController _coopClientFirearmController = coopClientFirearmController;
        public readonly bool QuickReload = quickReload;
        private readonly string[] _ammoIds = ammoIds;
        public readonly Il2CppSystem.Collections.Generic.List<int> ShellsIndexes = shellsIndexes;
        private readonly CylinderMagazine _cylinderMagazine = cylinderMagazine;

        public void Process(IResult _)
        {
            if (_fikaPlayer.HealthController.IsAlive)
            {
                _coopClientFirearmController._packet.Type = EFirearmSubPacketType.CylinderMag;
                _coopClientFirearmController._packet.SubPacket = CylinderMagPacket.FromValue(EReloadWithAmmoStatus.StartReload,
                    _cylinderMagazine.CurrentCamoraIndex, 0, true,
                    _coopClientFirearmController.Item.CylinderHammerClosed, _ammoIds);
                _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _coopClientFirearmController._packet, DeliveryMethod.ReliableOrdered, true);
            }
        }
    }

    private sealed class ReloadBarrelsHandler(FikaPlayer fikaPlayer, FikaClientFirearmController coopClientFirearmController, ItemAddress placeToPutContainedAmmoMagazine, AmmoPack ammoPack)
    {
        private readonly FikaPlayer _fikaPlayer = fikaPlayer;
        private readonly FikaClientFirearmController _coopClientFirearmController = coopClientFirearmController;
        private readonly ItemAddress _placeToPutContainedAmmoMagazine = placeToPutContainedAmmoMagazine;
        private readonly AmmoPack _ammoPack = ammoPack;

        public void Process(IResult _)
        {
            if (_fikaPlayer.HealthController.IsAlive)
            {
                _coopClientFirearmController._packet.Type = EFirearmSubPacketType.ReloadBarrels;
                _coopClientFirearmController._packet.SubPacket = ReloadBarrelsPacket.FromValue(_ammoPack.GetReloadingAmmoIds(), _placeToPutContainedAmmoMagazine);
                _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _coopClientFirearmController._packet, DeliveryMethod.ReliableOrdered, true);
            }
        }
    }

    private sealed class ReloadWithAmmoHandler(FikaPlayer fikaPlayer, FikaClientFirearmController coopClientFirearmController, string[] ammoIds)
    {
        private readonly FikaPlayer _fikaPlayer = fikaPlayer;
        private readonly FikaClientFirearmController _coopClientFirearmController = coopClientFirearmController;
        private readonly string[] _ammoIds = ammoIds;

        public void Process(IResult _)
        {
            if (_fikaPlayer.HealthController.IsAlive)
            {
                _coopClientFirearmController._packet.Type = EFirearmSubPacketType.ReloadWithAmmo;
                _coopClientFirearmController._packet.SubPacket = ReloadWithAmmoPacket.FromValue(EReloadWithAmmoStatus.StartReload, ammoIds: _ammoIds);
                _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _coopClientFirearmController._packet, DeliveryMethod.ReliableOrdered, true);
            }
        }
    }
}