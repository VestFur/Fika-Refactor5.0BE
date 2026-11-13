using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using EFT.Quests;

namespace Fika.Core.Main.BaseClasses;

/// <summary>
/// Owns the game's sealed local quest controller. Native callers keep the actual
/// controller, including its local-game connectors, auto-start quests and rewards.
/// Fika state is resolved by native pointer rather than managed wrapper identity.
/// </summary>
public abstract class FikaQuestController : IDisposable
{
    private static readonly Dictionary<IntPtr, FikaQuestController> Controllers = new();
    private bool _disposed;
    protected bool IsDisposed => _disposed;

    protected FikaQuestController(Profile profile, InventoryController inventoryController,
        IPlayerSearchController searchController, IQuestSession session)
    {
        Native = new QuestControllerClientLocalGame(profile, inventoryController, searchController, session);
        Controllers.Add(Native.Pointer, this);
    }

    public QuestControllerClientLocalGame Native { get; }
    public Profile Profile => Native.Profile;
    public InventoryController InventoryController => Native.InventoryController;
    public ConditionalBook<Quest> ConditionalBook => Native.ConditionalBook;
    public QuestBook Quests => Native.Quests;

    public static T Find<T>(ConditionalController<Quest> native) where T : FikaQuestController =>
        native != null && Controllers.TryGetValue(native.Pointer, out var controller) ? controller as T : null;

    public virtual void Init() => Native.Init();
    public virtual void Run() => Native.Run();

    public void Dispose()
    {
        if (_disposed) return;
        DisposeNative();
        OnNativeDisposed();
    }

    protected virtual void DisposeNative() => Native.Dispose();

    internal void OnNativeDisposed()
    {
        if (_disposed) return;
        _disposed = true;
        Controllers.Remove(Native.Pointer);
        DisposeManaged();
    }

    protected virtual void DisposeManaged() { }
}
