using FikaServer.Helpers;
using FikaServer.Models.Enums;
using FikaServer.Models.Fika;
using FikaServer.Models.Fika.Config;
using FikaServer.Models.Fika.Presence;
using FikaServer.Models.Fika.Routes.Raid.Create;
using FikaServer.Services.Headless;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.InRaid;
using System.Collections.Concurrent;

namespace FikaServer.Services;

[Injectable(InjectionType.Singleton)]
public class MatchService(ISptLogger<MatchService> logger, LocationLifecycleService locationLifecycleService,
    SaveServer saveServer, FikaServerConfig fikaServerConfig, HeadlessHelper headlessHelper, HeadlessService headlessService,
    InsuranceService insuranceService, PresenceService presenceService, WebhookService webhookService)
{
    public ConcurrentDictionary<MongoId, FikaMatch> Matches { get; set; } = [];

    protected readonly ConcurrentDictionary<MongoId, Timer> _timeoutIntervals = [];
    // Retained for the service lifetime so an old waiter can never acquire a
    // different gate when the same headless profile starts another raid.
    private readonly ConcurrentDictionary<MongoId, SemaphoreSlim> _matchGates = [];

    /// <summary>
    /// Adds a timeout interval for the given match
    /// </summary>
    /// <param name="matchId">The match ID to add a timeout for</param>
    private void AddTimeoutInterval(MongoId matchId, FikaMatch expectedMatch)
    {
        if (_timeoutIntervals.ContainsKey(matchId))
        {
            RemoveTimeoutInterval(matchId);
        }

        Timer timer = new(async _ =>
        {
            try
            {
                // Disposed timer callbacks can run after the next raid has
                // reused this profile ID. They must never end the new match.
                if (!ReferenceEquals(GetMatch(matchId), expectedMatch)) return;

                bool timedOut;
                lock (expectedMatch)
                {
                    if (!ReferenceEquals(GetMatch(matchId), expectedMatch)) return;
                    timedOut = expectedMatch.Timeout++ >= fikaServerConfig.Server.SessionTimeout;
                }

                if (timedOut)
                    await EndMatch(matchId, EFikaMatchEndSessionMessage.PingTimeout, expectedMatch);
            }
            catch (Exception exception)
            {
                logger.Error($"Failed to handle match timeout for {matchId}", exception);
            }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        if (!_timeoutIntervals.TryAdd(matchId, timer))
        {
            logger.Error($"Failed to add match {matchId} to timers");
            timer.Dispose();
        }
    }

    /// <summary>
    /// Removes the timeout interval for the given match
    /// </summary>
    /// <param name="matchId">The match ID to remove a timeout for</param>
    private void RemoveTimeoutInterval(MongoId matchId)
    {
        if (_timeoutIntervals.TryRemove(matchId, out var timer))
        {
            timer.Dispose();
        }
        else
        {
            logger.Error($"Could not delete match {matchId} from timers");
        }
    }

    /// <summary>
    /// Get an ongoing match
    /// </summary>
    /// <param name="matchId">The match ID of the match to get</param>
    /// <returns>Returns the match object if a match is found with this match ID, returns null if not.</returns>
    public FikaMatch? GetMatch(MongoId? matchId)
    {
        if (!matchId.HasValue)
        {
            return null;
        }

        if (Matches.TryGetValue(matchId.Value, out var match))
        {
            return match;
        }

        return null;
    }

    /// <summary>
    /// Gets a player in an ongoing match
    /// </summary>
    /// <param name="matchId">The match ID of what match the player is in</param>
    /// <param name="playerId">The player ID to look for</param>
    /// <returns>Returns a FikaPlayer object if the player is found, returns null if not.</returns>
    public FikaPlayer? GetPlayerInMatch(MongoId matchId, MongoId playerId)
    {
        if (!Matches.TryGetValue(matchId, out var match))
        {
            return null;
        }

        lock (match)
        {
            return match.Players.TryGetValue(playerId, out var value) ? value : null;
        }
    }

    /// <summary>
    /// Returns the match ID that has a player with the given player ID.
    /// </summary>
    /// <param name="playerId">The ID of the player whose match ID is to be found.</param>
    /// <returns>The match ID containing the player, or null if the player isn't in a match.</returns>
    public MongoId? GetMatchIdByPlayer(MongoId playerId)
    {
        foreach ((var matchId, var match) in Matches)
        {
            lock (match)
            {
                // A match remains in the dictionary while its insurance
                // snapshot drains. Do not expose that ending match to new
                // StartLocalRaid/Headless callers; late EndLocalRaid uses
                // InsuranceService's retained token lookup instead.
                if (!match.IsEnding && match.Players.ContainsKey(playerId)) return matchId;
            }
        }

        return null;
    }

    /// <summary>
    /// Retrieves the match identifier associated with the specified profile session.
    /// </summary>
    /// <param name="sessionId">The unique identifier of the profile session for which to retrieve the match ID.</param>
    /// <returns>A nullable match identifier associated with the profile, or null if no match is found or the profile does exist.</returns>
    public MongoId? GetMatchIdByProfile(MongoId sessionId)
    {
        // Fika stores profile/session IDs as match player keys. Check that key
        // before falling back to character IDs used by older callers.
        var matchId = GetMatchIdByPlayer(sessionId);
        if (matchId.HasValue)
        {
            return matchId;
        }

        var profile = saveServer.GetProfile(sessionId);
        if (profile == null)
        {
            return null;
        }

        var characterData = profile.CharacterData;
        if (characterData is null)
        {
            return null;
        }

        var pmcId = characterData.PmcData?.Id;
        if (pmcId.HasValue)
        {
            matchId = GetMatchIdByPlayer(pmcId.Value);
            if (matchId.HasValue)
            {
                return matchId;
            }
        }

        var scavId = characterData.ScavData?.Id;
        return scavId.HasValue ? GetMatchIdByPlayer(scavId.Value) : null;
    }

    /// <summary>
    /// Atomically reserves a live match for a StartLocalRaid request.
    ///
    /// The reservation closes the gap between looking up a profile's match
    /// and registering the ServerId token in InsuranceService. EndMatch marks
    /// the same match as ending under its lock and waits for reservations
    /// that won this race. Requests arriving after IsEnding are rejected and
    /// may continue as ordinary SPT local raids.
    /// </summary>
    public bool TryBeginLocalRaid(MongoId sessionId, out MongoId matchId, out FikaMatch? expectedMatch)
    {
        // Preserve the same session/PMC/Scav identity fallback used by the
        // original StartLocalRaid path. Some older clients identify the
        // participant by character id rather than the profile session id.
        var candidateId = GetMatchIdByProfile(sessionId);
        if (candidateId is { } resolvedMatchId &&
            Matches.TryGetValue(resolvedMatchId, out var candidateMatch))
        {
            lock (candidateMatch)
            {
                if (!ReferenceEquals(GetMatch(resolvedMatchId), candidateMatch) ||
                    candidateMatch.IsEnding)
                {
                    matchId = default;
                    expectedMatch = null;
                    return false;
                }

                if (!candidateMatch.LocalRaidStarts.TryReserve(admissionOpen: true))
                {
                    matchId = default;
                    expectedMatch = null;
                    return false;
                }
                matchId = resolvedMatchId;
                expectedMatch = candidateMatch;
                return true;
            }
        }

        matchId = default;
        expectedMatch = null;
        return false;
    }

    /// <summary>
    /// Releases a StartLocalRaid reservation. The expected object check is
    /// required because a host profile id can be reused by a replacement
    /// match after the old match has been removed.
    /// </summary>
    public void EndLocalRaidStart(MongoId matchId, FikaMatch expectedMatch)
    {
        lock (expectedMatch)
        {
            if (!expectedMatch.LocalRaidStarts.Release())
            {
                logger.Warning($"Ignoring duplicate local-raid start release for {matchId}");
            }
        }
    }

    /// <summary>
    /// Waits for reservations that were admitted before a match began
    /// ending. A bounded wait mirrors insurance settlement and prevents a
    /// permanently faulted request from blocking server teardown forever.
    /// </summary>
    private async Task WaitForLocalRaidStartsAsync(MongoId matchId, FikaMatch expectedMatch)
    {
        var completed = await expectedMatch.LocalRaidStarts.WaitAsync(TimeSpan.FromSeconds(30));
        if (!completed)
        {
            logger.Warning($"Timed out waiting for {expectedMatch.LocalRaidStarts.InFlight} local-raid start(s) for {matchId}");
        }
    }

    /// <summary>
    /// Creates a new match with the specified configuration and associates it with the given session.
    /// </summary>
    /// <remarks>If a match with the same server ID already exists, it is deleted before creating the new
    /// match. The method also initializes the match with the provided settings and adds the host player to the
    /// match.</remarks>
    /// <param name="data">The configuration data for the match to be created, including server information, host details, and raid
    /// settings. Cannot be null.</param>
    /// <param name="sessionId">The unique identifier of the session to associate with the new match.</param>
    /// <returns>true if the match was successfully created and registered; otherwise, false.</returns>
    public async Task<bool> CreateMatch(FikaRaidCreateRequestData data, MongoId sessionId)
    {
        var matchGate = _matchGates.GetOrAdd(data.ServerId, _ => new SemaphoreSlim(1, 1));
        await matchGate.WaitAsync();
        FikaServer.Models.Fika.Headless.HeadlessClientInfo? lockedHeadlessOwner = null;
        try
        {
            // Bind the room to the exact headless connection/reservation that
            // created it. A replacement socket can become READY while this
            // room's raid-end callbacks are still draining.
            FikaServer.Models.Fika.Headless.HeadlessClientInfo? headlessOwner = null;
            long? headlessReservationVersion = null;
            var isHeadlessHost = headlessHelper.IsHeadlessClient(data.ServerId);
            if (isHeadlessHost &&
                !headlessService.HeadlessClients.TryGetValue(data.ServerId, out headlessOwner))
            {
                logger.Warning($"Headless room {data.ServerId} has no active host connection");
                return false;
            }
            if (headlessOwner != null)
            {
                await headlessOwner.LifecycleGate.WaitAsync();
                lockedHeadlessOwner = headlessOwner;
                lock (headlessOwner) headlessReservationVersion = headlessOwner.ReservationVersion;
            }

            // An old /raid/create must be rejected before it can tear down a
            // newer room. Keep the owner gate through the old-room cleanup,
            // location generation, publication, and host registration.
            if (isHeadlessHost && (headlessOwner == null ||
                !headlessReservationVersion.HasValue ||
                !headlessService.IsCurrentReservation(data.ServerId, headlessOwner,
                    headlessReservationVersion.Value, data.ReservationToken)))
            {
                logger.Warning($"Headless room {data.ServerId} lost its requester reservation before creation: " +
                    (headlessOwner == null || !headlessReservationVersion.HasValue
                        ? "host unavailable"
                        : headlessService.DescribeReservationRejection(data.ServerId, headlessOwner,
                            headlessReservationVersion.Value, data.ReservationToken)));
                return false;
            }

            if (Matches.TryGetValue(data.ServerId, out var previousMatch))
            {
                // The same reservation can replace its room during transit.
                // A late create from a prior reservation cannot end it.
                if (isHeadlessHost &&
                    (previousMatch.ServerGuid == data.ServerGuid ||
                     !ReferenceEquals(previousMatch.HeadlessReservationOwner, headlessOwner) ||
                     previousMatch.HeadlessReservationVersion != headlessReservationVersion))
                {
                    logger.Warning($"Ignoring stale or duplicate headless raid create for {data.ServerId}");
                    return false;
                }
                await EndMatchCore(data.ServerId, previousMatch, EFikaMatchEndSessionMessage.HostShutdown,
                    resetHeadless: false);
            }

            var locationData = locationLifecycleService.GenerateLocationAndLoot(sessionId, data.Settings!.Location!);

            if (isHeadlessHost && !headlessService.IsCurrentReservation(data.ServerId, headlessOwner!,
                headlessReservationVersion!.Value, data.ReservationToken))
            {
                logger.Warning($"Headless room {data.ServerId} lost its requester reservation during creation: " +
                    headlessService.DescribeReservationRejection(data.ServerId, headlessOwner!,
                        headlessReservationVersion!.Value, data.ReservationToken));
                return false;
            }

            FikaMatch match = new()
            {
                Ips = [],
                ServerGuid = data.ServerGuid,
                Port = 0,
                HostUsername = data.HostUsername,
                Timestamp = data.Timestamp,
                RaidConfig = data.Settings,
                LocationData = locationData,
                Status = EFikaMatchStatus.LOADING,
                Timeout = 0,
                Players = [],
                GameVersion = data.GameVersion,
                CRC32 = data.CRC32,
                Side = data.Side,
                Time = data.Time,
                RaidCode = data.RaidCode,
                NatPunch = false,
                UseFikaNatPunchServer = false,
                IsHeadless = false,
                Raids = 0,
                CustomRaidSettings = data.CustomRaidSettings,
                HeadlessReservationOwner = headlessOwner,
                HeadlessReservationVersion = headlessReservationVersion
            };

            if (!Matches.TryAdd(data.ServerId, match))
            {
                logger.Error($"Failed to create match {data.ServerId}");
                return false;
            }

            AddTimeoutInterval(data.ServerId, match);

            if (!AddPlayerToMatch(data.ServerId, data.ServerId, new FikaPlayer
            {
                GroupId = string.Empty,
                IsDead = false,
                IsSpectator = data.IsSpectator
            }))
            {
                lock (match) match.IsEnding = true;
                await insuranceService.OnMatchEndAsync(data.ServerId);
                await DeleteMatchCore(data.ServerId, match);
                return false;
            }

            return Matches.ContainsKey(data.ServerId) && _timeoutIntervals.ContainsKey(data.ServerId);
        }
        finally
        {
            lockedHeadlessOwner?.LifecycleGate.Release();
            matchGate.Release();
        }
    }

    /// <summary>
    /// Deletes a coop match and removes the timeout interval
    /// </summary>
    /// <param name="matchId">The match Id to remove</param>
    public ValueTask DeleteMatch(MongoId matchId)
    {
        var expectedMatch = GetMatch(matchId);
        return expectedMatch is null ? ValueTask.CompletedTask : DeleteMatch(matchId, expectedMatch);
    }

    private async ValueTask DeleteMatch(MongoId matchId, FikaMatch expectedMatch)
    {
        var matchGate = _matchGates.GetOrAdd(matchId, _ => new SemaphoreSlim(1, 1));
        await matchGate.WaitAsync();
        try
        {
            if (Matches.TryGetValue(matchId, out var match) && ReferenceEquals(match, expectedMatch))
            {
                lock (match) match.IsEnding = true;
                await WaitForLocalRaidStartsAsync(matchId, match);
                try { await insuranceService.OnMatchEndAsync(matchId); }
                finally { await DeleteMatchCore(matchId, match); }
            }
        }
        finally
        {
            matchGate.Release();
        }
    }

    private async ValueTask DeleteMatchCore(MongoId matchId, FikaMatch expectedMatch)
    {
        // Mutations on this match lock the same object and verify it is still
        // current. Removing under the lock prevents a late join from writing
        // player and insurance state after this raid has ended.
        bool removed;
        lock (expectedMatch)
        {
            removed = Matches.TryRemove(new KeyValuePair<MongoId, FikaMatch>(matchId, expectedMatch));
        }
        if (removed)
        {
            RemoveTimeoutInterval(matchId);
            logger.Info($"Deleted match [{matchId}]");

            var requesterName = headlessHelper.GetRequesterUsername(matchId)
                ?? expectedMatch.HostUsername;

            await webhookService.SendWebhookMessage($"{requesterName}'s raid has ended.");
        }
    }

    /// <summary>
    /// Ends the given match, logs a reason and removes the timeout interval
    /// </summary>
    /// <param name="matchId"></param>
    /// <param name="reason"></param>
    public ValueTask EndMatch(MongoId matchId, EFikaMatchEndSessionMessage reason)
    {
        var expectedMatch = GetMatch(matchId);
        return expectedMatch is null ? ValueTask.CompletedTask : EndMatch(matchId, reason, expectedMatch);
    }

    private async ValueTask EndMatch(MongoId matchId, EFikaMatchEndSessionMessage reason, FikaMatch expectedMatch)
    {
        var matchGate = _matchGates.GetOrAdd(matchId, _ => new SemaphoreSlim(1, 1));
        await matchGate.WaitAsync();
        try
        {
            if (!Matches.TryGetValue(matchId, out var match) ||
                !ReferenceEquals(match, expectedMatch)) return;

            await EndMatchCore(matchId, match, reason, resetHeadless: true);
        }
        finally
        {
            matchGate.Release();
        }
    }

    private async ValueTask EndMatchCore(MongoId matchId, FikaMatch match,
        EFikaMatchEndSessionMessage reason, bool resetHeadless)
    {
        // Close admission before awaiting insurance persistence. Otherwise a
        // late join can recreate an insurance record after it was finalized.
        lock (match) match.IsEnding = true;
        // A StartLocalRaid request admitted before IsEnding may still be
        // assembling its response. Let it register its token before the
        // insurance snapshot closes; requests admitted afterwards are
        // rejected by TryBeginLocalRaid and fall back to normal SPT.
        await WaitForLocalRaidStartsAsync(matchId, match);
        logger.Info($"Coop session {matchId} has ended {Enum.GetName(reason)}");
        try
        {
            await insuranceService.OnMatchEndAsync(matchId);
        }
        finally
        {
            // Keep the headless reserved until the match is gone. Deleting
            // first preserves the requester name for the webhook.
            try
            {
                await DeleteMatchCore(matchId, match);
            }
            finally
            {
                // During CreateMatch the headless may already have a new
                // requester reservation. That request owns the new match.
                if (resetHeadless && match.HeadlessReservationOwner != null)
                    headlessService.EndHeadlessRaid(matchId, match.HeadlessReservationOwner,
                        match.HeadlessReservationVersion);
            }
        }
    }

    /// <summary>
    /// Updates the status of the given match
    /// </summary>
    /// <param name="matchId"></param>
    /// <param name="status"></param>
    public async Task SetMatchStatus(MongoId matchId, EFikaMatchStatus status)
    {
        var matchGate = _matchGates.GetOrAdd(matchId, _ => new SemaphoreSlim(1, 1));
        await matchGate.WaitAsync();
        try
        {
            if (Matches.TryGetValue(matchId, out var match))
            {
                if (match.IsEnding) return;
                match.Status = status;
                presenceService.UpdateMatchStatus(matchId, status);

                if (status == EFikaMatchStatus.COMPLETE && match.IsHeadless)
                    await headlessService.SendJoinMessageToRequester(matchId, match,
                        () => ReferenceEquals(GetMatch(matchId), match));
            }
            else
                logger.Error($"Failed to find match {matchId} when trying to set status to {status}");
        }
        finally
        {
            matchGate.Release();
        }

    }

    /// <summary>
    /// Sets the ip and port for the given match
    /// </summary>
    /// <param name="matchId"></param>
    /// <param name="ips"></param>
    /// <param name="port"></param>
    /// <param name="natPunch"></param>
    /// <param name="useFikaNatPunchServer"></param>
    /// <param name="isHeadless"></param>
    public void SetMatchHost(MongoId matchId, string[] ips, ushort port, bool natPunch, bool useFikaNatPunchServer, bool isHeadless)
    {
        if (Matches.TryGetValue(matchId, out var match))
        {
            lock (match)
            {
                if (!ReferenceEquals(GetMatch(matchId), match)) return;
                match.Ips = ips;
                match.Port = port;
                match.NatPunch = natPunch;
                match.UseFikaNatPunchServer = useFikaNatPunchServer;
                match.IsHeadless = isHeadless;
            }
        }
    }

    /// <summary>
    /// Resets the timeout of the given match
    /// </summary>
    /// <param name="matchId"></param>
    public void ResetTimeout(MongoId matchId)
    {
        if (Matches.TryGetValue(matchId, out var match))
        {
            lock (match)
            {
                if (!ReferenceEquals(GetMatch(matchId), match)) return;

                match.Timeout = 0;
                if (_timeoutIntervals.TryGetValue(matchId, out var timer))
                {
                    // Restart the 1-minute countdown. A disposed timer can
                    // race this request while a raid is ending.
                    try { timer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)); }
                    catch (ObjectDisposedException) { }
                }
            }
        }
    }

    /// <summary>
    /// Adds a player to a match
    /// </summary>
    /// <param name="matchId">The match id</param>
    /// <param name="playerId">The player id to add to the match</param>
    /// <param name="data">Data for the player</param>
    public bool AddPlayerToMatch(MongoId matchId, MongoId playerId, FikaPlayer data)
    {
        if (!Matches.TryGetValue(matchId, out var match))
        {
            logger.Error($"Could not add player ({playerId}) to match {matchId}");
            return false;
        }

        lock (match)
        {
            if (!ReferenceEquals(GetMatch(matchId), match) || match.IsEnding) return false;
            if (!match.Players.TryAdd(playerId, data))
            {
                logger.Error($"Could not add player ({playerId}) to match {matchId}, they were most likely already in it");
                return false;
            }

            try
            {
                if (!insuranceService.AddPlayerToMatchId(matchId, playerId, data.IsSpectator))
                    throw new InvalidOperationException($"Insurance registration closed for match {matchId}");

                if (headlessHelper.IsHeadlessClient(matchId))
                    headlessService.AddPlayerToHeadlessMatch(matchId, playerId);

                presenceService.UpdatePlayerPresence(playerId, new FikaSetPresence
                {
                    Activity = EFikaPlayerPresences.IN_RAID,
                    RaidInformation = new FikaRaidPresence
                    {
                        Location = match.LocationData.Id,
                        Side = match.Side,
                        Time = match.Time,
                        Started = match.Status != EFikaMatchStatus.COMPLETE,
                        MatchId = matchId
                    }
                });
                return true;
            }
            catch (Exception exception)
            {
                match.Players.Remove(playerId);
                insuranceService.RemovePlayerFromMatchId(matchId, playerId);
                if (headlessHelper.IsHeadlessClient(matchId))
                    headlessService.RemovePlayerFromHeadlessMatch(matchId, playerId);
                logger.Error($"Could not fully register player ({playerId}) in match {matchId}", exception);
                return false;
            }
        }
    }

    /// <summary>
    /// Sets a player to dead
    /// </summary>
    /// <param name="matchId">The match id</param>
    /// <param name="playerId">The player id to set as dead</param>
    public void SetPlayerDead(MongoId matchId, MongoId playerId)
    {
        if (Matches.TryGetValue(matchId, out var match))
        {
            lock (match)
            {
                if (!ReferenceEquals(GetMatch(matchId), match) ||
                    !match.Players.TryGetValue(playerId, out var value)) return;
                value.IsDead = true;
            }
        }
    }

    /// <summary>
    /// Sets the groupId for a player
    /// </summary>
    /// <param name="matchId">The match id</param>
    /// <param name="playerId">The player id</param>
    /// <param name="groupId">The group to add <paramref name="playerId"/> to</param>
    public void SetPlayerGroup(MongoId matchId, MongoId playerId, string groupId)
    {
        if (Matches.TryGetValue(matchId, out var match))
        {
            lock (match)
            {
                if (!ReferenceEquals(GetMatch(matchId), match) ||
                    !match.Players.TryGetValue(playerId, out var value)) return;
                value.GroupId = groupId;
            }
        }
    }

    /// <summary>
    /// Removes a player from a match
    /// </summary>
    /// <param name="matchId">The match to remove the player from</param>
    /// <param name="playerId">The player id to remove</param>
    public void RemovePlayerFromMatch(MongoId matchId, MongoId playerId)
    {
        if (Matches.TryGetValue(matchId, out var match))
        {
            lock (match)
            {
                if (!ReferenceEquals(GetMatch(matchId), match) ||
                    !match.Players.Remove(playerId)) return;

                if (headlessHelper.IsHeadlessClient(matchId))
                    headlessService.RemovePlayerFromHeadlessMatch(matchId, playerId);

                presenceService.UpdatePlayerPresence(playerId, new FikaSetPresence
                {
                    Activity = EFikaPlayerPresences.IN_MENU
                });
            }
        }
    }
}
