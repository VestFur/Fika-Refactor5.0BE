using FikaServer.Models;
using FikaServer.Models.Enums;
using FikaServer.Models.Fika.Presence;
using FikaServer.Services;
using FikaShared;
using FikaShared.Responses;
using Microsoft.AspNetCore.Mvc;
using SPTarkov.Server.Core.Servers;
using static FikaShared.Enums;

namespace FikaServer.API;

[ApiController]
[Route("fika/api/players")]
[RequireApiKey]
public class PlayersController(SaveServer saveServer, PresenceService presenceService,
    ILogger<PlayersController> logger) : ControllerBase
{
    private static readonly Dictionary<string, EFikaLocation> _locationMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bigmap"] = EFikaLocation.Customs,
        ["factory4_day"] = EFikaLocation.Factory,
        ["factory4_night"] = EFikaLocation.Factory,
        ["interchange"] = EFikaLocation.Interchange,
        ["laboratory"] = EFikaLocation.Laboratory,
        ["labyrinth"] = EFikaLocation.Labyrinth,
        ["lighthouse"] = EFikaLocation.Lighthouse,
        ["rezervbase"] = EFikaLocation.Reserve,
        ["sandbox"] = EFikaLocation.GroundZero,
        ["sandbox_high"] = EFikaLocation.GroundZero,
        ["shoreline"] = EFikaLocation.Shoreline,
        ["tarkovstreets"] = EFikaLocation.Streets,
        ["woods"] = EFikaLocation.Woods
    };

    [HttpGet]
    public IActionResult HandleRequest()
    {
        // 在线状态以会话的 profile ID 为键。昵称可能
        // 重复或被更改，因此它们无法标识一个在线 profile。
        var presences = presenceService.AllPlayersPresenceById;
        var profiles = saveServer.GetProfiles();
        var onlinePlayers = new List<OnlinePlayer>(presences.Count);
        foreach (var (profileId, presence) in presences)
        {
            if (!profiles.TryGetValue(profileId, out var profile) || !profile.HasProfileData())
            {
                continue;
            }

            onlinePlayers.Add(new()
            {
                ProfileId = profileId,
                Nickname = profile.CharacterData?.PmcData?.Info?.Nickname ?? "Unknown",
                Level = profile.CharacterData?.PmcData?.Info?.Level ?? 1,
                Location = ToFikaLocation(presence)
            });
        }

        GetOnlinePlayersResponse playersResponse = new()
        {
            Players = onlinePlayers
        };

        return Ok(playersResponse);
    }

    private EFikaLocation ToFikaLocation(FikaPlayerPresence presence)
    {
        if (presence.RaidInformation != null)
        {
            if (_locationMap.TryGetValue(presence.RaidInformation.Location, out var eLocation))
            {
                return eLocation;
            }

            logger.LogWarning("Location was incorrect when getting presense for {Nickname}", presence.Nickname);
            return EFikaLocation.None;
        }

        if (presence.Activity is EFikaPlayerPresences.IN_HIDEOUT)
        {
            return EFikaLocation.Hideout;
        }

        return EFikaLocation.None;
    }
}
