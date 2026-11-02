using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Vehicle;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.BTR;

public class BTRSide_Patches
{
    public static List<ValueTuple<BTRSide, Player, int>> Passengers = [];

    /// <summary>
    /// 在世界拆除时丢弃乘客列表（analysis/transit-crash-unload）：每个条目都是一个原生
    /// BTRSide 和一个原生 Player，即刚结束的那个世界的对象。返回数量。
    /// </summary>
    internal static int ClearForWorldTeardown()
    {
        var count = Passengers.Count;
        if (count > 0)
        {
            Passengers.Clear();
        }

        return count;
    }

    public class BTRSide_AddPassenger_Patch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BTRSide), nameof(BTRSide.AddPassenger), [typeof(Player), typeof(int)]);
        }

        [PatchPrefix]
        public static bool Prefix(BTRSide __instance, Player player, int placeId)
        {
            if (FikaBackendUtils.ClientType == EClientType.None) return true;
            if (player == null || player.IsYourPlayer)
            {
                return true;
            }

            // AddPassenger 可能在一次重连快照或一次延迟的
            // 交互响应之后被重放。一个 BTR 乘客只属于一个侧面，
            // 所以在记录新的权威槽位之前，先在每一个侧面上
            // 移除这个玩家的过期条目。
            for (var i = Passengers.Count - 1; i >= 0; i--)
            {
                if (Passengers[i].Item2?.Pointer == player.Pointer)
                    Passengers.RemoveAt(i);
            }

            player.Transform.Original.parent = __instance._startPoint;
            __instance.ResetPlayerTransform(player);
            Passengers.Add(new ValueTuple<BTRSide, Player, int>(__instance, player, placeId));
            __instance.ApplyLocalPassenger();
            return false;
        }
    }

    public class BTRSide_RemovePassenger_Patch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BTRSide), nameof(BTRSide.RemovePassenger), [typeof(Player)]);
        }

        [PatchPrefix]
        public static bool Prefix(BTRSide __instance, Player player)
        {
            if (FikaBackendUtils.ClientType == EClientType.None) return true;
            if (player == null || player.IsYourPlayer)
            {
                return true;
            }

            for (var i = Passengers.Count - 1; i >= 0; i--)
            {
                var tuple = Passengers[i];
                if (tuple.Item1?.Pointer == __instance.Pointer && tuple.Item2?.Pointer == player.Pointer)
                {
                    Passengers.RemoveAt(i);
                }
            }

            // 针对某个侧面的延迟移除，绝不能把一个已经在另一个侧面上
            // 被重新加入的乘客分离出去。只有当这个玩家已无权威缓存条目
            // 时才清除父引用。
            var stillPassenger = false;
            for (var i = 0; i < Passengers.Count; i++)
            {
                if (Passengers[i].Item2?.Pointer == player.Pointer)
                {
                    stillPassenger = true;
                    break;
                }
            }

            if (!stillPassenger)
                player.Transform.Original.parent = null;
            return false;
        }
    }

    public class BTRSide_ApplyLocalPassenger_Patch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(BTRSide).GetMethod(nameof(BTRSide.ApplyLocalPassenger));
        }

        [PatchPrefix]
        public static void Prefix(BTRSide __instance)
        {
            if (FikaBackendUtils.ClientType == EClientType.None) return;
            for (var i = Passengers.Count - 1; i >= 0; i--)
            {
                var tuple = Passengers[i];
                if (tuple.Item1?.Pointer == __instance.Pointer)
                {
                    if (tuple.Item2 == null)
                    {
                        Passengers.RemoveAt(i);
                        continue;
                    }

                    switch (tuple.Item2.BtrState)
                    {
                        case EPlayerBtrState.Approach:
                        case EPlayerBtrState.GoIn:
                        case EPlayerBtrState.GoOut:
                            {
                                tuple.Item2.Teleport(__instance._startPoint.position);
                                var points = __instance.GoInPoints();
                                __instance.ApplyPlayerRotation(tuple.Item2.MovementContext, points.Item1, points.Item2);
                                break;
                            }
                        case EPlayerBtrState.Inside:
                            {
                                tuple.Item2.Teleport(__instance.PlacePosition(tuple.Item3));
                                var points = __instance.GoInPoints();
                                __instance.ApplyPlayerRotation(tuple.Item2.MovementContext, points.Item1, points.Item2);
                                var num = Mathf.Lerp(0f, 0.05f, Mathf.InverseLerp(0f, __instance.BtrView.MoveSpeed, __instance.BtrView.CurrentSpeed));
                                if (num > Mathf.Epsilon)
                                {
                                    tuple.Item2.ProceduralWeaponAnimation.ForceReact.HardShake(num);
                                }
                            }
                            break;
                        default:
                            continue;
                    }
                }
            }
        }
    }
}
