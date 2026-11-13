using FikaServer.Models.Enums;
using FikaServer.Models.Fika;
using FikaServer.Models.Fika.Config;
using FikaServer.Models.Fika.Headless;
using FikaServer.Models.Fika.Routes.Headless;
using FikaServer.WebSockets;
using Spectre.Console;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace FikaServer.Services.Headless;

[Injectable(InjectionType.Singleton)]
public class HeadlessService(ISptLogger<HeadlessService> logger,
    HeadlessRequesterWebSocket headlessRequesterWebSocket, JsonUtil jsonUtil,
    FikaServerConfig fikaServerConfig, SaveServer saveServer)
{
    public ConcurrentDictionary<MongoId, HeadlessClientInfo> HeadlessClients { get; } = [];

    /// <summary>
    /// 开始为某个 headless 客户端设置战局
    /// </summary>
    /// <returns>返回正在启动此战局的 headless 客户端的 SessionID；若找不到客户端或发生错误则返回 null。</returns>
    public async Task<string?> StartHeadlessRaid(string headlessSessionID, string requesterSessionID, StartHeadlessRequest info)
    {
        if (!HeadlessClients.TryGetValue(headlessSessionID, out var headlessClientInfo))
        {
            logger.LogWithColor($"Could not find HeadlessSessionID '{headlessSessionID}'", Color.Red);
            return null;
        }

        await headlessClientInfo.LifecycleGate.WaitAsync();
        try
        {
            WebSocket webSocket;
            long reservationVersion;
            string reservationToken;
            lock (headlessClientInfo)
            {
                // available-headless 的响应只是一个快照。在获取
                // 生命周期闸门之后，需确认该对象仍是已注册的
                // 连接，然后在闸门阻止重连/关闭替换它的情况下
                // 完成预留并发送。
                if (!HeadlessClients.TryGetValue(headlessSessionID, out var current) ||
                    !ReferenceEquals(current, headlessClientInfo) ||
                    headlessClientInfo.State != EHeadlessStatus.READY ||
                    !headlessClientInfo.ClientReady ||
                    headlessClientInfo.IsReconnecting ||
                    headlessClientInfo.RequesterSessionID != null)
                    return null;

                webSocket = headlessClientInfo.WebSocket;
                if (webSocket == null || webSocket.State != WebSocketState.Open)
                    return null;

                headlessRequesterWebSocket.ClearPending(requesterSessionID);
                headlessClientInfo.StartRaid(requesterSessionID);
                reservationVersion = headlessClientInfo.ReservationVersion;
                reservationToken = headlessClientInfo.ReservationToken!;
            }

            try
            {
                // 调用方无法选择该 token。它属于我们刚刚为
                // 这一确切连接所做的预留。
                StartHeadlessRaid startHeadlessRequest = new(info with
                {
                    ReservationToken = reservationToken
                });
                var data = jsonUtil.Serialize(startHeadlessRequest)
                    ?? throw new NullReferenceException("StartHeadlessRaid:: Data was null after serializing");
                logger.Debug($"Starting Headless reservation for {headlessSessionID}: generation={reservationVersion}, token={TokenLabel(reservationToken)}");
                using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await webSocket.SendAsync(Encoding.UTF8.GetBytes(data),
                    WebSocketMessageType.Text, true, sendTimeout.Token);
            }
            catch
            {
                lock (headlessClientInfo)
                {
                    if (headlessClientInfo.ReservationVersion == reservationVersion &&
                        headlessClientInfo.State == EHeadlessStatus.READY &&
                        headlessClientInfo.RequesterSessionID == requesterSessionID)
                    {
                        // SendAsync 失败并不能证明远端对端
                        // 没有收到消息。隔离该连接，使第二个
                        // 请求方无法在其仍能发出 /raid/create
                        // 时预留它。
                        headlessClientInfo.IsReconnecting = true;
                    }
                }
                try { webSocket.Abort(); }
                catch (Exception exception)
                {
                    logger.Warning($"Failed to abort uncertain Headless start socket: {exception.Message}");
                }
                throw;
            }

            return headlessSessionID;
        }
        finally
        {
            headlessClientInfo.LifecycleGate.Release();
        }
    }

    /// <summary>
    /// 向某个 headless 客户端的请求方发送加入消息
    /// </summary>
    public async Task SendJoinMessageToRequester(MongoId headlessClientId, FikaMatch expectedMatch,
        Func<bool> isCurrentMatch)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var headlessClientInfo))
        {
            logger.LogWithColor($"Could not find HeadlessSessionID '{headlessClientId}'", Color.Red);
            return;
        }

        await headlessClientInfo.LifecycleGate.WaitAsync();
        try
        {
            // 状态请求可能排在重连/关闭之后等待。不要用旧战局的
            // COMPLETE 事件去通知新的预留。
            string requesterSessionId;
            lock (expectedMatch)
            {
                // 在这次发送期间 SetMatchStatus 一直持有 matchGate，因此
                // EndMatch 无法在通知完成之前移除该房间或重置其
                // 预留。此处需确认确切的所有者。
                if (!HeadlessClients.TryGetValue(headlessClientId, out var currentClient) ||
                    !ReferenceEquals(currentClient, headlessClientInfo) ||
                    !ReferenceEquals(expectedMatch.HeadlessReservationOwner, headlessClientInfo) ||
                    expectedMatch.HeadlessReservationVersion != headlessClientInfo.ReservationVersion ||
                    !isCurrentMatch() || expectedMatch.IsEnding) return;

                lock (headlessClientInfo)
                {
                    // SetMatchStatus(COMPLETE) 可能被重复调用。只有第一次状态转换
                    // 可以把请求方送入战局。
                    if (headlessClientInfo.State != EHeadlessStatus.READY ||
                        headlessClientInfo.IsReconnecting ||
                        headlessClientInfo.HasNotifiedRequester != false ||
                        string.IsNullOrEmpty(headlessClientInfo.RequesterSessionID)) return;
                    requesterSessionId = headlessClientInfo.RequesterSessionID;
                    headlessClientInfo.State = EHeadlessStatus.IN_RAID;
                    headlessClientInfo.HasNotifiedRequester = true;
                }
            }

            await headlessRequesterWebSocket.SendAsync(requesterSessionId,
                new HeadlessRequesterJoinRaid(headlessClientId));
        }
        finally
        {
            headlessClientInfo.LifecycleGate.Release();
        }
    }

    /// <summary>
    /// 将玩家添加到指定的 headless 客户端 id
    /// </summary>
    /// <param name="headlessClientId">headless 客户端的 id</param>
    /// <param name="sessionID">玩家的 id</param>
    /// <exception cref="NullReferenceException"></exception>
    public void AddPlayerToHeadlessMatch(MongoId headlessClientId, MongoId sessionID)
    {
        if (HeadlessClients.TryGetValue(headlessClientId, out var headlessClientInfo))
        {
            if (headlessClientInfo == null)
            {
                throw new NullReferenceException($"AddPlayerToHeadlessMatch:: HeadlessClientInfo was null on {headlessClientId}");
            }

            string? requesterSessionId;
            lock (headlessClientInfo)
            {
                // 重连/中转可能将同一个 profile 注册多次。
                // 让大厅名单保持为集合，这样等级平均
                // 和 web UI 就不会把同一玩家计算两次。
                if (!headlessClientInfo.Players.Contains(sessionID))
                    headlessClientInfo.Players.Add(sessionID);
                requesterSessionId = headlessClientInfo.RequesterSessionID;
            }

            if (!fikaServerConfig.Headless.SetLevelToAverageOfLobby)
            {
                // 如果不设置平均等级，每次执行此操作并无必要，因此只在 headless 的最初请求方加入时设置一次。
                if (requesterSessionId == sessionID)
                {
                    CalculateAndSetHeadlessLevel(headlessClientId);
                }
            }
            else
            {
                CalculateAndSetHeadlessLevel(headlessClientId);
            }
        }
    }

    /// <summary>
    /// 当非 headless 玩家在战局结束前离开时，将其从当前主机名单中
    /// 移除。整份名单会在 EndHeadlessRaid 时清空，但在长时间战局或
    /// 中转期间保留已离开的玩家会破坏大厅
    /// 人数统计与平均等级计算。
    /// </summary>
    public void RemovePlayerFromHeadlessMatch(MongoId headlessClientId, MongoId sessionID)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var headlessClientInfo)) return;
        lock (headlessClientInfo)
        {
            headlessClientInfo.Players.Remove(sessionID);
        }
    }

    /// <summary>
    /// 将 headless 客户端设置为指定等级
    /// </summary>
    /// <remarks>主要用于中转</remarks>
    /// <param name="headlessClientId">客户端的 id</param>
    /// <exception cref="NullReferenceException"></exception>
    public void SetHeadlessLevel(MongoId headlessClientId)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var clientInfo))
        {
            throw new NullReferenceException($"Could not find headlessClientId '{headlessClientId}'");
        }

        var headlessProfile = saveServer.GetProfile(headlessClientId)
                ?? throw new NullReferenceException($"Could not find headlessProfile {headlessClientId}");

        // 中转开始可能与 socket 替换/房间拆除重叠。不要将
        // 旧房间的等级应用到复用了同一 headless profile id 的
        // 新预留上。
        lock (clientInfo)
        {
            if (!HeadlessClients.TryGetValue(headlessClientId, out var current) ||
                !ReferenceEquals(current, clientInfo) ||
                current.State is not EHeadlessStatus.IN_RAID)
                return;

            headlessProfile.CharacterData!.PmcData!.Info!.Level = clientInfo.Level;
        }
    }

    /// <summary>
    /// 计算 headless 客户端在此战局中的等级
    /// </summary>
    /// <param name="headlessClientId">客户端的 id</param>
    /// <exception cref="NullReferenceException"></exception>
    public void CalculateAndSetHeadlessLevel(MongoId headlessClientId)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var headlessClientInfo))
        {
            throw new NullReferenceException($"Could not find headlessClientId '{headlessClientId}'");
        }

        long reservationVersion;
        List<MongoId> players;
        lock (headlessClientInfo)
        {
            if (!HeadlessClients.TryGetValue(headlessClientId, out var current) ||
                !ReferenceEquals(current, headlessClientInfo) ||
                current.State is not EHeadlessStatus.IN_RAID)
                return;

            reservationVersion = headlessClientInfo.ReservationVersion;
            players = [.. headlessClientInfo.Players];
        }

        var headlessProfile = saveServer.GetProfile(headlessClientId)
            ?? throw new NullReferenceException($"Could not find headlessProfile {headlessClientId}");

        var baseHeadlessLevel = 0;
        var countedPlayers = 0; // 只统计有效的、非 headless 玩家

        foreach (var profileId in players)
        {
            var profile = saveServer.GetProfile(profileId);
            if (profile?.IsHeadlessProfile() != false)
            {
                continue;
            }

            baseHeadlessLevel += profile.CharacterData.PmcData.Info.Level ?? 1;
            countedPlayers++;
        }

        // 避免除以零
        if (countedPlayers > 0)
        {
            baseHeadlessLevel = Math.Max(1, baseHeadlessLevel / countedPlayers);
        }
        else
        {
            baseHeadlessLevel = 1;
        }

        if (logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Log(LogLevel.Debug,
                $"[{headlessClientId}] Settings headless level to: {baseHeadlessLevel} | Players: {countedPlayers}");
        }

        // profile 查找与等级平均的耗时可能足以让房间结束，
        // 并使同一 profile 被再次预留。发布之前需确认
        // 确切的客户端对象与预留代数。
        lock (headlessClientInfo)
        {
            if (!HeadlessClients.TryGetValue(headlessClientId, out var current) ||
                !ReferenceEquals(current, headlessClientInfo) ||
                current.ReservationVersion != reservationVersion ||
                current.State is not EHeadlessStatus.IN_RAID)
                return;

            headlessProfile.CharacterData.PmcData.Info.Level = baseHeadlessLevel;
            headlessClientInfo.Level = baseHeadlessLevel;
        }
    }

    public long? GetReservationVersion(string headlessClientId)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var headlessClientInfo)) return null;
        lock (headlessClientInfo) return headlessClientInfo.ReservationVersion;
    }

    /// <summary>
    /// 确认正在进行的房间创建在位置生成之后仍属于同一个
    /// headless 连接与请求方预留。
    /// </summary>
    public bool IsCurrentReservation(string headlessClientId, HeadlessClientInfo owner,
        long reservationVersion, string? reservationToken)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var current) ||
            !ReferenceEquals(current, owner)) return false;

        lock (owner)
        {
            return HeadlessClients.TryGetValue(headlessClientId, out current) &&
                ReferenceEquals(current, owner) &&
                owner.ReservationVersion == reservationVersion &&
                HeadlessReservationToken.Matches(owner.ReservationToken, reservationToken) &&
                !owner.IsReconnecting &&
                !string.IsNullOrEmpty(owner.RequesterSessionID) &&
                owner.WebSocket.State == WebSocketState.Open;
        }
    }

    /// <summary>
    /// 报告被拒绝的创建为何失败，同时不记录完整的 secret token。
    /// 仅应在 IsCurrentReservation 拒绝某个请求之后调用。
    /// </summary>
    public string DescribeReservationRejection(string headlessClientId, HeadlessClientInfo owner,
        long reservationVersion, string? reservationToken)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var current))
            return "host connection missing";
        if (!ReferenceEquals(current, owner))
            return "host connection replaced";

        lock (owner)
        {
            if (!HeadlessClients.TryGetValue(headlessClientId, out current) ||
                !ReferenceEquals(current, owner))
                return "host connection replaced";
            if (owner.ReservationVersion != reservationVersion)
                return $"generation changed (expected={reservationVersion}, current={owner.ReservationVersion})";
            if (!HeadlessReservationToken.Matches(owner.ReservationToken, reservationToken))
                return $"token mismatch (expected={TokenLabel(owner.ReservationToken)}, received={TokenLabel(reservationToken)})";
            if (owner.IsReconnecting)
                return "host reconnecting";
            if (string.IsNullOrEmpty(owner.RequesterSessionID))
                return "requester missing";
            if (owner.WebSocket.State != WebSocketState.Open)
                return $"host socket {owner.WebSocket.State}";
            return "reservation changed during validation";
        }
    }

    private static string TokenLabel(string? token) =>
        string.IsNullOrEmpty(token) ? "missing" : token[..Math.Min(token.Length, 6)];

    /// <summary>
    /// 只接受来自当前已注册 socket 以及刚刚结束的预留的就绪
    /// 上报。在 EndMatch 仍在收尾时发送的上报会排队至 Reset，
    /// 它绝不会让活跃房间对第二个
    /// 请求方可用。所有者闸门在校验上报期间阻止重连
    /// 替换。该路径从不获取
    /// MatchService 的 match gate，从而避免与 CreateMatch 形成反向的加锁顺序。
    /// </summary>
    public async Task MarkClientReadyAsync(string headlessClientId, WebSocket socket,
        string? reservationToken)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var client)) return;

        await client.LifecycleGate.WaitAsync();
        try
        {
            lock (client)
            {
                if (!HeadlessClients.TryGetValue(headlessClientId, out var current) ||
                    !ReferenceEquals(current, client) ||
                    !ReferenceEquals(client.WebSocket, socket) ||
                    client.IsReconnecting || socket.State != WebSocketState.Open)
                    return;

                client.TryMarkReady(reservationToken);
            }
        }
        finally
        {
            client.LifecycleGate.Release();
        }
    }

    /// <summary>
    /// 结束指定 headless 客户端的战局。该主机将保持
    /// 不可用，直到其对应的战局后就绪上报到达。
    /// </summary>
    public void EndHeadlessRaid(string headlessClientId, HeadlessClientInfo expectedClient,
        long? expectedReservationVersion)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var headlessClientInfo))
        {
            logger.LogWithColor($"EndHeadlessRaid:: Could not find '{headlessClientId}' to remove");
            return;
        }

        lock (headlessClientInfo)
        {
            // 在上一场战局的 webhook 仍在发送时，新的请求就可能预留
            // 该主机；重连也可能用新的版本计数器替换整个
            // 连接。较早的那次结束操作不得取消
            // 任何一个新的预留。
            if (!HeadlessClients.TryGetValue(headlessClientId, out var currentClient) ||
                !ReferenceEquals(currentClient, headlessClientInfo) ||
                !ReferenceEquals(expectedClient, headlessClientInfo) ||
                expectedReservationVersion != headlessClientInfo.ReservationVersion) return;
            headlessRequesterWebSocket.ClearPending(headlessClientInfo.RequesterSessionID);
            headlessClientInfo.Reset();
        }
    }
}
