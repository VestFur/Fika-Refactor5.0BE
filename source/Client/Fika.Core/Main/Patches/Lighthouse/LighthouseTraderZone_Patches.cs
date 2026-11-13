using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Lighthouse;

public static class LighthouseTraderZone_Patches
{
    public class LighthouseTraderZone_AddPlayer_Patch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(LighthouseTraderZone).GetMethod(nameof(LighthouseTraderZone.AddPlayer));
        }

        [PatchPrefix]
        public static bool Prefix(Player player, LighthouseTraderZone __instance)
        {
            if (FikaBackendUtils.ClientType == EClientType.None) return true;
            if (!CoopHandler.TryGetCoopHandler(out var coopHandler))
            {
                return false;
            }

            var allPlayers = __instance.allPlayersInZone;
            var allowedPlayers = __instance.allowedPlayers;
            var unallowedPlayers = __instance.unallowedPlayers;
            if (allPlayers.Contains(player)) return false;
            if (NativeDelegates.TryBindManaged<System.Action<EFT.Player, EFT.IPlayer, EFT.Ballistics.DamageInfo, EBodyPart>>(
                    __instance, nameof(LighthouseTraderZone.OnPlayerDieInZone), "LighthouseTraderZone.OnPlayerDead", out var dieInZone))
                {
                    player.OnPlayerDead += dieInZone;
                }
            var flag = player.RecodableItemsHandler.TryToGetRecodableComponent(out RadioTransmitterRecodableComponent radioTransmitterRecodableComponent);

            if (player.IsAI && __instance.IsValidAiPlayer(player))
            {
                allowedPlayers.Add(player);
                allPlayers.Add(player);

                if (coopHandler.MyPlayer == player)
                {
                    //Todo: 可能需要 patch SetAgressor，因为主机与其他客户端可能需要知道 IsAgressorInLighthouseTraderZone
                    if (NativeDelegates.TryBindManaged<System.Action<Player, IPlayer>>(
                        __instance, nameof(LighthouseTraderZone.SetAgressor), "LighthouseTraderZone.SetAgressor", out var agressor))
                    {
                        player.ActiveHealthController.OnApplyDamageByPlayer += agressor;
                    }
                }

                return false;
            }
            if (!flag)
            {
                unallowedPlayers.Add(player);
                allPlayers.Add(player);
                return false;
            }
            if (!__instance.IsValidPlayer(radioTransmitterRecodableComponent.Handler))
            {
                unallowedPlayers.Add(player);
                // 47888 把 LighthouseTraderZone.OnPlayerAllowStatusChanged 暴露为事件，而该事件无法
                // 从外部触发。改为通知我们自己的订阅者，而不走该区域事件。
                FikaGlobals.LogInfo($"[Lighthouse] player {player.RaidId} is no longer allowed in the trader zone (the 47888 event cannot be raised from outside)");
            }
            else
            {
                allowedPlayers.Add(player);
                radioTransmitterRecodableComponent.OnRadioTransmitterStatusChanged += NativeDelegates.BindManaged<System.Action<RadioTransmitterStatus>>(__instance, nameof(LighthouseTraderZone.OnPlayerChangeRadioTransmitterStatus));

                if (coopHandler.MyPlayer == player)
                {
                    //Todo: 可能需要 patch SetAgressor，因为主机与其他客户端可能需要知道 IsAgressorInLighthouseTraderZone
                    if (NativeDelegates.TryBindManaged<System.Action<Player, IPlayer>>(
                        __instance, nameof(LighthouseTraderZone.SetAgressor), "LighthouseTraderZone.SetAgressor", out var agressor))
                    {
                        player.ActiveHealthController.OnApplyDamageByPlayer += agressor;
                    }
                }

                // 47888 把 LighthouseTraderZone.OnPlayerAllowStatusChanged 暴露为事件，而该事件无法
                // 从外部触发。改为通知我们自己的订阅者，而不走该区域事件。
                FikaGlobals.LogInfo($"[Lighthouse] player {player.RaidId} is allowed in the trader zone again (the 47888 event cannot be raised from outside)");
            }

            allPlayers.Add(player);

            // 跳过原始方法
            return false;
        }
    }

    public class LighthouseTraderZone_RemovePlayer_Patch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(LighthouseTraderZone).GetMethod(nameof(LighthouseTraderZone.RemovePlayer));
        }

        [PatchPrefix]
        public static bool Prefix(Player player, LighthouseTraderZone __instance)
        {
            if (FikaBackendUtils.ClientType == EClientType.None) return true;
            if (!CoopHandler.TryGetCoopHandler(out var coopHandler))
            {
                return false;
            }

            player.OnPlayerDead -= NativeDelegates.BindManaged<System.Action<EFT.Player, EFT.IPlayer, EFT.Ballistics.DamageInfo, EBodyPart>>(__instance, nameof(LighthouseTraderZone.OnPlayerDieInZone));
            player.RecodableItemsHandler.TryToGetRecodableComponent(out RadioTransmitterRecodableComponent radioTransmitterRecodableComponent);

            if (__instance.allowedPlayers.Contains(player))
            {
                __instance.allowedPlayers.Remove(player);

                if (radioTransmitterRecodableComponent != null)
                {
                    radioTransmitterRecodableComponent.OnRadioTransmitterStatusChanged -= NativeDelegates.BindManaged<System.Action<RadioTransmitterStatus>>(__instance, nameof(LighthouseTraderZone.OnPlayerChangeRadioTransmitterStatus));
                }

                if (coopHandler.MyPlayer == player)
                {
                    player.ActiveHealthController.OnApplyDamageByPlayer -= NativeDelegates.BindManaged<System.Action<Player, IPlayer>>(__instance, nameof(LighthouseTraderZone.SetAgressor));
                }
            }
            else
            {
                __instance.unallowedPlayers.Remove(player);
            }

            __instance.allPlayersInZone.Remove(player);

            return false;
        }
    }

    public class LighthouseTraderZone_Awake_Patch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(LighthouseTraderZone).GetMethod(nameof(LighthouseTraderZone.Awake));
        }

        [PatchPrefix]
        public static bool Prefix(LighthouseTraderZone __instance)
        {
            if (FikaBackendUtils.ClientType == EClientType.None) return true;
            if (FikaBackendUtils.IsClient)
            {
                UnityEngine.Object.Destroy(__instance);
                return false;
            }
            return true;
        }
    }
}