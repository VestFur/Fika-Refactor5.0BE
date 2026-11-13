using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;

namespace Fika.Core.Main.Components;

public class ItemPositionSyncer : MonoBehaviour
{
    public ItemPositionSyncer(System.IntPtr pointer) : base(pointer) { }


    public ItemPositionSyncer() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ItemPositionSyncer>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private FikaServer _server;
    private FikaClient _client;
    private bool _isServer;
    private ObservedLootItem _lootItem;
    private EFT.LootSyncPacket _data;
    private System.Action<RemoveItemEventArgs> _removeItemHandler;

    private bool PhysicsDone
    {
        get
        {
            return _lootItem.RigidBody == null;
        }
    }

    public static void Create(GameObject gameObject, bool isServer, ObservedLootItem lootItem)
    {
        // AP（analysis/nonfika-raid-inertness）：这个组件被注入 il2cpp，而它的传输层
        // （FikaServer/FikaClient）只存在于我们自己的突袭内。在突袭之外，它过去被添加到一个
        // 原生战利品物品上，随后每一帧都出错；这个守卫是调用方那层保险之外
        // 的第二道保险，这样将来的调用点也无法再引入那个状态。
        if (FikaRaidGate.Skip("ItemPositionSyncer.Create"))
        {
            return;
        }

        var posSync = gameObject.AddComponent<ItemPositionSyncer>();
        posSync._isServer = isServer;
        if (isServer)
        {
            posSync._server = FikaSingleton<FikaServer>.Instance;
        }
        else
        {
            posSync._client = FikaSingleton<FikaClient>.Instance;
        }
        posSync._lootItem = lootItem;
        posSync._data = new()
        {
            Id = lootItem.GetNetId()
        };
    }

    /// <summary>
    /// AP（analysis/nonfika-raid-inertness）：这个组件为之而存在的传输层。在我们自己的突袭
    /// 内，两个单例中总有一个在；在突袭外两个都缺席，而下面每个方法过去
    /// 都只会在此出错，而不是结束自己的生命周期。这个检查是唯一一个把
    /// “我们的组件挂在一个原生战利品物品上”从逐帧异常源变成干净自我销毁的东西。
    /// </summary>
    private bool HasTransport()
    {
        return _isServer ? _server != null : _client != null;
    }

    public void Start()
    {
        if (_lootItem == null)
        {
            FikaGlobals.LogError("ItemPositionSyncer::Start: LootItem was null!");
            Destroy(this);
            return;
        }

        if (!HasTransport())
        {
            FikaGlobals.LogError("ItemPositionSyncer::Start: no transport (" + (_isServer ? "server" : "client")
                + "); the component ends here instead of faulting on every frame");
            Destroy(this);
            return;
        }

        if (_lootItem.RigidBody == null)
        {
            FikaGlobals.LogError("ItemPositionSyncer::Start: Rigidbody was null!");
            Destroy(this);
            return;
        }

        _data.Position = _lootItem.transform.position;
        _data.Rotation = _lootItem.transform.rotation;
        _data.Velocity = _lootItem.RigidBody.velocity;
        _data.AngularVelocity = _lootItem.RigidBody.angularVelocity;
        if (_isServer)
        {
            _server.FikaHostWorld.AddLootSyncStruct(_data);
            return;
        }

        _client.FikaClientWorld.AddLootSyncStruct(_data);
        _removeItemHandler = (System.Action<RemoveItemEventArgs>)ItemOwner_RemoveItemEvent;
        _lootItem.ItemOwner.RemoveItemEvent += _removeItemHandler;
    }

    private void ItemOwner_RemoveItemEvent(RemoveItemEventArgs obj)
    {
        NotifyDone();
    }

    public void FixedUpdate()
    {
        // AP：传输层已经消失的组件（突袭拆除，或者一个比突袭活得更久的战利品物品）
        // 在这里结束自己；旧形态会一直每个物理帧出错，直到世界消失。
        if (!HasTransport())
        {
            Destroy(this);
            return;
        }

        if (PhysicsDone)
        {
            NotifyDone();
            return;
        }

        _data.Position = _lootItem.transform.position;
        _data.Rotation = _lootItem.transform.rotation;
        _data.Velocity = _lootItem.RigidBody.velocity;
        _data.AngularVelocity = _lootItem.RigidBody.angularVelocity;

        if (_isServer)
        {
            _server.FikaHostWorld.AddLootSyncStruct(_data);
            return;
        }

        _client.FikaClientWorld.AddLootSyncStruct(_data);
    }

    private void NotifyDone()
    {
        if (!HasTransport())
        {
            Destroy(this);
            return;
        }

#if DEBUG
        FikaGlobals.LogInfo($"{_lootItem.Item.LocalizedShortName()} rigid body done");
#endif

        _data.Position = _lootItem.transform.position;
        _data.Rotation = _lootItem.transform.rotation;
        _data.Velocity = Vector3.zero;
        _data.AngularVelocity = Vector3.zero;
        _data.Done = true;

        if (_isServer)
        {
            _server.FikaHostWorld.WorldPacket.LootSyncStructs.Add(_data);
            _server.FikaHostWorld.SetCritical();
        }
        else
        {
            _client.FikaClientWorld.WorldPacket.LootSyncStructs.Add(_data);
            _client.FikaClientWorld.SetCritical();
        }

        _lootItem.ItemOwner.RemoveItemEvent -= _removeItemHandler;
        Destroy(this);
    }
}
