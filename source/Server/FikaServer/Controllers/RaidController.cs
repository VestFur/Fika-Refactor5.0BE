using FikaServer.Helpers;
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
    /// Handle /fika/raid/create
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    public async Task<FikaRaidCreateResponse> HandleRaidCreate(FikaRaidCreateRequestData request, MongoId sessionId)
    {
        // The server id is the authenticated host profile/session id.  Do not
        // allow a client to create a room on behalf of another profile.
        if (request.ServerId != sessionId)
        {
            logger.Warning($"Rejected raid create for {request.ServerId} from session {sessionId}");
            return new FikaRaidCreateResponse { Success = false };
        }

        var success = await matchService.CreateMatch(request, sessionId);
        if (!success)
        {
            // Only announce a room after it was actually registered.  This
            // keeps lobby notifications and webhooks consistent with the
            // response returned to the host.
            return new FikaRaidCreateResponse { Success = false };
        }

        var hostUsername = request.HostUsername;

        if (headlessHelper.IsHeadlessClient(request.ServerId))
        {
            hostUsername = headlessHelper.GetHeadlessNickname(request.ServerId);
        }

        var requesterName = headlessHelper.GetRequesterUsername(request.ServerId)
            ?? hostUsername;

        // The match has already been published. A disconnected notification
        // subscriber or unavailable webhook must not turn a successful create
        // into an HTTP failure: the host would believe creation failed while
        // the server retained its room and (for Headless) its reservation.
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
    /// Handle /fika/raid/join
    /// </summary>
    /// <param name="request">The join request data</param>
    /// <returns>A new <see cref="FikaRaidJoinResponse"/></returns>
    public FikaRaidJoinResponse HandleRaidJoin(FikaRaidJoinRequestData request, MongoId sessionId)
    {
        // A join request carries the joining profile id.  It must agree with
        // the authenticated SPT session before any room metadata is returned.
        if (!string.Equals(request.ProfileId, sessionId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            logger.Warning($"Rejected raid join for profile {request.ProfileId} from session {sessionId}");
            return new FikaRaidJoinResponse();
        }

        var match = matchService.GetMatch(request.ServerId);
        if (match == null)
        {
            // The lobby can become stale between listing and joining.  Return
            // a safe empty payload instead of dereferencing a missing room.
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
    /// Handle /fika/raid/leave
    /// </summary>
    /// <param name="request"></param>
    public async Task HandleRaidLeave(FikaRaidLeaveRequestData request, MongoId sessionId)
    {
        if (request.ServerId == request.ProfileId)
        {
            // Only the authenticated host may terminate the room.  A guest
            // must never be able to spoof the host's profile id in this path.
            if (!string.Equals(request.ServerId, sessionId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                logger.Warning($"Rejected host raid leave for {request.ServerId} from session {sessionId}");
                return;
            }

            await matchService.EndMatch(request.ServerId, Models.Enums.EFikaMatchEndSessionMessage.HostShutdown);
            return;
        }

        // Guests may leave their own slot, but cannot remove another profile.
        if (!string.Equals(request.ProfileId, sessionId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            logger.Warning($"Rejected raid leave for profile {request.ProfileId} from session {sessionId}");
            return;
        }

        matchService.RemovePlayerFromMatch(request.ServerId, request.ProfileId);
    }

    /// <summary>
    /// Handle /fika/raid/gethost
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
    /// Handle /fika/raid/getsettings
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
    /// Handle /fika/raid/headless/start
    /// </summary>
    /// <param name="sessionID"></param>
    /// <param name="info"></param>
    /// <returns></returns>
    public async Task<StartHeadlessResponse> HandleRaidStartHeadless(MongoId sessionID, StartHeadlessRequest info)
    {
        // SPT 5 derives the HTTP session from the PHPSESSID cookie. An
        // unauthenticated request otherwise reserves a Headless socket with
        // an empty requester ID, which cannot own the resulting room.
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
            // Availability is only a snapshot. A concurrent requester or a
            // reconnect can win the reservation between the check above and
            // StartHeadlessRaid; return a useful response instead of a null
            // match id with no error, which leaves the client waiting forever.
            return new StartHeadlessResponse
            {
                MatchId = null,
                Error = "This headless client was reserved or disconnected before the raid could start."
            };
        }

        logger.Info($"Sent WS FikaHeadlessStartRaid to {headlessClientId}");

        return new StartHeadlessResponse
        {
            // This really isn't required, I just want to make sure on the client
            MatchId = headlessClientId,
            Error = null
        };
    }

    /// <summary>
    /// Handle /fika/raid/registerPlayer
    /// </summary>
    /// <param name="SessionID"></param>
    /// <param name="info"></param>
    public void HandleRaidRegisterPlayer(MongoId SessionID, RegisterPlayerRequestData info)
    {
        inraidController.AddPlayer(SessionID, info);
    }
}
