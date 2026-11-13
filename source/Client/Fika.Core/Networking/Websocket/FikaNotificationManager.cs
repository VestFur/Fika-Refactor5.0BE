using BepInEx.Logging;
using Comfort.Common;
using EFT.Communications;
using EFT.UI;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Serialization;
using Fika.Core.Networking.Websocket.Notifications;
using SPTushonka.Common.Http;
using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fika.Core.Networking.Websocket;

internal sealed class FikaNotificationManager : IDisposable
{
    private static readonly ManualLogSource _logger = Logger.CreateLogSource("FikaNotificationManager");
    public static FikaNotificationManager Instance;
    public static bool Exists => Instance != null;
    public string Host { get; }
    public string Url { get; }
    public string SessionId { get; }
    public bool Connected => _webSocket?.State == WebSocketState.Open;

    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private ClientWebSocket _webSocket;
    private TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public FikaNotificationManager()
    {
        Instance = this;
        Host = RequestHandler.Host.Replace("http", "ws");
        SessionId = RequestHandler.SessionId;
        Url = $"{Host}/fika/notification/";
        _ = ConnectLoopAsync();
    }

    public void Connect()
    {
        // Do not leak a faulted fire-and-forget task into Unity's synchronization
        // context.  The reconnect loop owns retries; this call only nudges it.
        _ = EnsureConnectedAsync(TimeSpan.Zero).ContinueWith(
            task =>
            {
                if (task.IsFaulted && task.Exception != null)
                {
                    _logger.LogDebug($"Notification websocket is not connected yet: {task.Exception.GetBaseException().Message}");
                }
            }, TaskScheduler.Default);
    }

    public async Task EnsureConnectedAsync(TimeSpan? timeout = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FikaNotificationManager));
        if (Connected) return;
        lock (_sync)
        {
            if (_connected.Task.IsCompleted) _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (timeout is null) await _connected.Task.ConfigureAwait(false);
        else await _connected.Task.WaitAsync(timeout.Value).ConfigureAwait(false);
    }

    private async Task ConnectLoopAsync()
    {
        while (!_stop.IsCancellationRequested && !_disposed)
        {
            ClientWebSocket socket = null;
            try
            {
                lock (_sync)
                {
                    // A failed attempt must not poison the completion source used
                    // by the next caller of EnsureConnectedAsync.
                    if (_connected.Task.IsCompleted)
                    {
                        _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }
                socket = FikaBackendSocket.Create(SessionId);
                lock (_sync) _webSocket = socket;
                await socket.ConnectAsync(new Uri(Url), _stop.Token).ConfigureAwait(false);
                _connected.TrySetResult(true);
                _logger.LogInfo("Connected to Fika notification websocket");
                await ReceiveLoopAsync(socket, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _connected.TrySetException(ex);
                _logger.LogWarning($"Notification websocket: {ex.GetBaseException().Message}");
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_webSocket, socket)) _webSocket = null;
                }
                socket?.Dispose();
            }
            if (!_stop.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
            message.SetLength(0);
            // Even notification Description getters access native localization and
            // session time. Queue the whole dispatch, not just the final UI call.
            MainThreadDispatcher.Post(() => Dispatch(json), token);
        }
    }

    private void Dispatch(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("type", out var typeElement)) return;
            if (!Enum.TryParse<EFikaNotification>(typeElement.ToString(), true, out var type))
                if (!typeElement.TryGetInt32(out var numeric) || !Enum.IsDefined(typeof(EFikaNotification), numeric)) return;
                else type = (EFikaNotification)numeric;
#if DEBUG
            _logger.LogDebug($"Received type: {type}");
#endif
            switch (type)
            {
                case EFikaNotification.StartedRaid:
                    var started = FikaJson.Deserialize<StartRaidNotification>(json);
                    if (!FikaGlobals.IsInRaid) Show(started.Description, started.Icon);
                    break;
                case EFikaNotification.SentItem:
                    var sent = FikaJson.Deserialize<ReceivedSentItemNotification>(json);
                    Show(sent.Description, sent.Icon);
                    break;
                case EFikaNotification.PushNotification:
                    var push = FikaJson.Deserialize<PushNotification>(json);
                    Show(push.Description, push.Icon);
                    break;
                case EFikaNotification.OpenAdminSettings:
                    var admin = FikaJson.Deserialize<OpenAdminMenuNotification>(json);
                    if (admin.Success) AdminSettingsUIScript.Create();
                    break;
                case EFikaNotification.ShutdownClient:
                    if (FikaBackendUtils.IsHeadless) Application.Quit();
                    break;
                case EFikaNotification.HeadlessConnected:
                    var connected = FikaJson.Deserialize<HeadlessConnectedNotification>(json);
                    Show(connected.Description, connected.Icon);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to process Fika notification: {ex}");
        }
    }

    private static void Show(string description, ENotificationIconType icon)
    {
        var notifier = Singleton<PreloaderUI>.Instance?.NotifierView;
        notifier?.NotificationReceivedHandler(new CustomNotification(description,
            ENotificationDurationType.Default, icon, new Il2CppSystem.Nullable<Color>(), true));
    }

    public void Close()
    {
        _disposed = true;
        _stop.Cancel();
        lock (_sync)
        {
            try { _webSocket?.Abort(); } catch { }
            _webSocket = null;
        }
    }

    public void Dispose() => Close();
}












