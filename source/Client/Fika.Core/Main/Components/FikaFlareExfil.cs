using CommonAssets.Scripts.Game;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.GameTriggers;
using EFT.GlobalEvents;
using EFT.Interactive;
using EFT.Interactive.SecretExfiltrations;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Communication;
using System;
using System.Collections.Generic;

namespace Fika.Core.Main.Components;

/// <summary>
/// 绿色（"ExitActivate"）信号弹撤离点的激活信息，与发射者一同
/// 经由网络传输。
/// <para>
/// 原生模型（已对照 47888 interop 成员导出验证，见
/// analysis/flare-exfil-verification）：绿色信号弹即 <c>FlareEventType.ExitActivate</c>；其
/// 成功会在本地触发 <c>FlareSuccessEvent</c>，而发射者所处的每一个
/// <see cref="FlareShootDetectorZone"/> 都会把发射玩家的
/// profile id 记录到 <c>_shotPlayerProfiles</c> 中。随后该撤离点会针对每个玩家通过
/// <c>ExfiltrationRequirement.Met(Player, ExfiltrationPoint)</c> 给出判定。原版还有一条小队
/// 通道用于同样的记录（<c>EZoneEventType.PlayerByPartyAddedInShotList</c>），这也
/// 就是此处的默认行为。
/// </para>
/// <para>
/// Fika 原先缺失的环节：客户端的信号弹从未在主机上重放那部分本地逻辑，因此
/// 客户端的信号弹没有激活任何东西，而 Fika 确实复制了的撤离点级状态又使
/// 该撤离点对从未发射过的对等端可用。此类让激活随发射者一起传递
/// （默认情况下还包括小队），维护由主机权威决定的允许列表，并对
/// 授权未覆盖的每个玩家拒绝撤离。
/// </para>
/// <para>
/// 绿色信号弹在原版中有两项职责，这里将它们分开处理：开启一个出口
/// （"exit-gate"，即海关的信号弹出口）与禁用地图狙击手（"immunity"，Ground Zero 的
/// <c>Sniper_exit</c>，其出口带有 <c>PassageRequirement: None</c>，而整个
/// <c>/EXITS/FlareFireCollector_zone</c> 区域反而是被狙击火力封锁的）。免疫区域
/// 从不触碰出口状态：被覆盖玩家的战局 id 会写入每台机器上的原生
/// <c>PlayersWithImmuneToSniperFire</c> 集合，而狙击区域正是通过
/// <c>IsPlayerImmuneForFire(Int32)</c> 读取该集合。
/// </para>
/// <para>
/// 开关：<c>Flare Extract: Squad Shares Activation</c>（默认开启 = 原版小队通道）。
/// 关闭时激活只绑定到发射者本人。
/// </para>
/// </summary>
public static class FikaFlareExfil
{
    /// <summary>与上报的信号弹位置相距超过此值时，主机不接受该撤离点。</summary>
    private const float PointRange = 120f;

    /// <summary>当检测区上没有碰撞体时，信号弹位置将与其原点进行比较。</summary>
    private const float ZoneRange = 150f;

    /// <summary>中途加入的玩家与丢失的数据包会在下一个 tick 补上当前的授权。</summary>
    private const float RepublishInterval = 10f;

    /// <summary>此类所做的每次状态变更都会写入该签名，以便原生侧能够归属来源。</summary>
    private const string NativeSignature = "FikaFlareExfil";

    private static readonly object Sync = new();

    /// <summary>撤离点名 -&gt; 允许使用它的 profile。在主机上写入，在客户端由授权镜像而来。</summary>
    private static readonly Dictionary<string, HashSet<string>> AllowedProfilesByPoint = new(StringComparer.Ordinal);

    /// <summary>本次战局中至少收到一次信号弹激活的撤离点。只有这些才会被门禁限制。</summary>
    private static readonly HashSet<string> GatedPoints = new(StringComparer.Ordinal);

    /// <summary>主机签发授权时的原样记录，用于重新发布。</summary>
    private static readonly List<GrantRecord> Grants = [];

    /// <summary>逐行诊断去重：每个玩家、撤离点与事件的组合只输出一行。</summary>
    private static readonly HashSet<string> LoggedLines = new(StringComparer.Ordinal);

    /// <summary>战局绑定各撤离点时的状态，此时尚未运行任何 Fika 复制。</summary>
    private static readonly Dictionary<string, EExfiltrationStatus> InitialStatuses = new(StringComparer.Ordinal);

    /// <summary>被绿色信号弹检测区覆盖、且初始需要手动激活的撤离点。</summary>
    private static readonly HashSet<string> ClassifiedGated = new(StringComparer.Ordinal);

    /// <summary>出口名 -> 激活它的绿色信号弹（ExitActivate）检测区。</summary>
    private static readonly Dictionary<string, string> FlareZoneByPoint = new(StringComparer.Ordinal);

    /// <summary>绿色信号弹检测区 id -> 它所激活的出口，在战局绑定出口时解析。</summary>
    private static readonly Dictionary<string, string> PointByFlareZone = new(StringComparer.Ordinal);

    /// <summary>出口名 -> 到其绿色信号弹检测区的距离，用于每战局只构建一次的表格。</summary>
    private static readonly Dictionary<string, float> FlareZoneDistanceByPoint = new(StringComparer.Ordinal);

    /// <summary>
    /// 绿色信号弹检测区 id -> 该区信号弹所执行的工作（开启出口或禁用狙击手）。
    /// 由分类流程每战局填充一次，因此授权路径不必再从场景中
    /// 重新推导结论。
    /// </summary>
    private static readonly Dictionary<string, ZoneKind> ZoneKinds = new(StringComparer.Ordinal);

    /// <summary>
    /// 每个键的失败状态，以便失败的开始与结束都能被上报。
    /// <para>
    /// <see cref="LogOnce"/> 适合一次性的提示，但它会掩盖“先失败、随后
    /// 恢复”这类情形：战局会一直显示旧的那一行，没人能判断下一次
    /// 尝试是否成功。因此每个可以自愈的失败路径都改走 <see cref="LogFailure"/>。
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, bool> Failing = new(StringComparer.Ordinal);

    /// <summary>
    /// 本次战局中成功反馈已展示过的“区域 id + profile id”组合。授权每
    /// <see cref="RepublishInterval"/> 秒重新发布一次，因此若没有这条记录，通知
    /// 会在每次重新发布时再次弹出。
    /// </summary>
    private static readonly HashSet<string> FeedbackShown = new(StringComparer.Ordinal);

    /// <summary>
    /// 成功弹窗在屏幕上停留的秒数，之后由此类将其撤下。该值实时读取自
    /// 开关 'Flare Extract: Feedback Seconds'，因此在配置管理器中修改后无需重启即可
    /// 对下一个弹窗生效。为 0 或更小时关闭自动撤下，横幅会留在
    /// 屏幕上，此前所有构建都是这种行为。
    /// </summary>
    internal static float FeedbackSeconds
    {
        get
        {
            try
            {
                var entry = FikaPlugin.Instance?.Settings?.FlareExfilFeedbackSeconds;
                return entry == null ? DefaultFeedbackSeconds : entry.Value;
            }
            catch (Exception)
            {
                return DefaultFeedbackSeconds;
            }
        }
    }

    /// <summary>
    /// 由此类显示到屏幕上、且仍需再次撤下的弹窗，每项都带有其
    /// 消失时的游戏时间。
    /// <para>
    /// 用列表而非单个槽位：第二次激活可能在第一个横幅仍然显示时弹出新横幅，
    /// 较早的弹窗既不能被较新的弹窗留在屏幕上，也不能被
    /// 较新弹窗的到期时间提前关闭。每一项都携带自己的通知对象，因此撤下一个
    /// 永远不会影响另一个。
    /// </para>
    /// </summary>
    private static readonly List<FeedbackPopup> FeedbackPopups = new();

    /// <summary>无法读取开关时使用的显示时间（缺少配置项、没有插件实例）。</summary>
    private const float DefaultFeedbackSeconds = 5f;

    private static bool _classificationLogged;

    private static System.IntPtr _stateWorld;
    private static float _nextRepublish;

    /// <summary>
    /// 原版小队通道开关。开启（默认）时，一发信号弹会为发射者的整个小队
    /// 打开该撤离点，这正是 <c>EZoneEventType.PlayerByPartyAddedInShotList</c>
    /// 所描述的行为。关闭时激活只绑定到发射者本人，因此每个玩家都必须
    /// 自己发射信号弹。
    /// </summary>
    internal static bool SquadSharesActivation
    {
        get
        {
            try
            {
                var settings = FikaPlugin.Instance?.Settings;
                var entry = settings?.FlareExtractSquadSharesActivation;
                return entry == null || entry.Value;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// 由发射者自己的枪械控制器在发射信号弹之后立即调用。识别
    /// ExitActivate（绿色）信号弹，解析检测区与撤离点，然后或者发送
    /// 上报（客户端），或者在本地授权（主机）。
    /// </summary>
    internal static void OnLocalFlareShot(Ammo flareItem, Vector3 shotPosition)
    {
        try
        {
            if (flareItem == null)
            {
                return;
            }

            var template = flareItem.AmmoTemplate;
            if (template == null || !template.ContainsType(FlareEventType.ExitActivate))
            {
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            var localPlayer = world?.MainPlayer;
            if (localPlayer == null)
            {
                return;
            }

            var profileId = localPlayer.ProfileId;
            if (string.IsNullOrEmpty(profileId))
            {
                Reject(string.Empty, "the local player has no profile id");
                return;
            }

            if (!TryResolveTarget(shotPosition, localPlayer.Position, out var zoneId, out var pointName, out var exitZone))
            {
                LogOnce("notarget:" + profileId,
                    $"[FlareExfil] fired zone= profile={profileId} -> no detector zone or exit point nearby, ignored");
                return;
            }

            if (FikaBackendUtils.IsClient)
            {
                if (FikaSingleton<IFikaNetworkManager>.Instance is not FikaClient client)
                {
                    Reject(profileId, "no FikaClient to report to");
                    return;
                }

                FlareExfilActivationPacket packet = new()
                {
                    Kind = FlareExfilActivationPacket.EKind.Report,
                    ProfileId = profileId,
                    PointName = pointName,
                    ZoneId = zoneId,
                    SquadId = string.Empty,
                    RaidId = localPlayer.RaidId,
                    FlarePosition = shotPosition
                };
                client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
                // 发射该枪的机器在信号弹燃烧时已经跑过原生场景链路；
                // 这里只覆盖它没有跑过的情况（信号弹在我们 postfix 运行之前就已燃烧）。
                QueueLocalVisual(flareItem, pointName, profileId, shotPosition);
                FikaGlobals.LogWarning($"[FlareExfil] fired zone={zoneId} profile={profileId} -> report sent");
                return;
            }

            QueueLocalVisual(flareItem, pointName, profileId, shotPosition);

            // 主机：本地信号弹立即生效，同时仍必须告知每个对等端
            // 它是为谁触发的——撤离点不得对授权之外的玩家开放。
            GrantForShooter(profileId, pointName, zoneId, exitZone, "host local flare", shotPosition);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] local flare handling failed: {exception}");
        }
    }

    /// <summary>
    /// 客户端上报的主机侧处理。由发送方对等端决定该数据包可以代表
    /// 哪个 profile；其余每个字段都会在签发授权前按本次战局校验。
    /// </summary>
    internal static void HostHandleReport(FlareExfilActivationPacket packet, NetPeer peer)
    {
        try
        {
            if (packet.Kind != FlareExfilActivationPacket.EKind.Report)
            {
                return;
            }

            var sender = peer?.Player?.ProfileId;
            if (string.IsNullOrEmpty(sender) || !string.Equals(sender, packet.ProfileId, StringComparison.Ordinal))
            {
                Reject(packet.ProfileId, $"peer='{sender ?? "unknown"}' does not own the claimed profile");
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                Reject(packet.ProfileId, "no game world");
                return;
            }

            var player = world.GetAlivePlayerByProfileID(packet.ProfileId);
            if (player == null)
            {
                Reject(packet.ProfileId, "unknown player");
                return;
            }

            if (packet.RaidId <= 0)
            {
                Reject(packet.ProfileId, $"invalid flare raid id '{packet.RaidId}'");
                return;
            }

            if (IsPlayerBanned(player.RaidId))
            {
                Reject(packet.ProfileId, "the player is banned from exfiltration");
                return;
            }

            var point = FikaExfilManager.FindExfiltrationPoint(packet.PointName);
            if (point == null)
            {
                Reject(packet.ProfileId, $"unknown exit point '{packet.PointName}'");
                return;
            }

            // 场景触发器上报不携带区域 id：那张地图按构造就没有检测区，因此
            // 只要上报位置所在的出口正是该撤离点、且该出口带有绿色信号弹能够满足的
            // 手动激活证据，主机就接受该撤离点。
            if (string.IsNullOrEmpty(packet.ZoneId))
            {
                var sceneDistance = float.MaxValue;
                try
                {
                    sceneDistance = Vector3.Distance(point.transform.position, packet.FlarePosition);
                }
                catch (Exception)
                {
                    // 下面按超出范围处理。
                }

                if (sceneDistance > PointRange)
                {
                    Reject(packet.ProfileId, $"the flare was fired farther than {PointRange} m from point '{packet.PointName}'");
                    return;
                }

                GrantForShooter(packet.ProfileId, packet.PointName, packet.ZoneId, true, "client report (scene trigger)", packet.FlarePosition);
                return;
            }

            // 主机不会仅凭客户端所说就相信它是在哪个区域发射的：上报的
            // 位置必须落在上报所指的检测区之内。
            if (!TryFindZone(packet.FlarePosition, out var reportedZone, out var reportedExitZone)
                || !string.Equals(ZoneName(reportedZone), packet.ZoneId, StringComparison.Ordinal))
            {
                Reject(packet.ProfileId, $"the flare position is not inside detector zone '{packet.ZoneId}'");
                return;
            }

            if (!reportedExitZone)
            {
                LogOnce("nonexitzone:" + packet.ZoneId,
                    $"[FlareExfil] report zone='{packet.ZoneId}' flareType={DescribeFlareType(reportedZone)} is not a green flare (ExitActivate) zone, so no exit can be opened by it");
            }

            // 并且出口是由该区域自行推导出来的，因此上报只能命名该
            // 区域所激活的出口，而不能是附近任意一个出口。
            var derivedPointName = ResolvePointForZone(packet.ZoneId) ?? FindNearestPointName(packet.FlarePosition);
            if (string.IsNullOrEmpty(derivedPointName))
            {
                Reject(packet.ProfileId, $"no exit point within {PointRange} m of the reported flare position");
                return;
            }

            if (!string.Equals(derivedPointName, packet.PointName, StringComparison.Ordinal))
            {
                Reject(packet.ProfileId, $"the report names '{packet.PointName}' but its flare position belongs to '{derivedPointName}'");
                return;
            }

            // 出口点本身必须是等待手动激活的那一个，或者已被某一轮
            // 流程分类过的那一个：否则手工构造的上报就能打开普通出口。
            if (HasManualActivationEvidence(packet.PointName, point) == null && !IsGatedPoint(packet.PointName))
            {
                Reject(packet.ProfileId, $"the exit '{packet.PointName}' does not wait for a manual activation, so a flare does not open it");
                return;
            }

            if (Vector3.Distance(point.transform.position, packet.FlarePosition) > PointRange)
            {
                Reject(packet.ProfileId, $"the flare was fired farther than {PointRange} m from point '{packet.PointName}'");
                return;
            }

            GrantForShooter(packet.ProfileId, packet.PointName, packet.ZoneId, reportedExitZone, "client report", packet.FlarePosition);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] report handling failed: {exception}");
        }
    }

    /// <summary>
    /// 主机授权的客户端侧处理。由发射者以及（小队开关开启时）其小队
    /// 成员应用；其他小队则把该撤离点记为受限，并输出被忽略的那一行日志。
    /// </summary>
    internal static void ClientHandlePacket(FlareExfilActivationPacket packet)
    {
        try
        {
            if (packet.Kind != FlareExfilActivationPacket.EKind.Grant)
            {
                return;
            }

            var localPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
            var ownProfileId = localPlayer?.ProfileId;
            if (string.IsNullOrEmpty(ownProfileId))
            {
                // 在这里丢弃授权是无声的：本机不写免疫、不做任何门禁
                // 也不打印被忽略的那一行——而主机却认为授权已送达。
                LogFailure("client-no-profile", true,
                    $"[FlareExfil] grant dropped: the local profile id is unknown on this machine " +
                    $"(world={(Singleton<GameWorld>.Instance == null ? "missing" : "present")}, " +
                    $"mainPlayer={(localPlayer == null ? "missing" : "present")}) - zone='{packet.ZoneId}' " +
                    $"point='{packet.PointName}' firedBy='{packet.ProfileId}'",
                    string.Empty);
                return;
            }

            LogFailure("client-no-profile", false, string.Empty,
                $"[FlareExfil] grant handling resumed: the local profile id is '{ownProfileId}' again");

            // 主机决定授权覆盖谁，但授权实际做什么由本机的
            // 场景决定：只有绿色信号弹区域（或本战局已分类为受限的出口）才会在此
            // 产生门禁，而接入原生狙击免疫注册表的区域改为授予
            // 免疫——在每台机器上都如此，因为每台机器都模拟自己的狙击手。
            var exitZone = IsExitZoneById(packet.ZoneId);
            var kind = KindOf(packet.ZoneId, exitZone, packet.PointName);
            if (kind == ZoneKind.Other && !exitZone && !IsGatedPoint(packet.PointName) && CountImmunityRegistries() > 0)
            {
                // 本机没有该 id 的检测区（或者它从未被分类），
                // 该授权在此不产生任何门禁，而本战局确实存在原生注册表：主机
                // 解析出的是免疫区域，因此遵从免疫，而不是丢弃该授权。
                kind = ZoneKind.Immunity;
                LogOnce("immunity-inferred:" + packet.ZoneId,
                    $"[FlareExfil] immunity zone='{packet.ZoneId}' is not in this scene, treating the grant as immunity " +
                    $"(the raid carries {CountImmunityRegistries()} PlayersWithImmuneToSniperFire registr(ies))");
            }

            var mine = string.Equals(packet.ProfileId, ownProfileId, StringComparison.Ordinal);
            var squadWide = !string.IsNullOrEmpty(packet.SquadId)
                && string.Equals(LocalSquadId(localPlayer), packet.SquadId, StringComparison.Ordinal);

            if (kind == ZoneKind.Immunity)
            {
                // 每个对等端都会写入被覆盖的战局 id，而不仅仅是被覆盖的玩家：未被
                // 覆盖的对等端同样运行着可能向他们开火的狙击区域。
                ApplyImmunity(packet.ZoneId, CoveredProfiles(packet, ownProfileId, mine || squadWide),
                    $"grant from host (fired by {packet.ProfileId})");
                // 出口动画不属于免疫路径，但在此发射的信号弹是一个
                // 每台机器都必须播放的场景事件，才能看到发生了什么——无论是否被覆盖。
                QueueFlareVisual(packet);
                LogOnce($"visual-immunity:{packet.ZoneId}:{packet.ProfileId}",
                    $"[FlareExfil] heli: point='{packet.PointName}' open=n/a visual=broadcast kind=immunity " +
                    $"firedBy='{packet.ProfileId}' zone='{packet.ZoneId}' mine={mine} squadWide={squadWide}");
                LogOnce($"immunity-applied:{packet.ZoneId}:{ownProfileId}",
                    $"[FlareExfil] immunity applied on this machine zone='{packet.ZoneId}' (fired by {packet.ProfileId} " +
                    $"mine={mine} squadWide={squadWide})");
                return;
            }

            var gate = kind == ZoneKind.ExitGate;
            if (gate)
            {
                AddGatedPoint(packet.PointName);
            }

            // 放在下面的“是否被覆盖”判定之前：信号弹出口的出口动画属于
            // 战局中的每台机器，无论该授权是否覆盖此玩家，因为播放它的原生
            // 链路是由信号弹事件而不是出口开启来启动的。
            QueueFlareVisual(packet);

            if (!mine && !squadWide)
            {
                LogOnce($"ignored:{packet.PointName}:{packet.ProfileId}",
                    $"[FlareExfil] ignored (report profile={packet.ProfileId} != me) point={packet.PointName}");
                return;
            }

            AllowProfile(packet.PointName, ownProfileId, gate);

            var profiles = new List<string> { ownProfileId };
            ApplyGrant(packet.PointName, packet.ZoneId, profiles, out var transition, out var native);

            if (!gate)
            {
                LogOnce($"noop:{packet.PointName}:{ownProfileId}",
                    $"[FlareExfil] no-op point='{packet.PointName}' status={transition} for 1 players - the flare did not " +
                    $"resolve to a green flare exit, so nothing was opened (zone='{packet.ZoneId}' exitZone={exitZone})");
            }
            else if (!mine)
            {
                LogOnce($"applied:{packet.PointName}:{packet.ProfileId}:{ownProfileId}",
                    $"[FlareExfil] applied (fired by {packet.ProfileId}, squad-wide) point='{packet.PointName}' " +
                    $"status={transition} zone='{packet.ZoneId}' native=[{native}]");
            }
            else
            {
                LogOnce($"applied-self:{packet.PointName}:{ownProfileId}",
                    $"[FlareExfil] applied (fired by {packet.ProfileId}) point='{packet.PointName}' " +
                    $"status={transition} zone='{packet.ZoneId}' native=[{native}]");
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] grant handling failed: {exception}");
        }
    }

    /// <summary>
    /// 记录战局绑定各撤离点时的状态。信号弹出口是满足以下条件的那些撤离点：
    /// （a）初始时等待手动激活，且（b）位于绿色信号弹检测区内；
    /// 这两个事实都来自原生战局设置，而不是来自名称列表。
    /// </summary>
    internal static void CaptureInitialStatus(ExfiltrationPoint[] regular, SecretExfiltrationPoint[] secret)
    {
        try
        {
            // 在记录本战局之前先丢弃上一战局的表格。下面的上报会走
            // LogOnce，其世界切换分支会清理同一批表格，而一次过晚的清理
            // 会抹掉本战局刚刚得出的分类结果。
            SyncRaidState();

            lock (Sync)
            {
                RememberStatus(regular);
                if (secret != null)
                {
                    for (var i = 0; i < secret.Length; i++)
                    {
                        var point = secret[i];
                        if (point?.Settings == null)
                        {
                            continue;
                        }

                        InitialStatuses.TryAdd(point.Settings.Name, point.Status);
                    }
                }
            }

            ClassifyDetectorPoints();
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] initial status capture failed: {exception}");
        }
    }

    private static void RememberStatus(ExfiltrationPoint[] regular)
    {
        if (regular == null)
        {
            return;
        }

        for (var i = 0; i < regular.Length; i++)
        {
            var point = regular[i];
            if (point?.Settings == null)
            {
                continue;
            }

            InitialStatuses.TryAdd(point.Settings.Name, point.Status);
        }
    }

    /// <summary>
    /// 在任何玩家发射之前，先标记出需要绿色信号弹的出口，这样无人发射的战局
    /// 就无法使用它们；同时打印结论所依据的证据：每个检测区及其
    /// 信号弹类型和距离最近的出口，以及每个出口的状态、需求
    /// 和与之关联的检测区。
    /// <para>
    /// 一个出口要被纳入门禁，必须同时满足两点：必须有一个绿色信号弹
    /// （<see cref="FlareEventType.ExitActivate"/>）检测区与它关联，并且它
    /// 必须带有手动激活证据——无论是战局绑定它时的状态、当前实时
    /// 状态，还是它自身需求列表中的 <see cref="AwaitsManualActivationRequirement"/>。
    /// 单凭状态快照并不可信，因为战局设置、服务器侧出口
    /// 设置以及该撤离点已经收到的状态都可能让它停在
    /// <see cref="EExfiltrationStatus.AwaitsManualActivation"/> 之外的其他值上。
    /// </para>
    /// <para>
    /// 带有绿色信号弹无法满足的需求的出口（付费出口、合作
    /// 出口、世界事件出口）永远不会被纳入门禁：否则会把普通出口对每个
    /// 从不发射信号弹的玩家锁死。
    /// </para>
    /// </summary>
    private static void ClassifyDetectorPoints()
    {
        FlareShootDetectorZone[] zones;
        try
        {
            zones = UnityEngine.Object.FindObjectsOfType<FlareShootDetectorZone>(true);
        }
        catch (Exception exception)
        {
            LogOnce("classify", $"[FlareExfil] detector zone lookup failed: {exception.Message}");
            return;
        }

        // 一个已销毁的条目否则会无声消失：区域表就会比
        // 会比场景实际拥有的区域少一行。
        var destroyedZones = CountDestroyed(zones);
        if (destroyedZones > 0)
        {
            LogOnce("zones-destroyed",
                $"[FlareExfil] detector zones: {destroyedZones} of {zones.Length} entry(ies) are destroyed and were skipped");
        }

        if (zones == null || zones.Length == 0)
        {
            LogOnce("zones", "[FlareExfil] detector zones: none in this raid");
            return;
        }

        var controller = ExfiltrationController.Instance;
        if (controller == null)
        {
            // 没有该控制器就什么都无法分类：没有受限表、没有免疫表，
            // 也没有“flare gated points”这一行。把它报告出来，才能让空的分类结果不至于
            // 被误读为“这张地图没有信号弹出口”。
            LogFailure("classify-nocontroller", true,
                "[FlareExfil] classification skipped: ExfiltrationController is not available on this machine, " +
                "so no exit is gated and no immunity zone is named (a fired flare is still granted from the scene alone)",
                string.Empty);
            return;
        }

        LogFailure("classify-nocontroller", false, string.Empty,
            "[FlareExfil] classification resumed: ExfiltrationController is available again");

        // 秘密出口被有意排除在分类之外：它们走的是小队/团队共享发现
        // 路径（SecretExfiltrationPointFoundShareEvent），一旦误分类，就会跳过它们
        // 对整个小队的状态同步。
        var points = controller.ExfiltrationPoints;

        // 出口同理：没有设置项的撤离点既不会被纳入门禁，也不会在下面列出，而一个
        // 最终为空的表格必须说明这是地图本身如此，还是场景已被拆除。
        var skippedPoints = DescribeSkippedPoints(points);
        if (skippedPoints != null)
        {
            LogOnce("points-skipped",
                $"[FlareExfil] classification skipped exit point(s) that are destroyed or not initialised - {skippedPoints} - " +
                "they are neither gated nor listed in the point table");
        }

        // 区域遍历解析每个信号弹开启什么，出口遍历决定哪些出口被纳入门禁，
        // 只有之后才能给区域定性：免疫结论需要先有出口结论。
        var infos = CollectDetectorZones(zones, points);
        ClassifyPoints(points);
        ReportZoneKinds(infos);
        LogClassification();
    }

    /// <summary>
    /// 收集本次战局的每个检测区，解析绿色信号弹区域所激活的出口，
    /// 并返回上报流程要打印的快照。这里不做任何日志输出：区域的定性需要
    /// 出口结论，而出口结论由 <see cref="ClassifyPoints"/> 稍后产生。
    /// </summary>
    private static List<ZoneInfo> CollectDetectorZones(FlareShootDetectorZone[] zones, ExfiltrationPoint[] points)
    {
        var infos = new List<ZoneInfo>();
        for (var i = 0; i < zones.Length; i++)
        {
            var zone = zones[i];
            if (zone == null)
            {
                continue;
            }

            var zoneId = ZoneName(zone);
            var exitZone = IsExitZone(zone);
            var nearestName = null as string;
            var nearestDistance = -1f;
            try
            {
                nearestName = NearestPointTo(points, zone.transform.position, out nearestDistance);
            }
            catch (Exception)
            {
                // 使用区域那一行的占位符上报。
            }

            var inside = PointInsideZone(zone, points, out var insideName);

            // 只有当区域在物理上包含某个出口，或者最近的出口落在可接受
            // 范围内时，区域才会与该出口绑定。绑定一个很远的出口会把门禁关联到
            // 与信号弹毫无关系的撤离点上。只有绿色信号弹（ExitActivate）区域才会开启
            // 出口，因此只绑定这些区域。
            var associated = inside
                ? insideName
                : (nearestName != null && nearestDistance >= 0f && nearestDistance <= PointRange ? nearestName : null);

            if (exitZone && !string.IsNullOrEmpty(zoneId) && !string.IsNullOrEmpty(associated))
            {
                lock (Sync)
                {
                    PointByFlareZone[zoneId] = associated;
                    FlareZoneByPoint[associated] = zoneId;
                    FlareZoneDistanceByPoint[associated] = inside ? 0f : nearestDistance;
                }
            }

            infos.Add(new ZoneInfo
            {
                Zone = zone,
                ZoneId = zoneId,
                ExitZone = exitZone,
                FlareType = DescribeFlareType(zone),
                Collider = DescribeZoneCollider(zone),
                NearestName = nearestName,
                NearestDistance = nearestDistance,
                InsideName = insideName,
                AssociatedName = associated
            });
        }

        return infos;
    }

    /// <summary>
    /// 每个检测区一行：它处理的信号弹类型、距离它最近的出口、位于其
    /// 碰撞体内部的出口，以及其信号弹所做工作的类别。
    /// <para>
    /// <c>kind=exit-gate</c> 是绿色（<c>ExitActivate</c>）区域，因此信号弹会激活与之
    /// 关联的出口；该出口是否真的被纳入门禁，则由下面出口那一行的结论说明。
    /// <c>kind=immunity</c> 则是接入原生狙击免疫注册表的区域；该结论的
    /// 证据打印在 <c>immune=</c> 之后。<c>kind=other</c> 是空投、
    /// 任务或狙击火力区域，任何撤离逻辑都不得触碰。
    /// </para>
    /// </summary>
    private static void ReportZoneKinds(List<ZoneInfo> infos)
    {
        for (var i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            var exitGated = !string.IsNullOrEmpty(info.AssociatedName) && IsGatedPoint(info.AssociatedName);
            var kind = ClassifyKind(info, exitGated, out var evidence);

            if (kind == ZoneKind.Immunity && exitGated)
            {
                // 两类重叠的唯一边界情形：某个绿色信号弹区域既接入
                // 免疫注册表，又位于一个等待手动激活的出口之上。
                // 免疫优先（免疫区域绝不能触碰出口状态），因此它原本
                // 会打开的门禁就丢失了——把它标记出来，而不是默默决定。
                LogFailure("kind-conflict:" + info.ZoneId, true,
                    $"[FlareExfil] zone '{info.ZoneId}' matches BOTH immunity and exit-gate (associatedExit='{info.AssociatedName}' " +
                    $"exitGated=True immune=[{evidence}]) - treated as immunity, so that exit is not gated: confirm this zone by hand",
                    $"[FlareExfil] zone '{info.ZoneId}' no longer matches both immunity and exit-gate (exitGated={exitGated}, " +
                    $"kind={DescribeKind(kind)})");
            }
            else
            {
                LogFailure("kind-conflict:" + info.ZoneId, false, string.Empty,
                    $"[FlareExfil] zone '{info.ZoneId}' no longer matches both immunity and exit-gate " +
                    $"(exitGated={exitGated}, kind={DescribeKind(kind)}) - the verdict is unambiguous again");
            }

            if (!string.IsNullOrEmpty(info.ZoneId))
            {
                lock (Sync)
                {
                    ZoneKinds[info.ZoneId] = kind;
                }
            }

            LogOnce("zone:" + info.ZoneId,
                $"[FlareExfil] detector zone '{info.ZoneId}' flareType={info.FlareType} exitZone={info.ExitZone} " +
                $"collider={info.Collider} nearestExit='{info.NearestName ?? "none"}' " +
                $"distance={(info.NearestDistance < 0f ? "n/a" : info.NearestDistance.ToString("F1") + "m")} " +
                $"exitInsideZone='{info.InsideName ?? "none"}' associatedExit='{info.AssociatedName ?? "none"}' " +
                $"exitGated={exitGated} kind={DescribeKind(kind)} immune=[{evidence ?? "none"}]");
        }
    }

    /// <summary>
    /// 本机上某区域信号弹所做的工作。免疫优先于门禁：接入
    /// 狙击免疫注册表的区域绝不允许触碰出口状态，因为这类地图的出口
    /// （Ground Zero 的 <c>Sniper_exit</c>）本身按需求就是开启的，只是被狙击手
    /// 封锁而已。
    /// </summary>
    private static ZoneKind ClassifyKind(ZoneInfo info, bool exitGated, out string evidence)
    {
        evidence = info.Zone != null ? ImmunityEvidence(info.Zone, exitGated) : null;
        if (evidence != null)
        {
            return ZoneKind.Immunity;
        }

        if (info.ExitZone)
        {
            evidence = "green flare (ExitActivate) zone";
            return ZoneKind.ExitGate;
        }

        return ZoneKind.Other;
    }

    private static string DescribeKind(ZoneKind kind)
    {
        return kind switch
        {
            ZoneKind.ExitGate => "exit-gate",
            ZoneKind.Immunity => "immunity",
            _ => "other"
        };
    }

    /// <summary>
    /// 每个出口一行，附带门禁结论，这样不用调试器也能回溯一次战局：
    /// 状态、记录的初始状态、出口类型、需求、关联区域、距离。
    /// </summary>
    private static void ClassifyPoints(ExfiltrationPoint[] points)
    {
        if (points == null)
        {
            return;
        }

        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (point?.Settings == null || string.IsNullOrEmpty(point.Settings.Name))
            {
                continue;
            }

            var name = point.Settings.Name;
            var status = "unreadable";
            try
            {
                status = DescribeStatus(point.Status);
            }
            catch (Exception)
            {
                // 保持为不可读取。
            }

            var initial = "unrecorded";
            lock (Sync)
            {
                if (InitialStatuses.TryGetValue(name, out var recorded))
                {
                    initial = DescribeStatus(recorded);
                }
            }

            var zoneId = "none";
            var zoneDistance = "n/a";
            lock (Sync)
            {
                if (FlareZoneByPoint.TryGetValue(name, out var associated))
                {
                    zoneId = associated;
                }

                if (FlareZoneDistanceByPoint.TryGetValue(name, out var distance))
                {
                    zoneDistance = distance <= 0f ? "inside zone" : distance.ToString("F1") + "m";
                }
            }

            var evidence = HasManualActivationEvidence(name, point);
            var blocker = DescribeBlockingRequirement(point);

            var gated = false;
            string reason;
            if (zoneId == "none")
            {
                reason = "no green flare (ExitActivate) detector zone is associated with this exit";
            }
            else if (blocker != null)
            {
                reason = blocker;
            }
            else if (evidence == null)
            {
                reason = "no manual activation evidence: the status is neither recorded nor read as AwaitsManualActivation and the exit carries no AwaitsManualActivationRequirement";
            }
            else
            {
                gated = true;
                reason = evidence;
            }

            if (gated)
            {
                lock (Sync)
                {
                    ClassifiedGated.Add(name);
                }
            }

            LogOnce("point:" + name,
                $"[FlareExfil] point '{name}' status={status} initial={initial} type={DescribePointType(point)} " +
                $"requirements=[{DescribeRequirements(point)}] zone='{zoneId}' distance={zoneDistance} " +
                $"verdict={(gated ? "gated" : "not-gated")} reason='{reason}'");
        }
    }

    /// <summary>若检测区会激活某个出口则为 true：只有绿色信号弹才会这样。</summary>
    private static bool IsExitZone(FlareShootDetectorZone zone)
    {
        try
        {
            return zone != null && zone.flareTypeForHandle == FlareEventType.ExitActivate;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string DescribeFlareType(FlareShootDetectorZone zone)
    {
        try
        {
            return DescribeFlareType(zone.flareTypeForHandle);
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string DescribeFlareType(FlareEventType flareType)
    {
        return $"{flareType}({(int)flareType})";
    }

    private static string DescribeZoneCollider(FlareShootDetectorZone zone)
    {
        try
        {
            var collider = GetZoneCollider(zone);
            if (collider == null)
            {
                return "none";
            }

            var size = collider.bounds.size;
            return $"bounds=({size.x:F1},{size.y:F1},{size.z:F1})";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string DescribeStatus(EExfiltrationStatus status)
    {
        return $"{status}({(int)status})";
    }

    private static string DescribePointType(ExfiltrationPoint point)
    {
        try
        {
            return point.Settings != null ? point.Settings.ExfiltrationType.ToString() : "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// 需求项的原生类型名。运行时类型从 IL2CPP 类型读取，因为
    /// 需求数组声明的元素类型始终是基类。
    /// </summary>
    private static string RequirementTypeName(ExfiltrationRequirement requirement)
    {
        try
        {
            var type = requirement.GetIl2CppType();
            if (type != null && !string.IsNullOrEmpty(type.Name))
            {
                return type.Name;
            }
        }
        catch (Exception)
        {
            // 下面回退到托管代理类型。
        }

        try
        {
            return requirement.GetType().Name;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string DescribeRequirements(ExfiltrationPoint point)
    {
        try
        {
            var requirements = point.Requirements;
            if (requirements == null || requirements.Length == 0)
            {
                return "none";
            }

            var text = string.Empty;
            for (var i = 0; i < requirements.Length; i++)
            {
                var requirement = requirements[i];
                if (requirement == null)
                {
                    continue;
                }

                if (text.Length > 0)
                {
                    text += ", ";
                }

                text += RequirementTypeName(requirement);
                try
                {
                    text += "(" + requirement.Requirement + ")";
                }
                catch (Exception)
                {
                    text += "(unreadable)";
                }
            }

            return text.Length == 0 ? "none" : text;
        }
        catch (Exception exception)
        {
            return "unreadable: " + exception.Message;
        }
    }

    /// <summary>
    /// 给出绿色信号弹无法满足的第一个需求项；若该出口只带有
    /// 手动激活需求项和空的需求槽位，则返回 null。
    /// </summary>
    private static string DescribeBlockingRequirement(ExfiltrationPoint point)
    {
        try
        {
            if (point.TransferItemRequirement != null)
            {
                return "the exit requires an item transfer, so it is a paid exit";
            }

            var requirements = point.Requirements;
            if (requirements == null)
            {
                return null;
            }

            for (var i = 0; i < requirements.Length; i++)
            {
                var requirement = requirements[i];
                if (requirement == null)
                {
                    continue;
                }

                var typeName = RequirementTypeName(requirement);
                if (typeName.IndexOf("AwaitsManualActivation", StringComparison.Ordinal) >= 0)
                {
                    continue;
                }

                ERequirementState state;
                try
                {
                    state = requirement.Requirement;
                }
                catch (Exception)
                {
                    continue;
                }

                if (state == ERequirementState.None)
                {
                    continue;
                }

                return $"the exit carries the requirement {typeName}({state}), which a flare does not satisfy";
            }
        }
        catch (Exception exception)
        {
            return "the requirements of the exit are unreadable: " + exception.Message;
        }

        return null;
    }

    /// <summary>
    /// 出口的手动激活证据：战局绑定它时记录的状态、实时状态，
    /// 或者该出口自身的 <c>AwaitsManualActivationRequirement</c>。返回证据文本，
    /// 若三者都不适用则返回 null。
    /// </summary>
    private static string HasManualActivationEvidence(string name, ExfiltrationPoint point)
    {
        lock (Sync)
        {
            if (InitialStatuses.TryGetValue(name, out var recorded) && recorded == EExfiltrationStatus.AwaitsManualActivation)
            {
                return "recorded status AwaitsManualActivation";
            }
        }

        try
        {
            if (point.Status == EExfiltrationStatus.AwaitsManualActivation)
            {
                return "live status AwaitsManualActivation";
            }
        }
        catch (Exception)
        {
            // 继续落到需求项检查。
        }

        try
        {
            var requirements = point.Requirements;
            if (requirements != null)
            {
                for (var i = 0; i < requirements.Length; i++)
                {
                    var requirement = requirements[i];
                    if (requirement != null
                        && RequirementTypeName(requirement).IndexOf("AwaitsManualActivation", StringComparison.Ordinal) >= 0)
                    {
                        return "AwaitsManualActivationRequirement";
                    }
                }
            }
        }
        catch (Exception)
        {
            // 没有需求项证据。
        }

        return null;
    }

    private static string NearestPointTo(ExfiltrationPoint[] points, Vector3 position, out float distance)
    {
        distance = -1f;
        var bestName = null as string;
        var best = float.MaxValue;

        if (points == null)
        {
            return null;
        }

        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (point?.Settings == null || string.IsNullOrEmpty(point.Settings.Name))
            {
                continue;
            }

            float current;
            try
            {
                current = Vector3.Distance(point.transform.position, position);
            }
            catch (Exception)
            {
                continue;
            }

            if (current < best)
            {
                best = current;
                bestName = point.Settings.Name;
            }
        }

        if (bestName != null)
        {
            distance = best;
        }

        return bestName;
    }

    /// <summary>触发器位于该区域内的出口（如果有）。</summary>
    private static bool PointInsideZone(FlareShootDetectorZone zone, ExfiltrationPoint[] points, out string name)
    {
        name = null;

        try
        {
            var collider = GetZoneCollider(zone);
            if (collider == null || points == null)
            {
                return false;
            }

            var bounds = collider.bounds;
            var best = float.MaxValue;
            for (var i = 0; i < points.Length; i++)
            {
                var point = points[i];
                if (point?.Settings == null || string.IsNullOrEmpty(point.Settings.Name))
                {
                    continue;
                }

                var position = point.transform.position;
                if (!bounds.Contains(position))
                {
                    continue;
                }

                var distance = Vector3.Distance(position, zone.transform.position);
                if (distance < best)
                {
                    best = distance;
                    name = point.Settings.Name;
                }
            }
        }
        catch (Exception)
        {
            return false;
        }

        return !string.IsNullOrEmpty(name);
    }

    /// <summary>若指定名称的区域在本地存在且处理出口激活，则为 true。</summary>
    private static bool IsExitZoneById(string zoneId)
    {
        var zone = FindZoneById(zoneId);
        return zone != null && IsExitZone(zone);
    }

    /// <summary>本机上具有该 id 的检测区；若此场景没有则为 null。</summary>
    private static FlareShootDetectorZone FindZoneById(string zoneId)
    {
        if (string.IsNullOrEmpty(zoneId))
        {
            return null;
        }

        try
        {
            var zones = UnityEngine.Object.FindObjectsOfType<FlareShootDetectorZone>(true);
            if (zones == null)
            {
                return null;
            }

            for (var i = 0; i < zones.Length; i++)
            {
                var zone = zones[i];
                if (zone != null && string.Equals(ZoneName(zone), zoneId, StringComparison.Ordinal))
                {
                    return zone;
                }
            }
        }
        catch (Exception exception)
        {
            LogOnce("zonecheck", $"[FlareExfil] detector zone lookup failed: {exception.Message}");
        }

        return null;
    }

    private static void LogClassification()
    {
        if (_classificationLogged)
        {
            return;
        }

        _classificationLogged = true;
        var names = new List<string>();
        lock (Sync)
        {
            foreach (var name in ClassifiedGated)
            {
                names.Add(name);
            }
        }

        // 场景触发链路是信号弹出口的第二种形态，不依赖检测区（破冰船
        // 直升机出口）：一张地图若有它却没有检测区，就无法按几何关系分类，因此
        // 计数正是在单行日志中区分这两种形态的依据。
        var triggers = CountSceneFlareTriggers();

        if (names.Count == 0)
        {
            FikaGlobals.LogWarning($"[FlareExfil] flare gated points: none in this raid (no detector zone over an " +
                $"awaiting-manual-activation exit) sceneTriggers={triggers}");
            return;
        }

        FikaGlobals.LogWarning($"[FlareExfil] flare gated points: {string.Join(", ", names)} sceneTriggers={triggers}");
    }

    /// <summary>
    /// 当该玩家可以使用此撤离点时为 true。从未发生过信号弹激活的撤离点
    /// 保持其原版行为，因此只有信号弹出口会受影响。
    /// </summary>
    public static bool MayExtract(string profileId, string pointName)
    {
        try
        {
            if (string.IsNullOrEmpty(pointName))
            {
                return true;
            }

            lock (Sync)
            {
                if (!GatedPoints.Contains(pointName) && !ClassifiedGated.Contains(pointName))
                {
                    return true;
                }

                if (AllowedProfilesByPoint.TryGetValue(pointName, out var allowed) && allowed != null
                    && !string.IsNullOrEmpty(profileId) && allowed.Contains(profileId))
                {
                    return true;
                }
            }

            LogOnce($"refused:{pointName}:{profileId}",
                $"[FlareExfil] rejected profile={profileId} reason=no flare activation for point '{pointName}'");
            return false;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] extraction check failed: {exception}");
            return true;
        }
    }

    /// <summary>本战局中只要有玩家用信号弹激活过此撤离点即为 true。</summary>
    public static bool IsGatedPoint(string pointName)
    {
        if (string.IsNullOrEmpty(pointName))
        {
            return false;
        }

        lock (Sync)
        {
            return GatedPoints.Contains(pointName) || ClassifiedGated.Contains(pointName);
        }
    }

    /// <summary>当本地玩家在该撤离点的允许列表中时为 true。</summary>
    public static bool LocalPlayerAllowed(string pointName)
    {
        var profileId = Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId;
        if (string.IsNullOrEmpty(profileId) || string.IsNullOrEmpty(pointName))
        {
            return false;
        }

        lock (Sync)
        {
            return AllowedProfilesByPoint.TryGetValue(pointName, out var allowed) && allowed != null && allowed.Contains(profileId);
        }
    }

    /// <summary>
    /// 当收到的撤离点状态不得写入本地时为 true：该撤离点受信号弹门禁限制
    /// 而此玩家不在允许列表中，因此发送方给出的撤离点级状态
    /// 与此玩家无关。
    /// </summary>
    public static bool ShouldSkipPointStatus(string pointName)
    {
        return IsGatedPoint(pointName) && !LocalPlayerAllowed(pointName);
    }

    /// <summary>
    /// 主机侧：再次把当前授权交给各对等端。由定时器驱动，以便后加入的
    /// 小队成员也能看到在其连接之前已经发生的激活。
    /// </summary>
    internal static void HostTick()
    {
        if (FikaBackendUtils.IsClient)
        {
            return;
        }

        try
        {
            if (Time.time < _nextRepublish)
            {
                return;
            }

            _nextRepublish = Time.time + RepublishInterval;

            GrantRecord[] snapshot;
            lock (Sync)
            {
                if (Grants.Count == 0)
                {
                    return;
                }

                snapshot = [.. Grants];
            }

            foreach (var grant in snapshot)
            {
                BroadcastGrant(grant);
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] grant republication failed: {exception}");
        }
    }

    /// <summary>
    /// 当受门禁限制的撤离点拒绝复制其撤离点级倒计时时记录：激活是
    /// 以按玩家的授权形式传递的，绝不会以无身份的广播形式发送。
    /// </summary>
    public static void LogCountdownSuppressed(string pointName)
    {
        LogOnce("countdown-suppressed:" + pointName,
            $"[FlareExfil] point {pointName} countdown not broadcast (flare gated, activation travels per player)");
    }

    /// <summary>丢弃战局状态。在发现新世界时调用，且任何时候调用都是安全的。</summary>
    /// <summary>
    /// 保留为显式入口，供希望丢弃战局表格的调用方使用（<see cref="LogOnce"/> 的
    /// 世界切换分支会自行清理同一批表格，因此没有任何东西依赖它被调用；
    /// 它的存在只是为了让将来的钩子不必直接操作这些表格）。
    /// </summary>
    internal static void ResetRaidState()
    {
        lock (Sync)
        {
            ClearRaidStateLocked();
        }
    }

    /// <summary>
    /// 清理所有按战局划分的表格。在世界切换（新战局、重新加载关卡的
    /// 转运）时调用，也由 <see cref="ResetRaidState"/> 调用；该锁是可重入的，因此调用方
    /// 可以已经持有它。
    /// </summary>
    private static void ClearRaidStateLocked()
    {
        AllowedProfilesByPoint.Clear();
        GatedPoints.Clear();
        Grants.Clear();
        LoggedLines.Clear();
        InitialStatuses.Clear();
        ClassifiedGated.Clear();
        FlareZoneByPoint.Clear();
        PointByFlareZone.Clear();
        FlareZoneDistanceByPoint.Clear();
        ZoneKinds.Clear();
        Failing.Clear();
        FeedbackShown.Clear();
        FeedbackPopups.Clear();
        PendingVisuals.Clear();
        AppliedVisuals.Clear();
        LastFlareByProfile.Clear();
        _classificationLogged = false;
    }

    /// <summary>
    /// 当世界指针自上次调用以来发生变化时丢弃战局表格。每个入口
    /// 都会在读取状态前运行它，因此迟到的世界切换既不会抹掉刚刚填好的表格，
    /// 新战局也永远不会读到上一战局的激活信息。
    /// </summary>
    private static void SyncRaidState()
    {
        lock (Sync)
        {
            var world = Singleton<GameWorld>.Instance;
            var worldPointer = world == null ? System.IntPtr.Zero : world.Pointer;
            if (worldPointer != _stateWorld)
            {
                // 新世界：一次全新的战局，或者是重新加载了关卡的转运。
                _stateWorld = worldPointer;
                ClearRaidStateLocked();
            }
        }
    }

    /// <summary>
    /// 签发发射者的信号弹所换来的激活：被覆盖的 profile 写入允许列表，
    /// 撤离点被打开，授权随后发往每个对等端。<paramref name="flarePosition"/> 是信号弹
    /// 燃烧的位置，随授权一起传递，以便每个对等端都能运行发射者机器所跑的同一套出口
    /// 链路；<see cref="Vector3.zero"/> 表示不携带任何视觉表现。
    /// </summary>
    private static void GrantForShooter(string profileId, string pointName, string zoneId, bool exitZone, string source, Vector3 flarePosition)
    {
        // 信号弹在此处的作用决定下面的一切。授权只会对属于绿色信号弹出口的出口
        // 施加门禁：信号弹区域必须是出口激活区域，或者该出口必须是
        // 本战局已分类为受限的出口。解析到普通出口的信号弹不得
        // 为授权之外的玩家锁死该出口；而在免疫区域
        // （Ground Zero 的狙击区）发射的信号弹完全不得触碰出口：
        // 在那里它只会对授权覆盖的玩家禁用狙击手。
        var kind = KindOf(zoneId, exitZone, pointName);
        var gate = kind == ZoneKind.ExitGate;

        var squadId = string.Empty;
        var extraProfiles = null as List<string>;
        if (SquadSharesActivation)
        {
            squadId = FindSquadId(profileId);
            if (!string.IsNullOrEmpty(squadId))
            {
                extraProfiles = CollectGroupProfiles(squadId, profileId);
            }
        }

        AllowProfile(pointName, profileId, gate);

        var granted = new List<string> { profileId };
        var extra = 0;
        if (extraProfiles != null)
        {
            foreach (var groupProfile in extraProfiles)
            {
                if (string.IsNullOrEmpty(groupProfile) || string.Equals(groupProfile, profileId, StringComparison.Ordinal))
                {
                    continue;
                }

                AllowProfile(pointName, groupProfile, gate);
                granted.Add(groupProfile);
                extra++;
            }
        }

        string transition;
        string native;
        if (kind == ZoneKind.Immunity)
        {
            // 免疫区域的出口保持服务器赋予它的状态；信号弹改为在本机为
            // 被覆盖的玩家写入原生狙击免疫集合。
            ApplyImmunity(zoneId, granted, source);
            transition = "point-status-untouched (immunity zone)";
            native = "immunity";
        }
        else
        {
            ApplyGrant(pointName, zoneId, granted, out transition, out native);
        }

        var record = new GrantRecord(pointName, profileId, squadId, zoneId, flarePosition);
        if (gate || kind == ZoneKind.Immunity)
        {
            lock (Sync)
            {
                if (gate)
                {
                    GatedPoints.Add(pointName);
                }

                // 免疫授权同样会被重新发布：稍后才连接的对等端仍然需要
                // 把这些战局 id 写入自己的注册表。
                Grants.RemoveAll(grant => string.Equals(grant.PointName, pointName, StringComparison.Ordinal)
                    && string.Equals(grant.ProfileId, profileId, StringComparison.Ordinal));
                Grants.Add(record);
            }
        }

        BroadcastGrant(record);

        var members = extra + 1;
        if (kind == ZoneKind.Immunity)
        {
            LogOnce("applied:" + pointName,
                $"[FlareExfil] applied zone='{zoneId}' kind=immunity for {members} players - the flare disables the " +
                $"sniper for them and no exit is gated (point='{pointName}' exitZone={exitZone} status={transition} " +
                $"native=[{native}] source={source})");
        }
        else if (gate)
        {
            LogOnce("applied:" + pointName,
                $"[FlareExfil] applied point='{pointName}' status={transition} for {members} players " +
                $"(zone='{zoneId}' kind=exit-gate exitZone={exitZone} native=[{native}] source={source})");
        }
        else
        {
            LogOnce("noop:" + pointName,
                $"[FlareExfil] no-op point='{pointName}' status={transition} for {members} players - the flare did not " +
                $"resolve to a green flare exit, so nothing was opened and nothing was gated (zone='{zoneId}' exitZone={exitZone} source={source})");
        }

        if (extra > 0)
        {
            LogOnce("grant:" + pointName,
                $"[FlareExfil] report profile={profileId} zone={zoneId} -> granted to party ({members} members) ({source})");
            return;
        }

        LogOnce("grant:" + pointName,
            $"[FlareExfil] report profile={profileId} zone={zoneId} -> granted for {profileId} only ({source})");
    }

    private static void BroadcastGrant(GrantRecord grant)
    {
        if (FikaBackendUtils.IsClient)
        {
            // 只有主机才会签发授权；在客户端走到这里意味着该授权永远不会
            // 被通知给任何人。
            LogFailure("broadcast:client", true,
                $"[FlareExfil] grant not broadcast: this machine is a client, so no peer learns about " +
                $"zone='{grant.ZoneId}' point='{grant.PointName}' firedBy='{grant.ProfileId}'",
                string.Empty);
            return;
        }

        LogFailure("broadcast:client", false, string.Empty,
            "[FlareExfil] grant broadcasting resumed: this machine has the host role again");

        if (FikaSingleton<IFikaNetworkManager>.Instance is not FikaServer server)
        {
            // 典型的“主机以为已经发出去了”：授权只在本地做出决定，而每个对等端
            // 都得不到它携带的免疫或门禁。
            LogFailure("broadcast:no-server", true,
                $"[FlareExfil] grant not broadcast: the local manager is not a FikaServer " +
                $"(manager='{FikaSingleton<IFikaNetworkManager>.Instance?.GetType().Name ?? "none"}'), " +
                $"so no peer learns about zone='{grant.ZoneId}' point='{grant.PointName}' firedBy='{grant.ProfileId}'",
                string.Empty);
            return;
        }

        LogFailure("broadcast:no-server", false, string.Empty,
            "[FlareExfil] grant broadcasting resumed: the local manager is a FikaServer");

        FlareExfilActivationPacket packet = new()
        {
            Kind = FlareExfilActivationPacket.EKind.Grant,
            ProfileId = grant.ProfileId,
            PointName = grant.PointName,
            ZoneId = grant.ZoneId,
            SquadId = grant.SquadId,
            FlarePosition = default,
            // 信号弹燃烧的位置。对等端正是需要它来运行发射者机器所跑的同一套出口
            // 链路（破冰船直升机动画）；Vector3.zero 表示该授权
            // 不携带视觉表现，这种情况只出现在未带信号弹位置而构建的授权中。
            FlareDirection = grant.FlarePosition
        };

        // 每个授权一个数据包，发送给每个对等端：它们依据 ProfileId/SquadId 判断该授权
        // 是否覆盖自己，而未被覆盖的对等端仍然会得知该撤离点受门禁限制。
        server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    private static void AllowProfile(string pointName, string profileId, bool gate)
    {
        if (string.IsNullOrEmpty(pointName) || string.IsNullOrEmpty(profileId))
        {
            return;
        }

        lock (Sync)
        {
            if (gate)
            {
                GatedPoints.Add(pointName);
            }

            if (!AllowedProfilesByPoint.TryGetValue(pointName, out var allowed) || allowed == null)
            {
                allowed = new HashSet<string>(StringComparer.Ordinal);
                AllowedProfilesByPoint[pointName] = allowed;
            }

            allowed.Add(profileId);
        }
    }

    private static void AddGatedPoint(string pointName)
    {
        if (string.IsNullOrEmpty(pointName))
        {
            return;
        }

        lock (Sync)
        {
            GatedPoints.Add(pointName);
        }
    }

    /// <summary>
    /// 对授权覆盖的每个 profile，镜像原生侧在发射机器上所做的处理：
    /// 出口离开手动激活状态，并写入原生的按玩家记录。
    /// 出口对象在每台机器上都存在，而这段逻辑只在授权覆盖的机器上运行。
    /// <paramref name="transition"/> 与 <paramref name="native"/> 携带可供观察的结果，用于
    /// 调用方的验收日志行。
    /// </summary>
    private static void ApplyGrant(string pointName, string zoneId, List<string> profiles, out string transition, out string native)
    {
        transition = "point-not-found";
        native = "none";

        var point = FikaExfilManager.FindExfiltrationPoint(pointName);
        if (point == null)
        {
            return;
        }

        EExfiltrationStatus old;
        try
        {
            old = point.Status;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] reading the status of point '{pointName}' failed: {exception}");
            transition = "status-unreadable";
            return;
        }

        if (old == EExfiltrationStatus.AwaitsManualActivation)
        {
            try
            {
                // 原生激活路径：外部 setter 会触发 OnStatusChanged，因此出口 UI
                // 和每个原生订阅者都会跟随这次翻转。
                point.ExternalSetStatus(EExfiltrationStatus.RegularMode, NativeSignature);
            }
            catch (Exception exception)
            {
                FikaGlobals.LogWarning($"[FlareExfil] external activation of point '{pointName}' failed ({exception.Message}), writing the status directly");
                try
                {
                    point.Status = EExfiltrationStatus.RegularMode;
                }
                catch (Exception inner)
                {
                    FikaGlobals.LogError($"[FlareExfil] activating point '{pointName}' failed: {inner}");
                    transition = $"{DescribeStatus(old)} (activation failed)";
                    return;
                }
            }
        }

        var current = old;
        try
        {
            current = point.Status;
        }
        catch (Exception)
        {
            // 按旧状态上报。
        }

        transition = $"{DescribeStatus(old)}->{DescribeStatus(current)}";

        var shots = 0;
        var met = 0;
        var count = profiles?.Count ?? 0;
        if (profiles != null)
        {
            for (var i = 0; i < profiles.Count; i++)
            {
                if (RecordNativeShotProfile(zoneId, profiles[i]))
                {
                    shots++;
                }

                if (RecordMetRequirement(point, profiles[i]))
                {
                    met++;
                }
            }
        }

        native = $"shotList+{shots}/{count},metRequirements+{met}/{count}";
    }

    /// <summary>
    /// 把该 profile 写入出口所评估的原生按 profile 列表
    /// （<c>ExfiltrationPoint._playersMetAllRequirements</c>），这是手动激活需求项所读取的
    /// 玩家级记录。未暴露该列表的运行时仍可正常工作：
    /// 本类的允许列表依旧决定谁可以撤离。
    /// </summary>
    private static bool RecordMetRequirement(ExfiltrationPoint point, string profileId)
    {
        if (point == null || string.IsNullOrEmpty(profileId))
        {
            return false;
        }

        try
        {
            var met = point._playersMetAllRequirements;
            if (met == null || met.Contains(profileId))
            {
                return false;
            }

            met.Add(profileId);
            return true;
        }
        catch (Exception exception)
        {
            LogOnce("metwrite:" + profileId,
                $"[FlareExfil] native met requirement list could not be written for '{profileId}': {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 尽力复制原生按 profile 的记录
    /// （<c>FlareShootDetectorZone._shotPlayerProfiles</c>），撤离点的按玩家需求项正是
    /// 建立在其之上。未暴露该字段的运行时仍可正常工作：
    /// 本类的允许列表依旧决定谁可以撤离。
    /// </summary>
    private static bool RecordNativeShotProfile(string zoneId, string profileId)
    {
        if (string.IsNullOrEmpty(zoneId) || string.IsNullOrEmpty(profileId))
        {
            return false;
        }

        try
        {
            var zones = UnityEngine.Object.FindObjectsOfType<FlareShootDetectorZone>(true);
            if (zones == null)
            {
                return false;
            }

            var written = false;
            foreach (var zone in zones)
            {
                if (zone == null || !string.Equals(ZoneName(zone), zoneId, StringComparison.Ordinal))
                {
                    continue;
                }

                var shotProfiles = zone._shotPlayerProfiles;
                if (shotProfiles != null && !shotProfiles.Contains(profileId))
                {
                    shotProfiles.Add(profileId);
                    written = true;
                }
            }

            return written;
        }
        catch (Exception exception)
        {
            LogOnce("nativewrite:" + zoneId,
                $"[FlareExfil] native shot list of zone '{zoneId}' could not be written: {exception.Message}");
            return false;
        }
    }

    private static bool TryResolveTarget(Vector3 shotPosition, Vector3 playerPosition, out string zoneId, out string pointName, out bool exitZone)
    {
        zoneId = null;
        pointName = null;
        exitZone = false;

        if (!TryFindZone(shotPosition, out var zone, out exitZone))
        {
            // 信号弹出口由场景触发器而非检测区驱动的地图，根本没有任何
            // FlareShootDetectorZone 可供解析。信号弹自身的模板已经证明
            // 发射者发射的是绿色（ExitActivate）信号弹；出口就是信号弹燃烧处
            // 旁边的等待手动激活撤离点，而激活仍只在该撤离点带有
            // 手动激活证据、或已被某一轮流程分类过时才会签发。没有这个分支，在此类地图上
            // 发射的信号弹在本机什么都激活不了——甚至根本无法上报。
            if (!TryResolveSceneTriggerPoint(shotPosition, out pointName, out var evidence))
            {
                return false;
            }

            zoneId = string.Empty;
            exitZone = true;
            LogOnce("scene-trigger:" + pointName,
                $"[FlareExfil] flare resolved point='{pointName}' via=scene-trigger (no detector zone in this scene, " +
                $"evidence='{evidence}') - the flare event is the only evidence this map carries, so the point is " +
                "accepted from the shot position");
            return true;
        }

        zoneId = ZoneName(zone);
        if (string.IsNullOrEmpty(zoneId))
        {
            return false;
        }

        // 属于该区域的出口优先于恰好距离玩家最近的那个出口：
        // 该关联是在战局绑定出口时依据场景几何解析出来的。
        var origin = "zone-association";
        pointName = ResolvePointForZone(zoneId);
        if (string.IsNullOrEmpty(pointName))
        {
            origin = "nearest-exit-to-flare";
            pointName = FindNearestPointName(shotPosition);
        }

        if (string.IsNullOrEmpty(pointName))
        {
            origin = "nearest-exit-to-player";
            pointName = FindNearestPointName(playerPosition);
        }

        if (!string.IsNullOrEmpty(pointName))
        {
            LogOnce($"resolved:{zoneId}:{pointName}",
                $"[FlareExfil] flare resolved zone='{zoneId}' flareType={DescribeFlareType(zone)} exitZone={exitZone} -> point='{pointName}' (via={origin})");
        }

        return !string.IsNullOrEmpty(zoneId) && !string.IsNullOrEmpty(pointName);
    }

    /// <summary>给定检测区所激活的出口，前提是战局绑定过程解析出了它。</summary>
    private static string ResolvePointForZone(string zoneId)
    {
        if (string.IsNullOrEmpty(zoneId))
        {
            return null;
        }

        lock (Sync)
        {
            if (PointByFlareZone.TryGetValue(zoneId, out var associated) && !string.IsNullOrEmpty(associated))
            {
                return associated;
            }
        }

        return null;
    }

    private static bool TryFindZone(Vector3 position, out FlareShootDetectorZone zone, out bool exitZone)
    {
        zone = null;
        exitZone = false;

        var zones = UnityEngine.Object.FindObjectsOfType<FlareShootDetectorZone>(true);
        if (zones == null || zones.Length == 0)
        {
            return false;
        }

        FlareShootDetectorZone containingExit = null;
        FlareShootDetectorZone containingAny = null;
        FlareShootDetectorZone nearest = null;
        var nearestIsExit = false;
        var nearestDistance = float.MaxValue;
        foreach (var candidate in zones)
        {
            if (candidate == null)
            {
                continue;
            }

            var candidateIsExit = IsExitZone(candidate);

            var contains = false;
            try
            {
                var collider = GetZoneCollider(candidate);
                contains = collider != null && collider.bounds.Contains(position);
            }
            catch (Exception)
            {
                // 没有可用碰撞体的区域回退到下面的距离检查。
            }

            if (contains)
            {
                containingAny ??= candidate;
                if (candidateIsExit)
                {
                    containingExit = candidate;
                    break;
                }
            }

            float distance;
            try
            {
                distance = Vector3.Distance(candidate.transform.position, position);
            }
            catch (Exception)
            {
                continue;
            }

            var better = nearest == null
                || (candidateIsExit && !nearestIsExit)
                || (candidateIsExit == nearestIsExit && distance < nearestDistance);
            if (distance <= ZoneRange && better)
            {
                nearestDistance = distance;
                nearest = candidate;
                nearestIsExit = candidateIsExit;
            }
        }

        // 绿色信号弹（ExitActivate）区域始终优先于恰好覆盖同一片地面的
        // 空投、任务或狙击信号弹区域。
        zone = containingExit ?? containingAny ?? nearest;
        exitZone = zone != null && IsExitZone(zone);
        return zone != null;
    }

    private static Collider GetZoneCollider(FlareShootDetectorZone zone)
    {
        var collider = zone.GetComponent<Collider>();
        if (collider != null)
        {
            return collider;
        }

        collider = zone.GetComponentInParent<Collider>();
        if (collider != null)
        {
            return collider;
        }

        return zone.GetComponentInChildren<Collider>(true);
    }

    private static string ZoneName(FlareShootDetectorZone zone)
    {
        try
        {
            var id = zone.zoneID;
            if (!string.IsNullOrEmpty(id))
            {
                return id;
            }

            return zone.gameObject != null ? zone.gameObject.name : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 距离该位置最近的普通出口点，需在 <see cref="PointRange"/> 之内。绿色信号弹出口的
    /// 信号弹区域就位于出口处，因此最近的那个撤离点正是该信号弹
    /// 所属的撤离点。秘密出口被排除在外：它们的激活并非由信号弹驱动。
    /// </summary>
    private static string FindNearestPointName(Vector3 position)
    {
        var controller = ExfiltrationController.Instance;
        if (controller == null)
        {
            return null;
        }

        var bestName = null as string;
        var bestDistance = float.MaxValue;

        bestName = ConsiderPoints(controller.ExfiltrationPoints, position, bestName, ref bestDistance);
        return bestName;
    }

    private static string ConsiderPoints(ExfiltrationPoint[] points, Vector3 position, string bestName, ref float bestDistance)
    {
        if (points == null)
        {
            return bestName;
        }

        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (point == null)
            {
                continue;
            }

            float distance;
            try
            {
                distance = Vector3.Distance(point.transform.position, position);
            }
            catch (Exception)
            {
                continue;
            }

            if (distance <= PointRange && distance < bestDistance)
            {
                bestDistance = distance;
                bestName = point.Settings != null ? point.Settings.Name : null;
            }
        }

        return bestName;
    }

    private static string LocalSquadId(Player player)
    {
        if (player == null)
        {
            return string.Empty;
        }

        return string.IsNullOrEmpty(player.TeamId) ? player.GroupId : player.TeamId;
    }

    private static string FindSquadId(string profileId)
    {
        var shooter = FindCoopPlayer(profileId);
        return shooter == null ? string.Empty : LocalSquadId(shooter);
    }

    private static FikaPlayer FindCoopPlayer(string profileId)
    {
        try
        {
            var handler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
            if (handler?.Players == null || handler.Players.Count == 0)
            {
                return null;
            }

            foreach (var candidate in handler.Players.Values)
            {
                if (candidate != null && string.Equals(candidate.ProfileId, profileId, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] coop player lookup failed: {exception}");
        }

        return null;
    }

    /// <summary>小队的 profile（除发射者以外的所有人）。</summary>
    private static List<string> CollectGroupProfiles(string squadId, string shooterProfileId)
    {
        if (string.IsNullOrEmpty(squadId))
        {
            return null;
        }

        try
        {
            var handler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
            if (handler?.Players == null || handler.Players.Count == 0)
            {
                return null;
            }

            List<string> profiles = [];
            foreach (var candidate in handler.Players.Values)
            {
                if (candidate == null || string.Equals(candidate.ProfileId, shooterProfileId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(LocalSquadId(candidate), squadId, StringComparison.Ordinal))
                {
                    profiles.Add(candidate.ProfileId);
                }
            }

            return profiles.Count > 0 ? profiles : null;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] squad lookup failed: {exception}");
            return null;
        }
    }

    private static bool IsPlayerBanned(int raidId)
    {
        try
        {
            var controller = ExfiltrationController.Instance;
            var banned = controller?.BannedPlayers;
            return banned != null && banned.Contains(raidId);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Reject(string profileId, string reason)
    {
        FikaGlobals.LogWarning($"[FlareExfil] rejected profile={profileId ?? string.Empty} reason={reason}");
    }

    /// <summary>
    /// 本机上某检测区所接入的工作：先查每战局一次的表格，
    /// 再查实时场景，因此即使授权在分类之前（或没有分类）到达也仍能解析。
    /// 免疫是粘性的；其余情况完全保持原有的门禁规则
    /// （<c>exitZone || IsGatedPoint</c>），因此过去能开启出口的区域不会失去该能力。
    /// </summary>
    private static ZoneKind KindOf(string zoneId, bool exitZone, string pointName)
    {
        if (!string.IsNullOrEmpty(zoneId))
        {
            lock (Sync)
            {
                if (ZoneKinds.TryGetValue(zoneId, out var known) && known == ZoneKind.Immunity)
                {
                    return ZoneKind.Immunity;
                }
            }

            var zone = FindZoneById(zoneId);
            if (zone != null && ImmunityEvidence(zone, IsGatedPoint(pointName)) != null)
            {
                return ZoneKind.Immunity;
            }
        }

        return exitZone || IsGatedPoint(pointName) ? ZoneKind.ExitGate : ZoneKind.Other;
    }

    /// <summary>
    /// 表明该区域接入的是原生狙击免疫注册表而非某个出口的证据；
    /// 若并非如此则返回 null。
    /// <para>
    /// 主要判据，取自 47888 interop 成员列表
    /// （类见 <c>analysis/flare-exfil-verification/interop-47888-full.txt:407404</c>，
    /// 收集器见 <c>:407423</c>）：区域对象本身、其某个父对象或某个子对象
    /// 带有 <c>EFT.Interactive.PlayersWithImmuneToSniperFire</c> 组件。该类拥有
    /// <c>playersWithImmune : HashSet&lt;Int32&gt;</c> 以及 <c>AddImmunePlayer(Int32)</c> /
    /// <c>IsPlayerImmuneForFire(Int32)</c>，而狙击区域读取的正是这个集合；其子类
    /// <c>PlayersWithImmuneToSniperFireFlareCollector</c> 在消费 <c>FlareShootZoneEvent</c>
    /// 时会加入发射玩家的战局 id，它也就是 Ground Zero 中名为
    /// <c>FlareFireCollector_zone</c> 的对象。收集器自身的 <c>flareTypeForImmune</c> 也会作为
    /// 证据的一部分打印出来，因此该日志行可以显示两个对象处理的是同一种信号弹类型。
    /// </para>
    /// <para>
    /// 当场景把收集器放在兄弟对象上时的回退判据：一个绿色（<c>ExitActivate</c>）
    /// 区域，它不施加任何门禁，所在战局确实存在此类注册表，并且其对象名称
    /// 表明了它的身份。
    /// </para>
    /// </summary>
    private static string ImmunityEvidence(FlareShootDetectorZone zone, bool pointGated)
    {
        if (zone == null)
        {
            return null;
        }

        try
        {
            var own = zone.GetComponent<PlayersWithImmuneToSniperFire>();
            if (own != null)
            {
                return DescribeRegistry("self", own);
            }

            var parent = zone.GetComponentInParent<PlayersWithImmuneToSniperFire>();
            if (parent != null)
            {
                return DescribeRegistry("parent", parent);
            }

            var child = zone.GetComponentInChildren<PlayersWithImmuneToSniperFire>(true);
            if (child != null)
            {
                return DescribeRegistry("child", child);
            }
        }
        catch (Exception exception)
        {
            LogOnce("immuneprobe:" + ZoneName(zone),
                $"[FlareExfil] immunity registry lookup on zone '{ZoneName(zone)}' failed: {exception.Message}");
        }

        if (pointGated || !IsExitZone(zone))
        {
            return null;
        }

        var zoneId = ZoneName(zone);
        if (!LooksLikeImmunityZone(zoneId))
        {
            return null;
        }

        var registries = CountImmunityRegistries();
        return registries > 0 ? $"name:{ObjectNameOf(zoneId)}(registries={registries})" : null;
    }

    private static string DescribeRegistry(string where, PlayersWithImmuneToSniperFire registry)
    {
        var type = RegistryTypeName(registry);
        var text = where + ":" + type;
        try
        {
            var collector = registry.TryCast<PlayersWithImmuneToSniperFireFlareCollector>();
            if (collector == null)
            {
                // 仅有普通注册表（狙击管理器）已足以支撑该结论，但无法从中读出
                // 信号弹类型。每种类型说明一次，而不是默默丢掉
                // 结论常常依赖的那一半证据。
                LogOnce("immuneprobe-cast:" + type,
                    $"[FlareExfil] immunity registry '{type}' is not a PlayersWithImmuneToSniperFireFlareCollector (where={where}), " +
                    "so its flareTypeForImmune is missing from the zone evidence");
                return text;
            }

            text += $"(flareTypeForImmune={DescribeFlareType(collector.flareTypeForImmune)})";
        }
        catch (Exception exception)
        {
            LogOnce("immuneprobe-flaretype:" + type,
                $"[FlareExfil] immunity registry '{type}' could not report flareTypeForImmune ({exception.Message}); " +
                "the zone evidence keeps the registry type only");
        }

        return text;
    }

    private static string RegistryTypeName(PlayersWithImmuneToSniperFire registry)
    {
        try
        {
            var type = registry.GetIl2CppType();
            if (type != null && !string.IsNullOrEmpty(type.Name))
            {
                return type.Name;
            }
        }
        catch (Exception)
        {
            // 下面回退到托管代理类型。
        }

        try
        {
            return registry.GetType().Name;
        }
        catch (Exception)
        {
            return nameof(PlayersWithImmuneToSniperFire);
        }
    }

    /// <summary>区域 id 的最后一段路径：<c>Sandbox_Design_Main:/EXITS/FlareFireCollector_zone</c> -&gt; <c>FlareFireCollector_zone</c>。</summary>
    private static string ObjectNameOf(string zoneId)
    {
        if (string.IsNullOrEmpty(zoneId))
        {
            return string.Empty;
        }

        var cut = zoneId.LastIndexOf('/');
        if (cut >= 0 && cut + 1 < zoneId.Length)
        {
            return zoneId[(cut + 1)..];
        }

        cut = zoneId.LastIndexOf(':');
        return cut >= 0 && cut + 1 < zoneId.Length ? zoneId[(cut + 1)..] : zoneId;
    }

    /// <summary>
    /// Ground Zero 给狙击免疫区域起的对象名。仅作为
    /// <see cref="ImmunityEvidence"/> 的回退判据使用，从不单独使用。
    /// </summary>
    private static bool LooksLikeImmunityZone(string zoneId)
    {
        return !string.IsNullOrEmpty(zoneId)
            && zoneId.IndexOf("FlareFireCollector", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static PlayersWithImmuneToSniperFire[] FindImmunityRegistries()
    {
        try
        {
            // FindObjectsOfType 返回 Il2Cpp 的数组包装类型；改用托管数组可以让
            // 各调用点不必出现该包装类型。
            PlayersWithImmuneToSniperFire[] registries = UnityEngine.Object.FindObjectsOfType<PlayersWithImmuneToSniperFire>(true);
            return registries ?? [];
        }
        catch (Exception exception)
        {
            LogOnce("immuneregistry", $"[FlareExfil] sniper immunity registry lookup failed: {exception.Message}");
            return [];
        }
    }

    private static int CountImmunityRegistries()
    {
        return FindImmunityRegistries().Length;
    }

    /// <summary>
    /// 某个 profile 的数值型战局 id。它是原生信号弹记录
    /// （<c>FlareShootZoneEvent.PlayerRaidID</c>）与狙击免疫集合
    /// （<c>playersWithImmune : HashSet&lt;Int32&gt;</c>）的键。
    /// </summary>
    private static bool TryResolveRaidId(string profileId, out int raidId, out string reason)
    {
        raidId = 0;
        reason = null;
        if (string.IsNullOrEmpty(profileId))
        {
            reason = "empty profile id";
            return false;
        }

        try
        {
            var player = Singleton<GameWorld>.Instance?.GetAlivePlayerByProfileID(profileId);
            if (player == null)
            {
                reason = "unknown player";
                return false;
            }

            raidId = player.RaidId;
            if (raidId <= 0)
            {
                reason = $"raid id {raidId}";
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// 在本机写入信号弹所授予的原生狙击免疫，覆盖主机判定为该激活所及的每个
    /// profile：它们的战局 id 会写入每个
    /// <c>PlayersWithImmuneToSniperFire</c> 注册表，即狙击区域通过
    /// <c>IsPlayerImmuneForFire(Int32)</c> 读取、且 <c>HasSniperImmunityTriggerRequirement</c>
    /// 游戏触发器同样会读取的那个集合
    /// （<c>playersWithImmune : HashSet&lt;Int32&gt;</c>）。
    /// <para>
    /// 这里不编造任何持续时间：原生集合没有过期字段，而 <c>AddImmunePlayer</c>
    /// 就是一次 <c>HashSet.Add</c>，因此重复发射信号弹会刷新该条目，唯一的退出途径是
    /// 地图触发器所调用的原生 <c>RemoveImmunePlayer</c>。日志行所报告的正是通过
    /// <c>IsPlayerImmuneForFire</c> 回读到的结果。
    /// </para>
    /// <para>
    /// 每台机器都自行写入这些 id，因为原版信号弹只会到达发射它的那台机器
    /// （区域消费的是本地的 <c>FlareSuccessEvent</c>，并在本地触发
    /// <c>FlareShootZoneEvent</c>），而每台机器都模拟自己的狙击区域。
    /// </para>
    /// <para>
    /// 成功路径同样以原版面向玩家的那部分收尾
    /// （<see cref="ShowFlareSuccessFeedback"/>）：被覆盖玩家自己的机器会弹出与原版相同的
    /// <c>SniperFlareZoneDisabledNotification</c>，因为这次激活从头到尾都不会进入
    /// 拥有该通知的原生事件分支。
    /// </para>
    /// </summary>
    private static void ApplyImmunity(string zoneId, List<string> profiles, string source)
    {
        var total = profiles?.Count ?? 0;
        var registries = FindImmunityRegistries();
        if (registries.Length == 0)
        {
            // 这是状态而非一次性日志：稍后才出现的注册表必须作为一次恢复被
            // 看见，否则战局会一直把最初的失败当作当前事实。
            LogFailure("immunity-registry:" + zoneId, true,
                $"[FlareExfil] immunity zone='{zoneId}' has no PlayersWithImmuneToSniperFire registry on this machine, " +
                $"so the sniper keeps firing at {total} player(s) (source={source})",
                string.Empty);
            return;
        }

        LogFailure("immunity-registry:" + zoneId, false, string.Empty,
            $"[FlareExfil] immunity zone='{zoneId}' can be written again: {registries.Length} PlayersWithImmuneToSniperFire " +
            $"registr(ies) are present (source={source})");

        // 下面的反馈只属于一个玩家：本机自己的玩家。它需要的两个事实
        // 都在写入之前读取，因为事后再回读无法区分“是这次调用写进去的”
        // 与“原生链路本来就有”。
        var localProfileId = Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId;
        var localCovered = IsCovered(profiles, localProfileId);
        var localRaidId = 0;
        var localWasImmune = false;
        if (localCovered && TryResolveRaidId(localProfileId, out var resolvedLocalRaidId, out _))
        {
            localRaidId = resolvedLocalRaidId;
            localWasImmune = IsImmuneInAnyRegistry(registries, resolvedLocalRaidId);
        }

        var resolved = new List<int>();
        var unresolved = new List<string>();
        if (profiles != null)
        {
            for (var i = 0; i < profiles.Count; i++)
            {
                var profileId = profiles[i];
                if (string.IsNullOrEmpty(profileId))
                {
                    continue;
                }

                if (!TryResolveRaidId(profileId, out var raidId, out var reason))
                {
                    unresolved.Add($"{profileId}:{reason}");
                    continue;
                }

                if (resolved.Contains(raidId))
                {
                    continue;
                }

                resolved.Add(raidId);
                for (var r = 0; r < registries.Length; r++)
                {
                    try
                    {
                        registries[r].AddImmunePlayer(raidId);
                    }
                    catch (Exception exception)
                    {
                        LogOnce("immunity-write:" + zoneId,
                            $"[FlareExfil] AddImmunePlayer({raidId}) on registry '{RegistryTypeName(registries[r])}' failed: {exception.Message}");
                    }
                }
            }
        }

        var verified = 0;
        var missing = new List<int>();
        for (var i = 0; i < resolved.Count; i++)
        {
            if (IsImmuneInAnyRegistry(registries, resolved[i]))
            {
                verified++;
            }
            else
            {
                missing.Add(resolved[i]);
            }
        }

        var raidIds = resolved.Count == 0 ? "none" : string.Join(",", resolved);
        LogOnce("immunity:" + zoneId,
            $"[FlareExfil] immunity granted zone='{zoneId}' for {total} players (raidIds=[{raidIds}]) verified={verified}/{total}" +
            $" registries={registries.Length}" +
            (unresolved.Count > 0 ? $" unresolved=[{string.Join(";", unresolved)}]" : string.Empty) +
            (verified < total ? $" missing=[{(missing.Count == 0 ? "none" : string.Join(",", missing))}]" : string.Empty) +
            $" source={source}");

        LogFailure("immunity-verified:" + zoneId, verified < total,
            $"[FlareExfil] immunity partial zone='{zoneId}' verified={verified}/{total} resolved={resolved.Count} " +
            $"registries={registries.Length} - the native sniper immunity set did not take every raid id, " +
            $"so the sniper can still fire at them (source={source})",
            $"[FlareExfil] immunity complete again zone='{zoneId}' verified={verified}/{total} (source={source})");

        // 仅在成功路径上，且仅在授权覆盖的玩家所在机器上：刚才的回读已经
        // 证明原生集合对该本地玩家给出肯定答复，这正是
        // 原版用“Flare signal received. You can extract safely!”告知该玩家的状态。
        // 被拒绝的上报、无法写入的注册表或未能解析的战局 id 都不会走到这里。
        if (localCovered && localRaidId > 0 && IsImmuneInAnyRegistry(registries, localRaidId))
        {
            ShowFlareSuccessFeedback(zoneId, localProfileId, source, localWasImmune);
        }
    }

    /// <summary>
    /// 成功激活中面向玩家的那部分，也是原版所显示的同一则通知：
    /// 一旦信号弹把玩家放入原生狙击免疫集合，游戏就会弹出
    /// <see cref="SniperFlareZoneDisabledNotification"/>（"Flare signal received. You can extract
    /// safely!" / CN "道路已开放"）。该弹窗位于原生收集器自己的事件分支中
    /// （<c>PlayersWithImmuneToSniperFireFlareCollector._flareExitSucceedNotification</c>），该分支只在
    /// 消费了自身本地 <c>FlareSuccessEvent</c> 的机器上运行——被复制过来的信号弹
    /// 永远到不了那里，因此这里通过游戏自己的通知管理器显示同一个通知对象，
    /// 这也正是原生分支所做的调用。
    /// <para>
    /// 每个被覆盖的玩家与区域一行：只有被覆盖玩家自己的机器才会带着自己的
    /// profile id 走到这里，因此主机与客户端各自为自己弹出，而每 10 秒一次的授权重新发布
    /// 不会让它弹两次。只有在成功之后才会记录该展示，因此游戏未接受的弹窗
    /// 会在下次授权时重试，而不会就此丢失。
    /// </para>
    /// </summary>
    private static void ShowFlareSuccessFeedback(string zoneId, string profileId, string source, bool nativeAlreadyImmune)
    {
        var key = zoneId + ":" + profileId;
        lock (Sync)
        {
            if (FeedbackShown.Contains(key))
            {
                return;
            }
        }

        try
        {
            var notification = new SniperFlareZoneDisabledNotification();
            NotificationManager.DisplayNotification(notification);

            // 游戏里没有任何东西会隐藏本类显示的通知：原生收集器
            // 自己拥有并管理它自己的那一份（_flareExitSucceedNotification），而我们这一份
            // 不归任何控制器管理。因此该弹窗在此登记，并在其显示时间结束后
            // 由 FeedbackTick 撤下。
            var seconds = FeedbackSeconds;
            lock (Sync)
            {
                FeedbackShown.Add(key);
                if (seconds > 0f)
                {
                    FeedbackPopups.Add(new FeedbackPopup
                    {
                        Notification = notification,
                        Key = key,
                        ZoneId = zoneId,
                        ProfileId = profileId,
                        Seconds = seconds,
                        HideAt = Time.time + seconds
                    });
                }
            }

            LogOnce("feedback:" + key,
                $"[FlareExfil] flare success feedback shown zone='{zoneId}' local='{profileId}' " +
                $"notification={NotificationTypeName(notification)} text='{DescribeNotification(notification)}' " +
                $"nativeAlreadyImmune={nativeAlreadyImmune} (source={source})");
        }
        catch (Exception exception)
        {
            LogFailure("feedback:" + key, true,
                $"[FlareExfil] flare success feedback could not be shown for zone='{zoneId}' local='{profileId}' " +
                $"({exception.Message}) - the immunity is written, but this player sees no 'can extract' message (source={source})",
                string.Empty);
        }
    }

    /// <summary>
    /// 在每个已显示的成功弹窗到达显示时间后，将其从屏幕上撤下。
    /// 由 exfil 管理器自己的 Update 驱动（该边界在两类机器上都已在驱动
    /// <see cref="HostTick"/>），因此不涉及引擎协程，也不涉及引擎 -&gt; 托管的
    /// 回调，而这正是崩溃报告所排除的形态。
    /// <para>
    /// 由结构保证幂等：每个条目只会离开 <see cref="FeedbackPopups"/> 一次，
    /// 本类为每个弹窗保留一个通知对象，因此隐藏某个对象既不会
    /// 对同一弹窗重复执行，也不会波及仍在显示时间内的弹窗。空
    /// 列表只需一次加锁便返回，因此没有信号弹的战局每帧没有额外开销。
    /// </para>
    /// </summary>
    internal static void FeedbackTick()
    {
        // 同时也驱动出口视觉重放：重放会等待发射者的信号弹对象或其
        // 到期时间，而这正是主机与客户端每帧都已在运行的 tick。
        if (HasPendingVisuals())
        {
            try
            {
                FlushPendingVisuals();
            }
            catch (Exception exception)
            {
                FikaGlobals.LogError($"[FlareExfil] heli visual replay failed: {exception}");
            }
        }

        if (!HasFeedbackPopups())
        {
            return;
        }

        try
        {
            var now = Time.time;
            var due = null as List<FeedbackPopup>;
            lock (Sync)
            {
                for (var i = 0; i < FeedbackPopups.Count; i++)
                {
                    var popup = FeedbackPopups[i];
                    if (now >= popup.HideAt)
                    {
                        (due ??= []).Add(popup);
                    }
                }
            }

            if (due == null)
            {
                return;
            }

            // 只有确实有工作要做的帧才读取战局状态：新世界（全新战局、
            // 转运重载）会清理该表，因此上一战局的弹窗会在此被丢弃，
            // 也永远不会碰到过期的通知对象。
            SyncRaidState();

            for (var i = 0; i < due.Count; i++)
            {
                var popup = due[i];
                var claimed = false;
                lock (Sync)
                {
                    // 把条目从表中取出即为“认领”：它只会成功一次，因此每个弹窗只会
                    // 被隐藏一次，而因世界切换被丢弃的弹窗根本不会被隐藏。
                    claimed = FeedbackPopups.Remove(popup);
                }

                if (claimed)
                {
                    HideFeedbackPopup(popup);
                }
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] taking the flare success feedback down failed: {exception}");
        }
    }

    private static bool HasFeedbackPopups()
    {
        lock (Sync)
        {
            return FeedbackPopups.Count > 0;
        }
    }

    /// <summary>
    /// 移除一个已显示的弹窗。<c>Hide()</c> 是该通知家族自身的移除调用：
    /// 它由此处所显示类的基类 <c>FireFlareForExitNotification</c> 声明，而
    /// 基类通知携带视图宿主所响应的 <c>OnForceHide</c> 事件（
    /// 通知视图通过自身的强制完成路径隐藏，宿主随后移除该视图）。
    /// 原生收集器拥有完全相同的通知副本，因此也有同样的调用可用。
    /// </summary>
    private static void HideFeedbackPopup(FeedbackPopup popup)
    {
        try
        {
            popup.Notification?.Hide();

            LogOnce("feedback-hidden:" + popup.Key,
                $"[FlareExfil] flare success feedback hidden after {popup.Seconds:0.#}s zone='{popup.ZoneId}' " +
                $"local='{popup.ProfileId}' notification=SniperFlareZoneDisabledNotification " +
                "(switch 'Flare Extract: Feedback Seconds')");
        }
        catch (Exception exception)
        {
            LogFailure("feedback-hide:" + popup.Key, true,
                $"[FlareExfil] flare success feedback could not be hidden zone='{popup.ZoneId}' " +
                $"local='{popup.ProfileId}' ({exception.Message}) - the banner stays on screen until the " +
                "raid ends (source=Hide after the display time)",
                string.Empty);
        }
    }

    /// <summary>
    /// 通知将要显示的文本，仅用于日志行。原生通知的每个访问器
    /// 都可能抛出异常，因此读取失败只会作为日志细节，而不会连带把信号弹
    /// 流程拖垮。
    /// </summary>
    private static string DescribeNotification(EFT.Communications.Notification notification)
    {
        try
        {
            var description = notification?.Description;
            if (description == null)
            {
                return "none";
            }

            var text = description.ToString();
            return string.IsNullOrWhiteSpace(text) ? "none" : text;
        }
        catch (Exception exception)
        {
            return "unreadable (" + exception.Message + ")";
        }
    }

    /// <summary>
    /// 通知的原生类名，仅用于日志行。无法读取时回退到代理类型名，
    /// 因此即便原生类型读不到，该日志行也能指出是哪个通知。
    /// </summary>
    private static string NotificationTypeName(EFT.Communications.Notification notification)
    {
        try
        {
            var type = notification?.GetIl2CppType();
            if (type != null && !string.IsNullOrEmpty(type.Name))
            {
                return type.Name;
            }
        }
        catch (Exception)
        {
            // 下面回退到托管代理类型。
        }

        try
        {
            return notification?.GetType().Name ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// 当授权覆盖 <paramref name="profileId"/> 时为 true。使用序数比较，与本类中
    /// 其他所有 profile id 检查一致。
    /// </summary>
    private static bool IsCovered(List<string> profiles, string profileId)
    {
        if (profiles == null || string.IsNullOrEmpty(profileId))
        {
            return false;
        }

        for (var i = 0; i < profiles.Count; i++)
        {
            if (string.Equals(profiles[i], profileId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 本机的任一注册表是否对该战局 id 给出“免疫”答复——这正是狙击区域
    /// 所执行的查询，因此此处为 true 意味着狙击手确实对该玩家停火。
    /// </summary>
    private static bool IsImmuneInAnyRegistry(PlayersWithImmuneToSniperFire[] registries, int raidId)
    {
        for (var r = 0; r < registries.Length; r++)
        {
            try
            {
                if (registries[r].IsPlayerImmuneForFire(raidId))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 无法作答的注册表按不免疫处理，与之前完全一致。
            }
        }

        return false;
    }

    /// <summary>
    /// 授权在本机覆盖哪些 profile：发射者、
    /// <c>Flare Extract: Squad Shares Activation</c> 开启时发射者所在的小队，以及（当授权覆盖本地
    /// 玩家时）本地玩家本身，这样即使某台机器对小队的视图不完整，
    /// 它也会贡献自己的战局 id。
    /// </summary>
    private static List<string> CoveredProfiles(FlareExfilActivationPacket packet, string ownProfileId, bool covered)
    {
        var profiles = new List<string>();
        if (!string.IsNullOrEmpty(packet.ProfileId))
        {
            profiles.Add(packet.ProfileId);
        }

        if (SquadSharesActivation && !string.IsNullOrEmpty(packet.SquadId))
        {
            var squad = CollectGroupProfiles(packet.SquadId, packet.ProfileId);
            if (squad != null)
            {
                profiles.AddRange(squad);
            }
        }

        if (covered && !string.IsNullOrEmpty(ownProfileId) && !profiles.Contains(ownProfileId))
        {
            profiles.Add(ownProfileId);
        }

        return profiles;
    }

    /// <summary>
    /// 在失败开始时上报失败、在其结束时上报恢复，以失败项为键。
    /// <para>
    /// 与 <see cref="LogOnce"/> 不同，它不会掩盖“先失败、之后恢复”：首次
    /// 观察正常时不打印任何内容（因此正常战局保持安静），首次失败会打印完整的
    /// 失败行，而回退到正常时则打印恢复行。
    /// </para>
    /// </summary>
    private static void LogFailure(string key, bool failing, string failureLine, string recoveryLine)
    {
        bool changed;
        lock (Sync)
        {
            if (Failing.TryGetValue(key, out var wasFailing))
            {
                changed = wasFailing != failing;
            }
            else
            {
                changed = failing;
            }

            Failing[key] = failing;
        }

        if (!changed)
        {
            return;
        }

        FikaGlobals.LogWarning(failing ? failureLine : recoveryLine);
    }

    /// <summary>interop 数组中有多少个条目是已销毁的引用。</summary>
    // ---------------------------------------------------------------------------------------------------
    // 出口视觉复制：发射者机器自行播放的内容必须在每台机器上都播放。
    //
    // 信号弹在发射它的机器上触发的链路是
    //   Player/FirearmController.InitiateFlare -> 本地事件总线上的 <c>FlareSuccessEvent</c>
    //   -> 场景中每个 <c>_flareEventType</c> 匹配的 <c>EFT.GameTriggers.TriggerFlareSuccess</c>
    //   -> 携带该触发器 id 的 <c>TriggersEmitter</c>
    //   -> 地图的处理程序（<c>HandlerAnimator</c>、<c>HandlerGameObjectState</c>、
    //      <c>HandlerPlayContinousSound</c>、<c>HandlerEffect</c>、<c>HandlerExfiltration</c> 等），
    // 正是它驱动破冰船直升机的动画并让出口可用。
    //
    // 上述每个对象都存在于每台机器已加载的场景中，因此这条链路可以简单地在
    // 每个对等端上再跑一遍。这里没有发明任何东西：调用的正是确切的
    // 原生入口点（<c>TriggerFlareSuccess.OnFlareSuccess</c>），并传入发射者机器所用的
    // 同一份事件数据，至于它意味着什么由地图自己决定。
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// 等待自身本地信号弹事件或等待超时的重放，以 profile id 为键。授权可能
    /// 在发射者的信号弹数据包之前到达；此时就应用视觉会在
    /// 地图处理程序所读取的信号弹对象尚不存在的情况下启动场景链路。
    /// </summary>
    private static readonly Dictionary<string, PendingVisual> PendingVisuals = new(StringComparer.Ordinal);

    /// <summary>
    /// 本机已应用过的视觉，键的构造方式与 <see cref="VisualIdOf"/> 一致。授权每
    /// <see cref="RepublishInterval"/> 秒重新发布一次，而发射者自己的机器既会看到
    /// 本地信号弹也会收到授权，因此若没有它，动画会在每次发布时重复启动。
    /// </summary>
    private static readonly HashSet<string> AppliedVisuals = new(StringComparer.Ordinal);

    /// <summary>
    /// 待处理的视觉在无论如何都会应用之前，等待发射者本地信号弹事件的秒数。
    /// 从未见到信号弹对象的机器仍会播放出口动画，而不是什么都没有。
    /// </summary>
    private const float VisualWaitSeconds = 1.5f;

    /// <summary>
    /// 开关 'Flare Extract: Replicate Exit Visual'。实时读取，因此在配置管理器中的修改
    /// 无需重启即可对下一发信号弹生效。
    /// </summary>
    internal static bool ReplicatesExitVisual
    {
        get
        {
            try
            {
                var entry = FikaPlugin.Instance?.Settings?.FlareReplicateExitVisual;
                return entry == null || entry.Value;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// 由发射者自己的枪械控制器在枪响之后立即调用：原生链路在这里通常是
    /// 自行运行的，因此只有在它没有运行时才需要应用视觉（以本地信号弹
    /// 事件为判断依据，见 <see cref="MarkLocalFlareEvent"/>）。
    /// </summary>
    private static void QueueLocalVisual(Ammo flareItem, string pointName, string profileId, Vector3 shotPosition)
    {
        if (!ReplicatesExitVisual || string.IsNullOrEmpty(profileId))
        {
            return;
        }

        var template = flareItem?.AmmoTemplate;
        if (template == null || !template.ContainsType(FlareEventType.ExitActivate))
        {
            return;
        }

        var key = VisualIdOf(pointName, profileId, shotPosition);
        var applied = false;
        lock (Sync)
        {
            applied = AppliedVisuals.Contains(key);
            if (!applied)
            {
                // 原生链路会在枪响后片刻自行在本机运行，而这段等待正是让它能够
                // 停止本次重放的机制（见 MarkLocalFlareEvent）。没有这段等待，本机就会把
                // 动画播放两次。
                PendingVisuals[profileId] = new PendingVisual
                {
                    PointName = pointName ?? string.Empty,
                    Position = shotPosition,
                    Holder = flareItem,
                    HolderArrived = true,
                    ReadyAt = Time.time + VisualWaitSeconds
                };
            }
        }

        if (applied)
        {
            return;
        }

        // 只有此前从未运行过任何东西的情况才会走到超时：此时本机改为自行播放
        // 该链路，而不是什么都不显示。现在就把这一点记录下来，以便下一次战局能区分两种情况。
        FikaGlobals.LogWarning($"[FlareExfil] heli: point='{pointName}' open={DescribeLocalStatus(pointName)} " +
            $"visual=local-armed firedBy='{profileId}' replayIn={VisualWaitSeconds:F1}s " +
            "unless the native chain runs here first");
    }

    /// <summary>
    /// 绿色信号弹的本地原生链路已在本机运行：发射它的机器永远不需要
    /// 重放，因此待处理的视觉被标记为已满足而不是被应用。每个本地信号弹事件调用一次，
    /// 这也是证据行在这里而不是在重放路径中打印的原因。
    /// </summary>
    internal static void MarkLocalFlareEvent(string profileId, FlareEventType flareType, string source)
    {
        if (string.IsNullOrEmpty(profileId))
        {
            return;
        }

        var pending = false;
        lock (Sync)
        {
            pending = PendingVisuals.Remove(profileId);
        }

        if (flareType == FlareEventType.ExitActivate || pending)
        {
            LogOnce("localflare:" + profileId,
                $"[FlareExfil] heli: point=? open=? visual=local firedBy='{profileId}' flareType={DescribeFlareType(flareType)} " +
                $"chain=ran-local source={source}");
        }
    }

    /// <summary>
    /// 某个对等端的信号弹事件已为本机排入队列。持有者必须是发射者所发射的那个对象，
    /// 因此队列会等待该对象在此出现（信号弹数据包）或等待其超时。
    /// </summary>
    private static void QueueFlareVisual(FlareExfilActivationPacket packet)
    {
        if (packet == null || string.IsNullOrEmpty(packet.ProfileId))
        {
            return;
        }

        if (packet.FlareDirection == Vector3.zero)
        {
            // 没有携带位置（较旧的授权，或者本来就没有位置的免疫授权）：没有
            // 东西可摆放，而把链路从世界原点开始启动也是错的。
            return;
        }

        if (!ReplicatesExitVisual)
        {
            LogOnce("visual-off:" + packet.ProfileId + ":" + packet.PointName,
                $"[FlareExfil] heli: point='{packet.PointName}' open=? visual=skipped firedBy='{packet.ProfileId}' " +
                $"reason='Flare Extract: Replicate Exit Visual is off'");
            return;
        }

        var key = VisualIdOf(packet.PointName, packet.ProfileId, packet.FlareDirection);
        var applied = false;
        lock (Sync)
        {
            applied = AppliedVisuals.Contains(key);
            if (!applied)
            {
                var holder = FindFlareHolder(packet.ProfileId);
                var now = Time.time;
                PendingVisuals[packet.ProfileId] = new PendingVisual
                {
                    PointName = packet.PointName,
                    Position = packet.FlareDirection,
                    Holder = holder,
                    HolderArrived = holder != null,
                    ReadyAt = holder != null ? now : now + VisualWaitSeconds
                };
            }
        }

        if (applied)
        {
            return;
        }

        // 信号弹数据包可能比授权晚一帧到达：在此询问能让动画落在信号弹上
        // 而不是数据包上，下面的 tick 则覆盖另一种顺序。
        MatchArrivedFlare(packet.ProfileId);
    }

    /// <summary>
    /// 对待处理的视觉做一次遍历。由主机与客户端的 tick 调用，因此重放会在其
    /// 持有者一出现时立即应用，且绝不会阻塞某一帧。
    /// </summary>
    internal static void FlushPendingVisuals()
    {
        List<string> flush = null;
        var now = Time.time;
        lock (Sync)
        {
            foreach (var entry in PendingVisuals)
            {
                var applied = AppliedVisuals.Contains(VisualIdOf(entry.Value.PointName, entry.Key, entry.Value.Position));
                if (applied)
                {
                    (flush ??= []).Add(entry.Key);
                    continue;
                }

                if (!entry.Value.HolderArrived && now >= entry.Value.ReadyAt)
                {
                    entry.Value.Holder = FindFlareHolder(entry.Key);
                    entry.Value.HolderArrived = entry.Value.Holder != null;
                    entry.Value.ReadyAt = now;
                }

                if (entry.Value.HolderArrived && now >= entry.Value.ReadyAt)
                {
                    (flush ??= []).Add(entry.Key);
                }
            }

            if (flush != null)
            {
                for (var i = 0; i < flush.Count; i++)
                {
                    ApplyVisualLocked(flush[i]);
                }
            }
        }
    }

    /// <summary>
    /// 重放所需的持有者：发射者所发射的信号弹对象，也就是地图处理程序在发射者
    /// 机器上从信号弹事件中读出的那个对象。若授权在发射者的信号弹数据包之前
    /// 到达，持有者就仍未设置，视觉会继续等待（见 <see cref="VisualWaitSeconds"/>）。
    /// </summary>
    private static void MatchArrivedFlare(string profileId)
    {
        if (string.IsNullOrEmpty(profileId))
        {
            return;
        }

        lock (Sync)
        {
            if (!PendingVisuals.TryGetValue(profileId, out var pending) || pending.HolderArrived)
            {
                return;
            }

            var holder = FindFlareHolder(profileId);
            if (holder == null)
            {
                return;
            }

            pending.Holder = holder;
            pending.HolderArrived = true;
        }
    }

    /// <summary>
    /// 查找数据包所携带的信号弹对象。发射者自己的机器是在枪械控制器中创建它的；
    /// 在对等端上，应用信号弹数据包时运行的是同一条代码路径
    /// （<c>FlareShotPacket.Execute</c> 通过 <c>ItemFactory</c> 创建 <c>Ammo</c>），因此该对象
    /// 在这里也存在。
    /// </summary>
    private static Ammo FindFlareHolder(string profileId)
    {
        if (string.IsNullOrEmpty(profileId))
        {
            return null;
        }

        lock (Sync)
        {
            return LastFlareByProfile.TryGetValue(profileId, out var flare) ? flare : null;
        }
    }

    /// <summary>
    /// 本机为某个 profile 记录的最后一次信号弹，保留它是为了让在信号弹数据包
    /// 之后到达的授权仍能用同一个对象重放链路。
    /// </summary>
    private static readonly Dictionary<string, Ammo> LastFlareByProfile = new(StringComparer.Ordinal);

    /// <summary>记录对等端的数据包刚刚在本机创建的信号弹。</summary>
    internal static void RememberRemoteFlareShot(string profileId, Ammo flare)
    {
        if (string.IsNullOrEmpty(profileId) || flare == null)
        {
            return;
        }

        lock (Sync)
        {
            LastFlareByProfile[profileId] = flare;
        }

        var template = flare.AmmoTemplate;
        if (template != null && template.ContainsType(FlareEventType.ExitActivate))
        {
            LogOnce("peerflare:" + profileId,
                $"[FlareExfil] heli: peer flare received profile='{profileId}' flareType={DescribeFlareType(FlareEventType.ExitActivate)} - " +
                "the exit chain can be replayed on this machine");
        }

        MatchArrivedFlare(profileId);
    }

    /// <summary>
    /// 应用一个待处理的视觉。调用方需持有 <see cref="Sync"/>。
    /// </summary>
    private static void ApplyVisualLocked(string profileId)
    {
        if (!PendingVisuals.TryGetValue(profileId, out var pending))
        {
            return;
        }

        PendingVisuals.Remove(profileId);

        var key = VisualIdOf(pending.PointName, profileId, pending.Position);
        if (!AppliedVisuals.Add(key))
        {
            return;
        }

        ApplyFlareVisual(profileId, pending.PointName, pending.Position, pending.Holder, out var hops);
        LogOnce("visual:" + profileId + ":" + pending.PointName,
            $"[FlareExfil] heli: point='{pending.PointName}' open={DescribeLocalStatus(pending.PointName)} visual=broadcast " +
            $"firedBy='{profileId}' triggers={hops} - this machine ran the same scene chain the shooter ran");
    }

    /// <summary>
    /// 在本机为一发信号弹运行原生信号弹链路，完全按照发射者自己的机器所运行的方式：
    /// 相同的 <c>FlareSuccessEvent</c> 数据，经过场景中每个
    /// <c>TriggerFlareSuccess</c> 的相同入口点。不直接播放任何东西——由地图自己的处理程序决定
    /// 该触发器意味着什么（直升机进场、灯光、音效、出口开启）。
    /// </summary>
    private static void ApplyFlareVisual(string profileId, string pointName, Vector3 position, Ammo holder, out int hops)
    {
        hops = 0;

        Player firedPlayer = null;
        try
        {
            firedPlayer = Singleton<GameWorld>.Instance?.GetAlivePlayerByProfileID(profileId);
        }
        catch (Exception)
        {
            // 已经离开的对等端不可能成为该事件的持有者；不过为出口本身
            // 仍然值得启动该链路。
        }

        if (firedPlayer == null)
        {
            try
            {
                firedPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
            }
            catch (Exception)
            {
                firedPlayer = null;
            }
        }

        TriggerFlareSuccess[] triggers;
        try
        {
            triggers = UnityEngine.Object.FindObjectsOfType<TriggerFlareSuccess>(true);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[FlareExfil] heli: trigger lookup failed for point='{pointName}' ({exception.Message})");
            return;
        }

        if (triggers == null || triggers.Length == 0)
        {
            // 此处没有可启动的东西，因此直接打开出口：授权已经决定了谁可以离开，
            // 而没有场景链路的地图也不应因为缺少动画就丢掉这次激活。
            OpenPointForVisual(pointName);
            LogOnce("heli-notriggers:" + pointName,
                $"[FlareExfil] heli: point='{pointName}' open={DescribeLocalStatus(pointName)} visual=broadcast-noop " +
                $"firedBy='{profileId}' reason='this scene has no TriggerFlareSuccess component'");
            return;
        }

        // 该事件通过其公开的 Invoke 填充：这正是原生开枪路径所用的调用，
        // 也是设置 il2cpp 事件数据的唯一方式（其属性在 C#
        // 侧是只读的）。链路不会读取玩家位置——只读取信号弹位置——因此两处
        // 传入的是同一个点。
        FlareSuccessEvent flareEvent = new();
        flareEvent.Invoke(firedPlayer?.Cast<IPlayer>(), position, position, holder?.AmmoTemplate);

        for (var i = 0; i < triggers.Length; i++)
        {
            var trigger = triggers[i];
            if (trigger == null)
            {
                continue;
            }

            try
            {
                trigger.OnFlareSuccess(flareEvent);
                hops++;
            }
            catch (Exception exception)
            {
                LogOnce("heli-triggerfail:" + (trigger.gameObject != null ? trigger.gameObject.name : "unknown"),
                    $"[FlareExfil] heli: TriggerFlareSuccess on '{trigger.gameObject?.name}' rejected the replayed " +
                    $"flare ({exception.Message})");
            }
        }

        // 出口本身由授权打开，而不是由动画打开：即使某张地图的处理程序链不
        // 包含撤离处理程序，也必须让被覆盖的玩家能够离开。
        OpenPointForVisual(pointName);
    }

    /// <summary>
    /// 在指定出口仍在等待手动激活时将其打开。授权已经判定该
    /// 玩家可以离开；否则动画会在一台出口仍然关闭的机器上播放。
    /// </summary>
    private static void OpenPointForVisual(string pointName)
    {
        if (string.IsNullOrEmpty(pointName))
        {
            return;
        }

        var point = FikaExfilManager.FindExfiltrationPoint(pointName);
        if (point == null)
        {
            return;
        }

        try
        {
            if (point.Status == EExfiltrationStatus.AwaitsManualActivation)
            {
                point.ExternalSetStatus(EExfiltrationStatus.RegularMode, NativeSignature);
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[FlareExfil] heli: point='{pointName}' could not be opened for the visual ({exception.Message})");
        }
    }

    /// <summary>视觉去重所用的键：撤离点、发射者以及信号弹燃烧的位置。</summary>
    private static string VisualIdOf(string pointName, string profileId, Vector3 position)
    {
        return $"{pointName}|{profileId}|{position.x:F1}|{position.y:F1}|{position.z:F1}";
    }

    /// <summary>某个撤离点的本地状态，用于重放的单行证据。</summary>
    private static string DescribeLocalStatus(string pointName)
    {
        try
        {
            var point = FikaExfilManager.FindExfiltrationPoint(pointName);
            return point == null ? "point-unknown" : point.Status.ToString();
        }
        catch (Exception)
        {
            return "unreadable";
        }
    }

    /// <summary>
    /// 当此场景确实带有“信号弹到出口”链路时返回 true：至少要存在一个
    /// <c>EFT.GameTriggers.TriggerFlareSuccess</c> 组件。在此类地图上，信号弹事件本身就是
    /// 出口的证据——这张地图没有检测区可供查询。
    /// </summary>
    private static int CountSceneFlareTriggers()
    {
        try
        {
            var triggers = UnityEngine.Object.FindObjectsOfType<TriggerFlareSuccess>(true);
            return triggers == null ? 0 : triggers.Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// 场景触发器信号弹所属的出口：距离枪响位置最近的等待手动激活
    /// 出口，或者某一轮流程已分类为受信号弹门禁限制的出口。其他出口一律拒绝，因此
    /// 信号弹永远无法在只是恰好某处有 TriggerFlareSuccess 的地图上打开一个普通出口。
    /// </summary>
    private static bool TryResolveSceneTriggerPoint(Vector3 shotPosition, out string pointName, out string evidence)
    {
        pointName = null;
        evidence = null;

        var controller = ExfiltrationController.Instance;
        if (controller?.ExfiltrationPoints == null || controller.ExfiltrationPoints.Length == 0)
        {
            return false;
        }

        var best = null as ExfiltrationPoint;
        var bestDistance = float.MaxValue;
        var bestEvidence = null as string;

        for (var i = 0; i < controller.ExfiltrationPoints.Length; i++)
        {
            var point = controller.ExfiltrationPoints[i];
            if (point?.Settings == null || string.IsNullOrEmpty(point.Settings.Name))
            {
                continue;
            }

            var own = FlarePointEvidence(point);
            if (own == null)
            {
                continue;
            }

            float distance;
            try
            {
                distance = Vector3.Distance(point.transform.position, shotPosition);
            }
            catch (Exception)
            {
                continue;
            }

            if (distance <= PointRange && distance < bestDistance)
            {
                bestDistance = distance;
                best = point;
                bestEvidence = own;
            }
        }

        if (best == null)
        {
            return false;
        }

        pointName = best.Settings.Name;
        evidence = $"{bestEvidence}, {bestDistance:F0} m from the shot";
        return true;
    }

    /// <summary>
    /// 信号弹能够打开该出口的依据；若不能则返回 null。与基于区域的
    /// 分类所使用的结论相同，因此两条路径接受的出口完全一致。
    /// </summary>
    private static string FlarePointEvidence(ExfiltrationPoint point)
    {
        if (point == null)
        {
            return null;
        }

        if (IsGatedPoint(point.Settings?.Name))
        {
            return "the exit was already classified as flare gated";
        }

        if (DescribeBlockingRequirement(point) != null)
        {
            return null;
        }

        if (HasManualActivationEvidence(point.Settings?.Name, point) == null)
        {
            return null;
        }

        // 地图的场景触发链路正是原生机制在此使用的同一证据：它只存在于
        // 由信号弹事件驱动出口的地图上。
        return CountSceneFlareTriggers() > 0
            ? "the exit waits for a manual activation and this scene carries a TriggerFlareSuccess chain"
            : null;
    }

    /// <summary>
    /// 一个等待中的重放。
    /// </summary>
    private sealed class PendingVisual
    {
        public string PointName = string.Empty;
        public Vector3 Position;

        /// <summary>地图处理程序从信号弹事件中读出的对象；在它于此出现之前为 null。</summary>
        public Ammo Holder;

        public bool HolderArrived;
        public float ReadyAt;
    }

    /// <summary>当某个待处理重放正在等待其持有者或等待超时时为 true。供诊断使用。</summary>
    internal static bool HasPendingVisuals()
    {
        lock (Sync)
        {
            return PendingVisuals.Count > 0;
        }
    }

    /// <summary>本机有多少个出口视觉重放在等待，供诊断使用。</summary>
    internal static int PendingVisualCount()
    {
        lock (Sync)
        {
            return PendingVisuals.Count;
        }
    }

    /// <summary>这张地图带有多少个场景信号弹触发器，供诊断使用。</summary>
    internal static int SceneFlareTriggerCount()
    {
        return CountSceneFlareTriggers();
    }

    /// <summary>
    /// 本机视为受信号弹门禁限制的出口（由某一轮流程分类，或由信号弹打开），供
    /// 诊断使用：若某张地图的信号弹出口不在此列表中，则它根本没有门禁。
    /// </summary>
    internal static List<string> GatedPointNames()
    {
        var names = new List<string>();
        lock (Sync)
        {
            foreach (var name in GatedPoints)
            {
                names.Add(name);
            }

            foreach (var name in ClassifiedGated)
            {
                if (!GatedPoints.Contains(name))
                {
                    names.Add(name);
                }
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static int CountDestroyed(FlareShootDetectorZone[] zones)
    {
        if (zones == null)
        {
            return 0;
        }

        var destroyed = 0;
        for (var i = 0; i < zones.Length; i++)
        {
            if (zones[i] == null)
            {
                destroyed++;
            }
        }

        return destroyed;
    }

    /// <summary>
    /// 某一轮流程因出口已销毁或未初始化而不得不跳过的出口，格式为
    /// <c>k/n: names</c>；若没有跳过任何出口则为 null。没有它，空的分类结果
    /// 与根本没有信号弹出口的地图就无法区分。
    /// </summary>
    private static string DescribeSkippedPoints(ExfiltrationPoint[] points)
    {
        if (points == null)
        {
            return "the exit list itself is null";
        }

        var skipped = new List<string>();
        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (point == null)
            {
                skipped.Add($"[{i}]=destroyed");
                continue;
            }

            try
            {
                if (point.Settings == null)
                {
                    skipped.Add($"[{i}]='{point.name}'(no settings)");
                }
            }
            catch (Exception)
            {
                skipped.Add($"[{i}]=unreadable");
            }
        }

        return skipped.Count == 0 ? null : $"{skipped.Count}/{points.Length}: {string.Join(", ", skipped)}";
    }

    /// <summary>每个键一行：调用方会按每发信号弹、每个数据包和每一帧运行。</summary>
    private static void LogOnce(string key, string line, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        // 新世界会清理该表，因此每个战局都会得到自己的一套日志行。
        SyncRaidState();

        lock (Sync)
        {
            if (!LoggedLines.Add(key))
            {
                return;
            }
        }

        // 调用方名称会被继续传递，因此日志行始终命名产生它的方法，
        // 而不是这个去重辅助方法。
        FikaGlobals.LogWarning(line, caller);
    }

    /// <summary>
    /// 本类显示过的一个横幅，包含撤下它所需的全部信息：通知对象
    /// 本身（隐藏它既不会重复，也不会波及另一个弹窗）以及它上报的日志行。
    /// </summary>
    private sealed class FeedbackPopup
    {
        /// <summary>
        /// 实际被显示的那个对象。有意声明为具体类型：<c>Hide()</c>
        /// 是由此类型的基类 <c>FireFlareForExitNotification</c> 声明的，而不是由
        /// 抽象类 <c>Notification</c> 声明——这也正是条目按弹窗而非按键组织的原因。
        /// </summary>
        internal SniperFlareZoneDisabledNotification Notification;
        internal string Key;
        internal string ZoneId;
        internal string ProfileId;
        internal float Seconds;
        internal float HideAt;
    }

    private readonly struct GrantRecord(string pointName, string profileId, string squadId, string zoneId, Vector3 flarePosition)
    {
        public readonly string PointName = pointName;
        public readonly string ProfileId = profileId;
        public readonly string SquadId = squadId;
        public readonly string ZoneId = zoneId;

        /// <summary>信号弹燃烧的位置。随授权传给各对等端，以便它们能在同一位置启动
        /// 自己的出口链路；当授权不携带视觉表现时为 <see cref="Vector3.zero"/>。</summary>
        public readonly Vector3 FlarePosition = flarePosition;
    }

    /// <summary>
    /// 绿色信号弹检测区所接入的东西。原版有两种形态：一种是打开出口的区域
    /// （"exit-gate"），另一种是把发射玩家放入原生狙击免疫集合的区域
    /// （"immunity"）。其他任何东西都是空投、任务或狙击火力区域，不予处理。
    /// </summary>
    internal enum ZoneKind
    {
        Other,
        ExitGate,
        Immunity
    }

    /// <summary>区域表就本次战局中某个检测区所报告的全部信息。</summary>
    private sealed class ZoneInfo
    {
        public FlareShootDetectorZone Zone;
        public string ZoneId;
        public bool ExitZone;
        public string FlareType;
        public string Collider;
        public string NearestName;
        public float NearestDistance;
        public string InsideName;
        public string AssociatedName;
    }
}