using EFT.Ballistics;
using System;
using Il2CppSystem.Collections.Generic;
using Fika.Core.Main.Utils;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;

namespace Fika.Core.Main.ObservedClasses;

/// <summary>
/// 创建者：Paulov
/// Paulov：为 Statistics Manager 的所有成员使用桩实现
/// </summary>
public sealed class ObservedStatisticsManager : Il2CppSystem.Object, IStatisticsManager
{
    public ObservedStatisticsManager(System.IntPtr pointer) : base(pointer) { }

    public ObservedStatisticsManager() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedStatisticsManager>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Il2CppSystem.Object>(this, []);
    }


    public Il2CppSystem.TimeSpan CurrentSessionLength
    {
        get
        {
            return default;
        }
    }

#pragma warning disable CS0067
#pragma warning restore CS0067
    private readonly NativeActionEvent _nativeOnUniqueLoot = new();
    // 47888 的 IStatisticsManager 事件访问器接收的是 IL2CPP 委托；
    // 那些 System.Action 重载只是该接口的 *_Managed 辅助方法。
    public void add_OnUniqueLoot(Il2CppSystem.Action handler) => _nativeOnUniqueLoot.Add(handler);
    public void remove_OnUniqueLoot(Il2CppSystem.Action handler) => _nativeOnUniqueLoot.Remove(handler);

    public void AddDoorExperience(bool breached)
    {
        // 不做任何事
    }

    public void BeginStatisticsSession()
    {
        // 不做任何事
    }

    public void EndStatisticsSession(ExitStatus exitStatus, float pastTime)
    {
        // 不做任何事
    }

    public void Init(Player player)
    {

    }

    public void OnEnemyDamage(DamageInfo damage, EBodyPart bodyPart, string playerProfileId, EPlayerSide playerSide, WildSpawnType role, string groupId, float fullHealth, bool isHeavyDamage, float distance, int hour, List<string> targetEquipment, HealthEffects enemyEffects, List<string> zoneIds)
    {
        // 不做任何事
    }

    public void OnEnemyKill(DamageInfo damage, EDamageType lethalDamageType, EBodyPart bodyPart, EPlayerSide playerSide, WildSpawnType role, string playerAccountId, string playerProfileId, string playerName, string groupId, int level, int killExp, float distance, int hour, List<string> targetEquipment, HealthEffects enemyEffects, List<string> zoneIds, bool isFriendly, bool isAI)
    {
        // 不做任何事
    }

    public void OnGrabLoot(Item item)
    {
        // 不做任何事
    }

    public void OnGroupMemberConnected(Inventory inventory)
    {
        // 不做任何事
    }

    public void OnInteractWithLootContainer(Item item)
    {
        // 不做任何事
    }

    public void OnShot(Weapon weapon, Ammo ammo)
    {
        // 不做任何事
    }
}


