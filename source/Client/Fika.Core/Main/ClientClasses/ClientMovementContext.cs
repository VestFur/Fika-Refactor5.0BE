using System;
using EFT;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.ClientClasses;

public class ClientMovementContext : MovementContext
{
    public ClientMovementContext() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ClientMovementContext>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public ClientMovementContext(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<MovementContext>(this);
    }


    private bool _doGravity;

    public new static ClientMovementContext Create(Player player, Func<IAnimator> animatorGetter, Func<ICharacterController> characterControllerGetter, LayerMask groundMask)
    {
        var movementContext = Create<ClientMovementContext>(player, (Il2CppSystem.Func<ICharacterController>)characterControllerGetter, groundMask);
        return movementContext;
    }

    public override void Init()
    {
        _doGravity = true;
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call4(this);
        if (!_player.IsYourPlayer && _player.IsAI && !FikaBackendUtils.IsHeadless)
        {
            // 修复基础游戏中待机动画不播放的 bug
            PlayerAnimator.SetIsThirdPerson(true);
            PlayerAnimator.SetLayerWeight(1, 1f);
        }
    }

    public override void ApplyGravity(ref Vector3 motion, float deltaTime, bool stickToGround)
    {
        if (!_doGravity)
        {
            return;
        }

        global::Fika.Core.Main.Utils.NativeBaseCalls.Call5(this, ref motion, deltaTime, stickToGround);
    }

    public void SetGravity(bool enabled)
    {
        _doGravity = enabled;
    }
}

