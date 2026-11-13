using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using Il2CppSystems.Effects;
using UnityEngine;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 客户端的投掷物是 ObservedGrenade：它从不执行真正的引信。它的 StartTimer 只
/// 排定 Expire()，后者记录 "Grenade expired by time" 并销毁对象，而完全没有
/// 任何爆炸（真实的客户端日志里有九条这样的行作为证据，而客户端既没看到、也没听到
/// 任何东西）。唯一能产生客户端可见、可听的爆炸效果的东西是
/// ObservedGrenade.OnDoneFromNet() -> Grenade.InvokeBlowUpEvent()，而它在客户端要么从不到达，
/// 要么被吞掉，所以这个辅助方法显式地执行同样的效果。
/// </summary>
internal static class FikaGrenadeExplosion
{
    private static readonly HashSet<IntPtr> Exploded = new();

    /// <summary>
    /// 在世界拆除时丢弃已爆炸投掷物集合（analysis/transit-crash-unload）。该集合保存的是原始
    /// 原生地址，所以新世界若恰好复用了某个地址，否则它就会被报告为
    /// 已爆炸。返回被丢弃的条目数量。
    /// </summary>
    internal static int ClearForWorldTeardown()
    {
        var count = Exploded.Count;
        if (count > 0)
        {
            Exploded.Clear();
        }

        return count;
    }

    internal static bool TryExplode(Grenade grenade, string reason)
    {
        try
        {
            if (grenade == null || grenade.Pointer == IntPtr.Zero)
            {
                return false;
            }
            if (!Exploded.Add(grenade.Pointer))
            {
                return false;
            }

            var settings = grenade.GrenadeSettings;
            var weapon = grenade.WeaponSource;
            var effect = settings != null && !string.IsNullOrEmpty(settings.EmmisionEffect)
                ? settings.EmmisionEffect
                : weapon?.ExplosionEffectType;
            var position = grenade.transform.position;

            // 1) 客户端缺失的可见 + 可听爆炸效果
            if (!string.IsNullOrEmpty(effect) && Singleton<Effects>.Instantiated)
            {
                Singleton<Effects>.Instance.EmitGrenade(effect, position, Vector3.up, 0f);
            }

            // 2) 对这台机器具有权威的任意目标执行原生伤害/震荡处理。
            //    在客户端上真实伤害由主机转发，所以这里只覆盖本地情形。
            var explosive = weapon == null ? null : weapon.TryCast<IExplosiveItem>();
            if (explosive != null)
            {
                Grenade.Explosion(grenade, explosive, position, grenade.PlayerRaidId,
                    grenade.Calculator, weapon, new Vector3(0f, 0.08f, 0f), false);
            }

            FikaGlobals.LogInfo($"[FikaGrenade] client explosion ran: id={grenade.Id} effect='{effect}' " +
                $"pos={position} reason={reason}");
            return true;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FikaGrenade] client explosion failed ({reason}): {exception}");
            return false;
        }
    }
}
