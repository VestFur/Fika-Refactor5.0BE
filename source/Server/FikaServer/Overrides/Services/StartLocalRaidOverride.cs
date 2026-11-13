using FikaServer.Services;
using FikaServer.Services.Headless;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Bot;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Services.Profile;
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
    private static RagfairConfig _ragfairConfig = default!;
    private static HideoutConfig _hideoutConfig = default!;
    private static ISptLogger<StartLocalRaidOverride> _logger = default!;


    public StartLocalRaidOverride(MatchService matchService, LocationLifecycleService locationLifecycleService,
        HeadlessService headlessService, GlobalTable globalTable, TemplateTable templateTable, SeasonalEventConfig seasonalEventConfig,
        ProfileHelper profileHelper, TimeUtil timeUtil, ProfileActivityService profileActivityService, BotNameService botNameService,
        InsuranceService insuranceService, BackupService backupService, RagfairConfig ragfairConfig,
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
        // Preserve the native SPT 5 raid-start backup before touching raid or
        // profile state. This also honors cancellation before a match start is
        // reserved, so a failed backup cannot strand the teardown barrier.
        await _backupService.InitializeAsync(cancellationToken);

        LocationBase location;
        // Reserve the live match before doing any potentially-async profile
        // or loot work. EndMatch closes admission under the same match lock
        // and waits for this reservation before finalizing insurance, so a
        // legal in-flight StartLocalRaid can always register its token.
        var hasStartReservation = _matchService.TryBeginLocalRaid(sessionId,
            out var reservedMatchId, out var reservedMatch);
        MongoId? matchId = hasStartReservation ? reservedMatchId : null;
        var raidToken = string.Empty;
        try
        {
            // Keep token construction inside the reservation scope so an
            // unexpected request/time source failure still releases the
            // admission held for this StartLocalRaid call.
            raidToken = $"{request.Location}.{request.PlayerSide}.{_timeUtil.GetTimeStamp()}.{Guid.NewGuid():N}";

            if (string.IsNullOrEmpty(matchId))
            {
                // player isn't in a Fika match, generate new loot
                location = _locationLifeCycleService.GenerateLocationAndLoot(sessionId, request!.Location!, !request.ShouldSkipLootGeneration ?? true);
            }
            else
            {
                // The reservation keeps the match alive while this request
                // assembles its response. It is still possible for a test or
                // shutdown path to remove the object unexpectedly; in that
                // case safely fall back to a standalone SPT raid.
                var match = reservedMatch ?? _matchService.GetMatch(matchId);
                if (match is null)
                {
                    matchId = null;
                    location = _locationLifeCycleService.GenerateLocationAndLoot(sessionId, request!.Location!, !request.ShouldSkipLootGeneration ?? true);
                    goto MatchResolved;
                }

                if (matchId == sessionId)
                {
                    // force another level set due to transits
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

        // StartLocalRaidAsync is replaced in full by this patch. Keep the
        // original in-raid maintenance intervals that SPT restores on end.
        _ragfairConfig.RunIntervalSeconds = _ragfairConfig.RunIntervalValues.InRaid;
        _hideoutConfig.RunIntervalSeconds = _hideoutConfig.RunIntervalValues.InRaid;

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
            // Handle Runddans / Khorovod event
            if (isRundansActive && location.Transits is not null)
            {
                // Get whitelist for maps transits, event should have 1 only
                var matchingTransitWhitelist = _seasonalEventConfig.KhorovodEventTransitWhitelist.GetValueOrDefault(
                    location.Id.ToLowerInvariant(),
                    []
                );

                foreach (var transit in location.Transits)
                {
                    // ActivateAfterSeconds sets the timer on the generator, events is needed because it is checked again in the client
                    // To enable certain stuff for the Khorovod event
                    if (matchingTransitWhitelist.Contains(transit.Id))
                    {
                        transit.ActivateAfterSeconds = 300;
                        transit.Events = true;
                    }
                    else
                    {
                        // Disable the other transits in this event, people are only allowed to transit to certain points
                        transit.IsActive = false;
                    }
                }
            }
        }

        StartLocalRaidResponseData result = new()
        {
            // Keep the first two dot-separated fields compatible with SPT's
            // EndLocalRaid parser, while making each local raid unique even
            // when two raids start in the same second.
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

        // Only has value when transitioning into map from previous one
        if (request.Transition != null)
        {
            result.Transition = request.Transition;
        }

        // Get data stored at end of previous raid (if any)
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

            // Complete, clean up
            raidActivity!.LocationTransit = null;
        }

        if (string.IsNullOrEmpty(matchId) || sessionId == matchId.Value)
        {
            // Apply changes from pmcConfig to bot hostility values
            typeof(LocationLifecycleService)
                .GetMethod("AdjustBotHostilitySettings", BindingFlags.NonPublic | BindingFlags.Instance)?
                .Invoke(_locationLifeCycleService, [result.LocationLoot]);
            typeof(LocationLifecycleService)
                .GetMethod("AdjustExtracts", BindingFlags.NonPublic | BindingFlags.Instance)?
                .Invoke(_locationLifeCycleService, [request.PlayerSide, request.Location, result.LocationLoot]);

            // Clear bot cache ready for bot generation call that occurs after this
            _botNameService.ClearNameCache();

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        }

        // Handle Player Inventory Wiping checks for alt-f4 prevention
        typeof(LocationLifecycleService)
            .GetMethod("HandlePreRaidInventoryChecks", BindingFlags.NonPublic | BindingFlags.Instance)?
            .Invoke(_locationLifeCycleService, [request.PlayerSide, playerProfile.CharacterData.PmcData, sessionId]);

            // Do not advertise a token until all response and profile work
            // has succeeded. An exception above means the client never gets
            // this ServerId and can never send its matching EndLocalRaid.
            //
            // A missing token must not fail the request: the match id comes from
            // SPT's own matchmaker, so every raid that was not registered through
            // /fika/raid/create or /fika/raid/join - a plain solo raid, or a peer
            // whose join never reached the server - has no Fika insurance state.
            // Throwing here turned those raids into "Empty response received" on
            // the client and blocked hosting entirely. SPT can still run the raid;
            // only the Fika insurance snapshot is unavailable for it.
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
}
