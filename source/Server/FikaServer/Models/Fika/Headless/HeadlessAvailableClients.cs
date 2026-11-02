using SPTarkov.Server.Core.Models.Common;
using System.Text.Json.Serialization;

namespace FikaServer.Models.Fika.Headless;

public record HeadlessAvailableClients
{
    public HeadlessAvailableClients(MongoId sessionId, string alias)
    {
        HeadlessSessionID = sessionId;
        Alias = alias;
    }

    /// <summary>
    /// headless 客户端的 SessionID
    /// </summary>
    [JsonPropertyName("headlessSessionID")]
    public MongoId HeadlessSessionID { get; set; } = default;
    /// <summary>
    /// headless 客户端的别名（若已分配）。若未分配，则使用昵称 
    /// </summary>
    [JsonPropertyName("alias")]
    public string Alias { get; set; } = string.Empty;
}
