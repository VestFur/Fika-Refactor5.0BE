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
        _unsubscribe = GlobalEventsController.Instance.SubscribeOnEvent<KolotunStateEvent>(native);

        // 原生控制器可能在构造期间就发布其初始状态，
        // 早于本组件完成订阅。因此要给已连接的对端发一份快照。
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
