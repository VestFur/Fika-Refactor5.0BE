using FikaServer.Models.Fika.Config;
using FikaServer.Models.Fika.Dialog;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace FikaServer.Services.Cache;

[Injectable(InjectionType.Singleton)]
public class FriendRequestsService(
    FikaPaths fikaPaths,
    JsonUtil jsonUtil,
    SaveServer saveServer,
    ISptLogger<PlayerRelationsService> logger)
{
    public List<FriendRequestListResponse> AllFriendRequests
    {
        get
        {
            lock (_listLock)
            {
                return [.. _friendRequests];
            }
        }
    }

    private readonly string _friendRequestsFullPath = fikaPaths.DatabasePath;
    private List<FriendRequestListResponse> _friendRequests = [];

    private readonly Lock _listLock = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public async Task OnPreLoad()
    {
        if (!Directory.Exists(_friendRequestsFullPath))
        {
            Directory.CreateDirectory(_friendRequestsFullPath);
        }

        var file = $"{_friendRequestsFullPath}/friendRequests.json";
        if (!File.Exists(file))
        {
            await SaveFriendRequests();
        }
        else
        {
            try
            {
                var requests = await jsonUtil.DeserializeFromFileAsync<List<FriendRequestListResponse>>(file) ?? throw new InvalidOperationException("Could not load friend requests!");
                lock (_listLock)
                {
                    _friendRequests = requests;
                }
            }
            catch (Exception ex)
            {
                logger.Error($"Failed to load friend requests: {ex.Message}");
            }
        }
    }

    public async Task OnPostLoad()
    {
        var profileIds = saveServer.GetProfiles().Keys.ToHashSet();
        int removed;
        lock (_listLock)
        {
            removed = _friendRequests.RemoveAll(request =>
                !profileIds.Contains(request.From) || !profileIds.Contains(request.To));
            logger.Debug($"Loaded {_friendRequests.Count} friend requests");
        }

        if (removed > 0)
        {
            logger.Info($"Removed {removed} friend requests for deleted profiles");
            await SaveFriendRequests();
        }
    }

    public async Task AddFriendRequest(FriendRequestListResponse friendRequest)
    {
        lock (_listLock)
        {
            _friendRequests.Add(friendRequest);
        }

        await SaveFriendRequests();
    }

    public async Task DeleteFriendRequest(FriendRequestListResponse friendRequest)
    {
        lock (_listLock)
        {
            if (!_friendRequests.Remove(friendRequest))
            {
                logger.Error($"Unable to remove friend request {friendRequest.Id}");
                return;
            }
        }

        await SaveFriendRequests();
    }

    /// <summary>
    /// 检查两个 ProfileID 之间是否存在好友请求
    /// </summary>
    /// <param name="from"></param>
    /// <param name="to"></param>
    /// <returns></returns>
    public bool HasFriendRequest(MongoId from, MongoId to)
    {
        lock (_listLock)
        {
            return _friendRequests
                .Any(x => x.From == from && x.To == to);
        }
    }

    /// <summary>
    /// 检查两个 ProfileID 之间是否存在好友请求
    /// </summary>
    /// <param name="from"></param>
    /// <param name="to"></param>
    /// <param name="response"></param>
    /// <returns>该好友请求</returns>
    public bool HasFriendRequest(MongoId from, MongoId to, out FriendRequestListResponse? response)
    {
        lock (_listLock)
        {
            response = _friendRequests.FirstOrDefault(x => x.From == from && x.To == to);
            return response != null;
        }
    }

    public List<FriendRequestListResponse> GetReceivedFriendRequests(MongoId profileId)
    {
        lock (_listLock)
        {
            return [.. _friendRequests
                .Where(x => x.To == profileId)];
        }
    }

    public List<FriendRequestListResponse> GetSentFriendRequests(MongoId profileId)
    {
        lock (_listLock)
        {
            return [.. _friendRequests
                .Where(x => x.From == profileId)];
        }
    }

    private async Task SaveFriendRequests()
    {
        await _saveLock.WaitAsync();
        try
        {
            string json;
            lock (_listLock)
            {
                json = jsonUtil.Serialize(_friendRequests, true)
                    ?? throw new InvalidOperationException("Could not serialize friend requests");
            }

            await File.WriteAllTextAsync($"{_friendRequestsFullPath}/friendRequests.json", json);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
