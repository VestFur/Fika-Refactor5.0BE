using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace FikaServer.Models.Fika.SendItem;

// 在任一 profile 被更改之前写入。保留已完成的记录，使携带相同
// 背包根节点的 HTTP 重试能够返回原始结果。
public sealed record SendItemTransferRecord
{
    public required string Id { get; init; }
    public required string SenderId { get; init; }
    public required string TargetId { get; init; }
    public required string SenderNickname { get; init; }
    public required string[] RootItemIds { get; init; }
    public required List<Item> Items { get; init; }
    public required long StorageSeconds { get; init; }
    public string Stage { get; set; } = "Prepared";
}
