using Diz.Jobs;
using EFT.Settings;
using EFT.Settings.Sound;
using JsonType;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.InputSystem;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using HarmonyLib;
using static Fika.Core.FikaPlugin;
using static Fika.Core.Networking.IFikaNetworkManager;

namespace Fika.Core.Main.Utils;

public static class FikaGlobals
{
    /// <summary>Builds an IL2CPP delegate for a native instance method.</summary>
    public static Il2CppSystem.Action Il2CppActionFor(Il2CppSystem.Object target, string methodName) =>
        Il2CppSystem.Delegate.CreateDelegate(Il2CppInterop.Runtime.Il2CppType.Of<Il2CppSystem.Action>(), target, methodName)
            .Cast<Il2CppSystem.Action>();
    public const string TransitTraderId = "656f0f98d80a697f855d34b1";
    public const string TransitTraderName = "BTR";
    public const string DefaultTransitId = "66f5750951530ca5ae09876d";
    public const string FikaGroupId = "Fika";

    public static int PingMask = LayerMask.GetMask(["HighPolyCollider", "Interactive", "Deadbody", "Player", "Loot", "Terrain"]);

    /// <summary>
    /// Use when no callback is needed to reduce allocations
    /// </summary>
    public static Callback EmptyCallbackDelegate => (Callback)EmptyCallback;
    /// <summary>
    /// Use when no callback is needed to reduce allocations
    /// </summary>
    public static Action EmptyActionDelegate => EmptyAction;

    public const int PingRange = 1000;

    private static readonly ManualLogSource _logger = Logger.CreateLogSource("FikaGlobals");

    internal static readonly List<EInteraction> BlockedInteractions =
    [
        EInteraction.DropBackpack, EInteraction.NightVisionOffGear, EInteraction.NightVisionOnGear,
        EInteraction.FaceshieldOffGear, EInteraction.FaceshieldOnGear, EInteraction.BipodForwardOn,
        EInteraction.BipodForwardOff, EInteraction.BipodBackwardOn, EInteraction.BipodBackwardOff
    ];

    internal static readonly List<EquipmentSlot> WeaponSlots =
    [
        EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster
    ];

    public static ISearchController SearchControllerSerializer
    {
        get
        {
            return FullySearchedSearchController.Instance;
        }
    }

    public static InputTree InputTree
    {
        get
        {
            if (_inputTree == null)
            {
                var inputObj = GameObject.Find("___Input")
                    ?? throw new NullReferenceException("Could not find InputTree object!");

                _inputTree = inputObj.GetComponent<InputTree>();
            }

            return _inputTree;
        }
    }

    private static InputTree _inputTree;

    public static VoipSettings VOIPHandler
    {
        get
        {
            if (_voipHandler == null)
            {
                _voipHandler = VoipSettings.Default;
                _voipHandler.VoipQualitySettings.Apply();
                _voipHandler.MicrophoneChecked = SoundSettingsGroup.CheckMicrophone();
                _voipHandler.VoipEnabled = true;
                var pttSettings = _voipHandler.PushToTalkSettings;
                pttSettings.SpeakingSecondsLimit = 20f;
                pttSettings.BlockingTime = 5f;
            }

            return _voipHandler;
        }
    }

    /// <summary>
    /// Checks whether the game client is in a raid
    /// </summary>
    /// <returns></returns>
    public static bool IsInRaid
    {
        get
        {
            return FikaSingleton<IFikaGame>.Instantiated;
        }
    }

    private static VoipSettings _voipHandler;

    internal static float GetOtherPlayerSensitivity()
    {
        return 1f;
    }

    internal static float GetLocalPlayerSensitivity()
    {
        return Singleton<SettingsManager>.Instance.Control.Settings.MouseSensitivity;
    }

    internal static float GetLocalPlayerAimingSensitivity()
    {
        return Singleton<SettingsManager>.Instance.Control.Settings.MouseAimingSensitivity;
    }

    public static float GetApplicationTime()
    {
        return Time.time;
    }

    internal static bool LampControllerNetIdNot0(LampController controller)
    {
        return controller.NetId != 0;
    }

    internal static int LampControllerGetNetId(LampController controller)
    {
        return controller.NetId;
    }

    internal static bool WindowBreakerAvailableToSync(WindowBreaker breaker)
    {
        return breaker.AvailableToSync;
    }

    internal static Item GetLootItemPositionItem(JsonLootItem positionClass)
    {
        return positionClass.Item;
    }

    internal static EBodyPart GetBodyPartFromCollider(BodyPartCollider collider)
    {
        return collider.BodyPartType;
    }

    internal static string FormatFileSize(long bytes)
    {
        const int unit = 1024;
        if (bytes < unit) { return $"{bytes} B"; }

        var exp = (int)(Math.Log(bytes) / Math.Log(unit));
        return $"{bytes / Math.Pow(unit, exp):F2} {("KMGTPE")[exp - 1]}B";
    }

    internal static void SpawnItemInWorld(Item item, FikaPlayer player)
    {
        StaticManager.BeginCoroutine(SpawnItemRoutine(item, player).WrapToIl2Cpp());
    }

    private static IEnumerator SpawnItemRoutine(Item item, FikaPlayer player)
    {
        var collection = new Il2CppSystem.Collections.Generic.List<ResourceKey>();
        foreach (var subItem in item.GetAllItems())
        {
            foreach (var resource in subItem.Template.AllResources)
                collection.Add(resource);
        }
        var loadTask = Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid, ObjectsFactory.AssemblyType.Online,
            collection, JobYieldPriority.Immediate, null, new Il2CppSystem.Threading.CancellationToken());

        WaitForEndOfFrame waitForEndOfFrame = new();
        while (!loadTask.IsCompleted)
        {
            yield return waitForEndOfFrame;
        }

        Singleton<GameWorld>.Instance.SetupItem(item, player,
            player.Transform.Original.position + player.Transform.Original.forward + (player.Transform.Original.up / 2), Quaternion.identity);

        if (player.IsYourPlayer)
        {
            ConsoleScreen.Log("Spawned item: " + item.ShortName.Localized());
            yield break;
        }
        ConsoleScreen.Log($"{player.Profile.Info.Nickname} has spawned item: {item.ShortName.Localized()}");
    }

    /// <summary>
    /// Forces the <see cref="ProfileInfo.MainProfileNickname"/> to be set on a profile
    /// </summary>
    /// <param name="infoClass"></param>
    /// <param name="nickname"></param>
    public static void SetProfileNickname(this ProfileInfo infoClass, string nickname)
    {
        infoClass.MainProfileNickname = nickname;
    }

    /// <summary>
    /// Checks whether a profile belongs to a player or an AI
    /// </summary>
    /// <param name="profile"></param>
    /// <returns>True if the profile belongs to a player, false if it belongs to an AI</returns>
    public static bool IsPlayerProfile(this Profile profile)
    {
        return NativeNullable<MongoID>.Read(profile, nameof(Profile.PetId)).HasValue ||
            profile.Info.RegistrationDate > 0 || !string.IsNullOrEmpty(profile.Info.MainProfileNickname);
    }

    /// <summary>
    /// Gets the current <see cref="IEftSession"/>
    /// </summary>
    /// <returns><see cref="IEftSession"/> of the application</returns>
    public static IEftSession GetSession()
    {
        if (TarkovApplication.Exist(out var tarkovApplication))
        {
            return tarkovApplication.Session;
        }

        _logger.LogError("GetSession: Could not find TarkovApplication!");
        return null;
    }

    /// <summary>
    /// Gets the current PMC or scav profile
    /// </summary>
    /// <param name="scav">If the scav profile should be returned</param>
    /// <returns><see cref="Profile"/> of chosen side</returns>
    public static Profile GetProfile(bool scav)
    {
        var session = GetSession();
        if (session == null)
        {
            _logger.LogError("GetProfile: Session was null!");
            return null;
        }

        if (!scav)
        {
            return session.Profile;
        }

        return session.ProfileOfPet;
    }

    /// <summary>
    /// Gets the current PMC or scav profile with trimmed data
    /// </summary>
    /// <param name="scav">If the scav profile should be returned</param>
    /// <returns>A trimmed <see cref="Profile"/> of chosen side</returns>
    public static Profile GetLiteProfile(bool scav)
    {
        var profile = GetProfile(scav);
        ProfileDescriptor liteDescriptor = new(profile, SearchControllerSerializer)
        {
            Encyclopedia = new Il2CppSystem.Collections.Generic.Dictionary<MongoID, bool>(),
            InsuredItems = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<InsuredProfileItems>(0),
            TaskConditionCounters = new Il2CppSystem.Collections.Generic.Dictionary<MongoID, TaskConditionCounterDescriptor>()
        };
        return new(liteDescriptor);
    }

    /// <summary>
    /// Gets the states from a <see cref="TacticalComboVisualController"/>
    /// </summary>
    /// <param name="controller"></param>
    /// <returns><see cref="LightsState"/></returns>
    public static LightsState GetFirearmLightStates(TacticalComboVisualController controller)
    {
        return controller.LightMod.GetLightState(false, false);
    }

    /// <summary>
    /// Gets the contained item in a <see cref="Slot"/>
    /// </summary>
    /// <param name="slot">The <see cref="Slot"/> to check</param>
    /// <returns>An <see cref="Item"/> in the slot</returns>
    public static Item GetContainedItem(Slot slot)
    {
        return slot.ContainedItem;
    }

    /// <summary>
    /// Gets a light states from a <see cref="LightComponent"/>
    /// </summary>
    /// <param name="component">The <see cref="LightComponent"/> to check</param>
    /// <returns>A new <see cref="LightsState"/> with data</returns>
    public static LightsState GetFirearmLightStatesFromComponent(LightComponent component)
    {
        return new LightsState
        {
            Id = component.Item.Id,
            IsActive = component.IsActive,
            LightMode = component.SelectedMode
        };
    }

    /// <summary>
    /// The <c>Profile.Info.GroupId</c> the local profile really had before
    /// <see cref="FikaPlayer.SetupMainPlayer"/> replaced it with <see cref="FikaGroupId"/>.
    /// </summary>
    internal static string LocalRealGroupId { get; private set; }

    /// <summary>
    /// The <c>Profile.Info.TeamId</c> the local profile really had before
    /// <see cref="FikaPlayer.SetupMainPlayer"/> replaced it with <see cref="FikaGroupId"/>.
    /// </summary>
    internal static string LocalRealTeamId { get; private set; }

    private static bool _localGroupCaptured;

    /// <summary>
    /// Remembers the group/team of the local profile, then keeps Fika's
    /// <see cref="FikaGroupId"/> sentinel out of every copy of that profile that
    /// leaves this machine (see <see cref="StripFikaGroupSentinel"/>).
    /// </summary>
    /// <param name="profile">The local player's profile.</param>
    public static void CaptureLocalGroup(Profile profile)
    {
        var info = profile?.Info;
        if (info == null)
        {
            return;
        }

        // SetupMainPlayer is called once per local player; a second call for the same
        // profile would otherwise capture the sentinel we just wrote and hand it back
        // out through StripFikaGroupSentinel.
        if (_localGroupCaptured && string.Equals(info.GroupId, FikaGroupId, StringComparison.Ordinal))
        {
            return;
        }

        LocalRealGroupId = info.GroupId;
        LocalRealTeamId = info.TeamId;
        _localGroupCaptured = true;
    }

    /// <summary>
    /// Restores the real group/team on a descriptor built from the local profile, so
    /// the <see cref="FikaGroupId"/> sentinel that <see cref="FikaPlayer.SetupMainPlayer"/>
    /// writes into the local profile is never announced to peers or saved to the server.
    /// <para>
    /// Third-party mods read <c>Player.GroupId</c> (= <c>Profile.Info.GroupId</c>) to tell
    /// teammates from strangers, so a sentinel that claims every player is in the main
    /// player's group makes their classification collapse. Only the local player keeps
    /// the sentinel; it is the value the native secret-exfiltration share event is matched
    /// against (see <c>FikaExfilManager</c>).
    /// </para>
    /// </summary>
    /// <param name="descriptor">The descriptor about to be serialized.</param>
    public static void StripFikaGroupSentinel(ProfileDescriptor descriptor)
    {
        var info = descriptor?.Info;
        if (info == null)
        {
            return;
        }

        if (string.Equals(info.GroupId, FikaGroupId, StringComparison.Ordinal))
        {
            info.GroupId = _localGroupCaptured ? LocalRealGroupId : string.Empty;
        }

        if (string.Equals(info.TeamId, FikaGroupId, StringComparison.Ordinal))
        {
            info.TeamId = _localGroupCaptured ? LocalRealTeamId : string.Empty;
        }
    }

    /// <summary>
    /// Makes sure an announced profile descriptor carries a usable player name. The Fika
    /// nameplate reads <c>Info.MainProfileNickname</c> (FikaHealthBar.cs:267) while the map mods
    /// read <c>Info.Nickname</c>, so an empty field on either side leaves a teammate without a
    /// label. Both fields are filled from each other and the fix is logged, so one raid shows
    /// whether the profile that was announced was already missing one of them.
    /// </summary>
    /// <param name="descriptor">The descriptor about to be serialized.</param>
    public static void EnsureProfileNames(ProfileDescriptor descriptor)
    {
        var info = descriptor?.Info;
        if (info == null)
        {
            return;
        }

        var nickname = info.Nickname;
        var mainNickname = info.MainProfileNickname;
        var nicknameMissing = string.IsNullOrEmpty(nickname);
        var mainNicknameMissing = string.IsNullOrEmpty(mainNickname);
        if (!nicknameMissing && !mainNicknameMissing)
        {
            return;
        }

        if (nicknameMissing && !mainNicknameMissing)
        {
            info.Nickname = mainNickname;
        }
        else if (mainNicknameMissing && !nicknameMissing)
        {
            info.MainProfileNickname = nickname;
        }

        LogWarning($"[Nameplate] announced profile {descriptor.Id} had Nickname='{nickname}' " +
            $"MainProfileNickname='{mainNickname}', sent Nickname='{info.Nickname}' " +
            $"MainProfileNickname='{info.MainProfileNickname}'");
    }

    /// <summary>
    /// True for the human players of this co-op session: the local <see cref="FikaPlayer"/>
    /// and the <see cref="ObservedPlayer"/>s of the other clients. False for everything the
    /// game simulates as AI (the host's <see cref="FikaBot"/> and AI replicated to a client).
    /// <para>
    /// This replaces the old <c>Player.GroupId == "Fika"</c> test. The profile group can no
    /// longer carry that meaning: it is what third-party mods read, so remote players keep
    /// the group their own profile carries.
    /// </para>
    /// </summary>
    /// <param name="player">The <see cref="Player"/> to check</param>
    /// <returns>True if the player is another human participant of this session</returns>
    public static bool IsCoopPlayer(this Player player)
    {
        if (player == null)
        {
            return false;
        }

        // Injected (managed) players are handed out either as themselves or as an
        // interop proxy depending on where the reference came from; accept both.
        var fikaPlayer = player as FikaPlayer ?? player.TryCast<FikaPlayer>();
        if (fikaPlayer == null)
        {
            return false;
        }

        if (fikaPlayer.IsObservedAI)
        {
            return false;
        }

        return fikaPlayer is not FikaBot;
    }

    /// <summary>
    /// <see cref="IsCoopPlayer(Player)"/> for the interface references the game hands out
    /// (damage aggressors, kill notifications, ...).
    /// </summary>
    /// <param name="player">The <see cref="IPlayer"/> to check</param>
    /// <returns>True if the player is another human participant of this session</returns>
    public static bool IsCoopPlayer(this IPlayer player)
    {
        if (player == null)
        {
            return false;
        }

        var concrete = player as Player ?? player.TryCast<Player>();
        return concrete != null && concrete.IsCoopPlayer();
    }

    /// <summary>
    /// The <see cref="EPlayerSide"/> a role belongs to, mirroring the native
    /// <c>EFT.Player.SetDogtagInfo</c> (EFT 4.0 <c>Player.cs:30237</c>): <c>pmcUSEC</c> is a
    /// USEC and <c>pmcBEAR</c> is a BEAR, whatever the profile itself says.
    /// <para>
    /// SPT 5.0 rewrites the side of every generated PMC bot to <c>Savage</c> after creating it
    /// (<c>SPTarkov.Server.Core.Controllers.BotController.TryGenerateSingleBot</c>:
    /// <c>if (botBase.Info.Side is "Bear" or "Usec") botBase.Info.Side = "Savage"</c>), so an AI
    /// PMC reaches the game with <c>Profile.Info.Side == Savage</c> while its role still says
    /// <c>pmcUSEC</c>/<c>pmcBEAR</c>. Everything that classifies actors by side - the dog tag
    /// itself and third-party consumers such as "SPT Dynamic Maps" - has to go through the role.
    /// </para>
    /// </summary>
    /// <param name="role">The <see cref="WildSpawnType"/> of the actor</param>
    /// <param name="side">The side the profile currently carries</param>
    /// <returns>The side the actor must present</returns>
    public static EPlayerSide GetFactionSide(WildSpawnType role, EPlayerSide side)
    {
        return role switch
        {
            WildSpawnType.pmcUSEC => EPlayerSide.Usec,
            WildSpawnType.pmcBEAR => EPlayerSide.Bear,
            _ => side
        };
    }

    /// <summary>
    /// <see cref="GetFactionSide(WildSpawnType, EPlayerSide)"/> for a live player.
    /// </summary>
    /// <param name="player">The player to inspect</param>
    /// <returns>The side the player must present</returns>
    public static EPlayerSide GetFactionSide(this Player player)
    {
        var info = player?.Profile?.Info;
        var settings = info?.Settings;
        return settings == null ? (info?.Side ?? EPlayerSide.Savage) : GetFactionSide(settings.Role, info.Side);
    }

    /// <summary>
    /// Makes an AI actor present the faction its role says it belongs to. SPT 5.0 marks every
    /// PMC bot as <see cref="EPlayerSide.Savage"/>, so dog tag generation and side-based
    /// classifiers (for example "SPT Dynamic Maps") otherwise read a <c>pmcUSEC</c>/<c>pmcBEAR</c>
    /// bot as a Scav. Only AI actors are touched, and only when the profile disagrees with the
    /// role.
    /// <para>
    /// Call this after the spawn-time "spawned in session" handling
    /// (<c>CoopHandler.SpawnObservedPlayer</c> / <c>HostGameController.CreateBot</c>) has already
    /// run with the untouched side, so loot semantics stay exactly as they were.
    /// </para>
    /// </summary>
    /// <param name="player">The AI actor to correct</param>
    /// <param name="source">Where the actor came from, for the log line</param>
    /// <returns>True if the side was corrected</returns>
    public static bool ApplyFactionSide(Player player, string source)
    {
        var profile = player?.Profile;
        var info = profile?.Info;
        var settings = info?.Settings;
        if (settings == null)
        {
            return false;
        }

        var role = settings.Role;
        var faction = GetFactionSide(role, info.Side);
        if (faction == info.Side)
        {
            return false;
        }

        LogInfo($"[FactionSide] {source}: profileId={profile.ProfileId}, role={role}, " +
            $"side={info.Side}->{faction} (SPT 5.0 marks every PMC bot as Savage, consumers such " +
            "as SPT Dynamic Maps classify by Profile.Side)");
        info.Side = faction;
        return true;
    }

    /// <summary>
    /// Checks whether the player is part of the player group
    /// </summary>
    /// <param name="player">The <see cref="Player"/> to check</param>
    /// <returns>True if in the player group</returns>
    public static bool IsGroupMember(this Player player)
    {
        return player.IsCoopPlayer();
    }

    /// <summary>
    /// Unsubscribes all delegates from an <see cref="Action{T}"/>
    /// </summary>
    /// <param name="action"></param>
    public static Action ClearDelegates(Action action)
    {
        var list = action.GetInvocationList();
        for (var i = 0; i < list.Length; i++)
        {
#if DEBUG
            LogWarning($"Clearing {list[i].Method.Name}");
#endif
            action = (Action)Delegate.Remove(action, list[i]);
        }

        return action;
    }

    /// <summary>
    /// Unsubscribes all delegates from an <see cref="Action{T}"/>
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="action"></param>
    public static Action<T> ClearDelegates<T>(Action<T> action) where T : class
    {
        var list = action.GetInvocationList();
        for (var i = 0; i < list.Length; i++)
        {
#if DEBUG
            LogWarning($"Clearing {list[i].Method.Name}");
#endif
            action = (Action<T>)Delegate.Remove(action, list[i]);
        }

        return action;
    }

    /// <summary>
    /// Unsubscribes all delegates from an <see cref="Action{T, Y}"/>
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="Y"></typeparam>
    /// <param name="action"></param>
    public static Action<T, Y> ClearDelegates<T, Y>(Action<T, Y> action)
        where T : class
        where Y : class
    {
        var list = action.GetInvocationList();
        for (var i = 0; i < list.Length; i++)
        {
#if DEBUG
            LogWarning($"Clearing {list[i].Method.Name}");
#endif
            action = (Action<T, Y>)Delegate.Remove(action, list[i]);
        }

        return action;
    }

    public static void LogInfo(string message, [CallerMemberName] string caller = "")
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        Instance.FikaLogger.LogInfo($"[{caller}]: {message}");
    }

    public static void LogWarning(string message, [CallerMemberName] string caller = "")
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        Instance.FikaLogger.LogWarning($"[{caller}]: {message}");
    }

    public static void LogError(string message, [CallerMemberName] string caller = "")
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        Instance.FikaLogger.LogError($"[{caller}]: {message}");
    }

    public static void LogError(object obj, [CallerMemberName] string caller = "")
    {
        Instance.FikaLogger.LogError($"[{caller}]: {obj}");
    }

    public static void LogFatal(string message, [CallerMemberName] string caller = "")
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        Instance.FikaLogger.LogFatal($"[{caller}]: {message}");
    }

    public static int ToNumber(this ESendRate rate)
    {
        return rate switch
        {
            ESendRate.Low => 10,
            ESendRate.Medium => 20,
            ESendRate.High => 30,
            _ => 20,
        };
    }

    public static void EmptyAction()
    {

    }

    private static void EmptyCallback(IResult result)
    {

    }

    /// <summary>
    /// Converts the <see cref="ELoadPriority"/> to a delegate
    /// </summary>
    /// <param name="priority">The priority</param>
    /// <returns>A new <see cref="YieldDelegate"/> for <see cref="Components.CoopHandler.SpawnPlayer(Components.CoopHandler.SpawnObject)"/></returns>
    public static YieldDelegate ToLoadPriorty(this ELoadPriority priority)
    {
        return priority switch
        {
            ELoadPriority.Low => JobYieldPriority.Low,
            ELoadPriority.Medium => JobYieldPriority.General,
            ELoadPriority.High => JobYieldPriority.Immediate,
            _ => JobYieldPriority.Low,
        };
    }

    /// <summary>
    /// Migrates IL labels
    /// </summary>
    /// <param name="codes">List of instructions</param>
    /// <param name="index">Index to start at</param>
    /// <param name="count">Iterations</param>
    public static void MigrateLabels(List<CodeInstruction> codes, int index, int count)
    {
        var targetIndex = index + count;

        if (targetIndex < codes.Count)
        {
            var labelsToMove = new List<Label>();
            for (var i = index; i < targetIndex; i++)
            {
                labelsToMove.AddRange(codes[i].labels);
            }

            codes[targetIndex].labels.AddRange(labelsToMove);
        }
    }

    /// <summary>
    /// Checks whether all modifiers are pressed for a <see cref="KeyboardShortcut"/>
    /// </summary>
    /// <param name="shortcut">The shortcut to check</param>
    /// <returns><see langword="true"/> if all modifiers are pressed; <see langword="false"/> if not</returns>
    public static bool AreModifiersPressed(KeyboardShortcut shortcut)
    {
        foreach (var key in shortcut.Modifiers)
        {
            if (!Input.GetKey(key))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Dynamically creates and compiles a high-performance getter delegate for a private instance field using Expression Trees.
    /// </summary>
    /// <typeparam name="T">The declaring <see cref="Type"/> of the class containing the field.</typeparam>
    /// <typeparam name="TResult">The <see cref="Type"/> of the field value to retrieve.</typeparam>
    /// <param name="fieldName">The exact case-sensitive name of the private field.</param>
    /// <returns>A compiled <see cref="Func{T, TResult}"/> delegate that yields the field value when invoked.</returns>
    /// <exception cref="NullReferenceException">Thrown when the specified <paramref name="fieldName"/> cannot be found via reflection.</exception>
    public static Func<T, TResult> CreateGetter<T, TResult>(string fieldName)
    {
        var fieldInfo = HarmonyLib.AccessTools.Field(typeof(T), fieldName);
        var propertyInfo = HarmonyLib.AccessTools.Property(typeof(T), fieldName);

        if (fieldInfo != null || propertyInfo?.GetMethod != null)
        {
            var targetParam = Expression.Parameter(typeof(T), "instance");
            var fieldAccess = fieldInfo != null ? Expression.Field(targetParam, fieldInfo) : Expression.Property(targetParam, propertyInfo);

            return Expression.Lambda<Func<T, TResult>>(fieldAccess, targetParam)
                .Compile();
        }
        else
        {
            throw new NullReferenceException($"Failed to find private field [{fieldName}] in {typeof(T).Name}.");
        }
    }

    /// <summary>
    /// Dynamically creates and compiles a high-performance setter delegate for a private instance field using Expression Trees.
    /// </summary>
    /// <typeparam name="T">The declaring <see cref="Type"/> of the class containing the field.</typeparam>
    /// <typeparam name="TResult">The <see cref="Type"/> of the field value to assign.</typeparam>
    /// <param name="fieldName">The exact case-sensitive name of the private field.</param>
    /// <returns>A compiled <see cref="Action{T, TResult}"/> delegate that assigns a new value to the field when invoked.</returns>
    /// <exception cref="NullReferenceException">Thrown when the specified <paramref name="fieldName"/> cannot be found via reflection.</exception>
    public static Action<T, TResult> CreateSetter<T, TResult>(string fieldName)
    {
        var fieldInfo = HarmonyLib.AccessTools.Field(typeof(T), fieldName);
        var propertyInfo = HarmonyLib.AccessTools.Property(typeof(T), fieldName);

        if (fieldInfo != null || propertyInfo?.SetMethod != null)
        {
            var targetParam = Expression.Parameter(typeof(T), "instance");
            var fieldAccess = fieldInfo != null ? Expression.Field(targetParam, fieldInfo) : Expression.Property(targetParam, propertyInfo);
            var valueParam = Expression.Parameter(typeof(TResult), "value");
            var assignExpr = Expression.Assign(fieldAccess, valueParam);

            return Expression.Lambda<Action<T, TResult>>(assignExpr, targetParam, valueParam)
                .Compile();
        }
        else
        {
            throw new NullReferenceException($"Failed to find private field [{fieldName}] in {typeof(T).Name}.");
        }
    }

    /// <summary>
    /// Checks whether the shot type is a misfire
    /// </summary>
    /// <returns><see langword="true"/> if the shot is a misfire; otherwise <see langword="false"/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsMisfire(this EShotType shotType)
    {
        return shotType >= EShotType.Misfire;
    }
}
