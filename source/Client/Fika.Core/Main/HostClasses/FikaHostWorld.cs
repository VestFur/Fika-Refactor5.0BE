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
/// <see cref="World"/> used for the host to synchronize game logic
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
    /// Queues the terminal state raised by the native grenade destroy event.
    /// SPT 5 also exposes a critical-state queue from <c>UnregisterGrenade</c>;
    /// when that queue already contains the same grenade, skip the duplicate.
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
        // (analysis/transit-crash-unload) A fresh world exists from here on: the teardown window of
        // WorldLifecycle ends and the [LoadCost] window of the swap is closed with its summary line.
        Fika.Core.Main.Utils.WorldLifecycle.MarkWorldUp("FikaHostWorld.Create");
        // Bind the registered native method directly. DelegateSupport cannot
        // convert a managed Action carrying the boxed DamageInfo value type.
        // DamageInfo travels by value, which is the shape this interop refuses to marshal into a
        // CLR delegate. Window-hit synchronisation is an enhancement, so a refused bind disables
        // that one packet instead of taking the host raid down with it.
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
        // (analysis/cutscene-world-factory, superseded by analysis/cutscene-native-registration) The two
        // cutscene controllers used to be created here, inside the port's replacement of the native
        // GameWorld.Create<T> body. That body never created them (4.0 EFT/GameWorld.cs:1111-1123) - the native
        // position is the raid-start coroutine, which this port replaces with CoopGame.InitPlayer, and there
        // they are now created AND registered on their per-frame hook. This call is the rollback path only:
        // CutsceneBoundary.CreateControllerIfMissing == false switches it off, one const switches it back on.
        global::Fika.Core.Main.Components.CutsceneBoundary.EnsureControllers(gameWorld, "FikaHostWorld.Create");
        return hostWorld;
    }

    public override void OnDestroy()
    {
        if (_windowHitHandler != null)
            WindowBreaker.OnWindowHitAction -= _windowHitHandler;
        if (ReferenceEquals(_server?.FikaHostWorld, this)) _server.FikaHostWorld = null;
        // (analysis/transit-crash-unload) The world this component belongs to is going away: drop every
        // list and packet it still held for it, so nothing of the replaced raid can reach the next world
        // through the surviving FikaServer. The network ids in _terminalGrenadeIds are raid scoped.
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
    /// Marks the current <see cref="WorldPacket"/> as critical
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
            // SPT 5 UnregisterGrenade queues the final state, removes the
            // throwable and unsubscribes its destroy event. Consume that native
            // queue instead of suppressing GameWorld.GrenadeDestroyEvent.
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
        // Network ids are scoped to a raid.  This component is destroyed on
        // raid end, so the terminal-id set cannot leak into the next world.
        _gameWorld.ArtilleryProjectilesStates.Clear();
    }

    public void UpdateLootItems(DictionaryListHydra<int, LootItem> lootItems)
    {
        for (var i = LootSyncPackets.Count - 1; i >= 0; i--)
        {
            var gstruct = LootSyncPackets[i];
            // DictionaryListHydra is an IL2CPP native collection. Values can
            // be surfaced through the LootItem wrapper even when the native
            // object is the observed subclass, so CLR pattern matching can
            // silently skip position updates. Resolve the native identity
            // explicitly before applying the packet.
            if (lootItems.TryGetByKey(gstruct.Id, out var lootItem)
                && lootItem?.TryCast<ObservedLootItem>() is { } observedLootItem)
            {
                observedLootItem.ApplyNetPacket(gstruct);
            }
        }
        LootSyncPackets.Clear();
    }

    /// <summary>
    /// Sets up all the <see cref="BorderZone"/>s on the map
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
    /// Triggered when a <see cref="BorderZone"/> triggers (only runs on host)
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
