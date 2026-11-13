namespace Fika.Core.Main.Utils;

/// <summary>Preserves native delegate equality, including duplicate subscriptions.</summary>
internal sealed class NativeActionEvent
{
    private Il2CppSystem.Action _handlers;

    public void Add(Il2CppSystem.Action handler) =>
        _handlers = Il2CppSystem.Delegate.Combine(_handlers, handler)?.Cast<Il2CppSystem.Action>();

    public void Remove(Il2CppSystem.Action handler) =>
        _handlers = Il2CppSystem.Delegate.Remove(_handlers, handler)?.Cast<Il2CppSystem.Action>();

    public void Invoke() => _handlers?.Invoke();
    public void Clear() => _handlers = null;
}
