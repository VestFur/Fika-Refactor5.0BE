using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Servers;

namespace FikaServer.Services.Cache;

/// <summary>
/// 用于按昵称缓存 profile
/// </summary>
/// <param name="saveServer"></param>
[Injectable(InjectionType.Singleton)]
public class FikaProfileService(ISptLogger<FikaProfileService> logger, SaveServer saveServer)
{
    private readonly Dictionary<string, MongoId> _profiles = [];
    private readonly Lock _profilesLock = new();

    public Dictionary<string, MongoId> GetAllProfiles()
    {
        lock (_profilesLock)
        {
            // 名称可能改变而 profile 数量不变。每次重建
            // 全新的映射还能避免调用方在另一个请求
            // 正在重建缓存时观察到该缓存。
            RefreshProfiles();
            return new Dictionary<string, MongoId>(_profiles);
        }
    }

    public MongoId? GetProfileIdByNickname(string nickname)
    {
        var profiles = GetAllProfiles();
        return profiles.TryGetValue(nickname, out var foundId) ? foundId : null;
    }

    public SptProfile? GetProfileByNickname(string nickname)
    {
        var profileId = GetProfileIdByNickname(nickname);
        if (!profileId.HasValue ||
            !saveServer.GetProfiles().TryGetValue(profileId.Value, out var profile) ||
            profile.CharacterData?.PmcData?.Info?.Nickname != nickname)
        {
            return null;
        }

        return profile;
    }

    private void RefreshProfiles()
    {
        _profiles.Clear();
        var profiles = saveServer.GetProfiles();
        HashSet<string> duplicateNicknames = [];
        foreach ((var id, var profile) in profiles)
        {
            if (!profile.HasProfileData())
            {
                continue;
            }

            var nick = profile?.CharacterData?.PmcData?.Info?.Nickname;
            if (!string.IsNullOrEmpty(nick))
            {
                if (duplicateNicknames.Contains(nick))
                {
                    continue;
                }

                if (!_profiles.TryAdd(nick, id))
                {
                    // 当两名玩家使用相同昵称时，仅凭昵称的
                    // 命令无法安全地在两个 profile 之间做出选择。
                    _profiles.Remove(nick);
                    duplicateNicknames.Add(nick);
                    logger.Error($"Nickname {nick} is shared by multiple profiles; nickname lookup is disabled for it");
                }
            }
        }
    }
}
