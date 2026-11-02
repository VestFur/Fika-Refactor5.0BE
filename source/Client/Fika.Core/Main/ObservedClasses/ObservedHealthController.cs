// © 2026 Lacyway All Rights Reserved

using EFT.NetworkPackets;
using System;
using System.Collections.Generic;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;

namespace Fika.Core.Main.ObservedClasses;

public sealed class ObservedHealthController
    : NetworkHealthController
{
    private readonly byte[] serializedState;
    private readonly ObservedPlayer player;
    private readonly InventoryController inventory;
    private readonly SkillManager skills;

    public ObservedHealthController(byte[] serializedState, ObservedPlayer player, InventoryController inventory, SkillManager skills) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedHealthController>())
    {
        this.serializedState = serializedState;
        this.player = player;
        this.inventory = inventory;
        this.skills = skills;
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<NetworkHealthController>(this, new object[] { (Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>)serializedState, inventory, skills });
    }


    public ObservedHealthController(System.IntPtr pointer) : base(pointer) { }


    public override Player Player => player;

    public override bool ApplyItem(Item item, EBodyPart bodyPart, Il2CppSystem.Nullable<float> amount = default) => false;

    public override bool ApplyItem(Item item, OneAndList<EBodyPart> bodyPart, Il2CppSystem.Nullable<float> amount = default) => false;

    public override void CancelApplyingItem()
    {
        // 复制的治疗效果由 HandleSyncPacket 应用，绝不在本地应用。
    }

    public void PauseAllEffects()
    {
        EffectHandler handler = new(this);
        for (var i = _effects.Count - 1; i >= 0; i--)
        {
            handler.PausedEffects.Add(_effects[i]);
            var gstruct = _effectsInfoPool.Withdraw();
            gstruct.SaveInfo(_effects[i].Id, _effects[i].HealthController, _effects[i].Type, _effects[i].BodyPart, _effects[i].Strength,
                _effects[i].CurrentStrength, _effects[i].DelayTime, _effects[i].StateTime, _effects[i].WorkStateTime, _effects[i].BuildUpTime,
                _effects[i].ResidueTime, _effects[i].State);
            RemoveEffectFromBodyPart(_effects[i]);
            _effects[i].ForceRemove();
            handler.PausedEffectsInfo.Add(gstruct);
        }

        _unpauseEffectsAction = new Action(handler.UnpauseEffects);
    }

    public override Profile.HealthInfo Store(Profile.HealthInfo health = null)
    {
        Profile.HealthInfo profileHealthClass;
        if ((profileHealthClass = health) == null)
        {
            Profile.HealthInfo profileHealthClass2 = new()
            {
                // 生成的 EnumHelper.GetDictWith 包装器在 IL2CPP 上会错误地
                // 对 TValue 闭合 EnumHelper。因此在下面从 BodyState 填充
                // 原生字典，并保留每一个真实的部位。
                BodyParts = new Il2CppSystem.Collections.Generic.Dictionary<EBodyPart, Profile.HealthInfo.BodyPartInfo>(),
                Energy = new Profile.HealthInfo.ValueInfo
                {
                    Current = _energy.Current,
                    Minimum = _energy.Minimum,
                    Maximum = _energy.Maximum
                },
                Hydration = new Profile.HealthInfo.ValueInfo
                {
                    Current = _hydration.Current,
                    Minimum = _hydration.Minimum,
                    Maximum = _hydration.Maximum
                },
                Temperature = new Profile.HealthInfo.ValueInfo
                {
                    Current = _temperature.Current,
                    Minimum = _temperature.Minimum,
                    Maximum = _temperature.Maximum
                }
            };
            profileHealthClass = profileHealthClass2;
            profileHealthClass2.Poison = new Profile.HealthInfo.ValueInfo
            {
                Current = _poison.Current,
                Minimum = _poison.Minimum,
                Maximum = _poison.Maximum
            };
        }
        health = profileHealthClass;
        // Store 可能会以已有的 profile 被调用。替换瞬态值与
        // 效果字典，使被移除的效果无法在重连时重新出现。
        health.Energy = new Profile.HealthInfo.ValueInfo { Current = _energy.Current, Minimum = _energy.Minimum, Maximum = _energy.Maximum };
        health.Hydration = new Profile.HealthInfo.ValueInfo { Current = _hydration.Current, Minimum = _hydration.Minimum, Maximum = _hydration.Maximum };
        health.Temperature = new Profile.HealthInfo.ValueInfo { Current = _temperature.Current, Minimum = _temperature.Minimum, Maximum = _temperature.Maximum };
        health.Poison = new Profile.HealthInfo.ValueInfo { Current = _poison.Current, Minimum = _poison.Minimum, Maximum = _poison.Maximum };
        foreach (var keyValuePair in BodyState)
        {
            keyValuePair.Deconstruct(out var ebodyPart, out var bodyPartState);
            var ebodyPart2 = ebodyPart;
            var bodyPartState2 = bodyPartState;
            if (!health.BodyParts.TryGetValue(ebodyPart2, out var gclass))
            {
                gclass = new Profile.HealthInfo.BodyPartInfo();
                health.BodyParts.Add(ebodyPart2, gclass);
            }
            gclass.Health = new Profile.HealthInfo.ValueInfo
            {
                Current = bodyPartState2.Health.Current,
                Maximum = bodyPartState2.Health.Maximum
            };
            gclass.Effects = new Il2CppSystem.Collections.Generic.Dictionary<string, Profile.HealthInfo.EffectInfo>();
        }

        foreach (var gclass in Effects)
        {
            if (gclass.TryCast<IRestorable>() != null && gclass.State != EEffectState.Residued)
            {
                var gclass2 = health.BodyParts[gclass.BodyPart];
                gclass2.Effects ??= new Il2CppSystem.Collections.Generic.Dictionary<string, Profile.HealthInfo.EffectInfo>();
                // 效果通过基类代理暴露；CLR 的 GetType() 对每个实例
                // 都可能报告 "Effect"。改用生命值序列化与重连恢复所消费的
                // 原生类型名。
                gclass2.Effects[gclass.GetIl2CppType().Name] = new()
                {
                    Time = gclass.TimeLeft
                };
            }
        }

        return health;
    }

    private class EffectHandler(ObservedHealthController healthController)
    {
        private readonly ObservedHealthController _healthController = healthController;
        public readonly List<NetworkHealthController.Effect> PausedEffects = [];
        public readonly List<EffectInfoStorage> PausedEffectsInfo = [];

        public void UnpauseEffects()
        {
            for (var i = PausedEffects.Count - 1; i >= 0; i--)
            {
                PausedEffects[i].SetEffectInfo(PausedEffectsInfo[i].ID, PausedEffectsInfo[i].HealthController, PausedEffectsInfo[i].Type,
                    PausedEffectsInfo[i].BodyPart, PausedEffectsInfo[i].Strength, PausedEffectsInfo[i].CurrentStrength,
                    PausedEffectsInfo[i].DelayTime, PausedEffectsInfo[i].StateTime, PausedEffectsInfo[i].WorkStateTime,
                    PausedEffectsInfo[i].BuildUpTime, PausedEffectsInfo[i].ResidueStateTime, PausedEffectsInfo[i].State);
                _healthController.AddEffectToList(PausedEffects[i]);
                PausedEffects[i].UnPauseEffect();
                _healthController._effectsInfoPool.Return(PausedEffectsInfo[i]);
            }
        }
    }
}

