namespace Fika.Core.Main.Custom;

/// <summary>
/// 一个抽象 MonoBehaviour，它限制 <see cref="Tick"/> 的调用频率，
/// 频率由指定的 <see cref="UpdateRate"/> 决定。
/// </summary>
public abstract class ThrottledMono : MonoBehaviour
{
    public ThrottledMono(System.IntPtr pointer) : base(pointer) { }


    /// <summary>
    /// 获取每秒的 tick 次数。<br/>
    /// 例如，值 10 表示 <see cref="Tick"/> 每秒被调用 10 次。
    /// </summary>
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    public abstract float UpdateRate { get; }

    private float _secondsPerTick;
    private float _tickTimer;

    /// <summary>
    /// 在脚本实例被加载时调用。
    /// 基于 <see cref="UpdateRate"/> 初始化 tick 间隔。<br/>
    /// 若你重写此方法，请务必调用基类实现，否则 <see cref="ThrottledMono"/> 将无法正常工作。
    /// </summary>
    /// <remarks>
    /// <see cref="_secondsPerTick"/> 在 <see cref="Awake"/> 期间赋值。
    /// </remarks>
    protected virtual void Awake()
    {
        _secondsPerTick = 1f / UpdateRate;
    }

    /// <summary>
    /// 每一帧都会被调用的 Unity 回调。
    /// 累加未缩放增量时间，并在间隔到点时调用 <see cref="Tick"/>。
    /// </summary>
    private void Update()
    {
        _tickTimer += Time.unscaledDeltaTime;
        if (_tickTimer >= _secondsPerTick)
        {
            Tick();
            _tickTimer -= _secondsPerTick; // monos in Tarkov don't last long enough for accumulation drift to occur
        }
    }

    /// <summary>
    /// 在 <see cref="UpdateRate"/> 所定义的限制间隔上调用。
    /// 实现此方法以定义自定义的 tick 行为。
    /// </summary>
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    public abstract void Tick();
}
