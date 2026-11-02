using Fika.Core.Networking.LiteNetLib;
using FikaServer.Models.Fika.Config;
using FikaServer.Models.Servers;
using FikaServer.Models.Servers.Enums;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using System.Net;
using System.Net.Sockets;

namespace FikaServer.Servers;

[Injectable(InjectionType.Singleton)]
public class NatPunchServer(FikaServerConfig fikaServerConfig, ISptLogger<NatPunchServer> logger) : INatPunchListener, INetEventListener
{
    private readonly Dictionary<Guid, NatPunchPeer> _serverPeers = [];
    private NetManager? _netServer;
    private CancellationTokenSource? _pollEventsRoutineCts;
    private DateTime _lastCleanupPeers = DateTime.Now;

    public void Start()
    {
        if (_netServer is null)
        {
            _netServer = new(this)
            {
                IPv6Enabled = false,
                NatPunchEnabled = true
            };
        }

        try
        {
            // IPv6 绑定地址必须是一个真实地址：空字符串会走
            // NetUtils.ResolveAddress -> Dns.GetHostEntry("")，其结果
            // 取决于解析器（要么只得到单个链路本地地址，要么抛出一个被
            // 下方吞掉的异常，让打洞服务器静默失效）。
            _netServer.Start(fikaServerConfig.Server.SPT.Http.Ip, "::", fikaServerConfig.NatPunchServer.Port);
            _netServer.NatPunchModule.Init(this);

            _pollEventsRoutineCts = new CancellationTokenSource();
            Task.Run(PollEventsRoutine, _pollEventsRoutineCts.Token);

            logger.Success($"[Fika NatPunch] NatPunchServer started on {fikaServerConfig.Server.SPT.Http.Ip}:{_netServer.LocalPort}");
        }
        catch (Exception ex)
        {
            logger.Error($"[Fika NatPunch] NatPunchServer failed to start: {ex.Message}");
        }
    }

    public void Stop()
    {
        _netServer?.Stop();
        _pollEventsRoutineCts?.Cancel();
    }

    public void PollEvents()
    {
        _netServer?.NatPunchModule?.PollEvents();
    }

    public void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
    {
        if (!TryParseToken(token, out var natPunchToken) || natPunchToken is null)
        {
            logger.Error($"[Fika NatPunch] Invalid token sent by peer: {remoteEndPoint}.");
            return;
        }

        var guid = natPunchToken.Guid;

        if (natPunchToken.Type == NatPunchType.Server)
        {
            if (_serverPeers.TryGetValue(guid, out var serverPeer))
            {
                serverPeer.UpdateRequestInfo();

                logger.Info($"[Fika NatPunch] Keep Alive {guid} ({remoteEndPoint}).");
            }
            else
            {
                _serverPeers[guid] = new(guid, localEndPoint, remoteEndPoint);

                logger.Info($"[Fika NatPunch] Added {guid} ({remoteEndPoint}) to server peers.");
            }
        }

        if (natPunchToken.Type == NatPunchType.Client)
        {
            if (_serverPeers.TryGetValue(guid, out var serverPeer))
            {
                logger.Info($"[Fika NatPunch] Introducing server {guid} to client: {remoteEndPoint}.");

                _netServer?.NatPunchModule?.NatIntroduce(
                    serverPeer.InternalAddr,
                    serverPeer.ExternalAddr,
                    localEndPoint,
                    remoteEndPoint,
                    token
                );
            }
            else
            {
                logger.Error($"[Fika NatPunch] Unknown server GUID ({guid}) provided by client: {remoteEndPoint}.");
            }
        }
    }

    public void OnNatIntroductionResponse(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
    {
        // 不做任何事
    }

    public void OnNatIntroductionSuccess(IPEndPoint targetEndPoint, NatAddressType type, string token)
    {
        // 不做任何事
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        // 不做任何事
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        // 不做任何事
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        // 不做任何事
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        // 不做任何事
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        // 不做任何事
    }

    public void OnPeerConnected(NetPeer peer)
    {
        // 不做任何事
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        // 不做任何事
    }

    private void CleanupPeers()
    {
        var currentTime = DateTime.Now;

        if (currentTime - _lastCleanupPeers > TimeSpan.FromSeconds(3))
        {
            List<Guid> serverPeerGuidsToRemove = [];

            foreach (var serverPeersGuid in _serverPeers.Keys)
            {
                var serverPeer = _serverPeers[serverPeersGuid];

                if (!serverPeer.IsActive(TimeSpan.FromSeconds(30)))
                {
                    serverPeerGuidsToRemove.Add(serverPeersGuid);
                }
            }

            foreach (var serverPeerGuidToRemove in serverPeerGuidsToRemove)
            {
                _serverPeers.Remove(serverPeerGuidToRemove);
                logger.Info($"[Fika NatPunch] Removed {serverPeerGuidToRemove} from server peers.");
            }

            _lastCleanupPeers = currentTime;
        }
    }

    private async Task PollEventsRoutine()
    {
        while (_netServer != null && _pollEventsRoutineCts != null)
        {
            if (_pollEventsRoutineCts.IsCancellationRequested)
            {
                break;
            }

            PollEvents();
            CleanupPeers();

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    private bool TryParseToken(string token, out NatPunchToken? natPunchToken)
    {
        natPunchToken = null;

        if (!token.Contains(':'))
        {
            return false;
        }

        string strGuid;
        string strType;

        try
        {
            strType = token.Split(':')[0];
            strGuid = token.Split(':')[1];
        }
        catch
        {
            return false;
        }

        if (strType != "Server" && strType != "Client")
        {
            return false;
        }

        if (!Enum.TryParse(strType, out NatPunchType natPunchType))
        {
            return false;
        }

        if (!Guid.TryParse(strGuid, out var guid))
        {
            return false;
        }

        natPunchToken = new(natPunchType, guid);

        return true;
    }
}
