// © 2026 Lacyway All Rights Reserved

using System;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using EFT.NetworkPackets;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.ObservedClasses.HandsControllers;

internal sealed class ObservedMedsController : Player.MedsController
{
    public ObservedMedsController(IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.MedsController>(this);
    }

    public ObservedMedsController() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedMedsController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private FikaPlayer _fikaPlayer;
    private int _animation;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _dropRequested;
    private bool _disposed;
    private ObservedMedsOperation ObservedOperation { get; set; }

    public static ObservedMedsController Create(FikaPlayer player, Item item, OneAndList<EBodyPart> bodyParts, float amount, int animationVariant)
    {
        var controller = CreateController<ObservedMedsController>(player, item, bodyParts, amount, animationVariant);
            // OnOutUseEvent 在 47888 中是事件，无法通过赋值清除
        // controller._fikaPlayer 在 47888 中已不存在
// controller._animation 在 47888 中已不存在：controller._animation = animationVariant;
        return controller;
    }

    public override void Spawn(float animationSpeed, Il2CppSystem.Action callback)
    {
        ObservedOperation?.UnbindEvents();
        FirearmsAnimator.SetAnimationSpeed(animationSpeed);
        FirearmsAnimator.SetPointOfViewOnSpawn(EPointOfView.ThirdPerson);
        ObservedOperation = new ObservedMedsOperation(this, InitiateOperation<MedsInHandsOperation>());
        ObservedOperation.ObservedStart((System.Action)(() => callback?.Invoke()));
    }

    public override void Destroy()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        ObservedOperation?.UnbindEvents();
        NativeBaseCalls.Call81(this);
        _lifetime.Dispose();
    }

    public override void OnPlayerDead()
    {
        if (_disposed) return;
        _lifetime.Cancel();
        ObservedOperation?.UnbindEvents();
        NativeBaseCalls.Call82(this);
    }

    public override void Drop(float animationSpeed, Il2CppSystem.Action callback, bool fastDrop = false, Item nextControllerItem = null)
    {
        if (_disposed || _dropRequested || _lifetime.IsCancellationRequested) return;
        _dropRequested = true;
        NativeTask.FromManaged(MainThreadDispatcher.RunAsync(DropController)).HandleExceptions();
    }

    private async Task DropController()
    {
        var operation = ObservedOperation;
        if (operation == null) return;
        operation.RequestDestroy();
        var token = _lifetime.Token;
        try { await Task.Delay(600, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        if (_disposed || token.IsCancellationRequested || !ReferenceEquals(operation, ObservedOperation)) return;
        Destroyed = true;
        operation.HideObservedWeapon();
    }

    public override bool CanChangeCompassState(bool newState) => false;
    public override void OnCanUsePropChanged(bool canUse) { }
    public override void SetCompassState(bool active) { }

    public override void FastForwardCurrentState() => ObservedOperation?.FastForwardObserved();

    public override void FastForwardCurrentOutdatedState()
    {
        var operation = ObservedOperation;
        if (operation != null && operation.IsOutdated()) operation.FastForwardObserved();
    }

    public override void IEventsConsumerOnWeapOut() => ObservedOperation?.HideObservedWeaponComplete();

    private sealed class ObservedMedsOperation
    {
        private readonly ObservedMedsController _observedMedsController;
        private readonly MedsInHandsOperation _operation;
        private readonly System.Action<IHealthEffect> _effectRemovedHandler;
        private readonly System.Action _outUseHandler;
        private IHealthController _healthController;
        private int _animation;
        private bool _destroyRequested;

        public ObservedMedsOperation(ObservedMedsController controller, MedsInHandsOperation operation)
        {
            _observedMedsController = controller;
            _operation = operation;
            _effectRemovedHandler = (System.Action<IHealthEffect>)HealthController_EffectRemovedEvent;
            _outUseHandler = (System.Action)ObservedMedsController_OnOutUseEvent;
        }

        public void ObservedStart(System.Action callback)
        {
            _operation.State = Player.EOperationState.Executing;
            _operation.SetLeftStanceAnimOnStartOperation();
            callback?.Invoke();
            _animation = _observedMedsController._animation;
            ObservedMedsController_OnOutUseEvent();
            _observedMedsController.FirearmsAnimator.SetAnimationVariant(_animation);
            BindEvents();
        }

        private void BindEvents()
        {
            if (_healthController != null || _observedMedsController._fikaPlayer?.HealthController == null) return;
            _healthController = _observedMedsController._fikaPlayer.HealthController;
            _healthController.add_EffectRemovedEvent(_effectRemovedHandler);
            _observedMedsController.OnOutUseEvent += _outUseHandler;
        }

        public void UnbindEvents()
        {
            if (_healthController != null)
            {
                _healthController.remove_EffectRemovedEvent(_effectRemovedHandler);
                _healthController = null;
            }
            _observedMedsController.OnOutUseEvent -= _outUseHandler;
        }

        public void ObservedMedsController_OnOutUseEvent()
        {
            if (_observedMedsController.FirearmsAnimator == null) return;
            _observedMedsController.FirearmsAnimator.SetActiveParam(true, false);
            _observedMedsController.FirearmsAnimator.SetNextLimb(false);
        }

        public void HealthController_EffectRemovedEvent(IHealthEffect effect)
        {
            if (effect?.TryCast<IMedEffect>() == null || _destroyRequested) return;
            if (_observedMedsController._player.HealthController.GetBodyPartHealth(EBodyPart.Common).AtMaximum) return;
            var animator = _observedMedsController.FirearmsAnimator;
            if (animator == null) return;
            if (animator.HasNextLimb())
            {
                animator.SetActiveParam(false, false);
                animator.SetNextLimb(true);
            }
            var mult = _observedMedsController._fikaPlayer?.Skills?.SurgerySpeed?.Value / 100f ?? 0f;
            animator.SetUseTimeMultiplier(1f + mult);
            _animation++;
            var variant = 0;
            if (_observedMedsController.Item != null && _observedMedsController.Item.TryGetItemComponent(out AnimationVariantsComponent component)) variant = component.VariantsNumber;
            animator.SetAnimationVariant(variant > 0 ? (int)Mathf.Repeat(_animation, variant) : 0);
        }

        public void HideObservedWeapon()
        {
            if (_observedMedsController.FirearmsAnimator == null) return;
            _observedMedsController.FirearmsAnimator.SetNextLimb(false);
            _observedMedsController.FirearmsAnimator.SetActiveParam(false, true);
        }

        public void HideObservedWeaponComplete() => _operation.State = Player.EOperationState.Finished;
        public void FastForwardObserved() { if (_operation.State != Player.EOperationState.Finished) HideObservedWeaponComplete(); }
        public bool IsOutdated() =>
            _operation != null && NativeOperationState.IsOutdated(_operation);
        public void RequestDestroy() => _destroyRequested = true;
    }
}

internal static class NativeOperationState
{
    private static readonly System.Reflection.MethodInfo IsOutdateMethod =
        typeof(Player.ObjectInHandsOperation).GetMethod("IsOutdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

    public static bool IsOutdated(Player.ObjectInHandsOperation operation)
    {
        try
        {
            return (bool)IsOutdateMethod?.Invoke(operation, null);
        }
        catch
        {
            return false;
        }
    }
}
