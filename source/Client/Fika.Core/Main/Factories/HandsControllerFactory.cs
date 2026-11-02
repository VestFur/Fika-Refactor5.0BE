// © 2026 Lacyway All Rights Reserved

using EFT;
using EFT.InventoryLogic;
using EFT.NetworkPackets;
using Fika.Core.Main.ObservedClasses.HandsControllers;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.Factories;

/// <summary>
/// 用于为客户端创建自定义的 <see cref="Player.ItemHandsController"/>，供网络同步使用
/// </summary>
/// <param name="player">要初始化该控制器的 <see cref="FikaPlayer"/>。</param>
/// <param name="item">要添加到该控制器上的 <see cref="EFT.InventoryLogic.Item"/>。</param>
internal sealed class HandsControllerFactory(ObservedPlayer player, Item item = null, KnifeComponent knifeComponent = null)
{
    public ObservedPlayer Player = player;
    public Item Item = item;
    public KnifeComponent KnifeComponent = knifeComponent;
    public Meds MedsItem;
    public FoodDrink FoodItem;
    public OneAndList<EBodyPart> BodyParts;
    public float Amount;
    public int AnimationVariant;

    /// <summary>
    /// 创建一个 <see cref="ObservedFirearmController"/>
    /// </summary>
    /// <returns>新的 <see cref="ObservedFirearmController"/>；操作失败时为 null。</returns>
    public Player.FirearmController CreateObservedFirearmController()
    {
        if (Item?.TryCast<Weapon>() is { } weapon)
        {
            return ObservedFirearmController.Create(Player, weapon);
        }

        FikaGlobals.LogError($"item was not of type Weapon, was: {Item?.GetType()?.FullName ?? "<null>"}");
        return null;
    }

    /// <summary>
    /// 创建一个 <see cref="ObservedGrenadeController"/>
    /// </summary>
    /// <returns>新的 <see cref="ObservedGrenadeController"/>；操作失败时为 null。</returns>
    public Player.GrenadeHandsController CreateObservedGrenadeController()
    {
        if (Item?.TryCast<ThrowWeap>() is { } grenade)
        {
            return ObservedGrenadeController.Create(Player, grenade);
        }

        FikaGlobals.LogError($"item was not of type GrenadeClass, was: {Item?.GetType()?.FullName ?? "<null>"}");
        return null;
    }

    /// <summary>
    /// 创建一个 <see cref="ObservedQuickGrenadeController"/>
    /// </summary>
    /// <returns>新的 <see cref="ObservedQuickGrenadeController"/>；操作失败时为 null。</returns>
    public Player.QuickGrenadeThrowHandsController CreateObservedQuickGrenadeController()
    {
        if (Item?.TryCast<ThrowWeap>() is { } grenade)
        {
            return ObservedQuickGrenadeController.Create(Player, grenade);
        }

        FikaGlobals.LogError($"item was not of type GrenadeClass, was: {Item?.GetType()?.FullName ?? "<null>"}");
        return null;
    }

    /// <summary>
    /// 创建一个 <see cref="ObservedKnifeController"/>
    /// </summary>
    /// <returns>新的 <see cref="ObservedKnifeController"/>；操作失败时为 null。</returns>
    public Player.KnifeController CreateObservedKnifeController()
    {
        if (KnifeComponent != null)
        {
            return ObservedKnifeController.Create(Player, KnifeComponent);
        }

        FikaGlobals.LogError("knifeComponent was null!");
        return null;
    }

    /// <summary>
    /// 创建一个 <see cref="ObservedMedsController"/>
    /// </summary>
    /// <returns>新的 <see cref="ObservedMedsController"/>；操作失败时为 null。</returns>
    public Player.MedsController CreateObservedMedsController()
    {
        if (FoodItem != null)
        {
            return ObservedMedsController.Create(Player, FoodItem, new(EBodyPart.Head), Amount, AnimationVariant);
        }
        if (MedsItem != null)
        {
            return ObservedMedsController.Create(Player, MedsItem, BodyParts, 1f, AnimationVariant);
        }

        FikaGlobals.LogError("meds or food was null!");
        return null;
    }
}
