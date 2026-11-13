using FikaServer.Helpers;
using FikaServer.Models.Enums;
using FikaServer.Models.Fika.Headless;
using FikaServer.Models.Fika.WebSocket.Notifications;
using FikaServer.Services;
using FikaServer.Services.Headless;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Servers.Ws;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FikaServer.WebSockets;

[Injectable(InjectionType.Singleton)]
public class HeadlessClientWebSocket(HeadlessHelper headlessHelper, HeadlessService headlessService,
    MatchService matchService, ISptLogger<HeadlessClientWebSocket> logger, WebhookService webhookService,
    NotificationWebSocket notificationWebSocket) : IWebSocketConnectionHandler
{
    private readonly ConcurrentDictionary<string, WebSocket> _headlessWebSockets = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionGates = [];

    public string GetHookUrl()
    {
        return "/fika/headless/client";
    }

    public string GetSocketId()
    {
        return "Fika Headless Client";
    }

    public async Task OnConnectionAsync(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        if (!WebSocketAuthHelper.TryGetSessionId(context, out var userSessionID))
        {
            await ws.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid authorization", CancellationToken.None);
            return;
        }

        logger.Debug($"[{GetSocketId()}] User is {userSessionID}");

        if (!headlessHelper.IsHeadlessClient(userSessionID))
        {
            logger.Error($"[{GetSocketId()}] Invalid headless client {userSessionID} tried to authenticate!");
            return;
        }

        WebSocket? oldSocket = null;
        var isNewClient = false;
        var connectionGate = _connectionGates.GetOrAdd(userSessionID, _ => new SemaphoreSlim(1, 1));
        await connectionGate.WaitAsync();
        HeadlessClientInfo? currentClient = null;
        try
        {
            if (headlessService.HeadlessClients.TryGetValue(userSessionID, out currentClient))
            {
                await currentClient.LifecycleGate.WaitAsync();
                try
                {
                    lock (currentClient) currentClient.IsReconnecting = true;
                }
                finally
                {
                    currentClient.LifecycleGate.Release();
                }
            }

            // 房间可能正在结束，也可能尚未注册它的主机。
            // Profile/player 查找会漏掉这两种情况，而 headless 房间
            // 总是直接用这个 profile ID 作为键。
            if (matchService.GetMatch(userSessionID) != null)
            {
                // 重启后的 headless 进程无法继续之前的战局。
                // 通过正常路径结束它，以便在该 socket 再次可用之前
                // 清空预约和保险状态。
                await matchService.EndMatch(userSessionID, EFikaMatchEndSessionMessage.HostShutdown);
            }

            // 清理上一场战局期间，对端可能断开连接。
            if (ws.State != WebSocketState.Open)
            {
                return;
            }

            _headlessWebSockets[userSessionID] = ws;
            headlessService.HeadlessClients.AddOrUpdate(userSessionID,
            _ =>
            {
                isNewClient = true;
                return new HeadlessClientInfo(ws, EHeadlessStatus.READY);
            },
            (_, existing) =>
            {
                oldSocket = existing.WebSocket;
                return new HeadlessClientInfo(ws, EHeadlessStatus.READY);
            });
        }
        finally
        {
            connectionGate.Release();
        }

        if (oldSocket != null)
        {
            try
            {
                await oldSocket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation,
                    "Replaced by new connection", CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.Error($"{ex.Message}", ex);
            }
        }

        var name = headlessHelper.GetHeadlessNickname(userSessionID);

        await webhookService.SendWebhookMessage($"Headless client {name} has connected");
        await notificationWebSocket.BroadcastAsync(new HeadlessConnectedNotification
        {
            Name = name
        });
        if (!isNewClient)
        {
            logger.Info($"Headless client {name} reconnected and replaced existing session");
        }
    }

    public async Task OnMessageAsync(byte[] rawData, WebSocketMessageType messageType, WebSocket ws, HttpContext context)
    {
        if (messageType != WebSocketMessageType.Text || rawData.Length > 1024) return;

        try
        {
            using var document = JsonDocument.Parse(rawData);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                type.GetString() != "HeadlessReady") return;

            string? reservationToken = null;
            if (root.TryGetProperty("ReservationToken", out var token))
            {
                if (token.ValueKind == JsonValueKind.String)
                    reservationToken = token.GetString();
                else if (token.ValueKind != JsonValueKind.Null)
                    return;
            }

            // 从权威的 socket 索引解析 profile。仅凭
            // 认证上下文无法证明在替换连接之后旧 socket
            // 仍然拥有这个 headless。
            var sessionId = _headlessWebSockets.FirstOrDefault(
                entry => ReferenceEquals(entry.Value, ws)).Key;
            if (string.IsNullOrEmpty(sessionId)) return;

            await headlessService.MarkClientReadyAsync(sessionId, ws, reservationToken);
        }
        catch (JsonException exception)
        {
            logger.Warning($"Ignoring malformed Headless readiness message: {exception.Message}");
        }
    }

    public async Task OnCloseAsync(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        var userSessionID = _headlessWebSockets.FirstOrDefault(x => ReferenceEquals(x.Value, ws)).Key;

        if (!string.IsNullOrEmpty(userSessionID))
        {
            var connectionGate = _connectionGates.GetOrAdd(userSessionID, _ => new SemaphoreSlim(1, 1));
            await connectionGate.WaitAsync();
            try
            {
                logger.Debug($"[{GetSocketId()}] Deleting client {userSessionID}");

                // 被替换 socket 的关闭回调可能在新 socket 已经启动
                // 另一场战局之后才到达。在结束任何东西之前，须在
                // 持有 per-profile 门的情况下重新确认权威性；
                // 旧回调绝不能停止新的对局。
                if (!_headlessWebSockets.TryGetValue(userSessionID, out var currentSocket) ||
                    !ReferenceEquals(currentSocket, ws) ||
                    (headlessService.HeadlessClients.TryGetValue(userSessionID, out var currentInfo) &&
                     !ReferenceEquals(currentInfo.WebSocket, ws)))
                {
                    return;
                }

                if (headlessService.HeadlessClients.TryGetValue(userSessionID, out var authoritativeClient))
                {
                    await authoritativeClient.LifecycleGate.WaitAsync();
                    try
                    {
                        if (!ReferenceEquals(authoritativeClient.WebSocket, ws)) return;
                        lock (authoritativeClient) authoritativeClient.IsReconnecting = true;
                    }
                    finally
                    {
                        authoritativeClient.LifecycleGate.Release();
                    }

                    await EndAuthoritativeMatchAsync(userSessionID);

                    if (headlessService.HeadlessClients.TryGetValue(userSessionID, out var client) &&
                        ReferenceEquals(client, authoritativeClient) &&
                        ReferenceEquals(client.WebSocket, ws))
                        headlessService.HeadlessClients.TryRemove(new(userSessionID, client));
                    _headlessWebSockets.TryRemove(new KeyValuePair<string, WebSocket>(userSessionID, ws));
                }
                else
                {
                    // 失败的替换可能在 profile 条目被移除之后
                    // 让 socket 索引仍然存活。在丢弃最后一个连接索引
                    // 之前，仍要终止以该 headless 会话为键的
                    // 对局。
                    await EndAuthoritativeMatchAsync(userSessionID);
                }

                if (!headlessService.HeadlessClients.ContainsKey(userSessionID))
                    _headlessWebSockets.TryRemove(new KeyValuePair<string, WebSocket>(userSessionID, ws));
            }
            finally
            {
                connectionGate.Release();
            }
        }
    }

    private async Task EndAuthoritativeMatchAsync(string userSessionID)
    {
        if (matchService.GetMatch(userSessionID) == null) return;
        try
        {
            await matchService.EndMatch(userSessionID, EFikaMatchEndSessionMessage.HostShutdown);
        }
        catch (Exception exception)
        {
            // MatchService 用 finally 块做保险/对局清理；
            // 即使持久化报告出错，也要移除已死的 socket，
            // 以免它被宣告为可用。
            logger.Error($"Failed to end headless match {userSessionID} during websocket close", exception);
        }
    }
}
