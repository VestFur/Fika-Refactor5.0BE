// © 2026 Lacyway All Rights Reserved

using System;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Audio.AudioWeatherSystem;
using Comfort.Common;
using CommonAssets.Scripts.Audio;
using Diz.LanguageExtensions;
using EFT;
using EFT.Ballistics;
using EFT.Communications;
using EFT.HealthSystem;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.SynchronizableObjects;
using EFT.Vehicle;
using EFT.CameraControl;
using EFT.Counters;
using EFT.Dialogs;
using EFT.Prestige;
using EFT.Quests;
using EFT.Settings;
using EFT.Settings.Sound;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.ClientClasses.HandsControllers;
using Fika.Core.Main.Components;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.PacketHandlers;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using Fika.Core.Networking.Packets.Communication;
using Fika.Core.Networking.Packets.FirearmController;
using Fika.Core.Networking.Packets.FirearmController.SubPackets;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.Player;
using Fika.Core.Networking.Packets.Player.Common;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;
using Fika.Core.Networking.Packets.World;
using Fika.Core.Networking.Snapshotting;
using Fika.Core.Networking.VOIP;
using HarmonyLib;
using JsonType;
using static Fika.Core.Main.ClientClasses.ClientInventoryController;
using InteractionPacket = Fika.Core.Networking.Packets.Player.Common.SubPackets.InteractionPacket;
using MountingPacket = Fika.Core.Networking.Packets.Player.Common.SubPackets.MountingPacket;

namespace Fika.Core.Main.Players;

/// <summary>
/// <see cref="FikaPlayer"/> is the <see cref="LocalPlayer"/>, there can only be one <see cref="FikaPlayer"/> in every game and that is always yourself.
/// </summary>
public class FikaPlayer : LocalPlayer
{
    public FikaPlayer(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<LocalPlayer>(this);
    }


    public FikaPlayer() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaPlayer>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    #region Fields and Properties
    public IPacketSender PacketSender;
    public FikaQuestController FikaQuests { get; protected set; }
    public float ObservedOverlap;
    public CorpseSyncPackets CorpseSyncPacket;
    public int NetId;
    public bool IsObservedAI;
    public readonly Dictionary<uint, Action<ServerOperationStatus>> OperationCallbacks = [];
    public PlayerSnapshotter<PlayerStateSnapshot> Snapshotter;
    public CommonPlayerPacket CommonPacket;
    public virtual bool LeftStanceDisabled { get; internal set; }
    public DateTime TalkDateTime { get; internal set; }
    /// <summary>
    /// If this player is waiting for callbacks from the host
    /// </summary>
    public bool WaitingForCallback
    {
        get
        {
            if (!_baseInventoryController.StrictSync)
            {
                return false;
            }
            return OperationCallbacks.Count > 0 || _proceedCallbacks.Count > 0;
        }
    }
    public ClientMovementContext ClientMovementContext
    {
        get
        {
            return MovementContext?.TryCast<ClientMovementContext>();
        }
    }
    public Transform SpectateTransform
    {
        get
        {
            return PlayerBones.LootRaycastOrigin;
        }
    }

    public bool Downed
    {
        get;
        internal set
        {
            field = value;
            OnPlayerDownedChanged?.Invoke(this, value);
        }
    }

    public override bool OnHisWayToOperateStationaryWeapon
    {
        get
        {
            if (FikaBackendUtils.IsServer)
            {
                return global::Fika.Core.Main.Utils.NativeBaseCalls.Call90(this);
            }

            return OperationCallbacks.ContainsKey(OperationStationaryCallbackId);
        }
    }

    protected MongoID? _lastWeaponId;
    protected Il2CppSystem.Action[] _armorUnsubcribes = new Il2CppSystem.Action[Inventory.ArmorSlots.Length];

    internal DamageInfo LatestDamageInfo => LastDamageInfo;

    private bool _hasSkilledScav;
    private bool _shouldSendSideEffect;
    private VoipSettings _voipHandler;
    private FikaVOIPController _voipController;
    internal bool IsVoipTalking => _voipController?.IsTalking == true;
    private Bleedout _bleedout;
    internal RevivalStateClock RevivalClock { get; } = new();
    internal bool AwaitingReconnectRevival { get; private set; }
    internal bool HasDied { get; private set; }

    internal RevivalState CaptureRevivalState()
    {
        var state = RevivalClock.Capture();
        if (!AwaitingReconnectRevival)
        {
            state.Downed = Downed;
            state.IsAlive = !HasDied && (Downed || HealthController.IsAlive);
        }
        return state;
    }

    internal void StageReconnectRevival(RevivalState state)
    {
        AwaitingReconnectRevival = true;
        RevivalClock.Restore(state);
        ActiveHealthController?.TryCast<ClientHealthController>()?.RestoreRevivalHistory(state);
    }

    internal void ApplyReconnectRevival()
    {
        if (!AwaitingReconnectRevival) return;
        var state = RevivalClock.Capture();
        var health = ActiveHealthController.Cast<ClientHealthController>();
        health.RestoreRevivalHistory(state);
        if (!state.IsAlive)
        {
            AwaitingReconnectRevival = false;
            health.KillAfterReconnect();
            return;
        }
        if (state.Downed)
        {
            // Run the regular visual/input transition only after PlayerOwner and
            // BattleUI start, then restore the elapsed timer instead of resetting it.
            ToggleDowned(true);
            RevivalClock.Restore(state);
            health.IsAlive = false;
            if (state.RevivePauseRemaining > 0) _bleedout.ShowRevive(state.ReviverNickname);
            else _bleedout.ShowUI();
        }
        AwaitingReconnectRevival = false;
        SendRevivalState();
    }

    internal void AcceptRevival()
    {
        var state = RevivalClock.Capture();
        if (!state.Downed || !state.IsAlive || state.BleedoutRemaining == 0 || HasDied) return;
        if (!IsYourPlayer)
        {
            if (this is ObservedPlayer) ToggleDowned(false);
            return;
        }
        if (AwaitingReconnectRevival)
        {
            RevivalClock.SetDowned(false, 0);
            ActiveHealthController.Cast<ClientHealthController>().RestoreRevivalHistory(RevivalClock.Capture());
            SendRevivalState();
        }
        else if (Downed) ToggleDowned(false);
    }

    private void SendRevivalState()
    {
        CommonPacket.Type = ECommonSubPacketType.DownedSync;
        CommonPacket.SubPacket = DownedSyncPacket.FromValue(CaptureRevivalState());
        PacketSender.NetworkManager.SendNetReusable(ref CommonPacket, DeliveryMethod.ReliableOrdered, true);
    }

    protected EPlayerState _currentState;
    private float _sign;
    private float _turnSoundTimer;

    public ushort OperationStationaryCallbackId;
    private uint _proceedCallbackId;
    private readonly Dictionary<uint, Callback> _proceedCallbacks = [];

    /// <summary>
    /// When each proceed callback was registered.  An answer from the host
    /// that never arrives otherwise keeps <see cref="WaitingForCallback"/>
    /// true for the rest of the raid, and that flag forces
    /// CanPressTrigger/CanStartReload to false even with a working firearm
    /// in hands.
    /// </summary>
    private readonly Dictionary<uint, float> _proceedCallbackTimes = [];
    protected BaseInventoryController _baseInventoryController;

    private static Func<Player, SurfaceSet> _getCurrentSet;
    private static Func<Player, float> _getLastStepTime;
    private static Action<Player, float> _setLastStepTime;
    private static Action<Player, bool> _setPlayedAtLeastOneStep;
    private static Func<Player, bool> _getPlayedAtLeastOneStep;
    private static Func<Player, float> _getSprintSurfaceCheck;
    private static Func<Player, float> _getRunSurfaceCheck;
    private static Func<Player, IPlayerStepAudioController> _getSpecificStepAudioController;
    #endregion

    /// <summary>
    /// Invoked when a player spawns and is ready
    /// </summary>
    public static Action<FikaPlayer> OnPlayerSpawned { get; set; }
    /// <summary>
    /// Invoked when a player is destroyed with <see cref="OnDestroy"/>
    /// </summary>
    public static Action<FikaPlayer> OnPlayerDestroyed { get; set; }
    /// <summary>
    /// Invoked when a player is killed
    /// </summary>
    public static Action<FikaPlayer> OnPlayerDeath { get; set; }
    /// <summary>
    /// Invoked when a player down state changes
    /// </summary>
    public static Action<FikaPlayer, bool> OnPlayerDownedChanged { get; set; }

    public static async Task<FikaPlayer> Create(GameWorld gameWorld, int playerId, Vector3 position,
        Quaternion rotation, string layerName, string prefix, EPointOfView pointOfView, Profile profile,
        bool aiControl, EUpdateQueue updateQueue, EUpdateMode armsUpdateMode, EUpdateMode bodyUpdateMode,
        CharacterControllerSpawner.Mode characterControllerMode, Func<float> getSensitivity,
        Func<float> getAimingSensitivity, IStatisticsManager statisticsManager, ICustomizationFilter filter, IEftSession session,
        int netId, bool strictSync, ReconnectInventoryState reconnectInventory = null)
    {
        var useSimpleAnimator = profile.Info.Settings.UseSimpleAnimator;
        var resourceKey = useSimpleAnimator ? InGameBundles.ZOMBIE_BUNDLE_NAME : InGameBundles.PLAYER_BUNDLE_NAME;
        var player = Create<FikaPlayer>(gameWorld, resourceKey, playerId, position, updateQueue, armsUpdateMode,
                    bodyUpdateMode, characterControllerMode, getSensitivity, getAimingSensitivity, prefix, false, useSimpleAnimator);

        player.IsYourPlayer = true;
        player.ResetDisposeState();
        player.NetId = netId;
        player._voipHandler = VoipSettings.Default;
        player.CommonPacket = new()
        {
            NetId = netId
        };

        PlayerOwnerInventoryController inventoryController = FikaBackendUtils.IsServer
            ? new HostInventoryController(player, profile, false, strictSync)
            : new ClientInventoryController(player, profile, false, strictSync, reconnectInventory);

        player._baseInventoryController = inventoryController.TryCast<BaseInventoryController>()
            ?? throw new InvalidOperationException("Fika inventory controller was not a BaseInventoryController");

        if (strictSync)
        {
            FikaGlobals.LogInfo("Using strict inventory sync");
        }
        else
        {
            FikaGlobals.LogWarning("Strict inventory sync disable - limited/no support will be given for this raid");
        }

        ClientQuestController questController;
        if (FikaPlugin.Instance.Settings.SharedQuestProgression)
        {
            questController = new ClientSharedQuestController(profile, inventoryController, inventoryController.PlayerSearchController, session, player);
        }
        else
        {
            questController = new ClientQuestController(profile, inventoryController, inventoryController.PlayerSearchController, session, player);
        }
        player.FikaQuests = questController;
        questController.Init();
        AchievementsControllerClientBackend achievementsController = new(profile, inventoryController, questController.Native, session, true);
        achievementsController.Init();
        achievementsController.AchievementUnlocked += (Il2CppSystem.Action<string>)player.UnlockAchievement;
        achievementsController.Run();
        questController.Run();
        PrestigeControllerClientGame prestigeController = new(profile, inventoryController, questController.Quests, session);
        ClientDialogController dialogController = new(profile, questController.Native, inventoryController);

        if (FikaBackendUtils.IsServer)
        {
            player.PacketSender = await ServerPacketSender.Create(player);
        }
        else if (FikaBackendUtils.IsClient)
        {
            player.PacketSender = await ClientPacketSender.Create(player);
        }

        var voipState = !FikaBackendUtils.IsHeadless && FikaSingleton<IFikaNetworkManager>.Instance.AllowVOIP
            ? (SoundSettingsGroup.CheckMicrophone() ? EVoipState.Available : EVoipState.MicrophoneFail)
            : EVoipState.NotAvailable;

        var questLocationObjects = new QuestLocationObjectsController(player, questController.Native, new Il2CppSystem.Collections.Generic.List<QuestLocationObject>());
        FikaPlugin.Instance.FikaLogger.LogDebug($"Player.Init entry: thread={System.Environment.CurrentManagedThreadId}, nativeContext={Il2CppSystem.Threading.SynchronizationContext.Current?.GetIl2CppType().FullName}");
        // SPT 5 assigns RaidId during Init, not the generic component factory.
        // Reuse the server-assigned Fika ID so native lookups agree on all peers.
        await player.Init(rotation, layerName, pointOfView, netId, profile, inventoryController,
            new ClientHealthController(profile.Health, player, inventoryController, profile.Skills, aiControl),
            statisticsManager, questController.Native, questLocationObjects, achievementsController, prestigeController, dialogController, filter,
            voipState, false, false, null).AsManaged();

#if DEBUG
        profile.Skills.SurgerySpeed.Value = 5f;
#endif

        foreach (var item in player.Inventory.GetPlayerItems(EPlayerItems.NonQuestItems))
        {
            var magazine = item.TryCast<Magazine>();
            if (magazine != null)
                player.InventoryController.StrictCheckMagazine(magazine, true, player.Profile.MagDrillsMastering, false, false);
        }

        var services = player._notYetPurchasedTraderServiceTypes;
        foreach (var etraderServiceType in Singleton<GlobalConfiguration>.Instance.ServicesData.Keys)
        {
            services.Add(etraderServiceType);
        }

        player._handsController = EmptyHandsController.CreateController<EmptyHandsController>(player);
        player._handsController.Spawn(1f, (Il2CppSystem.Action)FikaGlobals.EmptyAction);
        player.AIData = new AIData(null, player);
        player.AggressorFound = false;
        player._animators[0].enabled = true;

        var radioTransmitterRecodableComponent = player.FindRadioTransmitter();
        if (radioTransmitterRecodableComponent != null)
        {
            //Todo: (Archangel) method_131 refers to 'singlePlayerInventoryController_0' which is null in our case
            //radioTransmitterRecodableComponent.OnRadioTransmitterStatusChanged += player.method_131;

            if (player.Profile.GetTraderStanding("638f541a29ffd1183d187f57").IsZero())
            {
                radioTransmitterRecodableComponent.SetEncoded(false);
            }
        }

        player.SubscribeToArmorChangeEvent();
        player.RecalculateEquippedArmorComponents(null);

        player.Profile.Info.SetProfileNickname(FikaBackendUtils.PMCName ?? profile.Nickname);

        OnPlayerSpawned?.Invoke(player);

        return player;
    }

    private uint GetNextAvailableId()
    {
        while (_proceedCallbacks.ContainsKey(++_proceedCallbackId))
        {

        }
        return _proceedCallbackId;
    }

    private uint CreateProceedCallback(Action<bool> confirmAction)
    {
        var id = GetNextAvailableId();
        var handler = new ProceedCallbackHandler(confirmAction);
        _proceedCallbacks[id] = (Callback)handler.Handle;
        _proceedCallbackTimes[id] = Time.time;
#if DEBUG
        FikaGlobals.LogWarning($"Got callback {id} for proceed callback");
#endif
        return id;
    }

    internal void HandleCallbackResponse(uint callbackId, string error)
    {
        var success = string.IsNullOrWhiteSpace(error);
        if (!success)
        {
            FikaGlobals.LogError($"Could not execute callback with id {callbackId} on the server: {error}");
        }

        if (!_proceedCallbacks.TryGetValue(callbackId, out var callback))
        {
            // The callback is gone when the hands watchdog expired a proceed
            // the host answered far too late.
            FikaGlobals.LogError($"Could not get callback with id {callbackId}");
            return;
        }

        _proceedCallbacks.Remove(callbackId);
        _proceedCallbackTimes.Remove(callbackId);

#if DEBUG
        FikaGlobals.LogInfo($"Callback was success: {success}");
#endif

        if (success)
        {
            callback.Succeed();
        }
        else
        {
            callback.Fail(error);
        }
    }

    /// <summary>
    /// How many hands switches are still waiting for an answer from the host.
    /// </summary>
    internal int PendingProceedCallbacks => _proceedCallbacks.Count;

    /// <summary>
    /// Drops the proceed callbacks whose answer never arrived.  While any of
    /// them is registered <see cref="WaitingForCallback"/> stays true and
    /// FikaClientFirearmController refuses to press the trigger or start a
    /// reload, so a lost packet during a fast hands switch would otherwise
    /// leave the client unable to fire for the rest of the raid.  This is
    /// only called by the hands watchdog, after the switch it belongs to has
    /// already been given up on.
    /// </summary>
    internal int ExpireStaleProceedCallbacks(float maxAgeSeconds)
    {
        if (_proceedCallbacks.Count == 0)
        {
            return 0;
        }

        var now = Time.time;
        List<uint> expired = null;
        foreach (var pair in _proceedCallbacks)
        {
            if (_proceedCallbackTimes.TryGetValue(pair.Key, out var registeredAt)
                && now - registeredAt < maxAgeSeconds)
            {
                continue;
            }

            (expired ??= []).Add(pair.Key);
        }

        if (expired == null)
        {
            return 0;
        }

        foreach (var id in expired)
        {
            _proceedCallbacks.Remove(id);
            _proceedCallbackTimes.Remove(id);
        }

        return expired.Count;
    }

    private void AssignGetCurrentSet()
    {
        _getCurrentSet = FikaGlobals.CreateGetter<Player, SurfaceSet>("_currentSet");

        _getLastStepTime = FikaGlobals.CreateGetter<Player, float>("_lastStepTime");
        _setLastStepTime = FikaGlobals.CreateSetter<Player, float>("_lastStepTime");

        _getPlayedAtLeastOneStep = FikaGlobals.CreateGetter<Player, bool>("_playedAtLeastOneStep");
        _setPlayedAtLeastOneStep = FikaGlobals.CreateSetter<Player, bool>("_playedAtLeastOneStep");

        _getSprintSurfaceCheck = FikaGlobals.CreateGetter<Player, float>("_sprintSurfaceCheck");

        _getRunSurfaceCheck = FikaGlobals.CreateGetter<Player, float>("_runSurfaceCheck");

        _getSpecificStepAudioController = FikaGlobals.CreateGetter<Player, IPlayerStepAudioController>("_specificStepAudioController");
    }

    public override void InitAudioController()
    {
        FikaPlugin.Instance.FikaLogger.LogDebug($"Player.InitAudioController entry: thread={System.Environment.CurrentManagedThreadId}, nativeContext={Il2CppSystem.Threading.SynchronizationContext.Current?.GetIl2CppType().FullName}");
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call91(this);
        SetupFikaAudio();
        FikaPlugin.Instance.FikaLogger.LogDebug("Player.InitAudioController completed");
    }

    protected void SetupFikaAudio()
    {
        if (_getCurrentSet == null)
        {
            AssignGetCurrentSet();
        }

        MovementContext.OnStateChanged -= (MovementContext.OnStateChangeDelegate)StateChangedHandler;
        MovementContext.OnStateChanged += (MovementContext.OnStateChangeDelegate)MovementContext_OnStateChanged;

        if (_idleCoroutine != null)
        {
            StopCoroutine(_idleCoroutine);
            _idleCoroutine = null;
        }
    }

    protected void MovementContext_OnStateChanged(EPlayerState previousState, EPlayerState nextState)
    {
        PlayPrewarmSound(NestedStepSoundSource);

        switch (previousState)
        {
            case EPlayerState.Sprint:
                _setPlayedAtLeastOneStep(this, false);
                if (!_getPlayedAtLeastOneStep(this) && CheckSurface(_getSprintSurfaceCheck(this)))
                {
                    DefaultPlay(_getCurrentSet(this).SprintSoundBank, 1f, EAudioMovementState.Sprint);
                }

                if (nextState == EPlayerState.Transition || nextState == EPlayerState.Idle)
                {
                    var volumeMod = FirstPersonPointOfView ? _getCurrentSet(this).StopSoundBank.BaseVolume : 1f;
                    DefaultPlay(_getCurrentSet(this).StopSoundBank,
                        volumeMod * MovementContext.CovertMovementVolume, EAudioMovementState.Stop);
                }
                break;

            case EPlayerState.Run:
            case EPlayerState.MoveZombieState:
            case EPlayerState.StartMoveZombieState:
            case EPlayerState.EndMoveZombieState:
                _setPlayedAtLeastOneStep(this, false);
                if (!_getPlayedAtLeastOneStep(this) && SinceLastStep > 0.66f)
                {
                    if (CheckSurface(_getRunSurfaceCheck(this)))
                    {
                        PlayStepSound();
                    }
                    _setLastStepTime(this, Time.time);
                }
                break;
        }

        if (nextState == EPlayerState.Jump)
        {
            DefaultPlay(_getCurrentSet(this).JumpSoundBank, 1f, EAudioMovementState.Jump);
            PlayGearSound(MovementContext.CovertEquipmentNoise, true);
        }
        else if (nextState == EPlayerState.Prone2Stand)
        {
            PlayGearSound(0.7f, true);
        }
        else if (nextState == EPlayerState.Transit2Prone)
        {
            var moveState = (previousState == EPlayerState.Sprint) ? EAudioMovementState.Drop : EAudioMovementState.None;
            var finalVolume = 0.7f * MovementContext.CovertMovementVolume;

            if (previousState == EPlayerState.Sprint)
            {
                DefaultPlay(_getCurrentSet(this).ProneDropSoundBank, finalVolume, moveState);
            }
            else
            {
                PlayGearSound(finalVolume, true);
            }
        }

        _currentState = nextState;

        if (CurrentState.Name != _currentState)
        {
            _setPlayedAtLeastOneStep(this, false);
        }
    }

    public override void ManualUpdate(float deltaTime, Il2CppSystem.Nullable<float> platformDeltaTime = default, int loop = 1)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call92(this, deltaTime, platformDeltaTime, loop);
        if (HealthController.IsAlive)
        {
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

    protected void TickSprintState()
    {
        if (CurrentState.Name != _currentState)
        {
            return;
        }

        var currentSignValue = LeftStanceCurrentCurveValue;
        if (Math.Abs(_sign - currentSignValue) >= 1E-45f)
        {
            _sign = currentSignValue;
            var elapsed = Time.time - _getLastStepTime(this);

            if (elapsed > 0.2f && MovementContext.FreefallTime < 0.6f)
            {
                _setPlayedAtLeastOneStep(this, true);
                _setLastStepTime(this, Time.time);
                UpdateSourcePriority(NestedStepSoundSource);

                if (CheckSurface(_getSprintSurfaceCheck(this)))
                {
                    UpdateMuffledState();
                    var sprintBank = _getCurrentSet(this).SprintSoundBank;

                    var volumeBase = FirstPersonPointOfView ? sprintBank.BaseVolume : 1f;
                    var weightMod = 0.5f + (3f * Physical.Overweight);
                    var finalVolume = CalculateMovementVolumeDefaultMultiplier(EAudioMovementState.Sprint) * volumeBase * weightMod;

                    UpdateStepSourceRolloff(EAudioMovementState.Sprint, false);

                    sprintBank.Play(NestedStepSoundSource, EnvironmentType.Outdoor, Distance,
                        finalVolume, Distance, FirstPersonPointOfView, true);
                    _getSpecificStepAudioController(this).Play(EAudioMovementState.Sprint, Environment,
                        Distance, finalVolume, Distance, FirstPersonPointOfView);

                    PlayGearSound(1f, false);

                    if (elapsed < 1.2f && FirstPersonPointOfView)
                    {
                        ProceduralWeaponAnimation.Walk.StepFrequency = 0.5f / Mathf.Clamp(elapsed, 0.3f, 0.8f);
                    }
                }
            }
        }
    }

    protected void TickRunState()
    {
        if (CurrentState.Name != _currentState)
        {
            return;
        }

        var single_ = LeftStanceCurrentCurveValue;
        if (Math.Abs(_sign - single_) >= 1E-45f)
        {
            _sign = single_;
            var sinceLastStep = SinceLastStep;
            if (sinceLastStep > 0.2f && MovementContext.FreefallTime < 1f)
            {
                _setLastStepTime(this, Time.time);
                _setPlayedAtLeastOneStep(this, true);

                if (CheckSurface(_getRunSurfaceCheck(this)))
                {
                    if (sinceLastStep < 1.2f && FirstPersonPointOfView)
                    {
                        var clampRange = Mathf.Clamp(sinceLastStep,
                            0.7f - (MovementContext.SmoothedCharacterMovementSpeed / 2f), 1.2f);
                        ProceduralWeaponAnimation.Walk.StepFrequency = 0.5f / clampRange;
                    }
                    PlayStepSound();
                }
            }
        }
    }

    protected void TickIdleState()
    {
        if (CurrentState.Name != _currentState)
        {
            return;
        }

        var angleMagnitude = Math.Abs(HandsToBodyAngle);
        if (angleMagnitude > EFTHardSettings.Instance.TURN_ANGLE)
        {
            if (Time.time >= _turnSoundTimer)
            {
                PlayTurnSound(angleMagnitude);
                _turnSoundTimer = Time.time + EFTHardSettings.Instance.TURN_SOUND_DELAY;
            }
        }
        else
        {
            _turnSoundTimer = 0f;
        }
    }

    protected void SubscribeToArmorChangeEvent()
    {
        for (var i = 0; i < Inventory.ArmorSlots.Length; i++)
        {
            var slotType = Inventory.ArmorSlots[i];
            var slot = Inventory.Equipment.GetSlot(slotType);

            _armorUnsubcribes[i] = slot.ReactiveContainedItem
                .Subscribe((Il2CppSystem.Action<Item>)RecalculateEquippedArmorComponents); // Subscribe does not invoke on registration.
        }
    }

    /// <summary>
    /// Recalculates all equipped <see cref="ArmorComponent"/>s when an armor slot changes
    /// </summary>
    /// <param name="item">The item changed</param>
    protected void RecalculateEquippedArmorComponents(Item item)
    {
        _preAllocatedArmorComponents.Clear();
        Inventory.GetPutOnArmorsNonAlloc(_preAllocatedArmorComponents);
        RecalculateEquipmentParams();
    }

    public void AbuseNotification(string reporterId)
    {
        if (IsYourPlayer)
        {
            _voipController?.ReceiveAbuseNotification(reporterId);
        }
    }

    private void UnlockAchievement(string tpl)
    {
        _achievementsController.UnlockAchievementForced(tpl);
    }

    public virtual void ToggleDowned(bool downed)
    {
        if (!IsYourPlayer || HasDied)
        {
            return;
        }

        var clientHealthController = _healthController?.TryCast<ClientHealthController>()
            ?? throw new InvalidOperationException("Local player's health controller is not a ClientHealthController");
        var currentState = clientHealthController.Downed;
        if (currentState != !downed)
        {
            FikaGlobals.LogWarning($"Trying to set downed to {downed} but it is already {currentState}");
            return;
        }

        Downed = downed;
        RevivalClock.SetDowned(downed, clientHealthController.BleedoutTime);

        if (downed)
        {
            Speaker.Play(EPhraseTrigger.OnAgony, HealthStatus, true, new Il2CppSystem.Nullable<int>());
            ActiveHealthController.SetDamageCoeff(0f);
            ActiveHealthController.PauseAllEffects();
            ActiveHealthController.DisableMetabolism();
            MovementContext.IsInPronePose = true;
            HandsController.FastForwardCurrentState();
            TrySaveLastItemInHands();
            Proceed(false, null);
            CharacterController.Cast<SimpleCharacterController>().IsMoveIgnored = true;
            MovementContext.IsAxesIgnored = true;
            clientHealthController.Downed = true;
            if (_bleedout != null)
            {
                Destroy(_bleedout);
            }
            _bleedout = gameObject.AddComponent<Bleedout>();
            _bleedout.Init(clientHealthController);
            _bleedout.ShowUI();

            var camera = CameraManager.Instance.Camera;
            var deathEffect = camera.GetComponent<DeathFade>();
            if (deathEffect != null)
            {
                deathEffect.enabled = true;
                deathEffect.EnableEffect();
            }
            var fastBlur = camera.GetComponent<FastBlur>();
            if (fastBlur != null)
            {
                fastBlur.enabled = true;
                fastBlur.Die();
            }
        }
        else
        {
            ActiveHealthController.SetDamageCoeff(1f);
            ActiveHealthController.UnpauseAllEffects();
            ActiveHealthController.IsAlive = true;
            TrySetLastEquippedWeapon(true);
            CharacterController.Cast<SimpleCharacterController>().IsMoveIgnored = false;
            MovementContext.IsAxesIgnored = false;
            clientHealthController.EnableMetabolism();
            clientHealthController.Revive();
            UpdateSpeedLimitByHealth();
            if (_bleedout != null)
            {
                _bleedout.HideUI();
                Destroy(_bleedout);
            }

            var camera = CameraManager.Instance.Camera;
            var deathEffect = camera.GetComponent<DeathFade>();
            if (deathEffect != null)
            {
                deathEffect.enabled = true;
                deathEffect.DisableEffect();
            }
            var fastBlur = camera.GetComponent<FastBlur>();
            if (fastBlur != null)
            {
                fastBlur.Reset();
                fastBlur.enabled = false;
            }
        }

        if (!AwaitingReconnectRevival) SendRevivalState();
    }

    public virtual void ToggleRevive(bool reviving, string nickname)
    {
        RevivalClock.SetReviving(reviving, nickname, FikaPlugin.Instance.Settings.ReviveConfig.ReviveTime);
        if (AwaitingReconnectRevival) return;
#if DEBUG
        FikaGlobals.LogInfo($"Being revived by {nickname}");
#endif
        if (_bleedout == null)
        {
            FikaGlobals.LogError("Bleedout component was null when attempting to set revive notification");
            return;
        }

        if (reviving)
        {
            _bleedout.ShowRevive(nickname);
            return;
        }

        _bleedout.HideRevive();
        _bleedout.ShowUI();
    }

    public override void InitVoip(EVoipState voipState)
    {
        _voipController?.Dispose();
        _voipController = null;
        if (_voipHandler.VoipEnabled && voipState != EVoipState.NotAvailable)
        {
            var settings = Singleton<SettingsManager>.Instance.Sound.Settings;
            if (!settings.VoipEnabled)
            {
                voipState = EVoipState.Off;
            }
            if (!_voipHandler.MicrophoneChecked)
            {
                voipState = EVoipState.MicrophoneFail;
            }
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call93(this, voipState);
            _voipController = new(this, settings);
            VoipController = _voipController;
        }
    }

    public override void CreateNestedSource()
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call94(this);
        NestedStepSoundSource.SetBaseVolume(0.9f);
    }

    public override PhysicalBase CreatePhysical()
    {
        return new Physical();
    }

    public override void CreateMovementContext()
    {
        var movement_MASK = EFTHardSettings.Instance.MOVEMENT_MASK;
        if (FikaPlugin.Instance.Settings.UseInertia)
        {
            MovementContext = ClientMovementContext.Create(this, GetBodyAnimatorCommon,
                GetCharacterControllerCommon, movement_MASK);
            return;
        }
        MovementContext = NoInertiaMovementContext.Create(this, GetBodyAnimatorCommon,
                GetCharacterControllerCommon, movement_MASK);
    }

    public override void OnSkillLevelChanged(BaseSkill skill)
    {
        NotificationManager.DisplayNotification(new SkillLevelUpNotification(skill));
    }

    public override void SendVoiceMuffledState(bool isMuffled)
    {
        FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.MuffledState,
                MuffledState.FromValue(NetId, isMuffled), true);
    }

    public override void OnWeaponMastered(Mastering masterSkill)
    {
        FikaNotifications.DisplayMessageNotification(string.Format("MasteringLevelUpMessage".Localized(null),
            masterSkill.MasteringGroup.Id.Localized(null),
            masterSkill.Level.ToString()), ENotificationDurationType.Default, ENotificationIconType.Default, null);
    }

    public override void ApplyDamageInfo(DamageInfo damageInfo, EBodyPart bodyPartType, EBodyPartColliderType colliderType, float absorbed)
    {
        if (IsYourPlayer && damageInfo.Player != null
            && !FikaPlugin.Instance.Settings.FriendlyFire
            && damageInfo.Player.iPlayer.IsCoopPlayer())
        {
            return;
        }

        if (damageInfo.Weapon != null)
        {
            _lastWeaponId = damageInfo.Weapon.Id;
        }

        global::Fika.Core.Main.Utils.NativeBaseCalls.Call95(this, damageInfo, bodyPartType, colliderType, absorbed);
    }

    public override PlayerHitInfo ApplyShot(DamageInfo damageInfo, EBodyPart bodyPartType, EBodyPartColliderType colliderType, EArmorPlateCollider armorPlateCollider, ShotId shotId)
    {
        if (damageInfo.DamageType is EDamageType.Sniper or EDamageType.Landmine)
        {
            return SimulatedApplyShot(damageInfo, bodyPartType, colliderType, armorPlateCollider);
        }

        if (damageInfo.Player != null && damageInfo.Player.IsAI)
        {
            if (damageInfo.Weapon != null)
            {
                _lastWeaponId = damageInfo.Weapon.Id;
            }
            return SimulatedApplyShot(damageInfo, bodyPartType, colliderType, armorPlateCollider);
        }

        return null;
    }

    private PlayerHitInfo SimulatedApplyShot(DamageInfo damageInfo, EBodyPart bodyPartType, EBodyPartColliderType colliderType, EArmorPlateCollider armorPlateCollider)
    {
        var activeHealthController = ActiveHealthController;
        if (activeHealthController != null && !activeHealthController.IsAlive)
        {
            return null;
        }
        var flag = NativeNullable<MongoID>.Read(damageInfo, "DeflectedBy").HasValue;
        var damage = damageInfo.Damage;
        var list = ProceedDamageThroughArmor(ref damageInfo, colliderType, armorPlateCollider, true);
        ProceedArmorDamaged(list);
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
        ApplyDamageInfo(damageInfo, bodyPartType, colliderType, 0f);
        ShotReactions(damageInfo, bodyPartType);
        ReceiveDamage(damageInfo.Damage, bodyPartType, damageInfo.DamageType, num, hitInfo.Material);

        return hitInfo;
    }

    #region Proceed
    public override void Proceed(bool withNetwork, Callback<IEmptyHandsController> callback, bool scheduled = true)
    {
        var handler = new EmptyHandsControllerHandler(this, scheduled);
        Il2CppSystem.Func<EmptyHandsController> func = (Il2CppSystem.Func<EmptyHandsController>)handler.ReturnController;
        handler.Process = new Process<EmptyHandsController, IEmptyHandsController>(this, func, null);
        if (withNetwork && FikaBackendUtils.IsClient && _baseInventoryController.StrictSync)
        {
            handler.ConfirmCallback = new(handler.SendCallbackRequest);
        }
        else
        {
            handler.ConfirmCallback = new(handler.SendPacket);
        }
        handler.Process.Proceed((Callback)handler.HandleResult, callback, scheduled);
    }

    public override void Proceed(FoodDrink foodDrink, float amount, Callback<IMedsController> callback, int animationVariant, bool scheduled = true)
    {
        EFT.NetworkPackets.OneAndList<EBodyPart> bodyparts = new(EBodyPart.Head);
        FoodControllerHandler handler = new(this, foodDrink, amount, bodyparts, animationVariant);
        Il2CppSystem.Func<MedsController> func = (Il2CppSystem.Func<MedsController>)handler.ReturnController;

        if (FikaBackendUtils.IsClient && _baseInventoryController.StrictSync)
        {
            handler.Process = new(this, func, foodDrink, false, confirmation: AbstractProcess.Confirmation.Unknown);
            handler.ConfirmCallback = new(handler.SendCallbackRequest);
        }
        else
        {
            handler.Process = new(this, func, foodDrink, false);
            handler.ConfirmCallback = new(handler.SendPacket);
        }
        handler.Process.Proceed((Callback)handler.HandleResult, callback, scheduled);
    }

    public override void Proceed(Meds meds, EFT.NetworkPackets.OneAndList<EBodyPart> bodyParts, Callback<IMedsController> callback, int animationVariant, bool scheduled = true)
    {
        MedsControllerHandler handler = new(this, meds, bodyParts, animationVariant);

        Il2CppSystem.Func<MedsController> func = (Il2CppSystem.Func<MedsController>)handler.ReturnController;

        if (FikaBackendUtils.IsClient && _baseInventoryController.StrictSync)
        {
            handler.Process = new(this, func, meds, false, confirmation: AbstractProcess.Confirmation.Unknown);
            handler.ConfirmCallback = new(handler.SendCallbackRequest);
        }
        else
        {
            handler.Process = new(this, func, meds, false);
            handler.ConfirmCallback = new(handler.SendPacket);
        }
        handler.Process.Proceed((Callback)handler.HandleResult, callback, scheduled);
    }

    public override void Proceed<T>(Item item, Callback<IUsableItemController> callback, bool scheduled = true)
    {
        if (item.TryCast<PortableRangeFinder>() != null)
        {
            PortableRangeFinderControllerHandler rangeFinderHandler = new(this, item);

            Il2CppSystem.Func<PortableRangeFinderController> rangeFinderFunc = (Il2CppSystem.Func<PortableRangeFinderController>)rangeFinderHandler.ReturnController;

            if (FikaBackendUtils.IsClient && _baseInventoryController.StrictSync)
            {
                rangeFinderHandler.Process = new(this, rangeFinderFunc, item, false, confirmation: AbstractProcess.Confirmation.Unknown);
                rangeFinderHandler.ConfirmCallback = new(rangeFinderHandler.SendCallbackRequest);
            }
            else
            {
                rangeFinderHandler.Process = new(this, rangeFinderFunc, item, false);
                rangeFinderHandler.ConfirmCallback = new(rangeFinderHandler.SendPacket);
            }
            rangeFinderHandler.Process.Proceed((Callback)rangeFinderHandler.HandleResult, callback, scheduled);
            return;
        }

        UsableItemControllerHandler handler = new(this, item);

        Il2CppSystem.Func<UsableItemController> func = (Il2CppSystem.Func<UsableItemController>)handler.ReturnController;

        if (FikaBackendUtils.IsClient && _baseInventoryController.StrictSync)
        {
            handler.Process = new(this, func, item, false, confirmation: AbstractProcess.Confirmation.Unknown);
            handler.ConfirmCallback = new(handler.SendCallbackRequest);
        }
        else
        {
            handler.Process = new(this, func, item, false);
            handler.ConfirmCallback = new(handler.SendPacket);
        }
        handler.Process.Proceed((Callback)handler.HandleResult, callback, scheduled);
    }

    public override void Proceed(Item item, Callback<IQuickUseItem> callback, bool scheduled = true)
    {
        QuickUseItemControllerHandler handler = new(this, item);

        Il2CppSystem.Func<QuickUseItemController> func = (Il2CppSystem.Func<QuickUseItemController>)handler.ReturnController;

        if (FikaBackendUtils.IsClient && _baseInventoryController.StrictSync)
        {
            handler.Process = new(this, func, item, true, confirmation: AbstractProcess.Confirmation.Unknown);
            handler.ConfirmCallback = new(handler.SendCallbackRequest);
        }
        else
        {
            handler.Process = new(this, func, item, true);
            handler.ConfirmCallback = new(handler.SendPacket);
        }
        handler.Process.Proceed((Callback)handler.HandleResult, callback, scheduled);
    }

    public override void Proceed(KnifeComponent knife, Callback<IKnifeController> callback, bool scheduled = true)
    {
        KnifeControllerHandler handler = new(this, knife);

        Il2CppSystem.Func<KnifeController> func = (Il2CppSystem.Func<KnifeController>)handler.ReturnController;

        if (FikaBackendUtils.IsClient && _baseInventoryController.StrictSync)
        {
            handler.Process = new(this, func, handler.Knife.Item, false, confirmation: AbstractProcess.Confirmation.Unknown);
            handler.ConfirmCallback = new(handler.SendCallbackRequest);
        }
        else
        {
            handler.Process = new(this, func, handler.Knife.Item, false);
            handler.ConfirmCallback = new(handler.SendPacket);
        }
        handler.Process.Proceed((Callback)handler.HandleResult, callback, scheduled);
    }

    public override void Proceed(KnifeComponent knife, Callback<IQuickKnifeKickController> callback, bool scheduled = true)
    {
        QuickKnifeControllerHandler handler = new(this, knife);

        Il2CppSystem.Func<QuickKnifeKickController> func = (Il2CppSystem.Func<QuickKnifeKickController>)handler.ReturnController;

        if (FikaBackendUtils.IsClient && _baseInventoryController.StrictSync)
        {
            handler.Process = new(this, func, handler.Knife.Item, true, confirmation: AbstractProcess.Confirmation.Unknown);
            handler.ConfirmCallback = new(handler.SendCallbackRequest);
        }
        else
        {
            handler.Process = new(this, func, handler.Knife.Item, true);
            handler.ConfirmCallback = new(handler.SendPacket);
        }
        handler.Process.Proceed((Callback)handler.HandleResult, callback, scheduled);
    }

    public override void Proceed(ThrowWeap throwWeap, Callback<IQuickGrenadeThrowController> callback, bool scheduled = true)
    {
        QuickGrenadeControllerHandler handler = new(this, throwWeap);

        Il2CppSystem.Func<QuickGrenadeThrowHandsController> func = (Il2CppSystem.Func<QuickGrenadeThrowHandsController>)handler.ReturnController;

        if (FikaBackendUtils.IsClient && _baseInventoryController.StrictSync)
        {
            handler.Process = new(this, func, throwWeap, false, confirmation: AbstractProcess.Confirmation.Unknown);
            handler.ConfirmCallback = new(handler.SendCallbackRequest);
        }
        else
        {
            handler.Process = new(this, func, throwWeap, false);
            handler.ConfirmCallback = new(handler.SendPacket);
        }
        handler.Process.Proceed((Callback)handler.HandleResult, callback, scheduled);
    }

    public override void Proceed(ThrowWeap throwWeap, Callback<IGrenadeController> callback, bool scheduled = true)
    {
        GrenadeControllerHandler handler = new(this, throwWeap);

        Il2CppSystem.Func<GrenadeHandsController> func = (Il2CppSystem.Func<GrenadeHandsController>)handler.ReturnController;

        if (FikaBackendUtils.IsClient && _baseInventoryController.StrictSync)
        {
            handler.Process = new(this, func, throwWeap, false, confirmation: AbstractProcess.Confirmation.Unknown);
            handler.ConfirmCallback = new(handler.SendCallbackRequest);
        }
        else
        {
            handler.Process = new(this, func, throwWeap, false);
            handler.ConfirmCallback = new(handler.SendPacket);
        }
        handler.Process.Proceed((Callback)handler.HandleResult, callback, scheduled);
    }

    internal static bool CanFastSwitchWeapon(AbstractHandsController hands, Weapon weapon)
    {
        // Native fields can return an AbstractHandsController wrapper even when
        // the object is a firearm. CLR type tests do not inspect its native class.
        return hands?.TryCast<FirearmController>()?.CheckForFastWeaponSwitch(weapon) == true;
    }

    public override void Proceed(Weapon weapon, Callback<IFirearmHandsController> callback, bool scheduled = true)
    {
        FirearmControllerHandler handler = new(this, weapon);
        var flag = CanFastSwitchWeapon(_handsController, handler.Weapon);
        Il2CppSystem.Func<FirearmController> func = (Il2CppSystem.Func<FirearmController>)handler.ReturnController;

        if (FikaBackendUtils.IsClient && _baseInventoryController.StrictSync)
        {
            handler.Process = new Process<FirearmController, IFirearmHandsController>(this, func, handler.Weapon, flag, confirmation: AbstractProcess.Confirmation.Unknown);
            handler.ConfirmCallback = new(handler.SendCallbackRequest);
        }
        else
        {
            handler.Process = new Process<FirearmController, IFirearmHandsController>(this, func, handler.Weapon, flag);
            handler.ConfirmCallback = new(handler.SendPacket);
        }
        handler.Process.Proceed((Callback)handler.HandleResult, callback, scheduled);
    }
    #endregion

    public override void DropCurrentController(Il2CppSystem.Action callback, bool fastDrop, Item nextControllerItem = null)
    {
        CommonPacket.Type = ECommonSubPacketType.Drop;
        CommonPacket.SubPacket = DropPacket.FromValue(fastDrop);
        PacketSender.NetworkManager.SendNetReusable(ref CommonPacket, DeliveryMethod.ReliableOrdered, true);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call96(this, callback, fastDrop, nextControllerItem);
    }

    public override void OnBeenKilledByAggressor(IPlayer aggressor, DamageInfo damageInfo, EBodyPart bodyPart, EDamageType lethalDamageType)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call97(this, aggressor, damageInfo, bodyPart, lethalDamageType);

        // Handle 'Help Scav' rep gains
        if (aggressor?.TryCast<FikaPlayer>() is { } fikaPlayer)
        {
            if (fikaPlayer.Side == EPlayerSide.Savage)
            {
                fikaPlayer.Loyalty.MyPlayerIsSavage(this);
            }

            if (Side == EPlayerSide.Savage && fikaPlayer.Side != EPlayerSide.Savage && !fikaPlayer._hasSkilledScav)
            {
                fikaPlayer._hasSkilledScav = true;
                return;
            }
            else if (Side != EPlayerSide.Savage && _hasSkilledScav && aggressor.Side == EPlayerSide.Savage)
            {
                fikaPlayer.Profile?.FenceInfo?.AddStanding(Profile.Info.Settings.StandingForKill, EFT.Counters.EFenceStandingSource.ScavHelp);
            }
        }
    }

    public override void MouseLook(bool forceApplyToOriginalRibcage = false)
    {
        MovementContext.RotationAction?.Invoke(this);
    }

    protected Item FindWeapon()
    {
#if DEBUG
        FikaGlobals.LogWarning($"Finding weapon '{_lastWeaponId}'!");
#endif
        var itemResult = FindItemById(_lastWeaponId, false, false);
        var item = itemResult.Value;
        if (!itemResult.Succeeded)
        {
            for (var i = 0; i < FikaSingleton<IFikaGame>.Instance.GameController.ThrownGrenades.Count; i++)
            {
                var grenadeClass = FikaSingleton<IFikaGame>.Instance.GameController.ThrownGrenades[i];
                if (grenadeClass.Id == _lastWeaponId)
                {
                    item = grenadeClass;
                    break;
                }
            }
        }

        if (item == null)
        {
            var stationaryWeapon = GameWorld.FindStationaryWeaponByItemId(_lastWeaponId);
            if (stationaryWeapon != null)
            {
                item = stationaryWeapon.Item;
            }
        }

        return item;
    }

    protected void FindKillerWeapon()
    {
        if (LastAggressor == null)
        {
            FikaGlobals.LogWarning("LastAggressor was null, skipping");
            return;
        }

        if (!IsYourPlayer && !LastAggressor.IsCoopPlayer())
        {
#if DEBUG
            FikaGlobals.LogWarning($"Skipping because {LastAggressor.Profile.Nickname} is not a player");
#endif
            return;
        }

        var item = FindWeapon();
        if (item == null)
        {
            FikaGlobals.LogError($"Could not find killer weapon: {_lastWeaponId}!");
            return;
        }
        LastDamageInfo.Weapon = item;
    }

    public void HandleTeammateKill(DamageInfo damage, EBodyPart bodyPart,
        EPlayerSide playerSide, WildSpawnType role, string playerProfileId,
        float distance, List<string> targetEquipment,
        HealthEffects enemyEffects, List<string> zoneIds, FikaPlayer killer)
    {
#if DEBUG
        FikaGlobals.LogWarning($"HandleTeammateKill: Weapon {(damage.Weapon != null ? damage.Weapon.Name.Localized() : "None")}");
#endif

        if (role != WildSpawnType.pmcBEAR)
        {
            if (role == WildSpawnType.pmcUSEC)
            {
                playerSide = EPlayerSide.Usec;
            }
        }
        else
        {
            playerSide = EPlayerSide.Bear;
        }

        List<string> list = ["Any"];
        switch (playerSide)
        {
            case EPlayerSide.Usec:
                list.Add("Usec");
                list.Add("AnyPmc");
                list.Add("Enemy");
                break;
            case EPlayerSide.Bear:
                list.Add("Bear");
                list.Add("AnyPmc");
                list.Add("Enemy");
                break;
            case EPlayerSide.Savage:
                list.Add("Savage");
                list.Add("Bot");
                break;
        }

        for (var i = 0; i < list.Count; i++)
        {
            var value = list[i];
            QuestController.CheckKillConditionCounter(value, playerProfileId, targetEquipment.ToNativeList(), damage.Weapon,
                            bodyPart, Location, distance, role.ToStringNoBox(), CurrentHour, enemyEffects,
                            killer.HealthController.BodyPartEffects, zoneIds.ToNativeList(), killer.HealthController.ActiveBuffsNames());
        }
    }

    protected void HandleSharedExperience(bool countAsBoss, int experience, CountersCollection sessionCounters)
    {
        if (experience <= 0)
        {
            experience = Singleton<GlobalConfiguration>.Instance.Experience.Kill.VictimBotLevelExp;
        }

        if (FikaPlugin.Instance.Settings.SharedKillExperience.Value && !countAsBoss)
        {
            var toReceive = experience / 2;
#if DEBUG
            FikaGlobals.LogInfo($"Received shared kill XP of {toReceive}");
#endif
            sessionCounters.AddLong(1L, PredefinedCounters.Kills);
            sessionCounters.AddInt(toReceive, PredefinedCounters.ExpKillBase);
        }

        if (FikaPlugin.Instance.Settings.SharedBossExperience.Value && countAsBoss)
        {
            var toReceive = experience / 2;
#if DEBUG
            FikaGlobals.LogInfo($"Received shared boss XP of {toReceive}");
#endif
            sessionCounters.AddLong(1L, PredefinedCounters.Kills);
            sessionCounters.AddInt(toReceive, PredefinedCounters.ExpKillBase);
        }
    }

#if DEBUG
    public override void ShowStringNotification(string message)
    {
        if (IsYourPlayer)
        {
            EFT.UI.ConsoleScreen.Log(message);
            FikaGlobals.LogInfo(message);
        }
    }
#endif

    public override void SetInventoryOpened(bool opened)
    {
        if (this is ObservedPlayer)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call98(this, opened);
            return;
        }

        global::Fika.Core.Main.Utils.NativeBaseCalls.Call98(this, opened);

        // Native UI can close inventory after the raid transport has already
        // timed out and disposed its packet sender. Keep the local UI state,
        // but do not send through that ended connection.
        var networkManager = PacketSender?.NetworkManager;
        if (networkManager == null) return;

        CommonPacket.Type = ECommonSubPacketType.InventoryChanged;
        CommonPacket.SubPacket = InventoryChangedPacket.FromValue(opened);
        networkManager.SendNetReusable(ref CommonPacket, DeliveryMethod.ReliableOrdered, true);
    }

    public override void SendHeadlightsPacket(bool isSilent)
    {
        if (PacketSender != null && PacketSender.NetworkManager != null)
        {
            var lightStates = new List<LightsState>();
            foreach (var light in _helmetLightControllers) lightStates.Add(FikaGlobals.GetFirearmLightStates(light));

            CommonPacket.Type = ECommonSubPacketType.HeadLights;
            CommonPacket.SubPacket = HeadLightsPacket.FromValue(lightStates.Count, isSilent, lightStates.ToArray());
            PacketSender.NetworkManager.SendNetReusable(ref CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }
    }

    public override void SendWeaponLightPacket()
    {
        if (PacketSender == null)
        {
            return;
        }

        if (HandsController?.TryCast<FikaClientFirearmController>() is { } controller)
        {
            var array = new List<LightsState>();
            foreach (var slot in controller.Item.AllSlots)
            {
                var contained = FikaGlobals.GetContainedItem(slot);
                if (contained == null) continue;
                var component = contained.GetItemComponent<LightComponent>();
                if (component != null)
                    array.Add(FikaGlobals.GetFirearmLightStatesFromComponent(component));
            }
            if (array.Count == 0)
            {
                return;
            }

            controller.SendLightStates(LightStatesPacket.FromValue(array.Count, array.ToArray()));
        }
    }

    public override void OnPhraseTold(EPhraseTrigger @event, TaggedClip clip, TagBank bank, BaseSpeaker speaker)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call99(this, @event, clip, bank, speaker);

        if (ActiveHealthController.IsAlive)
        {
            CommonPacket.Type = ECommonSubPacketType.Phrase;
            CommonPacket.SubPacket = PhrasePacket.FromValue(@event, clip.NetId);
            PacketSender.NetworkManager.SendNetReusable(ref CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }
    }

    public override Corpse CreateCorpse()
    {
        var corpse = CreateCorpseInternal();
        // [CorpseSync] The native Player.OnDead reaches this method exactly once, after the
        // hands are frozen and before the corpse is put in charge of the body. The line proves
        // whether the authoritative machine produced a corpse and its loot container for this
        // victim; a client's own line is logged from ObservedPlayer.OnDead.
        CorpseSyncLog.LogDeath(this, FikaBackendUtils.IsServer ? "host" : "client", "native", LastDamageType);
        return corpse;
    }

    private Corpse CreateCorpseInternal()
    {
        if (FikaBackendUtils.IsServer)
        {
            var corpse = global::Fika.Core.Main.Utils.NativeBaseCalls.Call100(this);
            // SPT 5 corpse no longer exposes IsZombieCorpse; the prefab already carries the skeleton mode.
            CorpsePositionSyncer.Create(corpse.gameObject, corpse, NetId);
            return corpse;
        }

        var observedCorpse = CreateCorpse<ObservedCorpse>(Velocity);
        // SPT 5 corpse no longer exposes IsZombieCorpse; the prefab already carries the skeleton mode.
        observedCorpse.SetSpecificSettings(PlayerBones.RightPalm);
        Singleton<GameWorld>.Instance.ObservedPlayersCorpses.Add(NetId, observedCorpse);
        return observedCorpse;
    }

    public override void OperateStationaryWeapon(StationaryWeapon stationaryWeapon, StationaryWeaponPacket.EStationaryCommand command)
    {
        if (command is StationaryWeaponPacket.EStationaryCommand.Occupy)
        {
            if (WaitingForCallback || !HandsController.CanRemove())
            {
                return;
            }
            if (FikaBackendUtils.IsClient)
            {
                _inventoryController.ExecuteStationaryOperation(stationaryWeapon, (Callback)CheckIfStationarySucceeded);
            }
        }

        global::Fika.Core.Main.Utils.NativeBaseCalls.Call101(this, stationaryWeapon, command);

        CommonPacket.Type = ECommonSubPacketType.Stationary;
        CommonPacket.SubPacket = StationaryPacket.FromValue((EStationaryCommand)command, stationaryWeapon != null ? stationaryWeapon.Id : string.Empty);
        PacketSender.NetworkManager.SendNetReusable(ref CommonPacket, DeliveryMethod.ReliableOrdered, true);
    }

    private void CheckIfStationarySucceeded(IResult result)
    {
        if (result.Succeed)
        {
            return;
        }
        MovementContext.PlayerAnimatorSetStationary(false);
        MovementContext.PlayerAnimatorSetApproached(true);
        CurrentManagedState.DropStationary();
        if (MovementContext.StationaryWeapon != null)
        {
            MovementContext.StationaryWeapon.Unlock(ProfileId);
        }
    }

    // Start
    public override void StartInteraction(WorldInteractiveObject interactiveObject, InteractionResult interactionResult, Il2CppSystem.Action callback, bool enterDoorState = true)
    {
        CommonPacket.Type = ECommonSubPacketType.WorldInteraction;
        CommonPacket.SubPacket = WorldInteractionPacket.FromValue(interactiveObject.Id, interactionResult.InteractionType,
            EInteractionStage.Start, interactionResult.TryCast<UnlockResult>()?.Key?.Item?.Id);
        PacketSender.NetworkManager.SendNetReusable(ref CommonPacket, DeliveryMethod.ReliableOrdered, true);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call102(this, interactiveObject, interactionResult, callback, enterDoorState);
    }

    // Execute
    public override void ExecuteInteraction(WorldInteractiveObject door, InteractionResult interactionResult)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call103(this, door, interactionResult);
        if (!door.ForceLocalInteraction)
        {
            CommonPacket.Type = ECommonSubPacketType.WorldInteraction;
            CommonPacket.SubPacket = WorldInteractionPacket.FromValue(door.Id, interactionResult.InteractionType,
                EInteractionStage.Execute, interactionResult.TryCast<UnlockResult>()?.Key?.Item?.Id);
            PacketSender.NetworkManager.SendNetReusable(ref CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }
        UpdateInteractionCast();
    }

    public override void PlantItemNetwork(string itemId, string zoneId, bool successful)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call104(this, itemId, zoneId, successful);
        UpdateInteractionCast();
    }

    public override void OnAnimatedInteraction(EInteraction interaction)
    {
        if (!FikaGlobals.BlockedInteractions.Contains(interaction))
        {
            CommonPacket.Type = ECommonSubPacketType.Interaction;
            CommonPacket.SubPacket = InteractionPacket.FromValue(interaction);
            PacketSender.NetworkManager.SendNetReusable(ref CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }
    }

    public override void HealthControllerUpdate(float deltaTime)
    {
        _healthController?.ManualUpdate(deltaTime);
    }

    public override void UpdateTriggerColliderSearcher(float deltaTime, bool isCloseToCamera = true)
    {
        _triggerColliderSearcher.ManualUpdate(deltaTime);
    }

    public override void OnMounting(EFT.MountingPacket.EMountingCommand command)
    {
        var packet = MountingPacket.FromValue(command, MovementContext.IsInMountedState, MovementContext.IsInMountedState ? MovementContext.PlayerMountingPointData.MountPointData.MountDirection : default,
            MovementContext.IsInMountedState ? MovementContext.PlayerMountingPointData.MountPointData.MountPoint : default,
            MovementContext.IsInMountedState ? MovementContext.PlayerMountingPointData.CurrentMountingPointVerticalOffset : 0f,
           MovementContext.IsInMountedState ? (short)MovementContext.PlayerMountingPointData.MountPointData.MountSideDirection : (short)0);

        if (command == EFT.MountingPacket.EMountingCommand.Enter)
        {
            packet.TransitionTime = MovementContext.PlayerMountingPointData.CurrentApproachTime;
            packet.TargetPos = MovementContext.PlayerMountingPointData.PlayerTargetPos;
            packet.TargetPoseLevel = MovementContext.PlayerMountingPointData.TargetPoseLevel;
            packet.TargetHandsRotation = MovementContext.PlayerMountingPointData.TargetHandsRotation;
            packet.TargetBodyRotation = MovementContext.PlayerMountingPointData.TargetBodyRotation;
            packet.PoseLimit = MovementContext.PlayerMountingPointData.PoseLimit;
            packet.PitchLimit = MovementContext.PlayerMountingPointData.PitchLimit;
            packet.YawLimit = MovementContext.PlayerMountingPointData.YawLimit;
        }

        CommonPacket.Type = ECommonSubPacketType.Mounting;
        CommonPacket.SubPacket = packet;
        PacketSender.NetworkManager.SendNetReusable(ref CommonPacket,
            command is EFT.MountingPacket.EMountingCommand.Update ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered,
            true);
    }

    public override void TransitInteraction(TransitController controller, int transitPointId, string keyId, EDateTime time)
    {
        var packet = controller.GetInteractPacket(transitPointId, keyId, time);
        if (FikaBackendUtils.IsServer)
        {
            controller.InteractWithTransit(this, packet);
        }
        else
        {
            TransitInteractPacket interactPacket = new()
            {
                NetId = NetId,
                Data = packet
            };
            FikaSingleton<FikaClient>.Instance.SendData(ref interactPacket, DeliveryMethod.ReliableOrdered);
            if (FikaWorldControllers.Find<ClientClasses.ClientTransitController>(Singleton<GameWorld>.Instance.TransitController) is { } transitController)
            {
                transitController.InteractPacket = packet;
            }
        }
        UpdateInteractionCast();
    }

    public override void TripwireInteraction(TripwireSynchronizableObject tripwire)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call105(this, tripwire);
        SynchronizableObjectPacket data = new()
        {
            ObjectType = SynchronizableObjectType.Tripwire,
            ObjectId = tripwire.ObjectId,
            PacketData = new()
            {
                TripwireDataPacket = new()
                {
                    State = ETripwireState.Inert
                }
            },
            Position = tripwire.transform.position,
            Rotation = tripwire.transform.rotation.eulerAngles,
            IsActive = true
        };
        UpdateInteractionCast();

        FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.DisarmTripwire,
                DisarmTripwire.FromValue(data), true);
    }

    public override void EventObjectInteraction(EventObjectController controller, int objectId, EventObject.EInteraction interaction)
    {
        var packet = controller.GetInteractPacket(objectId, interaction);
        if (FikaBackendUtils.IsServer)
        {
            controller.InteractWithEventObject(this, packet);
        }
        else
        {
            EventControllerInteractPacket interactPacket = new()
            {
                NetId = NetId,
                Data = packet
            };
            PacketSender.NetworkManager.SendData(ref interactPacket, DeliveryMethod.ReliableOrdered);
        }
        UpdateInteractionCast();
    }

    public override void ApplyCorpseImpulse()
    {
        Corpse.Ragdoll.ApplyImpulse(LastDamageInfo.HitCollider, LastDamageInfo.Direction, LastDamageInfo.HitPoint, _corpseAppliedForce);
    }

    public void SetupCorpseSyncPacket(SyncHealthPacket packet)
    {
        var num = EFTHardSettings.Instance.HIT_FORCE;
        num *= 0.3f + (0.7f * Mathf.InverseLerp(50f, 20f, LastDamageInfo.PenetrationPower));
        _corpseAppliedForce = num;

        if (FikaBackendUtils.IsServer || IsYourPlayer)
        {
            // Native parity (EFT.Player.SetDogtagInfo, decompiled below from EFT 4.0
            // Player.cs:30237): the side a dog tag carries comes from the ROLE, never from the
            // profile. SPT 5.0 rewrites every PMC bot's Profile.Info.Side to Savage after
            // generating it, so a gate on "Side is not Savage" skipped the dog tag of every AI
            // PMC and left it without the victim and without the kill information.
            // GenerateDogtagDetails itself skips real Savage victims (scavs).
            GenerateDogtagDetails();
        }

        var inventoryDescriptor = ItemBinarySerializer.SerializeItem(Inventory.Equipment, FikaGlobals.SearchControllerSerializer);

        var packets = HealthSyncPacket.FromValue(packet);
        packets.BodyPart = LastBodyPart;
        packets.CorpseSyncPacket = new()
        {
            BodyPartColliderType = LastDamageInfo.BodyPartColliderType,
            Direction = LastDamageInfo.Direction,
            Point = LastDamageInfo.HitPoint,
            Force = _corpseAppliedForce,
            InventoryDescriptor = inventoryDescriptor,
            ItemSlot = EquipmentSlot.ArmBand
        };

        if (TriggerZones.Count > 0)
        {
            foreach (var zone in TriggerZones)
                packets.TriggerZones.Add(zone);
        }

        if (LastAggressor != null)
        {
            packets.KillerId = new Il2CppSystem.Nullable<MongoID>(new MongoID(LastAggressor.ProfileId));
        }

        if (_lastWeaponId is not null)
        {
            packets.WeaponId = new Il2CppSystem.Nullable<MongoID>(_lastWeaponId);
        }

        if (HandsController?.Item != null)
        {
            var heldItem = HandsController.Item;
            for (var i = 0; i < FikaGlobals.WeaponSlots.Count; i++)
            {
                var weaponSlot = FikaGlobals.WeaponSlots[i];
                if (heldItem == Equipment.GetSlot(weaponSlot).ContainedItem)
                {
                    packets.CorpseSyncPacket.ItemSlot = weaponSlot;
                    break;
                }
            }
        }

        if (this is ObservedPlayer) CorpseSyncPacket = packets.CorpseSyncPacket;
        CommonPacket.Type = ECommonSubPacketType.HealthSync;
        CommonPacket.SubPacket = packets;
        PacketSender.NetworkManager.SendNetReusable(ref CommonPacket, DeliveryMethod.ReliableOrdered, true);
    }

    public override void OnDead(EDamageType damageType)
    {
        if (HasDied) return;
        HasDied = true;
        RevivalClock.MarkDead();
        Downed = false;
        if (_bleedout != null) Destroy(_bleedout);
        OnPlayerDeath?.Invoke(this);

        foreach (var unsubcribe in _armorUnsubcribes)
        {
            unsubcribe?.Invoke();
        }

        if (LastDamageInfo.Weapon == null && _lastWeaponId is not null)
        {
            FindKillerWeapon();
#if DEBUG
            if (LastDamageInfo.Weapon != null)
            {
                FikaGlobals.LogWarning($"Found weapon '{LastDamageInfo.Weapon.Name.Localized()}'!");
            }
#endif
        }
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call106(this, damageType);
        PacketSender.SendState = false;
        if (IsYourPlayer)
        {
            StartCoroutine(LocalPlayerDied().WrapToIl2Cpp());
        }
    }

    public override void OnDestroy()
    {
        if (IsAI || IsYourPlayer)
        {
            CommonPacket?.Clear();
            CommonPacket = null;
        }
        OnPlayerDestroyed?.Invoke(this);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call107(this);
    }

    private void GenerateDogtagDetails()
    {
        if (LastDamageInfo.Weapon == null && _lastWeaponId is not null)
        {
            FindKillerWeapon();
#if DEBUG
            if (LastDamageInfo.Weapon != null)
            {
                FikaGlobals.LogWarning($"Found weapon '{LastDamageInfo.Weapon.Name.Localized()}'!");
            }
#endif
        }

        // The side on the tag is the faction of the role (native SetDogtagInfo switches on
        // WildSpawnType.pmcUSEC/pmcBEAR); a real Savage victim has no dog tag data at all.
        var role = Profile.Info.Settings.Role;
        var faction = FikaGlobals.GetFactionSide(role, Side);
        var item = Equipment.GetSlot(EquipmentSlot.Dogtag).ContainedItem;
        var dogtagComponent = item != null ? item.GetItemComponent<DogtagComponent>() : null;
        if (dogtagComponent == null)
        {
            if (faction == EPlayerSide.Savage)
            {
                // Scavs (and any bot the server did not give a dog tag to) are supposed to have
                // no tag data; native SetDogtagInfo returns for a Savage victim as well.
                MapMarkerDiagnostics.LogDogtag(this, "skipped", faction, null);
                return;
            }

            FikaGlobals.LogError($"GenerateAndSendDogTagPacket: Item or Dogtagcomponent was null on player {Profile.Nickname}, id {NetId}");
            MapMarkerDiagnostics.LogDogtag(this, "missing-item", faction, null);
            return;
        }

        var accountId = AccountId;
        var profileId = ProfileId;
        var nickname = Profile.Nickname;
        var hasAggressor = LastAggressor != null;
        var killerAccountId = hasAggressor ? LastAggressor.AccountId : string.Empty;
        var killerProfileId = hasAggressor ? LastAggressor.ProfileId : string.Empty;
        var killerNickname = (hasAggressor && !string.IsNullOrEmpty(LastAggressor.Profile.Nickname)) ? LastAggressor.Profile.Nickname : string.Empty;
        var side = faction;
        var level = Profile.Info.Level;
        var time = DateTimeExtensions.UtcNow.ToLocalTime();
        var weaponName = LastAggressor != null ? (LastDamageInfo.Weapon != null ? LastDamageInfo.Weapon.ShortName : string.Empty) : "-";
        var groupId = GroupId;

        dogtagComponent.Item.SpawnedInSession = true;
        dogtagComponent.AccountId = accountId;
        dogtagComponent.ProfileId = profileId;
        dogtagComponent.Nickname = nickname;
        dogtagComponent.KillerAccountId = killerAccountId;
        dogtagComponent.KillerProfileId = killerProfileId;
        dogtagComponent.KillerName = killerNickname;
        dogtagComponent.Side = side;
        dogtagComponent.Level = level;
        dogtagComponent.Time = time;
        dogtagComponent.Status = hasAggressor ? "Killed by" : "Died";
        dogtagComponent.WeaponName = weaponName;
        dogtagComponent.GroupId = groupId;
        MapMarkerDiagnostics.LogDogtag(this, "generated", side, dogtagComponent, LastAggressor);
    }

    private IEnumerator LocalPlayerDied()
    {
        AddPlayerRequest request = new(FikaBackendUtils.GroupId, FikaBackendUtils.Profile.ProfileId, FikaBackendUtils.IsSpectator);
        var diedTask = FikaRequestHandler.PlayerDied(request);
        WaitForEndOfFrame waitForEndOfFrame = new();
        while (!diedTask.IsCompleted)
        {
            yield return waitForEndOfFrame;
        }
    }

    public override void TryInteractionCallback(LootableContainer container)
    {
        LootableContainerInteractionHandler handler = new(this, container);
        if (handler.Container != null && _openAction != null)
        {
            _openAction.Invoke((Il2CppSystem.Action)handler.Handle);
        }
        _openAction = null;
    }

    public override void BtrInteraction(BTRSide btr, byte placeId, EInteractionType interaction)
    {
        if (FikaBackendUtils.IsServer)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call108(this, btr, placeId, interaction);
            return;
        }

        var client = FikaSingleton<FikaClient>.Instance;
        BTRInteractionPacket packet = new(NetId)
        {
            Data = btr.GetInteractWithBtrPacket(placeId, interaction)
        };
        client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        UpdateInteractionCast();
    }

    public void SetupMainPlayer()
    {
        // Fika's "this lobby is one team" group marker. The local player and every session
        // human share it (ObservedPlayer.InitObservedPlayer stamps the replicated ones, and the
        // announced profile carries it, see FikaSerializationExtensions.PutProfile).
        //
        // It has to be non-empty here: the native SecretExfiltrationPointFoundShareEvent is
        // matched against the *receiving* player's own Profile.Info.GroupId/TeamId
        // (FikaExfilManager sends this player's values), and the game's native group logic only
        // fires for a non-empty group id.
        //
        // It must stay shared: Player.GroupId is the only "same team" signal third-party
        // consumers have - "SPT Dynamic Maps" reports a player as "Friendly Player" when
        // Player.GroupId equals the main player's - and the game's own group logic (voice
        // groups, teammate nameplates) keys off it too. The marker categories are decided by
        // role/side (FikaGlobals.GetFactionSide), so sharing the group no longer collapses
        // PMC/Scav/Boss. Fika's own "is this one of ours" gameplay tests use
        // FikaGlobals.IsCoopPlayer, not the group id. The group only leaves the machine through
        // the raid-end scav save, which strips it again (RaidEndProfile.Capture).
        MapMarkerDiagnostics.ResetForRaid();
        FikaGlobals.CaptureLocalGroup(Profile);
        Profile.Info.GroupId = FikaGlobals.FikaGroupId;
        Profile.Info.TeamId = FikaGlobals.FikaGroupId;
        MapMarkerDiagnostics.Log(this, "local");
    }

    public void HandleHeadLightsPacket(HeadLightsPacket packet)
    {
        try
        {
            if (_helmetLightControllers != null)
            {
                foreach (var controller in _helmetLightControllers)
                {
                    if (controller?.LightMod == null) continue;
                    foreach (var state in packet.LightStates)
                    {
                        if (controller.LightMod.Item.Id == state.Id)
                        {
                            controller.LightMod.SetLightState(state);
                            break;
                        }
                    }
                }
                if (!packet.IsSilent)
                {
                    SwitchHeadLightsAnimation();
                }
            }
        }
        catch
        {
            // Do nothing
        }
    }

    public void HandleInventoryOpenedPacket(bool opened)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call98(this, opened);
    }

    public void HandleDropPacket(bool fastDrop)
    {
        /*DropHandler handler = new(this);*/
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call96(this, (Il2CppSystem.Action)FikaGlobals.EmptyAction, fastDrop, null);
    }

    public void HandleUsableItemPacket(UsableItemPacket packet)
    {
        if (HandsController?.TryCast<UsableItemController>() is { } usableItemController)
        {
            if (packet.ExamineWeapon)
            {
                usableItemController.ExamineWeapon();
            }

            if (packet.HasCompassState)
            {
                usableItemController.CompassState.Value = packet.CompassState;
            }

            if (packet.HasAim)
            {
                usableItemController.SetAim(packet.AimState);
            }
        }
    }

    public void ObservedStationaryInteract(StationaryWeapon stationaryWeapon, StationaryWeaponPacket.EStationaryCommand command)
    {
        if (command == StationaryWeaponPacket.EStationaryCommand.Occupy)
        {
            stationaryWeapon.SetOperator(ProfileId, false);
            MovementContext.StationaryWeapon = stationaryWeapon;
            MovementContext.InteractionParameters = stationaryWeapon.GetInteractionParameters();
            MovementContext.PlayerAnimatorSetApproached(false);
            MovementContext.PlayerAnimatorSetStationary(true);
            MovementContext.PlayerAnimatorSetStationaryAnimation((int)stationaryWeapon.Animation);
            return;
        }
        if (command == StationaryWeaponPacket.EStationaryCommand.Leave)
        {
            return;
        }
        MovementContext.PlayerAnimatorSetStationary(false);
        if (MovementContext.StationaryWeapon != null)
        {
            MovementContext.StationaryWeapon.Unlock(ProfileId);
        }
    }

    public virtual void DoObservedVault(VaultPacket vaultPacket)
    {

    }

    public override void PauseAllEffectsOnPlayer()
    {
        ActiveHealthController.PauseAllEffects();
    }

    public override void UnpauseAllEffectsOnPlayer()
    {
        ActiveHealthController.UnpauseAllEffects();
    }

    public void HandleCallbackFromServer(OperationCallbackPacket operationCallbackPacket)
    {
        if (OperationCallbacks.TryGetValue(operationCallbackPacket.CallbackId, out var callback))
        {
            if (operationCallbackPacket.Status != EOperationStatus.Started)
            {
                OperationCallbacks.Remove(operationCallbackPacket.CallbackId);
            }
            ServerOperationStatus status = new(operationCallbackPacket.Status, operationCallbackPacket.Error);
            callback(status);
        }
        else
        {
            FikaGlobals.LogError($"Could not find CallbackId: {operationCallbackPacket.CallbackId}!");
        }
    }

    public override void ApplyExplosionDamageToArmor(Il2CppSystem.Collections.Generic.Dictionary<ExplosionDamageInfo, float> armorDamage, DamageInfo damageInfo)
    {
        if (IsYourPlayer)
        {
            for (var i = 0; i < _preAllocatedArmorComponents.Count; i++)
            {
                var armorComponent = _preAllocatedArmorComponents[i];
                var num = 0f;
                foreach ((var colliderStruct, var amount) in armorDamage)
                {
                    if (armorComponent.ShotMatches(colliderStruct.BodyPartColliderType, colliderStruct.ArmorPlateCollider))
                    {
                        num += amount;
                    }
                }

                if (num > 0f)
                {
                    num = armorComponent.ApplyExplosionDurabilityDamage(num, damageInfo, _preAllocatedArmorComponents);
                    OnArmorDamaged(num, armorComponent);
                    OnArmorPointsChanged(armorComponent);
                }
            }
        }
    }

    public override void OnArmorPointsChanged(ArmorComponent armor, bool children = false)
    {
        if (!children)
        {
            CommonPacket.Type = ECommonSubPacketType.ArmorDamage;
            CommonPacket.SubPacket = ArmorDamagePacket.FromValue(armor.Item.Id, armor.Repairable.Durability);
            FikaSingleton<IFikaNetworkManager>.Instance.SendNetReusable(ref CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }
    }

    public virtual void HandleDamagePacket(DamagePacket packet)
    {
        if (_healthController == null || !_healthController.IsAlive)
        {
            return;
        }

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
            damageInfo.SourceId = packet.SourceId.Value.ToString();
        }

        if (packet.ProfileId.HasValue)
        {
            var world = Singleton<GameWorld>.Instance;
            var attacker = world.GetAlivePlayerByProfileID(packet.ProfileId.Value.ToString());
            var player = attacker != null ? world.GetAlivePlayerBridgeByRaidID(attacker.RaidId) : null;
            if (player != null)
            {
                damageInfo.Player = player;
                if (IsYourPlayer
                    && !FikaPlugin.Instance.Settings.FriendlyFire
                    && damageInfo.Player.iPlayer.IsCoopPlayer())
                {
                    return;
                }
            }
            _lastWeaponId = packet.WeaponId.HasValue ? packet.WeaponId.Value : null;
        }

        if (damageInfo.DamageType == EDamageType.Melee && _lastWeaponId is not null)
        {
            var item = FindWeapon();
            if (item != null)
            {
                damageInfo.Weapon = item;
                if (damageInfo.Player?.iPlayer?.TryCast<FikaPlayer>() is { } aggressor)
                {
                    aggressor._shouldSendSideEffect = true;
                }
#if DEBUG
                FikaGlobals.LogWarning("Found weapon for knife damage: " + item.Name.Localized());
#endif
            }
        }

        ShotReactions(damageInfo, packet.BodyPartType);
        ReceiveDamage(damageInfo.Damage, packet.BodyPartType, damageInfo.DamageType, packet.Absorbed, packet.Material);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call95(this, damageInfo, packet.BodyPartType, packet.ColliderType, packet.Absorbed);
    }

    public override void OnSideEffectApplied(SideEffectComponent sideEffect)
    {
        if (!_shouldSendSideEffect)
        {
            return;
        }

        SideEffectPacket packet = new()
        {
            ItemId = sideEffect.Item.Id,
            Value = sideEffect.Value
        };

        PacketSender.NetworkManager.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
        _shouldSendSideEffect = false;
    }

    public void HandleArmorDamagePacket(ArmorDamagePacket packet)
    {
        for (var i = 0; i < _preAllocatedArmorComponents.Count; i++)
        {
            var armorComponent = _preAllocatedArmorComponents[i];
            if (armorComponent.Item.Id == packet.ItemId)
            {
                armorComponent.Repairable.Durability = packet.Durability;
                armorComponent.Buff.TryDisableComponent(armorComponent.Repairable.Durability);
                armorComponent.Item.RaiseRefreshEvent(false, false);
                return;
            }
        }

        var gstruct = Singleton<GameWorld>.Instance.FindItemById(packet.ItemId);
        if (gstruct.Failed)
        {
            FikaGlobals.LogError("HandleArmorDamagePacket: " + gstruct.Error);
            return;
        }

        var itemComponent = gstruct.Value.GetItemComponent<ArmorComponent>();
        if (itemComponent != null)
        {
            itemComponent.Repairable.Durability = packet.Durability;
            itemComponent.Buff.TryDisableComponent(itemComponent.Repairable.Durability);
            itemComponent.Item.RaiseRefreshEvent(false, false);
        }
    }

    public override bool SetShotStatus(BodyPartCollider bodypart, Shot shot, Vector3 hitpoint, Vector3 shotNormal, Vector3 shotDirection)
    {
        var armorPlateCollider = bodypart.TryCast<ArmorPlateCollider>();
        var earmorPlateCollider = ((armorPlateCollider == null) ? 0 : armorPlateCollider.ArmorPlateColliderType);
        for (var i = 0; i < _preAllocatedArmorComponents.Count; i++)
        {
            var armorComponent = _preAllocatedArmorComponents[i];
            if (armorComponent.ShotMatches(bodypart.BodyPartColliderType, earmorPlateCollider))
            {
                if (armorComponent.Deflects(shotDirection, shotNormal, shot))
                {
                    return true;
                }
                if (!NativeNullable<MongoID>.Read(shot, "BlockedBy").HasValue)
                {
                    armorComponent.SetPenetrationStatus(shot);
                }
            }
        }
        return false;
    }

    public override void UpdateTick()
    {
        _voipController?.Update();
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call109(this);
    }

    public void CheckAndResetControllers(ExitStatus exitStatus, float pastTime, string locationId, string exitName)
    {
        _questController?.CheckExitConditionCounters(exitStatus, pastTime, locationId, exitName, HealthController.BodyPartEffects, TriggerZones);
        _questController?.ResetCurrentNullableCounters();

        _achievementsController?.CheckExitConditionCounters(exitStatus, pastTime, locationId, exitName, HealthController.BodyPartEffects, TriggerZones);
        _achievementsController?.ResetCurrentNullableCounters();
    }

    private bool _fikaDisposed;
    private bool _fikaDisposing;

    internal void ResetDisposeState()
    {
        _fikaDisposed = false;
        _fikaDisposing = false;
    }

    public override void Dispose()
    {
        // SPT 5's native Player.OnDestroy calls the virtual Dispose again when
        // ReturnToPool follows explicit raid cleanup. Release each lifetime once.
        if (_fikaDisposed || _fikaDisposing) return;
        _fikaDisposing = true;
        try
        {
            DisposePlayerResources();
            _fikaDisposed = true;
        }
        finally { _fikaDisposing = false; }
    }

    private void DisposePlayerResources()
    {
        UnregisterNetworkPlayer();

        for (var i = 0; i < _armorUnsubcribes.Length; i++)
        {
            _armorUnsubcribes[i]?.Invoke();
            _armorUnsubcribes[i] = null;
        }

        if (PacketSender != null)
        {
            PacketSender.DestroyThis();
            PacketSender = null;
        }

        _voipController?.Dispose();
        _voipController = null;
        _lastWeaponId = null;
        if (IsYourPlayer)
        {
            var inventoryController = InventoryController;
            if (inventoryController?.TryCast<HostInventoryController>() is { } hostController)
            {
                hostController.ClearPool();
            }
            else if (inventoryController?.TryCast<ClientInventoryController>() is { } clientController)
            {
                clientController.ClearPool();
            }
            else
            {
                FikaGlobals.LogWarning($"Unknown inventory controller when disposing: {inventoryController?.GetType().Name ?? "null"}");
            }
        }
        OperationCallbacks.Clear();
        _proceedCallbacks.Clear();
        _proceedCallbackTimes.Clear();
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call110(this);
    }

    protected void UnregisterNetworkPlayer()
    {
        // Native assets are released immediately; Unity OnDestroy may run much
        // later, while UDP polling still continues (also during transit).
        // Detach this lifetime before any callback can find a disposed player.
        var network = FikaSingleton<IFikaNetworkManager>.Instance;
        var handler = network?.CoopHandler;
        if (handler != null)
        {
            if (handler.Players.TryGetValue(NetId, out var registered) && registered.Pointer == Pointer)
                handler.Players.Remove(NetId);
            handler.HumanPlayers.RemoveAll(player => player.Pointer == Pointer);
        }
        network?.ObservedPlayers.RemoveAll(player => player.Pointer == Pointer);
    }

    public override Il2CppSystem.Threading.Tasks.Task OnGameSessionEnd(ExitStatus exitStatus, float pastTime, string locationId, string exitName)
    {
        if (_achievementsController != null)
        {
            _achievementsController.AchievementUnlocked -= (Il2CppSystem.Action<string>)UnlockAchievement;
        }
        return global::Fika.Core.Main.Utils.NativeBaseCalls.Call111(this, exitStatus, pastTime, locationId, exitName);
    }

    public override void OnVaulting()
    {
        CommonPacket.Type = ECommonSubPacketType.Vault;
        CommonPacket.SubPacket = VaultPacket.FromValue(VaultingParameters.GetVaultingStrategy(), VaultingParameters.MaxWeightPointPosition,
            VaultingParameters.VaultingHeight, VaultingParameters.VaultingLength, MovementContext.VaultingSpeed,
            VaultingParameters.BehindObstacleRatio, VaultingParameters.AbsoluteForwardVelocity);
        PacketSender.NetworkManager.SendNetReusable(ref CommonPacket, DeliveryMethod.ReliableOrdered, true);
    }

    public void ReceiveTraderServicesData(List<TraderServiceAvailabilityData> services)
    {
        if (!IsYourPlayer)
        {
            return;
        }

        var servicesData = Singleton<GlobalConfiguration>.Instance.ServicesData;

        for (var i = 0; i < services.Count; i++)
        {
            var service = services[i];
            GlobalConfiguration.ServiceData serviceData = new(service, null);
            if (servicesData.ContainsKey(serviceData.ServiceType))
            {
                servicesData[serviceData.ServiceType] = serviceData;
            }
            else
            {
                servicesData.Add(serviceData.ServiceType, serviceData);
            }
            if (!Profile.TradersInfo.TryGetValue(serviceData.TraderId, out var traderInfo))
            {
                FikaGlobals.LogWarning($"Can't find trader with id: {serviceData.TraderId}!");
            }
            else
            {
                traderInfo.SetServiceAvailability(serviceData.ServiceType, service.CanAfford, service.WasPurchasedInThisRaid);
            }
        }
    }

    public Item FindQuestItem(MongoID templateId)
    {
        for (var i = 0; i < Singleton<GameWorld>.Instance.LootList.Count; i++)
        {
            var lootItem = Singleton<GameWorld>.Instance.LootList[i];
            if (lootItem.TryCast<LootItem>() is { } observedLootItem && observedLootItem.Item.TemplateId == templateId && observedLootItem.isActiveAndEnabled)
            {
                return observedLootItem.Item;
            }
        }
#if DEBUG
        FikaGlobals.LogInfo($"Could not find questItem with id '{templateId}' in the current session, either the quest is not active or something else occured.");
#endif
        return null;
    }

    #region handlers
    public sealed class KeyHandler(FikaPlayer player)
    {
        public Option<UnlockResult> UnlockResult;
        private readonly FikaPlayer _player = player;

        internal void HandleKeyEvent()
        {
            UnlockResult.Value.RaiseEvents(_player._inventoryController, CommandStatus.Succeed);
        }
    }

    private sealed class LootableContainerInteractionHandler(FikaPlayer player, LootableContainer container)
    {
        private readonly FikaPlayer _player = player;
        public readonly LootableContainer Container = container;

        public void Handle()
        {
            _player.CommonPacket.Type = ECommonSubPacketType.ContainerInteraction;
            _player.CommonPacket.SubPacket = ContainerInteractionPacket.FromValue(Container.Id, EInteractionType.Close);
            _player.PacketSender.NetworkManager.SendNetReusable(ref _player.CommonPacket, DeliveryMethod.ReliableOrdered, true);

            Container.Interact(new InteractionResult(EInteractionType.Close));
            if (_player.MovementContext.LevelOnApproachStart > 0f)
            {
                _player.MovementContext.SetPoseLevel(_player.MovementContext.LevelOnApproachStart, false);
                _player.MovementContext.LevelOnApproachStart = -1f;
            }
        }
    }

    private sealed class EmptyHandsControllerHandler(FikaPlayer fikaPlayer, bool scheduled)
    {
        private readonly FikaPlayer _fikaPlayer = fikaPlayer;
        private readonly bool _scheduled = scheduled;
        public Process<EmptyHandsController, IEmptyHandsController> Process;
        public Action ConfirmCallback;

        internal EmptyHandsController ReturnController()
        {
            return EmptyHandsController.CreateController<EmptyHandsController>(_fikaPlayer);
        }

        internal void HandleCallbackResponse(bool succeed)
        {
            if (succeed)
            {
                SendPacket();
            }
            Process.Confirm(succeed);
        }

        internal void SendCallbackRequest()
        {
            var client = FikaSingleton<FikaClient>.Instance;
            var id = _fikaPlayer.CreateProceedCallback(HandleCallbackResponse);
            var packet = new ProceedRequestPacket
            {
                NetId = _fikaPlayer.NetId,
                CallbackId = id,
                ProceedType = EProceedType.EmptyHands
            };
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }

        internal void SendPacket()
        {
            _fikaPlayer.CommonPacket.Type = ECommonSubPacketType.Proceed;
            _fikaPlayer.CommonPacket.SubPacket = ProceedPacket.FromValue(default, default, 0f, 0, EProceedType.EmptyHands, _scheduled);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _fikaPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }

        internal void HandleResult(IResult result)
        {
            if (result.Succeed)
            {
                ConfirmCallback();
            }
        }
    }

    private sealed class FirearmControllerHandler(FikaPlayer fikaPlayer, Weapon weapon)
    {
        private readonly FikaPlayer _fikaPlayer = fikaPlayer;
        public readonly Weapon Weapon = weapon;
        public Process<FirearmController, IFirearmHandsController> Process;
        public Action ConfirmCallback;

        internal FikaClientFirearmController ReturnController()
        {
            return FikaClientFirearmController.Create(_fikaPlayer, Weapon);
        }

        internal void HandleCallbackResponse(bool succeed)
        {
            if (succeed)
            {
                SendPacket();
            }
            Process.Confirm(succeed);
        }

        internal void SendCallbackRequest()
        {
            var client = FikaSingleton<FikaClient>.Instance;
            var id = _fikaPlayer.CreateProceedCallback(HandleCallbackResponse);
            var packet = new ProceedRequestPacket
            {
                NetId = _fikaPlayer.NetId,
                CallbackId = id,
                ProceedType = EProceedType.Weapon,
                ItemId = Weapon.Id
            };
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }

        internal void SendPacket()
        {
            _fikaPlayer.CommonPacket.Type = ECommonSubPacketType.Proceed;
            _fikaPlayer.CommonPacket.SubPacket = ProceedPacket.FromValue(default, Weapon.Id, 0f, 0,
                Weapon.IsStationaryWeapon ? EProceedType.Stationary : EProceedType.Weapon, false);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _fikaPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }

        internal void HandleResult(IResult result)
        {
            if (result.Succeed)
            {
                ConfirmCallback();
            }
        }
    }

    private sealed class UsableItemControllerHandler(FikaPlayer fikaPlayer, Item item)
    {
        private readonly FikaPlayer _fikaPlayer = fikaPlayer;
        private readonly Item _item = item;
        public Process<UsableItemController, IUsableItemController> Process;
        public Action ConfirmCallback;

        internal FikaClientUsableItemController ReturnController()
        {
            return FikaClientUsableItemController.Create(_fikaPlayer, _item);
        }

        internal void HandleCallbackResponse(bool succeed)
        {
            if (succeed)
            {
                SendPacket();
            }
            Process.Confirm(succeed);
        }

        internal void SendCallbackRequest()
        {
            var client = FikaSingleton<FikaClient>.Instance;
            var id = _fikaPlayer.CreateProceedCallback(HandleCallbackResponse);
            var packet = new ProceedRequestPacket
            {
                NetId = _fikaPlayer.NetId,
                CallbackId = id,
                ProceedType = EProceedType.UsableItem,
                ItemId = _item.Id
            };
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }

        internal void SendPacket()
        {
            _fikaPlayer.CommonPacket.Type = ECommonSubPacketType.Proceed;
            _fikaPlayer.CommonPacket.SubPacket = ProceedPacket.FromValue(default, _item.Id, 0f, 0, EProceedType.UsableItem, false);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _fikaPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }

        internal void HandleResult(IResult result)
        {
            if (result.Succeed)
            {
                ConfirmCallback();
            }
        }
    }

    private sealed class PortableRangeFinderControllerHandler(FikaPlayer fikaPlayer, Item item)
    {
        private readonly FikaPlayer _fikaPlayer = fikaPlayer;
        private readonly Item _item = item;
        public Process<PortableRangeFinderController, IUsableItemController> Process;
        public Action ConfirmCallback;

        internal FikaClientPortableRangeFinderController ReturnController()
        {
            return FikaClientPortableRangeFinderController.Create(_fikaPlayer, _item);
        }

        internal void HandleCallbackResponse(bool succeed)
        {
            if (succeed)
            {
                SendPacket();
            }
            Process.Confirm(succeed);
        }

        internal void SendCallbackRequest()
        {
            var client = FikaSingleton<FikaClient>.Instance;
            var id = _fikaPlayer.CreateProceedCallback(HandleCallbackResponse);
            var packet = new ProceedRequestPacket
            {
                NetId = _fikaPlayer.NetId,
                CallbackId = id,
                ProceedType = EProceedType.UsableItem,
                ItemId = _item.Id
            };
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }

        internal void SendPacket()
        {
            _fikaPlayer.CommonPacket.Type = ECommonSubPacketType.Proceed;
            _fikaPlayer.CommonPacket.SubPacket = ProceedPacket.FromValue(default, _item.Id, 0f, 0, EProceedType.UsableItem, false);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _fikaPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }

        internal void HandleResult(IResult result)
        {
            if (result.Succeed)
            {
                ConfirmCallback();
            }
        }
    }

    private sealed class QuickUseItemControllerHandler(FikaPlayer fikaPlayer, Item item)
    {
        private readonly FikaPlayer _fikaPlayer = fikaPlayer;
        private readonly Item _item = item;
        public Process<QuickUseItemController, IQuickUseItem> Process;
        public Action ConfirmCallback;

        internal QuickUseItemController ReturnController()
        {
            return QuickUseItemController.CreateController<QuickUseItemController>(_fikaPlayer, _item);
        }

        internal void HandleCallbackResponse(bool succeed)
        {
            if (succeed)
            {
                SendPacket();
            }
            Process.Confirm(succeed);
        }

        internal void SendCallbackRequest()
        {
            var client = FikaSingleton<FikaClient>.Instance;
            var id = _fikaPlayer.CreateProceedCallback(HandleCallbackResponse);
            var packet = new ProceedRequestPacket
            {
                NetId = _fikaPlayer.NetId,
                CallbackId = id,
                ProceedType = EProceedType.QuickUse,
                ItemId = _item.Id
            };
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }

        internal void SendPacket()
        {
            _fikaPlayer.CommonPacket.Type = ECommonSubPacketType.Proceed;
            _fikaPlayer.CommonPacket.SubPacket = ProceedPacket.FromValue(default, _item.Id, 0f, 0, EProceedType.QuickUse, false);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _fikaPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }

        internal void HandleResult(IResult result)
        {
            if (result.Succeed)
            {
                ConfirmCallback();
            }
        }
    }

    private sealed class MedsControllerHandler(FikaPlayer fikaPlayer, Meds meds, EFT.NetworkPackets.OneAndList<EBodyPart> bodyParts, int animationVariant)
    {
        private readonly FikaPlayer _fikaPlayer = fikaPlayer;
        private readonly Meds _meds = meds;
        private readonly EFT.NetworkPackets.OneAndList<EBodyPart> _bodyParts = bodyParts;
        private readonly int _animationVariant = animationVariant;
        public Process<MedsController, IMedsController> Process;
        public Action ConfirmCallback;

        internal MedsController ReturnController()
        {
            return MedsController.CreateController<MedsController>(_fikaPlayer, _meds, _bodyParts, 1f, _animationVariant);
        }

        internal void HandleCallbackResponse(bool succeed)
        {
            if (succeed)
            {
                SendPacket();
            }
            Process.Confirm(succeed);
        }

        internal void SendCallbackRequest()
        {
            var client = FikaSingleton<FikaClient>.Instance;
            var id = _fikaPlayer.CreateProceedCallback(HandleCallbackResponse);
            var packet = new ProceedRequestPacket
            {
                NetId = _fikaPlayer.NetId,
                CallbackId = id,
                ProceedType = EProceedType.MedsClass,
                ItemId = _meds.Id
            };
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }

        internal void SendPacket()
        {
            _fikaPlayer.CommonPacket.Type = ECommonSubPacketType.Proceed;
            _fikaPlayer.CommonPacket.SubPacket = ProceedPacket.FromValue(_bodyParts, _meds.Id, 1f, _animationVariant,
                EProceedType.MedsClass, false);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _fikaPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }

        internal void HandleResult(IResult result)
        {
            if (result.Succeed)
            {
                ConfirmCallback();
            }
        }
    }

    private sealed class FoodControllerHandler(FikaPlayer fikaPlayer, FoodDrink foodDrink, float amount, EFT.NetworkPackets.OneAndList<EBodyPart> bodyParts, int animationVariant)
    {
        private readonly FikaPlayer _fikaPlayer = fikaPlayer;
        private readonly FoodDrink _foodDrink = foodDrink;
        private readonly float _amount = amount;
        private readonly EFT.NetworkPackets.OneAndList<EBodyPart> _bodyParts = bodyParts;
        private readonly int _animationVariant = animationVariant;
        public Process<MedsController, IMedsController> Process;
        public Action ConfirmCallback;

        internal MedsController ReturnController()
        {
            return MedsController.CreateController<MedsController>(_fikaPlayer, _foodDrink, _bodyParts, _amount, _animationVariant);
        }

        internal void HandleCallbackResponse(bool succeed)
        {
            if (succeed)
            {
                SendPacket();
            }
            Process.Confirm(succeed);
        }

        internal void SendCallbackRequest()
        {
            var client = FikaSingleton<FikaClient>.Instance;
            var id = _fikaPlayer.CreateProceedCallback(HandleCallbackResponse);
            var packet = new ProceedRequestPacket
            {
                NetId = _fikaPlayer.NetId,
                CallbackId = id,
                ProceedType = EProceedType.FoodClass,
                ItemId = _foodDrink.Id
            };
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }

        internal void SendPacket()
        {
            _fikaPlayer.CommonPacket.Type = ECommonSubPacketType.Proceed;
            _fikaPlayer.CommonPacket.SubPacket = ProceedPacket.FromValue(_bodyParts, _foodDrink.Id, _amount, _animationVariant,
                EProceedType.MedsClass, false);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _fikaPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }

        internal void HandleResult(IResult result)
        {
            if (result.Succeed)
            {
                ConfirmCallback();
            }
        }
    }

    private sealed class KnifeControllerHandler(FikaPlayer fikaPlayer, KnifeComponent knife)
    {
        private readonly FikaPlayer _fikaPlayer = fikaPlayer;
        public readonly KnifeComponent Knife = knife;
        public Process<KnifeController, IKnifeController> Process;
        public Action ConfirmCallback;

        internal FikaClientKnifeController ReturnController()
        {
            return FikaClientKnifeController.Create(_fikaPlayer, Knife);
        }

        internal void HandleCallbackResponse(bool succeed)
        {
            if (succeed)
            {
                SendPacket();
            }
            Process.Confirm(succeed);
        }

        internal void SendCallbackRequest()
        {
            var client = FikaSingleton<FikaClient>.Instance;
            var id = _fikaPlayer.CreateProceedCallback(HandleCallbackResponse);
            var packet = new ProceedRequestPacket
            {
                NetId = _fikaPlayer.NetId,
                CallbackId = id,
                ProceedType = EProceedType.Knife,
                ItemId = Knife.Item.Id
            };
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }

        internal void SendPacket()
        {
            _fikaPlayer.CommonPacket.Type = ECommonSubPacketType.Proceed;
            _fikaPlayer.CommonPacket.SubPacket = ProceedPacket.FromValue(default, Knife.Item.Id, 0f, 0, EProceedType.Knife, false);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _fikaPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }

        internal void HandleResult(IResult result)
        {
            if (result.Succeed)
            {
                ConfirmCallback();
            }
        }
    }

    private sealed class QuickKnifeControllerHandler(FikaPlayer fikaPlayer, KnifeComponent knife)
    {
        private readonly FikaPlayer _fikaPlayer = fikaPlayer;
        public readonly KnifeComponent Knife = knife;
        public Process<QuickKnifeKickController, IQuickKnifeKickController> Process;
        public Action ConfirmCallback;

        internal FikaClientQuickKnifeController ReturnController()
        {
            return FikaClientQuickKnifeController.Create(_fikaPlayer, Knife);
        }

        internal void HandleCallbackResponse(bool succeed)
        {
            if (succeed)
            {
                SendPacket();
            }
            Process.Confirm(succeed);
        }

        internal void SendCallbackRequest()
        {
            var client = FikaSingleton<FikaClient>.Instance;
            var id = _fikaPlayer.CreateProceedCallback(HandleCallbackResponse);
            var packet = new ProceedRequestPacket
            {
                NetId = _fikaPlayer.NetId,
                CallbackId = id,
                ProceedType = EProceedType.QuickKnifeKick,
                ItemId = Knife.Item.Id
            };
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }

        internal void SendPacket()
        {
            _fikaPlayer.CommonPacket.Type = ECommonSubPacketType.Proceed;
            _fikaPlayer.CommonPacket.SubPacket = ProceedPacket.FromValue(default, Knife.Item.Id, 0f, 0, EProceedType.QuickKnifeKick, false);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _fikaPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }

        internal void HandleResult(IResult result)
        {
            if (result.Succeed)
            {
                ConfirmCallback();
            }
        }
    }

    private sealed class GrenadeControllerHandler(FikaPlayer fikaPlayer, ThrowWeap throwWeap)
    {
        private readonly FikaPlayer _fikaPlayer = fikaPlayer;
        private readonly ThrowWeap _throwWeap = throwWeap;
        public Process<GrenadeHandsController, IGrenadeController> Process;
        public Action ConfirmCallback;

        internal FikaClientGrenadeController ReturnController()
        {
            return FikaClientGrenadeController.Create(_fikaPlayer, _throwWeap);
        }

        internal void HandleCallbackResponse(bool succeed)
        {
            if (succeed)
            {
                SendPacket();
            }
            Process.Confirm(succeed);
        }

        internal void SendCallbackRequest()
        {
            var client = FikaSingleton<FikaClient>.Instance;
            var id = _fikaPlayer.CreateProceedCallback(HandleCallbackResponse);
            var packet = new ProceedRequestPacket
            {
                NetId = _fikaPlayer.NetId,
                CallbackId = id,
                ProceedType = EProceedType.GrenadeClass,
                ItemId = _throwWeap.Id
            };
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }

        internal void SendPacket()
        {
            _fikaPlayer.CommonPacket.Type = ECommonSubPacketType.Proceed;
            _fikaPlayer.CommonPacket.SubPacket = ProceedPacket.FromValue(default, _throwWeap.Id, 0f, 0, EProceedType.GrenadeClass, false);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _fikaPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }

        internal void HandleResult(IResult result)
        {
            if (result.Succeed)
            {
                ConfirmCallback();
            }
        }
    }

    private sealed class QuickGrenadeControllerHandler(FikaPlayer fikaPlayer, ThrowWeap throwWeap)
    {
        private readonly FikaPlayer _fikaPlayer = fikaPlayer;
        private readonly ThrowWeap _throwWeap = throwWeap;
        public Process<QuickGrenadeThrowHandsController, IQuickGrenadeThrowController> Process;
        public Action ConfirmCallback;

        internal FikaClientQuickGrenadeController ReturnController()
        {
            return FikaClientQuickGrenadeController.Create(_fikaPlayer, _throwWeap);
        }

        internal void HandleCallbackResponse(bool succeed)
        {
            if (succeed)
            {
                SendPacket();
            }
            Process.Confirm(succeed);
        }

        internal void SendCallbackRequest()
        {
            var client = FikaSingleton<FikaClient>.Instance;
            var id = _fikaPlayer.CreateProceedCallback(HandleCallbackResponse);
            var packet = new ProceedRequestPacket
            {
                NetId = _fikaPlayer.NetId,
                CallbackId = id,
                ProceedType = EProceedType.QuickGrenadeThrow,
                ItemId = _throwWeap.Id
            };
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }

        internal void SendPacket()
        {
            _fikaPlayer.CommonPacket.Type = ECommonSubPacketType.Proceed;
            _fikaPlayer.CommonPacket.SubPacket = ProceedPacket.FromValue(default, _throwWeap.Id, 0f, 0, EProceedType.QuickGrenadeThrow, false);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _fikaPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }

        internal void HandleResult(IResult result)
        {
            if (result.Succeed)
            {
                ConfirmCallback();
            }
        }
    }

    private sealed class ProceedCallbackHandler(Action<bool> confirmAction)
    {
        private readonly Action<bool> _confirmAction = confirmAction;

        public void Handle(IResult result)
        {
            _confirmAction(result.Succeed);
        }
    }
}
#endregion
