// © 2026 Lacyway All Rights Reserved

using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using Fika.Core.Main.Players;

namespace Fika.Core.Main.ObservedClasses.HandsControllers;

internal sealed class ObservedGrenadeController : Player.GrenadeHandsController
{
    public ObservedGrenadeController(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.GrenadeHandsController>(this);
    }


    public ObservedGrenadeController() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedGrenadeController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private ObservedPlayer _observedPlayer;

    public static ObservedGrenadeController Create(ObservedPlayer observedPlayer, ThrowWeap item)
    {
        var controller = CreateController<ObservedGrenadeController>(observedPlayer, item);
        controller._observedPlayer = observedPlayer;
        return controller;
    }

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
        // 不做任何事
    }

    public override void SetCompassState(bool active)
    {
        // 不做任何事
    }

    public override void CompassStateHandler(bool isActive)
    {
        /*_observedPlayer.CreateObservedCompass();
        _objectInHandsAnimator.ShowCompass(isActive);
        _observedPlayer.SetPropVisibility(isActive);*/
    }

    /// <summary>
    /// 生成手雷的原始方法，我们改用 <see cref="SpawnGrenade(float, Vector3, Quaternion, Vector3, bool)"/>
    /// </summary>
    /// <param name="timeSinceSafetyLevelRemoved"></param>
    /// <param name="position"></param>
    /// <param name="rotation"></param>
    /// <param name="force"></param>
    /// <param name="lowThrow"></param>
    public override void ThrowGrenade(float timeSinceSafetyLevelRemoved, Vector3 position, Quaternion rotation, Vector3 force, bool lowThrow)
    {
        // 不做任何事，我们使用自己的方法
    }

    /// <summary>
    /// 生成一枚手雷，使用来自 <see cref="SubPackets.GrenadePacket"/> 的数据
    /// </summary>
    /// <param name="timeSinceSafetyLevelRemoved">解除保险以来经过的时间，使用 0f</param>
    /// <param name="position">起始的 <see cref="Vector3"/> 位置</param>
    /// <param name="rotation">手雷的 <see cref="Quaternion"/> 旋转</param>
    /// <param name="force">手雷的 <see cref="Vector3"/> 作用力</param>
    /// <param name="lowThrow">是否为低抛</param>
    public void SpawnGrenade(float timeSinceSafetyLevelRemoved, Vector3 position, Quaternion rotation, Vector3 force, bool lowThrow)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call57(this, timeSinceSafetyLevelRemoved, position, rotation, force, lowThrow);
    }
}

/// <summary>
/// 原生 sealed 绊线操作的仅远端行为。钩子保留
/// 其原生类型，这样游戏的操作工厂与隐藏／重置路径仍然可用。
/// </summary>
internal sealed class ObservedTripwireState(Player.GrenadeHandsController.PlantTripwireOperation operation)
{
    private Player.GrenadeHandsController Controller => operation.Controller;

    private void Finish()
    {
        // 远端客户端从不拥有库存变更或原生
        // 绊线生成。它仍然必须完成该原生操作，这样在复制的
        // 动画到达结尾之后，手部控制器才能被干净地
        // 替换。
        operation.State = Player.EOperationState.Finished;
    }

    public void Start()
    {
        Controller.FirearmsAnimator.SetFireMode(Weapon.EFireMode.greanadePlanting, false);
        operation.PlantOperationState = Player.GrenadeHandsController.PlantTripwireOperation.EPlantOperationState.StateIn;
        operation.State = Player.EOperationState.Executing;
        operation.SetLeftStanceAnimOnStartOperation();
    }

    public void OnIdleStartAction()
    {
        operation.PlantOperationState = Player.GrenadeHandsController.PlantTripwireOperation.EPlantOperationState.Idling;
    }

    public void Execute(Callback callback)
    {
        callback.Succeed();
    }

    public void End() => Finish();

    public void DropFinished() => Finish();

    public void FastForward() => Finish();

    public void PlantTripwire()
    {
        operation.PlantOperationState = Player.GrenadeHandsController.PlantTripwireOperation.EPlantOperationState.Planting;
        Controller.FirearmsAnimator.SetGrenadeFire(FirearmsAnimator.EGrenadeFire.Throw);
    }
}
