using System;
using BepInEx.Logging;
using Comfort.Common;
using EFT.GlobalEvents;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Main.Components;

internal class FikaHalloweenEventManager : MonoBehaviour
{
    public FikaHalloweenEventManager(System.IntPtr pointer) : base(pointer) { }


    public FikaHalloweenEventManager() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaHalloweenEventManager>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private ManualLogSource _logger;

    private Il2CppSystem.Action _summonStartedAction;
    private Il2CppSystem.Action _syncStateEvent;
    private Il2CppSystem.Action _syncExitsEvent;

    private FikaServer _server;

    protected void Awake()
    {
        _logger = BepInEx.Logging.Logger.CreateLogSource("CoopHalloweenEventManager");
    }

    protected void Start()
    {
        _logger.LogInfo("Initializing CoopHalloweenEventManager");

        _server = FikaSingleton<FikaServer>.Instance;

        Action<HalloweenSummonStartedEvent> summonStartedManaged = OnHalloweenSummonStarted;
        Action<HalloweenSyncStateEvent> syncStateManaged = OnHalloweenSyncStateEvent;
        Action<HalloweenSyncExitsEvent> syncExitsManaged = OnHalloweenSyncExitsEvent;
        Il2CppSystem.Action<HalloweenSummonStartedEvent> summonStarted = summonStartedManaged;
        Il2CppSystem.Action<HalloweenSyncStateEvent> syncState = syncStateManaged;
        Il2CppSystem.Action<HalloweenSyncExitsEvent> syncExits = syncExitsManaged;
        _summonStartedAction = GlobalEventsController.Instance.SubscribeOnEvent(summonStarted);
        _syncStateEvent = GlobalEventsController.Instance.SubscribeOnEvent(syncState);
        _syncExitsEvent = GlobalEventsController.Instance.SubscribeOnEvent(syncExits);
    }

    protected void OnDestroy()
    {
        _logger.LogInfo("Destroying CoopHalloweenEventManager");

        _summonStartedAction?.Invoke();
        _syncStateEvent?.Invoke();
        _syncExitsEvent?.Invoke();

        _summonStartedAction = null;
        _syncStateEvent = null;
        _syncExitsEvent = null;
    }

    private void OnHalloweenSummonStarted(HalloweenSummonStartedEvent summonStartedEvent)
    {
#if DEBUG
        _logger.LogWarning("OnHalloweenSummonStarted");
#endif

        HalloweenEventPacket packet = new()
        {
            PacketType = EHalloweenPacketType.Summon,
            SyncEvent = summonStartedEvent
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    private void OnHalloweenSyncStateEvent(HalloweenSyncStateEvent syncStateEvent)
    {
#if DEBUG
        _logger.LogWarning("OnHalloweenSyncStateEvent");
#endif

        HalloweenEventPacket packet = new()
        {
            PacketType = EHalloweenPacketType.Sync,
            SyncEvent = syncStateEvent
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    private void OnHalloweenSyncExitsEvent(HalloweenSyncExitsEvent syncStateEvent)
    {
#if DEBUG
        _logger.LogWarning("OnHalloweenSyncExitsEvent");
#endif

        HalloweenEventPacket packet = new()
        {
            PacketType = EHalloweenPacketType.Exit,
            SyncEvent = syncStateEvent
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }
}
