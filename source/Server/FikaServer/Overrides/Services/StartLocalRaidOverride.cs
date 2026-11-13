using FikaServer.Services;
using FikaServer.Services.Headless;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Bot;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Services.Profile;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using System.Reflection;

namespace FikaServer.Overrides.Services;

[Injectable]
public sealed class StartLocalRaidOverride : AbstractPatch
{
    private static MatchService _matchService = default!;
    private static LocationLifecycleService _locationLifeCycleService = default!;
    private static HeadlessService _headlessService = default!;
    private static GlobalTable _globalTable = default!;
    private static TemplateTable _templateTable = default!;
    private static SeasonalEventConfig _seasonalEventConfig = default!;
    private static ProfileHelper _profileHelper = default!;
    private static TimeUtil _timeUtil = default!;
    private static ProfileActivityService _profileActivityService = default!;
    private static BotNameService _botNameService = default!;
    private static InsuranceService _insuranceService = default!;
    private static BackupService _backupService = default!;
    private static SaveServer _saveServer = default!;
    private static RagfairConfig _ragfairConfig = default!;
    private static HideoutConfig _hideoutConfig = default!;
    private static ISptLogger<StartLocalRaidOverride> _logger = default!;

    // SPT 的 ProfileProgressService 在早于引入 RemoveItemListBeforeRaidStart 的
    // 那些 SPT 构建中并不存在,因此它通过反射从注入的
    // LocationLifecycleService 上解析,而不是通过构造函数。
    private static bool _profileProgressResolved;
    private static bool _profileProgressUnavailableLogged;
    private static object? _profileProgressService;
    private static MethodInfo? _removeListedItemsMethod;


    public StartLocalRaidOverride(MatchService matchService, LocationLifecycleService locationLifecycleService,
        HeadlessService headlessService, GlobalTable globalTable, TemplateTable templateTable, SeasonalEventConfig seasonalEventConfig,
        ProfileHelper profileHelper, TimeUtil timeUtil, ProfileActivityService profileActivityService, BotNameService botNameService,
        InsuranceService insuranceService, BackupService backupService, SaveServer saveServer, RagfairConfig ragfairConfig,
        HideoutConfig hideoutConfig, ISptLogger<StartLocalRaidOverride> logger)
    {
        _matchService = matchService;
        _locationLifeCycleService = locationLifecycleService;
        _headlessService = headlessService;
        _globalTable = globalTable;
        _templateTable = templateTable;
        _seasonalEventConfig = seasonalEventConfig;
        _profileHelper = profileHelper;
        _timeUtil = timeUtil;
        _profileActivityService = profileActivityService;
        _botNameService = botNameService;
        _insuranceService = insuranceService;
        _backupService = backupService;
        _saveServer = saveServer;
        _ragfairConfig = ragfairConfig;
        _hideoutConfig = hideoutConfig;
        _logger = logger;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocationLifecycleService)
            .GetMethod(nameof(LocationLifecycleService.StartLocalRaidAsync))!;
    }

    [PatchPrefix]
    public static bool Prefix(MongoId sessionId, StartLocalRaidRequestData request, CancellationToken cancellationToken,
        ref Task<StartLocalRaidResponseData> __result)
    {
        __result = StartLocalRaidAsync(sessionId, request, cancellationToken);

        return false;
    }

    private static async Task<StartLocalRaidResponseData> StartLocalRaidAsync(MongoId sessionId, StartLocalRaidRequestData request,
        CancellationToken cancellationToken)
    {
        // 在触碰 raid 或 profile 状态之前,先保留原生 SPT 5 的 raid 启动备份。
        // 这样也能在对局启动被预留之前遵守取消请求,因此一次失败的备份
        // 不会把拆除屏障搁死在半路上。
        await _backupService.InitializeAsync(cancellationToken);

        LocationBase location;
        // 在做任何可能异步的 profile 或战利品工作之前,先预留当前对局。
        // EndMatch 会在同一把对局锁下关闭准入,
        // 并在最终确定保险之前等待这次预留,因此一次
        // 合法的进行中 StartLocalRaid 总能登记它的令牌。
        var hasStartReservation = _matchService.TryBeginLocalRaid(sessionId,
            out var reservedMatchId, out var reservedMatch);
        MongoId? matchId = hasStartReservation ? reservedMatchId : null;
        var raidToken = string.Empty;
        try
        {
            // 把令牌构造保持在预留作用域内,这样一次意外的
            // 请求/时间源失败仍会释放为本次 StartLocalRaid
            // 调用持有的准入。
            raidToken = $"{request.Location}.{request.PlayerSide}.{_timeUtil.GetTimeStamp()}.{Guid.NewGuid():N}";

            if (string.IsNullOrEmpty(matchId))
            {
                // 玩家不在 Fika 对局中,生成新的战利品
                location = _locationLifeCycleService.GenerateLocationAndLoot(sessionId, request!.Location!, !request.ShouldSkipLootGeneration ?? true);
            }
            else
            {
                // 预留会在本次请求组装响应期间保持对局存活。
                // 测试或关机路径仍有可能
                // 意外移除该对象;此时
                // 安全地回退到一次独立的 SPT raid。
                var match = reservedMatch ?? _matchService.GetMatch(matchId);
                if (match is null)
                {
                    matchId = null;
                    location = _locationLifeCycleService.GenerateLocationAndLoot(sessionId, request!.Location!, !request.ShouldSkipLootGeneration ?? true);
                    goto MatchResolved;
                }

                if (matchId == sessionId)
                {
                    // 因转发而再强制设置一次关卡
                    if (match.IsHeadless)
                    {
                        _headlessService.SetHeadlessLevel(sessionId);
                    }

                    lock (match)
                    {
                        match.Raids++;
                        if (match.Raids > 1)
                        {
                            match.LocationData = _locationLifeCycleService.GenerateLocationAndLoot(sessionId, request!.Location!, !request.ShouldSkipLootGeneration ?? true);
                        }
                    }
                }

                location = match.LocationData;
            }

    MatchResolved:

        var playerProfile = _profileHelper.GetFullProfile(sessionId);

        var isSide = (bool)typeof(LocationLifecycleService)
            .GetMethod("IsSide", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_locationLifeCycleService, [request.PlayerSide!, "pmc"])!;
        typeof(LocationLifecycleService)
            .GetMethod("ResetSkillPointsEarnedDuringRaid", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_locationLifeCycleService, [isSide
                ? playerProfile!.CharacterData!.PmcData!.Skills!.Common
                : playerProfile!.CharacterData!.ScavData!.Skills!.Common]);

        // 本 patch 完整替换了 StartLocalRaidAsync。保留 SPT 在结束时
        // 恢复的那些原始 raid 内维护间隔。
        _ragfairConfig.RunIntervalSeconds = _ragfairConfig.RunIntervalValues.InRaid;
        _hideoutConfig.RunIntervalSeconds = _hideoutConfig.RunIntervalValues.InRaid;

        // SPT 会在此时从 profile 中丢弃地点在 ProfileProgressOptions.RemoveItemListBeforeRaidStart
        // 里列出的物品,并在确有移除时保存 profile。由于 StartLocalRaidAsync 已被
        // 本 patch 完整替换,这里复现该步骤以保持相同的顺序。
        await RemoveListedProfileItemsAsync(sessionId, playerProfile!, request.Location!, cancellationToken);

        var transitionType = TransitionType.NONE;

        if (request.TransitionType is TransitionType flags)
        {
            if (flags.HasFlag(TransitionType.COMMON))
            {
                transitionType = TransitionType.COMMON;
            }

            if (flags.HasFlag(TransitionType.EVENT))
            {
                transitionType = TransitionType.EVENT;
            }
        }

        var isRundansActive = _globalTable.Configuration.RunddansSettings.Active;

        if (transitionType == TransitionType.EVENT)
        {
            // 处理 Runddans / Khorovod 事件
            if (isRundansActive && location.Transits is not null)
            {
                // 获取地图转发用的白名单,事件应只有一个
                var matchingTransitWhitelist = _seasonalEventConfig.KhorovodEventTransitWhitelist.GetValueOrDefault(
                    location.Id.ToLowerInvariant(),
                    []
                );

                foreach (var transit in location.Transits)
                {
                    // ActivateAfterSeconds 在生成器上设置计时器;events 是必需的,因为客户端还会再检查一次
                    // 以为 Khorovod 事件启用某些东西
                    if (matchingTransitWhitelist.Contains(transit.Id))
                    {
                        transit.ActivateAfterSeconds = 300;
                        transit.Events = true;
                    }
                    else
                    {
                        // 在本事件中禁用其他转发,玩家只被允许转发到特定地点
                        transit.IsActive = false;
                    }
                }
            }
        }

        StartLocalRaidResponseData result = new()
        {
            // 保持前两个以点分隔的字段与 SPT 的 EndLocalRaid 解析器
            // 兼容,同时让每场本地 raid
            // 即使在同一秒内启动也保持唯一。
            ServerId = raidToken,
            ServerSettings = _templateTable.LocationServices,
            Profile = new ProfileInsuredItems
            {
                InsuredItems = playerProfile!.CharacterData!.PmcData!.InsuredItems
            },
            LocationLoot = location,
            TransitionType = transitionType,
            Transition = new Transition
            {
                TransitionType = transitionType,
                TransitionRaidId = new MongoId(),
                TransitionCount = 0,
                VisitedLocations = []
            },
            ExcludedBosses = []
        };

        // 仅在从上一张地图转发进入时才有值
        if (request.Transition != null)
        {
            result.Transition = request.Transition;
        }

        // 取上一场 raid 结束时存下的数据(如果有)
        var raidActivity = _profileActivityService.GetProfileActivityRaidData(sessionId);
        var transitionData = raidActivity?.LocationTransit;

        if (transitionData != null)
        {
            result.Transition.TransitionType = transitionType;
            result.Transition.TransitionRaidId = transitionData.TransitionRaidId;
            result.Transition.TransitionCount += 1;

            if (result.Transition.VisitedLocations == null)
            {
                result.Transition.VisitedLocations = [transitionData!.SptLastVisitedLocation!];
            }
            else
            {
                result.Transition.VisitedLocations.Add(transitionData!.SptLastVisitedLocation!);
            }

            // 完成,清理
            raidActivity!.LocationTransit = null;
        }

        if (string.IsNullOrEmpty(matchId) || sessionId == matchId.Value)
        {
            // 把 pmcConfig 中的改动应用到机器人的敌对数值上
            typeof(LocationLifecycleService)
                .GetMethod("AdjustBotHostilitySettings", BindingFlags.NonPublic | BindingFlags.Instance)?
                .Invoke(_locationLifeCycleService, [result.LocationLoot]);
            typeof(LocationLifecycleService)
                .GetMethod("AdjustExtracts", BindingFlags.NonPublic | BindingFlags.Instance)?
                .Invoke(_locationLifeCycleService, [request.PlayerSide, request.Location, result.LocationLoot]);

            // 清空机器人缓存,为随后的机器人生成调用做好准备
            _botNameService.ClearNameCache();

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        }

        // 处理玩家库存清除检查,用于防止 alt-f4
        typeof(LocationLifecycleService)
            .GetMethod("HandlePreRaidInventoryChecks", BindingFlags.NonPublic | BindingFlags.Instance)?
            .Invoke(_locationLifeCycleService, [request.PlayerSide, playerProfile.CharacterData.PmcData, sessionId]);

            // 在所有响应与 profile 工作都成功之前不要公布令牌。
            // 上面抛出异常意味着客户端永远拿不到
            // 这个 ServerId,也就永远无法发送与之匹配的 EndLocalRaid。
            //
            // 缺少令牌不得让请求失败:match id 来自 SPT 自己的
            // 匹配器,因此每一场没有通过 /fika/raid/create 或 /fika/raid/join
            // 注册过的 raid——一次单纯的单人 raid,或一个加入请求
            // 从未到达服务器的对端——都没有 Fika 保险状态。
            // 在这里抛出曾让那些 raid 在客户端变成 "Empty response received",
            // 并彻底堵死开房。SPT 仍然能跑这场 raid;
            // 只是它的 Fika 保险快照不可用。
            if (matchId is { } activeMatchId &&
                !_insuranceService.AddRaidToken(activeMatchId, sessionId, result.ServerId))
            {
                _logger.Warning($"Raid {activeMatchId} has no Fika registration for session {sessionId}; " +
                    "starting the raid without a Fika insurance token");
            }

            return result;
        }
        finally
        {
            if (hasStartReservation)
            {
                _matchService.EndLocalRaidStart(reservedMatchId, reservedMatch!);
            }
        }
    }

    /// <summary>
    /// SPT 会在 LocationLifecycleService.StartLocalRaidAsync 内部从 profile 中移除
    /// 地点在 ProfileProgressOptions.RemoveItemListBeforeRaidStart 里列出的物品,并在确有
    /// 移除时保存 profile。本 patch 完整替换了那个方法,因此这里在 raid 启动的
    /// 等价位置复现该步骤。
    /// 该服务从注入的 LocationLifecycleService 上解析,而不是通过构造函数,
    /// 以便早于 ProfileProgressService 的 SPT 构建仍能加载;若无法解析,
    /// 该步骤会在打印一条警告后被跳过,而 raid 启动不受影响继续。
    /// </summary>
    private static async Task RemoveListedProfileItemsAsync(MongoId sessionId, SptProfile playerProfile, string locationName,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_profileProgressResolved)
            {
                _profileProgressResolved = true;

                // 对 LocationLifecycleService 的 primary-constructor 捕获;按类型名匹配,
                // 因此即使编译器生成的字段被改名仍能找到,而类型缺失时保持为 null。
                var serviceField = typeof(LocationLifecycleService)
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(field => field.FieldType.Name == "ProfileProgressService");

                _profileProgressService = serviceField?.GetValue(_locationLifeCycleService);
                _removeListedItemsMethod = serviceField?.FieldType.GetMethod("RemoveListedItems",
                    BindingFlags.Instance | BindingFlags.Public, null, [typeof(SptProfile), typeof(string)], null);
            }

            if (_profileProgressService is null || _removeListedItemsMethod is null)
            {
                if (!_profileProgressUnavailableLogged)
                {
                    _profileProgressUnavailableLogged = true;
                    _logger.Warning("[ProfileProgress] ProfileProgressService is not available on this SPT build; " +
                        "items listed in a location's RemoveItemListBeforeRaidStart are not removed before a raid");
                }

                _logger.Info($"[ProfileProgress] Nothing removed for {sessionId} before {locationName}: " +
                    "this SPT build has no ProfileProgressService");

                return;
            }

            if (!(bool)_removeListedItemsMethod.Invoke(_profileProgressService, [playerProfile, locationName])!)
            {
                _logger.Info($"[ProfileProgress] Nothing removed for {sessionId} before {locationName}: " +
                    "no profile item is listed in RemoveItemListBeforeRaidStart");

                return;
            }

            await _saveServer.SaveProfileAsync(sessionId, cancellationToken);

            _logger.Info($"[ProfileProgress] Removed the items listed in RemoveItemListBeforeRaidStart for {locationName} " +
                $"from the profile of {sessionId} and saved it");
        }
        catch (OperationCanceledException)
        {
            // 取消操作仍会抛出;只有兼容性/运行时失败会被吸收。
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning($"[ProfileProgress] Could not remove the items listed in RemoveItemListBeforeRaidStart for " +
                $"{locationName} from the profile of {sessionId}, continuing raid start: {ex.Message}");
        }
    }
}
