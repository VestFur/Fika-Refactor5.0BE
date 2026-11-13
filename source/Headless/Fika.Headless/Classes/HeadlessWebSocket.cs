using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using Fika.Core.Networking.Http;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Websocket;
using Fika.Core.Networking.Websocket.Headless;
using SPTushonka.Common.Http;

namespace Fika.Headless.Classes;

public sealed class HeadlessWebSocket
{
    private static readonly ManualLogSource _logger = Logger.CreateLogSource("Fika.HeadlessWebSocket");
    internal static readonly JsonSerializerOptions RequestOptions = new(FikaJson.Options)
    {
        PropertyNameCaseInsensitive = true
    };

    public string Host { get; }
    public string Url { get; }
    public string SessionId { get; }
    public bool Connected => _webSocket?.State == WebSocketState.Open;

    private readonly object _sync = new();
    private ClientWebSocket _webSocket;
    private CancellationTokenSource _stop;
    private Task _connectionTask;
    private int _attempts;
    private int _connectionGeneration;
    private bool _closing;

    public int ConnectionGeneration => Volatile.Read(ref _connectionGeneration);

    public HeadlessWebSocket()
    {
        Host = RequestHandler.Host.Replace("http", "ws");
        SessionId = RequestHandler.SessionId;
        Url = $"{Host}/fika/headless/client";
    }

    public void Connect()
    {
        lock (_sync)
        {
            if (_connectionTask is { IsCompleted: false }) return;
            _closing = false;
            _stop?.Dispose();
            _stop = new CancellationTokenSource();
            _connectionTask = ConnectAndReceiveAsync(_stop.Token);
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            if (_closing) return;
            _closing = true;
            _stop?.Cancel();
            try { _webSocket?.Abort(); } catch { }
        }
    }

    public void Reconnect()
    {
        // 被拒绝的预留可能没有 match 可结束。替换这个
        // socket，使服务器在把 headless 提供给其他请求者之前
        // 丢弃旧预留。
        lock (_sync)
        {
            if (!_closing)
            {
                try { _webSocket?.Abort(); } catch { }
            }
        }
    }

    public async Task SendReadyAsync(string reservationToken)
    {
        ClientWebSocket socket;
        lock (_sync) socket = _webSocket;
        if (socket?.State != WebSocketState.Open)
        {
            // 在这里静默意味着服务器永远不知道主机在一场战局之后
            // 又可以用了。始终说明为何未上报就绪状态。
            _logger.LogWarning($"[Headless] Server registration: readiness was NOT reported, websocket state is " +
                $"{(socket == null ? "null" : socket.State.ToString())}");
            return;
        }

        var json = JsonSerializer.Serialize(new
        {
            Type = "HeadlessReady",
            ReservationToken = reservationToken
        });
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text,
                true, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInfo($"[Headless] Server registration: reported readiness to the server (token={(string.IsNullOrEmpty(reservationToken) ? "none" : reservationToken[..Math.Min(6, reservationToken.Length)] + "...")})");
        }
        catch (Exception exception)
        {
            _logger.LogWarning($"[Headless] Server registration: failed to report readiness: {exception.GetBaseException().Message}");
            Reconnect();
        }
    }

    private async Task ConnectAndReceiveAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            ClientWebSocket socket = null;
            try
            {
                if (Volatile.Read(ref _attempts) >= 15)
                {
                    _logger.LogError("[Headless] Server registration: took more than 15 attempts to connect to the websocket, quitting...");
                    MainThreadDispatcher.Post(Application.Quit);
                    return;
                }

                var attempt = Interlocked.Increment(ref _attempts);
                _logger.LogInfo($"[Headless] Server registration: connecting to {Url} ({attempt}/15)...");
                socket = FikaBackendSocket.Create(SessionId);
                lock (_sync)
                {
                    if (_closing) return;
                    _webSocket = socket;
                }

                await socket.ConnectAsync(new Uri(Url), stop).ConfigureAwait(false);
                Interlocked.Exchange(ref _attempts, 0);
                Interlocked.Increment(ref _connectionGeneration);
                _logger.LogMessage($"[Headless] Server registration: websocket connected to {Url}");
                if (FikaHeadlessPlugin.Instance?.CanHost == true)
                    await SendReadyAsync(null).ConfigureAwait(false);
                await ReceiveAsync(socket, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                _logger.LogWarning($"[Headless] Server registration: websocket connection lost: {exception.GetBaseException().Message}");
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_webSocket, socket)) _webSocket = null;
                }
                socket?.Dispose();
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
        }
    }

    private static async Task ReceiveAsync(ClientWebSocket socket, CancellationToken stop)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
            message.SetLength(0);
            MainThreadDispatcher.Post(() => HandleMessage(json));
        }
    }

    private static void HandleMessage(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (!document.RootElement.TryGetProperty("Type", out var typeElement) &&
                !document.RootElement.TryGetProperty("type", out typeElement)) return;
            if (!Enum.TryParse<EFikaHeadlessWSMessageType>(typeElement.ToString(), true, out var type)) return;

            switch (type)
            {
                case EFikaHeadlessWSMessageType.HeadlessStartRaid:
                    var startRaid = JsonSerializer.Deserialize<StartRaid>(data, RequestOptions);
                    if (startRaid?.StartHeadlessRequest == null)
                    {
                        _logger.LogError("[Headless] Start request: the server sent a start message without a start request payload");
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(startRaid.StartHeadlessRequest.ReservationToken))
                    {
                        _logger.LogError("[Headless] Start request: the server sent no requester reservation token; refusing to create an unreserved room");
                        return;
                    }
                    var token = startRaid.StartHeadlessRequest.ReservationToken;
                    _logger.LogInfo($"[Headless] Start request: received from the server, token={token[..Math.Min(6, token.Length)]}..., length={token.Length}");
                    FikaHeadlessPlugin.Instance.OnFikaStartRaid(startRaid.StartHeadlessRequest);
                    break;
                case EFikaHeadlessWSMessageType.ShutdownClient:
                    _logger.LogInfo("[Headless] Shutdown: the server asked this host to shut down");
                    Application.Quit();
                    break;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError($"[Headless] Server registration: failed to process a headless message: {exception}");
        }
    }
}
