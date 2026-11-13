using FikaServer.Models.Enums;
using SPTarkov.Server.Core.Models.Common;
using System.Diagnostics.CodeAnalysis;

namespace FikaServer.Models.Fika.Headless;

/// <summary>
/// 包含 headless 客户端数据的记录
/// </summary>
public record HeadlessClientInfo
{
    [SetsRequiredMembers]
    public HeadlessClientInfo(System.Net.WebSockets.WebSocket webSocket, EHeadlessStatus state)
    {
        WebSocket = webSocket;
        State = state;
    }

    /// <summary>
    /// headless 客户端的 WebSocket
    /// </summary>
    public required System.Net.WebSockets.WebSocket WebSocket { get; set; }
    /// <summary>
    /// headless 客户端的状态
    /// </summary>
    public required EHeadlessStatus State { get; set; }
    /// <summary>
    /// 旧连接正在被替换,而它的对局仍在清理中。
    /// 在新 socket 装载完成之前,
    /// 不要把它提供给另一个请求方。
    /// </summary>
    public bool IsReconnecting { get; set; }
    /// <summary>
    /// 只有当 headless 完成其菜单/profile 生命周期,
    /// 并通过当前 websocket 明确报告就绪之后才为 true。
    /// </summary>
    public bool ClientReady { get; private set; }
    /// <summary>
    /// 为该 profile 串行化预留与连接替换。
    /// </summary>
    public SemaphoreSlim LifecycleGate { get; } = new(1, 1);
    /// <summary>
    /// 每当这个 headless 被预留用于新的 raid 时改变。用于区分
    /// 同一玩家连续两次请求。
    /// </summary>
    public long ReservationVersion { get; private set; }
    /// <summary>
    /// Headless 启动请求的线路令牌。每次预留都会签发
    /// 一个新值,因此来自更旧 raid 的延迟创建会被拒绝。
    /// </summary>
    public string? ReservationToken { get; private set; }
    /// <summary>
    /// 刚刚结束的那个预留令牌。它会一直可用,以供
    /// 客户端在 raid 结束后的就绪握手使用,之后只有在新的预留开始
    /// 或替换 socket 装载完成时才会被清除。在就绪之后仍保留它,
    /// 可以防止一条迟到的空令牌报告冒充
    /// 本连接的初始就绪消息。
    /// </summary>
    public string? LastEndedReservationToken { get; private set; }
    /// <summary>
    /// 一条就绪消息可能与服务器的最后一次 /raid/leave 清理竞争。
    /// 在 Reset 能安全地再次发布该 headless 之前,保留它。
    /// </summary>
    public string? PendingReadyToken { get; private set; }
    /// <summary>
    /// 正在这个 headless 客户端上游玩的玩家,仅当状态为 IN_RAID 时设置
    /// </summary>
    public List<MongoId> Players { get; set; } = [];
    /// <summary>
    /// 请求该 headless 客户端者的 SessionID,仅当状态为 IN_RAID 时才会设置
    /// </summary>
    public string? RequesterSessionID { get; set; }
    /// <summary>
    /// 用于检查是否已通过请求方的 WebSocket 通知其「对局已开始」,以便其自动加入
    /// </summary>
    public bool? HasNotifiedRequester { get; set; }
    /// <summary>
    /// headless 当前的关卡,主要用于转发
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    /// 在启动 raid 之后设置客户端的信息
    /// </summary>
    /// <param name="requestId">请求该 raid 的 id</param>
    public void StartRaid(string requestId)
    {
        ReservationVersion++;
        ReservationToken = Guid.NewGuid().ToString("N");
        LastEndedReservationToken = null;
        PendingReadyToken = null;
        ClientReady = false;
        RequesterSessionID = requestId;
        HasNotifiedRequester = false;
    }

    /// <summary>
    /// 重置 <see cref="HeadlessClientInfo"/> 的数据
    /// </summary>
    public void Reset()
    {
        LastEndedReservationToken = ReservationToken;
        ReservationVersion++;
        ReservationToken = null;
        State = EHeadlessStatus.READY;
        Players.Clear();
        RequesterSessionID = null;
        HasNotifiedRequester = null;
        ClientReady = PendingReadyToken != null &&
            string.Equals(PendingReadyToken, LastEndedReservationToken, StringComparison.Ordinal);
        PendingReadyToken = null;
    }

    /// <summary>
    /// 记录来自本连接的就绪报告。调用方必须已经检查过
    /// socket 归属并持有客户端锁。
    /// </summary>
    public bool TryMarkReady(string? reservationToken)
    {
        if (ReservationToken != null)
        {
            if (string.Equals(ReservationToken, reservationToken, StringComparison.Ordinal))
                PendingReadyToken = reservationToken;
            return false;
        }

        if (State != EHeadlessStatus.READY || RequesterSessionID != null ||
            (LastEndedReservationToken == null
                ? reservationToken != null
                : !string.Equals(LastEndedReservationToken, reservationToken, StringComparison.Ordinal)))
            return false;

        ClientReady = true;
        PendingReadyToken = null;
        return true;
    }
}
