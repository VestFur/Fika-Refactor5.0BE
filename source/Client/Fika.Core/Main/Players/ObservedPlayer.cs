// © 2026 Lacyway All Rights Reserved

using EFT.CameraControl;
using EFT.Communications;
using EFT.Dialogs;
using EFT.GlobalEvents;
using EFT.HealthSystem;
using EFT.NetworkPackets;
using EFT.NextObservedPlayer;
using EFT.Settings;
using EFT.Settings.Sound;
using System;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Audio.SpatialSystem;
using Comfort.Common;
using Dissonance;
using EFT;
using EFT.AssetsManager;
using EFT.Ballistics;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.Vaulting;
using Fika.Core.Main.Components;
using Fika.Core.Main.Factories;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.ObservedClasses;
using Fika.Core.Main.ObservedClasses.HandsControllers;
using Fika.Core.Main.PacketHandlers;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Communication;
using Fika.Core.Networking.Packets.Player;
using Fika.Core.Networking.Packets.Player.Common;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;
using Fika.Core.Networking.Snapshotting;
using HarmonyLib;
using JsonType;
using RootMotion.FinalIK;
using static Fika.Core.UI.FikaUIGlobals;

namespace Fika.Core.Main.Players;

/// <summary>
/// Observed players are any other players in the world for a client, including bots. <br/>
/// Bots are handled by the server, and other clients send their own data which the server replicates to other clients. <br/>
/// As a host all <see cref="ObservedPlayer"/>s are only other clients.
/// </summary>
public sealed class ObservedPlayer : FikaPlayer
{
    public ObservedPlayer(System.IntPtr pointer) : base(pointer) { }


    public ObservedPlayer() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedPlayer>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    #region Fields and Properties
    public FikaHealthBar HealthBar
    {
        get
        {
            return _healthBar;
        }
    }

    public bool ShouldOverlap { get; internal set; }

    public override bool LeftStanceDisabled
    {
        get
        {
            return _leftStancedDisabled;
        }
        internal set
        {
            if (_leftStancedDisabled == value)
            {
                return;
            }
            _leftStancedDisabled = value;
            ShouldOverlap = true;
        }
    }

    public BetterSource VoipEftSource { get; set; }

    public ObservedHealthController NetworkHealthController
    {
        get
        {
            return HealthController.TryCast<ObservedHealthController>();
        }
    }

    public override bool CanBeSnapped
    {
        get
        {
            return false;
        }
    }

    public override EPointOfView PointOfView
    {
        get
        {
            return EPointOfView.ThirdPerson;
        }
        set
        {
            if (_playerBody.PointOfView.Value == value)
            {
                return;
            }
            _playerBody.PointOfView.Value = value;
            CalculateScaleValueByFov((float)Singleton<SettingsManager>.Instance.Game.Settings.FieldOfView);
            SetCompensationScale(false);
            PlayerBones.Ribcage.Original.localScale = new Vector3(1f, 1f, 1f);
            MovementContext.PlayerAnimatorPointOfView(value);
            var pointOfViewChanged = PointOfViewChanged;
            pointOfViewChanged?.Invoke();
            _playerBody.UpdatePlayerRenders(_playerBody.PointOfView.Value, Side);
            ProceduralWeaponAnimation.PointOfView = value;
        }
    }

    public override AbstractHandsController HandsController
    {
        get
        {
            return global::Fika.Core.Main.Utils.NativeBaseCalls.Call112(this);
        }

        set
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call113(this, value);
            var weaponAnimationType = GetWeaponAnimationType(_handsController);
            MovementContext.PlayerAnimatorSetWeaponId(weaponAnimationType);
        }
    }

    public override Ray InteractionRay
    {
        get
        {
            var vector = HandsRotation * Vector3.forward;
            return new(_playerLookRaycastTransform.position, vector);
        }
    }

    public override float ProtagonistHearing
    {
        get
        {
            return Mathf.Max(1f, Singleton<BetterAudio>.Instance.ProtagonistHearing + 1f);
        }
    }

    public override bool IsVisible
    {
        get
        {
            if (FikaBackendUtils.IsHeadless)
            {
                return true;
            }
            return _followerCullingObject != null && _followerCullingObject.IsVisible;
        }

        set
        {

        }
    }

    public ImpostorCharacterController ObservedCharacterController
    {
        get
        {
            return MovementContext.ImpostorCC;
        }
    }

    public float TurnOffFbbikAt;

    internal ObservedState CurrentPlayerState;
    private float _lastDistance;
    private OfflinePlayerCulling _cullingHandler;
    private float _rightHand;
    private float _leftHand;
    private LimbIK[] _observedLimbs;
    private Transform[] _observedMarkers;
    private bool _shouldCullController;
    private readonly List<ObservedSlotViewHandler> _observedSlotViewHandlers = [];
    private ObservedCorpseCulling _observedCorpseCulling;
    private bool _compassLoaded;
    private FollowerCullingObject _followerCullingObject;
    private readonly ObservedVaultingParameters _observedVaultingParameters = new();
    private bool _leftStancedDisabled;
    private FikaHealthBar _healthBar;
    private Coroutine _waitForStartRoutine;
    private bool _isServer;
    private VoiceBroadcastTrigger _voiceBroadcastTrigger;
    private SoundSettingsGroup _soundSettings;
    private ReviveInteractable _reviveInteractable;
    private bool _voipAssigned;
    private int _frameSkip;
    private bool _isZombie;
    private Corpse _fikaFinalisedCorpse;

    // (analysis/stutter-pose-reset) The animator flags this player was last given. The three Enable* calls in
    // ManualStateUpdate used to run on every grounded frame and every player, while the native jump state only
    // ever calls them on the frame the value changes (JumpStateClass.cs:108/148/200). They are booleans in the
    // animator, so pushing the same value again is a no-op for the animation - it is pure native call cost
    // (61 players x 3 calls x every frame). These caches turn the calls into edges; they are invalidated
    // whenever the player changes state, because another writer (ObservedMountedState) moves the same flags.
    private bool _pushedJumpFlag;
    private bool _pushedJumpFlagValid;
    private bool _pushedLandingFlag;
    private bool _pushedLandingFlagValid;
    private bool _pushedInertFlag;
    private bool _pushedInertFlagValid;
    private EPlayerState _pushedFlagState;
    private bool _pushedFlagStateValid;

    private const float _movementDeadZoneSqr = 0.05f * 0.05f;
    private const float _velocityDeadZoneSqr = 0.20f * 0.20f;
    #endregion

    public static async Task<ObservedPlayer> CreateObservedPlayer(GameWorld gameWorld, int playerId, Vector3 position, Quaternion rotation, string layerName,
        string prefix, EPointOfView pointOfView, Profile profile, byte[] healthBytes, bool aiControl,
        EUpdateQueue updateQueue, EUpdateMode armsUpdateMode, EUpdateMode bodyUpdateMode,
        CharacterControllerSpawner.Mode characterControllerMode, Func<float> getSensitivity, Func<float> getAimingSensitivity,
        ICustomizationFilter filter, MongoID firstId, ushort firstOperationId, bool isZombie)
    {
        var useSimpleAnimator = isZombie;
#if DEBUG
        if (useSimpleAnimator)
        {
            FikaGlobals.LogWarning("Using SimpleAnimator!");
        }
#endif
        var resourceKey = useSimpleAnimator ? InGameBundles.ZOMBIE_BUNDLE_NAME : InGameBundles.PLAYER_BUNDLE_NAME;
        var player = Create<ObservedPlayer>(gameWorld, resourceKey, playerId, position, updateQueue,
            armsUpdateMode, bodyUpdateMode, characterControllerMode, getSensitivity, getAimingSensitivity, prefix, aiControl, useSimpleAnimator);

        player.IsYourPlayer = false;
        player.ResetDisposeState();
        player.IsObservedAI = aiControl;
        player.CommonPacket = new()
        {
            NetId = playerId
        };

        ObservedInventoryController inventoryController = new(player, profile, true, firstId, firstOperationId, aiControl);
        ObservedHealthController healthController = new(healthBytes, player, inventoryController, profile.Skills);

        ObservedStatisticsManager statisticsManager = new();
        ObservedQuestController observedQuestController = null;
        ObservedDialogController dialogueController = null;
        if (!aiControl)
        {
            observedQuestController = new(profile, inventoryController, inventoryController.PlayerSearchController, null);
            player.FikaQuests = observedQuestController;
            observedQuestController.Init();
            observedQuestController.Run();

            dialogueController = new(profile, observedQuestController.Native, inventoryController);
        }

        player.VoipState = (!FikaBackendUtils.IsHeadless && !aiControl && FikaSingleton<IFikaNetworkManager>.Instance.AllowVOIP)
            ? EVoipState.Available : EVoipState.NotAvailable;

        await player.Init(rotation, layerName, pointOfView, playerId, profile, inventoryController, healthController,
            statisticsManager, observedQuestController?.Native, null, null, null, dialogueController, filter, player.VoipState, aiControl, false, null).AsManaged();

        player.DisposeObservers();

        player.Pedometer.Stop();
        player._handsController = EmptyHandsController.CreateController<EmptyHandsController>(player);
        player._handsController.Spawn(1f, (System.Action)FikaGlobals.EmptyAction);

        player.AIData = new AIData(null, player);
        player.botPlayerCulling = new();
        player._cullingHandler = player.botPlayerCulling;
        player._cullingHandler.Initialize(player, player.PlayerBones);

        if (FikaBackendUtils.IsHeadless || profile.IsPlayerProfile())
        {
            player._cullingHandler.Disable();
        }

        if (FikaBackendUtils.IsHeadless)
        {
            player.EnabledAnimators = EAnimatorMask.Thirdperson | EAnimatorMask.Arms;
        }

        if (!aiControl)
        {
            var services = player._notYetPurchasedTraderServiceTypes;
            foreach (var etraderServiceType in Singleton<GlobalConfiguration>.Instance.ServicesData.Keys)
            {
                services.Add(etraderServiceType);
            }
        }

        player._observedLimbs = player.GetComponent<PlayerPoolObject>().LimbIks;
        player._observedMarkers = player._markers;

        player.AggressorFound = false;
        player._animators[0].enabled = true;
        player._isServer = FikaBackendUtils.IsServer;
        player.Snapshotter = new PlayerSnapshotter<PlayerStateSnapshot>();
        player.CurrentPlayerState = new ObservedState(position, player.Rotation);
        player._isZombie = player.UsedSimplifiedSkeleton;

        if (ObservedPlayerController._evenOrNotEvenUpdateLastValue == 0)
        {
            ObservedPlayerController._evenOrNotEvenUpdateLastValue = 1;
            player._frameSkip = 1;
        }
        else
        {
            ObservedPlayerController._evenOrNotEvenUpdateLastValue = 0;
            player._frameSkip = 0;
        }

        CameraManager.Instance.FoVUpdateAction -= (System.Action<int>)player.OnFovUpdatedEvent;

        if (!FikaBackendUtils.IsHeadless)
        {
            player._followerCullingObject = player.gameObject.AddComponent<FollowerCullingObject>();
            player._followerCullingObject.enabled = true;
            player._followerCullingObject.CullByDistanceOnly = false;
            player._followerCullingObject.Init((Il2CppSystem.Func<Transform>)player.GetCullingTransform);
            player._followerCullingObject.SetParams(EFTHardSettings.Instance.CULLING_PLAYER_SPHERE_RADIUS,
                EFTHardSettings.Instance.CULLING_PLAYER_SPHERE_SHIFT, EFTHardSettings.Instance.CULLING_PLAYER_DISTANCE);
        }

        player.SubscribeToArmorChangeEvent();
        player.RecalculateEquippedArmorComponents(null);
        player.NetId = playerId;

        OnPlayerSpawned?.Invoke(player);

        return player;
    }

    private Transform GetCullingTransform()
    {
        return PlayerBones.BodyTransform.Original;
    }

    /// <summary>
    /// These are redundant on observed players
    /// </summary>
    private void DisposeObservers()
    {
        NightVisionObserver.Dispose();
        ThermalVisionObserver.Dispose();
        FaceCoverShieldObserver.Dispose();
        FaceCoverMaskObserver.Dispose();
    }

    public override void InitVoip(EVoipState voipState)
    {
        if (voipState == EVoipState.Available)
        {
            FikaGlobals.LogInfo($"Initializing VOIP for {Profile.Nickname}");
            SetupVoiceBroadcastTrigger();
            DissonanceComms = DissonanceComms.Instance;
            if (DissonanceComms != null)
            {
                DissonanceComms.TrackPlayerPosition(this);
                StartCoroutine(EngineCoroutines.Wrap(SourceBindingCreated(), "ObservedPlayer.SourceBindingCreated"));
                if (VoipAudioSource != null)
                {

                }
                else
                {
                    FikaGlobals.LogError($"VoipAudioSource was null when attempting to initialize VOIP for {Profile.Nickname}");
                }
            }
            else
            {
                FikaGlobals.LogError($"DissonanceComms was null when attempting to initialize VOIP for {Profile.Nickname}");
            }
        }
    }

    private IEnumerator SourceBindingCreated()
    {
        if (_voipAssigned)
        {
            yield break;
        }

        if (VoipAudioSource == null)
        {
            var attempts = 0;
            var waitForSeconds = new WaitSeconds(1f);

            while (VoipAudioSource == null)
            {
                FikaGlobals.LogInfo($"VoipAudioSource is null, waiting 1 second... [Attempt {attempts + 1}]");

                if (attempts >= 5)
                {
                    FikaGlobals.LogError("VoipAudioSource was null after 5 attempts! Cancelling.");
                    yield break;
                }

                attempts++;
                yield return waitForSeconds;
            }
        }

        VoipEftSource = MonoBehaviourSingleton<BetterAudio>.Instance.CreateBetterSource<SimpleSource>(
            VoipAudioSource, BetterAudio.AudioSourceGroupType.Voip, true, true);
        if (VoipEftSource == null)
        {
            FikaGlobals.LogError($"Could not initialize VoipEftSource for {Profile.Nickname}");
            yield break;
        }
        VoipEftSource.SetMixerGroup(MonoBehaviourSingleton<BetterAudio>.Instance.ObservedPlayerSpeechMixer);
        VoipEftSource.SetRolloff(60f);
        MonoBehaviourSingleton<SpatialAudioSystem>.Instance.ProcessSourceOcclusion(this, VoipEftSource, false);
        _voipAssigned = true;
    }

    private void SetupVoiceBroadcastTrigger()
    {
        _voiceBroadcastTrigger = gameObject.AddComponent<VoiceBroadcastTrigger>();
        _voiceBroadcastTrigger.ChannelType = CommTriggerTarget.Self;
        _soundSettings = Singleton<SettingsManager>.Instance.Sound.Settings;
        CompositeDisposable.BindState(_soundSettings.VoiceChatVolume, (System.Action<int>)ChangeVoipDeviceSensitivity);
    }

    private void ChangeVoipDeviceSensitivity(int value)
    {
        var num = (float)value / 100f;
        _voiceBroadcastTrigger.ActivationFader.Volume = num;
    }

    public override PhysicalBase CreatePhysical()
    {
        return new PhysicalBase();
    }

    public override void Say(EPhraseTrigger phrase, bool demand = false, float delay = 0, ETagStatus mask = 0, int probability = 100, bool aggressive = false, ETagFilter tagFilter = 0)
    {
        if (gameObject.activeSelf)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call114(this, phrase, demand, delay, mask, probability, aggressive, tagFilter);
        }
    }

    public override void GroundedStateHandler(float fallHeight, float jumpHeight)
    {
        var surface = NativeValueTuple<bool, BaseBallistic.ESurfaceSound>.Read(CalculateMovementSurface());
        UpdateSurfaceData(surface.Item1, surface.Item2);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call115(this, fallHeight, jumpHeight);
    }

    public override void OnSkillLevelChanged(BaseSkill skill)
    {
        // Do nothing
    }

    public override void OnWeaponMastered(Mastering masterSkill)
    {
        // Do nothing
    }

    public override void StartInflictSelfDamageCoroutine()
    {
        // Do nothing
    }

    public override void AddStateSpeedLimit(float speedDelta, ESpeedLimit cause)
    {
        // Do nothing
    }

    public override void UpdateSpeedLimit(float speedDelta, ESpeedLimit cause)
    {
        // Do nothing
    }

    public override void UpdateSpeedLimitByHealth()
    {
        // Do nothing
    }

    public override void UpdateSpeedLimit(float speedDelta, ESpeedLimit cause, float duration)
    {
        // Do nothing
    }

    public override void UpdatePhones()
    {
        // Do nothing
    }

    public override void FaceshieldMarkOperation(FaceShieldComponent armor, bool hasServerOrigin)
    {
        // Do nothing
    }

    public override void ShotReactions(DamageInfo shot, EBodyPart bodyPart)
    {
        TurnOffFbbikAt = Time.time + 0.6f;
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call116(this, shot, bodyPart);
    }

    public override void ManageAggressor(DamageInfo DamageInfo, EBodyPart bodyPart, EBodyPartColliderType colliderType)
    {
        if (_isDeadAlready)
        {
            return;
        }
        if (!HealthController.IsAlive)
        {
            _isDeadAlready = true;
        }
        if (DamageInfo.Player == null)
        {
            return;
        }
        var player = Singleton<GameWorld>.Instance.GetAlivePlayerByProfileID(DamageInfo.Player.iPlayer.ProfileId);
        if (player == this)
        {
            return;
        }
        if (DamageInfo.Weapon != null)
        {
            player.ExecuteShotSkill(DamageInfo.Weapon);
        }

        if (player.IsYourPlayer)
        {
            // Check for GClass increment
            var flag = DamageInfo.DidBodyDamage / HealthController.GetBodyPartHealth(bodyPart, false).Maximum >= 0.6f && HealthController.FindExistingEffect<IBleeding>(bodyPart) != null;
            player.StatisticsManager.OnEnemyDamage(DamageInfo, bodyPart, ProfileId, Side, Profile.Info.Settings.Role,
                GroupId, HealthController.GetBodyPartHealth(EBodyPart.Common, false).Maximum, flag,
                Vector3.Distance(player.Transform.position, Transform.position), CurrentHour,
                Inventory.EquippedInSlotsTemplateIds, HealthController.BodyPartEffects, TriggerZones);
        }
    }

    public override void UpdateArmsCondition()
    {
        // Do nothing
    }

    public override bool ShouldVocalizeDeath(EBodyPart bodyPart)
    {
        return bodyPart > EBodyPart.Head;
    }

    public override void SendHeadlightsPacket(bool isSilent)
    {
        // Do nothing
    }

    public override void ApplyHitDebuff(float damage, float staminaBurnRate, EBodyPart bodyPartType, EDamageType damageType)
    {
        if (damageType.IsEnemyDamage())
        {
            IncreaseAwareness(20f);
        }
        if (HealthController.IsAlive && (!MovementContext.PhysicalConditionIs(EPhysicalCondition.OnPainkillers) || damage > 4f) && !IsAI)
        {
            if (gameObject.activeSelf && Speaker != null)
            {
                Speaker.Play(EPhraseTrigger.OnBeingHurt, HealthStatus, true, new Il2CppSystem.Nullable<int>());
            }
        }
    }

    public void HandleExplosive(DamageInfo DamageInfo, EBodyPart bodyPartType, EBodyPartColliderType colliderType)
    {
        if (HealthController.DamageCoeff == 0)
        {
            return;
        }

        LastDamagedBodyPart = bodyPartType;
        LastBodyPart = bodyPartType;
        LastDamageInfo = DamageInfo;
        LastDamageType = DamageInfo.DamageType;

        CommonPacket.Type = ECommonSubPacketType.Damage;
        CommonPacket.SubPacket = DamagePacket.FromValue(NetId, DamageInfo, bodyPartType, colliderType);
        FikaSingleton<IFikaNetworkManager>.Instance.SendNetReusable(ref CommonPacket, DeliveryMethod.ReliableOrdered, true);
    }

    public override void ApplyDamageInfo(DamageInfo DamageInfo, EBodyPart bodyPartType, EBodyPartColliderType colliderType, float absorbed)
    {
        LastAggressor = DamageInfo.Player.iPlayer;
        LastDamagedBodyPart = bodyPartType;
        LastBodyPart = bodyPartType;
        LastDamageInfo = DamageInfo;
        LastDamageType = DamageInfo.DamageType;
    }

    public PlayerHitInfo HandleSniperShot(DamageInfo DamageInfo, EBodyPart bodyPartType, EBodyPartColliderType colliderType, EArmorPlateCollider armorPlateCollider, ShotId shotId)
    {
        if (HealthController.DamageCoeff == 0)
        {
            return null;
        }

        ApplyHitDebuff(DamageInfo.Damage, 0f, bodyPartType, DamageInfo.DamageType);
        LastDamagedBodyPart = bodyPartType;
        LastBodyPart = bodyPartType;
        LastDamageInfo = DamageInfo;
        LastDamageType = DamageInfo.DamageType;

        CommonPacket.Type = ECommonSubPacketType.Damage;
        CommonPacket.SubPacket = DamagePacket.FromValue(NetId, DamageInfo, bodyPartType, colliderType, armorPlateCollider);
        FikaSingleton<IFikaNetworkManager>.Instance.SendNetReusable(ref CommonPacket, DeliveryMethod.ReliableOrdered, true);

        return new()
        {
            PoV = EPointOfView.ThirdPerson,
            Penetrated = DamageInfo.Penetrated,
            Material = MaterialType.Body
        };
    }

    public override PlayerHitInfo ApplyShot(DamageInfo damageInfo, EBodyPart bodyPartType, EBodyPartColliderType colliderType, EArmorPlateCollider armorPlateCollider, ShotId shotId)
    {
        if (HealthController != null && !HealthController.IsAlive)
        {
            return null;
        }

        ShotReactions(damageInfo, bodyPartType);
        ApplyHitDebuff(damageInfo.Damage, 0f, bodyPartType, damageInfo.DamageType);
        var flag = NativeNullable<MongoID>.Read(damageInfo, "DeflectedBy").HasValue;
        var damage = damageInfo.Damage;
        var list = ProceedDamageThroughArmor(ref damageInfo, colliderType, armorPlateCollider, true);
        var materialType = flag ? MaterialType.HelmetRicochet : ((list == null || list.Count < 1)
            ? MaterialType.Body : list[0].Material);
        PlayerHitInfo hitInfo = new()
        {
            PoV = PointOfView,
            Penetrated = damageInfo.Penetrated,
            Material = materialType
        };
        var num = damage - damageInfo.Damage;
        if (num > 0)
        {
            damageInfo.DidArmorDamage = num;
        }
        damageInfo.DidBodyDamage = damageInfo.Damage;
        ReceiveDamage(damageInfo.Damage, bodyPartType, damageInfo.DamageType, num, hitInfo.Material);

        CommonPacket.Type = ECommonSubPacketType.Damage;
        CommonPacket.SubPacket = DamagePacket.FromValue(NetId, damageInfo, bodyPartType, colliderType, armorPlateCollider, absorbed: num);
        FikaSingleton<IFikaNetworkManager>.Instance.SendNetReusable(ref CommonPacket, DeliveryMethod.ReliableOrdered, true);

        // Run this to get weapon skill
        ManageAggressor(damageInfo, bodyPartType, colliderType);

        // [HitMarker] The shooter's machine never runs ActiveHealthController.ApplyDamage for this
        // hit (the machine that owns the victim does), so re-issue it locally with zero damage.
        ReportLocalHit(damageInfo, bodyPartType);

        return hitInfo;
    }

    /// <summary>
    /// Hands a hit that the local player just registered on this observed player to
    /// <see cref="LocalHitFeedback"/>, which fires the local hit-feedback hook without applying
    /// any damage. Calls from another machine's perspective are ignored.
    /// </summary>
    private void ReportLocalHit(DamageInfo damageInfo, EBodyPart bodyPartType)
    {
        var source = damageInfo.Player?.iPlayer;
        if (source == null || !source.IsYourPlayer)
        {
            return;
        }

        LocalHitFeedback.Report(source.TryCast<Player>(), this, bodyPartType, damageInfo.Damage, damageInfo.DamageType);
    }

    public override void OnItemAddedOrRemoved(Item item, ItemAddress location, bool added)
    {
        // Do nothing
    }

    public override void ApplyExplosionDamageToArmor(Il2CppSystem.Collections.Generic.Dictionary<ExplosionDamageInfo, float> armorDamage, DamageInfo DamageInfo)
    {
        if (_isServer)
        {
            foreach (var armorComponent in _preAllocatedArmorComponents)
            {
                var num = 0f;
                foreach (var keyValuePair in armorDamage)
                {
                    if (armorComponent.ShotMatches(keyValuePair.Key.BodyPartColliderType, keyValuePair.Key.ArmorPlateCollider))
                    {
                        num += keyValuePair.Value;
                    }
                }
                if (num > 0f)
                {
                    num = armorComponent.ApplyExplosionDurabilityDamage(num, DamageInfo, _preAllocatedArmorComponents);
                    OnArmorDamaged(num, armorComponent);
                    OnArmorPointsChanged(armorComponent);
                }
            }
        }
    }

    public PlayerHitInfo ApplyClientShot(DamageInfo damageInfo, EBodyPart bodyPartType, EBodyPartColliderType colliderType, EArmorPlateCollider armorPlateCollider, ShotId shotId)
    {
        ShotReactions(damageInfo, bodyPartType);
        ApplyHitDebuff(damageInfo.Damage, 0f, bodyPartType, damageInfo.DamageType);
        LastAggressor = damageInfo.Player.iPlayer;
        LastDamagedBodyPart = bodyPartType;
        LastBodyPart = bodyPartType;
        LastDamageInfo = damageInfo;
        LastDamageType = damageInfo.DamageType;

        if (HealthController != null && !HealthController.IsAlive)
        {
            return null;
        }

        var flag = NativeNullable<MongoID>.Read(damageInfo, "DeflectedBy").HasValue;
        var damage = damageInfo.Damage;
        var list = ProceedDamageThroughArmor(ref damageInfo, colliderType, armorPlateCollider, true);
        var materialType = flag ? MaterialType.HelmetRicochet : ((list == null || list.Count < 1)
            ? MaterialType.Body : list[0].Material);
        PlayerHitInfo hitInfo = new()
        {
            PoV = PointOfView,
            Penetrated = damageInfo.Penetrated,
            Material = materialType
        };
        var num = damage - damageInfo.Damage;
        if (num > 0)
        {
            damageInfo.DidArmorDamage = num;
        }
        damageInfo.DidBodyDamage = damageInfo.Damage;
        ReceiveDamage(damageInfo.Damage, bodyPartType, damageInfo.DamageType, num, hitInfo.Material);

        CommonPacket.Type = ECommonSubPacketType.Damage;
        CommonPacket.SubPacket = DamagePacket.FromValue(NetId, damageInfo, bodyPartType, colliderType, armorPlateCollider, absorbed: num);
        FikaSingleton<IFikaNetworkManager>.Instance.SendNetReusable(ref CommonPacket, DeliveryMethod.ReliableOrdered, true);

        // Run this to get weapon skill
        ManageAggressor(damageInfo, bodyPartType, colliderType);

        // [HitMarker] Same as ApplyShot: the damage of this hit is applied by the machine that
        // owns the victim, so the local hook has to be fired here to keep hit feedback alive.
        ReportLocalHit(damageInfo, bodyPartType);

        return hitInfo;
    }

    public override void OnMounting(EFT.MountingPacket.EMountingCommand command)
    {
        // Do nothing
    }

    public override void ApplyCorpseImpulse()
    {
        if (_cullingHandler.IsVisible || _isServer)
        {
            if (CorpseSyncPacket.BodyPartColliderType != EBodyPartColliderType.None
                    && PlayerBones.BodyPartCollidersDictionary.TryGetValue(CorpseSyncPacket.BodyPartColliderType, out var bodyPartCollider))
            {
                Corpse.Ragdoll.ApplyImpulse(bodyPartCollider.Collider, CorpseSyncPacket.Direction, CorpseSyncPacket.Point, CorpseSyncPacket.Force);
            }
        }
    }

    public override void CreateMovementContext()
    {
        var movement_MASK = EFTHardSettings.Instance.MOVEMENT_MASK;
        MovementContext = ObservedMovementContext.Create(this, GetBodyAnimatorCommon, GetCharacterControllerCommon, movement_MASK);
    }

    public override void OnHealthEffectAdded(IHealthEffect effect)
    {
        // Check for GClass increments
        if (effect?.TryCast<IFracture>() is { } fracture && !fracture.WasPaused && FractureSound != null && Singleton<BetterAudio>.Instantiated)
        {
            var options = PlayOptions.Default;
            options.volume = 0.7f;
            options.occlusionTest = EOcclusionTest.Fast;
            Singleton<BetterAudio>.Instance.PlayAtPoint(Position, FractureSound,
                BetterAudio.AudioSourceGroupType.Impacts, 15, ref options, CameraManager.Instance.Distance(Position));
        }
    }

    public override void OnHealthEffectRemoved(IHealthEffect effect)
    {
        // Do nothing
    }

    public override void ConnectSkillManager()
    {
        // Do nothing
    }

    #region proceed
    public override void Proceed(bool withNetwork, Callback<IEmptyHandsController> callback, bool scheduled = true)
    {
        Func<EmptyHandsController> func = new(ProceedEmptyHandsController);
        new Process<EmptyHandsController, IEmptyHandsController>(this, func, null, false)
            .Proceed(null, callback, scheduled);
    }

    public override void Proceed(KnifeComponent knife, Callback<IKnifeController> callback, bool scheduled = true)
    {
        HandsControllerFactory factory = new(this, knifeComponent: knife);
        Func<KnifeController> func = new(factory.CreateObservedKnifeController);
        new Process<KnifeController, IKnifeController>(this, func, factory.KnifeComponent.Item)
            .Proceed(null, callback, scheduled);
    }

    public override void Proceed(ThrowWeap throwWeap, Callback<IGrenadeController> callback, bool scheduled = true)
    {
        HandsControllerFactory factory = new(this, throwWeap);
        Func<GrenadeHandsController> func = new(factory.CreateObservedGrenadeController);
        new Process<GrenadeHandsController, IGrenadeController>(this, func, throwWeap, false)
            .Proceed(null, callback, scheduled);
    }

    public override void Proceed(ThrowWeap throwWeap, Callback<IQuickGrenadeThrowController> callback, bool scheduled = true)
    {
        HandsControllerFactory factory = new(this, throwWeap);
        Func<QuickGrenadeThrowHandsController> func = new(factory.CreateObservedQuickGrenadeController);
        new Process<QuickGrenadeThrowHandsController, IQuickGrenadeThrowController>(this, func, throwWeap, false)
            .Proceed(null, callback, scheduled);
    }

    public override void Proceed(Weapon weapon, Callback<IFirearmHandsController> callback, bool scheduled = true)
    {
        HandsControllerFactory factory = new(this, weapon);
        Func<FirearmController> func = new(factory.CreateObservedFirearmController);
        new Process<FirearmController, IFirearmHandsController>(this, func, factory.Item, true)
            .Proceed(null, callback, scheduled);
    }

    public override void Proceed(Meds meds, OneAndList<EBodyPart> bodyParts, Callback<IMedsController> callback, int animationVariant, bool scheduled = true)
    {
        HandsControllerFactory factory = new(this)
        {
            MedsItem = meds,
            BodyParts = bodyParts,
            AnimationVariant = animationVariant
        };
        Func<MedsController> func = new(factory.CreateObservedMedsController);
        new Process<MedsController, IMedsController>(this, func, meds, false)
            .Proceed(null, callback, scheduled);
    }

    public override void Proceed(FoodDrink foodDrink, float amount, Callback<IMedsController> callback, int animationVariant, bool scheduled = true)
    {
        HandsControllerFactory factory = new(this)
        {
            FoodItem = foodDrink,
            Amount = amount,
            AnimationVariant = animationVariant
        };
        Func<MedsController> func = new(factory.CreateObservedMedsController);
        new Process<MedsController, IMedsController>(this, func, foodDrink, false)
            .Proceed(null, callback, scheduled);
    }
    #endregion

    public override void OnFovUpdatedEvent(int fov)
    {
        // Do nothing
    }

    public override void ShowHelloNotification(string sender)
    {
        // Do nothing
    }

    public override void HealthControllerUpdate(float deltaTime)
    {
        // Do nothing
    }

    public override void OnPhraseTold(EPhraseTrigger @event, TaggedClip clip, TagBank bank, BaseSpeaker speaker)
    {
        if (!FikaBackendUtils.IsHeadless)
        {
            PlayPhraseClip(clip);
        }
    }

    public override void MouseLook(bool forceApplyToOriginalRibcage = false)
    {
        MovementContext.RotationAction.Invoke(this);
    }

    public override bool CheckSurface(float range)
    {
        if (_lastDistance > (range * ProtagonistHearing))
        {
            return false;
        }

        return global::Fika.Core.Main.Utils.NativeBaseCalls.Call117(this, range);
    }

    /// <summary>
    /// Updates replicated values
    /// </summary>
    public void ManualStateUpdate(double localTime)
    {
        var bufferState = Snapshotter.GetInterpolationIndices(localTime, out var from, out var to, out var t);

        

        // The Enable* caches below describe one player state only: after a state change another writer may have
        // moved the flags (ObservedMountedState does), so the next push is forced.
        if (!_pushedFlagStateValid || CurrentPlayerState.State != _pushedFlagState)
        {
            _pushedFlagState = CurrentPlayerState.State;
            _pushedFlagStateValid = true;
            _pushedJumpFlagValid = false;
            _pushedLandingFlagValid = false;
            _pushedInertFlagValid = false;
        }

        if (bufferState == EBufferState.Stale)
        {
            if (!CurrentPlayerState.IsMoving)
            {
                return;
            }

            CurrentPlayerState.Velocity = Vector3.zero;
            CurrentPlayerState.MovementDirection = Vector2.zero;
            CurrentPlayerState.IsMoving = false;
            ObservedCharacterController._velocity = CurrentPlayerState.Velocity;
            

            PushInertFlag(CurrentPlayerState.IsMoving);
            PushMovementDirection(CurrentPlayerState.MovementDirection);
            return;
        }

        ref readonly var snapFrom = ref Snapshotter.GetSnapshot(from);
        var currentState = CurrentPlayerState;

        if (bufferState == EBufferState.Interpolating)
        {
            ref readonly var snapTo = ref Snapshotter.GetSnapshot(to);

            currentState.Rotation = new Vector2(
                Mathf.LerpAngle(snapFrom.Data.Rotation.x, snapTo.Data.Rotation.x, t),
                Mathf.LerpUnclamped(snapFrom.Data.Rotation.y, snapTo.Data.Rotation.y, t)
            );

            currentState.HeadRotation = Vector3.LerpUnclamped(snapFrom.Data.HeadRotation, snapTo.Data.HeadRotation, t);
            currentState.Position = Vector3.LerpUnclamped(snapFrom.Data.Position, snapTo.Data.Position, t);

            var newDir = currentState.MovementDirection = Vector2.LerpUnclamped(snapFrom.Data.MovementDirection, snapTo.Data.MovementDirection, t);
            if (!_isZombie && (snapTo.Data.State is EPlayerState.Idle or EPlayerState.Transition || newDir.sqrMagnitude < _movementDeadZoneSqr))
            {
                currentState.MovementDirection = Vector2.zero;
                currentState.IsMoving = false;
            }
            else
            {
                currentState.MovementDirection = newDir;
                currentState.IsMoving = true;
            }

            currentState.State = snapTo.Data.State;
            currentState.Tilt = Mathf.LerpUnclamped(snapFrom.Data.Tilt, snapTo.Data.Tilt, t);
            currentState.Step = snapTo.Data.Step;
            currentState.MovementSpeed = Mathf.LerpUnclamped(snapFrom.Data.MovementSpeed, snapTo.Data.MovementSpeed, t);
            currentState.SprintSpeed = Mathf.LerpUnclamped(snapFrom.Data.SprintSpeed, snapTo.Data.SprintSpeed, t);
            currentState.IsProne = snapTo.Data.IsProne;
            currentState.PoseLevel = Mathf.LerpUnclamped(snapFrom.Data.PoseLevel, snapTo.Data.PoseLevel, t);
            currentState.IsSprinting = snapTo.Data.IsSprinting;
            currentState.Stamina = snapTo.Data.Physical;
            currentState.Blindfire = snapTo.Data.Blindfire;
            currentState.WeaponOverlap = Mathf.LerpUnclamped(snapFrom.Data.WeaponOverlap, snapTo.Data.WeaponOverlap, t);
            currentState.LeftStanceDisabled = snapTo.Data.LeftStanceDisabled;
            currentState.IsGrounded = snapTo.Data.IsGrounded;

            var velocity = Vector3.LerpUnclamped(snapFrom.Data.Velocity, snapTo.Data.Velocity, t);
            if (velocity.sqrMagnitude < _velocityDeadZoneSqr)
            {
                velocity = Vector3.zero;
            }

            currentState.Velocity = velocity;
        }
        else if (bufferState == EBufferState.Extrapolating)
        {
            currentState.Position = snapFrom.Data.Position + (snapFrom.Data.Velocity * t);

            currentState.Rotation = snapFrom.Data.Rotation;
            currentState.HeadRotation = snapFrom.Data.HeadRotation;
            currentState.MovementDirection = snapFrom.Data.MovementDirection;
            currentState.IsMoving = snapFrom.Data.MovementDirection.sqrMagnitude > _movementDeadZoneSqr;

            currentState.State = snapFrom.Data.State;
            currentState.Tilt = snapFrom.Data.Tilt;
            currentState.Step = snapFrom.Data.Step;
            currentState.MovementSpeed = snapFrom.Data.MovementSpeed;
            currentState.SprintSpeed = snapFrom.Data.SprintSpeed;
            currentState.IsProne = snapFrom.Data.IsProne;
            currentState.PoseLevel = snapFrom.Data.PoseLevel;
            currentState.IsSprinting = snapFrom.Data.IsSprinting;
            currentState.Stamina = snapFrom.Data.Physical;
            currentState.Blindfire = snapFrom.Data.Blindfire;
            currentState.WeaponOverlap = snapFrom.Data.WeaponOverlap;
            currentState.LeftStanceDisabled = snapFrom.Data.LeftStanceDisabled;
            currentState.IsGrounded = snapFrom.Data.IsGrounded;
            currentState.Velocity = snapFrom.Data.Velocity;
        }

        if (!_cullingHandler.IsVisible)
        {
            Position = CurrentPlayerState.Position;
            Rotation = CurrentPlayerState.Rotation;
            ObservedCharacterController._velocity = CurrentPlayerState.Velocity;

            if (!_isServer)
            {
                return;
            }

            if (CurrentPlayerState.State == EPlayerState.Jump)
            {
                MovementContext.EmitJumpNoise(1f);
                return;
            }

            if (CurrentPlayerState.IsMoving)
            {
                MovementContext.EmitStepNoise(CurrentPlayerState.MovementDirection);
            }

            return;
        }

        Rotation = CurrentPlayerState.Rotation;
        

        HeadRotation = CurrentPlayerState.HeadRotation;
        
        ProceduralWeaponAnimation.SetHeadRotation(CurrentPlayerState.HeadRotation);
        

        var newState = CurrentPlayerState.State;

        if (newState == EPlayerState.Jump)
        {
            PushJumpFlag(true);
            if (_isServer)
            {
                MovementContext.EmitJumpNoise(1f);
            }
        }

        var isGrounded = CurrentPlayerState.IsGrounded;
        MovementContext.IsGrounded = isGrounded;
        

        if (isGrounded)
        {
            PushJumpFlag(false);
            PushLandingFlag(true);
        }

        PushInertFlag(CurrentPlayerState.IsMoving);
        PushMovementDirection(CurrentPlayerState.MovementDirection);
        if (_isServer && CurrentPlayerState.IsMoving)
        {
            MovementContext.EmitStepNoise(CurrentPlayerState.MovementDirection);
        }

        Physical.SerializationStruct = CurrentPlayerState.Stamina;
        

        if (MovementContext.Step != CurrentPlayerState.Step)
        {
            CurrentManagedState.SetStep(CurrentPlayerState.Step);
            
        }
        else
        {
            
        }

        if (Physical.Sprinting != CurrentPlayerState.IsSprinting)
        {
            CurrentManagedState.EnableSprint(CurrentPlayerState.IsSprinting);
            
        }
        else
        {
            
        }

        if (MovementContext.IsInPronePose != CurrentPlayerState.IsProne)
        {
            MovementContext.IsInPronePose = CurrentPlayerState.IsProne;
            
        }
        else
        {
            
        }

        if (!Mathf.Approximately(PoseLevel, CurrentPlayerState.PoseLevel))
        {
            MovementContext.SetPoseLevel(CurrentPlayerState.PoseLevel);
            
        }
        else
        {
            
        }

        // The speed call stays where it was and keeps running every frame: its body also refreshes the covert
        // efficiency, which depends on the player's weight and skills and not only on the pushed speed. The
        // redundant half - the native smoothed-value write with its animator and weapon-sway side effects - is
        // skipped inside ObservedMovementContext.SetCharacterMovementSpeed.
        MovementContext.SetCharacterMovementSpeed(CurrentPlayerState.MovementSpeed, true);
        

        // (analysis/stutter-pose-reset) 值未变就不写: the native SprintSpeed setter pushes the value into the
        // animator unconditionally (MovementContext.cs:781-792), and this line used to run for every observed
        // player on every frame while a sprint speed never changes during a walk or a stand.
        if (!PoseWriteGuards.Enabled || MovementContext.SprintSpeed != CurrentPlayerState.SprintSpeed)
        {
            MovementContext.SprintSpeed = CurrentPlayerState.SprintSpeed;
            
        }
        else
        {
            
        }

        if (MovementContext.BlindFire != CurrentPlayerState.Blindfire)
        {
            MovementContext.SetBlindFire(CurrentPlayerState.Blindfire);
            
        }
        else
        {
            
        }

        Transform.position = CurrentPlayerState.Position;
        

        if (!Mathf.Approximately(MovementContext.Tilt, CurrentPlayerState.Tilt))
        {
            MovementContext.SetTilt(CurrentPlayerState.Tilt, true);
            
        }
        else
        {
            
        }

        if (!Mathf.Approximately(ObservedOverlap, CurrentPlayerState.WeaponOverlap))
        {
            ObservedOverlap = CurrentPlayerState.WeaponOverlap;
            ShouldOverlap = true;
            
        }
        else
        {
            
        }

        LeftStanceDisabled = CurrentPlayerState.LeftStanceDisabled;
        

        ObservedCharacterController._velocity = CurrentPlayerState.Velocity;
        
    }

    /// <summary>
    /// (analysis/stutter-pose-reset) 值未变就不写 for the jump flag. The native side is one unconditional call
    /// into the animator (MovementContext.cs:3852-3855 -> PlayerAnimator.EnableJump) and the native jump state
    /// only calls it on the frames the value changes (JumpStateClass.cs:108/148/200), so repeating it on every
    /// grounded frame only buys native calls. The pushed value sequence is unchanged: the first frame of a state
    /// always pushes (the cache is invalidated on every state change), and after that only real changes do.
    /// </summary>
    private void PushJumpFlag(bool enabled)
    {
        if (PoseWriteGuards.Enabled && _pushedJumpFlagValid && _pushedJumpFlag == enabled)
        {
            
            return;
        }

        MovementContext.PlayerAnimatorEnableJump(enabled);
        
        _pushedJumpFlag = enabled;
        _pushedJumpFlagValid = true;
    }

    /// <summary>
    /// (analysis/stutter-pose-reset) 值未变就不写 for the landing flag; same reasoning as
    /// <see cref="PushJumpFlag"/>. The flag is a boolean animation gate: keeping it at the value it already has
    /// changes nothing for the animation, while re-sending it costs a native call per player per frame.
    /// </summary>
    private void PushLandingFlag(bool enabled)
    {
        if (PoseWriteGuards.Enabled && _pushedLandingFlagValid && _pushedLandingFlag == enabled)
        {
            
            return;
        }

        MovementContext.PlayerAnimatorEnableLanding(enabled);
        
        _pushedLandingFlag = enabled;
        _pushedLandingFlagValid = true;
    }

    /// <summary>
    /// (analysis/stutter-pose-reset) 值未变就不写 for the inertia flag. The native movement code does call
    /// <c>PlayerAnimatorEnableInert</c> every frame in some states, which is the proof that repeating the same
    /// boolean is harmless - and also the proof that it is not needed: the animator only sees the value.
    /// </summary>
    private void PushInertFlag(bool enabled)
    {
        if (PoseWriteGuards.Enabled && _pushedInertFlagValid && _pushedInertFlag == enabled)
        {
            
            return;
        }

        MovementContext.PlayerAnimatorEnableInert(enabled);
        
        _pushedInertFlag = enabled;
        _pushedInertFlagValid = true;
    }

    /// <summary>
    /// (analysis/stutter-pose-reset) 值未变就不写 for the movement direction. The native setter is
    /// unconditional (MovementContext.cs:942-953 -> PlayerAnimator.SetMovementDirection), and a standing or
    /// idle player keeps the same direction for frame after frame. Reading the value back costs one native
    /// getter and saves the setter and its animator call whenever the value already matches.
    /// </summary>
    private void PushMovementDirection(Vector2 direction)
    {
        if (!PoseWriteGuards.Enabled || MovementContext.MovementDirection != direction)
        {
            MovementContext.MovementDirection = direction;
            
            return;
        }

        
    }

    public override void InteractionRaycast()
    {
        if (_playerLookRaycastTransform == null || !HealthController.IsAlive)
        {
            return;
        }

        InteractableObjectIsProxy = false;
        var interactionRay = InteractionRay;
        Sense = false;
        var gameObject = GameWorld.FindInteractable(interactionRay, out _);
        if (gameObject != null)
        {
            var player = gameObject.GetComponent<Player>();
            if (player != null && player != InteractablePlayer)
            {
                InteractablePlayer = (player != this) ? player : null;
            }
            return;
        }

        InteractablePlayer = null;
    }

    public override Corpse CreateCorpse()
    {
        // [CorpseSync] One corpse per victim. The native death path can still run after Fika
        // already finalised the death (for example a revival plugin that swallowed the first
        // Player.OnDead and releases it when its own timer expires); a second corpse component
        // on the same body would be a duplicate that cannot be looted.
        if (_fikaFinalisedCorpse != null)
        {
            return _fikaFinalisedCorpse;
        }

        if (CorpseSyncPacket.InventoryDescriptor != null)
        {
            SetInventory(CorpseSyncPacket.InventoryDescriptor);
        }
        if (FikaBackendUtils.IsClient)
        {
            var observedCorpse = CreateCorpse<ObservedCorpse>(Velocity);
            observedCorpse.UnlootableCorpseOnDeath = UsedSimplifiedSkeleton;
            observedCorpse.SetSpecificSettings(PlayerBones.RightPalm);
            Singleton<GameWorld>.Instance.ObservedPlayersCorpses.Add(NetId, observedCorpse);
            return observedCorpse;
        }

        var corpse = CreateCorpse<Corpse>(Velocity);
        corpse.UnlootableCorpseOnDeath = UsedSimplifiedSkeleton;
        CorpsePositionSyncer.Create(corpse.gameObject, corpse, NetId);
        return corpse;
    }

    public override void CreateNestedSource()
    {
        if (!FikaBackendUtils.IsHeadless)
        {
            base.CreateNestedSource();
        }
    }

    public override void CreateSpeechSource()
    {
        if (!FikaBackendUtils.IsHeadless)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call88(this);
        }
    }

    public override void OnDead(EDamageType damageType)
    {
        if (HasDied) return;
        ClearReviveInteractable();

        if (HealthBar != null)
        {
            Destroy(HealthBar);
        }

        if (FikaPlugin.Instance.Settings.ShowNotifications.Value)
        {
            if (!IsObservedAI)
            {
                var nickname = Profile.GetDisplayName();
                if (damageType != EDamageType.Undefined)
                {
                    NotificationManager.DisplayWarningNotification(string.Format(LocaleUtils.GROUP_MEMBER_DIED_FROM.Localized(),
                        [ColorizeText(EColor.GREEN, nickname), ColorizeText(EColor.RED, ("DamageType_" + damageType.ToString()).Localized())]));
                }
                else
                {
                    NotificationManager.DisplayWarningNotification(string.Format(LocaleUtils.GROUP_MEMBER_DIED.Localized(),
                        ColorizeText(EColor.GREEN, nickname)));
                }
            }
            if (LocaleUtils.IsBoss(Profile.Info.Settings.Role, out var name) && IsObservedAI && LastAggressor?.TryCast<FikaPlayer>() is { } aggressor)
            {
                var aggressorNickname = LastAggressor.Profile.GetDisplayName();
                if (aggressor.gameObject.name.StartsWith("Player_") || aggressor.IsYourPlayer)
                {
                    FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.KILLED_BOSS.Localized(),
                    [ColorizeText(EColor.GREEN, aggressorNickname), ColorizeText(EColor.BROWN, name)]),
                    iconType: EFT.Communications.ENotificationIconType.Friend);
                }
            }
        }
        Singleton<BetterAudio>.Instance.ProtagonistHearingChanged -= (System.Action)UpdateSoundRolloff;
        base.OnDead(damageType);

        // [CorpseSync] The death of a client's observed AI must not depend on the native
        // Player.OnDead being allowed to run. EFT derives Player.IsAI from AIData.IsAI, which the
        // game only sets when a BotOwner is attached - a puppet never has one - so every AI check
        // (the game's own and those of third-party plugins, for example a revival mod that guards
        // its death hook with "player.IsAI") classifies a remote bot as a human player. Such a
        // plugin turns the puppet's death into its critical state: its prefix on Player.OnDead
        // returns false, so Corpse = CreateCorpse(), the animator/character controller shutdown
        // and GameWorld.UnregisterPlayer never happen on this machine and the victim keeps
        // crawling, prone and unlootable, while the authoritative machine already has the corpse.
        // Finish the death here so the victim always ends up dead for everyone.
        if (Corpse == null && IsObservedAI && !IsYourPlayer)
        {
            FinaliseObservedAiDeath();
        }

        

        if (_cullingHandler != null)
        {
            _cullingHandler.DisableCullingOnDead();
        }
        if (!FikaBackendUtils.IsHeadless && Corpse != null)
        {
            _observedCorpseCulling = new(this, Corpse);
        }
        if (CorpseSyncPacket.ItemInHands != null)
        {
            Corpse.SetItemInHandsLootedCallback(null);
            Corpse.ItemInHands.Value = CorpseSyncPacket.ItemInHands;
            Corpse.SetItemInHandsLootedCallback((System.Action)ReleaseHand);
        }
        CorpseSyncPacket = default;
        FikaSingleton<IFikaNetworkManager>.Instance.ObservedPlayers.Remove(this);
    }

    /// <summary>
    /// Completes the death of an observed AI whose native <see cref="Player.OnDead"/> was skipped
    /// on this machine (a third-party prefix returned false).
    /// </summary>
    /// <remarks>
    /// This is the tail of the native Player.OnDead: the corpse is created from the host's synced
    /// inventory descriptor (which is also what builds the loot container), the ragdoll takes the
    /// body over, the puppet stops animating and the player leaves the world. Nothing here applies
    /// damage or touches the health controller, so it can never duplicate a death or a hit: it
    /// only runs when the game itself refused to finalise the death it already decided on.
    /// </remarks>
    private void FinaliseObservedAiDeath()
    {
        try
        {
            var corpse = CreateCorpse();
            _fikaFinalisedCorpse = corpse;
            Corpse = corpse;
        }
        catch (Exception exception)
        {
                        return;
        }

        try
        {
            ApplyCorpseImpulse();
        }
        catch (Exception exception)
        {
                    }

        // Stop the puppet the same way the native OnDead does, otherwise the body keeps being
        // animated (crawling in a prone pose) next to the corpse that was just created.
        try
        {
            EnabledAnimators = 0;
            var bodyAnimator = BodyAnimatorCommon;
            if (bodyAnimator != null)
            {
                bodyAnimator.enabled = false;
            }

            var armsAnimator = ArmsAnimatorCommon;
            if (armsAnimator != null)
            {
                armsAnimator.enabled = false;
            }

            var characterController = CharacterController;
            if (characterController != null)
            {
                characterController.isEnabled = false;
            }
        }
        catch (Exception exception)
        {
                    }

        try
        {
            var handsController = HandsController;
            if (handsController != null)
            {
                handsController.OnPlayerDead();
            }
        }
        catch (Exception exception)
        {
                    }

        try
        {
            var world = Singleton<GameWorld>.Instance;
            if (world != null)
            {
                world.UnregisterPlayer(this);
            }
        }
        catch (Exception exception)
        {
                    }
    }

    public override void TransitInteraction(TransitController controller, int transitPointId, string keyId, EDateTime time)
    {
        // Do nothing
    }

    public override void HandleDamagePacket(DamagePacket packet)
    {
        DamageInfo damageInfo = new()
        {
            Damage = packet.Damage,
            DamageType = packet.DamageType,
            BodyPartColliderType = packet.ColliderType,
            HitPoint = packet.Point,
            HitNormal = packet.HitNormal,
            Direction = packet.Direction,
            PenetrationPower = packet.PenetrationPower,
            ArmorDamage = packet.ArmorDamage
        };
        NativeNullable<MongoID>.Write(damageInfo, "BlockedBy", packet.BlockedBy);
        NativeNullable<MongoID>.Write(damageInfo, "DeflectedBy", packet.DeflectedBy);

        if (packet.SourceId.HasValue)
        {
            damageInfo.SourceId = packet.SourceId.Value;
        }

        if (packet.ProfileId.HasValue)
        {
            var world = Singleton<GameWorld>.Instance;
            var attacker = world.GetAlivePlayerByProfileID(packet.ProfileId.Value.ToString());
            var player = attacker != null ? world.GetAlivePlayerBridgeByRaidID(attacker.RaidId) : null;

            if (player != null)
            {
                damageInfo.Player = player;
                LastAggressor = player.iPlayer;
            }

            _lastWeaponId = packet.WeaponId.HasValue ? packet.WeaponId.Value : null;
        }

        ShotReactions(damageInfo, packet.BodyPartType);
        ReceiveDamage(damageInfo.Damage, packet.BodyPartType, damageInfo.DamageType, packet.Absorbed, packet.Material);

        LastDamageInfo = damageInfo;
        LastBodyPart = packet.BodyPartType;
        LastDamagedBodyPart = packet.BodyPartType;
    }

    public override void OnBeenKilledByAggressor(IPlayer aggressor, DamageInfo damageInfo, EBodyPart bodyPart, EDamageType lethalDamageType)
    {
        // Only handle if it was ourselves as otherwise it's irrelevant
        if (LastAggressor.IsYourPlayer)
        {
            base.OnBeenKilledByAggressor(aggressor, damageInfo, bodyPart, lethalDamageType);
            return;
        }

        if (aggressor.IsCoopPlayer() && !aggressor.IsYourPlayer)
        {
            var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer?.TryCast<FikaPlayer>();
            if (mainPlayer == null)
            {
                                return;
            }

            if (!mainPlayer.HealthController.IsAlive)
            {
                return;
            }

            var role = Profile.Info.Settings.Role;
            var countAsBoss = role.CountAsBossForStatistics() && !(role is WildSpawnType.pmcUSEC or WildSpawnType.pmcBEAR);
            var experience = Profile.Info.Settings.Experience;
            var sessionCounters = mainPlayer.Profile.EftStats.SessionCounters;
            HandleSharedExperience(countAsBoss, experience, sessionCounters);

            if (FikaPlugin.Instance.Settings.SharedQuestProgression && FikaPlugin.Instance.Settings.EasyKillConditions.Value &&
                aggressor.TryCast<FikaPlayer>() is { } killer)
            {
#if DEBUG
                FikaGlobals.LogInfo("Handling teammate kill from teammate: " + aggressor.Profile.Nickname);
#endif

                var distance = Vector3.Distance(aggressor.Position, Position);
                mainPlayer.HandleTeammateKill(damageInfo, bodyPart, Side, role, ProfileId,
                    distance, Inventory.EquippedInSlotsTemplateIds.AsManaged().ToList(), HealthController.BodyPartEffects, TriggerZones.AsManaged().ToList(),
                    killer);
            }
        }
    }

    public override void ExternalInteraction()
    {
        // Do nothing
    }

    public override void ToggleDowned(bool downed)
    {
        if (HasDied || Downed == downed) return;
        RevivalClock.SetDowned(downed, FikaPlugin.Instance.Settings.ReviveConfig.BleedoutTime);
#if DEBUG
        FikaGlobals.LogInfo($"Setting {Profile.GetCorrectedNickname()} downed state to {downed}");
#endif
        Downed = downed;
        NetworkHealthController.IsAlive = !Downed;
        if (_healthBar != null)
        {
            _healthBar.ToggleDowned(downed);
        }

        if (downed)
        {
            if (FikaPlugin.Instance.Settings.ShowNotifications.Value)
            {
                NotificationManager.DisplayWarningNotification(string.Format(LocaleUtils.UI_REVIVING_BEEN_DOWNED.Localized(),
                                ColorizeText(EColor.GREEN, Profile.GetCorrectedNickname())));
            }
            Speaker.Play(EPhraseTrigger.OnAgony, HealthStatus, true, new Il2CppSystem.Nullable<int>());
            if (_reviveInteractable != null)
            {
                FikaGlobals.LogWarning($"ReviveInteractable was not null on {Profile.GetCorrectedNickname()}");
                ClearReviveInteractable();
            }

            _reviveInteractable = ReviveInteractable.Create(this);
            return;
        }

        if (FikaPlugin.Instance.Settings.ShowNotifications.Value)
        {
            NotificationManager.DisplayWarningNotification(string.Format(LocaleUtils.UI_REVIVING_BEEN_REVIVED.Localized(),
                            ColorizeText(EColor.GREEN, Profile.GetCorrectedNickname())));
        }

        if (_reviveInteractable == null)
        {
#if DEBUG
            FikaGlobals.LogWarning("ReviveInteractable was null, this is intentional if we revived");
#endif
            return;
        }

        _reviveInteractable.RemoveRagdoll();
        ClearReviveInteractable();
    }

    public override void ToggleRevive(bool reviving, string nickname)
    {
        RevivalClock.SetReviving(reviving, nickname, FikaPlugin.Instance.Settings.ReviveConfig.ReviveTime);
#if DEBUG
        FikaGlobals.LogInfo($"{Profile.GetCorrectedNickname()} is being revived by {nickname}");
#endif
        if (_reviveInteractable != null)
        {
            _reviveInteractable.BeingRevived = reviving;
        }
        if (_healthBar != null)
        {
            _healthBar.ToggleRevive(reviving, nickname);
        }
    }

    internal void ClearReviveInteractable()
    {
        var interactable = _reviveInteractable;
        if (interactable != null)
        {
            Destroy(interactable);
        }
        _reviveInteractable = null;
    }

    internal void CreateObservedCompass()
    {
        const string bundlePath = "assets/content/weapons/additional_hands/item_compass.bundle";
        if (!_compassLoaded)
        {
            var transform = Singleton<ObjectsFactory>.Instance.CreateFromPool<Transform>(new ResourceKey
            {
                path = bundlePath
            });
            transform.SetParent(PlayerBones.Ribcage.Original, false);
            transform.localRotation = Quaternion.identity;
            transform.localPosition = Vector3.zero;
            UpdateCompassController(transform.gameObject);
            _compassLoaded = true;
        }
    }

    public void SetInventory(ItemDescriptor inventoryDescriptor)
    {
        if (HandsController != null)
        {
            HandsController.FastForwardCurrentState();
        }

        var inventory = new InventoryDescriptor()
        {
            Equipment = inventoryDescriptor
        }.ToInventory();

        InventoryController.ReplaceInventory(inventory);
        Profile.Inventory = inventory;
        if (CorpseSyncPacket.ItemSlot <= EquipmentSlot.Scabbard)
        {
            var heldItem = Equipment.GetSlot(CorpseSyncPacket.ItemSlot).ContainedItem;
            if (heldItem != null)
            {
                CorpseSyncPacket.ItemInHands = heldItem;
            }
        }

        if (!FikaBackendUtils.IsHeadless)
        {
            RefreshSlotViews();
        }
    }

    private void RefreshSlotViews()
    {
        foreach (var equipmentSlot in PlayerBody.SlotNames)
        {
            var slot = Inventory.Equipment.GetSlot(equipmentSlot);
            ObservedSlotViewHandler handler = new(slot, this, equipmentSlot);
            _observedSlotViewHandlers.Add(handler);
        }

        if (PlayerBody.HaveHolster && PlayerBody.SlotViews.ContainsKey(EquipmentSlot.Holster))
        {
            var slot = Inventory.Equipment.GetSlot(EquipmentSlot.Holster);
            ObservedSlotViewHandler handler = new(slot, this, EquipmentSlot.Holster);
            _observedSlotViewHandlers.Add(handler);
        }

        if (HandsController?.TryCast<ObservedFirearmController>() is { } controller)
        {
            if (Inventory.Equipment.TryFindItem(controller.Weapon.Id, out var item))
            {
                if (item.TryCast<Weapon>() is not { } newWeapon)
                {
                    FikaGlobals.LogError("HandsController item was not Weapon");
                    return;
                }

                var newSlots = newWeapon.AllSlots;
                if (newSlots != null)
                {
                    Dictionary<string, ContainerCollectionView.SlotView> currentViews = [];
                    foreach (var kvp in controller.CCV.ContainerBones)
                    {
                        if (kvp.Key.TryCast<Slot>() is { } slot && slot.ContainedItem != null)
                        {
                            if (currentViews.ContainsKey(slot.FullId))
                            {
                                FikaGlobals.LogError("CRITICAL ERROR DICTIONARY: " + slot.FullId);
                                continue;
                            }
                            currentViews.Add(slot.FullId, kvp.Value);
                        }
                    }
                    controller.CCV.RemoveBones(controller.Weapon.AllSlots.AsManaged().Select(slot => slot.Cast<EFT.InventoryLogic.IContainer>()).ToNativeList());
                    foreach (EFT.InventoryLogic.IContainer container in newSlots)
                    {
                        if (container.TryCast<Slot>() is { } slot)
                        {
                            if (slot.ContainedItem == null)
                            {
                                var transform = TransformTools.FindTransformRecursive(controller.CCV.GameObject.transform,
                                    slot.ID, true);
                                if (transform == null)
                                {
#if DEBUG
                                    FikaGlobals.LogWarning($"RefreshSlotViews::Transform was missing: {slot.ID}, this is harmless");
#endif
                                    continue;
                                }
                                controller.CCV.AddBone(slot, transform);
                                continue;
                            }
                            foreach (var kvp in currentViews)
                            {
                                if (kvp.Key == slot.FullId)
                                {
                                    controller.CCV.ContainerBones[slot] = kvp.Value;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    public override void DoObservedVault(VaultPacket packet)
    {
        if (packet.VaultingStrategy != EVaultingStrategy.Vault)
        {
            if (packet.VaultingStrategy != EVaultingStrategy.Climb)
            {
                return;
            }
            MovementContext.PlayerAnimator.SetDoClimb(true);
        }
        else
        {
            MovementContext.PlayerAnimator.SetDoVault(true);
        }

        _observedVaultingParameters.MaxWeightPointPosition = packet.VaultingPoint;
        _observedVaultingParameters.VaultingHeight = packet.VaultingHeight;
        _observedVaultingParameters.VaultingLength = packet.VaultingLength;
        _observedVaultingParameters.VaultingSpeed = packet.VaultingSpeed;
        _observedVaultingParameters.AbsoluteForwardVelocity = packet.AbsoluteForwardVelocity;
        _observedVaultingParameters.BehindObstacleRatio = packet.BehindObstacleHeight;

        MovementContext.PlayerAnimator.SetVaultingSpeed(packet.VaultingSpeed);
        MovementContext.PlayerAnimator.SetVaultingHeight(packet.VaultingHeight);
        MovementContext.PlayerAnimator.SetVaultingLength(packet.VaultingLength);
        MovementContext.PlayerAnimator.SetBehindObstacleRatio(packet.BehindObstacleHeight);
        MovementContext.PlayerAnimator.SetAbsoluteForwardVelocity(packet.AbsoluteForwardVelocity);

        MovementContext.PlayerAnimator.SetIsGrounded(true);
    }

    public void InitObservedPlayer()
    {
        PacketSender = gameObject.AddComponent<ObservedPacketSender>();

        if (IsObservedAI)
        {
            BotStatePacket packet = new()
            {
                NetId = NetId,
                Type = BotStatePacket.EStateType.LoadBot
            };

            PacketSender.NetworkManager.SendData(ref packet, DeliveryMethod.ReliableOrdered);

            var vaultingComponent = this._vaultingComponent;
            if (vaultingComponent != null)
            {
                UpdateEvent -= (System.Action)vaultingComponent.DoVaultingTick;
            }

            this._vaultingComponent = null;
            this._vaultingComponentDebug = null;
            this._vaultingParameters = null;
            this._vaultingGameplayRestrictions = null;
            this._vaultAudioController = null;
            this._sprintVaultAudioController = null;
            this._climbAudioController = null;
        }

        // Session humans share Fika's group sentinel, exactly like the local player
        // (FikaPlayer.SetupMainPlayer). It is the only "same team" signal the game and
        // third-party consumers have - "SPT Dynamic Maps" reports a player as
        // "Friendly Player" when Player.GroupId equals the main player's, and the game's group
        // logic (voice groups, exfil sharing, teammate nameplates) keys off it as well. The
        // marker categories themselves come from the role/side classification, not from this
        // value, so sharing it does not collapse PMC/Scav/Boss. Bots keep their own group.
        if (!IsObservedAI)
        {
            Profile.Info.GroupId = FikaGlobals.FikaGroupId;
            Profile.Info.TeamId = FikaGlobals.FikaGroupId;
        }

        if (IsObservedAI)
        {
            // SPT 5.0 sends every PMC bot with Profile.Info.Side = Savage (the server rewrites
            // the side once the dog tag template has been chosen). Present the faction the role
            // says instead, so side-based consumers such as "SPT Dynamic Maps" see a PMC as a PMC.
            // This runs after CoopHandler already applied the spawn-time "spawned in session"
            // handling, so loot semantics are unchanged.
            FikaGlobals.ApplyFactionSide(this, "observedBot");
        }

        

        if (!IsObservedAI)
        {
            if (!FikaBackendUtils.IsHeadless)
            {
                _waitForStartRoutine = StartCoroutine(EngineCoroutines.Wrap(CreateHealthBar(), "ObservedPlayer.CreateHealthBar"));
            }

            var vaultingComponent = this._vaultingComponent;
            if (vaultingComponent != null)
            {
                UpdateEvent -= (System.Action)vaultingComponent.DoVaultingTick;
            }
            this._vaultingComponent = null;
            this._vaultingComponentDebug = null;
            this._vaultingParameters = null;
            this._vaultingGameplayRestrictions = null;

            InitVaultingAudioControllers(_observedVaultingParameters);

            if (FikaPlugin.Instance.Settings.ShowNotifications.Value)
            {
                FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.GROUP_MEMBER_SPAWNED.Localized(),
                    ColorizeText(EColor.GREEN, Profile.GetDisplayName())),
                ENotificationDurationType.Default, ENotificationIconType.Friend);
            }

            // Keep the replicated dogtag in the observed inventory. The host
            // owns its ID and corpse inventory, including during reconnect.
        }
    }

    private IEnumerator CreateHealthBar()
    {
        var fikaGame = FikaSingleton<IFikaGame>.Instance;
        if (fikaGame == null)
        {
            yield break;
        }

        while (fikaGame.GameController.GameInstance.Status != GameStatus.Started)
        {
            yield return null;
        }

        if (FikaPlugin.Instance.Settings.AllowNamePlates)
        {
            _healthBar = FikaHealthBar.Create(this);
        }
        else
        {
            
        }

        while (Singleton<GameWorld>.Instance.MainPlayer == null)
        {
            yield return null;
        }
        Singleton<GameWorld>.Instance.MainPlayer.StatisticsManager.OnGroupMemberConnected(Inventory);
    }

    public override void LateUpdate()
    {
        // (analysis/perf-multiplayer) The engine calls this once per observed player per frame (61 on the
        // measured client raids, none at all in a solo raid) and the body crosses into native code several
        // times before it can cull itself, so it is one of the two per-entity frame costs a client owns.
        // It gets its own slot; 
        try
        {
            DistanceDirty = true;
            OcclusionDirty = true;
            _observedCorpseCulling?.ManualUpdate();
            if (HealthController?.IsAlive != true)
            {
                return;
            }
            Physical.LateUpdate();
            ObservedVisualPass(Time.deltaTime, 3);
            PropUpdate();
            _armsupdated = false;
            _bodyupdated = false;
        }
        finally
        {
            
        }
    }

    public override void UpdateMuffledState()
    {
        // Do nothing
    }

    public override void SendVoiceMuffledState(bool isMuffled)
    {
        // Do nothing
    }

    public void SetMuffledState(bool muffled)
    {
        Muffled = muffled;
        if (MonoBehaviourSingleton<BetterAudio>.Instantiated)
        {
            var instance = MonoBehaviourSingleton<BetterAudio>.Instance;
            var audioMixerGroup = Muffled ? instance.SimpleOccludedMixerGroup : instance.ObservedPlayerSpeechMixer;
            if (SpeechSource != null)
            {
                SpeechSource.SetMixerGroup(audioMixerGroup);
            }
            if (VoipEftSource != null)
            {
                VoipEftSource.SetMixerGroup(audioMixerGroup);
            }
        }
    }

    public override void LandingAdjustments(float d)
    {
        // Do nothing
    }

    public new void CreateCompass()
    {
        var compassInstantiated = this._compassInstantiated;
        if (!compassInstantiated)
        {
            var transform = Singleton<ObjectsFactory>.Instance.CreateFromPool<Transform>(new ResourceKey
            {
                path = "assets/content/weapons/additional_hands/item_compass.bundle"
            });
            transform.SetParent(PlayerBones.Ribcage.Original, false);
            transform.localRotation = Quaternion.identity;
            transform.localPosition = Vector3.zero;
            UpdateCompassController(transform.gameObject);
            this._compassInstantiated = true;
        }
    }

    public override void OnAnimatedInteraction(EInteraction interaction)
    {
        if (interaction == EInteraction.FriendlyGesture)
        {
            InteractionRaycast();
            if (InteractablePlayer != null)
            {
                InteractablePlayer.ShowHelloNotification(Profile.Nickname);
            }
        }
    }

    public override void PauseAllEffectsOnPlayer()
    {
        NetworkHealthController.PauseAllEffects();
    }

    public override void UnpauseAllEffectsOnPlayer()
    {
        NetworkHealthController.UnpauseAllEffects();
    }

    public override void OnVaulting()
    {
        // Do nothing
    }

    public override void SetControllerInsteadRemovedOne(Item removingItem, Callback callback)
    {
        callback.Succeed();
    }

    public override void ManualUpdate(float deltaTime, Il2CppSystem.Nullable<float> platformDeltaTime = default, int loop = 1)
    {
        MovementUpdate(deltaTime);

        if (HealthController.IsAlive)
        {
            if (Time.frameCount % 2 == _frameSkip)
            {
                UpdateTriggerColliderSearcher(deltaTime, _cullingHandler.IsCloseToMyPlayerCamera);
            }
            _cullingHandler.ManualUpdate(deltaTime);
            switch (_currentState)
            {
                case EPlayerState.Idle:
                    TickIdleState();
                    break;

                case EPlayerState.Run:
                case EPlayerState.MoveZombieState:
                case EPlayerState.StartMoveZombieState:
                case EPlayerState.EndMoveZombieState:
                    TickRunState();
                    break;

                case EPlayerState.Sprint:
                    TickSprintState();
                    break;
            }
        }
    }

    public override void InitAudioController()
    {
        if (!FikaBackendUtils.IsHeadless)
        {
            base.InitAudioController();
            Singleton<BetterAudio>.Instance.ProtagonistHearingChanged += (System.Action)UpdateSoundRolloff;
        }
    }

    private void UpdateSoundRolloff()
    {
        CalculateMovementVolumeDefaultMultiplier(CommonAssets.Scripts.Audio.EAudioMovementState.Run);
        UpdateVoiceSoundRolloff();
    }

    private void UpdateVoiceSoundRolloff()
    {
        SpeechSource?.SetRolloff(60f * ProtagonistHearing);
    }

    public override void StartInteraction(WorldInteractiveObject interactiveObject, InteractionResult interactionResult, Il2CppSystem.Action callback, bool enterDoorState = true)
    {
        if (enterDoorState)
        {
            CurrentManagedState.StartDoorInteraction(interactiveObject, interactionResult, callback);
        }
        else
        {
            callback?.Invoke();
        }
        UpdateInteractionCast();
    }

    public override void ExecuteInteraction(WorldInteractiveObject door, InteractionResult interactionResult)
    {
        if (door != null)
        {
            CurrentManagedState.ExecuteDoorInteraction(door, interactionResult, null, this);
        }
    }

    public override bool UpdateGrenadeAnimatorDuePoV()
    {
        return true;
    }

    public override void FixedUpdateTick()
    {
        // Do nothing
    }

    public override void OnDestroy()
    {
        ClearReviveInteractable();
        if (_followerCullingObject != null)
        {
            _followerCullingObject.enabled = false;
        }
        if (HandsController != null)
        {
            var handsController = HandsController;
            if (handsController != null && handsController.ControllerGameObject != null)
            {
                HandsController.OnGameSessionEnd();
                HandsController.Destroy();
            }
        }
        if (_healthBar != null)
        {
            Destroy(_healthBar);
        }
        if (Singleton<BetterAudio>.Instantiated)
        {
            Singleton<BetterAudio>.Instance.ProtagonistHearingChanged -= (System.Action)UpdateSoundRolloff;
        }
        foreach (var slotViewHandler in _observedSlotViewHandlers)
        {
            slotViewHandler.Dispose();
        }
        _observedSlotViewHandlers.Clear();
        _observedCorpseCulling?.Dispose();
        UnregisterNetworkPlayer();
        base.OnDestroy();
    }

    public override void SendHandsInteractionStateChanged(bool value, int animationId)
    {
        if (value)
        {
            MovementContext.SetBlindFire(0);
        }
    }

    public void HandleProceedPacket(ProceedPacket packet)
    {
        switch (packet.ProceedType)
        {
            case EProceedType.EmptyHands:
                {
                    CreateEmptyHandsController();
                    break;
                }
            case EProceedType.FoodClass:
            case EProceedType.MedsClass:
                {
                    CreateMedsController(packet.ItemId, packet.BodyParts, packet.Amount, packet.AnimationVariant);
                    break;
                }
            case EProceedType.GrenadeClass:
                {
                    CreateGrenadeController(packet.ItemId);
                    break;
                }
            case EProceedType.QuickGrenadeThrow:
                {
                    CreateQuickGrenadeController(packet.ItemId);
                    break;
                }
            case EProceedType.QuickKnifeKick:
                {
                    CreateQuickKnifeController(packet.ItemId);
                    break;
                }
            case EProceedType.QuickUse:
                {
                    CreateQuickUseItemController(packet.ItemId);
                    break;
                }
            case EProceedType.Weapon:
                {
                    CreateFirearmController(packet.ItemId);
                    break;
                }
            case EProceedType.Knife:
                {
                    CreateKnifeController(packet.ItemId);
                    break;
                }
            case EProceedType.UsableItem:
                {
                    CreateUsableItemController(packet.ItemId);
                    break;
                }
            case EProceedType.Stationary:
                {
                    CreateFirearmController(packet.ItemId, true);
                    break;
                }
        }
    }

    private class ObservedSlotViewHandler : IDisposable
    {
        private readonly Slot _slot;
        private readonly ObservedPlayer _observedPlayer;
        private readonly EquipmentSlot _slotType;

        public ObservedSlotViewHandler(Slot itemSlot, ObservedPlayer player, EquipmentSlot equipmentType)
        {
            _slot = itemSlot;
            _observedPlayer = player;
            _slotType = equipmentType;

            itemSlot.OnAddOrRemoveItem += (System.Action<Item>)HandleItemMove;
        }

        public void Dispose()
        {
            _slot.OnAddOrRemoveItem -= (System.Action<Item>)HandleItemMove;
        }

        private void HandleItemMove(Item item)
        {
            var slotBone = _observedPlayer.PlayerBody.GetSlotBone(_slotType);
            var alternativeHolsterBone = _observedPlayer.PlayerBody.GetAlternativeHolsterBone(_slotType);
            PlayerBody.SlotView newSlotView = new(_observedPlayer.PlayerBody, _slot, slotBone, _slotType,
                    _observedPlayer.Inventory.Equipment.GetSlot(EquipmentSlot.Backpack), alternativeHolsterBone, false);
            var oldSlotView = _observedPlayer.PlayerBody.SlotViews.AddOrReplace(_slotType, newSlotView);
            if (oldSlotView != null)
            {
                ClearSlotView(oldSlotView);
                oldSlotView.Dispose();
            }
            _observedPlayer.PlayerBody.ValidateHoodedDress(_slotType);
            GlobalEventsController.Instance.CreateCommonEvent<ObservedPlayerChangedEquipEvent>().Invoke(_observedPlayer.ProfileId);
            Dispose();
        }

        private void ClearSlotView(PlayerBody.SlotView oldSlotView)
        {
            for (var i = 0; i < oldSlotView.Renderers.Length; i++)
            {
                oldSlotView.Renderers[i].forceRenderingOff = false;
            }
            if (oldSlotView.Dresses != null)
            {
                var dresses = oldSlotView.Dresses;
                for (var j = 0; j < dresses.Length; j++)
                {
                    var bodyRenderer = dresses[j].GetBodyRenderer();
                    for (var k = 0; k < bodyRenderer.Renderers.Length; k++)
                    {
                        bodyRenderer.Renderers[k].forceRenderingOff = false;
                    }
                }
            }
        }
    }

    private void ObservedVisualPass(float deltaTime, int ikUpdateInterval)
    {
        if (CustomAnimationsAreProcessing || !_cullingHandler.IsVisible || !HealthController.IsAlive)
        {
            return;
        }

        _lastDistance = CameraManager.Instance.Distance(Transform.position);
        var isVisibleOrClose = IsVisible && _lastDistance <= EFTHardSettings.Instance.CULL_GROUNDER;

        if (_armsupdated && isVisibleOrClose && !UsedSimplifiedSkeleton)
        {
            ProceduralWeaponAnimation.ProcessEffectors(deltaTime, 2, Motion, Velocity);
            PlayerBones.Offset = ProceduralWeaponAnimation.HandsContainer.WeaponRootAnim.localPosition;
            PlayerBones.DeltaRotation = ProceduralWeaponAnimation.HandsContainer.WeaponRootAnim.localRotation;
        }

        if (isVisibleOrClose && !UsedSimplifiedSkeleton)
        {
            RestoreIKPos();
            ObservedFBBIKUpdate(_lastDistance, ikUpdateInterval);
            MouseLook(false);
            const float num2 = 1f;
            var num4 = GetCurveValue(PlayerAnimator.LEFT_STANCE_CURVE);
            ProceduralWeaponAnimation.SetLeftStanceCurrentCurveValue(num4);
            _rightHand = 1f - (GetCurveValue(PlayerAnimator.RIGHT_HAND_WEIGHT) * num2);
            _leftHand = 1f - (GetCurveValue(PlayerAnimator.LEFT_HAND_WEIGHT) * num2);
            ThirdPersonWeaponRootAuthority = MovementContext.IsInMountedState ? 0f : (GetCurveValue(PlayerAnimator.WEAPON_ROOT_3RD) * num2);
            AdjustUtilityLayerWeight(_lastDistance);
            if (_armsupdated)
            {
                var num5 = ThirdPersonWeaponRootAuthority;
                if (MovementContext.StationaryWeapon != null)
                {
                    num5 = 0f;
                }
                ProceduralWeaponAnimation.SetLeftStanceCurrentCurveValue(num4);
                PlayerBones.ShiftWeaponRoot(deltaTime, EPointOfView.ThirdPerson, num5);
            }
            PlayerBones.RotateHead(0f, ProceduralWeaponAnimation.GetHeadRotation(),
                MovementContext.LeftStanceEnabled && HasFirearmInHands(), num4,
                ProceduralWeaponAnimation.IsAiming);
            HandPosers[0].weight = _leftHand;
            _observedLimbs[0].solver.IKRotationWeight = _observedLimbs[0].solver.IKPositionWeight = _leftHand;
            _observedLimbs[1].solver.IKRotationWeight = _observedLimbs[1].solver.IKPositionWeight = _rightHand;
            IkProcess(_lastDistance);
            AdjustElbows(num2, GetCurveValue(PlayerAnimator.ELBOW_LEFT_WEIGHT), GetCurveValue(PlayerAnimator.ELBOW_RIGHT_WEIGHT));
            IkApply(_lastDistance);
            if (_rightHand < 1f)
            {
                PlayerBones.Kinematics(_observedMarkers[1], _rightHand);
            }
            var num6 = GetCurveValue(PlayerAnimator.AIMING_LAYER_CURVE);
            MovementContext.PlayerAnimator.Animator.SetLayerWeight(6, 1f - num6);
            _prevHeight = Transform.position.y;
        }
        else
        {
            if (!Mathf.Approximately(PlayerBones.AnimatedTransform.localPosition.y, 0f))
            {
                PlayerBones.AnimatedTransform.localPosition = new Vector3(PlayerBones.AnimatedTransform.localPosition.x, 0f, PlayerBones.AnimatedTransform.localPosition.z);
            }
            MouseLook(false);
            var child = PlayerBones.Weapon_Root_Anim.GetChild(0);
            child.localPosition = Vector3.zero;
            child.localRotation = Quaternion.identity;
        }
        if (_lastDistance > EFTHardSettings.Instance.AnimatorCullDistance)
        {
            BodyAnimatorCommon.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            ArmsAnimatorCommon.cullingMode = _shouldCullController ? AnimatorCullingMode.AlwaysAnimate : AnimatorCullingMode.CullUpdateTransforms;
        }
        else
        {
            BodyAnimatorCommon.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            ArmsAnimatorCommon.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }
        if (_armsupdated)
        {
            ProceduralWeaponAnimation.LateTransformations(deltaTime);
            if (HandsController != null)
            {
                HandsController.ManualLateUpdate(deltaTime);
            }
        }
    }

    #region handControllers
    private void CreateHandsController(Func<AbstractHandsController> controllerFactory, Item item)
    {
        CreateHandsControllerHandler handler = new((item != null) ? BeginSetInHands(item) : null);

        handler.SetInHandsOperation?.Confirm(true);

        if (HandsController != null)
        {
            var handsController = HandsController;
            HandsController.FastForwardCurrentState();
            if (HandsController != handsController && HandsController != null)
            {
                HandsController.FastForwardCurrentState();
            }
            HandsController.Destroy();
            if (HandsController != null)
            {
                Destroy(HandsController);
            }
            if (_removeFromHandsCallback != null)
            {
                _removeFromHandsCallback.Invoke(SuccessfulResult.New);
                _removeFromHandsCallback = null;
            }
            HandsController = null;
        }

        SpawnController(controllerFactory(), (System.Action)handler.DisposeHandler);
        ((ISetInHandsHandler)this).OnSetInHands(new(item, CommandStatus.Succeed, InventoryController));
        _shouldCullController = _handsController?.TryCast<EmptyHandsController>() != null
            || _handsController?.TryCast<KnifeController>() != null
            || _handsController?.TryCast<UsableItemController>() != null;
    }

    public void SpawnHandsController(EHandsControllerType controllerType, MongoID itemId, bool isStationary)
    {
        switch (controllerType)
        {
            case EHandsControllerType.Empty:
                CreateEmptyHandsController();
                break;
            case EHandsControllerType.Firearm:
                CreateFirearmController(itemId, isStationary, true);
                break;
            case EHandsControllerType.Meds:
                CreateMedsController(itemId, new(EBodyPart.Head), 0f, 1);
                break;
            case EHandsControllerType.Grenade:
                CreateGrenadeController(itemId);
                break;
            case EHandsControllerType.Knife:
                CreateKnifeController(itemId);
                break;
            case EHandsControllerType.QuickGrenade:
                CreateQuickGrenadeController(itemId);
                break;
            case EHandsControllerType.QuickKnife:
                CreateQuickKnifeController(itemId);
                break;
            case EHandsControllerType.QuickUseItem:
                CreateQuickUseItemController(itemId);
                break;
            case EHandsControllerType.UsableItem:
                CreateUsableItemController(itemId);
                break;
            default:
                FikaGlobals.LogWarning($"ObservedPlayer::SpawnHandsController: Unhandled ControllerType, was {controllerType}");
                break;
        }
    }

    private void CreateEmptyHandsController()
    {
        CreateHandsController(ReturnEmptyHandsController, null);
    }

    private AbstractHandsController ReturnEmptyHandsController()
    {
        return ObservedEmptyHandsController.Create(this);
    }

    private ObservedEmptyHandsController ProceedEmptyHandsController()
    {
        return ObservedEmptyHandsController.Create(this);
    }

    private void CreateFirearmController(MongoID itemId, bool isStationary = false, bool initial = false)
    {
        CreateFirearmControllerHandler handler = new(this);

        if (isStationary)
        {
            if (initial)
            {
                handler.item = Singleton<GameWorld>.Instance.FindStationaryWeaponByItemId(itemId).Item;
                CreateHandsController(handler.ReturnController, handler.item);
                FastForwardToStationaryWeapon(handler.item, MovementContext.Rotation, Transform.rotation, Transform.rotation);
                return;
            }
            handler.item = Singleton<GameWorld>.Instance.FindStationaryWeaponByItemId(itemId).Item;
            CreateHandsController(handler.ReturnController, handler.item);
            return;
        }
        var result = FindItemById(itemId, false, false);
        if (!result.Succeeded)
        {
            FikaGlobals.LogError(result.Error);
            return;
        }
        handler.item = result.Value;
        CreateHandsController(handler.ReturnController, handler.item);
    }

    private void CreateGrenadeController(MongoID itemId)
    {
        CreateGrenadeControllerHandler handler = new(this);

        var result = FindItemById(itemId, false, false);
        if (!result.Succeeded)
        {
            FikaGlobals.LogError(result.Error);
            return;
        }
        handler.Item = result.Value;
        if (handler.Item.TryCast<ThrowWeap>() != null)
        {
            CreateHandsController(handler.ReturnController, handler.Item);
        }
        else
        {
            FikaGlobals.LogError($"CreateGrenadeController: Item was not of type GrenadeClass, was {handler.Item?.GetType()?.FullName ?? "<null>"}!");
        }
    }

    private void CreateMedsController(MongoID itemId, OneAndList<EBodyPart> bodyParts, float amount, int animationVariant)
    {
        var result = FindItemById(itemId, false, false);
        if (!result.Succeeded)
        {
            FikaGlobals.LogError(result.Error);
            return;
        }
        CreateMedsControllerHandler handler = new(this, result.Value, bodyParts, amount, animationVariant);
        CreateHandsController(handler.ReturnController, handler.Item);
    }

    private void CreateKnifeController(MongoID itemId)
    {
        CreateKnifeControllerHandler handler = new(this);
        var result = FindItemById(itemId, false, false);
        if (!result.Succeeded)
        {
            FikaGlobals.LogError(result.Error);
            return;
        }
        handler.Knife = result.Value.GetItemComponent<KnifeComponent>();
        if (handler.Knife != null)
        {
            CreateHandsController(handler.ReturnController, handler.Knife.Item);
        }
        else
        {
            FikaGlobals.LogError($"CreateKnifeController: Item did not contain a KnifeComponent, item type was {handler.Knife?.GetType()?.FullName ?? "<null>"}!");
        }
    }

    private void CreateQuickGrenadeController(MongoID itemId)
    {
        CreateQuickGrenadeControllerHandler handler = new(this);
        var result = FindItemById(itemId, false, false);
        if (!result.Succeeded)
        {
            FikaGlobals.LogError(result.Error);
            return;
        }
        handler.tem = result.Value;
        if (handler.tem.TryCast<ThrowWeap>() != null)
        {
            CreateHandsController(handler.ReturnController, handler.tem);
        }
        else
        {
            FikaGlobals.LogError($"CreateQuickGrenadeController: Item was not of type GrenadeClass, was {handler.tem?.GetType()?.FullName ?? "<null>"}!");
        }
    }

    private void CreateQuickKnifeController(MongoID itemId)
    {
        CreateQuickKnifeControllerHandler handler = new(this);
        var result = FindItemById(itemId, false, false);
        if (!result.Succeeded)
        {
            FikaGlobals.LogError(result.Error);
            return;
        }
        handler.Knife = result.Value.GetItemComponent<KnifeComponent>();
        if (handler.Knife != null)
        {
            CreateHandsController(handler.ReturnController, handler.Knife.Item);
        }
        else
        {
            FikaGlobals.LogError($"CreateQuickKnifeController: Item did not contain a KnifeComponent, item type was {handler.Knife?.GetType()?.FullName ?? "<null>"}!");
        }
    }

    private void CreateUsableItemController(MongoID itemId)
    {
        var result = FindItemById(itemId, false, false);
        if (!result.Succeeded)
        {
            FikaGlobals.LogError(result.Error);
            return;
        }
        CreateUsableItemControllerHandler handler = new(this, result.Value);
        CreateHandsController(handler.ReturnController, handler.Item);
    }

    private void CreateQuickUseItemController(MongoID itemId)
    {
        var result = FindItemById(itemId, false, false);
        if (!result.Succeeded)
        {
            FikaGlobals.LogError(result.Error);
            return;
        }
        CreateQuickUseItemControllerHandler handler = new(this, result.Value);
        CreateHandsController(handler.ReturnController, handler.Item);
    }

    public void SetAggressorData(MongoID? killerId, EBodyPart bodyPart, MongoID? weaponId)
    {
        var killer = killerId is not null ? Singleton<GameWorld>.Instance.GetEverExistedPlayerByProfileId(killerId.ToString()) : null;
        if (killer != null)
        {
            LastAggressor = killer;
        }
        LastBodyPart = bodyPart;
        _lastWeaponId = weaponId;

        if (LastDamageInfo.Weapon == null && _lastWeaponId is not null)
        {
            FindKillerWeapon();
        }

        // The corpse of an observed player is rebuilt from the host's inventory descriptor, so
        // its dog tag is written by the host; 
    }

    private void ObservedFBBIKUpdate(float distance, int ikUpdateInterval)
    {
        _fbbik.solver.iterations = (int)Mathf.Clamp(15f / distance, 0f, 2f);
        if (!_fbbik.solver.Quick && Time.time > TurnOffFbbikAt)
        {
            _fbbik.solver.Quick = true;
        }
        if (!_fbbik.solver.Quick || Time.frameCount % ikUpdateInterval == 0)
        {
            _fbbik.solver.Update();
        }
    }

    private class RemoveHandsControllerHandler(ObservedPlayer fikaPlayer, Callback callback)
    {
        private readonly ObservedPlayer _fikaPlayer = fikaPlayer;
        private readonly Callback _callback = callback;

        public void Handle(Result<IEmptyHandsController> result)
        {
            if (_fikaPlayer._removeFromHandsCallback == _callback)
            {
                _fikaPlayer._removeFromHandsCallback = null;
            }
            _callback.Invoke(result);
        }
    }

    private class CreateHandsControllerHandler(Player.InventoryOperation setInHandsOperation)
    {
        public readonly Player.InventoryOperation SetInHandsOperation = setInHandsOperation;

        internal void DisposeHandler()
        {
            var handler = SetInHandsOperation;
            if (handler == null)
            {
                return;
            }
            handler.Dispose();
        }
    }

    private class CreateFirearmControllerHandler(ObservedPlayer fikaPlayer)
    {
        private readonly ObservedPlayer _fikaPlayer = fikaPlayer;
        public Item item;

        internal AbstractHandsController ReturnController()
        {
            return ObservedFirearmController.Create(_fikaPlayer, item.Cast<Weapon>());
        }
    }

    private class CreateGrenadeControllerHandler(ObservedPlayer fikaPlayer)
    {
        private readonly ObservedPlayer _fikaPlayer = fikaPlayer;
        public Item Item;

        internal AbstractHandsController ReturnController()
        {
            return ObservedGrenadeController.Create(_fikaPlayer, Item.Cast<ThrowWeap>());
        }
    }

    private class CreateMedsControllerHandler(ObservedPlayer fikaPlayer, Item item, OneAndList<EBodyPart> bodyParts, float amount, int animationVariant)
    {
        private readonly ObservedPlayer _fikaPlayer = fikaPlayer;
        public readonly Item Item = item;
        private readonly OneAndList<EBodyPart> _bodyParts = bodyParts;
        private readonly float _amount = amount;
        private readonly int _animationVariant = animationVariant;

        internal AbstractHandsController ReturnController()
        {
            return ObservedMedsController.Create(_fikaPlayer, Item, _bodyParts, _amount, _animationVariant);
        }
    }

    private class CreateKnifeControllerHandler(ObservedPlayer fikaPlayer)
    {
        private readonly ObservedPlayer _fikaPlayer = fikaPlayer;
        public KnifeComponent Knife;

        internal AbstractHandsController ReturnController()
        {
            return ObservedKnifeController.Create(_fikaPlayer, Knife);
        }
    }

    private class CreateQuickGrenadeControllerHandler(ObservedPlayer fikaPlayer)
    {
        private readonly ObservedPlayer _fikaPlayer = fikaPlayer;
        public Item tem;

        internal AbstractHandsController ReturnController()
        {
            return ObservedQuickGrenadeController.Create(_fikaPlayer, tem.Cast<ThrowWeap>());
        }
    }

    private class CreateQuickKnifeControllerHandler(ObservedPlayer fikaPlayer)
    {
        private readonly ObservedPlayer _fikaPlayer = fikaPlayer;
        public KnifeComponent Knife;

        internal AbstractHandsController ReturnController()
        {
            return ObservedQuickKnifeController.Create(_fikaPlayer, Knife);
        }
    }

    private class CreateUsableItemControllerHandler(ObservedPlayer fikaPlayer, Item item)
    {
        private readonly ObservedPlayer _fikaPlayer = fikaPlayer;
        public readonly Item Item = item;

        internal AbstractHandsController ReturnController()
        {
            return UsableItemController.CreateController<UsableItemController>(_fikaPlayer, Item);
        }
    }

    private class CreateQuickUseItemControllerHandler(ObservedPlayer fikaPlayer, Item item)
    {
        private readonly ObservedPlayer _fikaPlayer = fikaPlayer;
        public readonly Item Item = item;

        internal AbstractHandsController ReturnController()
        {
            return ObservedQuickUseItemController.Create(_fikaPlayer, Item);
        }
    }
}

    #endregion
