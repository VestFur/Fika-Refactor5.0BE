using System.Security.Cryptography;
using System.Text;
using FikaServer.Models.Fika.Config;
using FikaServer.Models.Fika.SendItem;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Dialog;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using Path = System.IO.Path;

namespace FikaServer.Services;

/// <summary>
/// 用于玩家间物品邮件的预写日志（write-ahead log）。在创建邮件之前
/// 先保存发送方，收件人邮件中的标记则使重放变得安全。
/// </summary>
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 1)]
public sealed class SendItemTransferService(
    FikaPaths fikaPaths,
    SaveServer saveServer,
    MailSendService mailSendService,
    TemplateTable templateTable,
    JsonUtil jsonUtil,
    ICloner cloner,
    ISptLogger<SendItemTransferService> logger) : IOnLoad
{
    private const string MailMarkerPrefix = "fika-item-transfer:";
    private const string ReceiptPrefix = "fika-item-transfer-receipt:";
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private string JournalDirectory => Path.Combine(fikaPaths.DatabasePath, "sendItemTransfers");

    public bool HasTransferRecord(MongoId senderId, MongoId[] rootIds) =>
        File.Exists(GetRecordPath(GetTransferId(senderId, rootIds)));

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(JournalDirectory))
            return;
        await _transferGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var path in Directory.EnumerateFiles(JournalDirectory, "*.json").OrderBy(path => path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var transfer = await ReadRecordAsync(path, cancellationToken);
                    if (transfer.Stage != "Completed")
                    {
                        await CompleteAsync(transfer, cancellationToken);
                        logger.Info($"Recovered item transfer {transfer.Id} from {transfer.SenderId} to {transfer.TargetId}");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 保留该日志以便稍后重试。单个损坏的
                    // profile 不应阻止服务器启动。
                    logger.Error($"Could not recover item transfer journal {path}: {ex}");
                }
            }
        }
        finally
        {
            _transferGate.Release();
        }
    }

    public async Task CompleteOrCreateAsync(MongoId senderId, MongoId targetId, string senderNickname,
        MongoId[] rootIds, List<Item> items, long storageSeconds, ItemEventRouterResponse output,
        CancellationToken cancellationToken = default)
    {
        await _transferGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(JournalDirectory);
            var transferId = GetTransferId(senderId, rootIds);
            var path = GetRecordPath(transferId);
            SendItemTransferRecord transfer;
            if (File.Exists(path))
            {
                transfer = await ReadRecordAsync(path, cancellationToken);
                if (transfer.SenderId != senderId.ToString() || transfer.TargetId != targetId.ToString()
                    || !transfer.RootItemIds.Order(StringComparer.Ordinal)
                        .SequenceEqual(rootIds.Select(id => id.ToString()).Order(StringComparer.Ordinal)))
                {
                    throw new InvalidOperationException("The selected items were already sent to another player");
                }
            }
            else
            {
                if (!saveServer.ProfileExists(senderId) || !saveServer.ProfileExists(targetId))
                    throw new InvalidOperationException("A transfer profile no longer exists");

                foreach (var item in items)
                {
                    if (!templateTable.Items.ContainsKey(item.Template))
                        throw new InvalidOperationException($"Cannot send item with unknown template {item.Template}");
                }

                var senderItems = saveServer.GetProfile(senderId).CharacterData?.PmcData?.Inventory?.Items
                    ?? throw new InvalidOperationException("Sender inventory is unavailable");
                if (items.Count == 0 || items.Any(item => !senderItems.Any(owned => owned.Id == item.Id)))
                    throw new InvalidOperationException("One or more transfer items are no longer in inventory");
                if (rootIds.Any(root => !items.Any(item => item.Id == root)))
                    throw new InvalidOperationException("A transfer root is missing from the snapshot");

                transfer = new SendItemTransferRecord
                {
                    Id = transferId,
                    SenderId = senderId.ToString(),
                    TargetId = targetId.ToString(),
                    SenderNickname = senderNickname,
                    RootItemIds = rootIds.Select(id => id.ToString()).ToArray(),
                    Items = cloner.Clone(items) ?? throw new InvalidOperationException("Could not snapshot transfer items"),
                    StorageSeconds = storageSeconds
                };
                await WriteRecordAsync(transfer, cancellationToken);
            }

            if (transfer.Stage != "Completed")
                await CompleteAsync(transfer, cancellationToken);

            // HTTP 失败原因不明时的重试仍会告知客户端
            // 移除其选定的 roots。日志则防止产生第二封邮件。
            var deleted = output.ProfileChanges[senderId].Items.DeletedItems;
            foreach (var rootId in rootIds)
            {
                if (!deleted.Any(item => item.Id == rootId))
                    deleted.Add(new DeletedItem { Id = rootId });
            }
        }
        finally
        {
            _transferGate.Release();
        }
    }

    private async Task CompleteAsync(SendItemTransferRecord transfer, CancellationToken cancellationToken)
    {
        var senderId = new MongoId(transfer.SenderId);
        var targetId = new MongoId(transfer.TargetId);
        if (!saveServer.ProfileExists(senderId) || !saveServer.ProfileExists(targetId))
            throw new InvalidOperationException($"Transfer {transfer.Id} has a missing profile; recovery stopped");

        if (transfer.Stage == "Prepared")
        {
            var senderPmc = saveServer.GetProfile(senderId).CharacterData?.PmcData
                ?? throw new InvalidOperationException($"Transfer {transfer.Id} has no sender PMC");
            var ids = transfer.Items.Select(item => item.Id).ToHashSet();
            var inventory = senderPmc.Inventory?.Items
                ?? throw new InvalidOperationException($"Transfer {transfer.Id} has no sender inventory");
            var unknownRoots = transfer.RootItemIds.Select(id => new MongoId(id))
                .Where(id => !ids.Contains(id)).ToArray();
            if (unknownRoots.Length > 0)
                throw new InvalidDataException($"Transfer {transfer.Id} journal omitted a root item");
            inventory.RemoveAll(item => ids.Contains(item.Id));
            senderPmc.InsuredItems?.RemoveAll(item => item.ItemId.HasValue && ids.Contains(item.ItemId.Value));
            await saveServer.SaveProfileAsync(senderId, cancellationToken);
            await VerifySourceSavedAsync(transfer, cancellationToken);
            transfer.Stage = "SourceSaved";
            await WriteRecordAsync(transfer, cancellationToken);
        }

        if (transfer.Stage == "SourceSaved")
        {
            var recipient = saveServer.GetProfile(targetId);
            var mailCreatedNow = false;
            if (!HasMail(recipient, transfer.Id) && !HasReceipt(recipient, transfer.Id))
            {
                var items = cloner.Clone(transfer.Items)
                    ?? throw new InvalidOperationException($"Could not clone items for transfer {transfer.Id}");
                var rootParentId = new MongoId();
                items.AdoptOrphanedItems(rootParentId);
                var details = new SendMessageDetails
                {
                    RecipientId = targetId,
                    Sender = MessageType.SystemMessage,
                    MessageText = $"You have received a gift from {transfer.SenderNickname}",
                    Items = items,
                    ItemsMaxStorageLifetimeSeconds = transfer.StorageSeconds,
                    SystemData = new SystemData { Location = MailMarkerPrefix + transfer.Id }
                };
                try
                {
                    mailSendService.SendMessageToPlayer(details);
                    mailCreatedNow = true;
                }
                catch (Exception ex) when (HasMail(recipient, transfer.Id))
                {
                    // SPT 在通知其 websocket 之前就已创建邮件。通知
                    // 失败不得导致附件被重复添加。
                    logger.Warning($"Item transfer {transfer.Id} mail exists but notification failed: {ex.Message}");
                    mailCreatedNow = true;
                }
            }

            var mail = FindMail(recipient, transfer.Id);
            // 此前的尝试可能已经持久化了邮件，且收件人
            // 可能在重放之前就已领取了附件。
            if (mailCreatedNow && (mail?.HasRewards != true || mail.Items?.Data is not { Count: > 0 }))
                throw new InvalidOperationException($"Transfer {transfer.Id} created mail without rewards");
            if (mail is null && !HasReceipt(recipient, transfer.Id))
                throw new InvalidOperationException($"Transfer {transfer.Id} did not create mail");

            // 收件人可以清除邮件。同一 profile 中单独的
            // 回执会保留下来，从而关闭重放窗口。
            recipient.SptData ??= new Spt();
            recipient.SptData.Migrations ??= [];
            recipient.SptData.Migrations.TryAdd(ReceiptPrefix + transfer.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            await saveServer.SaveProfileAsync(targetId, cancellationToken);
            await VerifyMailSavedAsync(transfer, cancellationToken);
            transfer.Stage = "Completed";
            await WriteRecordAsync(transfer, cancellationToken);
        }
    }

    private static Message? FindMail(SptProfile profile, string transferId) =>
        profile.DialogueRecords?.Values.SelectMany(dialog => dialog.Messages ?? [])
            .FirstOrDefault(message => message.SystemData?.Location == MailMarkerPrefix + transferId);

    private static bool HasMail(SptProfile profile, string transferId) => FindMail(profile, transferId) is not null;

    private static bool HasReceipt(SptProfile profile, string transferId) =>
        profile.SptData?.Migrations?.ContainsKey(ReceiptPrefix + transferId) == true;

    private async Task VerifySourceSavedAsync(SendItemTransferRecord transfer, CancellationToken cancellationToken)
    {
        var profile = await ReadSavedProfileAsync(transfer.SenderId, cancellationToken);
        var ids = transfer.Items.Select(item => item.Id).ToHashSet();
        if (profile.CharacterData?.PmcData?.Inventory?.Items?.Any(item => ids.Contains(item.Id)) != false)
            throw new IOException($"Transfer {transfer.Id} source deletion is not persisted");
    }

    private async Task VerifyMailSavedAsync(SendItemTransferRecord transfer, CancellationToken cancellationToken)
    {
        var profile = await ReadSavedProfileAsync(transfer.TargetId, cancellationToken);
        if (!HasMail(profile, transfer.Id) && !HasReceipt(profile, transfer.Id))
            throw new IOException($"Transfer {transfer.Id} recipient mail is not persisted");
    }

    private async Task<SptProfile> ReadSavedProfileAsync(string profileId, CancellationToken cancellationToken) =>
        await jsonUtil.DeserializeFromFileAsync<SptProfile>(Path.Combine("user", "profiles", profileId + ".json"), cancellationToken)
        ?? throw new IOException($"Saved profile {profileId} is unavailable");

    private static string GetTransferId(MongoId senderId, MongoId[] rootIds)
    {
        var key = senderId + ":" + string.Join(",", rootIds.Select(id => id.ToString()).Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }

    private string GetRecordPath(string transferId) => Path.Combine(JournalDirectory, transferId + ".json");

    private async Task<SendItemTransferRecord> ReadRecordAsync(string path, CancellationToken cancellationToken)
    {
        var record = await jsonUtil.DeserializeFromFileAsync<SendItemTransferRecord>(path, cancellationToken)
            ?? throw new InvalidDataException($"Cannot read item transfer journal {path}");
        if (record.Items is not { Count: > 0 } || record.RootItemIds is not { Length: > 0 } ||
            !MongoId.IsValidMongoId(record.SenderId) || !MongoId.IsValidMongoId(record.TargetId) ||
            record.RootItemIds.Any(id => !MongoId.IsValidMongoId(id)) ||
            record.Id != GetTransferId(new MongoId(record.SenderId), record.RootItemIds.Select(id => new MongoId(id)).ToArray()) ||
            record.Id != Path.GetFileNameWithoutExtension(path) ||
            record.Stage is not ("Prepared" or "SourceSaved" or "Completed"))
            throw new InvalidDataException($"Invalid item transfer journal {path}");
        return record;
    }

    private async Task WriteRecordAsync(SendItemTransferRecord record, CancellationToken cancellationToken)
    {
        var path = GetRecordPath(record.Id);
        var tempPath = path + ".tmp";
        var json = jsonUtil.Serialize(record) ?? throw new InvalidOperationException("Could not serialize item transfer");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
