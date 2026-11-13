// © 2026 Lacyway All Rights Reserved

using System;
using Diz.LanguageExtensions;
using EFT;
using Fika.Core.Main.ObservedClasses.HandsControllers;
using Fika.Core.Main.ObservedClasses.MovementStates;

namespace Fika.Core.Main.ObservedClasses;

public class ObservedMovementContext : MovementContext
{
    public ObservedMovementContext() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedMovementContext>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public ObservedMovementContext(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<MovementContext>(this);
    }


    public override bool CanJump
    {
        get
        {
            return true;
        }
    }
    public override bool CanMoveInProne
    {
        get
        {
            return true;
        }
    }
    public override bool CanProne
    {
        get
        {
            return true;
        }
    }
    public override bool CanSprint
    {
        get
        {
            return true;
        }
    }
    public override bool CanWalk
    {
        get
        {
            return true;
        }
    }
    public override Error CanInteract
    {
        get
        {
            return null;
        }
    }

    public override bool StateLocksInventory { set { } }

    public override void ApplyApproachMotion(Vector3 motion, float deltaTime)
    {
        DirectApplyMotion(motion, deltaTime);
    }

    public override void DirectApplyMotion(Vector3 motion, float deltaTime)
    {
        InputMotion = motion / deltaTime;
        var speedLimit = CharacterController.SpeedLimit;
        var hasPlatformMotion = PlatformMotion != Vector3.zero;
        if (hasPlatformMotion)
        {
            CharacterController.SpeedLimit = -1f;
        }

        CharacterController.Move(motion + PlatformMotion, deltaTime);
        if (hasPlatformMotion)
        {
            CharacterController.SpeedLimit = speedLimit;
        }
        if (Platform == null)
        {
            DampPlatformMotion(deltaTime);
        }
    }

    public override void Flash(ref Vector3 motion)
    {
        // Do nothing
    }

    public override void LimitMotionXZ(ref Vector3 motion, float deltaTime, float threshold = 0.0001F)
    {
        InputMotionBeforeLimit = motion / deltaTime;
    }

    public override void LimitProneMotion(ref Vector3 motion)
    {
        // Do nothing
    }

    public override void ApplyGravity(ref Vector3 motion, float deltaTime, bool stickToGround)
    {
        // Do nothing
    }

    public override bool CanRoll(int direction)
    {
        return true;
    }

    public override bool CanStandAt(float h)
    {
        return true;
    }

    public override bool HasGround(float depth, Il2CppSystem.Nullable<Vector3> axis = default, float extraCastLn = 0)
    {
        return true;
    }

    public override bool HeadBump(float velocity)
    {
        return false;
    }

    public override void OnControllerColliderHit(ControllerColliderHit hit)
    {
        // Do nothing
    }

    public override bool OverlapOrHasNoGround(float depth, Il2CppSystem.Nullable<Vector3> axis = default, float width = 0, float heightDivider = 4, float extraCastLn = 0)
    {
        return false;
    }

    public override void ProjectMotionToSurface(ref Vector3 motion)
    {
        // Do nothing
    }

    public override bool IsAbleToRotateProne(Vector3 motion, float deltaYaw, Quaternion predictionRotation, Transform pivot, out ECantRotate cause)
    {
        cause = ECantRotate.NotGround;
        return true;
    }

    /// <summary>
    /// (analysis/stutter-pose-reset) 值未变就不写. The native setter this overrides is unconditional:
    /// <c>MovementContext.SmoothedCharacterMovementSpeed</c> (decompiled MovementContext.cs:794-809) always calls
    /// <c>PlayerAnimatorSetCharacterMovementSpeed</c> and then <c>ProceduralWeaponAnimation.Walk.Speed</c>, and
    /// that second one is not a plain field either - <c>WalkEffector.Speed</c> (WalkEffector.cs:110-122)
    /// recomputes the walk-bob intensity of the weapon sway. ManualStateUpdate passes the snapshot speed with
    /// force on every frame and every observed player (61 on the measured client raids), i.e. it re-pushed a
    /// value the native object already held, which is exactly the 「每帧把姿态/移动写回去」 shape this round is
    /// about. The value state after the guard is identical - the field keeps the value it already had - while the
    /// animator call, the walk-intensity recompute and one interop setter per player per frame disappear.
    /// <para>
    /// The other two statements stay unconditional on purpose: <c>CharacterMovementSpeed</c> is the plain native
    /// field and <c>UpdateCovertEfficiency</c> also depends on the player's weight and skills, not only on the
    /// pushed speed, so it must keep being refreshed.
    /// </para>
    /// </summary>
    public override void SetCharacterMovementSpeed(float characterMovementSpeed, bool force = false)
    {
        CharacterMovementSpeed = characterMovementSpeed;
        if (!global::Fika.Core.Main.Utils.PoseWriteGuards.Enabled
            || SmoothedCharacterMovementSpeed != characterMovementSpeed)
        {
            SmoothedCharacterMovementSpeed = characterMovementSpeed;
        }

        UpdateCovertEfficiency(characterMovementSpeed, false);
    }

    public override void SmoothPitchLimitations(float deltaTime)
    {
        // Do nothing
    }

    public override void ProcessSpeedLimits(float deltaTime)
    {
        // Do nothing
    }

    public override void ManualUpdate(float deltaTime)
    {
        if (!_player.HealthController.IsAlive)
        {
            return;
        }
        LastDeltaTime = deltaTime;


        CheckFlying(deltaTime);
        SmoothPoseLevel(deltaTime);
        SmoothTilt(deltaTime);
    }

    public override void UpdateGroundCollision(float deltaTime)
    {
        if (!_player.IsVisible)
        {
            return;
        }
        var num = 1f;
        if (IsGrounded)
        {
            num = 0f;
            FreefallTime = deltaTime;
        }
        else
        {
            FreefallTime += deltaTime;
        }
        PlayerAnimatorSetFallingDownFloat(num);
        PlayerAnimator.SetIsGrounded(IsGrounded);
        CheckFlying(deltaTime);
    }

    public override void CheckFlying(float deltaTime)
    {
        var y = TransformPosition.y;
        if (IsGrounded)
        {
            var fallHeight = _startFallingHeight - y;
            var jumpHeight = y - _startFlyHeight;
            _startFallingHeight = y;
            if (!_previousGroundResult)
            {
                FreefallTime = 0f;
                OnGrounded?.Invoke(fallHeight, jumpHeight);
                FallHeight = fallHeight;
                JumpHeight = jumpHeight;
            }
        }
        else
        {
            FreefallTime += deltaTime;
            if (_previousGroundResult)
            {
                _startFlyHeight = y;
            }
            if (y > _startFallingHeight)
            {
                _startFallingHeight = y;
            }
        }

        _previousGroundResult = IsGrounded;
    }

    public override void WeightRelatedValuesUpdated()
    {
        PlayerAnimatorTransitionSpeed = TransitionSpeed;
        UpdateCovertEfficiency(ClampedSpeed, true);
        TiltInertia = EFTHardSettings.Instance.InertiaTiltCurve.Evaluate(_player.Physical.Inertia);
        WalkInertia = InertiaSettings.WalkInertia.Evaluate(_player.Physical.Inertia);
        SprintBrakeInertia = InertiaSettings.SprintBrakeInertia.Evaluate(_player.Physical.Inertia);
    }

    public override BaseMovementState GetNewState(EPlayerState name, bool isAI = false)
    {
        return name switch
        {
            EPlayerState.Idle => new ObservedIdleStateClass(this),
            EPlayerState.Run => new ObservedRunState(this),
            EPlayerState.ProneMove => new ObservedProneMoveStateClass(this),
            EPlayerState.Transit2Prone => new ObservedTransit2ProneStateClass(this),
            EPlayerState.Sprint => new ObservedSprintState(this),
            EPlayerState.Stationary => new ObservedStationaryState(this),
            EPlayerState.IdleWeaponMounting => new ObservedMountedState(this, _player),
            EPlayerState.Jump => new ObservedJumpState(this),
            EPlayerState.BreachDoor => new ObservedBreachDoorState(this),
            EPlayerState.DoorInteraction => new ObservedDoorInteractionState(this),
            EPlayerState.DoorInteractionZombieState => new ObservedDoorInteractionState(this),
            _ => global::Fika.Core.Main.Utils.NativeBaseCalls.Call87(this, name, isAI)
        };
    }

    public new static ObservedMovementContext Create(Player player, Func<IAnimator> animatorGetter, Func<ICharacterController> characterControllerGetter, LayerMask groundMask)
    {
        return Create<ObservedMovementContext>(player, (Il2CppSystem.Func<ICharacterController>)characterControllerGetter, groundMask);
    }

    public override void SmoothPoseLevel(float deltaTime)
    {
        var num = Math.Abs(SmoothedPoseLevel - _poseLevel);
        if (num < 1E-45f)
        {
            return;
        }
        if (num > 0.001f)
        {
            SmoothedPoseLevel = Mathf.Lerp(SmoothedPoseLevel, _poseLevel, deltaTime * EFTHardSettings.Instance.POSE_CHANGING_SPEED * TransitionSpeed);
            return;
        }
        SmoothedPoseLevel = _poseLevel;
    }

    public override void SetStationaryWeapon(Il2CppSystem.Action<Player.AbstractHandsController, Player.AbstractHandsController> callback)
    {
        StationaryHandler handler = new(this, _player, callback);
        if (_player.HandsController.Item == StationaryWeapon.Item)
        {
            handler.callback.Invoke(null, _player.HandsController);
            return;
        }
        _player.OnHandsControllerChanged += handler.HandleSwapDelegate;
    }

    public override void DropStationary(StationaryWeaponPacket.EStationaryCommand command)
    {
        if (command is StationaryWeaponPacket.EStationaryCommand.Leave)
        {
            PlayerAnimatorSetStationary(false);
            RotationAction = DefaultRotationFunction;
        }
    }

    public override void Init()
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call4(this);
        RotationAction = _player.UsedSimplifiedSkeleton ? (System.Action<Player>)SimpleRotate : (System.Action<Player>)Rotate;

        // Fix base game bug where idle animations are not playing
        PlayerAnimator.SetIsThirdPerson(true);
        PlayerAnimator.SetLayerWeight(1, 1f);
    }

    private void Rotate(Player player)
    {
        if (player.HandsController != null)
        {
            var handsRotation = Quaternion.Euler(Pitch, Yaw, 0);
            player.HandsController.ControllerGameObject.transform.SetPositionAndRotation(player.PlayerBones.Ribcage.Original.position, handsRotation);
            player.CameraContainer.transform.rotation = handsRotation;
        }
    }

    private void SimpleRotate(Player player)
    {
        // do nothing
    }

    public void ObservedStartExitingMountedState()
    {
        if (OverridenControlsState is not ObservedMountedState observedMountedState)
        {
            return;
        }
        _player.ProceduralWeaponAnimation.SetMountingData(false, false);
        observedMountedState.StartExiting();
        PlayerMountingPointData.OnStartExitMountedState -= NativeDelegates.BindManaged<System.Action>(this, nameof(StartExitingMountedState));
        var handsController = _player.HandsController;
        if (handsController == null)
        {
            return;
        }
        handsController.FirearmsAnimator.SetMounted(false);
    }

    private class StationaryHandler
    {
        private readonly MovementContext _context;
        private readonly Player _player;
        public readonly Il2CppSystem.Action<Player.AbstractHandsController, Player.AbstractHandsController> callback;
        public readonly System.Action<Player.AbstractHandsController, Player.AbstractHandsController> HandleSwapDelegate;

        public StationaryHandler(MovementContext context, Player player, Il2CppSystem.Action<Player.AbstractHandsController, Player.AbstractHandsController> callback)
        {
            _context = context;
            _player = player;
            this.callback = callback;
            HandleSwapDelegate = (System.Action<Player.AbstractHandsController, Player.AbstractHandsController>)HandleSwap;
        }

        public void HandleSwap(Player.AbstractHandsController oldController, Player.AbstractHandsController newController)
        {
            if (newController?.TryCast<ObservedFirearmController>() == null)
            {
                return;
            }

            if (newController?.TryCast<ObservedFirearmController>() is { } observedController && observedController.Item == _context.StationaryWeapon.Item)
            {
                _player.OnHandsControllerChanged -= HandleSwapDelegate;
                callback.Invoke(null, newController);
            }
        }
    }
}

