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

    /// <summary>
    /// <see cref="Connected"/> 供那些无需关心管理器是否已存在的调用方使用。
    /// 在这个 socket 起来之前，在线名单是空的（服务器会在该 socket 的连接处理中
    /// 注册玩家），因此面板会先询问这里，之后才判定 "nobody is online"
    /// 是否为真相。
    /// </summary>
    public static bool IsConnected => Instance is { } instance && instance.Connected;

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

    /// <summary>
    /// 创建唯一的通知客户端，并记录是哪个调用点请求了它。
    ///
    /// 这个 socket 就是客户端的在线注册：服务器会把玩家加入它的在线列表，
    /// 位置在该 socket 的连接处理中（<c>/fika/notification/</c>
    /// 对应的 <c>FikaServer.WebSockets.NotificationWebSocket.OnConnectionAsync</c> ->
    /// <c>PresenceService.AddPlayerPresence</c>）。如果它从未被打开，<c>/fika/presence/get</c>
    /// 会返回一个空数组，主菜单名单一行都不显示 —— 连本地玩家也没有，
    /// 这正是 47888 那次运行在应用层补丁因游戏方法改名而失效之后
    /// 所呈现的情况。
    ///
    /// 创建请求来自两处钩子（应用通知初始化器，以及作为兜底的
    /// <c>MenuScreen.Awake</c>），因此它必须保持幂等：第二个管理器会
    /// 打开第二个 socket，而服务器会把同一会话的第二次连接当作
    /// 重连处理，并中止它已经持有的那个 socket。
    /// </summary>
    /// <param name="source">请求该管理器的调用点。</param>
    /// <returns>当前已存活的管理器。</returns>
    internal static FikaNotificationManager EnsureCreated(string source)
    {
        var existing = Instance;
        if (existing != null)
        {
            return existing;
        }

        // 从补丁 postfix 调用，因此这里的失败绝不能传播进被补丁的游戏方法：
        // 改为上报它，并让兜底钩子再获得一次机会。
        try
        {
            var created = new FikaNotificationManager();
            if (FikaPlugin.Instance != null)
            {
                FikaPlugin.Instance.NotificationManager = created;
            }

            
            _logger.LogInfo($"Notification websocket client created by {source} ({created.Url})");
            return created;
        }
        catch (Exception exception)
        {
            
            _logger.LogError($"Notification websocket client could not be created by {source}: {exception}");
            return null;
        }
    }

    public void Connect()
    {
        // 不要把已故障的即发即忘任务泄漏进 Unity 的同步上下文。
        // 重连循环负责重试；这次调用只是推动它一下。
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
                    // 失败的尝试不得污染下一次调用 EnsureConnectedAsync 时
                    // 所使用的完成源。
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
            catch (Exception ex) when (_stop.IsCancellationRequested && WebSocketShutdown.IsTeardown(ex))
            {
                // Close() 会中止 socket，其上所有在途操作都会以这些结果之一返回。
                // 这是本循环的正常结束，而不是一个发现：不记录任何日志，
                // 且连接等待者不会被置为故障 —— 已经没人在等它了。
                break;
            }
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
            if (!await WebSocketShutdown.WaitBeforeReconnectAsync(TimeSpan.FromSeconds(10), _stop.Token).ConfigureAwait(false))
            {
                break;
            }
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
                // 管理器正在关闭：挂起的读取被取消，这正是本循环应有的结束方式。
                // 这不是警告，绝不能按警告记录。
                return;
            }
            catch (Exception exception) when (token.IsCancellationRequested && WebSocketShutdown.IsTeardown(exception))
            {
                // Close() 会中止 socket；在途的读取会以已中止、已释放或
                // 已失败的流返回，而不是一个干净的关闭帧。
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
            message.SetLength(0);
            // 连通知 Description 的 getter 也会访问原生本地化与
            // 会话时间。要把整个派发过程排队，而不只是最后的 UI 调用。
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












