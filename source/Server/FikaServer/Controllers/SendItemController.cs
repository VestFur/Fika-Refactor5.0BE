using FikaServer.Models.Fika.Config;
using FikaServer.Models.Fika.SendItem;
using FikaServer.Models.Fika.WebSocket.Notifications;
using FikaServer.Services;
using FikaServer.WebSockets;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using System.Collections.Concurrent;

namespace FikaServer.Controllers;

[Injectable]
public class SendItemController(ISptLogger<SendItemController> logger, EventOutputHolder eventOutputHolder,
    SendItemTransferService transferService, SaveServer saveServer, HttpResponseUtil httpResponseUtil,
    FikaServerConfig fikaServerConfig, NotificationWebSocket notificationWebSocket, ICloner cloner)
{
    private static readonly ConcurrentDictionary<MongoId, SemaphoreSlim> SenderGates = new();

    public async ValueTask<ItemEventRouterResponse> SendItem(SendItemRequestData body, MongoId sessionId)
    {
        // 在读库存之前先把同一发送方的请求串行化。第二个
        // 请求必须看到第一个请求已移除的物品，否则两个请求
        // 都会为同一个库存根创建邮件。
        var gate = SenderGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        ItemEventRouterResponse output;
        ReceivedSentItemNotification notification;
        try
        {
            if (!saveServer.ProfileExists(sessionId))
            {
                throw new InvalidOperationException($"Sender profile {sessionId} was not found");
            }

            output = eventOutputHolder.GetOutput(sessionId);
            if (!fikaServerConfig.Server.AllowItemSending)
            {
                return httpResponseUtil.AppendErrorToOutput(output, "Item sending is disabled");
            }
            if (body is null || body.ItemIds is not { Length: > 0 } || string.IsNullOrWhiteSpace(body.Target))
            {
                return httpResponseUtil.AppendErrorToOutput(output, "Missing data in body");
            }

            var senderProfile = saveServer.GetProfile(sessionId);
            var senderPmc = senderProfile?.CharacterData?.PmcData;
            if (senderPmc?.Inventory?.Items is null)
            {
                return httpResponseUtil.AppendErrorToOutput(output, "Sender profile or inventory not found");
            }

            if (body.Target == sessionId)
            {
                return httpResponseUtil.AppendErrorToOutput(output, "Cannot send items to yourself");
            }

            if (!saveServer.ProfileExists(body.Target))
            {
                return httpResponseUtil.AppendErrorToOutput(output, "Target profile not found");
            }

            var targetProfile = saveServer.GetProfile(body.Target);
            if (targetProfile?.CharacterData?.PmcData?.Info is null || targetProfile.IsHeadlessProfile())
            {
                return httpResponseUtil.AppendErrorToOutput(output, "Target cannot receive items");
            }

            var requestedIdStrings = body.ItemIds.Distinct().ToArray();
            if (requestedIdStrings.Any(id => string.IsNullOrWhiteSpace(id) || !MongoId.IsValidMongoId(id)))
                return httpResponseUtil.AppendErrorToOutput(output, "Invalid item id");
            var requestedItemIds = requestedIdStrings.Select(id => new MongoId(id)).ToArray();

            // 客户端可能在上一次 HTTP 响应丢失后重试。此时发送方的
            // 库存已经为空；用日志来完成同一次转移，并
            // 重复它当时的删除响应。
            if (transferService.HasTransferRecord(sessionId, requestedItemIds))
            {
                await transferService.CompleteOrCreateAsync(sessionId, body.Target, senderPmc.Info?.Nickname ?? "Unknown",
                    requestedItemIds, [], 0, output);
                return output;
            }

            var senderItems = senderPmc.Inventory.Items;
            if (requestedItemIds.Any(id => !senderItems.Any(item => item.Id == id)))
            {
                return httpResponseUtil.AppendErrorToOutput(output, "One or more items were not found in inventory");
            }

            // RemoveItem 会连所选根的后代一起删除。否则同时发送
            // 容器和其中被选中的子项会创建两个邮件根，
            // 却只向客户端报告一次删除。
            var requestedIds = requestedItemIds.ToHashSet();
            foreach (var itemId in requestedItemIds)
            {
                foreach (var member in senderItems.GetItemWithChildren(itemId))
                {
                    if (member.Id != itemId && requestedIds.Contains(member.Id))
                        return httpResponseUtil.AppendErrorToOutput(output, "A selected item is inside another selected item");
                }
            }

            logger.Info($"{requestedItemIds.Length} items are going to sessionID: {body.Target}");

            // MailSendService 会收养孤立的根，并可能改动 ParentId/SlotId。
            // 在 RemoveItem 生成响应之前，不要触碰正在使用的库存。
            var itemsToSend = cloner.Clone(requestedItemIds
                .SelectMany(id => senderItems.GetItemWithChildren(id))
                .DistinctBy(item => item.Id)
                .ToList()) ?? [];

            if (itemsToSend.Count == 0)
            {
                return httpResponseUtil.AppendErrorToOutput(output, "Item not found in inventory");
            }

            // 客户端在打开收件人选择器之前会检查这一点，但
            // 物品事件请求也可以直接发送。检查完整的库存
            // 快照，这样被选中的容器就无法藏住被禁的
            // 配件，也无法藏住多个选中物品中的某一个。
            var blacklistedItems = fikaServerConfig.Client.BlacklistedItems;
            if (blacklistedItems is { Length: > 0 } && itemsToSend.Any(item =>
                    blacklistedItems.Contains(item.Template.ToString(), StringComparer.OrdinalIgnoreCase)))
            {
                return httpResponseUtil.AppendErrorToOutput(output, "One or more items are blacklisted from sending");
            }

            if (fikaServerConfig.Server.SentItemsLoseFIR)
            {
                foreach (var item in itemsToSend)
                {
                    item.Upd ??= new();
                    item.Upd.SpawnedInSession = false;
                }
            }

            await transferService.CompleteOrCreateAsync(sessionId, body.Target,
                senderPmc.Info?.Nickname ?? "Unknown", requestedItemIds, itemsToSend,
                fikaServerConfig.Server.ItemSendingStorageTime * 86_400L, output);

            var stackCount = itemsToSend.Count > 1
                ? 1d
                : itemsToSend[0].Upd?.StackObjectsCount.GetValueOrDefault() ?? 1d;

            notification = new ReceivedSentItemNotification
            {
                Nickname = senderPmc.Info?.Nickname ?? "Unknown",
                TargetId = body.Target,
                ItemName = $"{itemsToSend[0].Template} ShortName",
                StackCount = stackCount,
                Multiple = itemsToSend.Count > 1
            };
        }
        finally
        {
            gate.Release();
        }

        try
        {
            await notificationWebSocket.SendAsync(body.Target, notification);
        }
        catch (Exception ex)
        {
            // 邮件和库存变更已经成功；过期的 websocket 不得
            // 让发送方以为该物品事件失败。
            logger.Warning($"Unable to notify {body.Target} of the sent item: {ex.Message}");
        }

        return output;
    }

    public Dictionary<string, string> HandleAvailableReceivers(string sessionID)
    {
        Dictionary<string, string> result = [];

        if (!saveServer.ProfileExists(sessionID))
        {
            return result;
        }

        foreach (var profileKvP in saveServer.GetProfiles())
        {
            var profile = profileKvP.Value;

            // 跳过刚创建的 profile，它们会引发错误
            if (profile.CharacterData?.PmcData?.Info is null)
            {
                continue;
            }

            // 跳过 headless 客户端
            if (profile.IsHeadlessProfile())
            {
                continue;
            }

            var nickname = profile.CharacterData.PmcData.Info.Nickname;
            var targetId = profile.ProfileInfo?.ProfileId;

            if (string.IsNullOrWhiteSpace(nickname) || string.IsNullOrWhiteSpace(targetId) ||
                targetId == sessionID)
            {
                continue;
            }

            // 若结果中已存在同一用户则跳过
            if (result.ContainsKey(nickname))
            {
                continue;
            }

            result.Add(nickname, targetId);
        }

        return result;
    }
}
