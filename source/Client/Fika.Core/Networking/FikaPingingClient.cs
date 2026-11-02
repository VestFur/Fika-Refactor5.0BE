using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using Comfort.Common;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using SPTushonka.Common.Http;

namespace Fika.Core.Networking;

/// <summary>
/// 用于在初始化 <see cref="FikaClient"/> 与 <see cref="CoopGame"/> 之前验证能否建立连接的客户端
/// </summary>
public class FikaPingingClient : INetEventListener, INatPunchListener, IDisposable
{
    /// <summary>
    /// 网络客户端管理器实例。
    /// </summary>
    public NetManager NetClient;

    /// <summary>
    /// 表示是否已从服务器收到成功响应。
    /// </summary>
    public bool Received;

    /// <summary>
    /// 表示连接尝试是否已被服务器拒绝。
    /// </summary>
    public bool Rejected;

    /// <summary>
    /// 表示服务器当前是否进行中（忙碌）。
    /// </summary>
    public bool InProgress;

    private readonly ManualLogSource _logger;
    private List<IPEndPoint> _endPoints;
    private NetDataWriter _writer;
    private List<IPEndPoint> _candidates;
    private float _firstResponseTime;
    private bool _hasResponse;
    private bool _natIntroductionRequested;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;
    private string _serverId;
    private const float _responseWaitSeconds = 1f;

    /// <summary>单条日志行中保留的最长原始响应体。</summary>
    private const int MaxLoggedBodyChars = 512;

    /// <summary>
    /// <see cref="Init"/> 拒绝开始 ping 的原因；未失败时为 <see langword="null"/>。
    /// 加入界面会把它追加到自己的错误框里："主机从未公布地址" 与
    /// "地址存在但没有应答" 对玩家来说需要不同的话术。
    /// </summary>
    public string FailureReason { get; private set; }

    /// <summary>
    /// 当持有该对局的服务器给出的回答不是可用的主机记录时为 true，
    /// 也就是说缺失的地址在主机一侧。传输层/后端失败时为 false
    /// （没有回答或非 2xx 状态），此时有用的建议是检查我们自己的服务器连接。
    /// </summary>
    public bool HostAddressMissing { get; private set; }

    public FikaPingingClient()
    {
        _logger = Logger.CreateLogSource("Fika.PingingClient");
        _cts = new();
    }

    /// <summary>
    /// 初始化 ping 客户端并尝试解析服务器端点。
    /// </summary>
    /// <param name="serverId">要连接的服务器标识符。</param>
    /// <returns>初始化成功时为 true，否则为 false。</returns>
    public bool Init(string serverId)
    {
        NetClient = new(this)
        {
            UnconnectedMessagesEnabled = true,
            NatPunchEnabled = true,
            UseNativeSockets = NativeSocket.IsSupported
        };

        _endPoints = [];
        _candidates = [];
        _writer = new();

        _serverId = serverId;
        GetHostRequest body = new(serverId);
        GetHostResponse result;
        try
        {
            result = FikaRequestHandler.GetHost(body);
        }
        catch (Exception exception)
        {
            // 服务器有回答，但不是主机记录：未知的对局 id 会返回空
            // 响应体，损坏或不可达的后端则会从传输层抛出（其消息
            // 携带 HTTP 状态）。两者都会以原始回答上报，然后加入流程才停止。
            HostAddressMissing = FikaRequestHandler.LastGetHostResponseBody != null;
            FailureReason = $"The raid server did not return a host record for '{serverId}' "
                + $"(/fika/raid/gethost): {exception.GetBaseException().Message}";
            LogHostLookupFailure(serverId, "Host lookup failed");
            return false;
        }

        FikaBackendUtils.ServerGuid = result.ServerGuid;
        _logger.LogInfo(result.ToString());

        FikaBackendUtils.IsHostNatPunch = result.NatPunch;
        FikaBackendUtils.IsHeadlessGame = result.IsHeadless;

        if (result.Port == 0)
        {
            HostAddressMissing = true;
            FailureReason = $"The host has not published an address on that server (their side): "
                + $"'/fika/raid/gethost' returned port 0 for server '{serverId}'.";
            LogHostLookupFailure(serverId, "Host port was empty when pinging");
            return false;
        }

        // 服务器可能在公布有效局域网地址的同时，也公布一个不可用的广域网
        // 地址。要独立解析每一个，这样单个坏地址就不会
        // 阻止加入一个可达的主机。当公布的列表为空时，NAT 打洞也可能
        // 提供唯一的端点。
        foreach (var address in result.IPs ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(address)) continue;
            try
            {
                var candidate = NetworkUtils.ResolveRemoteAddress(address, result.Port);
                if (candidate.AddressFamily == AddressFamily.InterNetworkV6
                    && !FikaPlugin.Instance.Settings.UseIPv6.Value)
                {
                    _logger.LogInfo($"Skipping IPv6 candidate {address}: IPv6 is disabled in the network settings");
                    continue;
                }
                _endPoints.Add(candidate);
            }
            catch (Exception exception)
            {
                _logger.LogWarning($"Cannot resolve host address {address}: {exception.Message}");
            }
        }

        if (_endPoints.Count == 0 && !result.NatPunch)
        {
            HostAddressMissing = true;
            FailureReason = $"The host has not published an address on that server (their side): "
                + $"the advertised address(es) [{DescribeAddresses(result.IPs)}] could not be used (port {result.Port}).";
            LogHostLookupFailure(serverId, "Host did not provide a usable address for pinging");
            return false;
        }

        NetClient.Start();

        if (FikaBackendUtils.IsHostNatPunch)
        {
            NetClient.NatPunchModule.Init(this);

            var natPunchServerIP = FikaPlugin.Instance.NatPunchServerIP;
            var natPunchServerPort = FikaPlugin.Instance.NatPunchServerPort;

            if (result.UseFikaNatPunchServer)
            {
                natPunchServerIP = FikaPlugin.FikaNATPunchMasterServer;
                natPunchServerPort = FikaPlugin.FikaNATPunchMasterPort;
            }

            try
            {
                var resolved = NetUtils.ResolveAddress(natPunchServerIP, AddressFamily.InterNetwork); // no ipv6 for natpunch master
                var endPoint = new IPEndPoint(resolved, natPunchServerPort);
                var token = $"Client:{FikaBackendUtils.ServerGuid}";
                _natIntroductionRequested = true;
                _ = Task.Run(() => NatIntroduceTask(endPoint, token, _cts.Token));
                _logger.LogInfo($"Sent NAT Introduce Request to Nat Punch Server: {endPoint}");
            }
            catch (Exception exception)
            {
                _logger.LogWarning($"Cannot use NAT punching server {natPunchServerIP}:{natPunchServerPort}: {exception.Message}");
                if (_endPoints.Count == 0)
                {
                    // NAT 打洞是唯一的入口，而它的服务器不可用，因此没有地址
                    // 可以再试了。上报方式与其他 "没有地址" 的出口相同。
                    HostAddressMissing = true;
                    FailureReason = $"The host has not published an address on that server (their side): "
                        + "no usable address was advertised and the NAT punch server could not be used.";
                    LogHostLookupFailure(serverId,
                        "Host did not provide a usable address for pinging (its NAT punch server is unusable too)");
                    return false;
                }
            }
        }

        return true;
    }

    private async Task NatIntroduceTask(IPEndPoint endPoint, string token, CancellationToken ct = default)
    {
        _logger.LogInfo("Send NAT Introduce Request task started.");

        while (!ct.IsCancellationRequested)
        {
            NetClient?.NatPunchModule?.SendNatIntroduceRequest(endPoint, token);

            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }

        _logger.LogInfo("Send NAT Introduce Request task stopped.");
    }

    /// <summary>
    /// 一行日志，说完区分 "主机从未公布地址" 与
    /// "地址存在但无人应答" 所需的全部信息：问了谁、在哪台服务器上问、HTTP
    /// 状态是什么、原始回答是什么，外加一句大白话的结论。
    /// </summary>
    private void LogHostLookupFailure(string serverId, string summary)
    {
        var body = FikaRequestHandler.LastGetHostResponseBody;
        var failure = FikaRequestHandler.LastGetHostFailure;
        var message = $"[FikaPingingClient] {summary}: the host has not published an address on that server (their side); "
            + $"serverId={serverId}, server={FikaRequestHandler.ServerHost}, "
            + $"http={(failure == null ? "2xx (the http layer throws for every other status)" : failure)}, "
            + $"body={(body == null ? "<none: no body was returned>" : Abbreviate(body))}";
        _logger.LogError(message);
        
    }

    private static string DescribeAddresses(string[] addresses)
    {
        if (addresses == null || addresses.Length == 0)
        {
            return "none";
        }

        return string.Join(", ", addresses.Select(address => string.IsNullOrWhiteSpace(address) ? "<blank>" : address));
    }

    /// <summary>
    /// 本次尝试所用的地址：被 ping 过的那些，以及有应答的那些。
    /// 两者都重要，因为端点一旦应答就会离开 ping 列表。
    /// </summary>
    private string DescribeEndpoints()
    {
        var pinged = _endPoints is { Count: > 0 }
            ? string.Join(", ", _endPoints.Select(endpoint => endpoint.ToString()))
            : "none";
        var answered = _candidates is { Count: > 0 }
            ? string.Join(", ", _candidates.Select(candidate => candidate.ToString()))
            : "none";
        return $"pinged=[{pinged}], answered=[{answered}]";
    }

    private static string Abbreviate(string value) =>
        value.Length <= MaxLoggedBodyChars ? value : value[..MaxLoggedBodyChars] + $"...(+{value.Length - MaxLoggedBodyChars} chars)";

    public async Task<bool> AttemptToPingHost(string knockMessage, CancellationToken ct = default)
    {
        var deadline = Time.realtimeSinceStartup + 12.5f;
        while (!Rejected && !Received &&
               (Time.realtimeSinceStartup < deadline || (_hasResponse &&
                Time.realtimeSinceStartup - _firstResponseTime < _responseWaitSeconds)))
        {
            ct.ThrowIfCancellationRequested();
            PingEndPoint(knockMessage);
            if (NetClient != null)
            {
                NetClient.PollEvents();
                NetClient.NatPunchModule?.PollEvents();
            }

            await Task.Delay(250, ct);
        }

        if (!Received)
        {
            // (analysis/transit-timeout-crash) 握手在从未抵达任何主机的情况下放弃了：
            // ping 循环在 12.5 s 截止时间后返回 false。这里用一行把它作为事件陈述，
            // 因为它发生在突袭存在*之前*，也就是在任何 [LoadSync] 行之前。
            var pingedCount = _endPoints?.Count ?? 0;
            
            var logError = "Unable to connect to the session!";
            if (Rejected)
            {
                logError += $" Connection was rejected! [{FikaBackendUtils.ServerGuid}] did not match the server's Guid or data was malformed.";
            }
            if (InProgress)
            {
                logError += " Session already in progress and you are not active in the session!";
            }

            // 这次服务器确实给了地址（Init 返回 true），因此失败出在
            // 传输线上，而不是对局记录里：点出服务器、尝试过的地址，以及
            // 加入开始时服务器所回答的响应体。
            var body = FikaRequestHandler.LastGetHostResponseBody;
            var failure = FikaRequestHandler.LastGetHostFailure;
            logError += $" serverId={_serverId}, server={FikaRequestHandler.ServerHost}, tried={DescribeEndpoints()}, "
                + $"http={(failure == null ? "2xx (the http layer throws for every other status)" : failure)}, "
                + $"body={(body == null ? "<none: no body was returned>" : Abbreviate(body))}";
            FikaGlobals.LogError(logError);
            _logger.LogError(logError);
            

            FailureReason = $"The host's server did publish an address ({DescribeEndpoints()}), but nothing answered on it. "
                + "The host's port is not reachable from here (closed, not forwarded, or a stale address).";
        }

        return Received;
    }

    private void RemoveEndpointIfExists(IPEndPoint remote)
    {
        for (var i = _endPoints.Count - 1; i >= 0; i--)
        {
            var ep = _endPoints[i];
            if (ep.Address.Equals(remote.Address) && ep.Port == remote.Port)
            {
                _endPoints.RemoveAt(i);
                _logger.LogInfo($"Stopped pinging {ep}");
                return;
            }
        }
    }

    /// <summary>
    /// 向已配置的端点发送一条 ping 消息。
    /// </summary>
    /// <param name="message">要发送的消息。</param>
    public void PingEndPoint(string message)
    {
        if (_disposed) return;
        // 在等待窗口结束后敲定选择
        if (!Received && _hasResponse && Time.realtimeSinceStartup - _firstResponseTime >= _responseWaitSeconds)
        {
            var selected = SelectBestCandidate();

            if (selected != null)
            {
                CommitEndpoint(selected);
                Received = true;
                _endPoints.Clear(); // stop all further pings
                return;
            }

            _logger.LogError($"{_responseWaitSeconds} seconds has passed, but no candidate could be found?");
        }

        _writer.Reset();
        _writer.Put(message);

        foreach (var ipEndPoint in _endPoints)
        {
            NetClient.SendUnconnectedMessage(_writer.AsReadOnlySpan(), ipEndPoint);
        }
    }

    private void CommitEndpoint(IPEndPoint ep)
    {
        FikaBackendUtils.RemoteEndPoint = ep;
        FikaBackendUtils.LocalPort = (ushort)NetClient.LocalPort;

        _logger.LogInfo($"Picked candidate {ep.Address}:{ep.Port}, using LocalPort: {NetClient.LocalPort}");
    }

    private IPEndPoint SelectBestCandidate()
    {
        if (_candidates.Count == 0)
        {
            return null;
        }

        var preferIPv6 = FikaPlugin.Instance.Settings.UseIPv6.Value && FikaPlugin.Instance.Settings.PreferIPv6.Value;

        if (preferIPv6)
        {
            // IPv6 没有 NAT：直连候选是延迟最低的路径，而
            // 唯一本地地址就是 IPv6 中相当于主机局域网地址的东西。
            var ipv6 = _candidates.FirstOrDefault(c => c.Address.AddressFamily == AddressFamily.InterNetworkV6 && IsPrivate(c.Address))
                ?? _candidates.FirstOrDefault(c => c.Address.AddressFamily == AddressFamily.InterNetworkV6);
            if (ipv6 != null)
            {
                _logger.LogInfo($"Selected IPv6 candidate {ipv6.Address}:{ipv6.Port} (prefer IPv6 is enabled)");
                return ipv6;
            }
        }

        // 优先局域网
        var lan = _candidates.FirstOrDefault(c => IsPrivate(c.Address));
        if (lan != null)
        {
            return lan;
        }

        // 否则取最先应答的广域网地址
        return _candidates[0];
    }

    /// <inheritdoc/>
    public void OnConnectionRequest(ConnectionRequest request)
    {
        // 不执行任何操作
    }

    /// <inheritdoc/>
    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        // 不执行任何操作
    }

    /// <inheritdoc/>
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        // 不执行任何操作
    }

    /// <inheritdoc/>
    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        // 不执行任何操作
    }

    /// <summary>
    /// 处理从远端端点收到的无连接网络消息。
    /// </summary>
    /// <param name="remoteEndPoint">发送该消息的远端端点。</param>
    /// <param name="reader">包含消息数据的包读取器。</param>
    /// <param name="messageType">所收到的无连接消息的类型。</param>
    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        if (_disposed) return;
        if (reader.TryGetString(out var result))
        {
            switch (result)
            {
                case "fika.hello":
                    RemoveEndpointIfExists(remoteEndPoint);

                    if (!_hasResponse)
                    {
                        _firstResponseTime = Time.realtimeSinceStartup;
                        _hasResponse = true;
                    }

                    if (!_candidates.Any(c =>
                        c.Address.Equals(remoteEndPoint.Address) &&
                        c.Port == remoteEndPoint.Port))
                    {
                        _candidates.Add(remoteEndPoint);
                        _logger.LogInfo($"Received candidate: {remoteEndPoint}");
                    }

                    break;
                case "fika.inprogress":
                    InProgress = true;
                    break;
                case "fika.reject":
                    Rejected = true;
                    break;
                default:
                    _logger.LogError("Data was not as expected");
                    break;
            }
        }
        else
        {
            _logger.LogError("Could not parse string");
        }
    }

    private bool IsPrivate(IPAddress ip)
    {
        switch (ip.AddressFamily)
        {
            case AddressFamily.InterNetwork: // IPv4
                {
#if DEBUG
                    _logger.LogInfo($"Checking {ip} which is IPv4");
#endif
                    var b = ip.GetAddressBytes();
                    return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
                }
            case AddressFamily.InterNetworkV6: // IPv6
                {
#if DEBUG
                    _logger.LogInfo($"Checking {ip} which is IPv6");
#endif
                    var b = ip.GetAddressBytes();
                    // fc00::/7 > 检查第一个字节的高 7 位（0b1111_1100 = 0xfc）
                    return (b[0] & 0b1111_1100) == 0b1111_1100;
                }
            default:
                return false;
        }
    }

    /// <inheritdoc/>
    public void OnPeerConnected(NetPeer peer)
    {
        // 不执行任何操作
    }

    /// <inheritdoc/>
    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        // 不执行任何操作
    }

    /// <inheritdoc/>
    public void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
    {
        // 不执行任何操作
    }

    /// <inheritdoc/>
    public void OnNatIntroductionSuccess(IPEndPoint targetEndPoint, NatAddressType type, string token)
    {
        if (_disposed || !string.Equals(token, $"Client:{FikaBackendUtils.ServerGuid}", StringComparison.Ordinal)) return;
        if (!_natIntroductionRequested) return;
        if (_endPoints.Any(endpoint => endpoint.Equals(targetEndPoint)) ||
            _candidates.Any(endpoint => endpoint.Equals(targetEndPoint))) return;

        _logger.LogInfo($"Received endpoint {targetEndPoint} from the NAT punching server.");
        _endPoints.Add(targetEndPoint);
        // 打洞介绍可能同时暴露局域网和广域网路由。持续接收
        // 它们，直到其中一个应答；此时优先局域网的选择就能挑中
        // 同一网络上的对等端可达的那条路由。
        _cts?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _endPoints?.Clear();
        _candidates?.Clear();
        _endPoints = null;
        _candidates = null;
        _writer?.Reset();
        _writer = null;

        NetClient?.Stop();
        _cts.Dispose();
        if (!FikaSingleton<FikaPingingClient>.TryRelease(this))
        {
            _logger.LogError("Failed to release FikaPingingClient Singleton!");
        }
        _logger.LogInfo("Stopped FikaPingingClient");
    }
}
