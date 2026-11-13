using FikaServer.Models.Fika.Insurance;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Servers;
using System.Collections.Concurrent;

namespace FikaServer.Services;

[Injectable(InjectionType.Singleton)]
public class InsuranceService(SaveServer saveServer, ItemHelper itemHelper, ISptLogger<InsuranceService> logger)
{
    private sealed class MatchInsuranceState
    {
        public Dictionary<MongoId, FikaInsurancePlayer> Players { get; } = [];
        public bool IsEnding { get; set; }
        public int PendingRaidEnds { get; set; }
        public TaskCompletionSource<bool>? RaidEndsCompleted { get; set; }
    }

    private readonly ConcurrentDictionary<MongoId, MatchInsuranceState> _matchInsuranceInfo = [];

    /// <summary>
    /// 获取该玩家所在的战局
    /// </summary>
    /// <param name="sessionID">要查找的 profile id</param>
    /// <returns>战局 id；若未找到则为 <see langword="null"/></returns>
    public MongoId? GetMatchId(MongoId sessionID)
        => GetMatchId(sessionID, null);

    /// <summary>
    /// 获取持有某个原生战局结束回调的保险快照。
    ///
    /// 某个 profile 可能同时存在于正在结束的战局和新加入的
    /// 战局中。当 SPT 发送本地战局的 ServerId 时，
    /// 匹配该 token 是避免把旧回调分配给新战局的
    /// 唯一可靠方式。
    /// </summary>
    /// <param name="sessionID">要查找的 profile id。</param>
    /// <param name="raidToken">StartLocalRaid 返回的 ServerId。</param>
    /// <returns>战局 id；若未找到则为 <see langword="null"/>。</returns>
    public MongoId? GetMatchId(MongoId sessionID, string? raidToken)
    {
        var hasToken = !string.IsNullOrWhiteSpace(raidToken);
        MongoId? endingMatch = null;
        foreach ((var matchId, var state) in _matchInsuranceInfo)
        {
            lock (state)
            {
                // EndMatchCore 会在原生 EndLocalRaidAsync 必然到达
                // 之前就关闭准入。在其收尾期间仍应持续返回
                // 保留的战局，好让迟到的请求仍能
                // 记录最终库存。
                if (!state.Players.ContainsKey(sessionID))
                    continue;

                if (hasToken)
                {
                    if (!state.Players[sessionID].RaidTokens.Contains(raidToken!))
                        continue;

                    // token 由 StartLocalRaid 唯一生成。一旦找到确切的
                    // 所有者，就不要再继续搜索。
                    return matchId;
                }

                // 旧状态仍在排空时，某个 profile 可能已经加入了下一场
                // 战局。优先返回实时状态，同时
                // 保留正在结束的状态，作为上一场战局迟到的原生
                // EndLocalRaid 请求的兜底。
                if (!state.IsEnding)
                    return matchId;

                endingMatch ??= matchId;
            }
        }

        return endingMatch;
    }

    /// <summary>
    /// 在实时 Fika 名单已移除某玩家之后，读取保留的战局参与者
    /// 元数据。原生战局结束请求可能在 /fika/raid/leave 之后
    /// 到达，因此需要这些数据。
    /// </summary>
    public bool IsSpectator(MongoId matchId, MongoId sessionID)
    {
        if (!_matchInsuranceInfo.TryGetValue(matchId, out var state)) return false;

        lock (state)
        {
            return state.Players.TryGetValue(sessionID, out var player) && player.IsSpectator;
        }
    }

    /// <summary>
    /// 将玩家加入某个战局
    /// </summary>
    /// <param name="matchId">战局的 id</param>
    /// <param name="sessionID">要添加的 profile id</param>
    public bool AddPlayerToMatchId(MongoId matchId, MongoId sessionID, bool isSpectator = false)
    {
        // 重连可能在名单条目已被移除、而保险快照被有意
        // 保留以供迟到的 EndLocalRaid 请求使用时到达。
        // 应把这次注册视为幂等的成功，然后再读取
        // profile；此时战局正在拆除，
        // profile 可能已在卸载。
        if (_matchInsuranceInfo.TryGetValue(matchId, out var existingState))
        {
            lock (existingState)
            {
                if (existingState.IsEnding) return false;
                if (existingState.Players.ContainsKey(sessionID)) return true;
            }
        }

        var profile = saveServer.GetProfile(sessionID)
            ?? throw new NullReferenceException("[Fika Insurance] Profile was null");

        var pmcData = profile.CharacterData?.PmcData;
        var equipmentId = pmcData!.Inventory?.Equipment!;
        var equipment = pmcData!.Inventory!.Items!
            .Where(i => i.ParentId! == equipmentId || pmcData.DoesItemHaveRootId(i, equipmentId.Value))
            .Select(i => i.Id)
            .ToArray();
        var insuredItems = pmcData.InsuredItems!
            .Where(i => i.ItemId.HasValue && equipment.Contains(i.ItemId.Value))
            .Select(i => i.ItemId!.Value)
            .ToArray();

        logger.Debug($"[Fika Insurance] {sessionID} brought {insuredItems.Length} items into the raid");

        FikaInsurancePlayer player = new()
        {
            SessionID = sessionID,
            IsSpectator = isSpectator,
            EndedRaid = false,
            InsuredItemsBroughtToRaid = insuredItems
        };

        var state = _matchInsuranceInfo.GetOrAdd(matchId, _ => new MatchInsuranceState());
        lock (state)
        {
            // 重连可能再次注册同一个 profile。保留
            // 首次进入战局时的库存，以及该玩家已记录的
            // 任何战局后结果。
            if (state.IsEnding) return false;
            // 第二次检查关闭了上述快速路径与 profile 快照
            // 构建之间的竞态。
            if (state.Players.ContainsKey(sessionID)) return true;
            state.Players.Add(sessionID, player);
            return true;
        }
    }

    /// <summary>
    /// 将某个 StartLocalRaid 响应与参与者保留的
    /// 保险快照关联起来。EndLocalRaidRequestData.ServerId 是原生 SPT 端点
    /// 回传的唯一战局标识，因此必须在客户端结束
    /// 战局之前建立该关联。
    /// </summary>
    public bool AddRaidToken(MongoId matchId, MongoId sessionID, string? raidToken)
    {
        if (string.IsNullOrWhiteSpace(raidToken) ||
            !_matchInsuranceInfo.TryGetValue(matchId, out var state))
            return false;

        lock (state)
        {
            // EndMatchCore 在等待原生战局结束回调之前，
            // 会先关闭新参与者的准入。已经在飞行中的
            // StartLocalRaid 请求可能在该闸门关闭后才完成；
            // 其已有的参与者仍需要注册该 token，以便后续
            // 的 EndLocalRaid 回调能匹配到这个快照。
            // AddPlayerToMatchId 仍是新玩家的准入门。
            // 若当前已知的所有战局结束都已结算完毕，则
            // 不再有等待者能观察到新加入的 token。此时应拒绝
            // 这个迟到的响应，而不是让它在最终保险快照
            // 之后被复制，或附加到已移除的状态上。
            if (state.IsEnding &&
                (state.RaidEndsCompleted is null || state.RaidEndsCompleted.Task.IsCompleted))
            {
                return false;
            }

            if (!state.Players.TryGetValue(sessionID, out var player))
                return false;

            if (player.RaidTokens.Add(raidToken))
            {
                player.RaidTokenOrder[raidToken] = player.NextRaidTokenSequence++;
            }
            return true;
        }
    }

    public void RemovePlayerFromMatchId(MongoId matchId, MongoId sessionID)
    {
        if (!_matchInsuranceInfo.TryGetValue(matchId, out var state)) return;
        lock (state)
        {
            if (!state.IsEnding) state.Players.Remove(sessionID);
        }
    }

    /// <summary>
    /// 在客户端请求结束战局时执行
    /// </summary>
    /// <param name="sessionID">请求结束的 profile id</param>
    /// <param name="matchId">战局的 id</param>
    /// <param name="endLocalRaidRequest">请求数据</param>
    public void OnEndLocalRaidRequest(MongoId sessionID, MongoId? matchId, EndLocalRaidRequestData endLocalRaidRequest)
    {
        if (!matchId.HasValue)
        {
            logger.Error($"{sessionID} ended their raid but the match could not be found");
            return;
        }

        if (!_matchInsuranceInfo.TryGetValue(matchId.Value, out var state))
        {
            logger.Error("[Fika Insurance] onEndLocalRaidRequest: matchId not found!");
            return;
        }

        CompleteRaidEnd(state, sessionID, endLocalRaidRequest.ServerId, endLocalRaidRequest);
    }

    public bool BeginRaidEnd(MongoId sessionID, MongoId? matchId)
        => TryBeginRaidEnd(sessionID, matchId, null, out _);

    /// <summary>
    /// 为某个本地战局预留原生结束战局回调。
    /// 一个 Fika 战局下可能因中转产生多个本地战局，因此
    /// 已尝试/待处理状态以 StartLocalRaid 的 ServerId 为键。
    /// </summary>
    public bool TryBeginRaidEnd(MongoId sessionID, MongoId? matchId, string? raidToken,
        out string? reservationToken)
    {
        reservationToken = string.IsNullOrWhiteSpace(raidToken) ? null : raidToken;
        if (!matchId.HasValue || !_matchInsuranceInfo.TryGetValue(matchId.Value, out var state))
            return false;

        lock (state)
        {
            if (!state.Players.TryGetValue(sessionID, out var player) || player.IsSpectator)
                return false;

            if (reservationToken is not null)
            {
                // 该 token 必须由本服务对应的 StartLocalRaid 响应
                // 签发过。存在显式 token 时，绝不退回到仅按
                // profile 的预留方式。
                if (!player.RaidTokens.Contains(reservationToken) ||
                    !player.RaidEndAttemptedTokens.Add(reservationToken))
                    return false;

                player.RaidEndPendingTokens.Add(reservationToken);
            }
            else
            {
                // 为未发送 ServerId 的原生客户端保留的兼容路径。
                // 它被刻意设计为一次性使用，因为请求侧
                // 没有可用于区分中转战局的标识。
                if (player.RaidEndAttempted)
                    return false;

                player.RaidEndAttempted = true;
                player.RaidEndPending = true;
            }

            state.PendingRaidEnds++;
            return true;
        }
    }

    public void CompleteRaidEnd(MongoId sessionID, MongoId? matchId, EndLocalRaidRequestData request)
        => CompleteRaidEnd(sessionID, matchId, null, request);

    public void CompleteRaidEnd(MongoId sessionID, MongoId? matchId, string? raidToken,
        EndLocalRaidRequestData request)
    {
        if (!matchId.HasValue || !_matchInsuranceInfo.TryGetValue(matchId.Value, out var state))
            return;
        CompleteRaidEnd(state, sessionID, raidToken, request);
    }

    /// <summary>
    /// 当原生 SPT 请求在产出结果之前被取消或出错时，
    /// 释放对应的待处理战局结束槽位。
    /// </summary>
    public void AbortRaidEnd(MongoId sessionID, MongoId? matchId)
        => AbortRaidEnd(sessionID, matchId, null);

    public void AbortRaidEnd(MongoId sessionID, MongoId? matchId, string? raidToken)
    {
        if (!matchId.HasValue || !_matchInsuranceInfo.TryGetValue(matchId.Value, out var state))
            return;

        lock (state)
        {
            // 原生任务出错时，离开回调可能已将玩家从
            // 保险名单中移除。待处理计数器才是该槽位
            // 权威的归属标记。
            if (state.Players.TryGetValue(sessionID, out var player))
            {
                var pendingRemoved = false;
                if (!string.IsNullOrWhiteSpace(raidToken))
                {
                    if (!player.RaidTokens.Contains(raidToken))
                    {
                        logger.Warning($"[Fika Insurance] Ignoring raid-end callback for unknown raid token {raidToken} ({sessionID})");
                        return;
                    }

                    player.RaidEndAttemptedTokens.Add(raidToken);
                    pendingRemoved = player.RaidEndPendingTokens.Remove(raidToken);
                }
                else
                {
                    player.RaidEndAttempted = true;
                    pendingRemoved = player.RaidEndPending;
                    player.RaidEndPending = false;
                }

                if (pendingRemoved && state.PendingRaidEnds > 0)
                {
                    state.PendingRaidEnds--;
                }
            }
            SignalIfAllRaidEndsAttempted(state);
        }

        logger.Warning($"[Fika Insurance] Raid-end request for {sessionID} was cancelled or faulted; released pending settlement slot");
    }

    private void CompleteRaidEnd(MatchInsuranceState state, MongoId sessionID, string? raidToken,
        EndLocalRaidRequestData request)
    {
        var inventoryAfterRaid = request.Results?.Profile?.Inventory?.Items?
            .Select(i => i.Id)
            .ToArray();
        lock (state)
        {
            if (state.Players.TryGetValue(sessionID, out var player))
            {
                var pendingRemoved = false;
                if (!string.IsNullOrWhiteSpace(raidToken))
                {
                    if (!player.RaidTokens.Contains(raidToken))
                    {
                        logger.Warning($"[Fika Insurance] Ignoring raid-end completion for unknown raid token {raidToken} ({sessionID})");
                        return;
                    }

                    player.RaidEndAttemptedTokens.Add(raidToken);
                    pendingRemoved = player.RaidEndPendingTokens.Remove(raidToken);
                }
                else
                {
                    player.RaidEndAttempted = true;
                    pendingRemoved = player.RaidEndPending;
                    player.RaidEndPending = false;
                }

                if (pendingRemoved && state.PendingRaidEnds > 0)
                {
                    state.PendingRaidEnds--;
                }

                // 把库存与完成标志放在同一个临界区内，
                // 使战局结束流程无法观察到写了一半的结果。
                // 中转回调可能乱序完成；token 序列
                // 可防止较旧的映射替换最新的
                // 战局后库存。
                // 当没有注册任何 token 时，不带 ServerId 的旧式回调
                // 仍然有用，但一旦收到带 token 的回调，
                // 就不得覆盖中转库存。
                var sequence = player.RaidTokens.Count > 0 ? -1 : long.MaxValue;
                if (!string.IsNullOrWhiteSpace(raidToken) &&
                    player.RaidTokenOrder.TryGetValue(raidToken, out var tokenSequence))
                {
                    sequence = tokenSequence;
                }

                if (inventoryAfterRaid != null && sequence >= player.LastCompletedRaidSequence)
                {
                    player.InventoryAfterRaid = inventoryAfterRaid;
                    player.LastCompletedRaidSequence = sequence;
                    player.EndedRaid = true;
                }
            }

            if (inventoryAfterRaid == null)
                logger.Warning($"[Fika Insurance] Ignoring incomplete raid-end inventory for {sessionID}");

            SignalIfAllRaidEndsAttempted(state);
        }

        if (inventoryAfterRaid != null)
            logger.Debug($"[Fika Insurance] {sessionID} brought {inventoryAfterRaid.Length} items out from the raid");
    }

    private static bool PlayerRaidEndsSettled(FikaInsurancePlayer player)
    {
        if (player.IsSpectator)
            return true;

        if (player.RaidTokens.Count > 0)
        {
            return player.RaidEndPendingTokens.Count == 0 &&
                player.RaidEndAttemptedTokens.IsSupersetOf(player.RaidTokens);
        }

        return player.RaidEndPending == false && player.RaidEndAttempted;
    }

    private static bool AllRaidEndsSettled(MatchInsuranceState state) =>
        state.PendingRaidEnds == 0 && state.Players.Values.All(PlayerRaidEndsSettled);

    private static void SignalIfAllRaidEndsAttempted(MatchInsuranceState state)
    {
        if (AllRaidEndsSettled(state))
            state.RaidEndsCompleted?.TrySetResult(true);
    }

    /// <summary>
    /// 在战局结束时执行
    /// </summary>
    /// <param name="matchId">战局的 id</param>
    public async Task OnMatchEndAsync(MongoId matchId)
    {
        if (!_matchInsuranceInfo.TryGetValue(matchId, out var state))
        {
            logger.Error($"[Fika Insurance] Could not find match with ID {matchId}");
            return;
        }

        FikaInsurancePlayer[] players;
        Task? pendingRaidEnds = null;
        lock (state)
        {
            if (state.IsEnding) return;
            state.IsEnding = true;
            // EndLocalRaidAsync 可能在 /fika/raid/leave 之后、且主机已开始
            // 结束战局之后才到达。应等待所有保留的非观察者
            // 参与者走到原生请求这一步，无论该请求最终
            // 成功还是出错，而不是只等待那些恰好在此刻
            // 正处于飞行中的请求。
            if (!AllRaidEndsSettled(state))
            {
                state.RaidEndsCompleted ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingRaidEnds = state.RaidEndsCompleted.Task;
            }
        }

        if (pendingRaidEnds != null)
        {
            var completed = await Task.WhenAny(pendingRaidEnds, Task.Delay(TimeSpan.FromSeconds(30)));
            if (completed != pendingRaidEnds)
                logger.Warning($"[Fika Insurance] Timed out waiting for raid-end profile saves for {matchId}");
        }

        lock (state)
        {
            players = state.Players.Values
                .Select(player => new FikaInsurancePlayer
                {
                    SessionID = player.SessionID,
                    IsSpectator = player.IsSpectator,
                    RaidTokens = new HashSet<string>(player.RaidTokens, StringComparer.Ordinal),
                    RaidTokenOrder = new Dictionary<string, long>(player.RaidTokenOrder, StringComparer.Ordinal),
                    RaidEndAttemptedTokens = new HashSet<string>(player.RaidEndAttemptedTokens, StringComparer.Ordinal),
                    RaidEndPendingTokens = new HashSet<string>(player.RaidEndPendingTokens, StringComparer.Ordinal),
                    NextRaidTokenSequence = player.NextRaidTokenSequence,
                    LastCompletedRaidSequence = player.LastCompletedRaidSequence,
                    RaidEndAttempted = player.RaidEndAttempted,
                    RaidEndPending = player.RaidEndPending,
                    EndedRaid = player.EndedRaid,
                    InsuredItemsBroughtToRaid = player.InsuredItemsBroughtToRaid,
                    InventoryAfterRaid = player.InventoryAfterRaid
                })
                .ToArray();
        }

        try
        {
            logger.Debug($"[Fika Insurance] Iterating over {players.Length} players");

            var postRaidInventoryMap = new Dictionary<MongoId, MongoId[]>();
            var profilesWithChangedInsurance = new HashSet<MongoId>();
            foreach (var player in players)
            {
                if (!player.EndedRaid || player.InventoryAfterRaid == null)
                    continue;

                if (!postRaidInventoryMap.TryAdd(player.SessionID, player.InventoryAfterRaid))
                    logger.Error($"OnMatchEnd: Duplicate SessionID detected: {player.SessionID}.");
            }

            foreach (var player in players)
            {
                if (!player.EndedRaid || player.InsuredItemsBroughtToRaid == null)
                    continue;

                var otherPlayersPostRaidInventories = new HashSet<MongoId>(postRaidInventoryMap
                    .Where(kvp => kvp.Key != player.SessionID)
                    .SelectMany(kvp => kvp.Value));

                var lootedItemsForThisPlayer = player.InsuredItemsBroughtToRaid
                    .Where(otherPlayersPostRaidInventories.Contains)
                    .ToArray();

                if (lootedItemsForThisPlayer.Length > 0)
                {
                    try
                    {
                        logger.Debug($"{player.SessionID} will lose {lootedItemsForThisPlayer.Length} items in insurance");
                        if (RemoveItemsFromInsurance(player.SessionID, lootedItemsForThisPlayer))
                            profilesWithChangedInsurance.Add(player.SessionID);
                    }
                    catch (Exception exception)
                    {
                        logger.Error($"[Fika Insurance] Failed to settle insurance for {player.SessionID}: {exception.Message}", exception);
                    }
                }
            }

            // SPT 会在 Fika 关闭战局之前保存战局 profile。此处产生的
            // 保险变更需要持久化，以便在服务器重启后仍然存在。
            foreach (var sessionID in profilesWithChangedInsurance)
            {
                try
                {
                    await saveServer.SaveProfileAsync(sessionID);
                }
                catch (Exception exception)
                {
                    logger.Error($"[Fika Insurance] Failed to persist insurance changes for {sessionID}: {exception.Message}", exception);
                }
            }
        }
        finally
        {
            _matchInsuranceInfo.TryRemove(new KeyValuePair<MongoId, MatchInsuranceState>(matchId, state));
        }
    }

    /// <summary>
    /// 从 profile 的 <see cref="Insurance"/> 中移除所有给定的 itemIds
    /// </summary>
    /// <param name="sessionID">要从中移除的 profile</param>
    /// <param name="ids">要查找并移除的物品 id</param>
    /// <exception cref="NullReferenceException"></exception>
    private bool RemoveItemsFromInsurance(MongoId sessionID, MongoId[] ids)
    {
        var profile = saveServer.GetProfile(sessionID)
            ?? throw new NullReferenceException("[Fika Insurance] Profile was null");

        var changed = false;
        List<Item> toRemove = [];
        for (var i = 0; i < profile.InsuranceList?.Count; i++)
        {
            var insurance = profile.InsuranceList[i];
            if (insurance.Items == null)
            {
                continue;
            }

            foreach (var itemId in ids)
            {
                var item = insurance.Items!
                    .FirstOrDefault(x => x.Id == itemId);

                if (item is null)
                {
                    continue;
                }

                // 从护甲和头盔中移除软插板
                if (itemHelper.IsOfBaseclasses(item.Template, [BaseClasses.ARMOR, BaseClasses.HEADWEAR]))
                {
                    logger.Debug($"[Fika Insurance] {itemId} is armor or helmet");
                    var children = insurance.Items!
                        .Where(x => x.ParentId! == itemId && itemHelper.IsOfBaseclass(x.Template, BaseClasses.BUILT_IN_INSERTS));

                    foreach (var childItem in children)
                    {
                        // 软插板不受保险
                        if (!ids.Contains(childItem.Id))
                        {
                            toRemove.Add(childItem);
                        }
                    }
                }

                // 移除该物品的子项
                foreach (var itemToRemove in toRemove)
                {
                    if (!insurance.Items!.Remove(itemToRemove))
                    {
                        logger.Debug($"[Fika Insurance] Unable to remove the item {itemToRemove.Id}");
                    }
                    else
                    {
                        changed = true;
                    }
                }

                // 为下一次迭代清空列表
                toRemove.Clear();

                // 移除原始物品（父项）
                changed |= insurance.Items!.Remove(item);
            }

            // 若所有受保物品都已消失，则移除该条目
            if (insurance.Items?.Count == 0)
            {
                profile.InsuranceList.RemoveAt(i--);
                changed = true;
            }
        }

        return changed;
    }
}
