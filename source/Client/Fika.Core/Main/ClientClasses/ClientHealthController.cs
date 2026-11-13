// © 2026 Lacyway All Rights Reserved

using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Screens;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.Player.Common;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;

namespace Fika.Core.Main.ClientClasses;

public sealed class ClientHealthController : PlayerHealthController
{
    private readonly Profile.HealthInfo healthInfo;
    private readonly Player player;
    private readonly InventoryController inventoryController;
    private readonly SkillManager skillManager;
    private readonly bool aiHealth;

    public ClientHealthController(Profile.HealthInfo healthInfo, Player player, InventoryController inventoryController, SkillManager skillManager, bool aiHealth) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ClientHealthController>())
    {
        this.healthInfo = healthInfo;
        this.player = player;
        this.inventoryController = inventoryController;
        this.skillManager = skillManager;
        this.aiHealth = aiHealth;
        _fikaPlayer = player.TryCast<FikaPlayer>()
            ?? throw new System.InvalidOperationException("Client health controller received a non-Fika player");
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Fika.Core.Main.Utils.NativeConstructor.InvokeBase<PlayerHealthController>(this, healthInfo, player, inventoryController, skillManager, aiHealth);
    }


    public ClientHealthController(System.IntPtr pointer) : base(pointer) { }


    public bool ReviveEnabled { get; } = FikaPlugin.Instance.Settings.ReviveConfig.Enabled;
    public bool Downed { get; internal set; }
    public bool CanBeDowned
    {
        get
        {
            return !_bledOut && (_maxRevives == 0 || _revives < _maxRevives) && CanBeRevivedByOtherPlayer();
        }
    }

    public float BleedoutTime { get; } = FikaPlugin.Instance.Settings.ReviveConfig.BleedoutTime;
    public bool ShouldBleedOut => BleedoutTime > 0f;

    private readonly int _maxRevives = FikaPlugin.Instance.Settings.ReviveConfig.MaxRevives;
    private readonly bool _headshotKills = FikaPlugin.Instance.Settings.ReviveConfig.HeadshotKills;
    private readonly bool _grenadesKills = FikaPlugin.Instance.Settings.ReviveConfig.GrenadesKills;
    private int _revives;
    private bool _bledOut;

    private readonly FikaPlayer _fikaPlayer ;

    public override bool _sendNetworkSyncPackets
    {
        get
        {
            return true;
        }
    }

    public void EnableMetabolism()
    {
        MetabolismDisabled = false;
    }

    internal void RestoreRevivalHistory(RevivalState state)
    {
        _revives = state.Revives;
        _bledOut = state.BledOut || !state.IsAlive;
    }

    /// <summary>
    /// 检查是否还有其他能施救的存活玩家
    /// </summary>
    /// <returns>若有人能施救则为 <see langword="true"/>；否则为 <see langword="false"/></returns>
    private bool CanBeRevivedByOtherPlayer()
    {
        if (CoopHandler.TryGetCoopHandler(out var coopHandler))
        {
            if (coopHandler.AmountOfHumans <= 1)
            {
                return false;
            }

            return !coopHandler.AreAllHumanPlayersDead();
        }

        return false;
    }

    /// <summary>
    /// 检查最后一击的伤害是否应当因手雷或爆头而致死
    /// </summary>
    /// <returns>若该伤害应当致死则为 <see langword="true"/>；否则为 <see langword="false"/></returns>
    public bool CheckIfDamageShouldInstantKill()
    {
        if (_grenadesKills && _fikaPlayer.LatestDamageInfo.DamageType is EDamageType.GrenadeFragment)
        {
            return true;
        }

        if (_fikaPlayer.LastDamagedBodyPart is EBodyPart.Head && _headshotKills)
        {
            return true;
        }

        return false;
    }

    public override void SendNetworkSyncPacket(SyncHealthPacket packet)
    {
        // 断线之后，发送包的对象正在被拆除时，生命控制器
        // 仍可能收到原生回调。
        var networkManager = _fikaPlayer?.PacketSender?.NetworkManager;
        if (networkManager is null)
        {
            return;
        }

        if (packet.SyncType == SyncHealthPacket.ESyncType.IsAlive && !packet.Data.IsAlive.IsAlive)
        {
            if (!TryProcessDownedState())
            {
                _fikaPlayer.SetupCorpseSyncPacket(packet);
            }
            return;
        }

        if (packet.SyncType is SyncHealthPacket.ESyncType.ApplyDamage)
        {
            return;
        }

        _fikaPlayer.CommonPacket.Type = ECommonSubPacketType.HealthSync;
        _fikaPlayer.CommonPacket.SubPacket = HealthSyncPacket.FromValue(packet);
        networkManager.SendNetReusable(ref _fikaPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
    }

    public void Revive()
    {
        _revives++;
        Downed = false;
        if (Energy.Current <= 20f)
        {
            ChangeEnergy(20f);
        }

        if (Hydration.Current <= 20f)
        {
            ChangeHydration(20f);
        }

        if (ShouldBleedOut)
        {
            var gameUi = MonoBehaviourSingleton<GameUI>.Instance;
            gameUi.BattleUiPanelExtraction.Close();
        }
    }

    public void BleedOut()
    {
        if (_bledOut || !Downed) return;
        _bledOut = true;
        IsAlive = true; // need to be alive to trigger Kill() again
        Kill(_fikaPlayer.LatestDamageInfo.DamageType);
    }

    private bool TryProcessDownedState()
    {
        if (!ReviveEnabled)
        {
            return false;
        }

        if (_bledOut)
        {
            return false;
        }

        if (Downed)
        {
            return true;
        }

        if (!CanBeDowned)
        {
            return false;
        }

        if (CheckIfDamageShouldInstantKill())
        {
            return false;
        }

        if ((_fikaPlayer.LatestDamageInfo.DamageType & (EDamageType.Exhaustion | EDamageType.Dehydration | EDamageType.Stimulator)) != 0) // starving / dehydration / stims will not trigger downed
        {
            return false;
        }

        if (EftScreenManager.Instance.CheckCurrentScreen(EEftScreenType.Inventory))
            EftScreenManager.Instance.ToggleScreen(EEftScreenType.Inventory);

        RestoreBodyPartNoEvents(EBodyPart.Head); // prevent blacked out head
        RestoreBodyPartNoEvents(EBodyPart.Chest); // prevent blacked out chest

        RemoveAllBleedEffects();

        _fikaPlayer.ToggleDowned(true);
        return true;
    }

    /// <summary>
    /// 从生命控制器上移除所有流血效果
    /// </summary>
    /// <remarks>主要用于防止复活后肢体立刻破损</remarks>
    private void RemoveAllBleedEffects()
    {
        for (var i = Effects.Count - 1; i >= 0; i--)
        {
            // Effects 是一个 IL2CPP 原生集合；包装器可能暴露
            // 基类型 IHealthEffect，即使原生对象是
            // 流血的子类型。
            if (Effects[i]?.TryCast<HeavyBleeding>() is { } heavyBleeding)
            {
                heavyBleeding.ForceRemove();
                continue;
            }

            if (Effects[i]?.TryCast<LightBleeding>() is { } lightBleeding)
            {
                lightBleeding.ForceRemove();
            }
        }
    }

    private void RestoreBodyPartNoEvents(EBodyPart bodyPart)
    {
        var limb = BodyState[bodyPart];
        if (limb.IsDestroyed)
        {
            limb.IsDestroyed = false;
            limb.Health.Current = 1f;

            NetworkSyncDestroyedBodyPart(bodyPart, EDamageType.Medicine);
            NetworkSyncBodyHealth(bodyPart);
        }
    }
}
