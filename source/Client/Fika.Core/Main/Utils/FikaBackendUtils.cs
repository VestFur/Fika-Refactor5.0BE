using Diz.Binding;
using EFT.Communications;
using JsonType;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.UI;
using EFT.UI.Matchmaker;
using Fika.Core.Main.Components;
using Fika.Core.Main.Patches.VOIP;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using HarmonyLib;
using System.Text.Json.Serialization;

namespace Fika.Core.Main.Utils;

public enum EClientType
{
    None = 0,
    Client = 1,
    Host = 2
}

public static class FikaBackendUtils
{
    /// <summary>
    /// 本地玩家的 PMC <see cref="EFT.Profile"/>
    /// </summary>
    public static Profile Profile
    {
        get
        {
            _profile ??= FikaGlobals.GetProfile(false);
            return _profile;
        }

        internal set
        {
            _profile = value;
        }
    }
    /// <summary>
    /// 本地玩家 PMC 的名称
    /// </summary>
    public static string PMCName => Profile.Nickname;
    /// <summary>
    /// 当前 raid 是否为一次 scav raid
    /// </summary>
    public static bool IsScav { get; internal set; }
    public static EClientType ClientType { get; internal set; } = EClientType.None;
    /// <summary>
    /// 本客户端是否为无头客户端
    /// </summary>
    /// <remarks>
    /// 无头客户端始终是 raid 主机（<see cref="IsServer"/> 始终为 <see langword="true"/>） <br/>
    /// 此检查主要用于阻止 GPU/动画逻辑等。
    /// </remarks>
    public static bool IsHeadless { get; set; }
    /// <summary>
    /// 本客户端是否为一名走进已经进行中的 raid 的玩家 scav。
    /// 这样的玩家以 scav 身份生成，且永远不会加入主机的队伍。
    /// </summary>
    public static bool IsScavLateJoin { get; internal set; }
    /// <summary>
    /// raid 主机是否为无头客户端
    /// </summary>
    public static bool IsHeadlessGame { get; set; }
    /// <summary>
    /// 本客户端是否请求了无头会话
    /// </summary>
    public static bool IsHeadlessRequester { get; set; }
    /// <summary>
    /// 当前 raid 是否为一次中转
    /// </summary>
    public static bool IsTransit { get; set; }
    /// <summary>
    /// 当前会话是否仅为观战
    /// </summary>
    public static bool IsSpectator { get; internal set; }
    /// <summary>
    /// 主机是否使用 NAT 打洞
    /// </summary>
    public static bool IsHostNatPunch { get; internal set; }
    public static IPEndPoint RemoteEndPoint { get; internal set; }
    public static ushort LocalPort { get; internal set; }
    public static string HostLocationId { get; internal set; }
    public static RaidSettings CachedRaidSettings { get; set; }
    public static BindableList<RaidPlayer> GroupPlayers { get; set; } = new();
    public static FikaCustomRaidSettings CustomRaidSettings { get; set; } = new();

    internal static bool RequestFikaWorld;

    internal static MatchMakerAcceptScreen MatchMakerAcceptScreenInstance { get; set; }
    internal static PlayersRaidReadyPanel PlayersRaidReadyPanel { get; set; }
    internal static MatchMakerGroupPreview MatchMakerGroupPreview { get; set; }

    private static Profile _profile;

    /// <summary>
    /// 当前加入 / raid 启动失败的原因；一切正常时为 <see langword="null"/>。
    /// raid 启动在创建游戏之前读取它，因此一次失败的准备（没有网络
    /// 管理器、没有主机端点、一个后端错误）会带原因中止，而不是继续
    /// 进入一个半初始化的会话。
    /// </summary>
    public static string JoinFailure { get; private set; }

    public static bool JoinFailed
    {
        get
        {
            return JoinFailure != null;
        }
    }

    /// <summary>记录当前尝试的第一次失败，并写入稳定日志。</summary>
    public static void MarkJoinFailed(string reason, Exception exception = null)
    {
        if (JoinFailure != null)
        {
            return;
        }

        JoinFailure = reason;
        
        if (exception != null)
        {
            
        }
    }

    /// <summary>清除失败状态，让下一次尝试从干净状态开始。</summary>
    public static void ClearJoinFailure()
    {
        if (JoinFailure == null)
        {
            return;
        }

        JoinFailure = null;
        
    }

    /// <summary>
    /// 在新一次尝试开始的那一刻，丢弃<em>更早</em>一次尝试记录的失败。
    ///
    /// CreateFikaGameCore 在创建任何东西之前读取 <see cref="JoinFailed"/>，并以
    /// "The raid could not be started: " + <see cref="JoinFailure"/> 中止 raid。MarkJoinFailed 保留一次
    /// 尝试的第一个原因，且只有 CleanUpVariables 会清除它，所以一次失败的加入会让之后每一次
    /// raid 启动都立即失败 —— 用的是上一次尝试的原因，而且是在一个什么都还没
    /// 出错的菜单上。*当前*尝试的失败仍照原样记录并展示：
    /// 这里只在新一次尝试开始时才被调用。
    /// </summary>
    public static void ClearLeftoverJoinFailure()
    {
        var previous = JoinFailure;
        if (previous == null)
        {
            return;
        }

        JoinFailure = null;
        FikaGlobals.LogInfo($"[Join] cleared the leftover join failure from a previous attempt: {previous}");
        
    }

    public static void CleanUpVariables()
    {
        ClearJoinFailure();

        if (!IsTransit)
        {
            IsSpectator = false;
            IsHeadlessRequester = false;
            IsHeadlessGame = false;
            // AP (analysis/nonfika-raid-inertness)：raid 种类是一个 raid 作用域的事实，过去会跨 raid
            // 存留，因此在一场 Fika raid 之后，下一场原生单人 raid 的每个 "ClientType == EClientType.None"
            // 门禁都回答「不，这是 Fika」—— 于是本移植作用到了那场 raid 上。
            // 2026-10-12 23:52 的崩溃正是在那种状态下发生的（raid 自己的行写着 side=single）。
            // 与其他 raid 作用域字段一起清除；中转会保留它，因为重载
            // 属于同一场 raid。
            ClientType = EClientType.None;
        }

        MatchMakerAcceptScreenInstance = null;
        PlayersRaidReadyPanel = null;
        MatchMakerGroupPreview = null;

        RequestFikaWorld = false;
        IsScavLateJoin = false;
        GroupPlayers?.Clear();
    }

    /// <summary>
    /// 本客户端是否在主持 raid
    /// </summary>
    /// <remarks>无头客户端始终是服务器</remarks>
    public static bool IsServer
    {
        get
        {
            return ClientType == EClientType.Host;
        }
    }
    /// <summary>
    /// 本客户端是否加入了一场 raid
    /// </summary>
    public static bool IsClient
    {
        get
        {
            return ClientType == EClientType.Client;
        }
    }
    /// <summary>
    /// 本客户端是否在主持 raid 且没有客户端连接
    /// </summary>
    public static bool IsSinglePlayer
    {
        get
        {
            return FikaSingleton<FikaServer>.Instantiated
                && FikaSingleton<FikaServer>.Instance.NetServer.ConnectedPeersCount == 0;
        }
    }
    public static string GroupId { get; internal set; }
    public static string RaidCode { get; internal set; }
    /// <summary>
    /// 当前 raid 会话的 <see cref="Guid"/>
    /// </summary>
    public static Guid ServerGuid { get; internal set; }
    public static TransitSettings TransitData
    {
        get
        {
            return _transitData ?? new()
            {
                transitionType = ELocationTransition.None,
                transitionCount = 0,
                transitionRaidId = FikaGlobals.DefaultTransitId,
                visitedLocations = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(0)
            };
        }
        set
        {
            _transitData = value;
        }
    }

    private static TransitSettings _transitData;

    public static void ResetTransitData()
    {
        TransitData = null;
    }

    public static bool JoinMatch(string profileId, string serverId, out CreateMatch result, out string errorMessage)
    {
        result = new CreateMatch();
        errorMessage = "No server matches the data provided or the server no longer exists";

        /*if (MatchMakerAcceptScreenInstance == null)
        {
            FikaGlobals.LogError("Could not find MatchMakerAcceptScreen");
            return false;
        }*/

        MatchJoinRequest body = new(serverId, profileId);
        
        result = FikaRequestHandler.RaidJoin(body);
        

        if (result.GameVersion != FikaPlugin.EFTVersionMajor)
        {
            errorMessage = string.Format(LocaleUtils.UI_ERROR_HOST_EFT_MISMATCH.Localized(), FikaPlugin.EFTVersionMajor, result.GameVersion);
            MarkJoinFailed(errorMessage);
            return false;
        }

        if (result.Crc32 != FikaPlugin.Crc32)
        {
            errorMessage = string.Format(LocaleUtils.UI_ERROR_HOST_FIKA_MISMATCH.Localized(), FikaPlugin.Crc32, result.Crc32);
            MarkJoinFailed(errorMessage);
            return false;
        }

        RaidCode = result.RaidCode;

        return true;
    }

    public static async Task CreateMatch(string profileId, string hostUsername, RaidSettings raidSettings,
        string reservationToken = null)
    {
        if (IsHeadless)
        {
            if (string.IsNullOrWhiteSpace(reservationToken))
                throw new InvalidOperationException("Headless raid creation has no requester reservation token.");
            FikaGlobals.LogInfo($"Creating headless room with reservation token={reservationToken[..Math.Min(6, reservationToken.Length)]}..., length={reservationToken.Length}");
        }
        NotificationManager.DisplayWarningNotification(LocaleUtils.STARTING_RAID.Localized());
        var timestamp = DateTimeOffset.Now.ToUnixTimeSeconds();
        var raidCode = GenerateRaidCode(6);
        var serverGuid = Guid.NewGuid();
        CreateMatch body = new(raidCode, profileId, serverGuid, hostUsername, IsSpectator, timestamp, raidSettings, FikaPlugin.Crc32,
            raidSettings.Side, raidSettings.SelectedDateTime, CustomRaidSettings, reservationToken);

        
        await FikaRequestHandler.RaidCreate(body);
        

        GroupId = profileId;
        ClientType = EClientType.Host;

        RaidCode = raidCode;
        ServerGuid = serverGuid;
    }

    internal static string GenerateRaidCode(int length)
    {
        System.Random random = new();
        var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray();
        var raidCode = "";
        for (var i = 0; i < length; i++)
        {
            var charIndex = random.Next(chars.Length);
            raidCode += chars[charIndex];
        }

        return raidCode;
    }

    internal static void AddPartyMembers(Dictionary<Profile, bool> profiles)
    {
        if (IsHeadless)
        {
            return;
        }

        if (Profile == null)
        {
            FikaGlobals.LogError("AddPartyMembers: Own profile was null!");
            return;
        }

        GroupPlayers.Clear();
        foreach ((var profile, var isLeader) in profiles)
        {
            var info = profile.Info;
            GroupPlayer infoSet = new()
            {
                AccountId = profile.AccountId,
                Id = profile.Id,
                IsLeader = isLeader,
                Info = new()
                {
                    Level = info.Level,
                    MemberCategory = info.MemberCategory,
                    SelectedMemberCategory = info.SelectedMemberCategory,
                    Nickname = profile.GetCorrectedNickname(),
                    Side = info.Side,
                    GameVersion = info.GameVersion,
                    HasCoopExtension = info.HasCoopExtension
                }
            };
            RaidPlayer visualProfile = new(infoSet)
            {
                PlayerVisualRepresentation = profile.GetVisualEquipmentState(false)
            };

            GroupPlayers.Add(visualProfile);
        }

        if (TarkovApplication.Exist(out var app))
        {
            var controller = app.Matchmaker;
            if (controller != null)
            {
                var menuUi = Singleton<MenuUI>.Instance;
                if (menuUi != null)
                {
                    var panel = menuUi.MatchmakerTimeHasCome._partyInfoPanel;
                    panel.Close();
                    panel.Show(GroupPlayers, Profile, false);
                    return;
                }
                FikaGlobals.LogWarning("AddPartyMembers: MenuUI was null!");
                return;
            }
            FikaGlobals.LogWarning("AddPartyMembers: MatchmakerPlayersController was null!");
        }
        FikaGlobals.LogWarning("AddPartyMembers: TarkovApplication was null!");
    }
}

public class FikaCustomRaidSettings
{
    [JsonPropertyName("useCustomWeather")]
    public bool UseCustomWeather { get; set; }

    [JsonPropertyName("disableOverload")]
    public bool DisableOverload { get; set; }

    [JsonPropertyName("disableLegStamina")]
    public bool DisableLegStamina { get; set; }

    [JsonPropertyName("disableArmStamina")]
    public bool DisableArmStamina { get; set; }

    public override string ToString()
    {
        return $"UseCustomWeather: {UseCustomWeather}, DisableOverload: {DisableOverload}, DisableLegStamina: {DisableLegStamina}, DisableArmStamina: {DisableArmStamina}";
    }

    public void Reset()
    {
        UseCustomWeather = default;
        DisableOverload = default;
        DisableLegStamina = default;
        DisableArmStamina = default;
    }
}
