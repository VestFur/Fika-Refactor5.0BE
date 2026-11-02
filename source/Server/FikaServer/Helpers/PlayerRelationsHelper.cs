using FikaServer.Models.Fika.WebSocket;
using FikaServer.Services.Cache;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Ws;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Servers.Ws;

namespace FikaServer.Helpers;

[Injectable]
public class PlayerRelationsHelper(ISptLogger<PlayerRelationsHelper> logger,
    PlayerRelationsService playerRelationsService, SaveServer saveServer,
    SptWebSocketConnectionHandler webSocketHandler, FriendRequestsService friendRequestsService)
{
    /// <summary>
    /// 获取玩家的好友列表
    /// </summary>
    /// <param name="profileId"></param>
    /// <returns>好友列表</returns>
    public List<string> GetFriendsList(MongoId profileId)
    {
        // 调用方可能在遍历返回的列表时剔除过期好友。
        return [.. playerRelationsService.GetStoredValue(profileId).Friends];
    }

    public void RemoveInvalidFriend(MongoId profileId, string invalidId)
    {
        var relations = playerRelationsService.GetStoredValue(profileId);
        if (relations.Friends.Remove(invalidId))
        {
            playerRelationsService.SaveProfileRelations();
        }
    }

    /// <summary>
    /// 移除一条好友关系
    /// </summary>
    /// <param name="fromProfileId">发起请求的 profileId</param>
    /// <param name="toProfileId">目标 profileId</param>
    /// <exception cref="NotImplementedException"></exception>
    public void RemoveFriend(MongoId fromProfileId, MongoId toProfileId)
    {
        var fromRelations = playerRelationsService.GetStoredValue(fromProfileId);

        var removedFrom = fromRelations.Friends.Remove(toProfileId);
        if (!removedFrom)
        {
            logger.Warning($"{fromProfileId} tried to remove {toProfileId} from their friend list unsuccessfully");
        }

        // 另一个档案可能已被删除。从调用方移除过期好友
        // 不得为该档案创建幽灵记录。
        var removedTo = playerRelationsService.TryGetStoredValue(toProfileId, out var toRelations) &&
            toRelations!.Friends.Remove(fromProfileId);
        if (toRelations != null && !removedTo)
        {
            logger.Warning($"{toProfileId} tried to remove {fromProfileId} from their friend list unsuccessfully");
        }

        if (removedFrom || removedTo)
        {
            playerRelationsService.SaveProfileRelations();
        }

        if (saveServer.GetProfiles().TryGetValue(fromProfileId, out var profile) &&
            profile.ProfileInfo != null && profile.CharacterData?.PmcData?.Info != null)
        {
            webSocketHandler.SendMessageAsync(toProfileId, new WsFriendListRemove()
            {
                EventIdentifier = new(),
                EventType = NotificationEventType.youAreRemovedFromFriendList,
                Profile = profile.ToFriendData()
            });
        }
    }

    /// <summary>
    /// 获取玩家的屏蔽列表
    /// </summary>
    /// <param name="profileId"></param>
    /// <returns>屏蔽列表</returns>
    public List<string> GetIgnoreList(MongoId profileId)
    {
        return [.. playerRelationsService.GetStoredValue(profileId).Ignore];
    }

    /// <summary>
    /// 返回屏蔽了该玩家的玩家列表
    /// </summary>
    /// <param name="profileId">要检查的玩家</param>
    /// <returns>屏蔽该玩家的玩家列表</returns>
    /// <exception cref="NotImplementedException"></exception>
    public List<string> GetInIgnoreList(MongoId profileId)
    {
        return [.. playerRelationsService.Keys
            .Where(x => playerRelationsService.GetStoredValue(x).Ignore.Contains(profileId))];
    }

    public bool RemoveFriendRequest(MongoId from, MongoId to, ERemoveFriendReason reason)
    {
        if (!friendRequestsService.HasFriendRequest(from, to, out var response))
        {
            logger.Error($"{from} tried to remove a friend request from {to} but it doesn't exist. Reason: {reason}");
            return false;
        }

        friendRequestsService.DeleteFriendRequest(response!)
            .GetAwaiter().GetResult();

        switch (reason)
        {
            case ERemoveFriendReason.Accept:
                {
                    var profile = saveServer.GetProfile(to);
                    webSocketHandler.SendMessageAsync(from, new WsFriendListRemove()
                    {
                        EventIdentifier = new(),
                        EventType = NotificationEventType.friendListRequestAccept,
                        Profile = profile.ToFriendData()
                    });
                }
                break;
            case ERemoveFriendReason.Cancel:
                {
                    var profile = saveServer.GetProfile(from);
                    webSocketHandler.SendMessageAsync(to, new WsFriendListRemove()
                    {
                        EventIdentifier = new(),
                        EventType = NotificationEventType.friendListRequestCancel,
                        Profile = profile.ToFriendData()
                    });
                }
                break;
            case ERemoveFriendReason.Decline:
                {
                    var profile = saveServer.GetProfile(to);
                    webSocketHandler.SendMessageAsync(from, new WsFriendListRemove()
                    {
                        EventIdentifier = new(),
                        EventType = NotificationEventType.friendListRequestDecline,
                        Profile = profile.ToFriendData()
                    });
                }
                break;
        }

        return true;
    }

    /// <summary>
    /// 将新关系加入缓存并保存
    /// </summary>
    /// <param name="from"></param>
    /// <param name="to"></param>
    public void AddFriend(MongoId from, MongoId to)
    {
        var fromRelations = playerRelationsService.GetStoredValue(from);
        if (!fromRelations.Friends.Contains(to))
        {
            fromRelations.Friends.Add(to);
        }

        var toRelations = playerRelationsService.GetStoredValue(to);
        if (!toRelations.Friends.Contains(from))
        {
            toRelations.Friends.Add(from);
        }

        playerRelationsService.SaveProfileRelations();
    }

    public void AddToIgnoreList(MongoId from, MongoId to)
    {
        var fromRelations = playerRelationsService.GetStoredValue(from);
        if (fromRelations.Ignore.Contains(to))
        {
            logger.Warning($"{from} already has {to} in their ignore list");
            return;
        }

        fromRelations.Ignore.Add(to);
        playerRelationsService.SaveProfileRelations();

        var profile = saveServer.GetProfile(from);
        ArgumentNullException.ThrowIfNull(profile);

        webSocketHandler.SendMessageAsync(to, new WsIgnoreListAdd()
        {
            EventIdentifier = new(),
            EventType = NotificationEventType.youAreAddToIgnoreList,
            Id = from,
            Profile = profile.ToFriendData()
        });
    }

    public void RemoveFromIgnoreList(MongoId from, MongoId to)
    {
        var fromRelations = playerRelationsService.GetStoredValue(from);
        if (!fromRelations.Ignore.Remove(to))
        {
            logger.Warning($"{from} tried to remove {to} from their ignore list but it was unsuccesful");
            return;
        }

        playerRelationsService.SaveProfileRelations();

        var profile = saveServer.GetProfile(from);
        ArgumentNullException.ThrowIfNull(profile);

        webSocketHandler.SendMessageAsync(to, new WsIgnoreListAdd()
        {
            EventIdentifier = new(),
            EventType = NotificationEventType.youAreRemoveFromIgnoreList,
            Id = from,
            Profile = profile.ToFriendData()
        });
    }

    public enum ERemoveFriendReason
    {
        Accept,
        Cancel,
        Decline
    }
}
