using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.UI;
using Fika.Core.Main.Utils;
using Fika.Core.UI.Custom;
using Fika.Core.Networking.Serialization;
using Fika.Core.Networking.Websocket.Headless;
using SPTushonka.Common.Http;

namespace Fika.Core.Networking.Websocket;

public sealed class HeadlessRequesterWebSocket : IDisposable
{
    private static readonly ManualLogSource _logger = Logger.CreateLogSource("Fika.HeadlessWebSocket");
    public string Host { get; }
    public string Url { get; }
    public string SessionId { get; }
    public bool Connected => _webSocket?.State == WebSocketState.Open;

    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ClientWebSocket _webSocket;
    private Task _connectionTask;
    private bool _disposed;
    private bool _joining;

    public HeadlessRequesterWebSocket()
    {
        Host = RequestHandler.Host.Replace("http", "ws");
        SessionId = RequestHandler.SessionId;
        Url = $"{Host}/fika/headless/requester";
    }

    public void Connect()
    {
        _ = EnsureConnectedAsync(TimeSpan.Zero).ContinueWith(
            task =>
            {
                if (task.IsFaulted && task.Exception != null)
                {
                    _logger.LogDebug($"Headless requester websocket is not connected yet: {task.Exception.GetBaseException().Message}");
                }
            }, TaskScheduler.Default);
    }

    public async Task EnsureConnectedAsync(TimeSpan? timeout = null)
    {
        Task connected;
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HeadlessRequesterWebSocket));
            if (Connected) return;
            if (_connectionTask == null || _connectionTask.IsCompleted)
            {
                if (_connected.Task.IsCompleted) _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _connectionTask = ConnectAndReceiveAsync();
            }
            connected = _connected.Task;
        }
        if (timeout is null) await connected.ConfigureAwait(false);
        else await connected.WaitAsync(timeout.Value).ConfigureAwait(false);
    }

    private async Task ConnectAndReceiveAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            ClientWebSocket socket = null;
            TaskCompletionSource<bool> connected;
            lock (_sync)
            {
                if (_disposed) return;
                if (_connected.Task.IsCompleted) _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                connected = _connected;
            }
            try
            {
                socket = FikaBackendSocket.Create(SessionId);
                lock (_sync)
                {
                    if (_disposed) return;
                    _webSocket = socket;
                }
                await socket.ConnectAsync(new Uri(Url), _stop.Token).ConfigureAwait(false);
                connected.TrySetResult(true);
                _logger.LogInfo("Connected to HeadlessRequesterWebSocket");
                await ReceiveLoopAsync(socket, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception ex) when (_stop.IsCancellationRequested && WebSocketShutdown.IsTeardown(ex))
            {
                // socket 在应用退出时被关闭：这些被释放的操作是
                // 本循环的正常结束（见 WebSocketShutdown），而不是连接问题。
                return;
            }
            catch (Exception ex)
            {
                // 在短暂失败期间保持连接等待者处于挂起状态；
                // 请求超时与取消由其调用方负责。
                _logger.LogWarning($"Headless requester websocket: {ex.GetBaseException().Message}");
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_webSocket, socket)) _webSocket = null;
                    if (!_disposed && _connected.Task.IsCompleted)
                        _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                socket?.Dispose();
            }
            if (!await WebSocketShutdown.WaitBeforeReconnectAsync(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false)) return;
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (token.IsCancellationRequested && WebSocketShutdown.IsTeardown(exception))
            {
                // 在此读取仍挂起时 socket 被关闭：这是循环的正常结束。
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
            message.SetLength(0);
            MainThreadDispatcher.Post(() => Dispatch(json), token);
        }
    }

    private void Dispatch(string json)
    {
        if (_disposed || _joining || !FikaBackendUtils.IsHeadlessRequester) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Type", out var typeElement) &&
                !document.RootElement.TryGetProperty("type", out typeElement)) return;
            if (!Enum.TryParse<EFikaHeadlessWSMessageType>(typeElement.ToString(), true, out var type))
                if (!typeElement.TryGetInt32(out var numeric) || !Enum.IsDefined(typeof(EFikaHeadlessWSMessageType), numeric)) return;
                else type = (EFikaHeadlessWSMessageType)numeric;
            if (type != EFikaHeadlessWSMessageType.RequesterJoinRaid) return;
            var data = FikaJson.Deserialize<RequesterJoinRaid>(json);
            MainThreadDispatcher.RunAsync(() => HandleRequesterJoinRaidAsync(data)).Forget();
        }
        catch (Exception ex) { _logger.LogError($"Failed to process headless message: {ex}"); }
    }

    private async Task HandleRequesterJoinRaidAsync(RequesterJoinRaid data)
    {
        if (_disposed || _joining) return;
        _joining = true;
        try
        {
            if (string.IsNullOrEmpty(data.MatchId))
                throw new InvalidDataException("Received RequesterJoinRaid without a matchId");
            if (!TarkovApplication.Exist(out var tarkovApplication) || tarkovApplication.Session?.Profile == null)
            {
                throw new InvalidOperationException("TarkovApplication was not available for the headless join");
            }

            var success = await MatchMakerUIScript.JoinMatch(tarkovApplication.Session.Profile.Id, data.MatchId, null, _stop.Token);
            if (!_disposed && success)
                FikaBackendUtils.MatchMakerAcceptScreenInstance.GameStart().HandleExceptions();
            else if (!success) FikaBackendUtils.IsHeadlessRequester = false;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            FikaBackendUtils.IsHeadlessRequester = false;
            _logger.LogError($"Cannot join headless raid: {exception}");
            if (!_disposed) PreloaderUI.Instance.ShowErrorScreen("Fika Headless Error", exception.Message);
        }
        finally
        {
            Close();
            if (ReferenceEquals(FikaPlugin.HeadlessRequesterWebSocket, this))
                FikaPlugin.HeadlessRequesterWebSocket = null;
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            _connected.TrySetCanceled(_stop.Token);
            try { _webSocket?.Abort(); } catch { }
            _webSocket = null;
        }
    }

    public void Dispose() => Close();
}

