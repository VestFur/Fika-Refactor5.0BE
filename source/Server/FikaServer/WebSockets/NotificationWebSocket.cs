using FikaServer.Models.Fika.WebSocket;
using FikaServer.Services;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers.Ws;
using SPTarkov.Server.Core.Utils;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace FikaServer.WebSockets;

[Injectable(InjectionType.Singleton)]
public class NotificationWebSocket(JsonUtil jsonUtil, ISptLogger<NotificationWebSocket> logger, PresenceService fikaPresenceService) : IWebSocketConnectionHandler
{
    private ConcurrentDictionary<string, WebSocket> clientWebSockets = [];

    public string GetHookUrl()
    {
        return "/fika/notification/";
    }

    public string GetSocketId()
    {
        return "Fika Notification Manager";
    }

    public async Task OnConnectionAsync(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        if (!WebSocketAuthHelper.TryGetSessionId(context, out var userSessionID))
        {
            await ws.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid authorization", CancellationToken.None);
            return;
        }

        logger.Debug($"[{GetSocketId()}] User is {userSessionID}");

        WebSocket? previousSocket = null;
        if (!clientWebSockets.TryAdd(userSessionID, ws))
        {
            // 重连可能在上一个 socket 的关闭
            // 回调之前到达。只替换那一个旧连接；它最终
            // 的 OnCloseAsync 不得移除新 socket 或在线状态。
            if (clientWebSockets.TryGetValue(userSessionID, out previousSocket) &&
                !clientWebSockets.TryUpdate(userSessionID, ws, previousSocket))
            {
                clientWebSockets[userSessionID] = ws;
            }
        }

        // 重连之后不要留下被取代的连接继续存活。其
        // 关闭回调做了身份保护，因此无法移除
        // 新安装的 socket 或在线状态条目。
        if (previousSocket != null && !ReferenceEquals(previousSocket, ws))
        {
            try { previousSocket.Abort(); }
            catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException) { }
        }

        fikaPresenceService.AddPlayerPresence(userSessionID);
    }

    public Task OnMessageAsync(byte[] rawData, WebSocketMessageType messageType, WebSocket ws, HttpContext context)
    {
        // 不做任何事
        return Task.CompletedTask;
    }

    public Task OnCloseAsync(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        var client = clientWebSockets.Where(x => x.Value == ws).FirstOrDefault();

        if (client.Key != null)
        {
            logger.Debug($"[{GetSocketId()}] Deleting client {client.Key}");

            if (clientWebSockets.TryRemove(new KeyValuePair<string, WebSocket>(client.Key, ws)))
            {
                fikaPresenceService.RemovePlayerPresence(client.Key);
            }
        }

        return Task.CompletedTask;
    }

    public async Task SendAsync<T>(MongoId sessionID, T message) where T : IFikaNotification
    {
        // 客户端不在线，或当前未连接到 websocket。
        if (!clientWebSockets.TryGetValue(sessionID, out var ws))
        {
            return;
        }

        // 客户端此前连接过 websocket，但可能存在
        // 连接问题，因为它没有执行 OnCloseAsync。SendAsync 对
        // CloseReceived/CloseSent/Aborted 也会抛异常，因此只有打开的 socket
        // 才有资格接收通知。
        if (ws.State != WebSocketState.Open)
        {
            if (clientWebSockets.TryRemove(new KeyValuePair<string, WebSocket>(sessionID, ws)))
            {
                fikaPresenceService.RemovePlayerPresence(sessionID);
            }
            return;
        }

        var serialized = jsonUtil.Serialize(message);
        if (string.IsNullOrEmpty(serialized))
        {
            logger.Warning($"[{GetSocketId()}] Could not serialize notification for {sessionID}");
            return;
        }

        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(serialized), WebSocketMessageType.Text, true,
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or
            InvalidOperationException or OperationCanceledException)
        {
            // 过期的 socket 不得中断向其他所有
            // 玩家的广播。只移除这一个确切的连接；可能已有重连
            // 为同一会话安装了更新的 socket。
            if (clientWebSockets.TryRemove(new KeyValuePair<string, WebSocket>(sessionID, ws)))
            {
                fikaPresenceService.RemovePlayerPresence(sessionID);
            }

            logger.Debug($"[{GetSocketId()}] Notification socket for {sessionID} was unavailable: {exception.Message}");
        }
    }

    public async Task BroadcastAsync<T>(T message) where T : IFikaNotification
    {
        foreach (var websocket in clientWebSockets)
        {
            await SendAsync(websocket.Key, message);
        }
    }
}
