using System;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.Player.Common;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;
using static EFT.Player;

namespace Fika.Core.Main.Components;

internal sealed class ReviveInteractable : InteractableObject
{
    public ReviveInteractable(System.IntPtr pointer) : base(pointer) { }


    public ReviveInteractable() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ReviveInteractable>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private static readonly int _playerLayer = LayerMask.NameToLayer("Player");
    private static readonly int _ragdollLayer = LayerMask.NameToLayer("Deadbody");
    private static readonly int _bodyPartHitLayer = LayerMask.NameToLayer("HitCollider");

    /// <summary>
    /// 玩家当前是否正在被复活
    /// </summary>
    public bool BeingRevived { get; internal set; }

    private float ReviveTime => FikaPlugin.Instance.Settings.ReviveConfig.ReviveTime;
    private bool AllowLooting => FikaPlugin.Instance.Settings.ReviveConfig.AllowLooting;

    private ObservedPlayer _observedPlayer;
    private Action _startReviveDelegate;
    private Action<bool> _revivePlayerDelegate;
    private Action _startSearchingDelegate;
    private Callback _finishLootingDelegate;
    private GamePlayerOwner _owner;
    private FikaPlayer _localPlayer;
    private CorpseRagdoll _ragdoll;
    private System.Action<EPlayerState, EPlayerState> _stateChangedHandler;
    private System.Action<EPhysicalCondition, EPhysicalCondition> _physicalConditionHandler;
    private System.Action _preCollisionHandler;
    private bool _ragdollActive;
    private bool _reviveInProgress;

    public static ReviveInteractable Create(ObservedPlayer observedPlayer)
    {
        var component = observedPlayer.gameObject.AddComponent<ReviveInteractable>();
        component._observedPlayer = observedPlayer;
        component._startReviveDelegate = component.StartRevive;
        component._revivePlayerDelegate = component.RevivePlayer;
        component._startSearchingDelegate = component.StartSearching;
        component._finishLootingDelegate = (Callback)component.FinishLooting;
        // 这些处理器由原生代码中的 Player.Init 安装。托管
        // trampoline 具有不同的标识，无法移除那些处理器。
        component._stateChangedHandler = NativeDelegates.BindManaged<System.Action<EPlayerState, EPlayerState>>(
            observedPlayer, nameof(Player.MovementContextOnStateChanged));
        component._physicalConditionHandler = NativeDelegates.BindManaged<System.Action<EPhysicalCondition, EPhysicalCondition>>(
            observedPlayer.ProceduralWeaponAnimation,
            nameof(observedPlayer.ProceduralWeaponAnimation.PhysicalConditionUpdated));
        component._preCollisionHandler = NativeDelegates.BindManaged<System.Action>(
            observedPlayer, nameof(Player.IkStoreRaw));
        component.Init();
        return component;
    }

    private void OnDestroy()
    {
        CancelInvoke(nameof(AgonySFX));
    }

    private void Update()
    {
        if (BeingRevived && !_reviveInProgress && _observedPlayer != null &&
            _observedPlayer.CaptureRevivalState().RevivePauseRemaining <= 0)
            _observedPlayer.ToggleRevive(false, string.Empty);
    }

    private bool CanStillRevive()
    {
        if (_observedPlayer == null || !_observedPlayer.Downed) return false;
        return _observedPlayer.CaptureRevivalState().BleedoutRemaining != 0;
    }

    private void Init()
    {
        if (FikaBackendUtils.IsHeadless)
        {
            _observedPlayer.HandsController.FastForwardCurrentState();
            _observedPlayer.HandsController.OnPlayerDead();
            return;
        }

#if DEBUG
        FikaGlobals.LogInfo($"Adding ragdoll to {_observedPlayer.Profile.GetCorrectedNickname()}");
#endif

        _observedPlayer.MovementContext.ReleaseDoorIfInteractingWithOne();
        if (_stateChangedHandler != null) _observedPlayer.MovementContext.OnStateChanged -= _stateChangedHandler;
        if (_physicalConditionHandler != null) _observedPlayer.MovementContext.PhysicalConditionChanged -= _physicalConditionHandler;
        _observedPlayer.EnabledAnimators = 0;
        _observedPlayer.BodyAnimatorCommon.enabled = false;
        _observedPlayer.ArmsAnimatorCommon.enabled = false;
        _observedPlayer._characterController.isEnabled = false;
        _observedPlayer.POM.Off();
        _observedPlayer.HandsController.FastForwardCurrentState();
        _observedPlayer.HandsController.OnPlayerDead();

        if (_preCollisionHandler != null) _observedPlayer.ProceduralWeaponAnimation.OnPreCollision -= _preCollisionHandler;

        TransformTools.SetLayersRecursively(_observedPlayer.gameObject, _ragdollLayer);

        var poolObject = _observedPlayer.gameObject.GetComponent<PlayerPoolObject>();
        _ragdoll = new CorpseRagdoll
        (
            poolObject.RigidbodySpawners,
            poolObject.JointSpawners,
            poolObject.PlayerRigidbodySleepHierarchy,
            _observedPlayer.Velocity,
            EFTHardSettings.Instance.CorpseMaxDepenetrationVelocity,
            CollisionDetectionMode.Discrete,
            _observedPlayer,
            (Il2CppSystem.Func<bool, float, bool>)CheckCorpseIsStill,
            _observedPlayer.PlayerBody,
            (Il2CppSystem.Func<bool>)_observedPlayer.PlayerBody.IsVisible,
            FikaGlobals.EmptyActionDelegate,
            false,
            false
        );

        _observedPlayer.enabled = false;
        _ragdollActive = true;
        InvokeRepeating(nameof(AgonySFX), 10f, 10f);
    }

    private void AgonySFX()
    {
        if (_observedPlayer == null || _observedPlayer.Speaker == null)
        {
            return;
        }

        _observedPlayer.Speaker.Play(EPhraseTrigger.OnAgony, _observedPlayer.HealthStatus, true, new Il2CppSystem.Nullable<int>());
    }

    public void RemoveRagdoll()
    {
        if (FikaBackendUtils.IsHeadless || !_ragdollActive)
        {
            return;
        }
        _ragdollActive = false;
        CancelInvoke(nameof(AgonySFX));

#if DEBUG
        FikaGlobals.LogInfo($"Removing ragdoll from {_observedPlayer.Profile.GetCorrectedNickname()}");
#endif
        if (_stateChangedHandler != null) _observedPlayer.MovementContext.OnStateChanged += _stateChangedHandler;
        if (_physicalConditionHandler != null) _observedPlayer.MovementContext.PhysicalConditionChanged += _physicalConditionHandler;
        _observedPlayer.EnabledAnimators = EAnimatorMask.Thirdperson | EAnimatorMask.Arms | EAnimatorMask.Procedural | EAnimatorMask.FBBIK | EAnimatorMask.IK;
        _observedPlayer.BodyAnimatorCommon.enabled = true;
        _observedPlayer.ArmsAnimatorCommon.enabled = true;
        _observedPlayer._characterController.isEnabled = true;
        _observedPlayer.POM.On();

        TransformTools.SetLayersRecursively(_observedPlayer.gameObject, _playerLayer);

        for (var i = 0; i < _observedPlayer.PlayerBones.ArmorPlateColliders.Length; i++)
        {
            _observedPlayer.PlayerBones.ArmorPlateColliders[i].gameObject.layer = _bodyPartHitLayer;
        }

        for (var i = 0; i < _observedPlayer.PlayerBones.BodyPartColliders.Length; i++)
        {
            _observedPlayer.PlayerBones.BodyPartColliders[i].gameObject.layer = _bodyPartHitLayer;
        }

        foreach (var joint in _observedPlayer.gameObject.GetComponentsInChildren<CharacterJoint>())
        {
            joint.enableProjection = false;
            joint.enablePreprocessing = true;
            joint.massScale = 1f;
        }

        foreach (var rb in _observedPlayer.gameObject.GetComponentsInChildren<Rigidbody>())
        {
            PhysicsExtensions.UpdateController.UnsupportRigidbody(rb);
        }

        if (_preCollisionHandler != null) _observedPlayer.ProceduralWeaponAnimation.OnPreCollision += _preCollisionHandler;
        _observedPlayer.enabled = true;
        _observedPlayer.RecalculateEquipmentParams();
    }

    private static bool CheckCorpseIsStill(bool sleeping, float timePass)
    {
        return sleeping || timePass >= 15f;
    }

    public AvailableInteractionState GetActions(GamePlayerOwner owner)
    {
        if (BeingRevived || !CanStillRevive())
        {
            return null;
        }

        _owner = owner;
        _localPlayer = owner.Player?.TryCast<FikaPlayer>();
        if (_localPlayer == null || _localPlayer.Downed || !_localPlayer.HealthController.IsAlive) return null;

        var actions = new AvailableInteractionState();
        actions.Actions.Add(new InteractionAction
        {
            Action = _startReviveDelegate,
            Name = string.Format(LocaleUtils.UI_REVIVE_PLAYER.Localized(), _observedPlayer.Profile.GetCorrectedNickname())
        });
        if (AllowLooting)
        {
            actions.Actions.Add(new InteractionAction
            {
                Action = _startSearchingDelegate,
                Name = "Search"
            });
        }

        return actions;
    }

    private void StartSearching()
    {
        _localPlayer.SaveInteractionRayInfo();
        _localPlayer.Interact(_observedPlayer.InventoryController, _finishLootingDelegate);
    }

    private void FinishLooting(IResult result)
    {
        if (result.Failed)
        {
            return;
        }

        _owner.ShowInventoryScreenLoot(_observedPlayer.InventoryController.RootItem.Cast<CompoundItem>(), FikaGlobals.EmptyActionDelegate);
        _localPlayer.StatisticsManager.OnInteractWithLootContainer(_observedPlayer.InventoryController.RootItem);
    }

    public void StartRevive()
    {
        if (!_reviveInProgress && !BeingRevived && CanStillRevive() &&
            _localPlayer != null && !_localPlayer.Downed && _localPlayer.HealthController.IsAlive &&
            _localPlayer.CurrentState?.TryCast<IdlePlayerState>() != null)
        {
            _reviveInProgress = true;
            var reviveTime = ReviveTime;
            _owner.ShowObjectivesPanel(LocaleUtils.UI_REVIVING_PLAYER.Localized(), reviveTime);
            var nickname = _localPlayer.Profile.GetCorrectedNickname();
            _observedPlayer.ToggleRevive(true, nickname);

            _observedPlayer.CommonPacket.Type = ECommonSubPacketType.RevivingPlayer;
            _observedPlayer.CommonPacket.SubPacket = RevivingPlayerPacket.FromValue(true, nickname);
            _observedPlayer.PacketSender.NetworkManager.SendNetReusable(ref _observedPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
            try { _localPlayer.CurrentManagedState.Plant(true, false, reviveTime, _revivePlayerDelegate); }
            catch
            {
                RevivePlayer(false);
                throw;
            }
        }
    }

    public void RevivePlayer(bool success)
    {
        if (!_reviveInProgress) return;
        _reviveInProgress = false;
        _owner?.CloseObjectivesPanel();
        if (_observedPlayer == null || !_observedPlayer.Downed) return;
        _observedPlayer.ToggleRevive(false, string.Empty);

        if (!success || !CanStillRevive() || _localPlayer == null || _localPlayer.Downed || !_localPlayer.HealthController.IsAlive)
        {
            _observedPlayer.CommonPacket.Type = ECommonSubPacketType.RevivingPlayer;
            _observedPlayer.CommonPacket.SubPacket = RevivingPlayerPacket.FromValue(false, string.Empty);
            _observedPlayer.PacketSender.NetworkManager.SendNetReusable(ref _observedPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
            return;
        }

        if (_localPlayer != null)
        {
            if (_observedPlayer != null)
            {
                _observedPlayer.CommonPacket.Type = ECommonSubPacketType.RevivedPlayer;
                _observedPlayer.CommonPacket.SubPacket = RevivedPlayerPacket.FromValue();
                _observedPlayer.PacketSender.NetworkManager.SendNetReusable(ref _observedPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);

#if DEBUG
                FikaGlobals.LogInfo($"Reviving {_observedPlayer.NetId}");
#endif
                _observedPlayer.AcceptRevival();
                _localPlayer.InteractableObject = null;
                _localPlayer.ForceInteractionsChanged();
                return;
            }

            FikaGlobals.LogError("ObservedPlayer was null!");
            return;
        }

        FikaGlobals.LogError("_localPlayer was null!");
    }
}
