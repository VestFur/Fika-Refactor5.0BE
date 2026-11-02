using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Main.Components;

/// <summary>
/// BJ (analysis/terminal-rauf-card)：终端哨兵的访问检查（即转移点处的俄罗斯
/// 士兵，RAUF）。
///
/// <para>原生命名规则——它在哪里。整条规则是哨兵的一个状态，而不是
/// 玩家的状态，也不是某个区域的状态：</para>
/// <list type="number">
/// <item><c>GlobalEventDispatcher.OnTerminalTransitAccessGained</c> 就是「此玩家已
/// 通过卡片检查」这个事件；它唯一的消费者是 <c>SentryBossLogic.OnTerminalAccessGained(Player)</c>
/// （<c>SentryBossLogic.cs</c>，interop :31193），后者把玩家 id 写入私有的
/// <c>HashSet&lt;Int32&gt; _accessedPlayerIds</c>；</item>
/// <item><c>SentryBossLogic : ABossLogic, IAccessBossLogic</c> 只暴露一个问题——
/// <c>HaveAccess(Int32 playerId)</c>（<c>IAccessBossLogic.cs</c>）——由该集合回答；</item>
/// <item>机器人组的敌对性在本机自行重新计算：
/// <c>BotGroupHostility.UpdateHostilityStatusForAllPlayers()</c>（原生，每帧）->
/// <c>BotHostilityService.EvaluateHostility</c> -> 规则列表，其中包含
/// <c>BossSentryHostilityRule : IHostilityRulePlayer</c> -> <c>IAccessBossLogic.HaveAccess</c>
/// （<c>BossSentryHostilityRule.cs</c>，interop :70175）。</item>
/// </list>
/// <para>因此发出归属于运行交互的那台机器，而评估归属于
/// 拥有该机器人的那台机器——在一场 Fika 战局中，这是两台不同的机器。本移植 r32 那次运行
/// 测到的结果（analysis/terminal-rauf-hostility-2）修正了对该症状的第一种解读：</para>
/// <list type="bullet">
/// <item>客户端确实会为它自己的玩家抬升原生事件（它的本地交互是原生运行的：
/// 「the native event was raised on this machine」会带着 <c>sentries=0</c> 出现在客户端日志里）；</item>
/// <item>客户端根本不持有任何哨兵——没有 <c>BotOwner</c>、没有 <c>BotBoss</c>、没有
/// <c>SentryBossLogic</c>、没有机器人组（它不运行 <c>BotsController</c>，而是把每个远程角色都生成为
/// <c>ObservedPlayer</c>），因此那台机器上根本没有东西会去评估哨兵规则；</item>
/// <item>真正评估它的那台机器——权威端——从未被告知该客户端的玩家。</item>
/// </list>
/// <para>因此断点不是「客户端的集合一直是空的」，而是「这个事实从未到达那台
/// 由它的哨兵做决定的机器」——而这正是所报告的症状。</para>
///
/// <para>修复——原生形状，比原生多走一跳。授予在原生链做出它的地方取得，
/// 并通过同一个原生入口点应用到每台机器的哨兵上
/// （<c>SentryBossLogic.OnTerminalAccessGained</c>）。这里不写入任何规则、任何敌对性列表、任何机器人状态：
/// 只有原生事件所携带的那一个事实，被写到该事件自身无法到达的
/// 机器上。</para>
///
/// <para>(analysis/terminal-rauf-hostility-2) Fika 战局中的一台机器只有在它是
/// 权威端（<see cref="IsAuthority"/>）时才拥有机器人；客户端完全不运行 <c>BotsController</c>，它把每个
/// 远程角色——包括机器人——都作为没有 <c>BotOwner</c> 的 <c>ObservedPlayer</c> 持有。这样的机器
/// 因此在两种落点中都没有哨兵可写；它真正拥有的是原生事件
/// 本身，并会为自己的玩家抬升它。因此这个事实在两个方向上被补全：</para>
/// <list type="bullet">
/// <item>向上（客户端 -> 权威端）：<see cref="NoteNativeEmission"/> 把非权威端机器的原生
/// 抬升报告给主机，主机拥有机器人模拟，会把该事实应用到自己的哨兵上
/// 并再把它分发出去。这一跳决定了所报告的情形——「客户端刷卡，RAUF
/// 继续射击」——因为是否射击是由权威端决定的；</item>
/// <item>侧向（在应用该事实的机器上有两个落点）：先是原生入口点，并且——仅当
/// 本机根本不持有哨兵 boss 逻辑时——该组敌对性数据专门为此事实保存的原生
/// 容器 <c>BotGroupInnerHostilityData.AlliesBySentryAcces</c>，它属于本机
/// 哨兵机器人的组。别的什么都不写：没有规则、没有阈值、没有敌人列表。</item>
/// </list>
///
/// <para>诊断——一次 grep，即任务书所要求的那几行：</para>
/// <code>
/// [SentryDiag] card=swiped role=assault state=access-gained applied=yes side=client sentries=0 profile=6acdcb85 raidId=2 -> result=the native event was raised on this machine
/// [SentryDiag] card=swiped role=assault state=access-gained applied=yes side=client granted=yes profile=6acdcb85 raidId=2 -> result=reported to the authority (this machine owns no bots)
/// [SentryDiag] card=swiped role=assault state=applied applied=yes side=host source=packet-from-client via=native-sentry sentries=4/4 allies=0 profile=6acdcb85 -> result=the sentries of this machine now accept the player
/// [SentryDiag] card=swiped role=assault state=verify applied=yes side=host source=packet-from-client sentries=4 haveAccess=yes:4/no:0 -> result=this machine's sentry accepts the player
/// [SentryDiag] card=swiped role=assault state=applied applied=yes side=host source=packet-from-client via=hostility-data sentries=0/0 allies=3 -> result=the sentry groups of this machine now hold the player as an ally by sentry access
/// [SentryDiag] card=swiped role=assault state=applied applied=no side=client source=packet-from-host via=none sentries=0/0 allies=0 -> result=no-sentry-and-no-group-on-this-machine
/// </code>
/// <para>一个开关即可回退该行为（<see cref="RepairEnabled"/>），一行式
/// 回退方式见报告。</para>
/// </summary>
internal static class FikaSentryAccess
{
    /// <summary>
    /// 该修复的总开关。OFF = 本移植与之前完全一样：日志行仍会被写入
    /// （它们就是证据），授予永远不会被复制。回退只需一行——一个字段，不改配置
    /// 文件，不改文件（主代理之后可以把它绑定到某个配置项）。
    /// </summary>
    internal static bool RepairEnabled = true;

    /// <summary>
    /// (analysis/terminal-rauf-hostility-2) 同一事实的第二个落点：该组
    /// 敌对性数据专门为此事实保存的原生容器——
    /// <c>BotGroupInnerHostilityData.AlliesBySentryAcces</c>——只为本机
    /// 哨兵机器人的组写入，且仅在原生入口点没有可运行对象时写入。OFF = 情形 (2) 回退到
    /// 之前的行为（拒绝仍会被写入）。
    /// </summary>
    internal static bool HostilityDataEnabled = true;

    /// <summary>
    /// (analysis/terminal-rauf-hostility-2) 向上那一跳：不拥有机器人模拟的机器把
    /// 它自己玩家的原生抬升报告给权威端，权威端拥有机器人，因此也拥有答案。
    /// OFF = 客户端的刷卡留在客户端上（之前的行为）。
    /// </summary>
    internal static bool RelayToAuthorityEnabled = true;

    /// <summary>日志行所指的落点：本机某个哨兵的原生入口点。</summary>
    internal const string ViaNativeSentry = "native-sentry";
    /// <summary>日志行所指的落点：本机哨兵机器人的组敌对性数据。</summary>
    internal const string ViaHostilityData = "hostility-data";
    /// <summary>本机上两个落点都不存在（它根本不拥有任何机器人——客户端情形）。</summary>
    internal const string ViaNone = "none";

    /// <summary>此族逐字日志行每场战局的上限；其余只做计数。</summary>
    private const int MaxLines = 400;

    private static readonly HashSet<string> Written = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Granted = new(StringComparer.Ordinal);

    /// <summary>
    /// 原生入口点已经对本机应用过其授予的玩家（哨兵 postfix 运行过）。
    /// 正是它使发出那一半不会在权威端上把同一个事实应用两次。
    /// </summary>
    private static readonly HashSet<string> NativeApplied = new(StringComparer.Ordinal);
    private static readonly HashSet<int> AccessYes = new();
    private static readonly Dictionary<int, int> AccessNo = new();
    private static int _suppressed;

    /// <summary>
    /// BT (analysis/terminal-hostility-graph)：本权威端机器从自身原生状态读回的最后一个判定
    /// （<see cref="Verify"/>）。权威端把它回传给上报的客户端，因此客户端日志
    /// 无需那台机器的日志就带有拥有机器人的机器的答案。
    /// </summary>
    private static int _lastVerifyYes;
    private static int _lastVerifyNo;
    private static int _lastVerifyAllies;

    /// <summary>host / client / headless / single——本移植的词汇。</summary>
    internal static string Side => FikaRaidGate.Side;

    /// <summary>在拥有机器人模拟、因而拥有发出的那台机器上为 true。</summary>
    internal static bool IsAuthority => FikaBackendUtils.IsServer || FikaBackendUtils.IsHeadless;

    internal static void Reset()
    {
        lock (Written)
        {
            Written.Clear();
            Granted.Clear();
            NativeApplied.Clear();
            AccessYes.Clear();
            AccessNo.Clear();
            _suppressed = 0;
        }

        // BT (analysis/terminal-hostility-graph)：fan-out 这一族同样每场战局一份。
        FikaTerminalHostility.Reset();
    }

    /// <summary>
    /// 原生入口点本身（<c>SentryBossLogic.OnTerminalAccessGained</c>）在本机
    /// 触发。由补丁在每台持有哨兵 boss 逻辑的机器上调用：主机通过原生事件
    /// 到达它，客户端通过本类的副本到达它。
    /// </summary>
    internal static void NoteNativeGrant(Player player, int accessedCount, string source)
    {
        var profileId = ProfileIdOf(player);
        var state = source == "native" ? "verified" : "applied";
        Line("grant:" + profileId + ":" + source, "card=swiped role=" + RoleOf(player)
            + " state=access-" + (state == "verified" ? "gained" : "applied")
            + " applied=yes side=" + Side
            + " sentries=" + CountLocalSentries()
            + " accessed=" + accessedCount
            + " profile=" + Short(profileId)
            + " -> result=" + (state == "verified" ? "sentry now treats this player as accessed" : "native entry point ran"));

        if (source != "native" || string.IsNullOrEmpty(profileId))
        {
            return;
        }

        // 本机自己的哨兵现在已经通过原生方法持有该事实：发出那一半绝不能
        // 再次应用它（见 NoteNativeEmission）。
        lock (Written)
        {
            NativeApplied.Add(profileId);
        }

        // 发出那一半：只有权威端机器会要求其它机器应用同一个事实，并且
        // 每个玩家只要求一次。
        if (IsAuthority && Remember(profileId))
        {
            Broadcast(player, profileId, true);
        }
    }

    /// <summary>
    /// 同一规则的第三个原生表面：本机为某个玩家抬升了原生事件
    /// （<c>GlobalEventDispatcher.ApplyOnTerminalTransitAccessGained</c>）。每台机器都为它运行交互的那个玩家
    /// 抬升它——包括客户端，客户端在本地运行它自己玩家的
    /// 交互（analysis/terminal-rauf-hostility-2，客户端 r32 那行
    /// 「the native event was raised on this machine」）。
    ///
    /// <para>缺的不是发出，而是方向：一台不拥有机器人模拟的机器
    /// ——一个 Fika 客户端——没有任何东西可以应用该事实，而做决定的那台机器（权威端，
    /// 它模拟每一个机器人）从未听说过它。因此本方法只负责路由这个事实：客户端
    /// 每个玩家向权威端上报一次；权威端应用它（当它自己哨兵的原生
    /// 订阅者没有应用时：没有哨兵实例），然后把它交给对端。</para>
    /// </summary>
    internal static void NoteNativeEmission(Player player)
    {
        var profileId = ProfileIdOf(player);

        Line("emit:" + profileId,
            "card=swiped role=" + RoleOf(player)
            + " state=access-gained applied=yes side=" + Side
            + " sentries=" + CountLocalSentries()
            + " profile=" + profileId + " raidId=" + SafeRaidId(player)
            + " -> result=the native event was raised on this machine");

        if (!RepairEnabled || string.IsNullOrEmpty(profileId))
        {
            return;
        }

        // BT：运行交互的那台机器把同一事实交给在这里也能对此玩家触发的其它系统
        // （包括它自己场景中由代码驱动的区域）——上面的哨兵入口并不是
        // 「此玩家拥有访问权限」的唯一读者。
        FikaTerminalHostility.Deliver(player, "native-event", true);

        var nativeHere = false;
        lock (Written)
        {
            nativeHere = NativeApplied.Contains(profileId);
        }

        if (!IsAuthority)
        {
            if (!RelayToAuthorityEnabled)
            {
                Line("emit:relay-off:" + Short(profileId),
                    "card=swiped role=" + RoleOf(player) + " state=access-gained applied=yes side=" + Side
                    + " profile=" + Short(profileId) + " raidId=" + SafeRaidId(player)
                    + " -> result=relay-off (FikaSentryAccess.RelayToAuthorityEnabled=false): this machine owns no bot and the authority is not told");
                return;
            }

            // 客户端根本不拥有机器人：这个事实在这里毫无价值，而答案由权威端
            // 拥有。每场战局每个玩家上报一次。
            if (Remember(profileId))
            {
                Broadcast(player, profileId, true, "reported to the authority (this machine owns no bots)");
            }

            return;
        }

        if (nativeHere)
        {
            // 本机的哨兵 postfix 已经应用了该事实（并把它分发出去）：原生
            // 入口做了工作，本方法不增加任何东西。
            return;
        }

        // 权威端抬升了事件，但它自己的哨兵没有任何一个消费了它（它此刻不持有哨兵
        // boss 逻辑，因此原生入口没有可运行的机器本地对象）：把它应用在
        // 本机原生链无法到达的地方——发出即是事实——并把它交给
        // 对端。应用是幂等的，分发每个玩家只发生一次。
        Apply(player, true, "native-event");
        if (Remember(profileId))
        {
            Broadcast(player, profileId, true, "handed to the peers (this machine raised the event without a sentry of its own)");
        }
    }

    /// <summary>
    /// 某台机器把 <paramref name="profileId"/> 的事实上报给本机。在主机上这是一个
    /// 在本地运行了原生链的客户端（它拥有其玩家的交互）；该事实会被应用并
    /// 交给其它对端。在客户端上这是主机的副本，只在本地应用。
    /// </summary>
    internal static void Receive(string profileId, int raidId, bool granted, bool fromClient,
        int hostAccessYes = 0, int hostAccessNo = 0, int hostAllies = 0)
    {
        if (string.IsNullOrEmpty(profileId))
        {
            Line("recv:empty", "card=swiped role=- state=applied applied=no side=" + Side
                + " -> result=empty-profile-id");
            return;
        }

        var world = Singleton<GameWorld>.Instance;
        var player = world != null ? world.GetAlivePlayerByProfileID(profileId) : null;
        if (player == null)
        {
            Line("recv:unresolved:" + profileId, "card=swiped role=- state=applied applied=no side=" + Side
                + " profile=" + Short(profileId) + " raidId=" + raidId
                + " -> result=player-not-in-this-world (nothing applied; the state is re-applied on the next report)");
            return;
        }

        // 客户端可以自行做出的判断：当到达的事实正是本机
        // 所上报的那个事实时，该数据包只可能来自权威端，而权威端只有在自己的
        // 哨兵（或它们的组）收下之后才会分发它——因此这一行就是权威端的确认，
        // 无需权威端的日志即可读取。在这一轮之前，客户端根本收不到关于自身
        // profile 的这类数据包（r32：唯一的 packet-from-host 行写的是主机的玩家）。
        if (!IsAuthority && !fromClient && IsLocalProfile(world, profileId))
        {
            // BT (analysis/terminal-hostility-graph)：回响中最有料的部分——权威端如今
            // 会把它自己的读取结果带回来（它的哨兵对多少个对象回答了 yes/no），因此客户端日志
            // 无需那台机器的日志就带有拥有机器人的机器的答案。
            var authorityAnswered = hostAccessYes + hostAccessNo > 0;
            Line("confirm:" + Short(profileId) + ":" + granted + ":" + Short(Side),
                "card=swiped role=" + RoleOf(player) + " state=confirm applied=- side=" + Side
                + " source=packet-from-host via=authority-echo"
                + " granted=" + (granted ? "yes" : "no")
                + " hostSentryAccess=yes:" + hostAccessYes + "/no:" + hostAccessNo
                + " hostAlliesByData=" + hostAllies
                + " profile=" + Short(profileId) + " raidId=" + raidId
                + " -> result=" + (authorityAnswered && hostAccessYes == 0
                    ? "the authority answered this machine's own report, but its own sentry still refuses the player (the native entry had no sentry object there: the group container is the landing to read on that machine)"
                    : "the authority answered this machine's own report (the authority only echoes a fact its own sentries took), so the swipe is in force where the bots live"));
        }

        var applied = Apply(player, granted, fromClient ? "packet-from-client" : "packet-from-host");
        if (IsAuthority && fromClient && applied > 0 && Remember(profileId))
        {
            // 一个客户端在它自己的世界上运行了原生链；答案归主机，因此其它对端
            // 从这里拿到同一行，而不是从该客户端处拿到。
            Broadcast(player, profileId, granted);
        }
    }

    /// <summary>当某次上报的 profile id 是本机本地玩家的 profile id 时为 true。</summary>
    private static bool IsLocalProfile(GameWorld world, string profileId)
    {
        try
        {
            return world != null && world.MainPlayer != null && world.MainPlayer.ProfileId == profileId;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 唯一的应用点：私有的原生状态通过原生入口点被写入
    /// （<c>SentryBossLogic.OnTerminalAccessGained</c>），因此读取它的规则
    /// （<c>BossSentryHostilityRule</c>）无法把这次授予与原生授予区分开。
    /// </summary>
    internal static int Apply(Player player, bool granted, string source)
    {
        if (player == null)
        {
            return 0;
        }

        if (!RepairEnabled)
        {
            Line("apply:off:" + ProfileIdOf(player), "card=swiped role=" + RoleOf(player)
                + " state=applied applied=no side=" + Side + " source=" + source
                + " -> result=repair-off (FikaSentryAccess.RepairEnabled=false)");
            return 0;
        }

        var applied = 0;
        var considered = 0;
        var allies = 0;
        var sentryBots = 0;
        try
        {
            var owners = LocalBotOwners();
            for (var i = 0; i < owners.Count; i++)
            {
                var logic = SentryLogicOf(owners[i]);
                if (logic == null)
                {
                    continue;
                }

                considered++;
                if (!granted)
                {
                    continue;
                }

                try
                {
                    // 原生路径：原生事件所调用的同一个方法。
                    logic.OnTerminalAccessGained(player);
                    applied++;
                }
                catch (Exception exception)
                {
                    Line("apply:error:" + Short(ProfileIdOf(player)) + ":" + considered,
                        "card=swiped role=" + RoleOf(player) + " state=applied applied=no side=" + Side
                        + " source=" + source + " -> result=native-threw "
                        + exception.GetType().Name);
                }
            }
        }
        catch (Exception exception)
        {
            Line("apply:scan-error", "card=swiped role=- state=applied applied=no side=" + Side
                + " source=" + source + " -> result=scan-threw " + exception.GetType().Name);
            return 0;
        }

        // 第二个落点 (analysis/terminal-rauf-hostility-2)。在一台不持有哨兵 boss 逻辑的
        // 机器上，第一个落点没有对象——每台 Fika 客户端都是这样，权威端在其哨兵
        // 未生成时也是如此。同一个事实在原生状态中还存在第二次，位于上一层：
        // 该组的敌对性数据携带表达哨兵访问所用的那个容器
        // （BotGroupInnerHostilityData.AlliesBySentryAcces，一个玩家 id 集合——与
        // SentryBossLogic._accessedPlayerIds 相同的 id 空间）。它只为本机哨兵机器人的组
        // 写入，且仅在原生入口点完全无法使用时写入；该组的其它任何东西都不被
        // 触碰（没有敌人列表、没有规则、没有阈值）。
        var via = applied > 0 ? ViaNativeSentry : ViaNone;
        if (granted && applied == 0 && HostilityDataEnabled)
        {
            try
            {
                ApplyToHostilityData(player, out sentryBots, out allies);
                if (allies > 0)
                {
                    via = ViaHostilityData;
                }
            }
            catch (Exception exception)
            {
                Line("apply:data-error:" + Short(ProfileIdOf(player)),
                    "card=swiped role=" + RoleOf(player) + " state=applied applied=no side=" + Side
                    + " source=" + source + " via=" + ViaNone
                    + " -> result=hostility-data-threw " + exception.GetType().Name);
            }
        }
        else if (granted && applied == 0 && !HostilityDataEnabled)
        {
            via = "none (HostilityDataEnabled=false)";
        }

        var landed = applied > 0 || allies > 0;
        var verdict = applied > 0
            ? "the sentries of this machine now accept the player"
            : allies > 0
                ? "the sentry groups of this machine now hold the player as an ally by sentry access"
                : considered > 0 ? "no-sentry-this-machine"
                : sentryBots > 0 ? "no-sentry-boss-logic"
                : "no-sentry-and-no-group-on-this-machine";
        Line("applied:" + Short(ProfileIdOf(player)) + ":" + source + ":" + considered,
            "card=swiped role=" + RoleOf(player) + " state=applied applied=" + (landed ? "yes" : "no")
            + " side=" + Side + " source=" + source + " via=" + via
            + " sentries=" + applied + "/" + considered
            + " allies=" + allies + " sentryBots=" + sentryBots
            + " profile=" + Short(ProfileIdOf(player)) + " -> result=" + verdict);

        // 判断本报告所依据的检查：本机的哨兵现在是否对访问
        // 问题回答了 yes，以及它哨兵组自己的容器是否持有该玩家？读取是通过原生
        // 成员本身进行的，而不是通过我们自己的记账。
        Verify(player, source, considered, allies, sentryBots);

        // BT：该事实唯一的应用点同时也是 fan-out 点——哨兵（上面）、
        // 组容器（上面），以及那些不归我们管、不持有我们任何容器的系统（本机
        // 由代码驱动的区域、本机机器人的组表）。
        FikaTerminalHostility.Deliver(player, source, granted);

        return applied > 0 ? applied : allies;
    }

    /// <summary>
    /// 判定行：原生问题 <c>IAccessBossLogic.HaveAccess(playerId)</c> 在应用之后
    /// 于本机上被重新提问——并且，对第二个落点而言，本机
    /// 哨兵组的原生容器也会被问同一个问题。任一列出现 yes，都是
    /// 本机的哨兵会在下一次敌对性重算中不再把该玩家当作
    /// 敌人的证明。
    /// </summary>
    private static void Verify(Player player, string source, int considered, int allies, int sentryBots)
    {
        var raidId = -1;
        var yes = 0;
        var no = 0;
        var dataYes = 0;
        var dataNo = 0;
        try
        {
            raidId = SafeRaidId(player);
            var owners = LocalBotOwners();
            for (var i = 0; i < owners.Count; i++)
            {
                var logic = SentryLogicOf(owners[i]);
                if (logic == null)
                {
                    continue;
                }

                if (logic.HaveAccess(raidId))
                {
                    yes++;
                }
                else
                {
                    no++;
                }
            }

            var sentries = SentryRoleOwners();
            for (var i = 0; i < sentries.Count; i++)
            {
                var state = ByData(sentries[i], raidId, false);
                if (state == 1)
                {
                    dataYes++;
                }
                else if (state == 0)
                {
                    dataNo++;
                }
            }
        }
        catch (Exception exception)
        {
            Line("verify:error:" + Short(ProfileIdOf(player)),
                "card=swiped role=" + RoleOf(player) + " state=verify applied=- side=" + Side
                + " source=" + source + " -> result=native-threw " + exception.GetType().Name);
            return;
        }

        var verdict = yes > 0
            ? "this machine's sentry accepts the player"
            : dataYes > 0
                ? "this machine's sentry groups hold the player as an ally by sentry access"
                : "this machine's sentry still refuses";
        lock (Written)
        {
            _lastVerifyYes = yes;
            _lastVerifyNo = no;
            _lastVerifyAllies = dataYes;
        }

        Line("verify:" + Short(ProfileIdOf(player)) + ":" + source + ":" + yes + ":" + no,
            "card=swiped role=" + RoleOf(player) + " state=verify applied=" + (yes > 0 || dataYes > 0 ? "yes" : "no")
            + " side=" + Side + " source=" + source + " sentries=" + considered
            + " haveAccess=yes:" + yes + "/no:" + no
            + " alliesByData=yes:" + dataYes + "/no:" + dataNo
            + " allies=" + allies + " sentryBots=" + sentryBots
            + " raidId=" + raidId
            + " -> result=" + verdict);
    }

    /// <summary>
    /// 本机的哨兵机器人，按其原生角色（<c>BotOwner.Role</c> =
    /// <c>WildSpawnType.sentry</c>，即终端 RAUF 的角色）。一台不拥有机器人的机器
    /// ——一个 Fika 客户端——一个都没有，于是该族的每一行都会这么说。
    /// </summary>
    private static List<BotOwner> SentryRoleOwners()
    {
        var result = new List<BotOwner>();
        var owners = LocalBotOwners();
        for (var i = 0; i < owners.Count; i++)
        {
            try
            {
                if (owners[i].Role == WildSpawnType.sentry)
                {
                    result.Add(owners[i]);
                }
            }
            catch (Exception)
            {
                // 角色无法读取的机器人不属于这个问题。
            }
        }

        return result;
    }

    /// <summary>
    /// 对第二个落点原生状态的一个问题，即比哨兵高一层的同一事实：
    /// <c>BotOwner.BotsGroup</c> -> <c>BotsGroup.BotGroupHostility</c> ->
    /// <c>BotGroupHostility.InnerHostilityData</c> -> <c>BotGroupInnerHostilityData.AlliesBySentryAcces</c>。
    /// 这四个都是本构建的原生成员（interop 面：<c>get_BotsGroup</c>、
    /// <c>get_BotGroupHostility</c>、<c>get_InnerHostilityData</c>、<c>get_AlliesBySentryAcces</c>），而该
    /// 容器就是表达哨兵访问的那一个 id 集合——与
    /// <c>SentryBossLogic._accessedPlayerIds</c> 相同的 id 空间。
    ///
    /// <para>当该机器人在本机上不携带这样的容器时返回 -1（客户端的观察机器人
    /// 根本没有组），容器存在但不含该玩家时返回 0，持有他时返回 1。
    /// 传入 <paramref name="write"/> 时会补上缺失的 id，并通过同一个原生 getter
    /// 读回答案，因此一次未能落地的写入会报告 0。</para>
    /// </summary>
    private static int ByData(BotOwner owner, int raidId, bool write)
    {
        try
        {
            var group = owner?.BotsGroup;
            if (group == null)
            {
                return -1;
            }

            var hostility = group.BotGroupHostility;
            if (hostility == null)
            {
                return -1;
            }

            var inner = hostility.InnerHostilityData;
            if (inner == null)
            {
                return -1;
            }

            var allies = inner.AlliesBySentryAcces;
            if (allies == null)
            {
                return -1;
            }

            if (write && !allies.Contains(raidId))
            {
                allies.Add(raidId);
            }

            return allies.Contains(raidId) ? 1 : 0;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>
    /// 第二个落点的写入：本机玩家对象的玩家 id 会被写进本机
    /// 每个哨兵机器人所属组的容器中。该组的其它任何东西都不被触碰。
    /// </summary>
    private static void ApplyToHostilityData(Player player, out int sentryBots, out int allies)
    {
        sentryBots = 0;
        allies = 0;

        var raidId = SafeRaidId(player);
        if (raidId < 0)
        {
            return;
        }

        var owners = SentryRoleOwners();
        sentryBots = owners.Count;
        for (var i = 0; i < owners.Count; i++)
        {
            if (ByData(owners[i], raidId, true) == 1)
            {
                allies++;
            }
        }
    }

    /// <summary>
    /// 把该事实交给其它每一台机器。主机向它的对端广播；客户端把它发送给
    /// 主机（随后由主机拥有答案并把它交给其余机器）。
    /// </summary>
    private static void Broadcast(Player player, string profileId, bool granted, string verdict = null)
    {
        try
        {
            var manager = FikaSingleton<IFikaNetworkManager>.Instance;
            if (manager == null)
            {
                Line("broadcast:none:" + Short(profileId), "card=swiped role=" + RoleOf(player)
                    + " state=access-gained applied=no side=" + Side + " -> result=no-network-manager");
                return;
            }

            var packet = new SentryAccessPacket
            {
                ProfileId = profileId,
                RaidId = SafeRaidId(player),
                Granted = granted,
                FromClient = (byte)(FikaBackendUtils.IsClient ? 1 : 0),
                // BT：只有权威端持有机器人，因此也持有判定；客户端报告零值，
                // 由权威端填入它回送的副本。
                HostAccessYes = IsAuthority ? _lastVerifyYes : 0,
                HostAccessNo = IsAuthority ? _lastVerifyNo : 0,
                HostAllies = IsAuthority ? _lastVerifyAllies : 0
            };

            manager.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            Line("broadcast:" + Short(profileId) + ":" + Short(Side) + ":" + (verdict ?? "broadcast"),
                "card=swiped role=" + RoleOf(player) + " state=access-gained applied=yes side=" + Side
                + " granted=" + (granted ? "yes" : "no") + " profile=" + Short(profileId)
                + " raidId=" + packet.RaidId
                + " -> result=" + (verdict ?? "broadcast"));
        }
        catch (Exception exception)
        {
            Line("broadcast:error:" + Short(profileId), "card=swiped role=" + RoleOf(player)
                + " state=access-gained applied=no side=" + Side
                + " -> result=broadcast-threw " + exception.GetType().Name);
        }
    }

    /// <summary>
    /// 本机的每一个机器人所有者。客户端和主机都把它们的机器人放在世界的
    /// 玩家列表里，而机器人通过 <c>Player.AIData.BotOwner</c> 携带其所有者。
    /// </summary>
    private static List<BotOwner> LocalBotOwners()
    {
        var result = new List<BotOwner>();
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
                if (player == null)
                {
                    continue;
                }

                var ai = player.AIData;
                if (ai == null || !ai.IsAI)
                {
                    continue;
                }

                var owner = ai.BotOwner;
                if (owner != null)
                {
                    result.Add(owner);
                }
            }
            catch (Exception)
            {
                // 未完整生成的玩家不属于这个问题。
            }
        }

        return result;
    }

    /// <summary>
    /// 某个机器人的哨兵 boss 逻辑，当该机器人不是哨兵时为 null。游戏把它存放在 <c>BotBoss.BossLogic</c>
    /// 中（<c>BotOwner.Boss.BossLogic</c>），而哨兵是实现
    /// <c>IAccessBossLogic</c> 的唯一一种 boss 逻辑。
    /// </summary>
    private static SentryBossLogic SentryLogicOf(BotOwner owner)
    {
        try
        {
            var boss = owner?.Boss;
            if (boss == null)
            {
                return null;
            }

            var logic = boss.BossLogic;
            if (logic == null)
            {
                return null;
            }

            return logic.TryCast<SentryBossLogic>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>本机持有多少个哨兵 boss 逻辑（在该族的每一行中都会写明）。</summary>
    internal static int CountLocalSentries()
    {
        try
        {
            var owners = LocalBotOwners();
            var count = 0;
            for (var i = 0; i < owners.Count; i++)
            {
                if (SentryLogicOf(owners[i]) != null)
                {
                    count++;
                }
            }

            return count;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>某玩家在本机上的第一次 yes（值得写入的那一行）。</summary>
    internal static bool NoteAccessYes(int raidId)
    {
        lock (Written)
        {
            return AccessYes.Add(raidId);
        }
    }

    /// <summary>
    /// 对原生问题的一次拒绝。它是一台哨兵从未见过该授予的机器的默认
    /// 答案，因此它被计数，并按有界的方式每个玩家写一次——第一行是证据，
    /// 其余的是每帧的重算。
    /// </summary>
    internal static void NoteAccessNo(int raidId)
    {
        int count;
        bool first;
        lock (Written)
        {
            AccessNo.TryGetValue(raidId, out count);
            count++;
            AccessNo[raidId] = count;
            first = count == 1;
        }

        if (!first || !IsAuthority)
        {
            return;
        }

        Line("haveaccess-no:" + raidId,
            "card=swiped role=- state=verify applied=no side=" + Side + " raidId=" + raidId
            + " sentries=" + CountLocalSentries()
            + " -> result=the sentry of this machine still refuses this player");
    }

    /// <summary>
    /// 哨兵规则某一次计算的判定（由 EvaluateHostility 补丁调用）。两种
    /// 有意义的情形会被写出；其余全部由去重写入器计数，因此一场战局无论帧率
    /// 多高都只花掉几行。
    /// </summary>
    internal static void RuleVerdict(Player player, int raidId, string verdict)
    {
        var unarmed = IsUnarmed(player);
        if (verdict == "Enemy")
        {
            Line("rule-enemy:" + raidId + ":" + (unarmed ? "unarmed" : "armed"),
                "card=unknown role=" + RoleOf(player) + " state=verify applied=no side=" + Side
                + " verdict=Enemy target=player raidId=" + raidId
                + " hands=" + (unarmed ? "empty" : "occupied")
                + " sentries=" + CountLocalSentries()
                + " -> result=this machine's sentry treats the player as an enemy"
                + (unarmed ? " and the player carries nothing (the reported case)" : string.Empty));
            return;
        }

        if (verdict == "Neutral" || verdict == "Ally")
        {
            Line("rule-ok:" + raidId + ":" + verdict,
                "card=swiped role=" + RoleOf(player) + " state=verify applied=yes side=" + Side
                + " verdict=" + verdict + " target=player raidId=" + raidId
                + " hands=" + (unarmed ? "empty" : "occupied")
                + " sentries=" + CountLocalSentries()
                + " -> result=this machine's sentry does not attack this player");
        }
    }

    private static bool Remember(string profileId)
    {
        lock (Written)
        {
            return Granted.Add(profileId);
        }
    }

    private static int SafeRaidId(Player player)
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

    private static string ProfileIdOf(Player player)
    {
        try
        {
            return player?.ProfileId ?? "-";
        }
        catch (Exception)
        {
            return "-";
        }
    }

    /// <summary>某玩家的原生角色名（RAUF 哨兵是机器人——它们的角色就是证明）。</summary>
    /// <summary>
    /// 玩家什么都没持有时为 true（所报告规则针对的状态：一个已通过
    /// 卡片检查的玩家，只要不带武器就不会被攻击）。
    /// </summary>
    internal static bool IsUnarmed(Player player)
    {
        try
        {
            var hands = player?.HandsController;
            if (hands == null)
            {
                return true;
            }

            return hands.TryCast<Player.EmptyHandsController>() != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>某玩家的原生角色名（RAUF 哨兵是机器人——它们的角色就是证明）。</summary>
    internal static string RoleOf(Player player)
    {
        try
        {
            if (player == null || player.Profile?.Info?.Settings == null)
            {
                return "-";
            }

            return player.Profile.Info.Settings.Role.ToString();
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static string Short(string value) =>
        string.IsNullOrEmpty(value) ? "-" : value.Length > 8 ? value.Substring(0, 8) : value;

    /// <summary>
    /// 该族的写入器：有界、去重、绝不让异常抛进原生路径。它独立于
    /// 'Coop / Diagnostics'，因为它本身就是该修复的证据，并且它通过
    /// 常规的 Fika logger 写入——因此在 LogOutput.log 里做一次 grep、在 Fika-diagnostics.log 里做一次 grep 都有效。
    /// </summary>
    internal static void Line(string key, string text)
    {
        try
        {
            lock (Written)
            {
                if (Written.Count >= MaxLines || !Written.Add(key))
                {
                    _suppressed++;
                    return;
                }
            }

            FikaGlobals.LogInfo("[SentryDiag] " + text, "FikaSentryAccess");
        }
        catch (Exception)
        {
            // 诊断绝不能成为一场战局崩掉的原因。
        }
    }
}
