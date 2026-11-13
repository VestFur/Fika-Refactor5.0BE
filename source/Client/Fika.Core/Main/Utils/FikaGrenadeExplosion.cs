using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using Il2CppSystems.Effects;
using UnityEngine;

namespace Fika.Core.Main.Utils;

/// <summary>
/// A client's grenade is an ObservedGrenade: it never runs the real fuse. Its StartTimer only
/// schedules Expire(), which logs "Grenade expired by time" and destroys the object WITHOUT any
/// explosion (proven by nine such lines in a real client log where the client saw and heard
/// nothing). The only thing that can produce the client's visible and audible explosion is
/// ObservedGrenade.OnDoneFromNet() -> Grenade.InvokeBlowUpEvent(), which for a client either never
/// arrives or is swallowed, so this helper runs the same effect explicitly.
/// </summary>
internal static class FikaGrenadeExplosion
{
    private static readonly HashSet<IntPtr> Exploded = new();

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

            // 1) the visible + audible explosion the client was missing
            if (!string.IsNullOrEmpty(effect) && Singleton<Effects>.Instantiated)
            {
                Singleton<Effects>.Instance.EmitGrenade(effect, position, Vector3.up, 0f);
            }

            // 2) the native damage/contusion pass for whatever this machine is authoritative for.
            //    On a client the host relays the real damage, so this only covers the local case.
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
