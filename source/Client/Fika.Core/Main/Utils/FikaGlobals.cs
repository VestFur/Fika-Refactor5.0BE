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
    /// <summary>
    /// 为一个原生实例方法构建 IL2CPP 委托。
    /// <para>
    /// 警告（第 8 轮，analysis/delegate-targets/REPORT.md）：<c>Il2CppSystem.Delegate.CreateDelegate</c>
    /// 也会接受<em>注入</em>类的方法，随后该委托会携带那个方法
    /// 注册的 MethodInfo——引擎通过 interop invoker 到达它，而该 invoker 结尾的
    /// <c>calli</c> 指向该方法的跳板 thunk，这正是
    /// 2026-10-10 主机战局中那次 coreclr fail-fast 的成因。这里请传入原生对象，或改为把托管 lambda 交给引擎
    /// （正如 <c>TarkovApplication_LocalGameCreator_Patch.AddRaidDisposable</c> 现在所做的那样）。目前没有调用点
    /// 使用这个辅助方法；它仅为原生目标保留。
    /// </para>
    /// </summary>
    public static System.Action Il2CppActionFor(Il2CppSystem.Object target, string methodName) =>
        Il2CppSystem.Delegate.CreateDelegate(Il2CppInterop.Runtime.Il2CppType.Of<System.Action>(), target, methodName)
            .Cast<System.Action>();
    public const string TransitTraderId = "656f0f98d80a697f855d34b1";
    public const string TransitTraderName = "BTR";
    public const string DefaultTransitId = "66f5750951530ca5ae09876d";
    public const string FikaGroupId = "Fika";

    public static int PingMask = LayerMask.GetMask(["HighPolyCollider", "Interactive", "Deadbody", "Player", "Loot", "Terrain"]);

    /// <summary>
    /// 在不需要回调时使用，以减少分配
    /// </summary>
    public static Callback EmptyCallbackDelegate => (Callback)EmptyCallback;
    /// <summary>
    /// 在不需要回调时使用，以减少分配
    /// </summary>
    public static Action EmptyActionDelegate => EmptyAction;

    private static Il2CppSystem.Action _emptyNativeAction;

    /// <summary>
    /// 用于基类调用的原生空操作，这些调用绝不能把 null 回调交给 IL2CPP 方法：原生
    /// 一侧接受的是原生委托对象，而绝不是托管闭包。生成的
    /// Il2CppSystem.Action.op_Implicit(System.Action) 会构建这座桥；结果被缓存，因为
    /// 每次转换都会分配一个原生委托。
    /// </summary>
    public static Il2CppSystem.Action EmptyNativeAction =>
        _emptyNativeAction ??= (Il2CppSystem.Action)EmptyActionDelegate;

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
    /// 检查游戏客户端是否处于战局中
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
        StaticManager.BeginCoroutine(EngineCoroutines.Wrap(SpawnItemRoutine(item, player), "FikaGlobals.SpawnItemInWorld"));
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
    /// 强制在某个 profile 上设置 <see cref="ProfileInfo.MainProfileNickname"/>
    /// </summary>
    /// <param name="infoClass"></param>
    /// <param name="nickname"></param>
    public static void SetProfileNickname(this ProfileInfo infoClass, string nickname)
    {
        infoClass.MainProfileNickname = nickname;
    }

    /// <summary>
    /// 为玩家渲染显示的名字。
    /// <para>
    /// Fika 把人类玩家的*主档案*名（PMC 名，这个名号在该玩家以 scav 身份游玩时也仍然是显示名）
    /// 存放在 <c>Info.MainProfileNickname</c> 里
    /// （<see cref="FikaPlayer.Init"/> 与 <c>FikaClient.OnPeerConnected</c> 会在
    /// profile 被公布之前填入它）。游戏对 AI 会把该字段留空，AI 唯一的名称是
    /// <c>Info.Nickname</c>，因此每一处可能取到 bot 的读取都必须走这里，而不是
    /// 自行读取该字段：名牌、击杀与生成通知，以及
    /// ping 标签，在一场普通战局中都会对 bot 显示。
    /// </para>
    /// </summary>
    /// <param name="profile">要取名的 profile（可以为 null）。</param>
    /// <returns>主档案名；没有主名时返回昵称。</returns>
    public static string GetDisplayName(this Profile profile)
    {
        var info = profile?.Info;
        if (info == null)
        {
            return string.Empty;
        }

        var mainNickname = info.MainProfileNickname;
        return !string.IsNullOrEmpty(mainNickname) ? mainNickname : info.Nickname;
    }

    /// <summary>
    /// 检查某个 profile 属于玩家还是 AI
    /// </summary>
    /// <param name="profile"></param>
    /// <returns>若该 profile 属于玩家则为 true，若属于 AI 则为 false</returns>
    public static bool IsPlayerProfile(this Profile profile)
    {
        return NativeNullable<MongoID>.Read(profile, nameof(Profile.PetId)).HasValue ||
            profile.Info.RegistrationDate > 0 || !string.IsNullOrEmpty(profile.Info.MainProfileNickname);
    }

    /// <summary>
    /// 获取当前的 <see cref="IEftSession"/>
    /// </summary>
    /// <returns>应用程序的 <see cref="IEftSession"/></returns>
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
    /// 获取当前的 PMC 或 scav profile
    /// </summary>
    /// <param name="scav">是否应返回 scav profile</param>
    /// <returns>所选阵营的 <see cref="Profile"/></returns>
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
    /// 获取当前 PMC 或 scav profile 的裁剪版数据
    /// </summary>
    /// <param name="scav">是否应返回 scav profile</param>
    /// <returns>所选阵营的裁剪版 <see cref="Profile"/></returns>
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
    /// 从 <see cref="TacticalComboVisualController"/> 获取状态
    /// </summary>
    /// <param name="controller"></param>
    /// <returns><see cref="LightsState"/></returns>
    public static LightsState GetFirearmLightStates(TacticalComboVisualController controller)
    {
        return controller.LightMod.GetLightState(false, false);
    }

    /// <summary>
    /// 获取 <see cref="Slot"/> 中盛装的物品
    /// </summary>
    /// <param name="slot">要检查的 <see cref="Slot"/></param>
    /// <returns>槽位中的 <see cref="Item"/></returns>
    public static Item GetContainedItem(Slot slot)
    {
        return slot.ContainedItem;
    }

    /// <summary>
    /// 从 <see cref="LightComponent"/> 获取灯光状态
    /// </summary>
    /// <param name="component">要检查的 <see cref="LightComponent"/></param>
    /// <returns>带数据的新 <see cref="LightsState"/></returns>
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
    /// 本地 profile 真正拥有的 <c>Profile.Info.GroupId</c>，即在
    /// <see cref="FikaPlayer.SetupMainPlayer"/> 用 <see cref="FikaGroupId"/> 把它替换之前。
    /// </summary>
    internal static string LocalRealGroupId { get; private set; }

    /// <summary>
    /// 本地 profile 真正拥有的 <c>Profile.Info.TeamId</c>，即在
    /// <see cref="FikaPlayer.SetupMainPlayer"/> 用 <see cref="FikaGroupId"/> 把它替换之前。
    /// </summary>
    internal static string LocalRealTeamId { get; private set; }

    private static bool _localGroupCaptured;

    /// <summary>
    /// 记住本地 profile 的组/队伍，然后确保 Fika 的
    /// <see cref="FikaGroupId"/> 哨兵值不会出现在该 profile 每个
    /// 离开本机的副本中（见 <see cref="StripFikaGroupSentinel"/>）。
    /// </summary>
    /// <param name="profile">本地玩家的 profile。</param>
    public static void CaptureLocalGroup(Profile profile)
    {
        var info = profile?.Info;
        if (info == null)
        {
            return;
        }

        // SetupMainPlayer 对每个本地玩家只调用一次；若对同一个 profile 再调用一次，
        // 就会捕获我们刚写入的哨兵值，并把它当作真实值
        // 通过 StripFikaGroupSentinel 再交出去。
        if (_localGroupCaptured && string.Equals(info.GroupId, FikaGroupId, StringComparison.Ordinal))
        {
            return;
        }

        LocalRealGroupId = info.GroupId;
        LocalRealTeamId = info.TeamId;
        _localGroupCaptured = true;
    }

    /// <summary>
    /// 在由本地 profile 构建的描述符上恢复真实的组/队伍，这样
    /// <see cref="FikaGroupId"/> 这个哨兵值——由 <see cref="FikaPlayer.SetupMainPlayer"/>
    /// 写入本地 profile——就绝不会被公布给对等端，也不会被保存到服务器。
    /// <para>
    /// 第三方 mod 会读 <c>Player.GroupId</c>（= <c>Profile.Info.GroupId</c>）来区分
    /// 队友和陌生人，因此一个宣称每个玩家都在主玩家组内的哨兵值
    /// 会让它们的分类彻底失效。只有本地玩家保留
    /// 该哨兵值；它正是原生秘密撤离分享事件所要匹配的值
    /// （见 <c>FikaExfilManager</c>）。
    /// </para>
    /// </summary>
    /// <param name="descriptor">即将被序列化的描述符。</param>
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
    /// 针对已公布 profile 描述符的最后一道防线：一个*完全没有*名字的 profile
    /// 在观察端是不可用的，因此在只有主档案名留存下来时，
    /// 会从主档案名恢复出一个 Nickname。
    /// <para>
    /// <c>Info.MainProfileNickname</c> 现在被刻意地不再回写。它是
    /// "主档案"名（人类玩家的 PMC 名，在以 scav 身份游玩时也是），而游戏
    /// 对 AI 会把它留空：SPT 的 <c>database/bots/base.json</c> 根本没有这个字段，
    /// 只有玩家 scav（<c>BotGenerator.GeneratePlayerScav</c>）或一个 "<c>assault</c>" bot 在
    /// <c>ChanceAssaultScavHasPlayerScavName</c> 中胜出，
    /// (<c>BotGenerator.GenerateBot</c> -> <c>BotNameService.AddRandomPmcNameToBotMainProfileNicknameProperty</c>)
    /// 才会拥有一个。把 bot 的昵称公布为其主档案名，就等于宣称这个 bot 是个
    /// 人类：<see cref="IsPlayerProfile"/> 会把非空的 MainProfileNickname 当作该标记
    /// （bot 的 RegistrationDate 为 0 且没有 PetId），而 <c>ObservedPlayer.Init</c> 会对这类
    /// profile 关闭剔除处理器。
    /// </para>
    /// <para>
    /// 读取方对 AI 并不需要该字段：Fika 的名牌会回退到
    /// <c>Info.Nickname</c>（FikaHealthBar.CreateHealthBar），而且所有地图 mod 读的本来就是
    /// <c>Info.Nickname</c>。人类 profile 在源头就被填好，也就是名字已知的地方——
    /// <c>FikaPlayer.Init</c> 与 <c>FikaClient.OnPeerConnected</c> 会在
    /// profile 被公布之前调用 <see cref="SetProfileNickname(ProfileInfo, string)"/>。
    /// </para>
    /// </summary>
    /// <param name="descriptor">即将被序列化的描述符。</param>
    public static void EnsureProfileNames(ProfileDescriptor descriptor)
    {
        var info = descriptor?.Info;
        if (info == null)
        {
            return;
        }

        var nickname = info.Nickname;
        if (!string.IsNullOrEmpty(nickname))
        {
            // 无需修复：空的 MainProfileNickname 是 AI profile 的正常状态，
            // 观察端会为它渲染昵称。
            return;
        }

        var mainNickname = info.MainProfileNickname;
        if (!string.IsNullOrEmpty(mainNickname))
        {
            info.Nickname = mainNickname;
            ReportNameDefect($"[Nameplate] announced profile {descriptor.Id} carried no Nickname; " +
                $"it was filled from MainProfileNickname='{mainNickname}'");
            return;
        }

        ReportNameDefect($"[Nameplate] announced profile {descriptor.Id} carries neither Nickname " +
            "nor MainProfileNickname; it will render without a label");
    }

    /// <summary>
    /// 上面的防线只在一个 profile 真的没有名字时才会发声——只有那种情况
    /// 才会改变队友所见的内容。上报量限制在前几个不同 profile 上，这样一个
    /// 被循环公布的 profile 无法刷满日志。
    /// </summary>
    private static readonly HashSet<string> _reportedNameDefects = new(StringComparer.Ordinal);

    private static void ReportNameDefect(string message)
    {
        if (_reportedNameDefects.Count >= 20)
        {
            return;
        }

        if (_reportedNameDefects.Add(message))
        {
            LogWarning(message);
        }
    }

    /// <summary>
    /// 对本合作会话中的人类玩家为 true：本地的 <see cref="FikaPlayer"/>
    /// 以及其他客户端的 <see cref="ObservedPlayer"/>。对游戏当作 AI 模拟的一切
    /// 都为 false（主机的 <see cref="FikaBot"/> 以及复制到客户端的 AI）。
    /// <para>
    /// 这取代了旧的 <c>Player.GroupId == "Fika"</c> 判断。profile 的组字段不再
    /// 承载该含义：它是第三方 mod 所读的内容，因此远端玩家保留
    /// 自己 profile 所携带的组。
    /// </para>
    /// </summary>
    /// <param name="player">要检查的 <see cref="Player"/></param>
    /// <returns>若该玩家是本会话的另一名人类参与者则为 true</returns>
    public static bool IsCoopPlayer(this Player player)
    {
        if (player == null)
        {
            return false;
        }

        // 注入（托管）玩家可能以自身形式、也可能以 interop 代理形式
        // 交出来，取决于引用来自何处；两种都接受。
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
    /// 面向游戏交出的接口引用的 <see cref="IsCoopPlayer(Player)"/>
    /// （伤害施加者、击杀通知等）。
    /// </summary>
    /// <param name="player">要检查的 <see cref="IPlayer"/></param>
    /// <returns>若该玩家是本会话的另一名人类参与者则为 true</returns>
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
    /// 某个角色所属的 <see cref="EPlayerSide"/>，与原生
    /// <c>EFT.Player.SetDogtagInfo</c> 保持一致（EFT 4.0 <c>Player.cs:30237</c>）：<c>pmcUSEC</c> 是
    /// USEC，<c>pmcBEAR</c> 是 BEAR，无论 profile 自己怎么说。
    /// <para>
    /// SPT 5.0 在创建每个生成的 PMC bot 之后，会把它的阵营改写成 <c>Savage</c>
    /// （<c>SPTarkov.Server.Core.Controllers.BotController.TryGenerateSingleBot</c>：
    /// <c>if (botBase.Info.Side is "Bear" or "Usec") botBase.Info.Side = "Savage"</c>），因此一个 AI
    /// PMC 进入游戏时 <c>Profile.Info.Side == Savage</c>，而它的角色仍然写着
    /// <c>pmcUSEC</c>/<c>pmcBEAR</c>。所有按阵营对角色做分类的东西——狗牌
    /// 本身，以及诸如 "SPT Dynamic Maps" 的第三方使用方——都必须走角色。
    /// </para>
    /// </summary>
    /// <param name="role">该角色的 <see cref="WildSpawnType"/></param>
    /// <param name="side">profile 当前携带的阵营</param>
    /// <returns>该角色必须呈现的阵营</returns>
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
    /// 面向存活玩家的 <see cref="GetFactionSide(WildSpawnType, EPlayerSide)"/>。
    /// </summary>
    /// <param name="player">要检查的玩家</param>
    /// <returns>该玩家必须呈现的阵营</returns>
    public static EPlayerSide GetFactionSide(this Player player)
    {
        var info = player?.Profile?.Info;
        var settings = info?.Settings;
        return settings == null ? (info?.Side ?? EPlayerSide.Savage) : GetFactionSide(settings.Role, info.Side);
    }

    /// <summary>
    /// 让一个 AI 角色呈现出其角色所声称的阵营。SPT 5.0 会把每个
    /// PMC bot 标记为 <see cref="EPlayerSide.Savage"/>，因此狗牌生成与基于阵营的
    /// 分类器（例如 "SPT Dynamic Maps"）否则会把一个 <c>pmcUSEC</c>/<c>pmcBEAR</c>
    /// bot 读成 Scav。只有 AI 角色会被改动，且仅在 profile 与
    /// 角色不一致时。
    /// <para>
    /// 请在生成时的"已在会话中生成"处理
    /// （<c>CoopHandler.SpawnObservedPlayer</c> / <c>HostGameController.CreateBot</c>）已经以
    /// 未改动的阵营运行过之后再调用，这样战利品语义与原来完全一致。
    /// </para>
    /// </summary>
    /// <param name="player">要修正的 AI 角色</param>
    /// <param name="source">该角色的来源，用于日志行</param>
    /// <returns>若阵营被修正则为 true</returns>
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
    /// 检查该玩家是否属于玩家队伍
    /// </summary>
    /// <param name="player">要检查的 <see cref="Player"/></param>
    /// <returns>若属于玩家队伍则为 true</returns>
    public static bool IsGroupMember(this Player player)
    {
        return player.IsCoopPlayer();
    }

    /// <summary>
    /// 从 <see cref="Action{T}"/> 上取消订阅所有委托
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
    /// 从 <see cref="Action{T}"/> 上取消订阅所有委托
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
    /// 从 <see cref="Action{T, Y}"/> 上取消订阅所有委托
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
    /// 把 <see cref="ELoadPriority"/> 转换为委托
    /// </summary>
    /// <param name="priority">优先级</param>
    /// <returns>新的 <see cref="YieldDelegate"/>，供 <see cref="Components.CoopHandler.SpawnPlayer(Components.CoopHandler.SpawnObject)"/> 使用</returns>
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
    /// 迁移 IL 标签
    /// </summary>
    /// <param name="codes">指令列表</param>
    /// <param name="index">起始索引</param>
    /// <param name="count">迭代次数</param>
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
    /// 检查 <see cref="KeyboardShortcut"/> 的所有修饰键是否都被按下
    /// </summary>
    /// <param name="shortcut">要检查的快捷键</param>
    /// <returns>若所有修饰键都被按下则为 <see langword="true"/>；否则为 <see langword="false"/></returns>
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
    /// 使用表达式树动态创建并编译一个用于私有实例字段的高性能 getter 委托。
    /// </summary>
    /// <typeparam name="T">包含该字段的类的声明 <see cref="Type"/>。</typeparam>
    /// <typeparam name="TResult">要取出的字段值的 <see cref="Type"/>。</typeparam>
    /// <param name="fieldName">私有字段的精确名称（区分大小写）。</param>
    /// <returns>已编译的 <see cref="Func{T, TResult}"/> 委托，调用时返回该字段的值。</returns>
    /// <exception cref="NullReferenceException">当无法通过反射找到指定的 <paramref name="fieldName"/> 时抛出。</exception>
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
    /// 使用表达式树动态创建并编译一个用于私有实例字段的高性能 setter 委托。
    /// </summary>
    /// <typeparam name="T">包含该字段的类的声明 <see cref="Type"/>。</typeparam>
    /// <typeparam name="TResult">要赋入的字段值的 <see cref="Type"/>。</typeparam>
    /// <param name="fieldName">私有字段的精确名称（区分大小写）。</param>
    /// <returns>已编译的 <see cref="Action{T, TResult}"/> 委托，调用时把新值赋给该字段。</returns>
    /// <exception cref="NullReferenceException">当无法通过反射找到指定的 <paramref name="fieldName"/> 时抛出。</exception>
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
    /// 检查该射击类型是否为哑火
    /// </summary>
    /// <returns>若该次射击是哑火则为 <see langword="true"/>；否则为 <see langword="false"/></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsMisfire(this EShotType shotType)
    {
        return shotType >= EShotType.Misfire;
    }
}
