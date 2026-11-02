using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using EFT.Quests;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.BaseClasses;

/// <summary>
/// 持有游戏密封的本地任务控制器。原生调用方保留实际的
/// 控制器，包括它的本地游戏连接器、自动开始的任务和奖励。
/// Fika 状态按原生指针解析，而不是按托管包装器身份解析。
/// </summary>
public abstract class FikaQuestController : IDisposable
{
    private static readonly Dictionary<IntPtr, Entry> Controllers = new();
    private bool _disposed;
    protected bool IsDisposed => _disposed;

    /// <summary>
    /// 一个已注册的控制器，外加它所属的世界世代（analysis/transit-crash-unload）。
    /// 一个任务控制器随其本地游戏每局突袭创建一次；没有世代号时，仅以
    /// 原生指针为键的查找就可能把一个已释放突袭的控制器交给下一局的原生对象。
    /// </summary>
    private readonly struct Entry(FikaQuestController controller, int epoch)
    {
        internal readonly FikaQuestController Controller = controller;
        internal readonly int Epoch = epoch;
    }

    private static int _staleReports;

    protected FikaQuestController(Profile profile, InventoryController inventoryController,
        IPlayerSearchController searchController, IQuestSession session)
    {
        Native = new QuestControllerClientLocalGame(profile, inventoryController, searchController, session);
        Controllers[Native.Pointer] = new Entry(this, WorldLifecycle.Epoch);
    }

    public QuestControllerClientLocalGame Native { get; }
    public Profile Profile => Native.Profile;
    public InventoryController InventoryController => Native.InventoryController;
    public ConditionalBook<Quest> ConditionalBook => Native.ConditionalBook;
    public QuestBook Quests => Native.Quests;

    public static T Find<T>(ConditionalController<Quest> native) where T : FikaQuestController
    {
        if (native == null || !Controllers.TryGetValue(native.Pointer, out var entry))
        {
            return null;
        }

        if (!WorldLifecycle.IsCurrent(entry.Epoch))
        {
            Controllers.Remove(native.Pointer);
            if (_staleReports < 8)
            {
                _staleReports++;
                FikaGlobals.LogWarning("[WorldLifecycle] a " + typeof(T).Name + " of world epoch " + entry.Epoch
                    + " was looked up while epoch " + WorldLifecycle.Epoch + " is current; the entry was dropped");
            }

            return null;
        }

        return entry.Controller as T;
    }

    /// <summary>
    /// 在世界拆除时丢弃所有控制器（analysis/transit-crash-unload）。与
    /// <see cref="FikaWorldControllers.ClearForWorldTeardown"/> 一样，这里有意不调用 Dispose：
    /// 那个世界的原生任务控制器已经消失，而 Dispose 会进入它。
    /// </summary>
    internal static int ClearForWorldTeardown()
    {
        var count = Controllers.Count;
        if (count > 0)
        {
            Controllers.Clear();
        }

        return count;
    }

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
