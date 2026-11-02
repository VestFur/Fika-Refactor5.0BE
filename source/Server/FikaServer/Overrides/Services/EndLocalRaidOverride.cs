using FikaServer.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Services.InRaid;
using System.Reflection;
using InsuranceService = FikaServer.Services.InsuranceService;

namespace FikaServer.Overrides.Services;

[Injectable]
public sealed class EndLocalRaidOverride : AbstractPatch
{
    private sealed class EndRaidState
    {
        public MongoId MatchId { get; init; }
        public bool Track { get; init; }
        public string? RaidToken { get; init; }
    }

    private static MatchService _matchService = default!;
    private static InsuranceService _insuranceService = default!;

    public EndLocalRaidOverride(MatchService matchService, InsuranceService insuranceService)
    {
        _matchService = matchService;
        _insuranceService = insuranceService;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocationLifecycleService)
            .GetMethod(nameof(LocationLifecycleService.EndLocalRaidAsync))!;
    }

    [PatchPrefix]
    public static bool Prefix(MongoId sessionId, EndLocalRaidRequestData request, ref Task __result,
        out object? __state)
    {
        __state = null;

        // 原始的 SPT 处理器必须先行完成：它会把投保物品的
        // 损失应用到 InsuranceList 并持久化 raid 档案。在 prefix 中
        // 记录库存，可能让 Fika 在 SPT 尚未把这些丢失物品加入之前
        // 就结算保险。
        // /fika/raid/leave 会在 SPT 收到原生 raid 结束请求之前，把玩家从
        // 在线比赛名单中移除。InsuranceService 会刻意保留该条目，
        // 以便该请求仍能携带最终的
        // 库存快照。当名单查找已经返回空时，
        // 就使用这份保留下来的状态。
        // EndLocalRaid 携带由 StartLocalRaid 返回的 ServerId。在一场比赛
        // 仍在收尾时，同一份档案可能已经出现在新的比赛中，因此仅按 profile
        // 查找可能把这个迟到的回调挂到错误的保险快照上。应优先使用精确的
        // raid token，只有当中途的原生请求没有提供它时，
        // 才使用旧的 profile 查找
        // 方式。
        var raidToken = request?.ServerId;
        var matchId = _insuranceService.GetMatchId(sessionId, raidToken);
        if (!matchId.HasValue && string.IsNullOrWhiteSpace(raidToken))
        {
            matchId = _matchService.GetMatchIdByProfile(sessionId)
                ?? _insuranceService.GetMatchId(sessionId);
        }
        if (matchId == null)
        {
            return true;
        }

        // 查找离开该 raid 的玩家
        var player = _matchService.GetPlayerInMatch(matchId.Value, sessionId);

        // 玩家可能在 SPT 处理 raid 结束请求之前就离开 Fika 比赛。
        // 只有已确认的观察者才应绕过档案保存。
        // 当在线名单已经不存在时，保险比赛记录就是
        // 权威的非观察者标记，仍必须预留一个待处理的
        // 结算槽位。
        if (player == null)
        {
            if (_insuranceService.IsSpectator(matchId.Value, sessionId))
            {
                // 即使在原生回调到达之前名单条目已被移除，
                // 也要保留原有的 Fika 观察者绕过逻辑。
                __result = Task.CompletedTask;
                return false;
            }

            var track = _insuranceService.TryBeginRaidEnd(sessionId, matchId, raidToken, out var reservationToken);
            __state = new EndRaidState
            {
                MatchId = matchId.Value,
                Track = track,
                RaidToken = reservationToken
            };
            return true;
        }

        if (!player.IsSpectator)
        {
            var track = _insuranceService.TryBeginRaidEnd(sessionId, matchId, raidToken, out var reservationToken);
            __state = new EndRaidState
            {
                MatchId = matchId.Value,
                Track = track,
                RaidToken = reservationToken
            };
            return true;
        }

        // 观察者没有可保存的 raid 档案。
        __result = Task.CompletedTask;

        return false;
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionId, EndLocalRaidRequestData request, Task __result,
        object? __state)
    {
        if (__state is not EndRaidState state || !state.Track) return;
        if (__result == null)
        {
            _insuranceService.AbortRaidEnd(sessionId, state.MatchId, state.RaidToken);
            return;
        }

        _ = __result.ContinueWith(task =>
        {
            if (task.IsCanceled || task.IsFaulted)
            {
                _insuranceService.AbortRaidEnd(sessionId, state.MatchId, state.RaidToken);
                return;
            }

            // leave 回调可能在 SPT 请求仍在运行时移除了名单条目。
            // 使用在异步请求开始之前捕获的那场比赛；此时
            // 按 profile 查找可能已经为空，
            // 那样就会丢失库存快照。
            _insuranceService.CompleteRaidEnd(sessionId, state.MatchId, state.RaidToken, request);
        }, TaskScheduler.Default);
    }
}
