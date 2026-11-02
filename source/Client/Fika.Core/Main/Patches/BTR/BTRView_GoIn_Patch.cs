using EFT.GlobalEvents;
using System;
using System.Reflection;
using System.Threading.Tasks;
using Audio.Vehicles.BTR;
using Comfort.Common;
using EFT;
using EFT.Vehicle;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.World;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.BTR;

public class BTRView_GoIn_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BTRView).GetMethod(nameof(BTRView.GoIn),
            [typeof(Player), typeof(BTRSide), typeof(byte), typeof(bool)]);
    }

    [PatchPrefix]
    public static bool Prefix(BTRView __instance, Player player, BTRSide side, byte placeId, bool fast, ref Il2CppSystem.Threading.Tasks.Task __result)
    {
        if (FikaBackendUtils.ClientType == EClientType.None) return true;
        var isServer = FikaBackendUtils.IsServer;
        if (player.TryCast<ObservedPlayer>() is { } observedPlayer)
        {
            var network = FikaSingleton<IFikaNetworkManager>.Instance;
            // 在启动异步任务之前先暂停常规的移动更新。
            // 执行很快或失败的任务可能在第一次 await 之前就同步结束。
            network?.ObservedPlayers.RemoveAll(current => current?.Pointer == observedPlayer.Pointer);
            __result = NativeTask.FromManaged(ObservedGoIn(__instance, observedPlayer, side, placeId, fast, network));
            return false;
        }

        if (player.IsYourPlayer && player.TryCast<FikaPlayer>() is { } myPlayer)
        {
            myPlayer.PacketSender.SendState = false;
            player.InputDirection = new(0, 0);
            if (isServer)
            {
                BTRInteractionPacket packet = new(myPlayer.NetId)
                {
                    Data = new()
                    {
                        HasInteraction = true,
                        InteractionType = EInteractionType.GoIn,
                        SideId = __instance.GetSideId(side),
                        SlotId = placeId,
                        Fast = fast
                    }
                };

                FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            }
        }

        return true;
    }

    private static async Task ObservedGoIn(BTRView view, ObservedPlayer observedPlayer, BTRSide side, byte placeId, bool fast,
        IFikaNetworkManager network)
    {
        var addedPassenger = false;
        try
        {
            var cancellationToken = view.PlayerToken(observedPlayer);
            observedPlayer.MovementContext.IsAxesIgnored = true;
            observedPlayer.BtrState = EPlayerBtrState.Approach;
            if (!fast)
            {
                var valueTuple = side.GoInPoints();
                await side.ProcessApproach(observedPlayer, valueTuple.Item1, valueTuple.Item2 + Vector3.up * 1.4f);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
            observedPlayer.HideWeapon();
            observedPlayer.CharacterController.isEnabled = false;
            observedPlayer.BtrState = EPlayerBtrState.GoIn;
            side.AddPassenger(observedPlayer, placeId);
            addedPassenger = true;
            var soundController = view._soundController;
            if (soundController != null)
            {
                soundController.UpdateBtrAudioRoom(EnvironmentType.Indoor, observedPlayer);
            }
            await view.GoInAnimation(observedPlayer.MovementContext.PlayerAnimator, fast, true, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                if (view.GetBusyPlacesCount() == 1)
                {
                    GlobalEventsController.CreateEvent<BtrFirstPassengerGoInEvent>().Invoke(observedPlayer.Side);
                }
                observedPlayer.BtrState = EPlayerBtrState.Inside;
            }
        }
        catch (Exception ex)
        {
            FikaGlobals.LogError("BTRView_GoIn_Patch: " + ex.Message);
        }
        finally
        {
            // 被中断的接近过程或动画永远不会走到 GoOut，而 GoOut 通常
            // 会把该玩家重新加回来。不要在战局余下的时间里让他的快照更新
            // 一直处于禁用状态。GoOut 自己负责它那段状态切换。
            if (observedPlayer.BtrState != EPlayerBtrState.Inside &&
                observedPlayer.BtrState != EPlayerBtrState.GoOut &&
                observedPlayer.BtrState != EPlayerBtrState.Outside)
            {
                if (observedPlayer.HealthController?.IsAlive == true)
                {
                    try
                    {
                        if (addedPassenger) side.RemovePassenger(observedPlayer);
                        observedPlayer.CharacterController.isEnabled = true;
                        observedPlayer.MovementContext.IsAxesIgnored = false;
                        observedPlayer.BtrState = EPlayerBtrState.Outside;
                        view._soundController?.UpdateBtrAudioRoom(EnvironmentType.Outdoor, observedPlayer);
                    }
                    catch (Exception ex) { FikaGlobals.LogError("BTR interrupted boarding cleanup: " + ex.Message); }
                }
                RestoreObservedUpdates(network, observedPlayer);
            }
        }
    }

    internal static void RestoreObservedUpdates(IFikaNetworkManager network, ObservedPlayer player)
    {
        if (network == null || player == null ||
            !ReferenceEquals(FikaSingleton<IFikaNetworkManager>.Instance, network) ||
            player.HealthController?.IsAlive != true) return;
        var players = network.CoopHandler?.Players;
        if (players == null || !players.TryGetValue(player.NetId, out var current) ||
            current?.Pointer != player.Pointer) return;
        var observed = network.ObservedPlayers;
        if (observed != null && !observed.Exists(candidate => candidate?.Pointer == player.Pointer))
            observed.Add(player);
    }
}
