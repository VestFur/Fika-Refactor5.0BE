using EFT.Ballistics;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Main.HostClasses;

/// <summary>
/// <see cref="World"/>，由主机用于同步游戏逻辑
/// </summary>
public class FikaHostWorld : World
{
    public FikaHostWorld(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<World>(this);
    }


    public FikaHostWorld() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaHostWorld>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public List<EFT.LootSyncPacket> LootSyncPackets;
    public WorldPacket WorldPacket;

    private FikaServer _server;
    private GameWorld _gameWorld;
    private List<GrenadeSyncPacket> _grenadeData;
    private readonly HashSet<int> _terminalGrenadeIds = new();
    private bool _hasCriticalData;

    /// <summary>
    /// 将原生手雷销毁事件引发的终态入队。
    /// SPT 5 还从 <c>UnregisterGrenade</c> 暴露了一个关键状态队列；
    /// 当该队列已经包含同一颗手雷时，跳过这条重复项。
    /// </summary>
    public bool TryQueueGrenadeDestroy(Throwable grenade)
    {
        if (grenade == null || !grenade.HasNetData || WorldPacket?.GrenadePackets == null)
            return false;

        var packet = grenade.GetNetPacket();
        if (!_terminalGrenadeIds.Add(packet.Id)) return false;

        WorldPacket.GrenadePackets.Add(packet);
        SetCritical();
        return true;
    }

    public static FikaHostWorld Create(FikaHostGameWorld gameWorld)
    {
        var hostWorld = gameWorld.gameObject.AddComponent<FikaHostWorld>();
        hostWorld._server = FikaSingleton<FikaServer>.Instance;
        hostWorld._server.FikaHostWorld = hostWorld;
        hostWorld._gameWorld = gameWorld;
        hostWorld.LootSyncPackets = new List<EFT.LootSyncPacket>(8);
        hostWorld.WorldPacket = new()
        {
            ArtilleryPackets = new(8),
            SyncObjectPackets = new(8),
            GrenadePackets = new(8),
            LootSyncStructs = new(8),
            RagdollPackets = new(8)
        };
        hostWorld._grenadeData = new(16);
        // (analysis/transit-crash-unload) 从这里开始存在一个全新的世界：WorldLifecycle 的拆除窗口
        // 结束，本次切换的 [LoadCost] 窗口以其汇总行关闭。
        Fika.Core.Main.Utils.WorldLifecycle.MarkWorldUp("FikaHostWorld.Create");
        // 直接绑定已注册的原生方法。DelegateSupport 无法
        // 转换携带装箱 DamageInfo 值类型的托管 Action。
        // DamageInfo 按值传递，而这正是该 interop 拒绝封送到
        // CLR 委托的形式。窗口命中同步是一项增强功能，因此绑定被拒绝只会禁用
        // 那一个封包，而不会顺带把主机 raid 弄崩。
        if (NativeDelegates.TryBindManaged<System.Action<WindowBreaker, DamageInfo, WindowBreakingConfig.Crack, float>>(
                hostWorld, nameof(WindowBreaker_OnWindowHitAction), "FikaHostWorld.WindowHit", out var windowHit))
        {
            WindowBreaker.OnWindowHitAction += windowHit;
            hostWorld._windowHitHandler = windowHit;
        }
        else
        {
            hostWorld._windowHitHandler = null;
        }
        // (analysis/cutscene-world-factory，已被 analysis/cutscene-native-registration 取代) 两个
        // 过场控制器过去在这里创建，位于本移植对原生
        // GameWorld.Create<T> 方法体的替换实现内部。该方法体从未创建它们（4.0 EFT/GameWorld.cs:1111-1123）——原生
        // 位置是 raid 启动协程，本移植用 CoopGame.InitPlayer 替换它，而现在
        // 它们在各自的每帧钩子上被创建并注册。此调用仅是回滚路径：
        return hostWorld;
    }

    public override void OnDestroy()
    {
        if (_windowHitHandler != null)
            WindowBreaker.OnWindowHitAction -= _windowHitHandler;
        if (ReferenceEquals(_server?.FikaHostWorld, this)) _server.FikaHostWorld = null;
        // (analysis/transit-crash-unload) 本组件所属的世界即将销毁：丢弃它
        // 仍为该世界持有的每个列表和封包，这样被替换的 raid 中的任何东西都无法通过残留的
        // FikaServer 到达下一个世界。_terminalGrenadeIds 中的网络 ID 是 raid 作用域的。
        LootSyncPackets?.Clear();
        _grenadeData?.Clear();
        _terminalGrenadeIds.Clear();
        WorldPacket?.Flush();
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call75(this);
    }

    private System.Action<WindowBreaker, DamageInfo, WindowBreakingConfig.Crack, float> _windowHitHandler;
    private System.Action<IObserverToPlayerBridge, BorderZone, float, bool> _borderZoneShotHandler;

    private void WindowBreaker_OnWindowHitAction(WindowBreaker windowBreaker, DamageInfo damageInfo, WindowBreakingConfig.Crack crack, float angle)
    {
        _server.SendGenericPacket(EGenericSubPacketType.SyncableItem,
            SyncableItemPacket.FromValue(windowBreaker.NetId, damageInfo.HitPoint), true);
    }

    protected void Update()
    {
        UpdateLootItems(_gameWorld.LootItems);
    }

    public void AddLootSyncStruct(EFT.LootSyncPacket syncStruct)
    {
        if (WorldPacket.LootSyncStructs.Count >= 8)
        {
            WorldPacket.RemoteTime = Time.time;
            _server.SendReusableToAll(WorldPacket,
                _hasCriticalData ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);

            _hasCriticalData = false;
        }

        WorldPacket.LootSyncStructs.Add(syncStruct);
    }

    /// <summary>
    /// 将当前的 <see cref="WorldPacket"/> 标记为关键
    /// </summary>
    internal void SetCritical()
    {
        _hasCriticalData = true;
    }

    protected void FixedUpdate()
    {
        var grenadesCount = _gameWorld.Grenades.Count;
        for (var i = 0; i < grenadesCount; i++)
        {
            var throwable = _gameWorld.Grenades.GetByIndex(i);
            if (throwable.HasNetData)
            {
                _grenadeData.Add(throwable.GetNetPacket());
            }
        }

        if (_gameWorld.GrenadesCriticalStates.Count > 0)
        {
            // SPT 5 的 UnregisterGrenade 会入队最终状态、移除
            // 投掷物并取消订阅其销毁事件。消费该原生
            // 队列，而不是压制 GameWorld.GrenadeDestroyEvent。
            foreach (var packet in _gameWorld.GrenadesCriticalStates)
            {
                if (_terminalGrenadeIds.Add(packet.Id))
                    _grenadeData.Add(packet);
            }
            _gameWorld.GrenadesCriticalStates.Clear();
            SetCritical();
        }

        WorldPacket.GrenadePackets.AddRange(_grenadeData);
        foreach (var packet in _gameWorld.ArtilleryProjectilesStates) WorldPacket.ArtilleryPackets.Add(packet);

        if (WorldPacket.HasData)
        {
            WorldPacket.RemoteTime = Time.time;
            _server.SendReusableToAll(WorldPacket,
                _hasCriticalData ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);

            _hasCriticalData = false;
        }

        _grenadeData.Clear();
        // 网络 ID 以 raid 为作用域。本组件会在
        // raid 结束时销毁，因此终态 ID 集合不会泄漏到下一个世界。
        _gameWorld.ArtilleryProjectilesStates.Clear();
    }

    public void UpdateLootItems(DictionaryListHydra<int, LootItem> lootItems)
    {
        for (var i = LootSyncPackets.Count - 1; i >= 0; i--)
        {
            var gstruct = LootSyncPackets[i];
            // DictionaryListHydra 是一个 IL2CPP 原生集合。即使原生
            // 对象是被观察的子类，值也可能通过 LootItem 包装器
            // 暴露出来，因此 CLR 模式匹配可能
            // 静默跳过位置更新。在应用该封包之前
            // 要显式解析原生身份。
            if (lootItems.TryGetByKey(gstruct.Id, out var lootItem)
                && lootItem?.TryCast<ObservedLootItem>() is { } observedLootItem)
            {
                observedLootItem.ApplyNetPacket(gstruct);
            }
        }
        LootSyncPackets.Clear();
    }

    /// <summary>
    /// 在地图上设置所有的 <see cref="BorderZone"/>
    /// </summary>
    public override void SubscribeToBorderZones(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BorderZone> zones)
    {
        foreach (var borderZone in zones)
        {
            _borderZoneShotHandler ??= (System.Action<IObserverToPlayerBridge, BorderZone, float, bool>)OnBorderZoneShot;
            borderZone.PlayerShotEvent += _borderZoneShotHandler;
        }
    }

    /// <summary>
    /// 当某个 <see cref="BorderZone"/> 触发时触发（仅在主机上运行）
    /// </summary>
    /// <param name="player"></param>
    /// <param name="zone"></param>
    /// <param name="arg3"></param>
    /// <param name="arg4"></param>
    private void OnBorderZoneShot(IObserverToPlayerBridge player, BorderZone zone, float arg3, bool arg4)
    {
        _server.SendGenericPacket(EGenericSubPacketType.BorderZone,
            BorderZoneEvent.FromValue(player.iPlayer.ProfileId, zone.Id), true);
    }
}
