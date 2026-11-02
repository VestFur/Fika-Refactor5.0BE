// © 2026 Lacyway All Rights Reserved

using System;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.UI;
using Fika.Core.Bundles;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.ClientClasses.HandsControllers;
using Fika.Core.Main.Factories;
using Fika.Core.Main.FreeCamera;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.Player;
using Fika.Core.UI.Custom;

namespace Fika.Core.Main.PacketHandlers;

public class ClientPacketSender : MonoBehaviour, IPacketSender
{
    public ClientPacketSender(System.IntPtr pointer) : base(pointer) { }


    public ClientPacketSender() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ClientPacketSender>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public bool SendState { get; set; }
    public IFikaNetworkManager NetworkManager { get; set; }

    private FikaPlayer _player;
    private int _animHash;
    private bool IsMoving
    {
        get
        {
            return _player.MovementContext.PlayerAnimator.Animator.GetBool(_animHash);
        }
    }


    private bool CanPing
    {
        get
        {
            // (analysis/perf-multiplayer) 同样的合取条件，把最便宜、
            // 最少成立的判断放在最前面。ping 键在几乎每一帧上都是 false，
            // 而它后面的每个谓词都是一次 il2cpp 读取（玩家、预加载控制台、游戏状态），
            // 所以把键测试提到最前，每帧最多能从客户端的帧里省掉六次原生读取——对客户端来说，
            // 这就是突袭中每秒都在跑的游戏帧。改变的只是顺序：所有谓词都是对相同值的无副作用读取。
            return FikaPlugin.Instance.Settings.UsePingSystem.Value
                && Input.GetKey(FikaPlugin.Instance.Settings.PingButton.Value.MainKey)
                && _lastPingTime < DateTime.Now.AddSeconds(-3)
                && _player.IsYourPlayer
                && FikaGlobals.AreModifiersPressed(FikaPlugin.Instance.Settings.PingButton.Value)
                && !MonoBehaviourSingleton<PreloaderUI>.Instance.Console.IsConsoleVisible
                && !FikaChatUIScript.IsActive
                && FikaSingleton<IFikaGame>.Instance is CoopGame coopGame && coopGame.Status is GameStatus.Started
                && !_player.IsInventoryOpened;
        }
    }

    private DateTime _lastPingTime;
    private float _updateRate;
    private float _updateCount;
    private float _updatesPerTick;

    public static Task<ClientPacketSender> Create(FikaPlayer player)
    {
        var sender = player.gameObject.AddComponent<ClientPacketSender>();
        sender._player = player;
        sender.NetworkManager = FikaSingleton<FikaClient>.Instance;
        sender.enabled = false;
        sender._lastPingTime = DateTime.Now;
        sender._updateRate = sender.NetworkManager.SendRate;
        sender._updateCount = 0;
        sender._updatesPerTick = 1f / sender._updateRate;
        sender._animHash = PlayerAnimator.INERT_PARAM_HASH;
        return Task.FromResult(sender);
    }

    public void Init()
    {
        enabled = true;
        SendState = true;
        if (_player.FikaQuests is ClientQuestController clientQuestController)
        {
            clientQuestController.LateInit();
        }
    }

    /// <summary>
    /// (analysis/perf-round2) 状态发送器是帧开销探针中的一个槽位：客户端正是在这里，
    /// 在主线程上以 'Send Rate' Hz 的速率序列化并发送自己的玩家状态。
    /// </summary>
    protected void Update()
    {
        
        try
        {
            UpdateMeasured();
        }
        finally
        {
            
        }
    }

    private void UpdateMeasured()
    {
        if (!SendState)
        {
            return;
        }

        _updateCount += Time.unscaledDeltaTime;
        if (_updateCount >= _updatesPerTick)
        {
            SendPlayerState();
            _updateCount -= _updatesPerTick;
        }
    }

    private void SendPlayerState()
    {
        var state = new PlayerStateData(_player, IsMoving);
        NetworkManager.SendPlayerState(ref state);
    }

    protected void LateUpdate()
    {
        
        if (CanPing)
        {
            SendPing();
        }

        
    }

    private void SendPing()
    {
        Transform originTransform;
        Ray sourceRaycast;
        var freeCamController = FikaSingleton<FreeCameraController>.Instance;
        if (freeCamController != null && freeCamController.IsScriptActive)
        {
            originTransform = freeCamController.CameraMain.gameObject.transform;
            sourceRaycast = new(originTransform.position + originTransform.forward / 2f, originTransform.forward);
        }
        else if (_player.HealthController.IsAlive)
        {
            if (_player.HandsController?.TryCast<FikaClientFirearmController>() is { } controller && controller.IsAiming)
            {
                sourceRaycast = new(controller.FireportPosition, controller.WeaponDirection);
            }
            else
            {
                originTransform = _player.CameraPosition;
                sourceRaycast = new(originTransform.position + originTransform.forward / 2f, _player.LookDirection);
            }
        }
        else
        {
            return;
        }

        if (Physics.Raycast(sourceRaycast, out var hit, FikaGlobals.PingRange, FikaGlobals.PingMask))
        {
            _lastPingTime = DateTime.Now;
            //GameObject gameObject = new("Ping", typeof(FikaPing));
            //gameObject.transform.localPosition = hit.point;
            Singleton<GUISounds>.Instance.PlayUISound(PingFactory.GetPingSound());
            var hitGameObject = hit.collider.gameObject;
            var hitLayer = hitGameObject.layer;

            var pingType = PingFactory.EPingType.Point;
            object userData = null;
            string localeId = null;

#if DEBUG
            ConsoleScreen.Log(statement: $"{hit.collider.GetFullPath()}: {LayerMask.LayerToName(hitLayer)}/{hitGameObject.name}");
#endif

            if (LayerMask.LayerToName(hitLayer) == "Player")
            {
                if (hitGameObject.TryGetComponent(out Player player))
                {
                    pingType = PingFactory.EPingType.Player;
                    userData = player;
                }
            }
            else if (LayerMask.LayerToName(hitLayer) == "Deadbody")
            {
                pingType = PingFactory.EPingType.DeadBody;
                userData = hitGameObject;
            }
            else if (hitGameObject.TryGetComponent(out LootableContainer container))
            {
                pingType = PingFactory.EPingType.LootContainer;
                userData = container;
                localeId = container.ItemOwner.Name;
            }
            else if (hitGameObject.TryGetComponent(out LootItem lootItem))
            {
                pingType = PingFactory.EPingType.LootItem;
                userData = lootItem;
                localeId = lootItem.Item.ShortName;
            }
            else if (hitGameObject.TryGetComponent(out Door door))
            {
                pingType = PingFactory.EPingType.Door;
                userData = door;
            }
            else if (hitGameObject.TryGetComponent(out InteractableObject interactable))
            {
                pingType = PingFactory.EPingType.Interactable;
                userData = interactable;
            }

            var basePingPrefab = InternalBundleLoader.Instance.GetFikaAsset(InternalBundleLoader.EFikaAsset.Ping);
            var basePing = GameObject.Instantiate(basePingPrefab);
            var hitPoint = hit.point;
            var abstractPing = PingFactory.FromPingType(pingType, basePing);
            var pingColor = FikaPlugin.Instance.Settings.PingColor.Value;
            pingColor = new(pingColor.r, pingColor.g, pingColor.b, 1);
            // ref 是为了在需要时能修改它，例如：给某个开关打 ping 时，我希望它的位置在 switch.gameObject.position + Vector3.up
            abstractPing.Initialize(ref hitPoint, userData, pingColor);

            NetworkManager.SendGenericPacket(Networking.Packets.Generic.EGenericSubPacketType.Ping,
                PingPacket.FromValue(hitPoint, pingType, pingColor, _player.Profile.GetDisplayName(), localeId), true);

            if (FikaPlugin.Instance.Settings.PlayPingAnimation.Value && _player.HealthController.IsAlive)
            {
                _player.ShowGesture(EInteraction.ThereGesture);
            }
        }
    }

    public void DestroyThis()
    {
        NetworkManager = null;
        Destroy(this);
    }
}
