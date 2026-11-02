using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Player.Common;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Mines;

/// <summary>
/// 此补丁用于在 <see cref="ObservedPlayer"/> 被地雷爆炸波及时防止空引用异常
/// </summary>
internal class Minefield_Explode_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Minefield).GetMethod(nameof(Minefield.Explode));
    }

    [PatchPrefix]
    public static bool Prefix(IPlayer player, bool first, Minefield __instance)
    {
        if (player?.TryCast<ObservedPlayer>() != null)
        {
            if (FikaBackendUtils.IsServer)
            {
                var position = player.Position;
                foreach (var player2 in __instance.TargetedPlayers.AsManaged().Concat(__instance.NotTargetedPlayers.AsManaged()).ToList())
                {
                    DoReplicatedMineDamage(player2, Vector3.Distance(position, player2.Position), first,
                        player != player2, __instance._collateralContusionRange, __instance._collateralDamageRange,
                        __instance._firstExplosionDamage, __instance._secondExplosionDamage, __instance);
                }
            }
            return false;
        }
        return true;
    }

    private static void DoReplicatedMineDamage(IPlayer player, float distance, bool first, bool isCollateral,
        float collateralContusionRange, float collateralDamageRange, float firstExplosionDamage,
        float secondExplosionDamage, Minefield minefield)
    {
        if (isCollateral && distance > collateralContusionRange)
        {
            return;
        }

        var fikaPlayer = Singleton<GameWorld>.Instance.GetAlivePlayerByProfileID(player.ProfileId)?.TryCast<FikaPlayer>();
        if (isCollateral && distance > collateralDamageRange)
        {
            return;
        }

        if (fikaPlayer != null)
        {
            var num2 = 1f - distance / collateralDamageRange;
            var targets = isCollateral ? minefield._collateralDamageTarget : minefield._explosionTarget;
            var enumerable = player.PlayerBones.BodyPartColliders.Where(collider => targets.Contains(collider.BodyPartType));

            enumerable = enumerable.DistinctBy(FikaGlobals.GetBodyPartFromCollider).ToArray();
            enumerable = enumerable.ToArray().OrderBy(_ => Random.value);

            var num3 = isCollateral || first ? Random.Range(2, enumerable.Count()) : int.MaxValue;
            var num4 = isCollateral || first ? firstExplosionDamage : secondExplosionDamage;
            var num5 = 0;

            foreach (var bodyPartCollider in enumerable)
            {
                fikaPlayer.CommonPacket.Type = ECommonSubPacketType.Damage;
                fikaPlayer.CommonPacket.SubPacket = DamagePacket.FromValue(fikaPlayer.NetId, new()
                {
                    DamageType = EDamageType.Landmine,
                    Damage = num4 * num2,
                    ArmorDamage = 0.5f,
                    PenetrationPower = 30f,
                    Direction = default,
                    HitNormal = default
                }, bodyPartCollider.BodyPartType, bodyPartCollider.BodyPartColliderType);
                FikaSingleton<IFikaNetworkManager>.Instance.SendNetReusable(ref fikaPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
                if (++num5 >= num3)
                {
                    break;
                }
            }
        }
        else
        {
            FikaGlobals.LogError($"DoReplicatedMineDamage: Could not find player with ProfileId: {player.ProfileId}, Nickname: {player.Profile.Nickname}!");
        }
    }
}
