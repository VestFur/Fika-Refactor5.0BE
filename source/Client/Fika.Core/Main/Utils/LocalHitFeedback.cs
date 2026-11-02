// © 2026 Lacyway All Rights Reserved

using System;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using Fika.Core.Main.Players;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 让开枪者获得游戏在本地产生的命中反馈，同时不重复结算伤害。
///
/// 为什么需要它：在 Fika 战局中，客户端造成的子弹伤害由拥有受害者的机器结算
/// ——bot 由主机结算，其他玩家由受害者自身结算——因此
/// <c>ActiveHealthController.ApplyDamage(EBodyPart, float, DamageInfo)</c> 从不会在开枪者的
/// 机器上以 <c>damageInfo.Player.iPlayer</c> 为开枪者的方式运行。于是该成员的所有消费者
/// （命中标记、伤害数字、命中音效——例如 SimpleHitMarker 插件，
/// 它正好 postfix 了那个方法）对开枪者都保持沉默，尽管
/// 命中已经登记并被转发到了权威机器。
///
/// 下面的调用在受害者自己的健康控制器上复用了插件所监听的那个成员，
/// 伤害值为 0，伤害类型不是敌方伤害：
///  * <c>ActiveHealthController.ApplyDamage</c> 只在基类上声明
///    （PlayerHealthController 和 NetworkHealthController 都没有 override 它），因此该 Harmony postfix
///    对受观察的木偶同样会运行；
///  * 零伤害意味着没有生命值变化、没有身体部位被摧毁、没有 OnApplyDamageByPlayer
///    （该回调以敌方伤害为条件，这也是伤害类型留空的原因）
///    也不会发包：本机器上受害者的生命值仍然只来自
///    权威机器的生命值同步。
/// </summary>
internal static class LocalHitFeedback
{
    /// <summary>
    /// 报告本地玩家刚在 <paramref name="victim"/> 上登记的一次命中，走的是
    /// 命中反馈插件所 patch 的健康控制器钩子。
    /// </summary>
    internal static void Report(Player shooter, ObservedPlayer victim, EBodyPart bodyPart, float damage, EDamageType damageType)
    {
        try
        {
            if (shooter == null || victim == null || damage <= 0f || !shooter.IsYourPlayer)
            {
                return;
            }

            var healthController = victim.ActiveHealthController;
            if (healthController == null || !healthController.IsAlive)
            {
                return;
            }

            // 该 bridge 就是权威机器上 DamageInfo.Player 所承载的对象；游戏为
            // 每个玩家登记一个（以 raid id 为键，以 collider 作为回退）。
            var world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            var owner = world.GetAlivePlayerBridgeByRaidID(shooter.RaidId);
            if (owner == null)
            {
                var characterController = shooter.GetCharacterControllerCommon();
                var collider = characterController != null ? characterController.GetCollider() : null;
                if (collider != null)
                {
                    owner = world.GetAlivePlayerBridgeByCollider(collider);
                }
            }

            if (owner == null)
            {
                FikaGlobals.LogWarning($"[HitMarker] no player bridge for {shooter.ProfileId}, the local hook was not fired");
                return;
            }

            DamageInfo feedback = new()
            {
                Damage = damage,
                DamageType = EDamageType.Undefined,
                Player = owner
            };

            // 零伤害：触发钩子，不改变任何东西。调用前后都会读取受害者的
            // 本地生命值，因此单次战局就能证明这里没有结算任何伤害。
            var healthBefore = healthController.GetBodyPartHealth(bodyPart, false).Current;
            healthController.ApplyDamage(bodyPart, 0f, feedback);
            var healthAfter = healthController.GetBodyPartHealth(bodyPart, false).Current;

            if (healthAfter < healthBefore)
            {
                FikaGlobals.LogError($"[HitMarker] the local hook changed the local health of {victim.ProfileId} " +
                    $"({healthBefore:F2} -> {healthAfter:F2}); this must never happen, please report it");
            }

            FikaPlugin.Instance.FikaLogger.LogInfo($"[HitMarker] shooter={shooter.ProfileId}, victim={victim.ProfileId}, " +
                $"role={(victim.Profile?.Info?.Settings != null ? victim.Profile.Info.Settings.Role.ToString() : "none")}, " +
                $"isObservedAI={victim.IsObservedAI}, part={bodyPart}, damage={damage:F2}, damageType={damageType}, " +
                $"health={healthBefore:F2}->{healthAfter:F2}, hook=ActiveHealthController.ApplyDamage(part, 0, DamageInfo)");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError("[HitMarker] could not report the local hit: " + exception);
        }
    }
}
