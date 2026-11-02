// © 2026 Lacyway All Rights Reserved

using System;
using System.Collections;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.FirearmController.SubPackets;
using HarmonyLib;
using static EFT.Player;

namespace Fika.Core.Main.ObservedClasses.HandsControllers;

public sealed class ObservedFirearmController : FirearmController
{
    public ObservedFirearmController(System.IntPtr pointer) : base(pointer) {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<FirearmController>(this);
    }


    public ObservedFirearmController() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedFirearmController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public Firearms WeaponManager
    {
        get
        {
            return _weaponManager;
        }
    }
    public override bool IsTriggerPressed
    {
        get
        {
            return _triggerPressed;
        }
    }

    public bool IsRevolver { get; internal set; }

    private ObservedPlayer _observedPlayer;
    private bool _triggerPressed;
    private bool _needsReset;
    private float _lastFireTime;
    private float _overlapCounter;
    private bool _hasFired;
    private Firearms _weaponManager;
    private UnderbarrelContainer _underBarrelManager;
    private bool _boltActionReload;
    private bool _isThrowingPatron;
    private bool _stationaryWeapon;
    private Weapon.EFireMode? _pendingFireMode;

    public override bool IsAiming
    {
        get
        {
            return _isAiming;
        }
        set
        {
            if (_isAiming == value)
            {
                if (FirearmsAnimator != null)
                {
                    ResetAimingAnimationsFlags(); // Reset animator flags 
                }
                return;
            }
            _isAiming = value;
            SetAimingInAnimator(_isAiming); // Set animator flags
            _observedPlayer.ProceduralWeaponAnimation.IsAiming = _isAiming;
        }
    }

    public override Vector3 WeaponDirection
    {
        get
        {
            return -CurrentFireport.up;
        }
    }

    public override Il2CppSystem.Collections.Generic.Dictionary<Il2CppSystem.Type, OperationFactoryDelegate> GetOperationFactoryDelegates()
    {
        // 检查 GClass 的递增..
        var operationFactoryDelegates = global::Fika.Core.Main.Utils.NativeBaseCalls.Call12(this);
        operationFactoryDelegates[Il2CppInterop.Runtime.Il2CppType.Of<Player.FirearmController.Idling>()] = (OperationFactoryDelegate)Idle1;
        // 查找实现了 OnShellEjectEvent 和 ThrowPatronAsLoot 的操作
        operationFactoryDelegates[Il2CppInterop.Runtime.Il2CppType.Of<Player.FirearmController.ReloadMultiBarrelOperation>()] = (OperationFactoryDelegate)ThrowPatron1;
        operationFactoryDelegates[Il2CppInterop.Runtime.Il2CppType.Of<Player.FirearmController.ReloadSingleBarrelOperation>()] = (OperationFactoryDelegate)ThrowPatron2;
        operationFactoryDelegates[Il2CppInterop.Runtime.Il2CppType.Of<Player.FirearmController.RepairMalfunction>()] = (OperationFactoryDelegate)ThrowPatron3;
        operationFactoryDelegates[Il2CppInterop.Runtime.Il2CppType.Of<Player.FirearmController.RechamberOperation>()] = (OperationFactoryDelegate)ThrowPatron4;
        return operationFactoryDelegates;
    }

    private Player.ObjectInHandsOperation ThrowPatron1()
    {
        return new ObservedThrowPatronOperation1(this);
    }

    private Player.ObjectInHandsOperation ThrowPatron2()
    {
        return new ObservedThrowPatronOperation2(this);
    }

    private Player.ObjectInHandsOperation ThrowPatron3()
    {
        return new ObservedThrowPatronOperation3(this);
    }

    private Player.ObjectInHandsOperation ThrowPatron4()
    {
        return new ObservedThrowPatronOperation4(this);
    }

    private Player.ObjectInHandsOperation Idle1()
    {
        return new ObservedIdleOperation(this);
    }

    private void Start()
    {
        _objectInHandsAnimator.SetAiming(false);
        _weaponManager = _weaponPrefab.ObjectInHands.TryCast<Firearms>();
        if (UnderbarrelWeapon != null)
        {
            _underBarrelManager = this._underbarrelContainer;
        }
        IsRevolver = Weapon.TryCast<Revolver>() != null;
        _stationaryWeapon = Weapon.IsStationaryWeapon;
    }

    public static ObservedFirearmController Create(ObservedPlayer player, Weapon weapon)
    {
        var controller = CreateController<ObservedFirearmController>(player, weapon);
        controller._observedPlayer = player;
        return controller;
    }

    public override bool CanStartReload(bool checkWeaponMalfunctions = true)
    {
        return true;
    }

    public override bool IsInInteractionStrictCheck()
    {
        return false;
    }

    public override void SetAim(int scopeIndex)
    {
        Item.AimIndex.Value = Mathf.Max(0, scopeIndex);
        SetObservedAim(scopeIndex >= 0);
    }

    private void SetObservedAim(bool isAiming)
    {
        IsAiming = isAiming;
        _player.PlayGearSound(0.2f, false);
        if (isAiming)
        {
            PlayAimingSound();
        }
    }

    public override void ManualUpdate(float deltaTime)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call76(this, deltaTime);
        ApplyPendingFireMode();
        if (_hasFired)
        {
            _lastFireTime += deltaTime;
            if (_lastFireTime > 0.1f)
            {
                FirearmsAnimator.SetFire(false);
                _hasFired = false;
                if (_needsReset)
                {
                    _needsReset = false;
                    WeaponSoundPlayer.OnBreakLoop();
                }
            }
        }
    }

    public void ReceiveFireMode(Weapon.EFireMode fireMode)
    {
        // 远程控制器可能仍在掏枪，而主机已经
        // 处于空闲状态。SPT 5 的非空闲操作可能返回 true 却不
        // 改变模式。在空闲之前保留最新的有序更新。
        _pendingFireMode = fireMode;
        ApplyPendingFireMode();
    }

    private void ApplyPendingFireMode()
    {
        if (Destroyed)
        {
            _pendingFireMode = null;
            return;
        }
        if (!_pendingFireMode.HasValue || CurrentOperation?.TryCast<FirearmController.Idling>() == null)
        {
            return;
        }

        var mode = _pendingFireMode.Value;
        ChangeFireMode(mode);
        if (Item.FireMode.FireMode == mode)
        {
            _pendingFireMode = null;
        }
    }

    public override void Destroy()
    {
        _pendingFireMode = null;
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call9(this);
    }

    public override void ReloadMag(Magazine magazine, ItemAddress itemAddress, Callback callback)
    {
        _player.MovementContext.PlayerAnimator.AnimatedInteractions.ForceStopInteractions();
        CurrentOperation.ReloadMag(magazine, itemAddress, callback, null);
    }

    public override void QuickReloadMag(Magazine magazine, Callback callback)
    {
        CurrentOperation.QuickReloadMag(magazine, callback, null);
    }

    public override void ReloadGrenadeLauncher(AmmoPack foundItem, Callback callback)
    {
        CurrentOperation.ReloadGrenadeLauncher(foundItem, callback);
    }

    public override void ReloadCylinderMagazine(AmmoPack ammoPack, Callback callback, bool quickReload = false)
    {
        if (Item.GetCurrentMagazine() == null)
        {
            return;
        }

        CurrentOperation.ReloadCylinderMagazine(ammoPack, callback, null, quickReload);
    }

    public override void ReloadWithAmmo(AmmoPack ammoPack, Callback callback)
    {
        if (IsRevolver)
        {
            CurrentOperation.ReloadCylinderMagazine(ammoPack, callback, null, false);
            return;
        }

        CurrentOperation.ReloadWithAmmo(ammoPack, callback, null);
    }

    public override void ReloadBarrels(AmmoPack ammoPack, ItemAddress placeToPutContainedAmmoMagazine, Callback callback)
    {
        if (ammoPack.AmmoCount > 0)
        {
            CurrentOperation.ReloadBarrels(ammoPack, placeToPutContainedAmmoMagazine, callback, null);
        }
    }

    public override void WeaponOverlapping()
    {
        if (!_observedPlayer.ShouldOverlap)
        {
            return;
        }

        SetWeaponOverlapValue(_observedPlayer.ObservedOverlap);
        ObservedOverlapView();
        if (_overlapCounter <= 1f)
        {
            _overlapCounter += Time.deltaTime / 1f;
        }
        if (_observedPlayer.LeftStanceDisabled && _observedPlayer.MovementContext.LeftStanceEnabled && _overlapCounter > 1f)
        {
            _observedPlayer.MovementContext.LeftStanceController.DisableLeftStanceAnimFromHandsAction();
            _overlapCounter = 0f;
        }
        if (!_observedPlayer.MovementContext.LeftStanceController.LastAnimValue && !_observedPlayer.LeftStanceDisabled && _observedPlayer.MovementContext.LeftStanceEnabled && _overlapCounter > 1f)
        {
            _observedPlayer.MovementContext.LeftStanceController.SetAnimatorLeftStanceToCacheFromHandsAction();
            _overlapCounter = 0f;
        }

        _observedPlayer.ShouldOverlap = false;
    }

    private void ObservedOverlapView()
    {
        if (_observedPlayer.ObservedOverlap < 0.02f)
        {
            _player.ProceduralWeaponAnimation.TurnAway.OverlapDepth = _observedPlayer.ObservedOverlap;
            _player.ProceduralWeaponAnimation.OverlappingAllowsBlindfire = true;
            _observedPlayer.ShouldOverlap = false;
            return;
        }

        var vector = _player.ProceduralWeaponAnimation.HandsContainer.HandsPosition.Get();
        _player.ProceduralWeaponAnimation.OverlappingAllowsBlindfire = false;
        _player.ProceduralWeaponAnimation.TurnAway.OriginZShift = vector.y;
        _player.ProceduralWeaponAnimation.TurnAway.OverlapDepth = _observedPlayer.ObservedOverlap;
    }

    public override void OnPlayerDead()
    {
        try
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call13(this);
            _triggerPressed = false;
            SetTriggerPressed(false);

            _needsReset = false;
            WeaponSoundPlayer.OnBreakLoop();

            _observedPlayer.HandsAnimator.Animator.Update(Time.fixedDeltaTime);
            ManualUpdate(Time.fixedDeltaTime);
            if (CurrentOperation.State != EOperationState.Finished)
            {
                CurrentOperation.FastForward();
            }

            Fika.Core.Il2CppCoroutineExtensions.StartCoroutine(this, BreakFiringLoop());
        }
        catch (Exception ex)
        {
            FikaGlobals.LogError("CoopObservedFirearmController::OnPlayerDead: Exception was caught: " + ex.Message);
        }
    }

    public override void IEventsConsumerOnShellEject()
    {
        if (_isThrowingPatron)
        {
            _isThrowingPatron = false;
            CurrentOperation?.OnShellEjectEvent();
            return;
        }

        _weaponManager.StartSpawnShell(_observedPlayer.Velocity * 0.66f, 0);
        if (_boltActionReload)
        {
            FirearmsAnimator.SetBoltActionReload(false);
            FirearmsAnimator.SetFire(false);

            _boltActionReload = false;
        }
    }

    public override void CompassStateHandler(bool isActive)
    {
        /*_observedPlayer.CreateObservedCompass();
        _objectInHandsAnimator.ShowCompass(isActive);
        _observedPlayer.SetPropVisibility(isActive);*/
    }

    private IEnumerator BreakFiringLoop()
    {
        WeaponSoundPlayer.ReleaseClipsSource();
        var attempts = 0;
        WaitForEndOfFrame waitForEndOfFrame = new();
        while (WeaponSoundPlayer.IsFiring && attempts < 10)
        {
            yield return waitForEndOfFrame;
            WeaponSoundPlayer.StopFiringLoop();
            attempts++;
        }
    }

    public override void SetScopeMode(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ScopeState> scopeStates)
    {
        _player.ProceduralWeaponAnimation.ObservedCalibration();
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call28(this, scopeStates);
    }

    public void SetScopeZoom(string sightId, int scopeIndex, float zoomValue)
    {
        var visualControllers = _weaponManager?._sightModVisualControllers;
        if (visualControllers == null)
        {
            return;
        }

        for (var i = 0; i < visualControllers.Length; i++)
        {
            var visualController = visualControllers[i];
            var sight = visualController?.SightMod;
            if (sight == null || sight.Item == null || sight.Item.Id != sightId)
            {
                continue;
            }

            if (scopeIndex >= 0 && scopeIndex < sight.ScopesCount)
            {
                sight.SelectedScopeIndex = scopeIndex;
            }

            sight.ScopeZoomValue = zoomValue;
            visualController._scopeZoomHandler?.SetSpectatorZoomValue(zoomValue);
            return;
        }
    }

    // AdjustShotVectors 在这里曾是空的 override。对这种（注入的）类型的 override 会让
    // interop 为它生成反向 P/Invoke 入口点（invoker + trampoline），运行时会在
    // 到达它的那次原生回调中首次 JIT 编译该入口点——此处的失败是不可捕获的
    // 0x80131506 fail fast，而不是托管异常。该行为（受观察的
    // 玩家不得在本地调整射击向量）现在改为在原生方法上表达：
    // Main/Patches/HandsController/FirearmController_AdjustShotVectors_Patch.cs。参见
    // analysis/adjust-shot-vectors/REPORT.md。

    public override bool CanChangeCompassState(bool newState)
    {
        return false;
    }

    public override bool CanRemove()
    {
        return true;
    }

    public override void OnCanUsePropChanged(bool canUse)
    {
        // 不执行任何操作
    }

    public override void SetCompassState(bool active)
    {
        // 不执行任何操作
    }

    public override bool ToggleBipod()
    {
        return HasBipod && CurrentOperation.ToggleBipod();
    }

    /// <summary>
    /// 发射一枚复制的火箭弹
    /// </summary>
    /// <param name="rocketClass">要射击的弹药</param>
    /// <param name="shotPosition">起始位置</param>
    /// <param name="shotForward">前向速度</param>
    public void HandleRocketShot(Ammo rocketClass, Vector3 shotPosition, Vector3 shotForward)
    {
        FirearmsAnimator.SetFire(true);

        // 处理火箭弹射击
        rocketClass.IsUsed = true;
        var smokePort = TransformTools.FindTransformRecursiveContains(WeaponRoot.transform, "smokeport", false);
        InitiateRocket(rocketClass.Cast<Rocket>(), shotPosition, shotForward, smokePort);
        Weapon.FirstLoadedChamberSlot.RemoveItem();
        WeaponManager.MoveAmmoFromChamberToShellPort(true, 0);

        FirearmsAnimator.SetFire(false);
    }

    public void HandleShotInfoPacket(ShotInfoPacket packet, InventoryController inventoryController)
    {
        if (packet.ShotType == EShotType.DryFire)
        {
            HandleObservedDryShot();
            return;
        }

        if (packet.ShotType >= EShotType.Misfire)
        {
            switch (packet.ShotType)
            {
                case EShotType.Misfire:
                    Weapon.MalfState.State = Weapon.EMalfunctionState.Misfire;
                    break;
                case EShotType.Feed:
                    Weapon.MalfState.State = Weapon.EMalfunctionState.Feed;
                    break;
                case EShotType.JamedShot:
                    Weapon.MalfState.State = Weapon.EMalfunctionState.Jam;
                    break;
                case EShotType.SoftSlidedShot:
                    Weapon.MalfState.State = Weapon.EMalfunctionState.SoftSlide;
                    break;
                case EShotType.HardSlidedShot:
                    Weapon.MalfState.State = Weapon.EMalfunctionState.HardSlide;
                    break;
            }

            var ammo = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), packet.AmmoTemplate, null).Cast<Ammo>();
            Weapon.MalfState.MalfunctionedAmmo = ammo;
            Weapon.MalfState.AmmoToFire = ammo;

            FirearmsAnimator.MisfireSlideUnknown(false);
            _weaponPrefab.InitMalfunctionState(Weapon, false, false, out _);
            FirearmsAnimator.Malfunction((int)Weapon.MalfState.State);

            switch (Weapon.MalfState.State)
            {
                case Weapon.EMalfunctionState.Misfire:
                    FirearmsAnimator.Animator.Play("MISFIRE", 1, 0f);
                    break;
                case Weapon.EMalfunctionState.Jam:
                    FirearmsAnimator.Animator.Play("JAM", 1, 0f);
                    break;
                case Weapon.EMalfunctionState.HardSlide:
                    FirearmsAnimator.Animator.Play("HARD_SLIDE", 1, 0f);
                    break;
                case Weapon.EMalfunctionState.SoftSlide:
                    FirearmsAnimator.Animator.Play("SOFT_SLIDE", 1, 0f);
                    break;
                case Weapon.EMalfunctionState.Feed:
                    FirearmsAnimator.Animator.Play("FEED", 1, 0f);
                    break;
            }

            if (Weapon.MalfState.State == Weapon.EMalfunctionState.Misfire)
            {
                if (Weapon.HasChambers)
                {
                    var firstChamber = Weapon.Chambers[0];
                    if (firstChamber.ContainedItem?.TryCast<Ammo>() != null)
                    {
                        firstChamber.RemoveItemWithoutRestrictions();
                    }
                }

                FirearmsAnimator.SetFire(true);
                _hasFired = true;
                _lastFireTime = 0f;
                return;
            }

            if (Weapon.HasChambers)
            {
                var firstChamber = Weapon.Chambers[0];
                if (firstChamber.ContainedItem?.TryCast<Ammo>() != null)
                {
                    firstChamber.RemoveItemWithoutRestrictions();
                }

                if (Weapon.MalfState.State == Weapon.EMalfunctionState.Feed)
                {
                    var currentMagazine = Weapon.GetCurrentMagazine();
                    if (currentMagazine != null)
                    {
                        var fedAmmo = currentMagazine.Cartridges.PopToNowhere(_observedPlayer.InventoryController).Value.ResultItem.Cast<Ammo>();
                        if (fedAmmo != null)
                        {
                            Weapon.MalfState.MalfunctionedAmmo = fedAmmo;
                            // 暂时保留在此处 - Lacyway
                            /*weaponManager.SetRoundIntoWeapon(fedAmmo, 0);
                            weaponManager.MoveAmmoFromChamberToShellPort(false, 0);*/
                        }
                        else
                        {
                            FikaGlobals.LogError("HandleShotInfoPacket: Could not find ammo when setting up feed malfunction!");
                        }
                    }
                }
                else
                {
                    _weaponManager.MoveAmmoFromChamberToShellPort(true, 0);
                }
            }
        }

        if (IsRevolver)
        {
            HandleRevolverShot(packet, inventoryController);
            return;
        }
        HandleObservedShot(packet, inventoryController);
    }

    private void HandleObservedDryShot()
    {
        if (IsRevolver)
        {
            var revolver = Weapon;
            var cylinderMagazine = revolver.GetCurrentMagazine().Cast<CylinderMagazine>();

            revolver.CylinderHammerClosed = revolver.FireMode.FireMode == Weapon.EFireMode.doubleaction;
            FirearmsAnimator.SetCamoraFireIndex(cylinderMagazine.CurrentCamoraIndex);
            if ((revolver.CylinderHammerClosed && revolver.FireMode.FireMode == Weapon.EFireMode.doubleaction)
                || (!revolver.CylinderHammerClosed && revolver.FireMode.FireMode == Weapon.EFireMode.single))
            {
                cylinderMagazine.DryFireIncrementCamoraIndex();
            }
            FirearmsAnimator.SetDoubleAction(Convert.ToSingle(revolver.CylinderHammerClosed));
            FirearmsAnimator.SetCamoraIndex(cylinderMagazine.CurrentCamoraIndex);

            FirearmsAnimator.SetFire(true);
            DryShot();
            _hasFired = true;
            _lastFireTime = 0f;
            return;
        }
        FirearmsAnimator.SetFire(true);
        DryShot();
        _hasFired = true;
        _lastFireTime = 0f;
    }

    private void HandleRevolverShot(ShotInfoPacket packet, InventoryController inventoryController)
    {
        var revolver = Weapon;
        var cylinderMagazine = revolver.GetCurrentMagazine().Cast<CylinderMagazine>();

        var ammo = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), packet.AmmoTemplate, null).Cast<Ammo>();
        _observedPlayer.TurnOffFbbikAt = Time.time + 0.6f;
        InitiateShot(Item, ammo, packet.ShotPosition, packet.ShotDirection,
            CurrentFireport.position, packet.ChamberIndex, packet.Overheat);

        WeaponSoundPlayer.FireBullet(ammo, packet.ShotPosition, packet.ShotDirection, false);

        SetMalfAndDurability(packet, revolver);

        revolver.CylinderHammerClosed = revolver.FireMode.FireMode == Weapon.EFireMode.doubleaction;
        FirearmsAnimator.SetCamoraFireIndex(cylinderMagazine.CurrentCamoraIndex);
        var firstIndex = cylinderMagazine.GetCamoraFireOrLoadStartIndex(!revolver.CylinderHammerClosed);
        var cylinderAmmo = cylinderMagazine.GetFirstAmmo(!revolver.CylinderHammerClosed);
        if (cylinderAmmo != null)
        {
            var removeOperation = cylinderMagazine.RemoveAmmoInCamora(cylinderAmmo, inventoryController);
            if (removeOperation.Failed)
            {
                FikaGlobals.LogError($"Error removing ammo from cylinderMagazine on netId [{_observedPlayer.NetId}], error: {removeOperation.Error}");
            }
            inventoryController.CheckChamber(revolver, false);
            cylinderAmmo.IsUsed = true;
            revolver.ShellsInChambers[firstIndex] = cylinderAmmo.AmmoTemplate;
        }
        if (revolver.CylinderHammerClosed || revolver.FireMode.FireMode != Weapon.EFireMode.doubleaction)
        {
            cylinderMagazine.IncrementCamoraIndex(false);
        }
        FirearmsAnimator.SetCamoraIndex(cylinderMagazine.CurrentCamoraIndex);
        if (cylinderMagazine.Count > 0)
        {
            revolver.CylinderHammerClosed = revolver.FireMode.FireMode == Weapon.EFireMode.doubleaction;
        }
        FirearmsAnimator.SetDoubleAction(Convert.ToSingle(revolver.CylinderHammerClosed));
        FirearmsAnimator.SetHammerArmed(!revolver.CylinderHammerClosed);
        _weaponManager.MoveAmmoFromChamberToShellPort(true, firstIndex);

        FirearmsAnimator.SetAmmoOnMag(cylinderMagazine.Count);

        FirearmsAnimator.SetFire(true);

        if (revolver.MalfState.State == Weapon.EMalfunctionState.None)
        {
            if (IsRevolver && Weapon.CylinderHammerClosed)
            {
                FirearmsAnimator.Animator.Play(FirearmsAnimator.FullDoubleActionFireStateName, 1, 0.2f);
            }
            else
            {
                FirearmsAnimator.Animator.Play(FirearmsAnimator.FullFireStateName, 1, 0.2f);
            }
        }

        FirearmsAnimator.SetFire(false);
    }

    private void HandleObservedShot(ShotInfoPacket packet, InventoryController inventoryController)
    {
        var ammo = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), packet.AmmoTemplate, null).Cast<Ammo>();
        _observedPlayer.TurnOffFbbikAt = Time.time + 0.6f;
        InitiateShot(Item, ammo, packet.ShotPosition, packet.ShotDirection,
            CurrentFireport.position, packet.ChamberIndex, packet.Overheat);

        var weapon = Weapon;
        SetMalfAndDurability(packet, weapon);

        if (_stationaryWeapon)
        {
            _player.MovementContext.StationaryWeapon.ObservedShot();
        }

        if (weapon.SelectedFireMode == Weapon.EFireMode.fullauto)
        {
            _triggerPressed = true;
        }

        WeaponSoundPlayer.FireBullet(ammo, packet.ShotPosition, packet.ShotDirection, false);

        _triggerPressed = false;
        _hasFired = true;
        _lastFireTime = 0f;
        if (weapon.SelectedFireMode == Weapon.EFireMode.fullauto)
        {
            _needsReset = true;
        }

        var magazine = weapon.GetCurrentMagazine();

        FirearmsAnimator.SetFire(true);

        if (weapon.MalfState.State == Weapon.EMalfunctionState.None)
        {
            if (weapon.FireMode.FireMode == Weapon.EFireMode.semiauto)
            {
                FirearmsAnimator.Animator.Play(FirearmsAnimator.FullSemiFireStateName, 1, 0.2f);
            }
            else
            {
                FirearmsAnimator.Animator.Play(FirearmsAnimator.FullFireStateName, 1, 0.2f);
            }
        }

        if (packet.UnderbarrelShot)
        {
            if (UnderbarrelWeapon != null)
            {
                if (UnderbarrelWeapon.Chamber.ContainedItem?.TryCast<Ammo>() is { } grenadeBullet && !grenadeBullet.IsUsed)
                {
                    grenadeBullet.IsUsed = true;
                    UnderbarrelWeapon.Chamber.RemoveItem();
                    _underBarrelManager?.DestroyPatronInWeapon();
                }
                FirearmsAnimator.SetFire(false);
                return;
            }

            if (weapon.ReloadMode == Weapon.EReloadMode.OnlyBarrel)
            {
                var slot = weapon.FirstLoadedChamberSlot;
                var index = Array.IndexOf(weapon.Chambers, slot);
                if (slot.ContainedItem?.TryCast<Ammo>() is { } grenadeBullet && !grenadeBullet.IsUsed)
                {
                    grenadeBullet.IsUsed = true;
                    slot.RemoveItem();
                    _weaponManager.MoveAmmoFromChamberToShellPort(true, index);
                    weapon.ShellsInChambers[index] = grenadeBullet.AmmoTemplate;
                    FirearmsAnimator.SetAmmoInChamber(weapon.ChamberAmmoCount);
                    FirearmsAnimator.SetShellsInWeapon(weapon.ShellsInWeaponCount);
                }

                FirearmsAnimator.SetFire(false);
                return;
            }
        }

        var hasChambers = weapon.HasChambers;
        if (hasChambers)
        {
            if (weapon.ReloadMode is Weapon.EReloadMode.OnlyBarrel)
            {
                if (weapon.FireMode.FireMode == Weapon.EFireMode.single)
                {
                    var firstLoadedSlot = weapon.FirstLoadedChamberSlot;
                    var index = Array.IndexOf(weapon.Chambers, firstLoadedSlot);
                    HandleBarrelOnlyShot(weapon, index);
                }
                else
                {
                    for (var i = 0; i < weapon.Chambers.Length; i++)
                    {
                        HandleBarrelOnlyShot(weapon, i);
                    }
                }
            }
            else
            {
                weapon.Chambers[0].RemoveItem();
                HandleShellEvent(_weaponManager, packet.ChamberIndex, ammo, magazine);
            }
            FirearmsAnimator.SetAmmoInChamber(weapon.ChamberAmmoCount);
        }

        ammo.IsUsed = true;

        // SPT 5 在 ProcessInventoryOperationsForSingleShot 中也会为栓动
        // 枪械提交下一发枪弹。受观察的动画事件可能被剔除，因此它们
        // 不得负责库存变更，也不得分配下一发枪弹的 ID。
        if (magazine?.Count > 0)
        {
            if (hasChambers && magazine.IsAmmoCompatible(Item.Chambers) && Item.Chambers[0].ContainedItem == null)
            {
                magazine.Cartridges.PopTo(inventoryController, Item.Chambers[0].CreateItemAddress());
                FirearmsAnimator.SetAmmoInChamber(weapon.ChamberAmmoCount);
            }
            else
            {
                FikaShotInventory.PopAndClone(magazine.Cartridges, inventoryController);
            }
        }

        if (weapon.IsBoltCatch && weapon.ChamberAmmoCount == 1 && !weapon.ManualBoltCatch && !weapon.MustBoltBeOpennedForExternalReload && !weapon.MustBoltBeOpennedForInternalReload)
        {
            FirearmsAnimator.SetBoltCatch(false);
        }

        if (ammo.AmmoTemplate.IsLightAndSoundShot)
        {
            LightAndSoundShotVisualEffect(packet.ShotPosition, packet.ShotDirection);
            LightAndSoundShot(packet.ShotPosition, packet.ShotDirection, ammo.AmmoTemplate);
        }
    }

    private void SetMalfAndDurability(ShotInfoPacket packet, Weapon weapon)
    {
        weapon.Repairable.Durability = Mathf.Clamp(packet.Durability, 0f, weapon.Repairable.MaxDurability);

        weapon.MalfState.LastShotOverheat = packet.LastShotOverheat;
        weapon.MalfState.LastShotTime = packet.LastShotTime;
        weapon.MalfState.SlideOnOverheatReached = packet.SlideOnOverheatReached;
    }

    /// <summary>
    /// 在换弹模式为 <see cref="Weapon.EReloadMode.OnlyBarrel"/> 时处理射击
    /// </summary>
    /// <param name="weapon">要处理的武器</param>
    /// <param name="index">弹膛的索引</param>
    private void HandleBarrelOnlyShot(Weapon weapon, int index)
    {
        var ammoSlot = weapon.Chambers[index];
        if (ammoSlot.ContainedItem?.TryCast<Ammo>() is { } bullet && !bullet.IsUsed)
        {
            bullet.IsUsed = true;
            if (!bullet.AmmoTemplate.RemoveShellAfterFire)
            {
                _weaponManager.MoveAmmoFromChamberToShellPort(bullet.IsUsed, index);
            }
            else
            {
                _weaponManager.DestroyPatronInWeapon();
            }
            ammoSlot.RemoveItem();
        }
    }

    public void HandleObservedBoltAction()
    {
        FirearmsAnimator.SetBoltActionReload(true);
        FirearmsAnimator.SetFire(true);

        _boltActionReload = true;
    }

    public Il2CppSystem.Collections.Generic.List<Ammo> FindAmmoByIds(string[] ammoIds)
    {
        _preallocatedAmmoList.Clear();
        foreach (var id in ammoIds)
        {
            var gstruct = _player.FindItemById(id);
            if (gstruct.Succeeded && gstruct.Value?.TryCast<Ammo>() is { } bulletClass)
            {
                _preallocatedAmmoList.Add(bulletClass);
            }
        }
        return _preallocatedAmmoList;
    }


    private void HandleShellEvent(Firearms weaponEffectsManager, int chamberIndex, Ammo ammo, Magazine magazine)
    {
        weaponEffectsManager.DestroyPatronInWeapon(chamberIndex);
        if (!ammo.AmmoTemplate.RemoveShellAfterFire)
        {
            weaponEffectsManager.CreatePatronInShellPort(ammo, chamberIndex);
            FirearmsAnimator.SetShellsInWeapon(1);
        }
        else
        {
            FirearmsAnimator.SetShellsInWeapon(0);
        }

        var boltAction = Weapon.BoltAction;

        if (magazine != null && !boltAction)
        {
            weaponEffectsManager.SetRoundIntoWeapon(ammo, 0);
        }

        if (Weapon.IsBoltCatch && Weapon.ChamberAmmoCount == 0 && Weapon.GetCurrentMagazine() != null && Weapon.GetCurrentMagazineCount() == 0 && !Weapon.ManualBoltCatch && !boltAction)
        {
            FirearmsAnimator.SetBoltCatch(true);
            FirearmsAnimator.SetAmmoInChamber(0);
        }
    }

    private class ObservedIdleOperation : Player.FirearmController.Idling
    {
        private readonly FirearmController controller;

        public ObservedIdleOperation(FirearmController controller) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedIdleOperation>())
        {
            this.controller = controller;
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
            Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.FirearmController.Idling>(this, new object[] { controller });
        }


        public ObservedIdleOperation(System.IntPtr pointer) : base(pointer) { }


        public override void ProcessRemoveOneOffWeapon()
        {
            // 不执行任何操作
        }
    }

    private class ObservedThrowPatronOperation1 : Player.FirearmController.ReloadMultiBarrelOperation
    {
        private readonly FirearmController controller;

        public ObservedThrowPatronOperation1(FirearmController controller) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedThrowPatronOperation1>())
        {
            this.controller = controller;
            _observedController = controller.TryCast<ObservedFirearmController>();
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
            Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.FirearmController.ReloadMultiBarrelOperation>(this, new object[] { controller });
        }


        public ObservedThrowPatronOperation1(System.IntPtr pointer) : base(pointer) { }


        private readonly ObservedFirearmController _observedController;

        public override void Start(Player.FirearmController.ReloadMultiBarrelResult reloadMultiBarrelResult, Callback callback)
        {
            if (_observedController != null) if (_observedController != null) if (_observedController != null) _observedController._isThrowingPatron = true;
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call77(this, reloadMultiBarrelResult, callback);
        }
    }

    private class ObservedThrowPatronOperation2 : Player.FirearmController.ReloadSingleBarrelOperation
    {
        private readonly FirearmController controller;

        public ObservedThrowPatronOperation2(FirearmController controller) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedThrowPatronOperation2>())
        {
            this.controller = controller;
            _observedController = controller.TryCast<ObservedFirearmController>();
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
            Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.FirearmController.ReloadSingleBarrelOperation>(this, new object[] { controller });
        }


        public ObservedThrowPatronOperation2(System.IntPtr pointer) : base(pointer) { }


        private readonly ObservedFirearmController _observedController;

        public override void Start(Player.FirearmController.ReloadSingleBarrelResult reloadSingleBarrelResult, Callback callback)
        {
            if (_observedController != null) if (_observedController != null) if (_observedController != null) _observedController._isThrowingPatron = true;
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call78(this, reloadSingleBarrelResult, callback);
        }
    }

    private class ObservedThrowPatronOperation3 : Player.FirearmController.RepairMalfunction
    {
        private readonly FirearmController controller;

        public ObservedThrowPatronOperation3(FirearmController controller) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedThrowPatronOperation3>())
        {
            this.controller = controller;
            _observedController = controller.TryCast<ObservedFirearmController>();
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
            Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.FirearmController.RepairMalfunction>(this, new object[] { controller });
        }


        public ObservedThrowPatronOperation3(System.IntPtr pointer) : base(pointer) { }


        private readonly ObservedFirearmController _observedController;

        public override void Start()
        {
            if (_observedController != null) if (_observedController != null) if (_observedController != null) _observedController._isThrowingPatron = true;
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call79(this);
        }
    }

    private class ObservedThrowPatronOperation4 : Player.FirearmController.RechamberOperation
    {
        private readonly FirearmController controller;

        public ObservedThrowPatronOperation4(FirearmController controller) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedThrowPatronOperation4>())
        {
            this.controller = controller;
            _observedController = controller.TryCast<ObservedFirearmController>();
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
            Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.FirearmController.RechamberOperation>(this, new object[] { controller });
        }


        public ObservedThrowPatronOperation4(System.IntPtr pointer) : base(pointer) { }


        private readonly ObservedFirearmController _observedController;

        public override void Start(Ammo ammo, Callback callback)
        {
            if (_observedController != null) if (_observedController != null) if (_observedController != null) _observedController._isThrowingPatron = true;
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call80(this, ammo, callback);
        }
    }
}


