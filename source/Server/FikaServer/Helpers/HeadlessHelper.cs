using FikaServer.Models.Enums;
using FikaServer.Models.Fika.Config;
using FikaServer.Models.Fika.Headless;
using FikaServer.Services.Headless;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;
using System.Collections.Concurrent;

namespace FikaServer.Helpers;

[Injectable]
public class HeadlessHelper(FikaServerConfig fikaServerConfig, SaveServer saveServer, HeadlessService headlessService,
    HeadlessProfileService headlessProfileService, ISptLogger<HeadlessHelper> logger)
{
    /// <summary>
    /// 获取所有当前已登录的 headless
    /// </summary>
    /// <returns>一个 <see cref="ConcurrentDictionary{TKey, TValue}"/>，键为 sessionID，值为 IHeadlessClientInfo 对象</returns>
    public ConcurrentDictionary<MongoId, HeadlessClientInfo> HeadlessClients
    {
        get
        {
            return headlessService.HeadlessClients;
        }
    }

    /// <summary>
    /// 用于检查某个 SessionID 是否为 headless 客户端
    /// </summary>
    /// <param name="sessionId">要检查的 sessionID</param>
    /// <returns>传入的 sessionID 是 headless 时返回 true，否则返回 false。</returns>
    public bool IsHeadlessClient(MongoId sessionId)
    {
        return headlessProfileService.HeadlessProfiles
            .Any(x => x.ProfileInfo?.ProfileId == sessionId);
    }

    /// <summary>
    /// 用于检查给定的 headless 客户端是否可用
    /// </summary>
    /// <param name="headlessSessionID"></param>
    /// <returns>可用时返回 true，不可用时返回 false。</returns>
    public bool IsHeadlessClientAvailable(MongoId headlessSessionID)
    {
        if (headlessService.HeadlessClients.TryGetValue(headlessSessionID, out var headlessClientInfo))
        {
            lock (headlessClientInfo)
            {
                return headlessClientInfo.State is EHeadlessStatus.READY &&
                    headlessClientInfo.ClientReady && !headlessClientInfo.IsReconnecting &&
                    headlessClientInfo.RequesterSessionID == null &&
                    headlessClientInfo.WebSocket.State == System.Net.WebSockets.WebSocketState.Open;
            }
        }

        return false;
    }

    /// <summary>
    /// 获取请求某个 headless 客户端的用户的用户名（若有）。
    /// </summary>
    /// <param name="headlessSessionID"></param>
    /// <returns>若该 headless 已被某个用户请求则返回其昵称，否则返回 null。</returns>
    public string? GetRequesterUsername(MongoId headlessSessionID)
    {
        if (headlessService.HeadlessClients.TryGetValue(headlessSessionID, out var headlessClientInfo))
        {
            string? requesterSessionId;
            lock (headlessClientInfo)
                requesterSessionId = headlessClientInfo.RequesterSessionID;

            if (string.IsNullOrEmpty(requesterSessionId))
            {
                return null;
            }

            var nickname = saveServer.GetProfile(requesterSessionId)?.CharacterData?.PmcData?.Info?.Nickname;
            if (string.IsNullOrEmpty(nickname))
            {
                return null;
            }

            return nickname;
        }

        return null;
    }

    /// <summary>
    /// 获取 headless 客户端的别名（若已设定）或昵称
    /// </summary>
    /// <param name="headlessSessionID"></param>
    /// <returns>headless 客户端的别名或昵称。</returns>
    public string GetHeadlessNickname(MongoId headlessSessionID)
    {
        if (fikaServerConfig.Headless.Profiles.Aliases.TryGetValue(headlessSessionID, out var alias))
        {
            return alias;
        }

        var nickname = saveServer.GetProfile(headlessSessionID)?.CharacterData?.PmcData?.Info?.Nickname;
        if (string.IsNullOrEmpty(nickname))
        {
            return "ERROR";
        }

        return nickname;
    }

    /// <summary>
    /// 获取所有可用的 headless 客户端
    /// </summary>
    /// <returns>返回可用 headless 客户端的数组</returns>
    public HeadlessAvailableClients[] GetAvailableHeadlessClients()
    {
        List<string> availableClients = [];
        foreach (var (sessionId, headlessClient) in HeadlessClients)
        {
            lock (headlessClient)
            {
                if (headlessClient.State == EHeadlessStatus.READY &&
                    headlessClient.ClientReady && !headlessClient.IsReconnecting &&
                    headlessClient.RequesterSessionID == null &&
                    headlessClient.WebSocket.State == System.Net.WebSockets.WebSocketState.Open)
                    availableClients.Add(sessionId);
            }
        }

        List<HeadlessAvailableClients> result = [];
        foreach (var sessionId in availableClients)
        {
            result.Add(new(sessionId, GetHeadlessNickname(sessionId)));
        }

        return [.. result];
    }
}
