using System;
using Comfort.Common;
using EFT;
using EFT.GlobalEvents;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Main.Components;

internal class FikaKolotunEventManager : MonoBehaviour
{
    public FikaKolotunEventManager(IntPtr pointer) : base(pointer) { }

    public FikaKolotunEventManager() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaKolotunEventManager>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private Il2CppSystem.Action _unsubscribe;
    private FikaServer _server;

    protected void Start()
    {
        _server = FikaSingleton<FikaServer>.Instance;
        Action<KolotunStateEvent> managed = OnKolotunStateChanged;
        Il2CppSystem.Action<KolotunStateEvent> native = managed;
        _unsubscribe = GlobalEventsController.Instance.SubscribeOnEvent(native);

        // The native controller can publish its initial state during construction,
        // before this component subscribes. Give already-connected peers a snapshot.
        var controller = Singleton<GameWorld>.Instance?.KolotunController;
        if (controller != null)
        {
            var packet = new KolotunStatePacket
            {
                IsActive = controller.IsActive,
                EventStage = controller.EventStage
            };
            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }
    }

    protected void OnDestroy()
    {
        _unsubscribe?.Invoke();
        _unsubscribe = null;
    }

    private void OnKolotunStateChanged(KolotunStateEvent state)
    {
        if (state == null || _server == null) return;
        var packet = new KolotunStatePacket
        {
            IsActive = state.IsActive,
            EventStage = state.EventStage
        };
        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }
}
