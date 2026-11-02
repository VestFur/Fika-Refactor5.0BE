using SPTarkov.Server.Core.Models.Common;

namespace FikaServer.Models.Fika.Insurance;

public sealed record FikaInsurancePlayer
{
    public required MongoId SessionID { get; init; }
    public bool IsSpectator { get; init; }
    /// <summary>
    /// StartLocalRaid 为该参与者返回的 ServerId 值。
    /// EndLocalRaid 允许在实时 Fika 成员名单已经
    /// 转到另一场比赛之后才到达，因此该值是请求侧唯一
    /// 能把回调与正确的战局快照关联起来的身份标识。
    /// 为一场比赛保留所有值，因为中转战局可能在 Fika 比赛关闭前
    /// 产生不止一个本地战局。
    /// </summary>
    public HashSet<string> RaidTokens { get; init; } = [];
    /// <summary>
    /// 注册本地战局令牌时分配的单调顺序。该
    /// 顺序可让较早中转战局的迟到回调避免覆盖
    /// 较新战局捕获的最终库存。
    /// </summary>
    public Dictionary<string, long> RaidTokenOrder { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> RaidEndAttemptedTokens { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> RaidEndPendingTokens { get; init; } = new(StringComparer.Ordinal);
    public long NextRaidTokenSequence { get; set; }
    public long LastCompletedRaidSequence { get; set; } = -1;
    /// <summary>
    /// 原生战局结束请求已到达 Fika 时为 true，无论是通过
    /// 成功完成还是取消／故障。比赛拆除可能在该
    /// 请求到达之前就开始；将该标记与
    /// <see cref="EndedRaid"/> 分开，可让服务等待迟到的请求，
    /// 而不会把失败的保存当作成功的库存快照。
    /// </summary>
    public bool RaidEndAttempted { get; set; }
    /// <summary>
    /// 原生 EndLocalRaid 任务持有结算槽位时为 true。该
    /// 参与者级标记使完成与故障回调保持幂等。
    /// </summary>
    public bool RaidEndPending { get; set; }
    public bool EndedRaid { get; set; }
    public MongoId[]? InsuredItemsBroughtToRaid { get; set; }
    public MongoId[]? InventoryAfterRaid { get; set; }
}
