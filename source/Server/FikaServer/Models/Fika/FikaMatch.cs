using FikaServer.Models.Enums;
using FikaServer.Models.Fika.Routes.Client;
using FikaServer.Models.Fika.Headless;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using System.Text.Json.Serialization;

namespace FikaServer.Models.Fika;

public record FikaMatch
{
    [JsonPropertyName("ips")]
    public string[] Ips { get; set; } = [];

    [JsonPropertyName("serverGuid")]
    public Guid ServerGuid { get; set; }

    [JsonPropertyName("port")]
    public ushort Port { get; set; }

    [JsonPropertyName("hostUsername")]
    public string HostUsername { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("crc32")]
    public uint CRC32 { get; set; }

    [JsonPropertyName("gameVersion")]
    public string GameVersion { get; set; } = string.Empty;

    [JsonPropertyName("raidConfig")]
    public required GetRaidConfigurationRequestData RaidConfig { get; set; }

    [JsonPropertyName("locationData")]
    public required LocationBase LocationData { get; set; }

    [JsonPropertyName("status")]
    public EFikaMatchStatus Status { get; set; }

    [JsonPropertyName("timeout")]
    public int Timeout { get; set; }

    [JsonPropertyName("players")]
    public Dictionary<MongoId, FikaPlayer> Players { get; set; } = [];

    [JsonPropertyName("side")]
    public EFikaSide Side { get; set; }

    [JsonPropertyName("time")]
    public EFikaTime Time { get; set; }

    [JsonPropertyName("raidCode")]
    public string RaidCode { get; set; } = string.Empty;

    [JsonPropertyName("natPunch")]
    public bool NatPunch { get; set; }

    [JsonPropertyName("useFikaNatPunchServer")]
    public bool UseFikaNatPunchServer { get; set; }

    [JsonPropertyName("isHeadless")]
    public bool IsHeadless { get; set; }

    [JsonPropertyName("raids")]
    public int Raids { get; set; }

    [JsonPropertyName("customRaidSettings")]
    public FikaCustomRaidSettings? CustomRaidSettings { get; set; }

    [JsonIgnore]
    public bool IsEnding { get; set; }

    // 保留创建该 match 的预留。重连会替换
    // HeadlessClientInfo，而旧的 match 可能仍在结算保险。
    [JsonIgnore]
    public HeadlessClientInfo? HeadlessReservationOwner { get; set; }

    [JsonIgnore]
    public long? HeadlessReservationVersion { get; set; }

    /// <summary>
    /// 针对在 IsEnding 之前放行的 StartLocalRaid 请求的逐 match 屏障。
    /// 它不会进入 match JSON，并且一直存活到该 match 对象
    /// 被完全销毁。
    /// </summary>
    [JsonIgnore]
    public FikaServer.Services.LocalRaidStartBarrier LocalRaidStarts { get; } = new();
}
