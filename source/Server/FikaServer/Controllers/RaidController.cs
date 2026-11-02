using FikaServer.Helpers;
using FikaServer.Models.Fika.API;
using FikaServer.Models.Fika.Routes.Headless;
using FikaServer.Models.Fika.Routes.Raid;
using FikaServer.Models.Fika.Routes.Raid.Create;
using FikaServer.Models.Fika.Routes.Raid.Host;
using FikaServer.Models.Fika.Routes.Raid.Join;
using FikaServer.Models.Fika.Routes.Raid.Leave;
using FikaServer.Models.Fika.Routes.Raid.Settings;
using FikaServer.Models.Fika.WebSocket.Notifications;
using FikaServer.Services;
using FikaServer.Services.Headless;
using FikaServer.WebSockets;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.InRaid;

namespace FikaServer.Controllers;

[Injectable]
public class RaidController(MatchService matchService, HeadlessHelper headlessHelper,
    HeadlessService headlessService,
    ISptLogger<RaidController> logger,
    InRaidController inraidController,
    NotificationWebSocket notificationWebSocket,
    WebhookService webhookService)
{
    /// <summary>
    /// 处理 /fika/raid/create
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    public async Task<FikaRaidCreateResponse> HandleRaidCreate(FikaRaidCreateRequestData request, MongoId sessionId)
    {
        // 服务器 id 就是通过认证的主机 profile/会话 id。不要让
        // 客户端替另一个 profile 创建房间。
        if (request.ServerId != sessionId)
        {
            logger.Warning($"Rejected raid create for {request.ServerId} from session {sessionId}");
            return new FikaRaidCreateResponse { Success = false };
        }

        var success = await matchService.CreateMatch(request, sessionId);
        if (!success)
        {
            // 只有房间真正注册之后才广播它。这样
            // 能让大厅通知与 webhook 跟返回给主机的响应
            // 保持一致。
            return new FikaRaidCreateResponse { Success = false };
        }

        var hostUsername = request.HostUsername;

        if (headlessHelper.IsHeadlessClient(request.ServerId))
        {
            hostUsername = headlessHelper.GetHeadlessNickname(request.ServerId);
        }

        var requesterName = headlessHelper.GetRequesterUsername(request.ServerId)
            ?? hostUsername;

        // 对局已经公布。一个断开的通知
        // 订阅者或不可用的 webhook 不得把一次成功的创建
        // 变成 HTTP 失败:否则主机会以为创建失败,而服务器
        // 却保留了它的房间,以及(对 Headless 而言)它的预留。
        try
        {
            await notificationWebSocket.BroadcastAsync(new StartRaidNotification
            {
                Nickname = hostUsername,
                Location = request.Settings!.Location!,
                IsHeadlessRaid = headlessHelper.IsHeadlessClient(request.ServerId),
                HeadlessRequesterName = requesterName,
                RaidTime = request.Time
            });
        }
        catch (Exception exception)
        {
            logger.Error($"Failed to broadcast created raid {request.ServerId}", exception);
        }

        try
        {
            await webhookService.SendWebhookMessage($"{requesterName} has started a {request.Side} raid on {request.Settings!.Location!.ToLower().ToFikaLocation()}.");
        }
        catch (Exception exception)
        {
            logger.Error($"Failed to send created-raid webhook for {request.ServerId}", exception);
        }

        return new FikaRaidCreateResponse
        {
            Success = true
        };
    }

    /// <summary>
    /// 处理 /fika/raid/join
    /// </summary>
    /// <param name="request">加入请求的数据</param>
    /// <returns>一个新的 <see cref="FikaRaidJoinResponse"/></returns>
    public FikaRaidJoinResponse HandleRaidJoin(FikaRaidJoinRequestData request, MongoId sessionId)
    {
        // 加入请求携带加入方的 profile id。它必须与通过认证的
        // SPT 会话一致,然后才能返回任何房间元数据。
        if (!string.Equals(request.ProfileId, sessionId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            logger.Warning($"Rejected raid join for profile {request.ProfileId} from session {sessionId}");
            return new FikaRaidJoinResponse();
        }

        var match = matchService.GetMatch(request.ServerId);
        if (match == null)
        {
            // 大厅在列出与加入之间可能变旧。此时返回
            // 一个安全的空载荷,而不是去解引用一个不存在的房间。
            return new FikaRaidJoinResponse();
        }

        lock (match)
        {
            if (match.IsEnding)
                return new FikaRaidJoinResponse();

            return new FikaRaidJoinResponse
            {
                ServerId = request.ServerId,
                ServerGuid = match.ServerGuid,
                Timestamp = match.Timestamp,
                GameVersion = match.GameVersion,
                CRC32 = match.CRC32,
                RaidCode = match.RaidCode,
            };
        }
    }

    /// <summary>
    /// 处理 /fika/raid/leave
    /// </summary>
    /// <param name="request"></param>
    public async Task HandleRaidLeave(FikaRaidLeaveRequestData request, MongoId sessionId)
    {
        if (request.ServerId == request.ProfileId)
        {
            // 只有通过认证的主机才能终止房间。访客
            // 绝不能在这条路径上伪造主机的 profile id。
            if (!string.Equals(request.ServerId, sessionId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                logger.Warning($"Rejected host raid leave for {request.ServerId} from session {sessionId}");
                return;
            }

            await matchService.EndMatch(request.ServerId, Models.Enums.EFikaMatchEndSessionMessage.HostShutdown);
            return;
        }

        // 访客可以离开自己的槽位,但不能移除另一个 profile。
        if (!string.Equals(request.ProfileId, sessionId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            logger.Warning($"Rejected raid leave for profile {request.ProfileId} from session {sessionId}");
            return;
        }

        matchService.RemovePlayerFromMatch(request.ServerId, request.ProfileId);
    }

    /// <summary>
    /// 处理 /fika/raid/gethost
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    public FikaRaidGethostResponse? HandleRaidGetHost(FikaRaidServerIdRequestData request)
    {
        var match = matchService.GetMatch(request.ServerId);

        if (match == null)
        {
            return null;
        }

        return new FikaRaidGethostResponse
        {
            Ips = match.Ips,
            ServerGuid = match.ServerGuid,
            Port = match.Port,
            NatPunch = match.NatPunch,
            UseFikaNatPunchServer = match.UseFikaNatPunchServer,
            IsHeadless = match.IsHeadless,
        };
    }

    /// <summary>
    /// 处理 /fika/raid/getsettings
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    public FikaRaidSettingsResponse? HandleRaidGetSettings(FikaRaidServerIdRequestData request)
    {
        var match = matchService.GetMatch(request.ServerId);

        if (match == null)
        {
            logger.Error($"Could not find match with id {request.ServerId}");
            return null;
        }

        return new FikaRaidSettingsResponse
        {
            Received = true,
            MetabolismDisabled = match.RaidConfig!.MetabolismDisabled!.Value,
            CustomRaidSettings = match.CustomRaidSettings,
            PlayersSpawnPlace = match.RaidConfig!.PlayersSpawnPlace!.Value,
            HourOfDay = match.RaidConfig!.TimeAndWeatherSettings!.HourOfDay!.Value,
            TimeFlowType = match.RaidConfig!.TimeAndWeatherSettings!.TimeFlowType!.Value
        };
    }

    /// <summary>
    /// 处理 /fika/raid/headless/start
    /// </summary>
    /// <param name="sessionID"></param>
    /// <param name="info"></param>
    /// <returns></returns>
    public async Task<StartHeadlessResponse> HandleRaidStartHeadless(MongoId sessionID, StartHeadlessRequest info)
    {
        // SPT 5 从 PHPSESSID cookie 推导 HTTP 会话。未经认证的请求
        // 否则会用一个空的请求方 ID 预留一个 Headless socket,
        // 而这样的 ID 无法拥有由此产生的房间。
        if (sessionID.IsEmpty)
        {
            logger.Warning("Rejected Headless raid start without a session cookie");
            return new StartHeadlessResponse
            {
                MatchId = null,
                Error = "A valid session cookie is required to start a Headless raid."
            };
        }

        if (!headlessHelper.IsHeadlessClientAvailable(info.HeadlessSessionID))
        {
            return new StartHeadlessResponse
            {
                MatchId = null,
                Error = "This headless client is not available."
            };
        }

        if (headlessHelper.IsHeadlessClient(sessionID))
        {
            return new StartHeadlessResponse
            {
                MatchId = null,
                Error = "You are trying to connect to a headless client while having Fika.Headless installed. Please remove Fika.Headless from your client and try again."
            };
        }

        var headlessClientId = await headlessService.StartHeadlessRaid(info.HeadlessSessionID, sessionID, info);

        if (string.IsNullOrEmpty(headlessClientId))
        {
            // 可用性只是一个快照。并发的请求方或一次重连
            // 可能在上面的检查与 StartHeadlessRaid 之间抢走这次预留;
            // 此时应返回一个有意义的响应,而不是一个既为 null
            // 又无错误的 match id,那会让客户端永远等下去。
            return new StartHeadlessResponse
            {
                MatchId = null,
                Error = "This headless client was reserved or disconnected before the raid could start."
            };
        }

        logger.Info($"Sent WS FikaHeadlessStartRaid to {headlessClientId}");

        return new StartHeadlessResponse
        {
            // 这其实并不必需,我只是想在客户端上确认一下
            MatchId = headlessClientId,
            Error = null
        };
    }

    /// <summary>
    /// 处理 /fika/raid/registerPlayer
    /// </summary>
    /// <param name="SessionID"></param>
    /// <param name="info"></param>
    public void HandleRaidRegisterPlayer(MongoId SessionID, RegisterPlayerRequestData info)
    {
        inraidController.AddPlayer(SessionID, info);
    }
}
