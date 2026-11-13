using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.Interactive;
using UnityEngine;

namespace Fika.Core.Main.Components;

/// <summary>
/// BT（analysis/terminal-hostility-graph）：终端 / RAUF 区域中其他那些会决定开火的系统。
///
/// <para>传送点的刷卡检查只是同一个事实，但在本构建中，有不止一个系统会对
/// 该区域的玩家开火。结合 <see cref="FikaSentryAccess"/> 一起看（哨兵规则及其队伍
/// 镜像），完整集合是：</para>
///
/// <list type="number">
/// <item>SENTRY——哨兵 boss 规则：<c>SentryBossLogic</c>（状态 <c>_accessedPlayerIds</c>，由
/// 原生入口 <c>OnTerminalAccessGained</c> 写入，由 <c>BossSentryHostilityRule</c> 通过
/// <c>IAccessBossLogic.HaveAccess</c> 读取）。由 <see cref="FikaSentryAccess"/> 在每台机器上落地。</item>
/// <item>GROUP——该区域每个 bot 队伍的敌意表（<c>BotGroupHostility</c>，每条规则在
/// <c>BotGroupInnerHostilityData</c> 内对应一个容器）。哨兵准入在那里有自己的容器
/// （<c>AlliesBySentryAcces</c>），那是 <see cref="FikaSentryAccess"/> 的第二个落点。其他
/// 容器（<c>EnemiesByHit</c>、<c>NeutralsByVsRF</c>、<c>EnemiesByAiPlaceAllEnemy</c> 等）是
/// 其他规则的产物，本组件不会写入它们——它们只被上报，因为
/// 任务书禁止清除敌意表。</item>
/// <item>ZONE——场景中代码驱动的射手，它们完全不会派出 bot：<c>BorderZone</c> 及其
/// 子类 <c>SniperFiringZone</c>（一个模拟狙击手：射击频率、命中概率，
/// N 发后致命）以及 <c>Minefield</c>。它们会查询 <c>PlayersWithImmuneToSniperFire</c>
/// （<c>playersWithImmune : HashSet&lt;Int32&gt;</c>），经由 <c>IsPlayerCanBeDamaged</c>；地图触发器
/// <c>HandlerSniperZoneImmunity</c> 就是该容器的原生写入方。射击本身由
/// 权威机计算（Fika 用 <c>BorderZoneEvent</c> /
/// <c>BorderZone.ProcessIncomingPacket</c> 交付），因此真正要紧的容器是权威机的那个——
/// 这就是为什么本组件会在每一台获知该事实的机器上写入它。</item>
/// <item>GUARDED——<c>GuardedZone</c>，它维护自己的一份 profile id 列表（<c>_guardedPlayerIds</c>），记录
/// 其 <c>SniperFireTargetManager</c> 不视为挑事者的玩家。此处仅作读取：该列表的含义是
/// 「该区域所保护的玩家」（该玩家被命中会触发 <c>_onGuardedPlayerHitTriggerId</c>），而不是「通过
/// 该检查的玩家」，因此它不是刷卡事实的落点。</item>
/// </list>
///
/// <para>本组件做什么：它通过原生容器与原生
/// 方法（<c>AddImmunePlayer(Int32)</c>）把该事实交给系统 3，并把该玩家在系统 2、3、4 上的判定写入
/// 拥有这些系统的机器的日志——于是「哪个系统仍然拒绝这个玩家」成为日志里的事实，
/// 而不是一种印象。其他什么都不动：不改规则、不改阈值、不清敌意表。</para>
///
/// <para>诊断（一次 grep，即本组件添加的这些行）：</para>
/// <code>
/// [SentryDiag] card=swiped sys=immunity state=applied applied=yes side=host source=packet-from-client via=native-immunity-registry registries=2 immune=yes:2/no:0 raidId=2 profile=6acdcb85 -> result=the code-driven zones of this machine now treat the player as immune
/// [SentryDiag] card=swiped sys=zone state=verify applied=yes side=host zones=2 sniper=1 minefield=0 targeted=0 immune=1/1 guarded=1 guardedMine=0 -> result=no code-driven zone of this machine fires at the player
/// [SentryDiag] card=swiped sys=group state=verify applied=- side=host groups=4 hostile=1 profile=6acdcb85 -> result=1 group(s) of this machine still hold the player as an enemy
/// [SentryDiag] card=swiped sys=damage state=verify side=client type=Sniper source=none hits=3 profile=6acdcb85 -> result=a code-driven zone fired at this player (no bot fired)
/// [SentryDiag] card=swiped sys=immunity state=verify applied=no side=host raidId=2 -> result=the native chain of this machine wrote the immunity container (not this component)
/// </code>
/// <para>每一半都各有一个开关可回退（<see cref="Enabled"/>、<see cref="ImmunityEnabled"/>、
/// <see cref="CensusEnabled"/>、<see cref="HitProbeEnabled"/>），一行式回退见报告。</para>
/// </summary>
internal static class FikaTerminalHostility
{
    /// <summary>
    /// 扇出总开关。OFF（现在是默认）＝其他开火系统完全保持
    /// 本机原生链给它们留下的状态。
    /// </summary>
    /// <remarks>
    /// (hotfix 2026-10-14) 首次实机测试后默认关闭：客户端完全不吃伤害，而
    /// 主机进程在它自己的玩家受到坠落伤害的瞬间崩溃。两个症状恰好都落在这
    /// 一轮的这一半上，因此在写入范围确定之前保持关闭（只限权威机，且只限
    /// 那张地图的原生触发器所写入的内容），并且要等到伤害路径的观测补丁能够装到
    /// 一个启动开关之后，而不是始终安装（见 <c>TerminalHostilityPatches</c>）。
    /// </remarks>
    internal static bool Enabled = false;

    /// <summary>
    /// 同一事实的落点 3：代码驱动区域的原生免疫容器。OFF（现在是默认）
    /// ＝ 容器不会从这里被写入；拒绝仍会作为一行日志上报。
    /// </summary>
    /// <remarks>(hotfix 2026-10-14) 默认关闭：正是这次写入让玩家变得无法被伤害。</remarks>
    internal static bool ImmunityEnabled = false;

    /// <summary>
    /// (2026-10-26 W35) 只允许**权威机**写免疫容器的那一档。2026-10-14 那次热修的两个症状里，
    /// "客机完全不吃伤害"就来自客机也写了自己的免疫；把写入收窄到权威机，这条症状在结构上就不成立。
    /// 默认 true，但真正生效还要把 <see cref="ImmunityEnabled"/> 打开（两档是 AND 的是"谁写"，不是"写不写"）——
    /// 也就是说：本档只负责"谁能写"，写入与否仍由 ImmunityEnabled 决定，默认仍为关闭，行为与热修后一致。
    /// </summary>
    internal static bool ImmunityAuthoritativeOnly = true;

    /// <summary>本机各系统的只读判定（证据那一半）。</summary>
    internal static bool CensusEnabled = true;

    /// <summary>伤害来源行：究竟是什么打中了本机的玩家。</summary>
    /// <remarks>
    /// (hotfix 2026-10-14) 与伤害路径的观测补丁一同默认关闭；
    /// 在这些补丁未安装的情况下，该行没有任何触发点。
    /// </remarks>
    internal static bool HitProbeEnabled = false;

    /// <summary>某行所指的落点：原生免疫容器（<c>AddImmunePlayer</c>）。</summary>
    internal const string ViaImmunityRegistry = "native-immunity-registry";

    /// <summary>本组件没有任何落点到达该行所指的系统。</summary>
    internal const string ViaNone = "none";

    private const int MaxZoneLines = 6;
    private const int MaxGroupLines = 6;
    private const int MaxHitLines = 48;
    private const int EveryNthHit = 10;

    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> HitCounts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> ImmuneVerified = new(StringComparer.Ordinal);
    private static int _censusRuns;

    /// <summary>当本组件自己正在写入免疫容器时为 true（原生补丁用以区分二者）。</summary>
    private static bool _writingImmunity;

    internal static void Reset()
    {
        lock (Keys)
        {
            Keys.Clear();
            HitCounts.Clear();
            ImmuneVerified.Clear();
            _censusRuns = 0;
            _writingImmunity = false;
        }
    }

    /// <summary>
    /// 扇出：在拥有答案的机器上，每（玩家, 准入事实）组合调用一次。调用方是
    /// <see cref="FikaSentryAccess.Apply"/>（每台落地该事实的机器，包括权威机）
    /// 以及 <c>FikaSentryAccess.NoteNativeEmission</c>（自己运行了该交互的机器）。
    /// </summary>
    internal static void Deliver(Player player, string source, bool granted)
    {
        try
        {
            if (player == null)
            {
                return;
            }

            var profileId = player.ProfileId;

            // (2026-10-26 W35) 仅限权威机的免疫那一半在扇出开关之前就决定了：一台
            // 保持扇出关闭的主机，仍然可以告诉自己的代码驱动区域该玩家已刷卡通过，
            // 而客户端永远不会写自己的免疫（正是那次写入让客户端在
            // 2026-10-14 的热修中变得无法被伤害）。
            var immunityWanted = granted && ImmunityEnabled && (!ImmunityAuthoritativeOnly || Fika.Core.Main.Utils.FikaBackendUtils.IsServer);
            if (!Enabled)
            {
                Line("fanout-off:" + Short(profileId) + ":" + source,
                    "card=swiped sys=fanout state=applied applied=no side=" + Side + " source=" + source
                    + " profile=" + Short(profileId)
                    + " -> result=fanout-off (FikaTerminalHostility.Enabled=false): the other firing systems of this machine keep whatever the native chain gave them");
                return;
            }

            if (immunityWanted)
            {
                ApplyImmunity(player, source);
            }
            else if (granted)
            {
                Line("immunity-off:" + Short(profileId) + ":" + source,
                    "card=swiped sys=immunity state=applied applied=no side=" + Side + " source=" + source
                    + " via=" + ViaNone + " profile=" + Short(profileId)
                    + " -> result=immunity-off (ImmunityEnabled=false, or ImmunityAuthoritativeOnly=true on a client): the code-driven zones of this machine were not told");
            }

            Census(player, source);
        }
        catch (Exception exception)
        {
            Line("fanout-error:" + Short(player?.ProfileId),
                "card=swiped sys=fanout state=applied applied=no side=" + Side + " source=" + source
                + " -> result=fanout-threw " + exception.GetType().Name);
        }
    }

    /// <summary>
    /// 落点 3：代码驱动区域所读取的原生容器。写入经由容器自身的原生
    /// 方法（<c>AddImmunePlayer(Int32)</c>，与信号弹收集器和
    /// 地图触发器 <c>HandlerSniperZoneImmunity</c> 所使用的入口点相同），并用区域所提出的原生
    /// 问题（<c>IsPlayerImmuneForFire(Int32)</c>）读回答案。
    ///
    /// <para>本构建中的容器是 <c>PlayersWithImmuneToSniperFire</c> 及其子类
    /// <c>SniperFireTargetManager</c>（它重写了这两个成员）。若一场战局一个都没有，就
    /// 根本不存在代码驱动区域，日志行会说明这一点。</para>
    /// </summary>
    internal static void ApplyImmunity(Player player, string source)
    {
        var profileId = player.ProfileId;
        var raidId = RaidIdOf(player);
        var registries = FindImmunityRegistries();
        var verified = 0;
        var failed = 0;
        var kinds = new List<string>();
        var raidIdText = raidId > 0 ? raidId.ToString() : "-";

        for (var i = 0; i < registries.Length; i++)
        {
            var registry = registries[i];
            var kind = RegistryKind(registry);
            if (!kinds.Contains(kind))
            {
                kinds.Add(kind);
            }

            if (raidId <= 0)
            {
                failed++;
                continue;
            }

            try
            {
                lock (Keys)
                {
                    _writingImmunity = true;
                }

                registry.AddImmunePlayer(raidId);
            }
            catch (Exception exception)
            {
                failed++;
                Line("immunity-write-error:" + Short(profileId) + ":" + i,
                    "card=swiped sys=immunity state=applied applied=no side=" + Side + " source=" + source
                    + " registry=" + kind + " raidId=" + raidIdText
                    + " -> result=AddImmunePlayer threw " + exception.GetType().Name);
            }
            finally
            {
                lock (Keys)
                {
                    _writingImmunity = false;
                }
            }

            try
            {
                if (registry.IsPlayerImmuneForFire(raidId))
                {
                    verified++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception)
            {
                failed++;
            }
        }

        var changed = true;
        lock (Keys)
        {
            if (ImmuneVerified.TryGetValue(profileId, out var last))
            {
                changed = last != verified;
            }

            ImmuneVerified[profileId] = verified;
        }

        if (!changed)
        {
            return;
        }

        var verdict = registries.Length == 0
            ? "no-code-driven-zone-on-this-machine (a raid without a PlayersWithImmuneToSniperFire container has no sniper zone to stand down)"
            : verified == registries.Length && verified > 0
                ? "the code-driven zones of this machine now treat the player as immune"
                : "the native container did not take the raid id on every registry: the code-driven zones of this machine can still fire at the player";
        Line("immunity:" + Short(profileId) + ":" + source + ":" + verified + ":" + registries.Length,
            "card=swiped sys=immunity state=applied applied=" + (verified > 0 ? "yes" : "no")
            + " side=" + Side + " source=" + source + " via=" + (registries.Length > 0 ? ViaImmunityRegistry : ViaNone)
            + " registries=" + registries.Length + " kinds=[" + string.Join(",", kinds) + "]"
            + " immune=yes:" + verified + "/no:" + failed
            + " raidId=" + raidIdText + " profile=" + Short(profileId)
            + " -> result=" + verdict);
    }

    /// <summary>
    /// 本机每个开火系统对某个玩家的只读判定：代码驱动区域
    /// (3)、本机 bot 的队伍敌意表 (2) 以及受保护区 (4)。写入时机是
    /// 事实落地的瞬间以及玩家被击中的瞬间，这样两者可以对齐。
    /// </summary>
    internal static void Census(Player player, string source)
    {
        if (!CensusEnabled || player == null)
        {
            return;
        }

        var profileId = player.ProfileId;
        var raidId = RaidIdOf(player);

        try
        {
            var zones = FindZones();
            var sniper = 0;
            var minefield = 0;
            var targeted = 0;
            var immune = 0;
            var immuneKnown = 0;
            var registryMissing = 0;

            for (var i = 0; i < zones.Length; i++)
            {
                var zone = zones[i];
                var kind = ZoneKind(zone);
                if (kind == "sniper")
                {
                    sniper++;
                }
                else if (kind == "minefield")
                {
                    minefield++;
                }

                var isTargeted = IsTargeted(zone, profileId);
                if (isTargeted)
                {
                    targeted++;
                }

                var immuneState = ZoneImmune(zone, raidId);
                if (immuneState < 0)
                {
                    registryMissing++;
                }
                else
                {
                    immuneKnown++;
                    if (immuneState == 1)
                    {
                        immune++;
                    }
                }

                if (i < MaxZoneLines)
                {
                    Line("zone:" + Short(profileId) + ":" + zone.Id + ":" + isTargeted + ":" + immuneState,
                        "card=swiped sys=zone state=verify applied=" + (isTargeted && immuneState != 1 ? "no" : "yes")
                        + " side=" + Side + " source=" + source + " zone=" + zone.Id + " kind=" + kind
                        + " targeted=" + (isTargeted ? "yes" : "no")
                        + " immune=" + (immuneState < 0 ? "no-registry" : immuneState == 1 ? "yes" : "no")
                        + " profile=" + Short(profileId)
                        + " -> result=" + (isTargeted ? "the code-driven zone of this machine holds the player as a target" : "this player is not in the target list of this zone"));
                }
            }

            var guarded = FindGuardedZones();
            var guardedMine = 0;
            for (var i = 0; i < guarded.Length; i++)
            {
                if (GuardedContains(guarded[i], profileId))
                {
                    guardedMine++;
                }
            }

            Line("zone-summary:" + Short(profileId) + ":" + source + ":" + zones.Length + ":" + targeted + ":" + immune,
                "card=swiped sys=zone state=verify applied=" + (targeted > 0 && immune == 0 ? "no" : "yes")
                + " side=" + Side + " source=" + source
                + " zones=" + zones.Length + " sniper=" + sniper + " minefield=" + minefield
                + " targeted=" + targeted + " immune=" + immune + "/" + immuneKnown
                + " noRegistry=" + registryMissing
                + " guarded=" + guarded.Length + " guardedMine=" + guardedMine
                + " profile=" + Short(profileId)
                + " -> result=" + (zones.Length == 0
                    ? "this machine holds no code-driven zone at all"
                    : targeted > 0
                        ? "a code-driven zone of this machine holds this player as a target (the immunity container is the place to look)"
                        : "no code-driven zone of this machine targets this player"));
        }
        catch (Exception exception)
        {
            Line("zone-scan-error:" + Short(profileId),
                "card=swiped sys=zone state=verify applied=no side=" + Side + " source=" + source
                + " -> result=zone-scan-threw " + exception.GetType().Name);
        }

        try
        {
            var groups = LocalGroups();
            var hostile = 0;
            for (var i = 0; i < groups.Count; i++)
            {
                var verdict = GroupVerdict(groups[i], raidId);
                var isHostile = verdict.StartsWith("enemy", StringComparison.Ordinal);
                if (isHostile)
                {
                    hostile++;
                }

                if (i < MaxGroupLines)
                {
                    Line("group:" + Short(profileId) + ":" + IdOf(groups[i]) + ":" + source + ":" + verdict,
                        "card=swiped sys=group state=verify applied=" + (isHostile ? "no" : "yes")
                        + " side=" + Side + " source=" + source + " group=" + IdOf(groups[i])
                        + " profile=" + Short(profileId) + " " + verdict
                        + " -> result=" + (isHostile
                            ? "this group of this machine still decides to fire on the player, and the container above names the rule that holds him"
                            : "this group of this machine does not fire on the player"));
                }
            }

            Line("group-summary:" + Short(profileId) + ":" + source + ":" + groups.Count + ":" + hostile,
                "card=swiped sys=group state=verify applied=" + (hostile > 0 ? "no" : "yes")
                + " side=" + Side + " source=" + source + " groups=" + groups.Count + " hostile=" + hostile
                + " profile=" + Short(profileId)
                + " -> result=" + (groups.Count == 0
                    ? "this machine owns no bot group at all (a Fika client holds every bot as an observed puppet)"
                    : hostile > 0
                        ? hostile + " group(s) of this machine still hold the player as an enemy"
                        : "no group of this machine holds the player as an enemy"));
        }
        catch (Exception exception)
        {
            Line("group-scan-error:" + Short(profileId),
                "card=swiped sys=group state=verify applied=no side=" + Side + " source=" + source
                + " -> result=group-scan-threw " + exception.GetType().Name);
        }

        lock (Keys)
        {
            _censusRuns++;
        }
    }

    /// <summary>
    /// 本机玩家受到的一次命中的来源：到达的是什么伤害类型，以及是否由本
    /// 战局的某个玩家造成。这一行用于区分 bot 的一轮齐射（有来源）与代码驱动系统
    /// （无来源：地图边界和受保护区的区域是 <c>Sniper</c>，雷区、炮击或脚本处理器则是 <c>Explosion</c> 或
    /// <c>Landmine</c>）。
    /// </summary>
    internal static void NoteDamage(Player victim, DamageInfo info)
    {
        try
        {
            if (!HitProbeEnabled || victim == null)
            {
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            var local = world?.MainPlayer;
            if (local == null || local.ProfileId != victim.ProfileId)
            {
                return;
            }

            var type = info.DamageType;

            // 场景中的代码驱动来源完全不携带射手；而 bot 或玩家既携带
            // 来源对象又携带其 id。两种读取都做了防御处理：这个探针本就是诊断用的。
            var hasSource = false;
            try
            {
                hasSource = info.Player != null;
            }
            catch (Exception)
            {
            }

            var sourceId = "-";
            try
            {
                var id = info.SourceId;
                if (!string.IsNullOrEmpty(id))
                {
                    sourceId = Short(id);
                }
            }
            catch (Exception)
            {
            }

            var source = hasSource ? sourceId : "none";

            var key = "hit:" + type + ":" + source;
            int count;
            lock (Keys)
            {
                HitCounts.TryGetValue(key, out count);
                count++;
                HitCounts[key] = count;
            }

            if (count > MaxHitLines || (count != 1 && count % EveryNthHit != 0))
            {
                return;
            }

            var verdict = type == EDamageType.Sniper
                ? "a code-driven zone fired at this player (no bot fired: the sniper of the map border or of the guarded area of this machine or of the authority)"
                : source == "none"
                    ? "the damage carries no shooter: a code-driven source of the scene applied it (zone, minefield, shelling or a script handler)"
                    : "a shooter of this raid applied it (the group tables of the firing machine are the place to look)";
            Line(key + ":" + count,
                "card=swiped sys=damage state=verify applied=- side=" + Side + " source=" + source
                + " type=" + type + " damage=" + Math.Round(info.Damage, 1)
                + " hits=" + count + " profile=" + Short(victim.ProfileId)
                + " -> result=" + verdict);

            if (count == 1)
            {
                // 玩家被击中的瞬间就是所有判定都必须读取的时刻：
                // 刷卡已生效这一事实与某个系统仍在开火这一事实，必须能够逐行比较。
                Census(victim, "hit");
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 代码驱动区域的一次射击在这台机器上是来自权威机的
    /// （Fika 的 <c>BorderZoneEvent</c> 调用的正是 <c>BorderZone.ProcessIncomingPacket</c>）。此处的一行
    /// 会指出该区域，并证明决定开火的机器就是权威机——以及它的
    /// 免疫容器里并没有这个玩家。
    /// </summary>
    internal static void NoteZonePacket(BorderZone zone, IObserverToPlayerBridge bridge)
    {
        try
        {
            if (zone == null || bridge == null)
            {
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            var local = world?.MainPlayer;
            if (local == null)
            {
                return;
            }

            var profileId = "-";
            try
            {
                var player = bridge.iPlayer;
                if (player != null)
                {
                    profileId = player.ProfileId;
                }
            }
            catch (Exception)
            {
            }

            if (string.IsNullOrEmpty(profileId) || profileId != local.ProfileId)
            {
                return;
            }

            var kind = ZoneKind(zone);
            Line("zone-hit:" + Short(profileId) + ":" + zone.Id + ":" + kind,
                "card=swiped sys=zone state=verify applied=no side=" + Side + " source=host-packet zone=" + zone.Id
                + " kind=" + kind + " profile=" + Short(profileId)
                + " -> result=the authority fired a code-driven zone of this map at this player (this machine only applies the shot)");
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 本机代码驱动狙击手对某个玩家的判定，通过原生问题
    /// （<c>SniperFiringZone.IsPlayerCanBeDamaged(IPlayer)</c>）读取。每（区域, 玩家, 判定）一行。
    /// </summary>
    internal static void NoteZoneVerdict(SniperFiringZone zone, IPlayer player, bool canBeDamaged)
    {
        try
        {
            if (zone == null || player == null || player.IsAI)
            {
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            var local = world?.MainPlayer;
            if (local == null || local.ProfileId != player.ProfileId)
            {
                return;
            }

            var raidId = RaidIdOf(local);
            var registry = ZoneRegistry(zone);
            Line("zone-verdict:" + Short(player.ProfileId) + ":" + zone.Id + ":" + canBeDamaged,
                "card=swiped sys=zone state=verify applied=" + (canBeDamaged ? "no" : "yes") + " side=" + Side
                + " zone=" + zone.Id + " canBeDamaged=" + (canBeDamaged ? "yes" : "no")
                + " immune=" + (registry == null ? "no-registry" : registry.IsPlayerImmuneForFire(raidId) ? "yes" : "no")
                + " profile=" + Short(player.ProfileId)
                + " -> result=" + (canBeDamaged
                    ? "the code-driven sniper zone of this machine still fires at this player (the native question said yes)"
                    : "the code-driven sniper zone of this machine stands down for this player"));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 原生容器是由本机的原生链写入的（信号弹收集器或地图
    /// 触发器 <c>HandlerSniperZoneImmunity</c>），而不是本组件。这是回答
    /// 「这张地图的原生链到底给不给免疫，且是在哪台机器上给」的证据。
    /// </summary>
    internal static void NoteNativeImmunityWrite(PlayersWithImmuneToSniperFire registry, int raidId, bool added)
    {
        try
        {
            lock (Keys)
            {
                if (_writingImmunity)
                {
                    return;
                }
            }

            Line("immunity-native:" + Short(registry.GetType().Name) + ":" + raidId + ":" + added,
                "card=swiped sys=immunity state=verify applied=" + (added ? "yes" : "no") + " side=" + Side
                + " via=native-chain registry=" + registry.GetType().Name + " raidId=" + raidId
                + " -> result=the native chain of this machine " + (added
                    ? "wrote the sniper immunity container (the code-driven zones of this machine stand down for that player)"
                    : "removed the player from the sniper immunity container"));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 受保护区把某个玩家标记为其狙击目标管理器的挑事者：本机的
    /// 代码驱动系统决定向他射击，完全没有 bot 参与。
    /// </summary>
    internal static void NoteAggressorMark(SniperFireTargetManager manager, int raidId)
    {
        try
        {
            if (manager == null || raidId <= 0)
            {
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            var local = world?.MainPlayer;
            if (local == null || RaidIdOf(local) != raidId)
            {
                return;
            }

            Line("aggressor:" + raidId + ":" + manager.GetInstanceID(),
                "card=swiped sys=zone state=verify applied=no side=" + Side + " source=guarded-zone"
                + " registry=" + RegistryKind(manager) + " raidId=" + raidId
                + " -> result=the guarded area of this machine marked the player as an aggressor of its sniper (a code-driven system, no bot involved)");
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 本机某个队伍的敌意表接收了一个玩家。该队伍把他放进哪个容器，
    /// 就指明了做出该决定的规则，而这正是事实落地后权威机的某个队伍仍在
    /// 开火时报告所读取的内容。
    /// </summary>
    internal static void NoteGroupEnemy(BotsGroup group, IPlayer player)
    {
        try
        {
            if (player == null || player.IsAI)
            {
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            var local = world?.MainPlayer;
            if (local == null || local.ProfileId != player.ProfileId)
            {
                return;
            }

            var raidId = RaidIdOf(local);
            Line("group-enemy:" + Short(player.ProfileId) + ":" + IdOf(group) + ":" + raidId,
                "card=swiped sys=group state=verify applied=no side=" + Side + " source=enemy-table"
                + " group=" + IdOf(group)
                + " profile=" + Short(player.ProfileId) + " " + GroupVerdict(group, raidId)
                + " -> result=the hostility recalculation of this machine put the player into the enemy side of this group");
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 本机某个队伍对某个玩家的判定，直接从原生状态读取：
    /// <c>BotGroupHostility</c> 的四张表，以及 <c>BotGroupInnerHostilityData</c> 中每个按规则划分的容器
    /// 里涉及某玩家 id 的那些（与 <c>Player.RaidId</c> 相同的 id 空间）。持有
    /// 该玩家的容器就指明了仍在拒绝他的系统。
    /// </summary>
    private static string GroupVerdict(BotsGroup group, int raidId)
    {
        try
        {
            var hostility = group?.BotGroupHostility;
            if (hostility == null)
            {
                return "state=no-hostility-data";
            }

            var inner = hostility.InnerHostilityData;
            var status = StatusOf(hostility, raidId);
            var enemy = status == "Enemy";
            var text = (enemy ? "enemy" : "not-enemy")
                + " status=" + status
                + " tables=enemies:" + Number(() => hostility.Enemies.Count)
                + "/warns:" + Number(() => hostility.Warns.Count)
                + "/neutrals:" + Number(() => hostility.Neutrals.Count)
                + "/allies:" + Number(() => hostility.Allies.Count)
                + " rules="
                + "sentryAccess:" + Flag(() => inner.AlliesBySentryAcces.Contains(raidId))
                + ",byHit:" + Flag(() => inner.EnemiesByHit.Contains(raidId))
                + ",vsrfHit:" + Flag(() => inner.EnemiesByHitVsRF.ContainsKey(raidId))
                + ",vsrfNeutral:" + Flag(() => inner.NeutralsByVsRF.Contains(raidId))
                + ",placeAllEnemy:" + Flag(() => inner.EnemiesByAiPlaceAllEnemy.Contains(raidId))
                + ",follow:" + Flag(() => inner.NeutralsByFollow.Contains(raidId))
                + ",followHit:" + Flag(() => inner.EnemiesByFollowGetHit.Contains(raidId))
                + ",warn:" + Flag(() => inner.EnemiesByWarn.Contains(raidId))
                + ",pairLogic:" + Flag(() => inner.EnemiesByPairLogic.Contains(raidId))
                + ",khorovod:" + Flag(() => inner.EnemiesByKhorovod.Contains(raidId))
                + ",migrate:" + Flag(() => inner.EnemiesByMigrate.Contains(raidId))
                + ",markUnknown:" + Flag(() => inner.EnemiesByMarkOfUnknownDistance.Contains(raidId));
            return text;
        }
        catch (Exception exception)
        {
            return "state=read-threw-" + exception.GetType().Name;
        }
    }

    private static string StatusOf(BotGroupHostility hostility, int raidId)
    {
        try
        {
            var status = hostility.LastHostilityStatus;
            if (status != null && status.TryGetValue(raidId, out var value))
            {
                return value.ToString();
            }
        }
        catch (Exception)
        {
        }

        return "unknown";
    }

    /// <summary>
    /// 原生状态的某个容器回答关于某玩家 id 的问题。读取写成 lambda，是因为这些容器的 il2cpp
    /// 包装器并不是本文件能够指名的 CLR 集合类型；抛出异常（容器缺失）会
    /// 按异常上报，而不是被读作"否"。
    /// </summary>
    private static string Flag(Func<bool> read)
    {
        try
        {
            return read() ? "yes" : "no";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    /// <summary>某张原生表的条目数；本机没有该表时返回 -1。</summary>
    private static int Number(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static PlayersWithImmuneToSniperFire[] FindImmunityRegistries()
    {
        try
        {
            PlayersWithImmuneToSniperFire[] registries = UnityEngine.Object.FindObjectsOfType<PlayersWithImmuneToSniperFire>(true);
            return registries ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static BorderZone[] FindZones()
    {
        try
        {
            BorderZone[] zones = UnityEngine.Object.FindObjectsOfType<BorderZone>(true);
            return zones ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static GuardedZone[] FindGuardedZones()
    {
        try
        {
            GuardedZone[] zones = UnityEngine.Object.FindObjectsOfType<GuardedZone>(true);
            return zones ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string ZoneKind(BorderZone zone)
    {
        try
        {
            if (zone.TryCast<SniperFiringZone>() != null)
            {
                return "sniper";
            }

            if (zone.TryCast<Minefield>() != null)
            {
                return "minefield";
            }
        }
        catch (Exception)
        {
        }

        return "border";
    }

    private static string RegistryKind(PlayersWithImmuneToSniperFire registry)
    {
        try
        {
            if (registry.TryCast<SniperFireTargetManager>() != null)
            {
                return "SniperFireTargetManager";
            }
        }
        catch (Exception)
        {
        }

        try
        {
            return registry.GetType().Name;
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static PlayersWithImmuneToSniperFire ZoneRegistry(SniperFiringZone zone)
    {
        try
        {
            return zone._playersWithImmuneToFire;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int ZoneImmune(BorderZone zone, int raidId)
    {
        try
        {
            var sniper = zone.TryCast<SniperFiringZone>();
            if (sniper == null || raidId <= 0)
            {
                return -1;
            }

            var registry = ZoneRegistry(sniper);
            if (registry == null)
            {
                return -1;
            }

            return registry.IsPlayerImmuneForFire(raidId) ? 1 : 0;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static bool IsTargeted(BorderZone zone, string profileId)
    {
        try
        {
            var targeted = zone.TargetedPlayers;
            if (targeted == null || string.IsNullOrEmpty(profileId))
            {
                return false;
            }

            for (var i = 0; i < targeted.Count; i++)
            {
                var player = targeted[i];
                if (player != null && string.Equals(player.ProfileId, profileId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    private static bool GuardedContains(GuardedZone zone, string profileId)
    {
        try
        {
            var guarded = zone._guardedPlayerIds;
            return guarded != null && !string.IsNullOrEmpty(profileId) && guarded.Contains(profileId);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 本机各自不同的 bot 队伍（Fika 客户端一个都不拥有：该战局里的每个 bot 在
    /// 那里都是被观察的傀儡，这正是那台机器上普查的"队伍"那一半为空的原因）。
    /// </summary>
    private static List<BotsGroup> LocalGroups()
    {
        var result = new List<BotsGroup>();
        var seen = new HashSet<int>();
        try
        {
            var world = Singleton<GameWorld>.Instance;
            var list = world?.AllAlivePlayersList;
            if (list == null)
            {
                return result;
            }

            for (var i = list.Count - 1; i >= 0; i--)
            {
                try
                {
                    var player = list[i];
                    var ai = player?.AIData;
                    if (ai == null || !ai.IsAI)
                    {
                        continue;
                    }

                    var owner = ai.BotOwner;
                    var group = owner?.BotsGroup;
                    if (group == null)
                    {
                        continue;
                    }

                    var id = IdOf(group);
                    if (seen.Add(id))
                    {
                        result.Add(group);
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    /// <summary>
    /// 本机某个原生队伍的身份标识。<c>BotsGroup</c> 是普通的 il2cpp 对象（它不
    /// 是 UnityEngine.Object），因此在日志中靠它自身的指针来区分两个队伍。
    /// </summary>
    private static int IdOf(BotsGroup group)
    {
        try
        {
            return group == null ? 0 : unchecked((int)group.Pointer.ToInt64());
        }
        catch (Exception)
        {
            return 0;
        }
    }

    internal static int RaidIdOf(Player player)
    {
        try
        {
            return player?.RaidId ?? -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    internal static string Side => FikaSentryAccess.Side;

    private static string Short(string value) =>
        string.IsNullOrEmpty(value) ? "-" : value.Length > 8 ? value.Substring(0, 8) : value;

    /// <summary>
    /// 与哨兵系列相同的、有上限且去重的写入器（<c>[SentryDiag]</c>，同时写入
    /// LogOutput.log 与 Fika-diagnostics.log），因此一次 grep 就能同时读到两个系列。
    /// </summary>
    internal static void Line(string key, string text) => FikaSentryAccess.Line(key, text);
}
