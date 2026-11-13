using System;
using System.Collections.Generic;
using System.Text;
using Comfort.Common;
using CommonAssets.Scripts.Game;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using UnityEngine;

namespace Fika.Core.Main.Components;

/// <summary>
/// 本地玩家的撤离情况，按每次尝试分别上报；此外还释放不再有中转
/// 可归属的中转撤离封禁。
/// <para>
/// 本文件负责回答的问题（除此之外没有别处回答）：
/// </para>
/// <list type="bullet">
/// <item><b>是谁拒绝了他。</b> 每个不同的尝试状态一行，写明点位、它的状态、
/// 原生点位是否接纳了玩家、是否存在可供他的撤离处理器、
/// 信号弹门禁是否放行他、未满足的需求类型、倒计时位置以及
/// 守卫状态 (<c>ExfiltrationController.BannedPlayers</c>, <c>TransitController.transitPlayers</c>)。
/// <see cref="FikaExfilManager.Update"/> 中的两道门禁目前是静默拒绝的：信号弹拒绝
/// 会去重成每场 raid 一行，而未满足的需求则什么都不说。</item>
/// <item><b>停止 raid 是否被请求过。</b> 本管理器对 <c>CoopGame.Extract</c> 的每次
/// 调用都被加了括号，因此一次未注册本地玩家就返回的调用（即
/// <c>HostGameController.Extract</c> / <c>ClientGameController.Extract</c> 中的提前返回）会
/// 变得可见，而不是看起来什么都没发生。</item>
/// <item><b>守卫确实被释放了。</b> 提交进入中转会把玩家封禁出撤离，
/// 并禁用撤离点交互
/// (<c>FikaHostTransitController.InteractWithTransit</c>，主机侧和远程侧两半）。
/// 原生控制器会在转移结束或被取消时释放这一对状态
/// (<c>TransitController.EnableExits(Player)</c>，它会调用
/// <c>ExfiltrationController.EnableExitsInteraction()</c>)；本移植没有这样的释放，因此一次
/// 没有把玩家带走的转移 —— 以及任何残留到下一个地点的状态 ——
/// 都会让他无法撤离。本类只释放那些没有活注册
/// 所拥有的条目。</item>
/// </list>
/// <para>
/// 一个封禁条目只有仍然拥有一个处于本场 raid、活着且
/// 未被标记为已撤离的玩家时，才被视为活的。这与 raid 自身使用的是同一套记账，
/// 因此当转移真的在进行中时，释放永远不会触发。
/// </para>
/// </summary>
internal static class FikaExtractDiag
{
    /// <summary>
    /// 已提交的中转会在这段时间内把玩家交接过去。一条在应用之后
    /// 过了这么久仍然活着的注册，就是一次不会发生的转移。
    /// </summary>
    private const float StaleGuardSeconds = 120f;

    private static float _nextSample;
    private static int _guardRaidId = int.MinValue;
    private static float _guardSince = -1f;

    /// <summary>1 Hz 门禁，用于 <see cref="Sample"/>。</summary>
    internal static bool ShouldSample()
    {
        var now = Time.time;
        if (now < _nextSample)
        {
            return false;
        }

        _nextSample = now + 1f;
        return true;
    }

    /// <summary>
    /// 在 raid 开始时运行，紧接管理器绑定本场 raid 的撤离点之后：报告绑定
    /// 了什么，并释放更早地点遗留的一对守卫状态，这一切都发生在玩家
    /// 走到撤离点之前。
    /// </summary>
    internal static void OnRaidBound(CoopGame game, int regularCount, int secretCount)
    {
        try
        {
            _guardRaidId = int.MinValue;
            _guardSince = -1f;

            var player = LocalPlayer();
            if (player == null)
            {
                                return;
            }

                        GuardTick(player, "raid start");
        }
        catch (Exception exception)
        {
                    }
    }

    /// <summary>
    /// 每秒执行：本机的撤离守卫检查。它释放不再有中转可归属的
    /// 中转撤离封禁，正因如此，一次从未把玩家带走的转移才不会让他
    /// 困在 raid 的每一个撤离点上。
    /// </summary>
    internal static void Sample(CoopGame game, ExfiltrationPoint[] points, List<string> handlerPoints, int countdownCount)
    {
        _ = game;
        _ = points;
        _ = handlerPoints;
        _ = countdownCount;
        try
        {
            var player = LocalPlayer();
            if (player == null)
            {
                return;
            }

            GuardTick(player, "tick");
        }
        catch (Exception)
        {
        }
    }



    /// <summary>
    /// 释放本机可见的、其中转已不再真实的每个玩家的撤离守卫条目，
    /// 并在已没有需要等待的对象时重新启用撤离点交互。
    /// </summary>
    private static void GuardTick(Player player, string context)
    {
        try
        {
            var controller = global::CommonAssets.Scripts.Game.ExfiltrationController.Instance;
            if (controller == null)
            {
                return;
            }

            var banned = controller.BannedPlayers;
            if (banned == null)
            {
                return;
            }

            if (banned.Count == 0)
            {
                _guardSince = -1f;
                return;
            }

            var live = CollectLiveRegistrations();
            var localIds = LocalRaidIds(player);
            var anyLive = false;
            for (var i = 0; i < live.Count; i++)
            {
                if (banned.Contains(live[i]))
                {
                    anyLive = true;
                    break;
                }
            }

            var localLive = false;
            for (var i = 0; i < localIds.Count; i++)
            {
                if (live.Contains(localIds[i]))
                {
                    localLive = true;
                    break;
                }
            }

            if (!anyLive)
            {
                // 集合中没有任何一个条目仍然拥有一个处于本场 raid、活着且
                // 未撤离的玩家：没有任何东西在等待转移，所以这一对整体都是残留。
                var count = banned.Count;
                banned.Clear();
                controller.EnableExitsInteraction();
                _guardSince = -1f;
                FikaGlobals.LogWarning($"[ExtractDiag] released the whole exfiltration ban set ({count} entry(ies), not one of them owns a player still in transit in this raid) and re-enabled the exit interaction ({context}) - the port applies the ban and the interaction disable in FikaHostTransitController.InteractWithTransit (the BannedPlayers.Add and DisableExitsInteraction pair) and never calls the native release TransitController.EnableExits -> ExfiltrationController.EnableExitsInteraction");
                return;
            }

            if (localLive)
            {
                // 他自己的注册是活的：仍有转移在进行中。只有时间窗口能决定。
                if (_guardRaidId != player.RaidId || _guardSince < 0f)
                {
                    _guardRaidId = player.RaidId;
                    _guardSince = Time.time;
                    FikaGlobals.LogWarning($"[ExtractDiag] the local player is registered for a transfer (profile={player.ProfileId} raidId={player.RaidId}): the exits stay disabled and are released after {StaleGuardSeconds:F0} s if the transfer does not take him ({context})");
                    return;
                }

                if (Time.time - _guardSince < StaleGuardSeconds)
                {
                    return;
                }

                FikaGlobals.LogWarning($"[ExtractDiag] the local player's transfer registration is {StaleGuardSeconds:F0} s old and the raid is still running: treating it as a transfer that will not happen ({context})");
            }
            else
            {
                _guardSince = -1f;
            }

            var released = new List<string>();
            for (var i = 0; i < localIds.Count; i++)
            {
                if (banned.Remove(localIds[i]))
                {
                    released.Add($"raidId={localIds[i]} (local player)");
                }
            }

            var others = OtherRaidIds(player);
            for (var i = 0; i < others.Count; i++)
            {
                if (banned.Remove(others[i]))
                {
                    released.Add($"raidId={others[i]} (other player)");
                }
            }

            if (released.Count == 0)
            {
                return;
            }

            var left = banned.Count;
            if (left == 0)
            {
                controller.EnableExitsInteraction();
            }

            FikaGlobals.LogWarning($"[ExtractDiag] released {released.Count} stale exfiltration ban(s) [{string.Join(", ", released)}] ({context}); entries left={left}, exit interaction {(left == 0 ? "re-enabled" : "kept disabled: another registration is still live")}");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[ExtractDiag] guard check failed: {exception}");
        }
    }

    /// <summary>
    /// 仍然拥有本场 raid 玩家的每个中转注册的 raid id：该玩家必须
    /// 在场、活着且尚未被标记为已撤离，否则该注册就是死的。
    /// </summary>
    private static List<int> CollectLiveRegistrations()
    {
        var live = new List<int>();
        try
        {
            var transit = Singleton<GameWorld>.Instance?.TransitController;
            var registrations = transit?.transitPlayers;
            if (registrations == null)
            {
                return live;
            }

            var humans = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler?.HumanPlayers;
            var extracted = FikaSingleton<IFikaGame>.Instance?.ExtractedPlayers;
            var local = LocalPlayer();

            foreach (var entry in registrations)
            {
                var profileId = entry.Key;
                if (string.IsNullOrEmpty(profileId))
                {
                    continue;
                }

                var owner = FindHuman(humans, profileId) ?? (Matches(local, profileId) ? local : null);
                if (owner == null)
                {
                    continue;
                }

                if (owner.HealthController?.IsAlive != true)
                {
                    continue;
                }

                if (extracted != null && extracted.Contains(NetIdOf(owner)))
                {
                    continue;
                }

                live.Add(entry.Value);
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[ExtractDiag] could not read the transit registrations: {exception.Message} - the guard check keeps every ban this tick");
        }

        return live;
    }

    private static Player FindHuman(List<FikaPlayer> humans, string profileId)
    {
        if (humans == null)
        {
            return null;
        }

        for (var i = 0; i < humans.Count; i++)
        {
            var human = humans[i];
            if (human != null && string.Equals(human.ProfileId, profileId, StringComparison.Ordinal))
            {
                return human;
            }
        }

        return null;
    }

    private static bool Matches(Player player, string profileId)
    {
        return player != null && !string.IsNullOrEmpty(profileId)
            && string.Equals(player.ProfileId, profileId, StringComparison.Ordinal);
    }

    /// <summary>属于本地玩家的 raid id，按游戏用于封禁的两个键分别给出。</summary>
    private static List<int> LocalRaidIds(Player player)
    {
        var ids = new List<int>();
        AddId(ids, player.RaidId);
        AddId(ids, SafeId(player));
        return ids;
    }

    /// <summary>本场 raid 其他人类玩家的 raid id。</summary>
    private static List<int> OtherRaidIds(Player local)
    {
        var ids = new List<int>();
        var humans = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler?.HumanPlayers;
        if (humans == null)
        {
            return ids;
        }

        for (var i = 0; i < humans.Count; i++)
        {
            var human = humans[i];
            if (human == null || Matches(local, human.ProfileId))
            {
                continue;
            }

            AddId(ids, human.RaidId);
            AddId(ids, SafeId(human));
        }

        return ids;
    }

    private static void AddId(List<int> ids, int id)
    {
        if (id <= 0 || ids.Contains(id))
        {
            return;
        }

        ids.Add(id);
    }

    /// <summary>
    /// <c>Player.Id</c> 是原生写撤离封禁时使用的第二个键；本移植
    /// 写的是 <c>RaidId</c>。两者都会被移除，因此无论由哪条路径写入的集合都会被释放。
    /// </summary>
    private static int SafeId(Player player)
    {
        try
        {
            return player.Id;
        }
        catch (Exception)
        {
            return 0;
        }
    }


    private static int NetIdOf(Player player)
    {
        try
        {
            if (player.TryCast<FikaPlayer>() is { } fikaPlayer)
            {
                return fikaPlayer.NetId;
            }
        }
        catch (Exception)
        {
            // 按 -1 上报。
        }

        return -1;
    }

    private static Player LocalPlayer()
    {
        try
        {
            return Singleton<GameWorld>.Instance?.MainPlayer;
        }
        catch (Exception)
        {
            return null;
        }
    }





    /// <summary>
    /// 本机信号弹撤离链的状态，供 raid 启动行与尝试行使用：
    /// <c>triggers=N</c> 是本地图携带的场景 <c>TriggerFlareSuccess</c> 组件数量
    /// （0 = 该地图只通过侦测区域驱动信号弹），<c>gated=[names]</c> 是被
    /// 分类标记为信号弹门禁的撤离点，而 <c>pending=N</c> 统计仍在等待
    /// 射击者信号弹对象的撤离点视觉重放次数。显示 <c>triggers=0 gated=[]</c> 的机器
    /// 完全无法对信号弹作出反应，而这正是缺少侦测区域和缺少触发链的地图的样子。
    /// </summary>



    private static bool Contains(List<string> names, string name)
    {
        if (names == null)
        {
            return false;
        }

        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>游戏认为对这名玩家仍未满足的需求类型。</summary>


    /// <summary>尝试仍有机会时为 null；否则为使它被拒的那道门禁。</summary>


}
