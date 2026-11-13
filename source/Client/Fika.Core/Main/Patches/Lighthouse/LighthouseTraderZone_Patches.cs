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
            player.OnPlayerDead += NativeDelegates.Bind<OnPlayerDead>(__instance, nameof(LighthouseTraderZone.OnPlayerDieInZone));
            var flag = player.RecodableItemsHandler.TryToGetRecodableComponent(out RadioTransmitterRecodableComponent radioTransmitterRecodableComponent);

            if (player.IsAI && __instance.IsValidAiPlayer(player))
            {
                allowedPlayers.Add(player);
                allPlayers.Add(player);

                if (coopHandler.MyPlayer == player)
                {
                    //Todo: Might have to patch SetAgressor as the host and other clients might need to know IsAgressorInLighthouseTraderZone
                    player.ActiveHealthController.OnApplyDamageByPlayer += NativeDelegates.Bind<Il2CppSystem.Action<Player, IPlayer>>(__instance, nameof(LighthouseTraderZone.SetAgressor));
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
                LighthouseTraderZone.OnPlayerAllowStatusChanged?.Invoke(player.RaidId, false);
            }
            else
            {
                allowedPlayers.Add(player);
                radioTransmitterRecodableComponent.OnRadioTransmitterStatusChanged += NativeDelegates.Bind<Il2CppSystem.Action<RadioTransmitterStatus>>(__instance, nameof(LighthouseTraderZone.OnPlayerChangeRadioTransmitterStatus));

                if (coopHandler.MyPlayer == player)
                {
                    //Todo: Might have to patch SetAgressor as the host and other clients might need to know IsAgressorInLighthouseTraderZone
                    player.ActiveHealthController.OnApplyDamageByPlayer += NativeDelegates.Bind<Il2CppSystem.Action<Player, IPlayer>>(__instance, nameof(LighthouseTraderZone.SetAgressor));
                }

                LighthouseTraderZone.OnPlayerAllowStatusChanged?.Invoke(player.RaidId, true);
            }

            allPlayers.Add(player);

            // Skip original method
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

            player.OnPlayerDead -= NativeDelegates.Bind<OnPlayerDead>(__instance, nameof(LighthouseTraderZone.OnPlayerDieInZone));
            player.RecodableItemsHandler.TryToGetRecodableComponent(out RadioTransmitterRecodableComponent radioTransmitterRecodableComponent);

            if (__instance.allowedPlayers.Contains(player))
            {
                __instance.allowedPlayers.Remove(player);

                if (radioTransmitterRecodableComponent != null)
                {
                    radioTransmitterRecodableComponent.OnRadioTransmitterStatusChanged -= NativeDelegates.Bind<Il2CppSystem.Action<RadioTransmitterStatus>>(__instance, nameof(LighthouseTraderZone.OnPlayerChangeRadioTransmitterStatus));
                }

                if (coopHandler.MyPlayer == player)
                {
                    player.ActiveHealthController.OnApplyDamageByPlayer -= NativeDelegates.Bind<Il2CppSystem.Action<Player, IPlayer>>(__instance, nameof(LighthouseTraderZone.SetAgressor));
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
