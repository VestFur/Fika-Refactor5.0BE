using FikaServer.Helpers;
using FikaServer.Models.Enums;
using FikaServer.Models.Fika.API;
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
    // 在整个服务生命周期内保留，使旧的等待者永远不会在同一个
    // headless profile 开启另一场战局时获取到不同的闸门。
    private readonly ConcurrentDictionary<MongoId, SemaphoreSlim> _matchGates = [];

    /// <summary>
    /// 为指定战局添加超时时间间隔
    /// </summary>
    /// <param name="matchId">要为其添加超时的战局 ID</param>
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
                // 已释放的计时器回调可能在下一场战局已经
                // 复用该 profile ID 之后才运行。它们绝不能结束新的战局。
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
    /// 移除指定战局的超时时间间隔
    /// </summary>
    /// <param name="matchId">要为其移除超时的战局 ID</param>
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
    /// 获取进行中的战局
    /// </summary>
    /// <param name="matchId">要获取的战局的 match ID</param>
    /// <returns>若找到该 match ID 对应的战局则返回战局对象，否则返回 null。</returns>
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
    /// 获取进行中战局里的某个玩家
    /// </summary>
    /// <param name="matchId">该玩家所在战局的 match ID</param>
    /// <param name="playerId">要查找的玩家 ID</param>
    /// <returns>若找到该玩家则返回 FikaPlayer 对象，否则返回 null。</returns>
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
    /// 返回含有指定玩家 ID 的战局 ID。
    /// </summary>
    /// <param name="playerId">要查找其所在战局 ID 的玩家 ID。</param>
    /// <returns>包含该玩家的战局 ID；若该玩家不在任何战局中则为 null。</returns>
    public MongoId? GetMatchIdByPlayer(MongoId playerId)
    {
        foreach ((var matchId, var match) in Matches)
        {
            lock (match)
            {
                // 战局在其保险快照排空期间仍会留在字典中。不要把这个
                // 正在结束的战局暴露给新的 StartLocalRaid/Headless 调用方；
                // 迟到的 EndLocalRaid 会改用 InsuranceService 保留的
                // token 查询。
                if (!match.IsEnding && match.Players.ContainsKey(playerId)) return matchId;
            }
        }

        return null;
    }

    /// <summary>
    /// 获取与指定 profile 会话关联的战局标识符。
    /// </summary>
    /// <param name="sessionId">要为其获取 match ID 的 profile 会话的唯一标识符。</param>
    /// <returns>与该 profile 关联的可空战局标识符；若未找到战局或该 profile 不存在则为 null。</returns>
    public MongoId? GetMatchIdByProfile(MongoId sessionId)
    {
        // Fika 把 profile/会话 ID 存为战局玩家的键。先检查该键，
        // 再退回使用旧版调用方所使用的角色 ID。
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
    /// 以原子方式为一个 StartLocalRaid 请求预留活跃战局。
    ///
    /// 该预留填补了从查找 profile 所属战局到在 InsuranceService 中
    /// 注册 ServerId token 之间的间隙。EndMatch 会在其锁内把
    /// 同一战局标记为正在结束，并等待在此竞态中胜出的
    /// 预留。在 IsEnding 之后到达的请求会被拒绝，
    /// 并可继续作为普通的 SPT 本地战局进行。
    /// </summary>
    public bool TryBeginLocalRaid(MongoId sessionId, out MongoId matchId, out FikaMatch? expectedMatch)
    {
        // 保留原 StartLocalRaid 路径所使用的 session/PMC/Scav
        // 身份回退逻辑。一些旧版客户端用角色 id 而不是
        // profile 会话 id 来标识参与者。
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
    /// 释放一个 StartLocalRaid 预留。之所以需要做预期的对象检查，
    /// 是因为旧战局被移除后，主机 profile id 可能被替换的
    /// 战局重新复用。
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
    /// 等待在战局开始收尾之前被准入的预留。
    /// 有界等待与保险结算保持一致，并可防止永久出错的
    /// 请求永远阻塞服务器拆除。
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
    /// 以指定配置创建新战局，并将其与给定会话关联。
    /// </summary>
    /// <remarks>若已存在相同 server ID 的战局，则在创建新战局
    /// 之前将其删除。该方法还会用提供的设置初始化战局，并把主机玩家
    /// 加入战局。</remarks>
    /// <param name="data">待创建战局的配置数据，包括服务器信息、主机详情与战局
    /// 设置。不可为 null。</param>
    /// <param name="sessionId">要与新战局关联的会话的唯一标识符。</param>
    /// <returns>若战局成功创建并注册则为 true，否则为 false。</returns>
    public async Task<bool> CreateMatch(FikaRaidCreateRequestData data, MongoId sessionId)
    {
        var matchGate = _matchGates.GetOrAdd(data.ServerId, _ => new SemaphoreSlim(1, 1));
        await matchGate.WaitAsync();
        FikaServer.Models.Fika.Headless.HeadlessClientInfo? lockedHeadlessOwner = null;
        try
        {
            // 把房间绑定到创建它的确切 headless 连接/预留上。
            // 一个替换用的 socket 可能在本房间的战局结束回调仍在
            // 排空时变为 READY。
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

            // 旧的 /raid/create 必须在它能拆除较新的房间之前被拒绝。
            // 从旧房间清理、位置生成、发布到主机注册的整个过程
            // 都要保持所有者闸门。
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
                // 同一预留可以在中转期间替换自己的房间。
                // 来自先前预留的迟到创建请求无法结束它。
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
    /// 删除合作战局并移除超时时间间隔
    /// </summary>
    /// <param name="matchId">要移除的战局 Id</param>
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
        // 对该战局的变更会锁定同一个对象，并校验它仍是当前对象。
        // 在锁内移除可防止迟到的加入在本场战局结束后
        // 写入玩家与保险状态。
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
    /// 结束指定战局、记录原因并移除超时时间间隔
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
        // 在等待保险持久化之前关闭准入。否则一个迟到的加入请求
        // 可能在保险记录已定稿后重新创建它。
        lock (match) match.IsEnding = true;
        // 在 IsEnding 之前被准入的 StartLocalRaid 请求可能仍在
        // 组装响应。应让它在其保险快照关闭之前注册 token；
        // 此后被准入的请求会被 TryBeginLocalRaid 拒绝，
        // 并回退为普通 SPT 行为。
        await WaitForLocalRaidStartsAsync(matchId, match);
        logger.Info($"Coop session {matchId} has ended {Enum.GetName(reason)}");
        try
        {
            await insuranceService.OnMatchEndAsync(matchId);
        }
        finally
        {
            // 在战局消失之前保持 headless 的预留状态。先执行删除
            // 可以保留请求方名称供 webhook 使用。
            try
            {
                await DeleteMatchCore(matchId, match);
            }
            finally
            {
                // 在 CreateMatch 期间，该 headless 可能已有新的请求方
                // 预留。那个请求才拥有新战局。
                if (resetHeadless && match.HeadlessReservationOwner != null)
                    headlessService.EndHeadlessRaid(matchId, match.HeadlessReservationOwner,
                        match.HeadlessReservationVersion);
            }
        }
    }

    /// <summary>
    /// 更新指定战局的状态
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
    /// 设置指定战局的 ip 与端口
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

            return;
        }

        // 房间（还）不存在：/fika/raid/create 要么尚未执行，要么已经替换
        // 了它。静默丢弃地址正是让加入方报告 "IPs: , Port: 0" 却无法
        // 判断是主机从未发布、还是记录在此处被丢弃的原因。
        var published = 0;
        if (ips != null)
        {
            for (var i = 0; i < ips.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(ips[i]))
                {
                    published++;
                }
            }
        }

        logger.Warning($"sethost for unknown match {matchId}: the host address was dropped "
            + $"({published} usable address(es), port {port}, natPunch={natPunch}, isHeadless={isHeadless})");
    }

    /// <summary>
    /// 重置指定战局的超时
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
                    // 重新开始 1 分钟倒计时。已释放的计时器可能
                    // 在战局结束期间与此请求并发。
                    try { timer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)); }
                    catch (ObjectDisposedException) { }
                }
            }
        }
    }

    /// <summary>
    /// 将玩家添加到战局
    /// </summary>
    /// <param name="matchId">战局 id</param>
    /// <param name="playerId">要加入战局的玩家 id</param>
    /// <param name="data">该玩家的数据</param>
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
                // 已经是参与者：这是重复通告，并非错误。刷新新请求
                // 携带的标志，并保留记录的其余部分，因为战局主机自身的
                // 角色状态依附于此。
                var existing = match.Players[playerId];
                existing.IsSpectator = data.IsSpectator;
                if (!string.IsNullOrEmpty(data.GroupId))
                {
                    existing.GroupId = data.GroupId;
                }

                logger.Info($"{playerId} is already a participant of match {matchId} "
                    + "(spectator=" + existing.IsSpectator + ")");
                return true;
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
    /// 将玩家设为死亡
    /// </summary>
    /// <param name="matchId">战局 id</param>
    /// <param name="playerId">要设为已死亡的玩家 id</param>
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
    /// 设置某个玩家的 groupId
    /// </summary>
    /// <param name="matchId">战局 id</param>
    /// <param name="playerId">玩家 id</param>
    /// <param name="groupId">要将 <paramref name="playerId"/> 加入的队伍</param>
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
    /// 从战局中移除玩家
    /// </summary>
    /// <param name="matchId">要从中移除玩家的战局</param>
    /// <param name="playerId">要移除的玩家 id</param>
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
