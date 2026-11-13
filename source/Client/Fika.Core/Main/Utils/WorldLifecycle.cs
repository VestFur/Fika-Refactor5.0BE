using System;
using Comfort.Common;
using EFT;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.Components;
using Fika.Core.Main.Patches.AI;
using Fika.Core.Main.Patches.BTR;
using Fika.Core.Main.HostClasses;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 唯一回答「本进程此刻持有的世界是否仍是上线的那个世界」的地方。
///
/// 它为何存在(第 12 轮,analysis/logs-r12test-20261012-184047):那次运行的两次崩溃都是
/// UnityPlayer.dll 内部的访问违例(Windows 事件 1000,异常代码 0xc0000005,故障偏移
/// 分别为 15:48:18 的 0x161f56e 与 16:45:51 的 0xe27f07),而且两者都落在拆除窗口内:第一次
/// 发生在主机记录 "world swap #2 noted (reason=DestroyNetManager: transit teardown, host world
/// is down)" 之后 4 秒(Fika-diagnostics.log,frame=48297),第二次发生在关机 5 秒时,此前一次 raid 启动
/// 已被引擎取消过两次。原生 AV 无法被捕获,因此要熬过那个窗口,唯一的办法就是
/// 确保我们这边没有任何东西还会去碰那个已经下线的世界。
///
/// epoch 是其中廉价的一半:我们每个跨帧边界保留的对象都会记录自己创建时的
/// epoch,而看到不同 epoch 的持有者就知道自己面对的是一个已经消失的世界。
/// 在一个世界被拆除与其后继世界被创建之间,<see cref="WorldsAlive"/> 为 false,
/// 而这正是 Singleton&lt;GameWorld&gt;.Instance 仍可能把刚刚结束的那场 raid 已释放的
/// 世界交回来的那个窗口。
///
/// 拆除本身是一个单一、幂等、不抛异常的序列:停掉泵原本会
/// 持续步进到已死世界的那些协程,丢弃会话序列持有的原生枚举器,并
/// 清空每个以该世界的原生对象为键、或持有该世界原生对象的静态缓存。每次清空都会报告
/// 自己丢掉了多少条目,因此下一次运行能在一行里看出某个缓存是否真的把状态
/// 带过了这次交换。
///
/// 回滚:本类每个调用点都是一条语句,整个行为可以用
/// <see cref="Enabled"/> = false 关掉(一行,见 analysis/transit-crash-unload 的报告)。
/// </summary>
internal static class WorldLifecycle
{
    /// <summary>本类判定行的诊断作用域(按前缀匹配,见 FikaDiagnostics)。</summary>
    private const string Scope = "WorldLifecycle";

    private const string Tag = "[WorldLifecycle]";

    /// <summary>
    /// 本轮改动的单行总开关。为 False 时这里每个方法都变成空操作,这等价于
    /// analysis/transit-crash-unload 之前的构建,唯一例外是 [LoadCost] 那一行。
    /// </summary>
    internal static bool Enabled = true;

    private static int _epoch;
    private static bool _worldsAlive = true;
    private static string _downReason = "none";
    private static int _downFrame = -1;
    private static float _downAt;
    private static string _upReason = "none";

    /// <summary>当前世界代际。每次世界拆除自增一次。</summary>
    internal static int Epoch => _epoch;

    /// <summary>在一个世界被拆除与下一个世界被创建之间为 false。</summary>
    internal static bool WorldsAlive => _worldsAlive;

    /// <summary>恰好在该窗口内为 true。对每帧调用的调用方来说足够廉价。</summary>
    internal static bool WorldDown => !_worldsAlive;

    /// <summary>
    /// 当在 <paramref name="epoch"/> 中捕获的值属于当前的世界代际时为
    /// true。这里刻意*不*以 <see cref="WorldsAlive"/> 作为前置条件:下一场 raid 的对象
    /// 正是在交换窗口内构造的(它的世界控制器、它的游戏世界组件),而它们
    /// 必须在存在的那一刻就被视为有效——窗口状态回答的是另一个问题,即刚刚结束的那场
    /// raid 是否仍可使用,而那个问题由 <see cref="WorldDown"/> 来问。
    /// </summary>
    internal static bool IsCurrent(int epoch)
    {
        return epoch == _epoch;
    }

    /// <summary>
    /// 当给定世界对象就是引擎当前交出的世界时为 true。一个跨帧边界存下了
    /// 世界引用的持有者可以在使用它之前调用本方法;对已结束 raid 的已释放
    /// 世界,以及对引擎已经丢弃的世界,它返回 false。
    /// </summary>
    internal static bool WorldIsCurrent(World world)
    {
        if (world == null)
        {
            return false;
        }

        try
        {
            return Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance != null
                && Singleton<GameWorld>.Instance.Pointer == world.Pointer;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 记录本进程此前持有的世界已经消失。幂等:同一窗口内的第二次调用
    /// 只会写一行日志,因此一次同时经过世界组件和网络管理器的
    /// 拆除不会清两次。
    /// </summary>
    internal static void MarkWorldTornDown(string reason)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            if (!_worldsAlive)
            {
                Diag("world teardown ignored (reason=" + reason + "): the world of epoch " + _epoch
                    + " is already down since frame " + _downFrame + " (" + _downReason + ")");
                return;
            }

            _epoch++;
            _worldsAlive = false;
            _downReason = reason;
            _downFrame = UnityEngine.Time.frameCount;
            _downAt = UnityEngine.Time.realtimeSinceStartup;

            

            var routines = FikaRoutinePump.Instance.StopWorldBound("the world went down (" + reason + ")");
            var sessionEnumerator = SessionPump.OnWorldTornDown(reason);

            var controllers = FikaWorldControllers.ClearForWorldTeardown();
            var quests = FikaQuestController.ClearForWorldTeardown();
            var magazines = FikaMagazineProcesses.ClearForWorldTeardown();
            var interactions = RaidInteractionSync.ForceResetForWorldTeardown(reason);
            var btr = BTRSide_Patches.ClearForWorldTeardown();
            var firstAid = BotFirstAidPending.ClearForWorldTeardown();
            var surgery = BotSurgeryPending.ClearForWorldTeardown();
            var timeouts = BotFirstAidTimeouts.ClearForWorldTeardown();
            var grenades = FikaGrenadeExplosion.ClearForWorldTeardown();
            var questSessions = ServerAutoStartQuests.ClearForWorldTeardown();
            // 转发控制器的同位置退出抑制表与它的掉线登记表都持有正在下线的那个世界的
            // 原生对象;清理由转发侧负责(analysis/
            // transit-abort-rules §5),而这里是唯一权威的调用点。
            var transitExits = TransitCoLocatedExits.ForceResetForWorldTeardown(reason);

            

        }
        catch (Exception exception)
        {
            // 拆除路径不得引入新的失败模式:世界无论如何都在下线。
            Warn("the world teardown of '" + reason + "' threw and was cut short: " + exception.Message);
        }
    }

    /// <summary>
    /// 记录一个新的世界已存在。清除「世界已下线」状态,关闭 [LoadCost] 窗口
    /// 并打印交换窗口持续了多久。
    /// </summary>
    internal static void MarkWorldUp(string reason)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var wasDown = !_worldsAlive;
            var seconds = wasDown ? UnityEngine.Time.realtimeSinceStartup - _downAt : 0f;
            _worldsAlive = true;
            _upReason = reason;
            var frames = wasDown ? UnityEngine.Time.frameCount - _downFrame : 0;

            

            if (wasDown)
            {
            }
        }
        catch (Exception exception)
        {
            Warn("the world-up record of '" + reason + "' threw: " + exception.Message);
        }
    }

    /// <summary>报告用的一行:本进程此刻对世界的看法。</summary>
    internal static string Describe()
    {
        return "epoch=" + _epoch + ", worldsAlive=" + _worldsAlive + ", downReason=" + _downReason
            + ", downFrame=" + _downFrame + ", upReason=" + _upReason;
    }

    private static void Diag(string message)
    {
        
        try
        {
            FikaGlobals.LogInfo(Tag + " " + message, "WorldLifecycle");
        }
        catch (Exception)
        {
        }

        try
        {
            
        }
        catch (Exception)
        {
        }
    }

    private static void Warn(string message)
    {
        try
        {
            FikaGlobals.LogWarning(Tag + " " + message, "WorldLifecycle");
        }
        catch (Exception)
        {
        }

        try
        {
            
        }
        catch (Exception)
        {
        }
    }
}
