using EFT.HealthSystem;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Pooling;
using static EFT.HealthSystem.SyncHealthPacket;
using static EFT.HealthSystem.SyncHealthPacket.SyncAddEffect;

namespace Fika.Core.Networking.Packets.Player.Common.SubPackets;

public sealed class HealthSyncPacket : IPoolSubPacket
{
    private HealthSyncPacket() { }

    public static HealthSyncPacket CreateInstance()
    {
        return new();
    }

    public SyncHealthPacket Packet;
    public Il2CppSystem.Nullable<MongoID> KillerId = new();
    public Il2CppSystem.Nullable<MongoID> WeaponId = new();
    public EBodyPart BodyPart;
    public CorpseSyncPackets CorpseSyncPacket;
    public List<string> TriggerZones = new(4);

    public static HealthSyncPacket FromValue(SyncHealthPacket value)
    {
        var packet = CommonSubPacketPoolManager.Instance.GetPacket<HealthSyncPacket>(ECommonSubPacketType.HealthSync);
        packet.Packet = value;
        return packet;
    }

    public void Execute(FikaPlayer player = null)
    {
        if (player is { IsYourPlayer: true } && player.Downed &&
            Packet.SyncType == ESyncType.IsAlive && !Packet.Data.IsAlive.IsAlive)
        {
            player.RevivalClock.MarkDead();
            return;
        }
        if (player.TryCast<ObservedPlayer>() is { } observedPlayer)
        {
            if (Packet.SyncType == ESyncType.IsAlive && !Packet.Data.IsAlive.IsAlive)
            {
                if (observedPlayer.HasDied) return;
                if (KillerId.HasValue)
                {
                    observedPlayer.SetAggressorData(KillerId.Value, BodyPart, WeaponId.HasValue ? WeaponId.Value : null);
                }
                observedPlayer.CorpseSyncPacket = CorpseSyncPacket;
                if (TriggerZones.Count > 0)
                {
                    foreach (var zone in TriggerZones)
                    {
                        observedPlayer.TriggerZones.Add(zone);
                    }
                }
                if (!observedPlayer.NetworkHealthController.IsAlive) // 防止已倒地的玩家没有真正死亡
                {
                    observedPlayer.NetworkHealthController.IsAlive = true;
                }
            }
            observedPlayer.NetworkHealthController.HandleSyncPacket(Packet);
            return;
        }

        FikaGlobals.LogError($"OnHealthSyncPacketReceived::Player with id {player.NetId} was not observed. Name: {player.Profile.GetCorrectedNickname()}");
    }

    public void Deserialize(NetDataReader reader)
    {
        ResetDeathData();
        SyncHealthPacket packet = new()
        {
            SyncType = reader.GetEnum<ESyncType>()
        };
        var data = packet.Data;

        switch (packet.SyncType)
        {
            case ESyncType.AddEffect:
                {
                    var addEffect = data.AddEffect;
                    addEffect.EffectId = reader.GetInt();
                    addEffect.Type = reader.GetByte();
                    addEffect.BodyPart = reader.GetEnum<EBodyPart>();
                    addEffect.DelayTime = reader.GetFloat();
                    addEffect.BuildUpTime = reader.GetFloat();
                    addEffect.WorkTime = reader.GetFloat();
                    addEffect.ResidueTime = reader.GetFloat();
                    addEffect.Strength = reader.GetPackedFloat(-100f, 100f, EFloatCompression.High);
                    addEffect.ExtraDataType = reader.GetEnum<EExtraDataType>();

                    // 嵌套的原生取值器返回的是装箱副本。请把每一层
                    // 都写回，这样效果才能解析到它真正的物品。
                    var extraData = addEffect.ExtraData;
                    switch (addEffect.ExtraDataType)
                    {
                        case EExtraDataType.MedEffect:
                            var medEffect = extraData.MedEffect;
                            medEffect.ItemId = reader.GetMongoID();
                            medEffect.Amount = reader.GetFloat();
                            extraData.MedEffect = medEffect;
                            break;

                        case EExtraDataType.Stimulator:
                            var stimulator = extraData.Stimulator;
                            stimulator.BuffsName = reader.GetString();
                            extraData.Stimulator = stimulator;
                            break;
                    }
                    addEffect.ExtraData = extraData;
                    data.AddEffect = addEffect;
                    break;
                }

            case ESyncType.RemoveEffect:
                var removeEffect = data.RemoveEffect;
                removeEffect.EffectId = reader.GetInt();
                data.RemoveEffect = removeEffect;
                break;

            case ESyncType.EffectNextState:
                var ens = data.EffectNextState;
                ens.EffectId = reader.GetInt();
                ens.StateTime = reader.GetFloat();
                data.EffectNextState = ens;
                break;

            case ESyncType.EffectStateTime:
                var est = data.EffectStateTime;
                est.EffectId = reader.GetInt();
                est.RemainingStateTime = reader.GetFloat();
                data.EffectStateTime = est;
                break;

            case ESyncType.EffectStrength:
                var estr = data.EffectStrength;
                estr.EffectId = reader.GetInt();
                estr.Strength = reader.GetPackedFloat(0f, 27f, EFloatCompression.High);
                data.EffectStrength = estr;
                break;

            case ESyncType.EffectMedResource:
                var emr = data.EffectMedResource;
                emr.EffectId = reader.GetInt();
                emr.Resource = reader.GetFloat();
                data.EffectMedResource = emr;
                break;

            case ESyncType.EffectStimulatorBuff:
                {
                    var stim = data.EffectStimulatorBuff;
                    stim.EffectId = reader.GetInt();
                    stim.BuffIndex = reader.GetPackedInt(0, 63);
                    stim.BuffActivate = reader.GetBool();

                    if (stim.BuffActivate)
                    {
                        stim.BuffValue = reader.GetFloat();
                        stim.BuffDuration = reader.GetFloat();
                        stim.BuffDelay = reader.GetFloat();
                    }
                    data.EffectStimulatorBuff = stim;
                    break;
                }

            case ESyncType.IsAlive:
                {
                    var alive = data.IsAlive;
                    alive.IsAlive = reader.GetBool();

                    if (!alive.IsAlive)
                    {
                        alive.DamageType = reader.GetEnum<EDamageType>();
                        var killerId = reader.GetNullableMongoID();
                        var weaponId = reader.GetNullableMongoID();
                        KillerId = killerId is not null ? new Il2CppSystem.Nullable<MongoID>(killerId) : new();
                        WeaponId = weaponId is not null ? new Il2CppSystem.Nullable<MongoID>(weaponId) : new();
                        BodyPart = reader.GetEnum<EBodyPart>();
                        CorpseSyncPacket = reader.GetCorpseSyncPacket();
                        int count = reader.GetByte();
                        for (var i = 0; i < count; i++)
                        {
                            TriggerZones.Add(reader.GetString());
                        }
                    }
                    data.IsAlive = alive;
                    break;
                }

            case ESyncType.BodyHealth:
                {
                    var bh = data.BodyHealth;
                    bh.BodyPart = reader.GetEnum<EBodyPart>();
                    bh.Value = reader.GetFloat();
                    data.BodyHealth = bh;
                    break;
                }

            case ESyncType.Energy:
                var energy = data.Energy;
                energy.Value = reader.GetPackedFloat(0f, 200f, EFloatCompression.High);
                data.Energy = energy;
                break;

            case ESyncType.Hydration:
                var hydration = data.Hydration;
                hydration.Value = reader.GetPackedFloat(0f, 200f, EFloatCompression.High);
                data.Hydration = hydration;
                break;

            case ESyncType.Temperature:
                var temperature = data.Temperature;
                temperature.Value = reader.GetPackedFloat(0f, 100f, EFloatCompression.High);
                data.Temperature = temperature;
                break;

            case ESyncType.DamageCoeff:
                var damageCoeff = data.DamageCoeff;
                damageCoeff.DamageCoeff = reader.GetFloat();
                data.DamageCoeff = damageCoeff;
                break;

            case ESyncType.ApplyDamage:
                {
                    var dmg = data.ApplyDamage;
                    dmg.BodyPart = reader.GetEnum<EBodyPart>();
                    dmg.Damage = reader.GetFloat();
                    dmg.DamageType = reader.GetEnum<EDamageType>();
                    data.ApplyDamage = dmg;
                    break;
                }

            case ESyncType.DestroyedBodyPart:
                {
                    var destroyed = data.DestroyedBodyPart;
                    destroyed.BodyPart = reader.GetEnum<EBodyPart>();
                    destroyed.IsDestroyed = reader.GetBool();

                    if (destroyed.IsDestroyed)
                    {
                        destroyed.DamageType = reader.GetEnum<EDamageType>();
                    }
                    else
                    {
                        destroyed.HealthMaximum = reader.GetFloat();
                    }
                    data.DestroyedBodyPart = destroyed;
                    break;
                }

            case ESyncType.HealthRates:
                {
                    var rates = data.HealthRates;
                    rates.HealRate = reader.GetPackedFloat(0f, 3000f, EFloatCompression.High);
                    rates.DamageRate = reader.GetPackedFloat(-1000f, 0f, EFloatCompression.High);
                    rates.DamageMultiplier = reader.GetPackedFloat(0f, 2f, EFloatCompression.High);
                    rates.Energy = reader.GetPackedFloat(-2000f, 3000f, EFloatCompression.High);
                    rates.Hydration = reader.GetPackedFloat(-2000f, 3000f, EFloatCompression.High);
                    rates.Temperature = reader.GetPackedFloat(-100f, 100f, EFloatCompression.High);
                    data.HealthRates = rates;
                    break;
                }

            case ESyncType.HealerDone:
                var healerDone = data.HealerDone;
                healerDone.EffectId = reader.GetInt();
                data.HealerDone = healerDone;
                break;

            case ESyncType.BurnEyes:
                {
                    var burn = data.BurnEyes;
                    burn.Position = reader.GetUnmanaged<Vector3>();
                    burn.DistanceStrength = reader.GetFloat();
                    burn.NormalTime = reader.GetFloat();
                    data.BurnEyes = burn;
                    break;
                }

            case ESyncType.Poison:
                var poison = data.Poison;
                poison.Value = reader.GetPackedFloat(0f, 100f, EFloatCompression.High);
                data.Poison = poison;
                break;

            case ESyncType.StaminaCoeff:
                var staminaCoeff = data.StaminaCoeff;
                staminaCoeff.StaminaCoeff = reader.GetFloat();
                data.StaminaCoeff = staminaCoeff;
                break;
        }

        packet.Data = data;
        Packet = packet;
    }

    public void Serialize(NetDataWriter writer)
    {
        var packet = Packet.Data;
        writer.PutEnum(Packet.SyncType);

        switch (Packet.SyncType)
        {
            case ESyncType.AddEffect:
                {
                    var addEffect = packet.AddEffect;
                    writer.Put(addEffect.EffectId);
                    writer.Put(addEffect.Type);
                    writer.PutEnum(addEffect.BodyPart);
                    writer.Put(addEffect.DelayTime);
                    writer.Put(addEffect.BuildUpTime);
                    writer.Put(addEffect.WorkTime);
                    writer.Put(addEffect.ResidueTime);
                    writer.PutPackedFloat(addEffect.Strength, -100f, 100f, EFloatCompression.High);
                    writer.PutEnum(addEffect.ExtraDataType);

                    switch (addEffect.ExtraDataType)
                    {
                        case EExtraDataType.MedEffect:
                            writer.PutMongoID(addEffect.ExtraData.MedEffect.ItemId);
                            writer.Put(addEffect.ExtraData.MedEffect.Amount);
                            break;
                        case EExtraDataType.Stimulator:
                            writer.Put(addEffect.ExtraData.Stimulator.BuffsName);
                            break;
                    }
                    break;
                }

            case ESyncType.RemoveEffect:
                writer.Put(packet.RemoveEffect.EffectId);
                break;

            case ESyncType.EffectNextState:
                {
                    var ens = packet.EffectNextState;
                    writer.Put(ens.EffectId);
                    writer.Put(ens.StateTime);
                    break;
                }

            case ESyncType.EffectStateTime:
                {
                    var est = packet.EffectStateTime;
                    writer.Put(est.EffectId);
                    writer.Put(est.RemainingStateTime);
                    break;
                }

            case ESyncType.EffectStrength:
                {
                    var estr = packet.EffectStrength;
                    writer.Put(estr.EffectId);
                    writer.PutPackedFloat(estr.Strength, 0f, 27f, EFloatCompression.High);
                    break;
                }

            case ESyncType.EffectMedResource:
                {
                    var emr = packet.EffectMedResource;
                    writer.Put(emr.EffectId);
                    // 这个值会成为背包状态，包括耗尽时的零值。
                    // 量化会永久改变套装资源。
                    writer.Put(emr.Resource);
                    break;
                }

            case ESyncType.EffectStimulatorBuff:
                {
                    var stim = packet.EffectStimulatorBuff;
                    writer.Put(stim.EffectId);
                    writer.PutPackedInt(stim.BuffIndex, 0, 63);
                    writer.Put(stim.BuffActivate);
                    if (stim.BuffActivate)
                    {
                        writer.Put(stim.BuffValue);
                        writer.Put(stim.BuffDuration);
                        writer.Put(stim.BuffDelay);
                    }
                    break;
                }

            case ESyncType.IsAlive:
                {
                    var alive = packet.IsAlive;
                    writer.Put(alive.IsAlive);
                    if (!alive.IsAlive)
                    {
                        writer.PutEnum(alive.DamageType);
                        writer.PutNullableMongoID(KillerId.HasValue ? KillerId.Value : null);
                        writer.PutNullableMongoID(WeaponId.HasValue ? WeaponId.Value : null);
                        writer.PutEnum(BodyPart);
                        writer.PutCorpseSyncPacket(CorpseSyncPacket);
                        writer.Put((byte)TriggerZones.Count);
                        for (var i = 0; i < TriggerZones.Count; i++)
                        {
                            writer.Put(TriggerZones[i]);
                        }
                    }
                    break;
                }

            case ESyncType.BodyHealth:
                {
                    var bh = packet.BodyHealth;
                    writer.PutEnum(bh.BodyPart);
                    writer.Put(bh.Value);
                    break;
                }

            case ESyncType.Energy:
                writer.PutPackedFloat(packet.Energy.Value, 0f, 200f, EFloatCompression.High);
                break;

            case ESyncType.Hydration:
                writer.PutPackedFloat(packet.Hydration.Value, 0f, 200f, EFloatCompression.High);
                break;

            case ESyncType.Temperature:
                writer.PutPackedFloat(packet.Temperature.Value, 0f, 100f, EFloatCompression.High);
                break;

            case ESyncType.DamageCoeff:
                writer.Put(packet.DamageCoeff.DamageCoeff);
                break;

            case ESyncType.ApplyDamage:
                {
                    var dmg = packet.ApplyDamage;
                    writer.PutEnum(dmg.BodyPart);
                    writer.Put(dmg.Damage);
                    writer.PutEnum(dmg.DamageType);
                    break;
                }

            case ESyncType.DestroyedBodyPart:
                {
                    var destroyed = packet.DestroyedBodyPart;
                    writer.PutEnum(destroyed.BodyPart);
                    writer.Put(destroyed.IsDestroyed);
                    if (destroyed.IsDestroyed)
                    {
                        writer.PutEnum(destroyed.DamageType);
                    }
                    else
                    {
                        writer.Put(destroyed.HealthMaximum);
                    }

                    break;
                }

            case ESyncType.HealthRates:
                {
                    var rates = packet.HealthRates;
                    writer.PutPackedFloat(rates.HealRate, 0f, 3000f, EFloatCompression.High);
                    writer.PutPackedFloat(rates.DamageRate, -1000f, 0f, EFloatCompression.High);
                    writer.PutPackedFloat(rates.DamageMultiplier, 0f, 2f, EFloatCompression.High);
                    writer.PutPackedFloat(rates.Energy, -2000f, 3000f, EFloatCompression.High);
                    writer.PutPackedFloat(rates.Hydration, -2000f, 3000f, EFloatCompression.High);
                    writer.PutPackedFloat(rates.Temperature, -100f, 100f, EFloatCompression.High);
                    break;
                }

            case ESyncType.HealerDone:
                writer.Put(packet.HealerDone.EffectId);
                break;

            case ESyncType.BurnEyes:
                {
                    var burn = packet.BurnEyes;
                    writer.PutUnmanaged(burn.Position);
                    writer.Put(burn.DistanceStrength);
                    writer.Put(burn.NormalTime);
                    break;
                }

            case ESyncType.Poison:
                writer.PutPackedFloat(packet.Poison.Value, 0f, 100f, EFloatCompression.High);
                break;

            case ESyncType.StaminaCoeff:
                writer.Put(packet.StaminaCoeff.StaminaCoeff);
                break;
        }
    }

    public void Dispose()
    {
        ResetDeathData();
        Packet = default;
    }

    private void ResetDeathData()
    {
        KillerId = new();
        WeaponId = new();
        BodyPart = default;
        CorpseSyncPacket = default;
        TriggerZones.Clear();
    }
}

public struct CorpseSyncPackets
{
    public ItemDescriptor InventoryDescriptor;
    public Item ItemInHands;

    public EBodyPartColliderType BodyPartColliderType;

    public Vector3 Direction;
    public Vector3 Point;

    public float Force;

    public EquipmentSlot ItemSlot;
}
