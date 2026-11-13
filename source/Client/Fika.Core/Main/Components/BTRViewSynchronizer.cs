using Comfort.Common;
using EFT.Vehicle;
using Fika.Core.Main.Custom;
using Fika.Core.Networking;

namespace Fika.Core.Main.Components;

/// <summary>
/// 把 <see cref="BTRViewSynchronizer"/> 添加到 <see cref="BTRView"/> 的 GameObject 上，以便同步位置
/// </summary>
internal class BTRViewSynchronizer : ThrottledMono
{
    public BTRViewSynchronizer(System.IntPtr pointer) : base(pointer) { }


    public BTRViewSynchronizer() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<BTRViewSynchronizer>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private FikaServer _server;
    private BTRView _view;

    public override float UpdateRate
    {
        get
        {
            return 20f;
        }
    }

    public static void CreateInstance(BTRView btrView)
    {
        var syncComp = btrView.gameObject.GetComponent<BTRViewSynchronizer>();
        if (syncComp == null) syncComp = btrView.gameObject.AddComponent<BTRViewSynchronizer>();
        syncComp._server = FikaSingleton<FikaServer>.Instance;
        syncComp._view = btrView;
    }

    public override void Tick()
    {
        if (_server != null)
        {
            var controller = BtrController.Instance;
            if (controller?.BtrView == null || _view == null || controller.BtrView.Pointer != _view.Pointer) return;
            var packet = controller._offlineSyncPacket;
            _server.SendBTRPacket(ref packet);
            controller._offlineSyncPacket = packet;
        }
    }
}
