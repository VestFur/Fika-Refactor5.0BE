using EFT.Ballistics;
using System;
using Il2CppSystem.Collections.Generic;
using Fika.Core.Main.Utils;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;

namespace Fika.Core.Main.ObservedClasses;

/// <summary>
/// Created by: Paulov
/// Paulov: Uses stubs for all of Statistics Manager
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
    public event Action OnUniqueLoot;
#pragma warning restore CS0067
    private readonly NativeActionEvent _nativeOnUniqueLoot = new();
    public void add_OnUniqueLoot(Il2CppSystem.Action handler) => _nativeOnUniqueLoot.Add(handler);
    public void remove_OnUniqueLoot(Il2CppSystem.Action handler) => _nativeOnUniqueLoot.Remove(handler);

    public void AddDoorExperience(bool breached)
    {
        // Do nothing
    }

    public void BeginStatisticsSession()
    {
        // Do nothing
    }

    public void EndStatisticsSession(ExitStatus exitStatus, float pastTime)
    {
        // Do nothing
    }

    public void Init(Player player)
    {

    }

    public void OnEnemyDamage(DamageInfo damage, EBodyPart bodyPart, string playerProfileId, EPlayerSide playerSide, WildSpawnType role, string groupId, float fullHealth, bool isHeavyDamage, float distance, int hour, List<string> targetEquipment, HealthEffects enemyEffects, List<string> zoneIds)
    {
        // Do nothing
    }

    public void OnEnemyKill(DamageInfo damage, EDamageType lethalDamageType, EBodyPart bodyPart, EPlayerSide playerSide, WildSpawnType role, string playerAccountId, string playerProfileId, string playerName, string groupId, int level, int killExp, float distance, int hour, List<string> targetEquipment, HealthEffects enemyEffects, List<string> zoneIds, bool isFriendly, bool isAI)
    {
        // Do nothing
    }

    public void OnGrabLoot(Item item)
    {
        // Do nothing
    }

    public void OnGroupMemberConnected(Inventory inventory)
    {
        // Do nothing
    }

    public void OnInteractWithLootContainer(Item item)
    {
        // Do nothing
    }

    public void OnShot(Weapon weapon, Ammo ammo)
    {
        // Do nothing
    }
}


