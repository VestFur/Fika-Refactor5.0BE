using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using Fika.Core.Main.Players;

namespace Fika.Core.Main.ObservedClasses.PlayerBridge;

public sealed class ObservedHostBridge : Il2CppSystem.Object, BodyPartCollider.IObserverToPlayerBridge
{
    private readonly ObservedPlayer observedPlayer;

    public ObservedHostBridge(ObservedPlayer observedPlayer) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedHostBridge>())
    {
        this.observedPlayer = observedPlayer;
        _observedPlayer = observedPlayer;
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Il2CppSystem.Object>(this, new object[] {  });
    }


    public ObservedHostBridge(System.IntPtr pointer) : base(pointer) { }


    private readonly ObservedPlayer _observedPlayer ;

    public IPlayer iPlayer
    {
        get
        {
            return _observedPlayer;
        }
    }

    public float WorldTime
    {
        get
        {
            return Singleton<AbstractGame>.Instance.LastServerTimeStamp;
        }
    }

    public bool UsingSimplifiedSkeleton
    {
        get
        {
            return _observedPlayer.UsedSimplifiedSkeleton;
        }
    }

    public void ApplyDamageInfo(DamageInfo damageInfo, EBodyPart bodyPartType, EBodyPartColliderType bodyPartCollider, float absorbed)
    {
        if (damageInfo.DamageType.IsEnvironmental() || damageInfo.DamageType == EDamageType.Environment)
        {
            return;
        }

        // 手雷的伤害类型是 EDamageType.GrenadeFragment（由 Grenade.GetDamageInfo 构造），
        // 而主机权威时原版 ObserverBridge.ApplyDamageInfo 是空实现，因此
        // 每种爆炸伤害类型都必须显式转发，否则客户端永远不会受伤。
        if (damageInfo.DamageType is EDamageType.Landmine or EDamageType.Artillery
            or EDamageType.GrenadeFragment or EDamageType.Explosion or EDamageType.ThermobaricExplosion)
        {
            global::Fika.Core.Main.Utils.FikaGlobals.LogInfo($"[FikaGrenade] host relay -> netId={_observedPlayer.NetId} " +
                $"part={bodyPartType} dmg={damageInfo.Damage:F2} type={damageInfo.DamageType}");
            _observedPlayer.HandleExplosive(damageInfo, bodyPartType, bodyPartCollider);
            return;
        }

        if (damageInfo.Player != null && (damageInfo.Player.iPlayer.IsYourPlayer || damageInfo.Player.iPlayer.IsAI))
        {
            _observedPlayer.ApplyDamageInfo(damageInfo, bodyPartType, bodyPartCollider, absorbed);
        }
    }

    public PlayerHitInfo ApplyShot(DamageInfo damageInfo, EBodyPart bodyPart, EBodyPartColliderType bodyPartCollider, EArmorPlateCollider armorPlateCollider, ShotId shotId)
    {
        if (damageInfo.DamageType is EDamageType.Sniper)
        {
            return _observedPlayer.HandleSniperShot(damageInfo, bodyPart, bodyPartCollider, armorPlateCollider, shotId);
        }

        if (damageInfo.Player != null && (damageInfo.Player.iPlayer.IsYourPlayer || damageInfo.Player.iPlayer.IsAI))
        {
            return _observedPlayer.ApplyShot(damageInfo, bodyPart, bodyPartCollider, armorPlateCollider, shotId);
        }

        _observedPlayer.ShotReactions(damageInfo, bodyPart);
        _observedPlayer.ApplyHitDebuff(damageInfo.Damage, 0f, bodyPart, damageInfo.DamageType);
        return new()
        {
            PoV = EPointOfView.ThirdPerson,
            Penetrated = damageInfo.Penetrated,
            Material = MaterialType.Body
        };
    }

    public bool CheckArmorHitByDirection(BodyPartCollider bodypart, Vector3 hitpoint, Vector3 shotNormal, Vector3 shotDirection)
    {
        return _observedPlayer.CheckArmorHitByDirection(bodypart);
    }

    public bool IsShotDeflectedByHeavyArmor(EBodyPartColliderType colliderType, EArmorPlateCollider armorPlateCollider, int shotSeed)
    {
        return _observedPlayer.IsShotDeflectedByHeavyArmor(colliderType, armorPlateCollider, shotSeed);
    }

    public bool SetShotStatus(BodyPartCollider bodypart, Shot shot, Vector3 hitpoint, Vector3 shotNormal, Vector3 shotDirection)
    {
        return _observedPlayer.SetShotStatus(bodypart, shot, hitpoint, shotNormal, shotDirection);
    }

    public bool TryGetArmorResistData(BodyPartCollider bodyPart, float penetrationPower, out ArmorResistanceData armorResistanceData)
    {
        return _observedPlayer.TryGetArmorResistData(bodyPart, penetrationPower, out armorResistanceData);
    }

}
