namespace Fika.Core.Main.Utils;

/// <summary>保留原生委托的相等性，包括重复订阅的情况。</summary>
internal sealed class NativeActionEvent
{
    private System.Action _handlers;
    private Il2CppSystem.Action _nativeHandlers;

    public void Add(System.Action handler) =>
        _handlers = System.Delegate.Combine(_handlers, handler) as System.Action;

    public void Remove(System.Action handler) =>
        _handlers = System.Delegate.Remove(_handlers, handler) as System.Action;

    // 游戏事件以 Il2CppSystem.Action 触发。原生委托没有
    // 托管对应物，所以它被原样保留，并通过互操作层调用。
    public void Add(Il2CppSystem.Action handler) =>
        _nativeHandlers = _nativeHandlers == null ? handler : _nativeHandlers + handler;

    public void Remove(Il2CppSystem.Action handler) =>
        _nativeHandlers = _nativeHandlers == null ? null : _nativeHandlers - handler;

    public void Invoke()
    {
        _handlers?.Invoke();
        InvokeNative();
    }

    /// <summary>
    /// (analysis/session-log-archive) 事件的原生那一半，与调用方隔离开来。
    /// <para>
    /// 原生订阅者会在这里、在自己的栈帧里抛出异常——实测到的是
    /// <c>BotGroupHostility.UpdateHostilityStatusForAllPlayers</c> ->
    /// <c>OwnedGroupCalcActionNextFrame</c>（NullReference），发生在主机的 <c>UpdateByUnity</c> 事件上，
    /// 观测到的时候突袭世界仍在构建中。没有这个 catch，异常会从泵
    /// （<c>BotStateManager.Update</c> -> <c>HostGameController.InvokeUpdate</c>）逃逸进
    /// <c>NativeManagedInvoker.Shim</c>，后者把它记录成 shim 失败——于是该帧其余的工作
    /// （bot 状态批次）也随之被跳过。
    /// </para>
    /// <para>
    /// 引擎自身的循环正是这样行事的：抛异常的 MonoBehaviour Update 会被记录，
    /// 下一帧照常运行。排在失败订阅者之后的订阅者在该帧仍会被跳过，
    /// 这也正是多播委托在原生侧的行为；新增的是失败被指名，
    /// 而不是被归咎于泵，并且泵保住了它剩余的工作。
    /// </para>
    /// </summary>
    private void InvokeNative()
    {
        var handlers = _nativeHandlers;
        if (handlers == null)
        {
            return;
        }

        try
        {
            handlers.Invoke();
        }
        catch (System.Exception exception)
        {
            NativeHandlerFault.Report(exception);
        }
    }

    public void Clear()
    {
        _handlers = null;
        _nativeHandlers = null;
    }
}
