using FikaServer.Helpers;
using FikaServer.ChatBot;
using FikaServer.Models.Fika.Dialog;
using FikaServer.Models.Fika.WebSocket;
using FikaServer.Services.Cache;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Dialogue;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Dialog;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.Ws;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Servers.Ws;
using SPTarkov.Server.Core.Utils;
using static FikaServer.Helpers.PlayerRelationsHelper;

namespace FikaServer.Controllers;

[Injectable]
public class FikaDialogueController(ISptLogger<FikaDialogueController> logger,
    PlayerRelationsHelper playerRelationsHelper, SaveServer saveServer,
    TimeUtil timeUtil, DialogueHelper dialogueHelper, SptWebSocketConnectionHandler socketConnectionHandler,
    FriendRequestsService friendRequestsService, HttpResponseUtil httpResponseUtil, CoreConfig coreConfig,
    IEnumerable<IDialogueChatBot> dialogueChatBots)
{
    protected readonly List<IDialogueChatBot> _dialogueChatBots = [.. dialogueChatBots];

    /// <summary>
    /// 获取指定 profileId 的全部好友列表
    /// </summary>
    /// <param name="sessionId">要获取好友列表的 profile id</param>
    /// <returns>新建的 <see cref="GetFriendListDataResponse"/></returns>
    public GetFriendListDataResponse GetFriendsList(MongoId sessionId)
    {
        List<UserDialogInfo> botsAndFriends = [];
        foreach (var item in _dialogueChatBots)
        {
            var chatBot = item.GetChatBot();
            // 与 SPT 的活跃 bot 过滤保持一致。Fika 自有的 bot 在
            // SPT 的 bot 设置中没有条目，仍可供 Fika 命令使用。
            if (item is FikaChatBot || coreConfig.Features.ChatbotFeatures.EnabledBots.GetValueOrDefault(chatBot.Id))
            {
                botsAndFriends.Add(chatBot);
            }
        }

        // SPT 5 的 GetPmcProfile 会调用 SaveServer.GetProfile，已删除的
        // 账号会在那里抛异常。先检查当前 profile 快照，这样过期的
        // 关系也不会让整个好友列表响应失败。
        var profiles = saveServer.GetProfiles();
        foreach (var friend in playerRelationsHelper.GetFriendsList(sessionId))
        {
            var profile = friend.TryParseMongoId(out var friendId) && profiles.TryGetValue(friendId, out var friendProfile)
                ? friendProfile.CharacterData?.PmcData
                : null;
            if (profile == null)
            {
                if (friend.TryParseMongoId(out friendId))
                {
                    playerRelationsHelper.RemoveFriend(sessionId, friendId);
                }
                else
                {
                    playerRelationsHelper.RemoveInvalidFriend(sessionId, friend);
                }
                continue;
            }

            botsAndFriends.Add(new()
            {
                Id = profile.Id.Value,
                Aid = profile.Aid,
                Info = new()
                {
                    Nickname = profile.Info.Nickname,
                    Level = profile.Info.Level,
                    // 与 SPT 的 GetChatRoomMemberFromPmcProfile 现在返回的声望载荷一致，
                    // 使 Fika 的好友列表始终与 SPT 的 DialogueController.GetFriendList 相符。
                    PrestigeLevel = profile.Info.PrestigeLevel,
                    PrestigeGameMode = profile.Info.PrestigeGameMode,
                    Side = profile.Info.Side,
                    MemberCategory = profile.Info.MemberCategory,
                    SelectedMemberCategory = profile.Info.SelectedMemberCategory
                }
            });
        }

        return new()
        {
            Friends = botsAndFriends,
            Ignore = playerRelationsHelper.GetIgnoreList(sessionId),
            InIgnoreList = playerRelationsHelper.GetInIgnoreList(sessionId)
        };
    }

    public FriendRequestSendResponse? AddFriendRequest(MongoId from, MongoId to)
    {
        if (from == default || to == default)
        {
            throw new NullReferenceException($"From or to was null! [From]: {from}, [To]: {to}");
        }

        if (friendRequestsService.HasFriendRequest(from, to))
        {
            logger.Error($"{from} has already sent a request to {to}");
            return null;
        }

        if (!saveServer.ProfileExists(to))
        {
            logger.Error($"{from} tried to send a friend request to {to} who doesn't exist");
            return null;
        }

        // todo: 修复异步问题
        friendRequestsService.AddFriendRequest(new()
        {
            Id = new MongoId(),
            From = from,
            To = to,
            Date = timeUtil.GetTimeStamp()
        }).GetAwaiter().GetResult();

        var fromProfile = saveServer.GetProfile(from)
            ?? throw new NullReferenceException($"{from} did not exist in the database");

        socketConnectionHandler.SendMessageAsync(to, new WsFriendListAdd()
        {
            EventIdentifier = new(),
            EventType = NotificationEventType.friendListNewRequest,
            Id = from,
            Profile = fromProfile.ToFriendData()
        });

        return new FriendRequestSendResponse
        {
            Status = BackendErrorCodes.None,
            RequestId = from,
            RetryAfter = 0
        };
    }

    /// <summary>
    /// 向另一位玩家发送消息
    /// </summary>
    /// <param name="sessionId">发送方的 profile id</param>
    /// <param name="request">待处理的请求</param>
    /// <returns>已发送消息的 id</returns>
    public string SendMessage(MongoId sessionId, SendMessageRequest request, Dictionary<MongoId, SptProfile> profiles)
    {
        var receiverProfile = profiles[request.DialogId];
        var senderProfile = profiles[sessionId];

        if (!senderProfile.DialogueRecords.ContainsKey(request.DialogId))
        {
            senderProfile.DialogueRecords.Add(request.DialogId, new()
            {
                AttachmentsNew = 0,
                New = 0,
                Pinned = false,
                Type = MessageType.UserMessage,
                Messages = [],
                Users = [],
                Id = request.DialogId
            });
        }

        var senderDialog = senderProfile.DialogueRecords[request.DialogId];
        senderDialog.Users = [
            new()
            {
                Id = receiverProfile.ProfileInfo.ProfileId.Value,
                Aid = receiverProfile.ProfileInfo.Aid,
                Info = new()
                {
                    Nickname = receiverProfile.CharacterData.PmcData.Info.Nickname,
                    Side = receiverProfile.CharacterData.PmcData.Info.Side,
                    Level = receiverProfile.CharacterData.PmcData.Info.Level,
                    // SPT 的 GetProfilesForMail/GetDialogueUsers 用同一个 Info 对象
                    // 填充这些内容；复制的对话用户列表必须保持一致。
                    PrestigeLevel = receiverProfile.CharacterData.PmcData.Info.PrestigeLevel,
                    PrestigeGameMode = receiverProfile.CharacterData.PmcData.Info.PrestigeGameMode,
                    MemberCategory = receiverProfile.CharacterData.PmcData.Info.MemberCategory,
                    SelectedMemberCategory = receiverProfile.CharacterData.PmcData.Info.SelectedMemberCategory
                }
            },
            new()
            {
                Id = senderProfile.ProfileInfo.ProfileId.Value,
                Aid = senderProfile.ProfileInfo.Aid,
                Info = new()
                {
                    Nickname = senderProfile.CharacterData.PmcData.Info.Nickname,
                    Side = senderProfile.CharacterData.PmcData.Info.Side,
                    Level = senderProfile.CharacterData.PmcData.Info.Level,
                    PrestigeLevel = senderProfile.CharacterData.PmcData.Info.PrestigeLevel,
                    PrestigeGameMode = senderProfile.CharacterData.PmcData.Info.PrestigeGameMode,
                    MemberCategory = senderProfile.CharacterData.PmcData.Info.MemberCategory,
                    SelectedMemberCategory = senderProfile.CharacterData.PmcData.Info.SelectedMemberCategory
                }
            }
        ];

        if (!receiverProfile.DialogueRecords.ContainsKey(sessionId))
        {
            receiverProfile.DialogueRecords.Add(sessionId, new()
            {
                AttachmentsNew = 0,
                New = 0,
                Pinned = false,
                Type = MessageType.UserMessage,
                Messages = [],
                Users = [],
                Id = sessionId
            });
        }

        var receiverDialog = receiverProfile.DialogueRecords[sessionId];
        receiverDialog.New++;
        receiverDialog.Users = [
            new()
            {
                Id = senderProfile.ProfileInfo.ProfileId.Value,
                Aid = senderProfile.ProfileInfo.Aid,
                Info = new()
                {
                    Nickname = senderProfile.CharacterData.PmcData.Info.Nickname,
                    Side = senderProfile.CharacterData.PmcData.Info.Side,
                    Level = senderProfile.CharacterData.PmcData.Info.Level,
                    PrestigeLevel = senderProfile.CharacterData.PmcData.Info.PrestigeLevel,
                    PrestigeGameMode = senderProfile.CharacterData.PmcData.Info.PrestigeGameMode,
                    MemberCategory = senderProfile.CharacterData.PmcData.Info.MemberCategory,
                    SelectedMemberCategory = senderProfile.CharacterData.PmcData.Info.SelectedMemberCategory
                }
            },
            new()
            {
                Id = receiverProfile.ProfileInfo.ProfileId.Value,
                Aid = receiverProfile.ProfileInfo.Aid,
                Info = new()
                {
                    Nickname = receiverProfile.CharacterData.PmcData.Info.Nickname,
                    Side = receiverProfile.CharacterData.PmcData.Info.Side,
                    Level = receiverProfile.CharacterData.PmcData.Info.Level,
                    // SPT 的 GetProfilesForMail/GetDialogueUsers 用同一个 Info 对象
                    // 填充这些内容；复制的对话用户列表必须保持一致。
                    PrestigeLevel = receiverProfile.CharacterData.PmcData.Info.PrestigeLevel,
                    PrestigeGameMode = receiverProfile.CharacterData.PmcData.Info.PrestigeGameMode,
                    MemberCategory = receiverProfile.CharacterData.PmcData.Info.MemberCategory,
                    SelectedMemberCategory = receiverProfile.CharacterData.PmcData.Info.SelectedMemberCategory
                }
            }
        ];

        Message message = new()
        {
            Id = new MongoId(),
            UserId = sessionId,
            MessageType = request.Type,
            Member = new()
            {
                Nickname = senderProfile.CharacterData.PmcData.Info.Nickname,
                Side = senderProfile.CharacterData.PmcData.Info.Side,
                Level = senderProfile.CharacterData.PmcData.Info.Level,
                MemberCategory = senderProfile.CharacterData.PmcData.Info.MemberCategory,
                IsIgnored = playerRelationsHelper
                    .GetInIgnoreList(sessionId)
                    .Contains(request.DialogId),
                IsBanned = false
            },
            DateTime = timeUtil.GetTimeStamp(),
            Text = request.Text,
            RewardCollected = false
        };

        if (!string.IsNullOrEmpty(request.ReplyTo))
        {
            var replyMessage = GetMessageToReplyTo(request.DialogId, request.ReplyTo, sessionId);
            if (replyMessage != null)
            {
                message.ReplyTo = replyMessage;
            }
        }

        senderDialog.Messages?.Add(message);
        receiverDialog.Messages?.Add(message);

        socketConnectionHandler.SendMessageAsync(receiverProfile.ProfileInfo.ProfileId.Value, new WsChatMessageReceived()
        {
            EventIdentifier = new(),
            EventType = NotificationEventType.new_message,
            DialogId = sessionId,
            Message = message
        });

        return message.Id;
    }

    /// <summary>
    /// 检查是否存在可回复的消息
    /// </summary>
    /// <param name="recipientId">接收方的 id</param>
    /// <param name="replyToId">被回复消息的 id</param>
    /// <param name="dialogueId">对话的 id，应为对方发送者的 profile id</param>
    /// <returns>新建的 <see cref="ReplyTo"/>，包含被回复消息的数据</returns>
    private ReplyTo? GetMessageToReplyTo(string recipientId, string replyToId, string dialogueId)
    {
        var currentDialogue = dialogueHelper.GetDialogueFromProfile(recipientId, dialogueId);
        if (currentDialogue == null)
        {
            logger.Warning($"Could not find dialogue {dialogueId} from sender");
            return null;
        }

        var message = currentDialogue.Messages
            .Where(x => x.Id == replyToId)
            .FirstOrDefault();

        if (message != null)
        {
            return new()
            {
                Id = message.Id,
                UserId = message.UserId,
                DateTime = message.DateTime,
                MessageType = message.MessageType,
                Text = message.Text
            };
        }

        return null;
    }

    public ValueTask<string> ListInbox(MongoId sessionID)
    {
        var receivedFriendRequests = friendRequestsService.GetReceivedFriendRequests(sessionID);
        var profiles = saveServer.GetProfiles();
        List<FriendRequestListResponse> validRequests = [];

        foreach (var receivedFriendRequest in receivedFriendRequests)
        {
            var profile = profiles.TryGetValue(receivedFriendRequest.From, out var sender)
                ? sender.CharacterData?.PmcData
                : null;

            if (profile == null)
            {
                continue;
            }

            receivedFriendRequest.Profile = profile.ToFriendData();
            validRequests.Add(receivedFriendRequest);
        }

        return new(httpResponseUtil.GetBody(validRequests));
    }

    public ValueTask<string> ListOutBox(MongoId sessionID)
    {
        var sentFriendRequests = friendRequestsService.GetSentFriendRequests(sessionID);
        var profiles = saveServer.GetProfiles();
        List<FriendRequestListResponse> validRequests = [];

        foreach (var sentFriendRequest in sentFriendRequests)
        {
            var profile = profiles.TryGetValue(sentFriendRequest.To, out var recipient)
                ? recipient.CharacterData?.PmcData
                : null;

            if (profile == null)
            {
                continue;
            }

            sentFriendRequest.Profile = profile.ToFriendData();
            validRequests.Add(sentFriendRequest);
        }

        return new(httpResponseUtil.GetBody(validRequests));
    }

    public ValueTask<string> SendFriendRequest(MongoId fromProfileId, MongoId toProfileId)
    {
        return new(httpResponseUtil.GetBody(AddFriendRequest(fromProfileId, toProfileId)));
    }

    public ValueTask<string> AcceptAllFriendRequests(MongoId sessionID)
    {
        var receivedFriendRequests = friendRequestsService.GetReceivedFriendRequests(sessionID);

        foreach (var receivedFriendRequest in receivedFriendRequests)
        {
            if (playerRelationsHelper.RemoveFriendRequest(receivedFriendRequest.From, receivedFriendRequest.To, ERemoveFriendReason.Accept))
            {
                playerRelationsHelper.AddFriend(receivedFriendRequest.From, receivedFriendRequest.To);
            }
        }

        return new ValueTask<string>(httpResponseUtil.NullResponse());
    }

    /// <summary>
    /// 接受好友请求
    /// </summary>
    /// <param name="sessionID"></param>
    /// <param name="request"></param>
    /// <returns></returns>
    public ValueTask<string> AcceptFriendRequest(MongoId sessionID, AcceptFriendRequestData request)
    {
        if (playerRelationsHelper.RemoveFriendRequest(request.ProfileId, sessionID, ERemoveFriendReason.Accept))
        {
            playerRelationsHelper.AddFriend(sessionID, request.ProfileId);
        }

        return new(httpResponseUtil.NullResponse());
    }

    public ValueTask<string> CancelFriendRequest(MongoId fromProfileId, string toProfileId)
    {
        if (toProfileId == null)
        {
            throw new NullReferenceException("Request.ProfileId was null!");
        }

        if (!playerRelationsHelper.RemoveFriendRequest(fromProfileId, toProfileId, ERemoveFriendReason.Cancel))
        {
            logger.Error($"Failed to delete friend request from {fromProfileId} to {toProfileId}");
        }

        return new(httpResponseUtil.NullResponse());
    }

    public ValueTask<string> DeclineFriendRequest(string fromProfileId, MongoId toProfileId)
    {
        playerRelationsHelper.RemoveFriendRequest(fromProfileId, toProfileId, ERemoveFriendReason.Decline);

        return new(httpResponseUtil.NullResponse());
    }

    public ValueTask<string> DeleteFriend(MongoId fromProfileId, MongoId toProfileId)
    {
        playerRelationsHelper.RemoveFriend(fromProfileId, toProfileId);

        return new(httpResponseUtil.NullResponse());
    }

    public ValueTask<string> IgnoreFriend(MongoId fromProfileId, string toProfileId)
    {
        playerRelationsHelper.AddToIgnoreList(fromProfileId, toProfileId);

        return new(httpResponseUtil.NullResponse());
    }

    public ValueTask<string> UnIgnoreFriend(MongoId fromProfileId, string toProfileId)
    {
        playerRelationsHelper.RemoveFromIgnoreList(fromProfileId, toProfileId);

        return new(httpResponseUtil.NullResponse());
    }
}
