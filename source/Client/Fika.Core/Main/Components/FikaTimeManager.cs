using Comfort.Common;
using EFT;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.Components;

public class FikaTimeManager : MonoBehaviour
{
    public FikaTimeManager(System.IntPtr pointer) : base(pointer) { }


    public FikaTimeManager() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaTimeManager>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public CoopGame CoopGame;
    public GameTimer GameTimer;

    public static FikaTimeManager Create(CoopGame game)
    {
        var timeManager = game.gameObject.AddComponent<FikaTimeManager>();
        timeManager.CoopGame = game;
        timeManager.GameTimer = game.GameTimer;
        return timeManager;
    }

    /// <summary>
    /// (analysis/perf-round2) 会话时间到期检查是帧开销探针的一个槽位：它每一帧
    /// 都按名称读取一个原生 Nullable 字段，所以其真实开销值得实测而不是假设。
    /// </summary>
    protected void Update()
    {
        
        try
        {
            UpdateMeasured();
        }
        finally
        {
            
        }
    }

    /// <summary>
    /// (analysis/perf-round2) 读取原生会话时钟的频率：每秒四次，而不是
    /// 每帧一次。参见 <see cref="UpdateMeasured"/> 中的说明。
    /// </summary>
    private const float SessionCheckInterval = 0.25f;

    private float _nextSessionCheck;

    private void UpdateMeasured()
    {
        if (CoopGame.Status != GameStatus.Started || GameTimer == null)
            return;
        if (FikaFinalMissionDirector.DeferStop) return;

        // 下面这次原生读取是一次按名称的字段查找外加一个新分配的 Nullable 包装器，而
        // 它唯一决定的事情就是突袭的会话时钟（默认 50 分钟）是否已经走完。这个
        // 答案每局突袭只变化一次，所以每秒采样四次而不是每帧一次，可以省下
        // 每 60 次包装器分配中的 59 次，以及同等比例的 il2cpp 字段读取，而结果
        // 不变：它触发的 MIA 撤离是一个持续数秒的流程，不可能
        // 因为提前 250 ms 而被察觉。用 realtimeSinceStartup 而不是 Time.time，这样暂停或时间尺度为零
        // 都无法把这项检查挂起（Time.time 在两种情况下都会冻结；这里绝不能冻结）。
        var now = UnityEngine.Time.realtimeSinceStartup;
        if (now < _nextSessionCheck)
            return;

        _nextSessionCheck = now + SessionCheckInterval;

        var sessionTime = NativeNullable<Il2CppSystem.TimeSpan>.Read(GameTimer, "<SessionTime>k__BackingField");
        if (sessionTime.HasValue && GameTimer.PastTime >= sessionTime.Value)
        {
            CoopGame.ExitStatus = ExitStatus.MissingInAction;
            if (Singleton<GameWorld>.Instance?.MainPlayer?.TryCast<FikaPlayer>() is { } fikaPlayer)
                CoopGame.Extract(fikaPlayer, null);
            enabled = false;
        }
    }
}
