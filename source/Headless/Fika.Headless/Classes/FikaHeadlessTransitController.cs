using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Comfort.Common;
using CommonAssets.Scripts.Game;
using EFT;
using EFT.GlobalEvents;
using EFT.Interactive;
using EFT.InventoryLogic;
using Fika.Core;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Communication;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using JsonType;

namespace Fika.Headless.Classes;

/// <summary>
/// 用于 <see cref="GameMode.HeadlessGame"/> 的交运控制器
/// </summary>
public class FikaHeadlessTransitController : TransitController
{
    public FikaHeadlessTransitController(IntPtr pointer) : base(pointer)
    {
    }

    public FikaHeadlessTransitController(TransitGlobalSettings settings, Il2CppReferenceArray<LocationSettings.Location.TransitParameters> parameters,
        LocalRaidSettings localRaidSettings) : base(ClassInjector.DerivedConstructorPointer<FikaHeadlessTransitController>())
    {
        ClassInjector.DerivedConstructorBody(this);
        _server = FikaSingleton<FikaServer>.Instance;
        _playersInTransitZone = [];
        _transittedPlayers = [];
        ClassInjector.InvokeBaseConstructor<TransitController>(this, settings, parameters);
        _localRaidSettings = localRaidSettings;
        IsEvent = localRaidSettings.transitionType.HasFlagNoBox(ELocationTransition.Event);
        // 已访问地点链对所有机器都在同一处推导（FikaTransitHistory），因此专用
        // 主机与可玩主机不会就某次转移历史的各个地图产生分歧。
        var maps = FikaTransitHistory.AppendLocation(localRaidSettings);
        if (TarkovApplication.Exist(out var app) && app.Session?.Profile is { } profile)
        {
            summonedTransits[profile.Id] = new Transit(localRaidSettings.transition.transitionRaidId,
                localRaidSettings.transition.transitionCount, maps, IsEvent);
        }

        TransferItemsController.InitItemControllerServer(FikaGlobals.TransitTraderId, FikaGlobals.TransitTraderName);
        _onHeadlessPlayerEnter = new Action<TransitPoint, Player>(HeadlessOnPlayerEnter);
        _onHeadlessPlayerExit = new Action<TransitPoint, Player>(HeadlessOnPlayerExit);
        OnPlayerEnter += _onHeadlessPlayerEnter;
        OnPlayerExit += _onHeadlessPlayerExit;
    }

    public bool IsEvent { get; }

    public LocalRaidSettings LocalRaidSettings
    {
        get
        {
            return _localRaidSettings;
        }
    }

    private readonly LocalRaidSettings _localRaidSettings;
    private readonly FikaServer _server;
    private readonly Il2CppSystem.Action<TransitPoint, Player> _onHeadlessPlayerEnter;
    private readonly Il2CppSystem.Action<TransitPoint, Player> _onHeadlessPlayerExit;
    private readonly Dictionary<string, int> _playersInTransitZone;
    /// <summary>
    /// “还没到齐”这一回答已对当时交互的玩家报告过的
    /// 点位 id，这样重试不会重复同一行日志。
    /// </summary>
    private readonly HashSet<string> _zoneCheckLogged = [];
    /// <summary>
    /// 已按玩家与点位解释过一次的离区报告；客户端几何兜底重复给出的虚假“离开”
    /// 因此只占一行，而不是每次报告一行。
    /// </summary>
    private readonly HashSet<string> _zoneExitLogged = [];
    private bool _headlessTransit;
    private readonly List<int> _transittedPlayers;
    private string _usedPoint;
    /// <summary>
    /// 本主机已接受其交运交互的每个玩家的 profile id -> 点位 id。严格
    /// 启动门槛会读它（见 <see cref="EveryoneInteracted"/>）：队伍尚未到齐时某玩家的
    /// 按键会被记住，但什么也不注册、也不启动倒计时。
    /// </summary>
    private readonly Dictionary<string, int> _interactedPlayers = [];
    /// <summary>每个玩家与点位只写一行的“已接受交互”日志，这样重试不会重复它。</summary>
    private readonly HashSet<string> _interactionLogged = [];
    /// <summary>本主机已为其推送过转移的玩家；避免各个驱动重复发送。</summary>
    private readonly HashSet<int> _transferIssued = [];
    /// <summary>
    /// 上一轮转移中某个已注册玩家为何没拿到转移，每个玩家一条记录；
    /// 倒计时结束时的报告会打印它（与可玩主机的账本对应）。
    /// </summary>
    private readonly List<string> _transferFailures = [];
    /// <summary>当前倒计时所服务的点位，或 -1。</summary>
    private int _countdownPointId = -1;
    /// <summary>运行中的倒计时归零时的非缩放时间。</summary>
    private float _countdownEndsAt;
    /// <summary>运行中倒计时的长度（秒，取该点位自身的时长）。</summary>
    private int _countdownSeconds;
    /// <summary>最后一次广播的秒数，这样每秒只有一个包、一行日志。</summary>
    private int _lastBroadcastSecond = -1;
    /// <summary>倒计时代号：上一场突袭的旧异步时钟会在下一步停下。</summary>
    private int _countdownGeneration;
    /// <summary>当本控制器的倒计时时钟运行时为 true。</summary>
    private bool _countdownLoopRunning;
    /// <summary>最近一次原生 Transit 调用的 hash、keys 与玩家数，供本主机自己发出的转移复用。</summary>
    private string _lastTransitHash;
    private Il2CppSystem.Collections.Generic.Dictionary<string, ProfileKey> _lastTransitKeys;
    private int _lastTransitPlayersCount;
    /// <summary>与可玩主机每场突袭的交互角色表对应，这样重试不会重复一行日志。</summary>
    private readonly HashSet<string> _gateLogged = [];
    /// <summary>运行中倒计时最后一次门槛复查的日志行，这样每次结论变化只出现一行。</summary>
    private string _recheckLoggedSignature;
    /// <summary>运行中倒计时的门槛复查次数，供取消时的日志行使用。</summary>
    private int _recheckCount;
    /// <summary>本场突袭中失败的门槛复查次数，最多只报告几次。</summary>
    private int _recheckFailures;

    public int AliveTransitPlayers
    {
        get
        {
            return _transittedPlayers.Count;
        }
    }

    private void HeadlessOnPlayerEnter(TransitPoint point, Player player)
    {
#if DEBUG
        FikaGlobals.LogInfo($"{player.Profile.Info.Nickname} entered transit point {point.Description}");
#endif

        if (!TryGetAccessToLocation(player, point.parameters.id, out _))
        {
#if DEBUG
            FikaGlobals.LogWarning("Player is not eligible for this transit point");
#endif
            // 入口路径的提前退出，按每个玩家点名（上面那行 DEBUG 在发行版中不会
            // 被构建）：没有权限时该玩家无法启动任何倒计时。
            return;
        }

        // 通过共享辅助记录，好让 headless 日志与由玩家主办突袭时的措辞一致，
        // 并让去重表同时覆盖两者。
        TransitZonePresence.RecordInZone(_playersInTransitZone, player.ProfileId, point.parameters.id, "player entered the zone");

        if (!transitPlayers.ContainsKey(player.ProfileId))
        {
            var fikaPlayer = player.TryCast<FikaPlayer>();
            if (fikaPlayer != null)
            {
                fikaPlayer.UpdateBtrTraderServiceData().HandleExceptions();
            }

            TransitEventPacket packet = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Interaction,
                TransitEvent = new TransitInteractionEvent()
                {
                    PlayerRaidId = player.RaidId,
                    PointId = point.parameters.id,
                    Type = TransitInteractionEvent.EType.Show
                }
            };

            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            return;
        }

        pointsById[point.parameters.id].GroupEnter(player);
    }

    private bool TryGetAccessToLocation(Player player, int pointId, out string keyId)
    {
        keyId = string.Empty;
        if (!TarkovApplication.Exist(out var application)) return false;
        var session = application.Session;
        var locations = session?.TryCast<ClientBackendSession>()?.LocationSettings?.locations;
        if (locations == null || !pointsById.TryGetValue(pointId, out var point) ||
            !locations.TryGetValue(point.parameters.target, out var destination) || destination == null)
            return false;

        var requirements = destination.GetAccessRequirements(session.GameMode,
            LocationSettings.Location.EAccessUsageType.Transit);
        if (requirements == null || requirements.Count == 0) return true;

        if (player.Side == EPlayerSide.Savage)
        {
            return false;
        }

        var profile = player.Profile;
        var items = profile.Inventory.GetPlayerItems(EPlayerItems.Equipment);
        if (!LocationAccessRequirementsUtility.TryFindSatisfiedRequirement(items, requirements, new Func<Item, bool>(profile.Examined),
            out var item, out _, out _) || item == null)
        {
            return false;
        }

        keyId = item.Id;
        return true;
    }

    /// <summary>
    /// 应用来自客户端的区域报告。headless 控制器同样用
    /// <see cref="CheckForPlayers"/> 为其交运设门槛，同样的不对称依然成立：原生触发器只
    /// 驱动本地回调，因此客户端上的玩家即便人就在点位里，
    /// 也可能缺失于区域映射中。
    /// </summary>
    internal void OnRemotePlayerZonePresence(string profileId, int pointId,
        TransitZonePresencePacket.ETransitZonePresenceKind kind)
    {
        if (string.IsNullOrEmpty(profileId))
        {
            return;
        }
        if (kind == TransitZonePresencePacket.ETransitZonePresenceKind.Left)
        {
            // 即使没有点位 id 也适用：客户端的死亡路径只知道它自己的 profile。
            // 该包不携带原因，而客户端侧的各个发送者也并不等价，因此在丢弃条目之前
            // 会先在本机确认这次移除——见
            // OnRemotePlayerLeftZone。
            OnRemotePlayerLeftZone(profileId, pointId);
            return;
        }

        if (!pointsById.TryGetValue(pointId, out _))
        {
            return;
        }

        var reason = kind == TransitZonePresencePacket.ETransitZonePresenceKind.In
            ? "client reported entering the zone" : "client interaction fallback";
        // RecordInZone 恰在该 profile 此前未登记在此点位时返回 true，因此下面的投递
        // 每次进入只发生一次，重复的报告无法再次发送它。
        if (!TransitZonePresence.RecordInZone(_playersInTransitZone, profileId, pointId, reason)) return;
        SendInteractionShow(profileId, pointId, reason);
    }

    /// <summary>
    /// 与可玩主机的 <c>FikaHostTransitController.SendInteractionShow</c> 对应：交运
    /// 提示是权威的，而远程玩家永远不会触达本机的原生 <c>PlayerEnter</c>
    /// （专用主机也没有本地提示可退而依赖），因此必须在该玩家的到场报告被接受的
    /// 那一刻，以 <c>TransitInteractionEvent{Show}</c> 的形式把提示推给客户端。
    /// 没有它，客户端永远看不到提示，也就永远不会发出交互请求。该包的
    /// 构造与可玩主机的远程分支完全一致；此处只面向远程玩家——
    /// headless 机器自己的玩家没有可接收它的客户端。
    /// </summary>
    private void SendInteractionShow(string profileId, int pointId, string reason)
    {
        var player = FindHumanPlayer(profileId);
        if (player == null)
        {
                        return;
        }
        if (player.IsYourPlayer) return;
        if (transitPlayers.ContainsKey(profileId)) return;

        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.Interaction,
            TransitEvent = new TransitInteractionEvent()
            {
                PlayerRaidId = player.RaidId,
                PointId = pointId,
                Type = TransitInteractionEvent.EType.Show
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// 某客户端报告其玩家离开了交运区域。与可玩主机的检查对应
    /// （<c>FikaHostTransitController.OnRemotePlayerLeftZone</c>）：该报告不会被照单全收，
    /// 因为真正走出触发器、死亡、撤离以及客户端 0.25 秒的几何
    /// 兜底都会以同一个“离开”到达，而只有最后一种会在玩家仍站在点位里时
    /// 判断错误。玩家必须因本机可确认的某个原因而确实不在——死亡、
    /// 已撤离、已被转移，或不在该点位自身的触发器体积内。确认后的
    /// 移除保留既有的原因字符串与日志格式。
    /// </summary>
    private void OnRemotePlayerLeftZone(string profileId, int reportedPointId)
    {
        if (!_playersInTransitZone.TryGetValue(profileId, out var registeredPoint))
        {
            return;
        }

        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        var player = FindHumanPlayer(profileId);
        var alive = player != null && player.HealthController?.IsAlive == true;
        var transited = player != null && _transittedPlayers.Contains(player.RaidId);
        var extracted = player != null && coopHandler != null && coopHandler.ExtractedPlayers.Contains(player.NetId);
        var inside = alive && !transited && !extracted && IsInsidePointBounds(registeredPoint, player);

        var verdict = player == null ? "player is no longer in this raid"
            : !alive ? "player is not alive"
            : transited ? "player already transferred"
            : extracted ? "player already extracted"
            : inside ? "still inside the point (spurious report)"
            : "outside the point";

        var detail = player == null ? "player=none" : DescribePlayerPlacement(registeredPoint, player);

        if (inside)
        {
            // 只报告第一次；客户端的兜底可能重复同一结论。
            if (_zoneExitLogged.Add(profileId + "@" + registeredPoint))
            {
            }
            return;
        }

        TransitZonePresence.RemoveFromZone(_playersInTransitZone, profileId, "client left the zone");
    }

    /// <summary>与 <see cref="PruneStaleZoneRegistrations"/> 内联执行的同一查找。</summary>
    private FikaPlayer FindHumanPlayer(string profileId)
    {
        var players = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler?.HumanPlayers;
        if (players == null) return null;
        foreach (var candidate in players)
        {
            if (candidate != null && string.Equals(candidate.ProfileId, profileId, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private TransitPoint FindPoint(int pointId) =>
        pointsById != null && pointsById.TryGetValue(pointId, out var point) ? point : null;

    /// <summary>
    /// 玩家是否仍站在该点位，按该点位拥有的每一个碰撞体判定。只检测
    /// 找到的第一个碰撞体会重现本检查正是要捕捉的那种误判
    /// （触发器体积可能挂在该点位的子物体上），而命中任一碰撞体都计入——这是
    /// 触发器体积的超集，也是否决方向上的安全选择，因为它只可能保留一个
    /// 本主机已经计入的注册。
    /// </summary>
    private bool IsInsidePointBounds(int pointId, Player player)
    {
        if (player == null) return false;
        var point = FindPoint(pointId);
        if (point == null) return false;

        var position = player.Position;
        var colliders = point.GetComponentsInChildren<UnityEngine.Collider>(true);
        if (colliders != null)
        {
            for (var i = 0; i < colliders.Count; i++)
            {
                var collider = colliders[i];
                if (collider != null && collider.bounds.Contains(position)) return true;
            }
        }

        return false;
    }

    /// <summary>玩家位置加点位锚点，这样几何结论可以从日志中重新推导。</summary>
    private string DescribePlayerPlacement(int pointId, Player player)
    {
        var position = player.Position;
        var builder = new System.Text.StringBuilder();
        builder.Append("pos=(").Append(position.x.ToString("F1")).Append(',')
            .Append(position.y.ToString("F1")).Append(',').Append(position.z.ToString("F1")).Append(')');

        var point = FindPoint(pointId);
        if (point == null)
        {
            builder.Append(" point=unavailable");
            return builder.ToString();
        }

        var anchor = point.transform.position;
        builder.Append(" point=(").Append(anchor.x.ToString("F1")).Append(',')
            .Append(anchor.y.ToString("F1")).Append(',').Append(anchor.z.ToString("F1")).Append(')')
            .Append(" distance=").Append(UnityEngine.Vector3.Distance(position, anchor).ToString("F1"))
            .Append(" colliders=").Append(point.GetComponentsInChildren<UnityEngine.Collider>(true)?.Count ?? 0);
        return builder.ToString();
    }

    private void HeadlessOnPlayerExit(TransitPoint point, Player player)
    {
#if DEBUG
        FikaGlobals.LogInfo($"{player.Profile.Info.Nickname} left transit point {point.Description}");
#endif

        // 这次离开是否会终结整组的转移，在下面才决定，但这个答案必须在此处
        // 移除注册之前先读出来。
        var registeredForThisPoint = transitPlayers != null &&
            transitPlayers.TryGetValue(player.ProfileId, out var registeredPoint) &&
            // (2026-10-27 W42 / T2-B) transitPlayers 有两种取值形状：原生与 RegisterTransitParticipant 写 raid id，
            // 历史路径写 point id。此前只认 point id，"已注册的玩家"被读成"没注册"，取消倒计时时谁都不放。
            (registeredPoint == point.parameters.id || registeredPoint == player.RaidId);

        if (_playersInTransitZone.TryGetValue(player.ProfileId, out var value))
        {
            if (value == point.parameters.id)
            {
                _playersInTransitZone.Remove(player.ProfileId);
            }
        }

        if (registeredForThisPoint && _countdownPointId == point.parameters.id &&
            TransitCountdownGuard.AbortWhenSomeoneLeaves)
        {
            // 一个已注册玩家在点位倒计时运行期间离开了点位（用户需求 1）：整个
            // 转移被取消，而取消会为他应用这次离开的原生那一半处理。
            CancelCountdown("a registered teammate left the point",
                " player=" + player.ProfileId + " " + DescribePlayerPlacement(point.parameters.id, player));
            return;
        }

        if (transitPlayers.ContainsKey(player.ProfileId))
        {
            point.GroupExit(player);
            // 丢弃原生分组会停掉那个把该玩家送出突袭的倒计时；同位置的
            // 退出会让触发器翻转并在此处显现。
        }

        // 与可玩主机的 OnHostPlayerExit 规则相同：玩家在转移尚未开始时离开
        // 点位，已接受的交互会被撤销（包括封禁），否则他在这场突袭中再也无法
        // 撤离。转移开始之后才发生的离开会保留封禁，正是它阻止
        // 未完成的交运被变成一个普通退出。专用主机没有本地
        // 玩家，因此这里对原生退出钩子报告的任何人执行。
        if (_transittedPlayers.Contains(player.RaidId))
        {
        }
        else
        {
            var wasRegistered = transitPlayers.Remove(player.ProfileId);
            ReleaseTransitBanFor(player.RaidId, player.ProfileId,
                $"player left the point (transitRegistrationRemoved={wasRegistered})");
        }

        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.Interaction,
            TransitEvent = new TransitInteractionEvent()
            {
                PlayerRaidId = player.RaidId,
                PointId = point.parameters.id,
                Type = TransitInteractionEvent.EType.Hide
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    public override void InactivePointNotification(int playerId, int pointId)
    {
        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.Interaction,
            TransitEvent = new TransitInteractionEvent()
            {
                PlayerRaidId = playerId,
                PointId = pointId,
                Type = TransitInteractionEvent.EType.InactivePoint
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    public override void Sizes(int pointId, Il2CppSystem.Collections.Generic.Dictionary<int, byte> sizes)
    {
        _currentTransitPointId = pointId;
        // 与可玩主机的 Sizes 对应：原生字典是该点位自身的账目
        // （针对点位各玩家分组的分组大小），而远程玩家只能通过
        // InteractWithTransit 进入它——所以它宣告的是交互请求已被接受的
        // 玩家，而读取 _playersInTransitZone 的 CheckForPlayers 统计的是所有
        // 站在点位里的玩家。这些数字由同一张映射、用本控制器
        // CheckForPlayers 所使用的谓词重建。此处不写入任何原生账目。
        var shown = BuildZoneSizes(pointId, sizes);

        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.GroupSize,
            TransitEvent = new TransitGroupSizeEvent()
            {
                PointId = pointId,
                Sizes = shown
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// 本控制器视作“站在点位里”的玩家：与
    /// <see cref="CheckForPlayers"/> 所用的同一谓词（存活、不是 headless 机器自己的玩家、尚未
    /// 转移、尚未撤离），从同一张映射读取——因此宣告出去的分组大小不会与
    /// 决定交运能否启动的那道门槛产生分歧。
    /// </summary>
    private List<FikaPlayer> CollectZonePlayers(int pointId)
    {
        var result = new List<FikaPlayer>();
        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        var players = coopHandler?.HumanPlayers;
        if (players == null) return result;

        foreach (var member in players)
        {
            if (member == null || member.HealthController?.IsAlive != true) continue;
            if (member.IsYourPlayer) continue;
            if (TransitDropouts.IsDropped(member)) continue;
            if (_transittedPlayers.Contains(member.RaidId)) continue;
            if (coopHandler.ExtractedPlayers.Contains(member.NetId)) continue;
            if (!_playersInTransitZone.TryGetValue(member.ProfileId, out var zone) || zone != pointId) continue;
            result.Add(member);
        }

        return result;
    }

    /// <summary>
    /// 用观测到的区内数量替换原生分组大小数字，每个位于该点位中的玩家各一次，
    /// 这样每台机器都能在字典中找到自己的 raid id，并都显示
    /// 同一个数字。什么都没观测到时，原生字典原样传递，
    /// 它定义的槽位也被保留，因此本映射无法解释的显示永远不会消失。
    /// </summary>
    private Il2CppSystem.Collections.Generic.Dictionary<int, byte> BuildZoneSizes(int pointId,
        Il2CppSystem.Collections.Generic.Dictionary<int, byte> nativeSizes)
    {
        var inZone = CollectZonePlayers(pointId);
        if (inZone.Count == 0) return nativeSizes;

        var value = (byte)System.Math.Clamp(inZone.Count, 0, byte.MaxValue);
        var result = new Il2CppSystem.Collections.Generic.Dictionary<int, byte>();
        foreach (var member in inZone)
        {
            if (!result.ContainsKey(member.RaidId)) result.Add(member.RaidId, value);
        }

        if (nativeSizes != null)
        {
            foreach (var entry in nativeSizes)
            {
                if (!result.ContainsKey(entry.Key)) result.Add(entry.Key, value);
            }
        }

        return result;
    }

    /// <summary>
    /// 站在某点位中的队友，形状符合共享启动门槛辅助的预期。本控制器的收集器
    /// 返回具体的 Fika 类型，而该辅助在两个主机上都作用于 EFT.Player。
    /// </summary>
    private List<Player> ZonePlayersAsPlayers(int pointId)
    {
        var result = new List<Player>();
        foreach (var member in CollectZonePlayers(pointId))
        {
            if (member != null) result.Add(member);
        }

        return result;
    }

    /// <summary>供诊断使用的 raid-id 到计数映射的紧凑视图。</summary>
    private static string DescribeSizes(Il2CppSystem.Collections.Generic.Dictionary<int, byte> sizes)
    {
        if (sizes == null) return "null";
        var builder = new System.Text.StringBuilder();
        foreach (var entry in sizes)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(entry.Key).Append('=').Append(entry.Value);
        }
        return builder.Length == 0 ? "empty" : builder.ToString();
    }

    /// <summary>
    /// 原生数字过去所依据的原生点位账目：哪个 profile 属于
    /// 哪个分组，以及每个分组的大小。用于判断远程玩家是否真的在本机上
    /// 进入过该点位自身的分组。
    /// </summary>
    private string DescribePointGroups(int pointId)
    {
        var point = FindPoint(pointId);
        if (point == null) return "point-unknown";

        var builder = new System.Text.StringBuilder();
        foreach (var entry in point._playerGroups)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(entry.Key).Append("->").Append(entry.Value);
        }

        builder.Append(" size[");
        if (point._groupSize != null)
        {
            foreach (var entry in point._groupSize)
            {
                builder.Append(entry.Key).Append('=').Append(entry.Value).Append(';');
            }
        }

        // 该点位的原生倒计时（分组 -> 剩余秒数）与已确认集合：一个分组的原生转移
        // 正是由这两部分构成的。
        builder.Append("] timers[");
        if (point._groupTimers != null)
        {
            foreach (var entry in point._groupTimers)
            {
                builder.Append(entry.Key).Append('=').Append(entry.Value).Append(';');
            }
        }

        builder.Append("] confirmed[");
        if (point._confirmedPlayers != null)
        {
            foreach (var profile in point._confirmedPlayers)
            {
                builder.Append(profile).Append(';');
            }
        }

        builder.Append("] enabled=").Append(point.Enabled).Append(" active=").Append(point.IsActive);
        return builder.ToString();
    }

    /// <summary>
    /// 原生定时器通知，在转发给客户端时多一步：凡是原生字典未携带的玩家，
    /// 都会得到一个以其 raid id 为键的条目，因为两块定时器面板都按 raid id
    /// 查找本地玩家，而原生账目只描述本地分组
    /// （见 <c>TransitStartGate.BuildTimers</c> 与可玩主机中对应的方法）。
    /// </summary>
    public override void Timers(int pointId, Il2CppSystem.Collections.Generic.Dictionary<int, ushort> timers)
    {
        try
        {
            if (_countdownPointId == pointId)
            {
                HeadlessCountdownTick();
                return;
            }

            var shown = TransitStartGate.BuildTimers(ZonePlayersAsPlayers(pointId), 0, timers);

            TransitEventPacket packet = new()
            {
                EventType = TransitEventPacket.ETransitEventType.GroupTimer,
                TransitEvent = new TransitGroupTimerEvent()
                {
                    PointId = pointId,
                    Timers = shown
                }
            };

            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }
        catch (System.Exception exception)
        {
                    }

        HeadlessCountdownTick();
    }

    /// <summary>
    /// 本机仍计入门槛与面板的队友：存活、尚未
    /// 转移、尚未撤离，也不是它自己的玩家（专用主机没有它自己的
    /// 交运入口）。<see cref="CheckForPlayers"/> 与 <see cref="CollectZonePlayers"/> 使用同一谓词。
    /// </summary>
    private List<Player> EligibleMembers()
    {
        var result = new List<Player>();
        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        var players = coopHandler?.HumanPlayers;
        if (players == null) return result;

        foreach (var member in players)
        {
            if (member == null || member.HealthController?.IsAlive != true) continue;
            if (member.IsYourPlayer) continue;
            if (TransitDropouts.IsDropped(member)) continue;
            if (_transittedPlayers.Contains(member.RaidId)) continue;
            if (coopHandler.ExtractedPlayers.Contains(member.NetId)) continue;
            result.Add(member);
        }

        return result;
    }

    /// <summary>记录一次已接受的交互；下面的严格门槛需要它作为该玩家的凭据。</summary>
    private void RecordInteraction(Player player, int pointId)
    {
        _interactedPlayers[player.ProfileId] = pointId;
        if (!_interactionLogged.Add(player.ProfileId + "@" + pointId)) return;
            }

    /// <summary>点位自身的“等待全队”标志，仅供诊断读取。</summary>
    private string ReadWaitForAllParty(int pointId)
    {
        var point = FindPoint(pointId);
        return point == null ? "point-unknown" : point.parameters.waitForAllParty.ToString();
    }

    /// <summary>严格启动门槛：每个合格队友都在此点位交互过，并且身在其中。</summary>
    private bool EveryoneInteracted(Player player, int pointId)
    {
        if (!TransitStartGate.RequireAllTeammatesInteracted) return true;

        var members = EligibleMembers();
        var missing = TransitStartGate.DescribeNotReady(members, _playersInTransitZone, _interactedPlayers, pointId, out var ready);
        if (missing == null)
        {
                        return true;
        }

        if (_gateLogged.Add(pointId + ":" + player.ProfileId + ":interaction"))
        {
                    }

        NotifyTeammatesNotReady(player);
        return false;
    }

    /// <summary>告知等候中的玩家倒计时尚未启动（只有一条消息，这里没有本地玩家）。</summary>
    private void NotifyTeammatesNotReady(Player player)
    {
        try
        {
            Il2CppSystem.Collections.Generic.Dictionary<int, TransitMessagesEvent.EType> messages = new();
            messages.Add(player.RaidId, TransitMessagesEvent.EType.NonAllTeammates);
            TransitEventPacket messagePacket = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Messages,
                TransitEvent = new TransitMessagesEvent()
                {
                    Messages = messages
                }
            };

            _server.SendData(ref messagePacket, DeliveryMethod.ReliableOrdered);
        }
        catch (System.Exception exception)
        {
                    }
    }

    /// <summary>交互账本的紧凑视图，每个 profile 一条，并标出当前点位。</summary>
    private string DescribeInteractions(int pointId)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var entry in _interactedPlayers)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(entry.Key).Append('=').Append(entry.Value);
            if (entry.Value == pointId) builder.Append('*');
        }
        return builder.Length == 0 ? "empty" : builder.ToString();
    }

    /// <summary>某点位倒计时所代表的队友，以 profile/raid-id 对表示。</summary>
    private string DescribeParticipants(int pointId)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var member in CollectZonePlayers(pointId))
        {
            if (member == null) continue;
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(member.ProfileId).Append('/').Append(member.RaidId);
        }
        return builder.Length == 0 ? "none" : builder.ToString();
    }

    /// <summary>本主机的原生交运注册表。</summary>
    private string DescribeNativeRegistry()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var entry in transitPlayers)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(entry.Key).Append('=').Append(entry.Value);
        }
        return builder.Length == 0 ? "empty" : builder.ToString();
    }

    /// <summary>
    /// 注册站在该点位中的每个队友，并向各自的控制器回以它等待的
    /// Confirm。只有在两道门槛都通过后才执行，因此倒计时对整组同时启动。
    /// </summary>
    private bool RegisterTransitParticipants(TransitPoint point, Player interacting, string keyId, EDateTime time)
    {
        var pointId = point.parameters.id;
        var members = EligibleMembers();
        var eligible = 0;
        var registered = 0;
        foreach (var member in members)
        {
            if (member == null) continue;
            if (!_playersInTransitZone.TryGetValue(member.ProfileId, out var zone) || zone != pointId) continue;
            eligible++;
            var preferredKey = member.ProfileId == interacting.ProfileId ? keyId : null;
            if (RegisterTransitParticipant(member, point, preferredKey, time)) registered++;
        }

        if (registered == 0)
        {
                        return false;
        }

                // 该注册会激活点位：其自身位置上的出口在转移完成之前被移出
        // 交互，这样两者不会争抢同一次按键。
        TransitCoLocatedExits.Suppress(point, $"the point registered {registered} player(s)");
        return true;
    }

    /// <summary>注册单个玩家，带守卫：某个队友失败绝不会影响其他队友或本次调用。</summary>
    private bool RegisterTransitParticipant(Player member, TransitPoint point, string preferredKeyId, EDateTime time)
    {
        var pointId = point.parameters.id;
        var profileId = member.ProfileId;
        if (transitPlayers.ContainsKey(profileId))
        {
                        return false;
        }

        var keyId = preferredKeyId;
        try
        {
            if (string.IsNullOrEmpty(keyId)) TryGetAccessToLocation(member, pointId, out keyId);
        }
        catch (System.Exception exception)
        {
                        keyId = null;
        }

        try
        {
            profileTime[profileId] = time;
            transitPlayers[profileId] = member.RaidId;
            profileKeys[profileId] = keyId;
            point.GroupEnter(member);
            ExfiltrationController.Instance.BannedPlayers.Add(member.RaidId);
            ExfiltrationController.Instance.CancelExtractionForPlayer(member);
            TransitEventPacket confirm = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Interaction,
                TransitEvent = new TransitInteractionEvent()
                {
                    PlayerRaidId = member.RaidId,
                    PointId = pointId,
                    Type = TransitInteractionEvent.EType.Confirm
                }
            };

            _server.SendData(ref confirm, DeliveryMethod.ReliableOrdered);
        }
        catch (System.Exception exception)
        {
                        ReleaseTransitBanFor(member.RaidId, profileId, "the registration threw; this player does not take part");
            return false;
        }

                return true;
    }

    /// <summary>
    /// 启动门槛已通过的点位的倒计时：时长取自该点位，广播则来自本控制器
    /// 自己的时钟（专用主机没有可依赖的、由玩家驱动的原生 tick）。
    /// </summary>
    private void BeginCountdown(TransitPoint point)
    {
        var seconds = TransitStartGate.CountdownSeconds(point);
        _countdownPointId = point.parameters.id;
        _countdownSeconds = seconds;
        _countdownEndsAt = Time.unscaledTime + seconds;
        _lastBroadcastSecond = -1;
        _recheckLoggedSignature = null;
        _recheckCount = 0;
        _countdownGeneration++;
                BroadcastCountdown(true);
        StartCountdownLoop();
    }

    /// <summary>倒计时时钟的一步；每次原生 <see cref="Timers"/> 调用也会调用它。</summary>
    private void HeadlessCountdownTick()
    {
        try
        {
            if (_countdownPointId < 0) return;

            // 规则的连续那一半：参与者列表在专用主机时钟的每一次 tick 上都会
            // 重新检查，而不只在倒计时启动时检查。
            if (RecheckCountdownParticipants()) return;

            var remaining = System.Math.Max(0, (int)System.Math.Ceiling(_countdownEndsAt - Time.unscaledTime));
            if (remaining != _lastBroadcastSecond)
            {
                _lastBroadcastSecond = remaining;
                BroadcastCountdown(false);
            }

            if (remaining <= 0) FinishCountdown();
        }
        catch (System.Exception exception)
        {
                        AbortCountdown("the countdown tick threw");
        }
    }

    /// <summary>
    /// 本控制器的时钟：在游戏更新线程上每 250 ms 走一步，随倒计时一起停止。
    /// 专用主机没有它自己驱动的 GameWorld.Update 轮询，因此这个循环由这里
    /// 拥有；代号机制让上一场突袭的一步成为空操作。
    /// </summary>
    private void StartCountdownLoop()
    {
        if (_countdownLoopRunning) return;
        _countdownLoopRunning = true;
        var generation = _countdownGeneration;
        MainThreadDispatcher.RunAsync(async () =>
        {
            try
            {
                while (_countdownGeneration == generation && _countdownPointId >= 0)
                {
                    await Task.Delay(250);
                    MainThreadDispatcher.Post(() =>
                    {
                        if (_countdownGeneration != generation) return;
                        HeadlessCountdownTick();
                    });
                }
            }
            catch (System.Exception exception)
            {
                            }
            finally
            {
                _countdownLoopRunning = false;
            }
        }).Forget();
    }

    /// <summary>把运行中的倒计时通告给每个客户端，每步一个分组定时器包。</summary>
    private void BroadcastCountdown(bool force)
    {
        var pointId = _countdownPointId;
        if (pointId < 0) return;

        var remaining = System.Math.Max(0, (int)System.Math.Ceiling(_countdownEndsAt - Time.unscaledTime));
        if (force) _lastBroadcastSecond = remaining;
        try
        {
            var timers = TransitStartGate.BuildTimers(ZonePlayersAsPlayers(pointId), remaining, null);
            TransitEventPacket packet = new()
            {
                EventType = TransitEventPacket.ETransitEventType.GroupTimer,
                TransitEvent = new TransitGroupTimerEvent()
                {
                    PointId = pointId,
                    Timers = timers
                }
            };

            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
                    }
        catch (System.Exception exception)
        {
                    }
    }

    /// <summary>倒计时归零：每个已注册客户端都得到一次转移，每人最多一次。</summary>
    private void FinishCountdown()
    {
        var pointId = _countdownPointId;
        _countdownPointId = -1;
        _lastBroadcastSecond = -1;
        _countdownGeneration++;
        var issued = IssueTransferForClients(pointId, "countdown end");
        var failures = _transferFailures.Count == 0 ? "none" : string.Join(";", _transferFailures);
            }

    /// <summary>
    /// 停止一个失败的运行中倒计时（tick 抛出异常），并把它注册过的玩家们的退出
    /// 交互还回去。它使用与取消相同的释放流程，因此一次中止同样不会留下注册、
    /// 原生分组归属或交运封禁。
    /// </summary>
    private void AbortCountdown(string reason)
    {
        var pointId = _countdownPointId;
        _countdownPointId = -1;
        _lastBroadcastSecond = -1;
        _recheckLoggedSignature = null;
        _countdownGeneration++;
                var released = ReleaseParticipantsOfCancelledCountdown(pointId, "countdown aborted: " + reason);
        var suppressed = TransitCoLocatedExits.Release("the countdown of point " + pointId + " was aborted: " + reason);
            }

    /// <summary>
    /// 本机为某个点位注册过的 profile——即运行中倒计时的参与者列表，
    /// 从原生注册表读取（与 <see cref="IssueTransferForClients"/> 所需的注册表相同，因此
    /// 两侧谈的是同一个集合）。与可玩主机同名方法对应。
    /// </summary>
    private List<(string ProfileId, int RaidId)> RegisteredParticipants(int pointId)
    {
        var result = new List<(string ProfileId, int RaidId)>();
        try
        {
            foreach (var entry in transitPlayers)
            {
                // (2026-10-27 W42) 同上：raid id 形状也要算进来，否则取消倒计时时列表恒空。
                if (entry.Value == pointId) { result.Add((entry.Key, entry.Value)); }
                else
                {
                    var owner = FindHumanPlayer(entry.Key);
                    if (owner != null && entry.Value == owner.RaidId) { result.Add((entry.Key, owner.RaidId)); }
                }
            }
        }
        catch (System.Exception exception)
        {
                    }

        return result;
    }

    /// <summary>当该玩家已从本场突袭撤离时为 true。</summary>
    private bool IsExtractedFromThisRaid(Player member)
    {
        try
        {
            var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
            return member != null && coopHandler != null && member.TryCast<FikaPlayer>() is { } fikaPlayer &&
                coopHandler.ExtractedPlayers.Contains(fikaPlayer.NetId);
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 启动规则的连续那一半（用户需求 1）：某点位倒计时运行期间，它的
    /// 参与者列表在时钟的每一次 tick 上重新检查，而不是只在启动时检查一次。
    /// 结论来自 <see cref="TransitCountdownGuard.Classify"/>，每次结论变化都会写一行
    /// 判据日志。倒计时被取消时返回 true，即调用者不得继续
    /// 本次 tick；它绝不抛出，因为一个坏掉的检查不能弄坏它所观察的转移。
    /// </summary>
    private bool RecheckCountdownParticipants()
    {
        var pointId = _countdownPointId;
        if (pointId < 0) return false;

        var members = 0;
        var inside = new List<string>();
        var missing = new List<string>();
        var left = new List<string>();
        var dead = new List<string>();
        var dropped = new List<string>();
        var gone = new List<string>();
        var toDrop = new List<string>();
        string cancelReason = null;
        string cancelDetail = null;
        var verdict = "continue";

        try
        {
            var participants = RegisteredParticipants(pointId);
            members = participants.Count;
            foreach (var entry in participants)
            {
                var profileId = entry.ProfileId;
                var player = FindHumanPlayer(profileId);
                var inRaid = player != null && !TransitDropouts.IsDropped(player);
                var alive = player != null && player.HealthController?.IsAlive == true;
                var goneFromRaid = player != null &&
                    (_transittedPlayers.Contains(player.RaidId) || IsExtractedFromThisRaid(player));
                var insidePoint = player != null && alive && !goneFromRaid && IsInsidePointBounds(pointId, player);
                var zoneEntryMatches = _playersInTransitZone.TryGetValue(profileId, out var zone) && zone == pointId;
                var state = TransitCountdownGuard.Classify(inRaid, alive, goneFromRaid, insidePoint, zoneEntryMatches);

                switch (state)
                {
                    case ETransitMemberState.Inside:
                        inside.Add(profileId);
                        break;
                    case ETransitMemberState.Missing:
                        // 仍在点位内，只是本主机的区域报告缺失：该条目会
                        // 从点位自身的碰撞体恢复，而不是丢弃一个确实在那里的玩家。
                        inside.Add(profileId);
                        missing.Add(profileId);
                        TransitZonePresence.RecordInZone(_playersInTransitZone, profileId, pointId,
                            "gate recheck: inside the point although this host had no zone report for him");
                        break;
                    case ETransitMemberState.Left:
                        left.Add(profileId + "(" + DescribePlayerPlacement(pointId, player) + ")");
                        if (TransitCountdownGuard.AbortWhenSomeoneLeaves)
                        {
                            cancelReason ??= "a registered teammate left the point";
                            cancelDetail = " player=" + profileId + " " + DescribePlayerPlacement(pointId, player);
                        }

                        break;
                    case ETransitMemberState.Dead:
                        dead.Add(profileId);
                        if (TransitCountdownGuard.AbortWhenSomeoneDies)
                        {
                            cancelReason ??= "a registered teammate died";
                            cancelDetail = " player=" + profileId + " " + DescribePlayerPlacement(pointId, player);
                        }
                        else
                        {
                            toDrop.Add(profileId);
                        }

                        break;
                    default:
                        if (inRaid) gone.Add(profileId);
                        else dropped.Add(profileId);
                        toDrop.Add(profileId);
                        break;
                }
            }

            verdict = cancelReason != null ? "cancel(" + cancelReason + ")" : "continue";
        }
        catch (System.Exception exception)
        {
            _recheckFailures++;
            if (_recheckFailures <= 3)
            {
                            }

            return false;
        }

        _recheckCount++;
        var line = TransitCountdownGuard.DescribeRecheck(pointId, members, inside, missing, left, dead, dropped, gone, verdict);
        if (!string.Equals(line, _recheckLoggedSignature, System.StringComparison.Ordinal))
        {
            _recheckLoggedSignature = line;
            FikaGlobals.LogWarning(line);
        }

        if (cancelReason != null)
        {
            CancelCountdown(cancelReason, (cancelDetail ?? string.Empty) +
                $" (checked {_recheckCount}x, switches: {TransitCountdownGuard.DescribeSwitches()})");
            return true;
        }

        foreach (var profileId in toDrop)
        {
            DropCountdownParticipant(pointId, profileId, true, "no longer a participant in this raid");
        }

        if (members > 0 && toDrop.Count >= members)
        {
            // 所有已注册队友都已不在：这次转移已无人可载。
            CancelCountdown("no registered teammate is left in this raid",
                $" (checked {_recheckCount}x, switches: {TransitCountdownGuard.DescribeSwitches()})");
            return true;
        }

        return false;
    }

    /// <summary>
    /// 从运行中的倒计时里移除一个参与者而不取消它：注册、该点位的原生
    /// 分组归属，以及——对尚未处于离开途中的玩家——他的交运封禁。
    /// 用于已死亡（死亡开关关闭时）的队友，以及每个离开本场突袭的队友，
    /// 这正是用户需求 3 的缺陷修复。
    /// </summary>
    private bool DropCountdownParticipant(int pointId, string profileId, bool releaseBan, string reason)
    {
        var point = FindPoint(pointId);
        var player = FindHumanPlayer(profileId);
        var raidId = player == null ? -1 : player.RaidId;
        var removed = false;
        try
        {
            if (transitPlayers.TryGetValue(profileId, out var registeredRaidId)) raidId = registeredRaidId;
            removed = transitPlayers.Remove(profileId);
        }
        catch (System.Exception exception)
        {
                    }

        _interactedPlayers.Remove(profileId);
        var groupExited = false;
        if (player != null && point != null)
        {
            try
            {
                point.GroupExit(player);
                point._confirmedPlayers.Remove(profileId);
                groupExited = true;
            }
            catch (System.Exception exception)
            {
                            }
        }

        if (releaseBan && raidId >= 0 && !_transittedPlayers.Contains(raidId))
        {
            ReleaseTransitBanFor(raidId, profileId, reason);
        }

                return removed || groupExited;
    }

    /// <summary>
    /// 结束某点位的倒计时以及它正在准备的转移（用户需求 1），与可玩主机
    /// 同名方法对应：每个已注册玩家都离开该点位的原生分组
    /// 并失去其已确认标志，原生注册表与交互账本把他移除，他的交运封禁
    /// 解除，该点位位置上的出口重新响应。每个客户端都会被告知——这让
    /// 它自己的控制器丢弃本地注册、重新启用出口并关闭它的倒计时面板。
    /// 专用主机没有面板，也没有它自己的本地玩家。
    /// </summary>
    private void CancelCountdown(string reason, string detail)
    {
        var pointId = _countdownPointId;
        if (pointId < 0) return;

        _countdownPointId = -1;
        _lastBroadcastSecond = -1;
        _recheckLoggedSignature = null;
        _countdownGeneration++;

                var released = ReleaseParticipantsOfCancelledCountdown(pointId, "countdown cancelled: " + reason);
        var suppressed = TransitCoLocatedExits.Release("the countdown of point " + pointId + " was cancelled: " + reason);
        var banned = ExfiltrationController.Instance == null ? -1 : ExfiltrationController.Instance.BannedPlayers.Count;
            }

    /// <summary>
    /// 释放被取消的倒计时所注册的每个玩家：原生分组归属（经由原生退出路径
    /// 所用的同一个 <c>point.GroupExit</c>）、已确认标志、原生注册表条目、他的
    /// 交运封禁，以及告知客户端在本地做同样处理的消息。返回确认日志行中的
    /// 细节片段。
    /// </summary>
    private string ReleaseParticipantsOfCancelledCountdown(int pointId, string reason)
    {
        var point = FindPoint(pointId);
        var released = 0;
        var groupsExited = 0;
        var clientsTold = 0;
        var failed = new System.Text.StringBuilder();

        foreach (var entry in RegisteredParticipants(pointId))
        {
            var profileId = entry.ProfileId;
            var raidId = entry.RaidId;
            var player = FindHumanPlayer(profileId);
            if (player != null && point != null)
            {
                try
                {
                    point.GroupExit(player);
                    point._confirmedPlayers.Remove(profileId);
                    groupsExited++;
                }
                catch (System.Exception exception)
                {
                    if (failed.Length > 0) failed.Append(';');
                    failed.Append(profileId).Append(":GroupExit=").Append(exception.GetType().Name);
                }
            }

            try
            {
                transitPlayers.Remove(profileId);
            }
            catch (System.Exception)
            {
            }

            _interactedPlayers.Remove(profileId);
            var raidIdForPacket = player == null ? raidId : player.RaidId;
            SendTransitInteraction(raidIdForPacket, pointId, TransitInteractionEvent.EType.Hide);
            NotifyTransitCancelled(raidIdForPacket, profileId);
            clientsTold++;

            if (!_transittedPlayers.Contains(raidId))
            {
                ReleaseTransitBanFor(raidId, profileId, reason);
            }

            released++;
        }

        return $"participants={released} nativeGroupsExited={groupsExited} remoteClientsTold={clientsTold} failed=[{failed}]";
    }

    /// <summary>本主机发给所有客户端的一次原生交互事件（已取消倒计时的 Hide）。</summary>
    private void SendTransitInteraction(int playerRaidId, int pointId, TransitInteractionEvent.EType type)
    {
        try
        {
            TransitEventPacket packet = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Interaction,
                TransitEvent = new TransitInteractionEvent()
                {
                    PlayerRaidId = playerRaidId,
                    PointId = pointId,
                    Type = type
                }
            };

            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }
        catch (System.Exception exception)
        {
                    }
    }

    /// <summary>
    /// 告知某个客户端他所在点位的转移已被取消，所用的消息与原生区域检查
    /// 表达同一事实时相同（“并非所有队友都在点位上”）。
    /// </summary>
    private void NotifyTransitCancelled(int playerRaidId, string profileId)
    {
        try
        {
            Il2CppSystem.Collections.Generic.Dictionary<int, TransitMessagesEvent.EType> messages = new();
            messages.Add(playerRaidId, TransitMessagesEvent.EType.NonAllTeammates);
            TransitEventPacket messagePacket = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Messages,
                TransitEvent = new TransitMessagesEvent()
                {
                    Messages = messages
                }
            };

            _server.SendData(ref messagePacket, DeliveryMethod.ReliableOrdered);
        }
        catch (System.Exception exception)
        {
                    }
    }

    /// <summary>
    /// 把转移发送给为该点位注册的每个远程队友。原生 tick 可能已经
    /// 驱动了其中一些人（它自己的调用会进入 <see cref="Transit"/>），而已发出集合让
    /// 本方法不会把同一个玩家发送两次。
    /// </summary>
    private int IssueTransferForClients(int pointId, string source)
    {
        var point = FindPoint(pointId);
        if (point == null)
        {
                        return 0;
        }

        _transferFailures.Clear();
        var issued = 0;
        var eligible = 0;
        foreach (var member in EligibleMembers())
        {
            if (member == null) continue;
            eligible++;
            // (2026-10-27 W42 / T2-B) 两种形状都接受，否则"客机先按"已注册的玩家会被跳过、且带着 ban 留在旧局。
            if (!transitPlayers.TryGetValue(member.ProfileId, out var registeredPoint) ||
                (registeredPoint != pointId && registeredPoint != member.RaidId))
            {
                _transferFailures.Add($"{member.ProfileId}=not registered for point {pointId}");
                                continue;
            }

            if (IssueClientTransfer(member, point, source)) issued++;
            else _transferFailures.Add($"{member.ProfileId}=not sent, see the line above");
        }

                return issued;
    }

    /// <summary>向单个客户端发送它的交运，带守卫且幂等（见可玩主机中对应的方法）。</summary>
    private bool IssueClientTransfer(Player member, TransitPoint point, string source)
    {
        if (!_transferIssued.Add(member.RaidId))
        {
                        return false;
        }

        try
        {
            if (!EnsureSummonedTransit(member.ProfileId))
            {
                                _transferIssued.Remove(member.RaidId);
                return false;
            }

            var destination = BuildDestination(member, point, _lastTransitHash, _lastTransitKeys,
                _lastTransitPlayersCount > 0 ? _lastTransitPlayersCount : CountRegisteredParticipants(point.parameters.id));
            alreadyTransits[member.ProfileId] = destination;
            _transittedPlayers.Add(member.RaidId);
            TransitEventPacket packet = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Extract,
                PlayerId = member.RaidId,
                TransitId = point.parameters.id,
                Destination = destination
            };

            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
                        return true;
        }
        catch (System.Exception exception)
        {
            _transferIssued.Remove(member.RaidId);
                        return false;
        }
    }

    /// <summary>本主机视作已为该点位注册的玩家，用作转移包中的玩家数。</summary>
    private int CountRegisteredParticipants(int pointId)
    {
        var count = 0;
        try
        {
            foreach (var entry in transitPlayers)
            {
                // (2026-10-27 W42) 同上：否则转移包的 player count 恒为 1。
                if (entry.Value == pointId) { count++; }
                else
                {
                    var owner = FindHumanPlayer(entry.Key);
                    if (owner != null && entry.Value == owner.RaidId) { count++; }
                }
            }
        }
        catch (System.Exception)
        {
            return 1;
        }

        return count > 0 ? count : 1;
    }

    /// <summary>为某个本主机尚无交运历史的 profile 创建它。</summary>
    private bool EnsureSummonedTransit(string profileId)
    {
        try
        {
            if (summonedTransits.ContainsKey(profileId)) return true;
            summonedTransits[profileId] = new Transit(_localRaidSettings.transition.transitionRaidId,
                _localRaidSettings.transition.transitionCount, FikaTransitHistory.AppendLocation(_localRaidSettings), IsEvent);
                        return true;
        }
        catch (System.Exception exception)
        {
                        return false;
        }
    }

    /// <summary>某个玩家转移的目的地（与可玩主机构建器的对应实现）。</summary>
    private LocationTransit BuildDestination(Player player, TransitPoint point, string hash,
        Il2CppSystem.Collections.Generic.Dictionary<string, ProfileKey> keys, int playersCount)
    {
        var profiles = new Il2CppSystem.Collections.Generic.Dictionary<string, ProfileKey>();
        if (keys != null)
        {
            foreach (var entry in keys)
            {
                if (!profiles.ContainsKey(entry.Key)) profiles.Add(entry.Key, entry.Value);
            }
        }

        var location = TransitDestination.Resolve(point, out var destinationSource);
        if (destinationSource != "parameters.location")
        {
                    }

        return new LocationTransit
        {
            hash = hash ?? string.Empty,
            playersCount = playersCount,
            ip = string.Empty,
            location = location,
            profiles = profiles,
            transitionRaidId = summonedTransits.TryGetValue(player.ProfileId, out var history)
                ? history.raidId : _localRaidSettings.transition.transitionRaidId,
            raidMode = ERaidMode.Local,
            side = player.Side == EPlayerSide.Savage ? ESideType.Savage : ESideType.Pmc,
            dayTime = profileTime.TryGetValue(player.ProfileId, out var time) ? time : _localRaidSettings.timeVariant
        };
    }

    public override void InteractWithTransit(Player player, InteractWithTransitPacket packet)
    {
        // 在改动交运状态之前，先符合主机的原生资格检查。
        // 远程请求可能在某个点位已被禁用之后到达，
        // 也可能携带过时/不匹配的 key 与时间选择。
        // 自愈：能到达本控制器的交互请求就证明发送者
        // 正站在该点位上，因此也为他注册。此后的所有环节
        // （权限、进入条件、已完成任务、分组大小门槛）均不变，因此这条
        // 兜底不会移动一个身处他处的玩家。
        if (player != null && packet != null && packet.hasInteraction &&
            player.HealthController?.IsAlive == true && pointsById.TryGetValue(packet.pointId, out _))
        {
            if (!_playersInTransitZone.TryGetValue(player.ProfileId, out var currentPoint) || currentPoint != packet.pointId)
            {
                TransitZonePresence.RecordInZone(_playersInTransitZone, player.ProfileId, packet.pointId,
                    "interact request at the point");
            }
        }

        if (player == null || packet == null || !packet.hasInteraction ||
            player.HealthController?.IsAlive != true ||
            !pointsById.TryGetValue(packet.pointId, out var point) || !point.Enabled ||
            transitPlayers.ContainsKey(player.ProfileId) ||
            !TryGetAccessToLocation(player, packet.pointId, out var keyId) ||
            !EFT.ClientTransitController.HasMetEntryRequirements(point, player) ||
            (!string.IsNullOrEmpty(point.parameters.completedQuestId) &&
             !new TransitCompletedQuestRequirement(point.parameters.completedQuestId).Met(player)))
        {
                        return;
        }

        if (_countdownPointId == packet.pointId)
        {
            // 整组已到点位且倒计时正在运行；重复的按键不得再启动第二个倒计时。
            FikaGlobals.LogWarning($"[TransitDiag] InteractWithTransit ignored for {player.ProfileId}: the countdown of point " +
                $"{packet.pointId} is already running ({_countdownSeconds}s in total)");
            return;
        }


        // 先记住这次按键：严格门槛需要它作为该玩家的凭据，而在整队都
        // 完成各自动作并站在点位内之前，什么都不会注册。
        RecordInteraction(player, packet.pointId);
        if (!CheckForPlayers(player, packet.pointId)) return;
        if (!EveryoneInteracted(player, packet.pointId)) return;
        if (!RegisterTransitParticipants(point, player, keyId, packet.time)) return;

        BeginCountdown(point);
    }

    private bool CheckForPlayers(Player player, int pointId)
    {
        // 已死亡、已撤离与已转移的玩家都被下面两个循环排除，因此他们的
        // 区域条目只会让映射变暗；在此丢弃它们。
        PruneStaleZoneRegistrations();

        var humanPlayers = 0;
        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        if (coopHandler == null)
        {
            FikaGlobals.LogError("Cannot validate headless transit without a CoopHandler");
            return false;
        }

        foreach (var fikaPlayer in coopHandler.HumanPlayers)
        {
            if (fikaPlayer.HealthController?.IsAlive == true &&
                !fikaPlayer.IsYourPlayer &&
                !TransitDropouts.IsDropped(fikaPlayer) &&
                !_transittedPlayers.Contains(fikaPlayer.RaidId) &&
                !coopHandler.ExtractedPlayers.Contains(fikaPlayer.NetId))
            {
                humanPlayers++;
            }
        }

        var playersInPoint = 0;
        foreach (var fikaPlayer in coopHandler.HumanPlayers)
        {
            var inPoint = _playersInTransitZone.TryGetValue(fikaPlayer.ProfileId, out var zone) && zone == pointId;
            if (fikaPlayer.HealthController?.IsAlive == true &&
                !fikaPlayer.IsYourPlayer &&
                !TransitDropouts.IsDropped(fikaPlayer) &&
                !_transittedPlayers.Contains(fikaPlayer.RaidId) &&
                !coopHandler.ExtractedPlayers.Contains(fikaPlayer.NetId) && inPoint)
            {
                playersInPoint++;
            }
        }

        if (playersInPoint < humanPlayers)
        {
            if (_zoneCheckLogged.Add(pointId + ":" + player.ProfileId))
            {
                            }

            Il2CppSystem.Collections.Generic.Dictionary<int, TransitMessagesEvent.EType> messages = new();
            messages.Add(player.RaidId, TransitMessagesEvent.EType.NonAllTeammates);

            TransitEventPacket messagePacket = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Messages,
                TransitEvent = new TransitMessagesEvent()
                {
                    Messages = messages
                }
            };

            _server.SendData(ref messagePacket, DeliveryMethod.ReliableOrdered);
            return false;
        }

                return true;
    }

    /// <summary>供诊断使用的区域映射紧凑视图：每个已知玩家一条。</summary>
    private string DescribeZoneMap(int pointId)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var entry in _playersInTransitZone)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(entry.Key).Append("=").Append(entry.Value);
            if (entry.Value == pointId) builder.Append('*');
        }
        return builder.Length == 0 ? "empty" : builder.ToString();
    }

    /// <summary>丢弃那些再也不可能转移的玩家的注册。</summary>
    private void PruneStaleZoneRegistrations()
    {
        if (_playersInTransitZone.Count == 0) return;
        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        if (coopHandler == null) return;

        List<string> stale = null;
        foreach (var entry in _playersInTransitZone)
        {
            FikaPlayer member = null;
            foreach (var candidate in coopHandler.HumanPlayers)
            {
                if (candidate != null && string.Equals(candidate.ProfileId, entry.Key, StringComparison.Ordinal))
                {
                    member = candidate;
                    break;
                }
            }

            // 已不在 HumanPlayers 中：该 profile 离开了突袭，上面两个循环都不会
            // 统计它，因此该条目可以留到突袭结束。
            if (member == null) continue;
            if (TransitDropouts.IsDropped(member) ||
                member.HealthController?.IsAlive != true ||
                _transittedPlayers.Contains(member.RaidId) ||
                coopHandler.ExtractedPlayers.Contains(member.NetId))
            {
                stale ??= [];
                stale.Add(entry.Key);
            }
        }

        if (stale == null) return;
        foreach (var profileId in stale)
        {
            TransitZonePresence.RemoveFromZone(_playersInTransitZone, profileId, "dead, extracted or already transferred");
        }
    }

    /// <summary>
    /// 本场突袭的原生转移入口，外加本主机自己发出的一切。原生
    /// 调用只携带它知道的玩家；本控制器的倒计时结束覆盖其余玩家，而
    /// 已发出集合防止一个玩家被转移两次。
    /// </summary>
    public override Il2CppSystem.Threading.Tasks.Task Transit(TransitPoint point, int playersCount, string hash,
        Il2CppSystem.Collections.Generic.Dictionary<string, ProfileKey> keys, Player player)
    {
        var callDestination = TransitDestination.Resolve(point, out var destinationSource);
                if (point == null || player == null)
        {
                        return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        }

        if (_transferIssued.Contains(player.RaidId))
        {
                                    return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        }

        // 保留原生参数，供本主机自己发出的转移使用。
        _lastTransitHash = hash;
        _lastTransitKeys = keys;
        _lastTransitPlayersCount = playersCount;

        if (keys == null || !keys.ContainsKey(player.ProfileId))
        {
            // 原生调用可能携带另一分组的 key 集合；转移本身并不依赖
            // 本机的条目（客户端会对照自己的 profile 校验目的地），因此
            // 这里选择报告，而不是丢弃该玩家。
            FikaGlobals.LogWarning($"[TransitDiag] Transit for {player.ProfileId}: the native key set did not contain this profile");
        }

        if (!pointsById.TryGetValue(point.parameters.id, out var registeredPoint) ||
            !registeredPoint.Enabled || _transittedPlayers.Contains(player.RaidId))
        {
                        ReleaseTransitBanFor(player.RaidId, player.ProfileId,
                "transit did not start (invalid request, disabled point or already transferred)");
            return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        }

        if (!EnsureSummonedTransit(player.ProfileId))
        {
                                    ReleaseTransitBanFor(player.RaidId, player.ProfileId,
                "transit did not start (no transit history for this profile)");
            return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        }

        var destination = BuildDestination(player, point, hash, keys, playersCount);
        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.Extract,
            PlayerId = player.RaidId,
            TransitId = point.parameters.id,
            Destination = destination
        };

        alreadyTransits[player.ProfileId] = destination;
        _transittedPlayers.Add(player.RaidId);
        _transferIssued.Add(player.RaidId);
                _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);

        if (!_headlessTransit)
        {
            ExtractHeadlessClient(point, destination);
        }

                return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
    }

    private void ExtractHeadlessClient(TransitPoint point, LocationTransit destination)
    {
        _headlessTransit = true;
        _usedPoint = point.parameters.name;

        if (TarkovApplication.Exist(out var tarkovApplication))
        {
            // 专用主机没有本地玩家的交运条目。对下一场突袭沿用
            // 第一个远程玩家经校验的目的地。
            tarkovApplication.TransitionStatus = new TransitionStatus(destination.location, false,
                destination.side, destination.raidMode, destination.dayTime);
        }

        FikaBackendUtils.IsTransit = true;
        // 在跨交运等待检查原生玩家状态期间，把每个续延都保持在
        // Unity 的更新线程上。
        MainThreadDispatcher.RunAsync(DelayHeadlessExtract).Forget();
    }

    private async Task DelayHeadlessExtract()
    {
        var game = FikaSingleton<IFikaGame>.Instance;
        var usedPoint = _usedPoint;
        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        if (coopHandler == null)
        {
            FikaGlobals.LogError("CoopHandler was null, quitting after 3 seconds");
            await Task.Delay(3000);
        }
        else
        {
            while (coopHandler.HumanPlayers.Exists(player =>
                !player.IsYourPlayer && player.HealthController?.IsAlive == true &&
                !_transittedPlayers.Contains(player.RaidId) &&
                !coopHandler.ExtractedPlayers.Contains(player.NetId)))
            {
                await Task.Delay(1000);
            }
        }

        // Task.Delay 在线程池线程上恢复。游戏销毁会触碰 Unity 与
        // 原生 EFT 对象，因此要把它交回游戏更新线程。上一场突袭
        // 延迟到达的完成不得停掉更新的突袭。
        MainThreadDispatcher.Post(() =>
        {
            if (ReferenceEquals(FikaSingleton<IFikaGame>.Instance, game))
            {
                game?.Stop(string.Empty, ExitStatus.Transit, usedPoint);
                return;
            }

            // 本该结束的那场突袭已不再是正在运行的那场，因此本场突袭为转移
            // 注册过的玩家仍在一场突袭中，而封禁让他们无法撤离：
            // 为他们逐人解除，与可玩主机对待自己的玩家完全一样。
            ReleaseTransitBansOfThisRaid("headless raid was replaced before its transit stop");
        });
    }

    /// <summary>
    /// 解除一次已接受交互所施加的交运封禁（见 <see cref="InteractWithTransit"/>），用于
    /// 转移最终并不会发生的情形。与可玩主机的
    /// <c>FikaHostTransitController.ReleaseTransitBanFor</c> 对应（本控制器是原生
    /// <see cref="TransitController"/>，因此状态位于共享的 <c>ExfiltrationController</c> 上）。
    /// <para>
    /// 接受路径会把玩家加入 <c>ExfiltrationController.BannedPlayers</c> 并取消他的
    /// 撤离，而唯一的原生解除只在离开途中经由
    /// <c>TransitController.EnableExits(player)</c> 执行——因此一次被中止的交运会
    /// 让该玩家在本场突袭余下的时间里无法撤离。
    /// </para>
    /// 幂等：移除一个未被封禁的 id 不会改变任何东西，而退出交互只会在
    /// 不再有任何被封禁玩家时重新启用，因此仍在等待自己交运的玩家
    /// 绝不会被解除阻塞。
    /// </summary>
    private void ReleaseTransitBanFor(int raidId, string profileId, string reason)
    {
        var exfiltration = ExfiltrationController.Instance;
        if (exfiltration?.BannedPlayers == null)
        {
                        return;
        }

        var removed = exfiltration.BannedPlayers.Remove(raidId);
        var remaining = exfiltration.BannedPlayers.Count;
        if (remaining == 0)
        {
            // 只有集合为空时才重新开放出口；其他玩家的封禁依然生效。
            exfiltration.EnableExitsInteraction();
            // 不再有人等待交运，因此该点位上的出口也重新响应。
            TransitCoLocatedExits.Release("no player waits for a transit any more");
        }

        var who = string.IsNullOrEmpty(profileId) ? $"raidId={raidId}" : $"{profileId} (raidId={raidId})";
            }

    /// <summary>
    /// 解除本场突袭为转移注册过的每个玩家的交运封禁。用于本该结束的那场
    /// 突袭已不再是正在运行的那场的情形：它的玩家仍在一场突袭中，而封禁
    /// 会让他们永远无法撤离。
    /// </summary>
    private void ReleaseTransitBansOfThisRaid(string reason)
    {
        if (_transittedPlayers.Count == 0) return;
        foreach (var raidId in _transittedPlayers)
        {
            ReleaseTransitBanFor(raidId, ProfileIdForRaidId(raidId), reason);
        }
    }

    /// <summary>本场突袭中拥有某个 raid id 的 profile，该玩家已不在时为 null。</summary>
    private string ProfileIdForRaidId(int raidId)
    {
        var players = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler?.HumanPlayers;
        if (players == null) return null;
        foreach (var candidate in players)
        {
            if (candidate != null && candidate.RaidId == raidId) return candidate.ProfileId;
        }

        return null;
    }

    public override void Dispose()
    {
        OnPlayerEnter -= _onHeadlessPlayerEnter;
        OnPlayerExit -= _onHeadlessPlayerExit;
        _zoneExitLogged.Clear();
        _gateLogged.Clear();
        _interactedPlayers.Clear();
        _interactionLogged.Clear();
        _transferIssued.Clear();
        _countdownPointId = -1;
        _countdownEndsAt = 0f;
        _countdownSeconds = 0;
        _lastBroadcastSecond = -1;
        _recheckLoggedSignature = null;
        _recheckCount = 0;
        TransitDropouts.Forget();
        // 停掉旧突袭的时钟：它的下一步会看到另一个代号并直接返回。
        _countdownGeneration++;
        _lastTransitHash = null;
        _lastTransitKeys = null;
        _lastTransitPlayersCount = 0;
        // 把点位的出口交互还回去（已销毁的点位跳过），然后关闭账本：
        // 下一场突袭从空的抑制列表开始。
        TransitCoLocatedExits.Release("the raid ended");
        TransitCoLocatedExits.Forget();
        _transferFailures.Clear();
    }

    public void Init()
    {
        // 新的突袭：已结束世界的任何东西都不得留存在这条链的静态成员中。WorldLifecycle 的
        // 销毁序列也会清空两个账本（TransitCoLocatedExits 与 TransitDropouts
        // 为此暴露了 ForceResetForWorldTeardown）；这些调用属于自愈的那一半，还覆盖了
        // 从未到达本类的销毁流程，而且它们完全不触碰任何原生对象。
        TransitCoLocatedExits.ForceResetForWorldTeardown("a new raid starts");
        TransitDropouts.ForceResetForWorldTeardown("a new raid starts");
        // 为 dropout 注册表供数的那唯一一个订阅每个进程只挂载一次（幂等）。
        TransitDropouts.Arm();
        EnablePoints(true);
    }
}

