using System;
using EFT;

namespace Fika.Core.Main.ObservedClasses.MovementStates;

public class ObservedStationaryState : StationaryPlayerState
{
    private readonly MovementContext movementContext;

    public ObservedStationaryState(MovementContext movementContext) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedStationaryState>())
    {
        this.movementContext = movementContext;
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<StationaryPlayerState>(this, new object[] { movementContext });
    }


    public ObservedStationaryState(System.IntPtr pointer) : base(pointer) { }


    public override bool OutOfOperationRange
    {
        get
        {
            return false;
        }
    }

    public override void Enter(bool isFromSameState)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call86(this, isFromSameState);
        StationaryWeapon = MovementContext.StationaryWeapon;
        StationaryWeapon.ObserverMagazineAmmoCount = StationaryWeapon.GetMagazineCount();
        if (OutOfOperationRange)
        {
            MovementContext.DropStationary(StationaryWeaponPacket.EStationaryCommand.Denied);
            return;
        }
        _denied = false;
        _exitRotation = new Vector2(StationaryWeapon.Yaw, StationaryWeapon.Pitch);
        _outTime = 0.5f;
        if (isFromSameState)
        {
            return;
        }
        Stage = EStationaryStage.In;
        MovementContext.StateLocksInventory = true;
        MovementContext.SetRotationLimit(MovementContext.StationaryWeapon.YawLimit, MovementContext.StationaryWeapon.PitchLimit);
        MovementContext.SetStationaryWeapon(new Action<Player.AbstractHandsController, Player.AbstractHandsController>(SetStationaryCallback));
        MovementContext.LeftStanceController.DisableLeftStanceAnimFromBodyAction();
    }

    public void SetStationaryCallback(Player.AbstractHandsController arg1, Player.AbstractHandsController newContoller)
    {
        MovementContext.SetStationaryStrategy();
        MovementContext.StateLocksInventory = false;
        _weaponTransform = newContoller.HandsHierarchy.GetTransform(ECharacterWeaponBones.weapon);
        StationaryWeapon.SetPivots(newContoller.HandsHierarchy);
        StationaryWeapon.Hide(MovementContext.IsAI);
        MovementContext.RotationAction = StationaryWeapon.Animation == EFT.Interactive.StationaryWeapon.EStationaryAnimationType.AGS_17
            ? MovementContext.AGSRotationFunction : MovementContext.UtesRotationFunction;
        Stage = EStationaryStage.Main;
        var player = MovementContext._player;
        var changed = NativeDelegates.Bind<Il2CppSystem.Action<Player.AbstractHandsController, Player.AbstractHandsController>>(this, nameof(OnHandsControllerChanged));
        var changing = NativeDelegates.Bind<Il2CppSystem.Action>(this, nameof(MovementContextOnHandsChangedEvent));
        player.OnHandsControllerChanged += changed;
        player.HandsChangingEvent += changing;
        _handsChangingEventUnsubscribe = (Il2CppSystem.Action)(() =>
        {
            player.OnHandsControllerChanged -= changed;
            player.HandsChangingEvent -= changing;
        });
    }
}
