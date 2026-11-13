using System;
using System.Collections;
using Fika.Core.Main.GameMode;

namespace Fika.Core.Main.Utils;

/// <summary>
/// <see cref="CoopGame"/> 的会话启动序列，由一个普通的托管静态类承载。
///
/// 为什么它不再是 <see cref="CoopGame"/> 的一个方法（第 8 轮，analysis/delegate-targets）：
/// Il2CppInterop 会把注入类中声明的<em>每一个</em>方法注册到 il2cpp，包括
/// 私有方法，并为每个方法分配自己的原生入口（一个 trampoline thunk，
/// <c>ClassInjector.CreateTrampoline</c>），再加上一个共享的托管 invoker
/// （<c>ClassInjector.ConvertMethodInfo</c>：<c>InvokerMethod = GetFunctionPointerForDelegate(GetOrCreateInvoker(...))</c>，
/// <c>MethodPointer = VirtualMethodPointer = GetFunctionPointerForDelegate(GetOrCreateTrampoline(...))</c>）。
/// 2026-10-10 的那次主机 raid 正死在这个构造里：invoker 收尾的 <c>calli</c> 被
/// 引到了 <c>CoopGame.StartSessionPump</c> 的 trampoline thunk——一个本代码库
/// 只曾直接调用过的私有方法——随后 coreclr fail-fast（"attempted to call a
/// UnmanagedCallersOnly method from managed code"，analysis/run-2026-10-10-1553/scan-thunk-result.txt）。
/// 未在注入类上声明的方法没有注册的 MethodInfo、没有 trampoline、也没有
/// invoker，因此根本无法以那种方式到达。引擎仍然只调用它
/// 自己声明的两个成员：<see cref="CoopGame.SessionRun(Il2CppSystem.Action)"/> 和
/// <see cref="CoopGame.SessionRun()"/>，它们仍留在注入类上。
///
/// 序列本身没有变化，顺序与原生基类协程执行的一致：FinishRaidSetup ->
/// 倒计时界面 -> 原生会话协程（它调用 InitSessionUI，然后 SessionRun，最后
/// 是释放原生 PrepareSession 的运行回调）。唯一的差别是谁来驱动它：
/// 插件中的 Update 泵而不是引擎，引擎从未收到此流程的托管枚举器。
/// </summary>
internal static class SessionPump
{
    /// <summary>泵在发出警告前，等待原生会话协程到达 SessionRun() 的时长。</summary>
    private const float StartWatchdogSeconds = 20f;

    /// <summary>
    /// 原生会话协程 <see cref="CoopGame.SessionRun(Il2CppSystem.Action)"/> 返回的对象，以及
    /// 驱动它的泵例程。两者都只在序列持续期间持有，并在
    /// 序列结束或新序列开始（一次 transit）时清除，因此已完成的 raid 绝不会持有 il2cpp
    /// 枚举器——以及经由它的 profile 和世界——使其继续存活。
    /// </summary>
    private static Il2CppSystem.Collections.IEnumerator _sessionCoroutine;
    private static FikaRoutinePump.RoutineHandle _pumpRoutine;

    /// <summary>
    /// 为一次 raid 启动（或重启）会话序列。取代私有的
    /// <c>CoopGame.StartSessionPump</c>；它过去在游戏实例上持有的状态（例程句柄
    /// 和 "原生协程已到达 SessionRun" 标志）现在作为游戏的字段传入。
    /// </summary>
    internal static void Start(CoopGame game, Il2CppSystem.Action runCallback)
    {
        // 第二次调用只可能意味着第二次 raid 启动（一次 transit）或重入的 prepare：停止上一个
        // 序列并清除 "原生协程已到达 SessionRun" 标志，好让看门狗度量本次运行。
        if (game._sessionRoutine != null)
        {
            FikaRoutinePump.Instance.Stop(game._sessionRoutine, "a new session start sequence replaced it");
            game._sessionRoutine = null;
        }

        // 上一个序列的原生会话协程（如果有）随之消亡：静态字段在此
        // 清除，因此已完成的 raid 无法让 il2cpp 枚举器、其 profile 和其世界继续存活。
        _sessionCoroutine = null;
        _pumpRoutine = null;
        game._sessionRunReached = false;

        
        // 与世界绑定：此序列驱动一次 raid，因此必须在这次 raid 的世界
        // 被销毁的那一刻停止它（analysis/transit-crash-unload，见 FikaRoutinePump.StopWorldBound）。
        game._sessionRoutine = FikaRoutinePump.Instance.StartWorldBound(Sequence(game, runCallback), "SessionRun");
    }

    /// <summary>
    /// 当此序列所属的世界销毁时由 WorldLifecycle 调用
    /// （analysis/transit-crash-unload）。两部分都必须在这里丢弃：
    ///   * 驱动原生会话协程的例程——它的枚举器属于那个
    ///     刚刚消亡的世界，因此泵绝不能再步进它（这就是本方法要解决的崩溃）；
    ///   * 枚举器引用本身，因此已完成的 raid 无法让该 il2cpp 对象——以及经由
    ///     它的 profile 和世界——继续存活（与类注释中针对新 Start 所述的规则相同）。
    /// 当确实丢弃了一个原生枚举器时返回 true，以便拆除流程的那行日志能够说明这一点。
    /// 幂等且不抛异常：它运行在拆除路径内部。
    /// </summary>
    internal static bool OnWorldTornDown(string reason)
    {
        var dropped = false;
        try
        {
            if (_pumpRoutine != null)
            {
                FikaRoutinePump.Instance.Stop(_pumpRoutine, "the world went down (" + reason + ")");
                _pumpRoutine = null;
            }

            if (_sessionCoroutine != null)
            {
                dropped = true;
                _sessionCoroutine = null;
            }
        }
        catch (Exception exception)
        {
            
        }

        return dropped;
    }

    private static IEnumerator Sequence(CoopGame game, Il2CppSystem.Action runCallback)
    {
        
        yield return FikaBoundaries.Run("SessionSequence step 1/3 (FinishRaidSetup)",
            () => game.GameController.FinishRaidSetup(), null);
        

        
        yield return FikaBoundaries.Run("SessionSequence step 2/3 (CountdownScreen)",
            () => game.GameController.CountdownScreen(game.Profile, game.ProfileId), null);
        

        
        FikaBoundaries.Run("SessionSequence step 3/3 (start the native session coroutine on the pump)",
            () => _sessionCoroutine = NativeBaseCalls.Call69(game, runCallback));

        if (_sessionCoroutine == null)
        {
            
            yield break;
        }

        // drivesNativeObject: true - 此例程步进游戏自身的 il2cpp 枚举器
        // （BaseLocalGame.SessionRun），它存在于这次 raid 的世界中。在那个世界
        // 销毁之后再步进它，正是 2026-10-12 崩溃轨迹中的原生访问违例，因此它会被
        // FikaRoutinePump.StopWorldBound 在世界被销毁的那一刻停止，无论它属于哪个 epoch。
        FikaBoundaries.Run("SessionSequence step 3/3 (the pump drives the native session coroutine)",
            () => _pumpRoutine = FikaRoutinePump.Instance.StartWorldBound(NativeRoutines.Adapt(_sessionCoroutine),
                "SessionRunNative", true));

        

        var deadline = UnityEngine.Time.time + StartWatchdogSeconds;
        while (UnityEngine.Time.time < deadline)
        {
            if (game._sessionRunReached)
            {
                
                _sessionCoroutine = null;
                _pumpRoutine = null;
                yield break;
            }

            yield return null;
        }

        
    }
}
