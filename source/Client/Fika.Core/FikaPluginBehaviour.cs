using System;
using Comfort.Common;
using Fika.Core.Main.Utils;
using Fika.Core.Main.Components;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.HostClasses;

namespace Fika.Core;

/// <summary>BepInEx IL2CPP 插件的 Unity 生命周期入口点。</summary>
public sealed class FikaPluginBehaviour : MonoBehaviour
{
    public FikaPluginBehaviour(IntPtr pointer) : base(pointer) { }

    private void Awake() => MainThreadDispatcher.Initialize();

    private void Start() => FikaPlugin.Instance.StartDeferredChecks();

    private void Update()
    {
        // 本方法是一个 native -> managed 边界：il2cpp 每帧调用它一次。任何从
        // 这里逃逸出去的东西，都是一次其上方没有托管帧的异常，而这正是运行时
        // 会转成崩溃而不是日志行的形态（analysis/crash-clr）。因此每一段
        // 都被单独防护，这样一个坏掉的段就不会饿死其他段 —— 尤其是
        // routine 泵，它是最后一条语句，并驱动整个 raid 启动序列。

        FikaPlugin.Instance.UpdateInitialization();

        RepairLocalStateInUpdate();

        // 主机端库存操作的 native 完成若始终不到达，会让 EFT.PickUpState 一直保持在进入状态；
        // 这里在其超时后让它失败，从而完成拾取回调。
        FikaBoundaries.Run(BoundaryUpdate + "/HostOperationWatchdog.Tick", static () => HostOperationWatchdog.Tick());

        // AP (analysis/nonfika-raid-inertness)：raid 类型在此被重新核实 —— 最多每 500 ms 一次
        // 单例读取 —— 这样一个为我们自己的 raid 布置的门就不可能存活进一场原生 raid，
        // 即使 raid 开始或 raid 结束的补丁从未运行过。
        FikaRaidGate.Verify();

        // 夜视 / 热成像自愈：一旦游戏禁用了这些相机效果，它们无法自行重新启用
        // （analysis/night-vision/REPORT.md）。除非相机效果被留在禁用状态，
        // 或已漂离所装备头戴设备的设置，否则这里是只读的。
        // AP：自愈会写入原生相机状态，因此在 Fika raid 之外本移植不会
        // 碰它。关闭 "Coop / Inert Outside Fika Raids" 会为所有调用方恢复旧行为。
        if (!FikaRaidGate.Skip("NvgDiagnostics.Tick"))
        {
            NvgDiagnostics.Tick();
        }

        DrainMainThreadQueue();

        // 同一个 tick 还驱动 Fika 的托管 routine（会话启动、倒计时路径、raid 视图
        // 恢复）：它们过去是引擎协程，而引擎在释放正是那种形态的托管
        // 枚举器包装器时崩溃了（analysis/host-raid-coroutine）。
        FikaBoundaries.Run(BoundaryUpdate + "/FikaRoutinePump.Tick", static () => FikaRoutinePump.Instance.Tick());
    }

    /// <summary>这个边界的 journal 标签；用常量，使 journal 从不为它分配。</summary>
    internal const string BoundaryUpdate = "FikaPluginBehaviour.Update (il2cpp -> managed)";

    private static void RepairLocalStateInUpdate()
    {
        // (analysis/transit-crash-unload) 在一个世界被拆解与其后继被创建之间，
        // 游戏单例仍指向刚刚结束的那场 raid，而它的世界、它的本地玩家与
        // 它的 hands 都已释放。下面每一处修复都恰好触碰这些对象，因此这个窗口
        // 被整体跳过；它很短（就是地图加载），而且其中任何东西本来也无法修复。
        // 该条件的后半段是安全网：一旦引擎重新报告出一个世界，
        // 修复就会恢复，即使某次拆解曾被记录得太晚 —— 一次被跳过的修复不可能超出它的
        // 窗口，因此这个防护永远不会停掉一场活跃 raid 的 hands/交互/库存修复。
        if (WorldLifecycle.WorldDown && !LoadSync.WorldReady)
        {
            return;
        }

        if (FikaSingleton<IFikaGame>.Instantiated && FikaSingleton<IFikaGame>.Instance is CoopGame coopGame)
        {
            coopGame.RepairLocalInteractionState();
            coopGame.RepairLocalInventoryState();
            coopGame.RepairLocalHands();
        }
    }

    private static void DrainMainThreadQueue()
    {
        MainThreadDispatcher.Drain(exception =>
            FikaPlugin.Instance.FikaLogger.LogError($"Fika main-thread callback failed: {exception}"));
    }

    private void OnApplicationQuit()
    {
    }

    private void OnDestroy()
    {
        FikaRoutinePump.Instance.StopAll("the plugin behaviour is being destroyed");
        FikaPlugin.Instance?.NotificationManager?.Close();
        FikaPlugin.HeadlessRequesterWebSocket?.Close();
    }
}
