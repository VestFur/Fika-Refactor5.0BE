using FikaServer.Models.Enums;
using FikaServer.Models.Fika.Presence;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using System.Collections.Concurrent;

namespace FikaServer.Services;

[Injectable(InjectionType.Singleton)]
public class PresenceService(SaveServer saveServer, TimeUtil timeUtil, ISptLogger<PresenceService> logger)
{
    public List<FikaPlayerPresence> AllPlayersPresence
    {
        get
        {
            return [.. _onlinePlayers.Values.Select(CopyPresence)];
        }
    }

    public Dictionary<MongoId, FikaPlayerPresence> AllPlayersPresenceById =>
        _onlinePlayers.ToDictionary(entry => entry.Key, entry => CopyPresence(entry.Value));

    private readonly ConcurrentDictionary<MongoId, FikaPlayerPresence> _onlinePlayers = [];

    public FikaPlayerPresence? GetPlayerPresence(MongoId profileId)
    {
        if (_onlinePlayers.TryGetValue(profileId, out var playerPresence))
        {
            return CopyPresence(playerPresence);
        }

        return null;
    }

    private static FikaPlayerPresence CopyPresence(FikaPlayerPresence presence) => presence with
    {
        RaidInformation = presence.RaidInformation is { } raid ? raid with { } : null
    };

    public void AddPlayerPresence(MongoId sessionID)
    {
        if (!saveServer.GetProfiles().TryGetValue(sessionID, out var profile) ||
            profile.CharacterData?.PmcData?.Info is null)
        {
            return;
        }

        FikaPlayerPresence data = new()
        {
            Nickname = profile.CharacterData.PmcData.Info.Nickname,
            Level = profile.CharacterData.PmcData.Info.Level ?? 0,
            Activity = EFikaPlayerPresences.IN_MENU,
            ActivityStartedTimestamp = timeUtil.GetTimeStamp()
        };

        logger.Debug($"[Fika Presence] Adding player: {data.Nickname}");

        _onlinePlayers.TryAdd(sessionID, data);
    }

    public void UpdatePlayerPresence(MongoId sessionID, FikaSetPresence NewPresence)
    {
        if (!saveServer.GetProfiles().TryGetValue(sessionID, out var profile) ||
            profile.CharacterData?.PmcData?.Info is null)
        {
            return;
        }

        while (_onlinePlayers.TryGetValue(sessionID, out var currentPresence))
        {
            var nextPresence = new FikaPlayerPresence
            {
                Nickname = profile.CharacterData.PmcData.Info.Nickname,
                Level = profile.CharacterData.PmcData.Info.Level ?? 0,
                Activity = NewPresence.Activity,
                ActivityStartedTimestamp = timeUtil.GetTimeStamp(),
                RaidInformation = NewPresence.RaidInformation is { } raid ? raid with { } : null
            };

            if (_onlinePlayers.TryUpdate(sessionID, nextPresence, currentPresence))
            {
                return;
            }
        }
    }

    public void RemovePlayerPresence(MongoId sessionID)
    {
        _onlinePlayers.TryRemove(sessionID, out _);
    }

    public void UpdateMatchStatus(MongoId matchId, EFikaMatchStatus status)
    {
        foreach (var profileId in _onlinePlayers.Keys)
        {
            while (_onlinePlayers.TryGetValue(profileId, out var currentPresence) &&
                currentPresence.RaidInformation is { } raid && raid.MatchId == matchId)
            {
                var nextPresence = currentPresence with
                {
                    RaidInformation = raid with { Started = status != EFikaMatchStatus.COMPLETE }
                };

                if (_onlinePlayers.TryUpdate(profileId, nextPresence, currentPresence))
                {
                    break;
                }
            }
        }
    }
}
