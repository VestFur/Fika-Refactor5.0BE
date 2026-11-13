using Fika.Core.Main.BaseClasses;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using CommonAssets.Scripts.Game;
using JsonType;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.GlobalEvents;
using EFT.Interactive;
using EFT.UI;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Communication;

namespace Fika.Core.Main.HostClasses;

public class FikaHostTransitController : FikaWorldController<LocalTransitController>
{
    public FikaHostTransitController(TransitGlobalSettings settings, Il2CppReferenceArray<LocationSettings.Location.TransitParameters> parameters, Profile profile, LocalRaidSettings localRaidSettings)
        : base(new LocalTransitController(settings, parameters, profile, localRaidSettings))
    {
        IsEvent = localRaidSettings.transitionType.HasFlagNoBox(ELocationTransition.Event);
        var array = FikaTransitHistory.AppendLocation(localRaidSettings);
        Native.summonedTransits[profile.Id] = new(localRaidSettings.transition.transitionRaidId, localRaidSettings.transition.transitionCount,
            array, IsEvent);
        Native.TransferItemsController.InitItemControllerServer(FikaGlobals.TransitTraderId, FikaGlobals.TransitTraderName);
        _server = FikaSingleton<FikaServer>.Instance;
        _playersInTransitZone = [];
        _transittedPlayers = [];
    }

    private readonly FikaServer _server;
    private readonly Dictionary<string, int> _playersInTransitZone;
    private readonly HashSet<int> _transittedPlayers;

    public bool IsEvent { get; }

    public int AliveTransitPlayers
    {
        get
        {
            return _transittedPlayers.Count;
        }
    }

    internal void OnHostPlayerEnter(TransitPoint point, Player player)
    {
        FikaGlobals.LogWarning($"[TransitDiag] PlayerEnter point={point?.parameters?.id} player={player?.ProfileId} your={player?.IsYourPlayer}");
        if (!Native.TryGetAccessToLocation(player, point.parameters.id, out var _))
        {
            if (player.IsYourPlayer)
            {
                Native.AccessNotGrantedNotification();
            }
            return;
        }


        _playersInTransitZone[player.ProfileId] = point.parameters.id;

        if (!Native.transitPlayers.ContainsKey(player.ProfileId))
        {
            if (player.TryCast<FikaPlayer>() is { } fikaPlayer)
            {
                fikaPlayer.UpdateBtrTraderServiceData()
                    .HandleExceptions();
            }

            if (player.IsYourPlayer)
            {
                Native.ShowInteraction(point.parameters.id, player, Native.GetSelectedTime());
                return;
            }

            TransitEventPacket packet = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Interaction,
                TransitEvent = new TransitInteractionEvent()
                {
                    PlayerRaidId = player.RaidId,
                    PointId = point.parameters.id,
                    Type = TransitInteractionEvent.EType.Show
                }
            };

            _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            return;
        }
        Native.pointsById[point.parameters.id].GroupEnter(player);
    }

    internal void OnHostPlayerExit(TransitPoint point, Player player)
    {
        if (_playersInTransitZone.TryGetValue(player.ProfileId, out var value))
        {
            if (value == point.parameters.id)
            {
                _playersInTransitZone.Remove(player.ProfileId);
            }
        }

        if (Native.transitPlayers.ContainsKey(player.ProfileId))
        {
            point.GroupExit(player);
        }
        if (player.IsYourPlayer)
        {
            Native.Cancel(player);
            return;
        }

        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.Interaction,
            TransitEvent = new TransitInteractionEvent()
            {
                PlayerRaidId = player.RaidId,
                PointId = point.parameters.id,
                Type = TransitInteractionEvent.EType.Hide
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    public void Sizes(int pointId, Il2CppSystem.Collections.Generic.Dictionary<int, byte> sizes)
    {
        Native._currentTransitPointId = pointId;
#if DEBUG
        foreach (var item in sizes)
        {
            FikaGlobals.LogWarning($"int: {item.Key}, byte: {item.Value}");
        }
#endif

        foreach (var size in sizes)
        {
            if (!FikaBackendUtils.IsHeadless && GamePlayerOwner.MyPlayer?.RaidId == size.Key && MonoBehaviourSingleton<GameUI>.Instance != null)
            {
                MonoBehaviourSingleton<GameUI>.Instance.LocationTransitGroupSize.Display();
                MonoBehaviourSingleton<GameUI>.Instance.LocationTransitGroupSize.Show(size.Value, Native.GetCurrentPointMaxGroupSize());
            }
        }

        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.GroupSize,
            TransitEvent = new TransitGroupSizeEvent()
            {
                PointId = pointId,
                Sizes = sizes
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    public void Timers(int pointId, Il2CppSystem.Collections.Generic.Dictionary<int, ushort> timers)
    {
        Native._currentTransitPointId = pointId;
#if DEBUG
        foreach (var item in timers)
        {
            FikaGlobals.LogWarning($"int: {item.Key}, ushort: {item.Value}");
        }
#endif

        foreach (var timer in timers)
        {
            if (!FikaBackendUtils.IsHeadless && GamePlayerOwner.MyPlayer?.RaidId == timer.Key && MonoBehaviourSingleton<GameUI>.Instance != null)
            {
                Native.ShowPanel(pointId);
                MonoBehaviourSingleton<GameUI>.Instance.LocationTransitTimerPanel.Display();
                MonoBehaviourSingleton<GameUI>.Instance.LocationTransitTimerPanel.Show((float)timer.Value);
            }
        }

        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.GroupTimer,
            TransitEvent = new TransitGroupTimerEvent()
            {
                PointId = pointId,
                Timers = timers
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    public void InactivePointNotification(int playerId, int pointId)
    {
        if (!FikaBackendUtils.IsHeadless && GamePlayerOwner.MyPlayer?.RaidId == playerId)
        {
            NotificationManager.DisplayWarningNotification("Transit/InactivePoint".Localized(null), ENotificationDurationType.Default);
            Native.ShowPanel(pointId);
            return;
        }

        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.Interaction,
            TransitEvent = new TransitInteractionEvent()
            {
                PlayerRaidId = playerId,
                PointId = pointId,
                Type = TransitInteractionEvent.EType.InactivePoint
            }
        };

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    public void InteractWithTransit(Player player, InteractWithTransitPacket packet)
    {
        if (player == null || packet == null || !packet.hasInteraction || player.HealthController?.IsAlive != true)
        {
            FikaGlobals.LogWarning($"[TransitDiag] InteractWithTransit rejected: invalid player/packet player={player?.ProfileId} hasInteraction={packet?.hasInteraction} alive={player?.HealthController?.IsAlive}");
            return;
        }
        if (!Native.pointsById.TryGetValue(packet.pointId, out var point))
        {
            FikaGlobals.LogWarning($"[TransitDiag] InteractWithTransit rejected: unknown point {packet.pointId} player={player.ProfileId}");
            return;
        }
        if (!point.Enabled)
        {
            FikaGlobals.LogWarning($"[TransitDiag] InteractWithTransit rejected: point {packet.pointId} is not enabled, player={player.ProfileId}");
            return;
        }
        if (!_playersInTransitZone.TryGetValue(player.ProfileId, out var currentPoint))
        {
            FikaGlobals.LogWarning($"[TransitDiag] InteractWithTransit rejected: player {player.ProfileId} never entered the transit zone (point {packet.pointId})");
            return;
        }
        if (currentPoint != packet.pointId)
        {
            FikaGlobals.LogWarning($"[TransitDiag] InteractWithTransit rejected: player {player.ProfileId} zone {currentPoint} != point {packet.pointId}");
            return;
        }
        if (Native.transitPlayers.ContainsKey(player.ProfileId))
        {
            FikaGlobals.LogWarning($"[TransitDiag] InteractWithTransit rejected: player {player.ProfileId} is already registered for a transit");
            return;
        }
        if (!Native.TryGetAccessToLocation(player, packet.pointId, out var keyId))
        {
            FikaGlobals.LogWarning($"[TransitDiag] InteractWithTransit rejected: access to point {packet.pointId} not granted for {player.ProfileId}");
            return;
        }
        if (!EFT.ClientTransitController.HasMetEntryRequirements(point, player))
        {
            FikaGlobals.LogWarning($"[TransitDiag] InteractWithTransit rejected: entry requirements of point {packet.pointId} not met for {player.ProfileId}");
            return;
        }
        // The quest condition is already validated for each player by the client that
        // shows the interaction prompt and by EFT.ClientTransitController.HasMetEntryRequirements
        // above, while this extra native lookup answered false for every profile and
        // blocked every transit. It is logged instead of enforced. Known gap: the port
        // does not drive the native countdown/transition stage yet, so a transit starts
        // without the countdown UI.
        if (!Native.HasMetCompletedQuestRequirement(point, player))
        {
            FikaGlobals.LogWarning($"[TransitDiag] InteractWithTransit: completed-quest requirement of point {packet.pointId} " +
                $"reported false for {player.ProfileId}, continuing because the client already validated it");
        }
        FikaGlobals.LogWarning($"[TransitDiag] InteractWithTransit accepted point={packet.pointId} player={player.ProfileId} your={player.IsYourPlayer} key={keyId}");

        Native.profileTime[player.ProfileId] = packet.time;
        if (!CheckForPlayers(player, packet.pointId))
        {
            return;
        }

        if (player.IsYourPlayer)
        {
            // Native.Cancel(player) used to run here and cancelled the native transit
            // interaction right after accepting it. That interaction state owns the
            // countdown UI, which is why neither the host nor the clients ever saw a
            // transit countdown while the transfer itself still completed. The
            // bookkeeping below is what actually drives the transfer, so the cancel is
            // not needed; the notification is still hidden.
            Native.HideTransitInteractionNotification();
            Native.transitPlayers[player.ProfileId] = player.RaidId;
            Native.profileKeys[player.ProfileId] = keyId;
            Native.pointsById[packet.pointId].GroupEnter(player);
            ExfiltrationController.Instance.BannedPlayers.Add(player.RaidId);
            ExfiltrationController.Instance.CancelExtractionForPlayer(player);
            ExfiltrationController.Instance.DisableExitsInteraction();
            return;
        }

        Native.transitPlayers[player.ProfileId] = player.RaidId;
        Native.profileKeys[player.ProfileId] = keyId;
        Native.pointsById[packet.pointId].GroupEnter(player);
        ExfiltrationController.Instance.BannedPlayers.Add(player.RaidId);
        ExfiltrationController.Instance.CancelExtractionForPlayer(player);

        // The remote native NetworkTransitController must receive the same
        // confirmation that the local controller gets from its interaction
        // action.  Without it the client can show a timer but still has no
        // transit-player/anti-leave state and may open a normal exit again.
        var confirm = new TransitEventPacket
        {
            EventType = TransitEventPacket.ETransitEventType.Interaction,
            TransitEvent = new TransitInteractionEvent
            {
                PlayerRaidId = player.RaidId,
                PointId = packet.pointId,
                Type = TransitInteractionEvent.EType.Confirm
            }
        };
        _server.SendData(ref confirm, DeliveryMethod.ReliableOrdered);
    }

    private bool CheckForPlayers(Player player, int pointId)
    {
        var humanPlayers = 0;
        foreach (var fikaPlayer in FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler.HumanPlayers)
        {
            // A player who has already completed this transition is removed
            // from the active raid on the next frame.  During that small
            // window do not make the remaining group wait for an extracted
            // member to enter the point.
            if (fikaPlayer.HealthController.IsAlive &&
                !_transittedPlayers.Contains(fikaPlayer.RaidId) &&
                !FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler.ExtractedPlayers.Contains(fikaPlayer.NetId))
            {
                if (fikaPlayer.IsYourPlayer && FikaBackendUtils.IsHeadless)
                {
                    continue;
                }

                humanPlayers++;
            }
        }

        var playersInPoint = 0;
        foreach (var member in FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler.HumanPlayers)
        {
            if (member.HealthController?.IsAlive == true &&
                !_transittedPlayers.Contains(member.RaidId) &&
                !FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler.ExtractedPlayers.Contains(member.NetId) &&
                !(member.IsYourPlayer && FikaBackendUtils.IsHeadless) &&
                _playersInTransitZone.TryGetValue(member.ProfileId, out var zone) && zone == pointId)
                playersInPoint++;
        }
        if (playersInPoint < humanPlayers)
        {
            if (player.IsYourPlayer)
            {
                NotificationManager.DisplayWarningNotification(TransitMessagesEvent.EType.NonAllTeammates.ToString(), ENotificationDurationType.Default);
                return false;
            }

            Il2CppSystem.Collections.Generic.Dictionary<int, TransitMessagesEvent.EType> messages = new();
            messages.Add(player.RaidId, TransitMessagesEvent.EType.NonAllTeammates);

            TransitEventPacket messagePacket = new()
            {
                EventType = TransitEventPacket.ETransitEventType.Messages,
                TransitEvent = new TransitMessagesEvent()
                {
                    Messages = messages
                }
            };

            _server.SendData(ref messagePacket, DeliveryMethod.ReliableOrdered);
            return false;
        }

        return true;
    }

    public TransitEventPacket CreateInitialization(Player player)
    {
        if (player == null || !Native.summonedTransits.TryGetValue(player.ProfileId, out var history)) return null;
        var points = new Il2CppSystem.Collections.Generic.Dictionary<int, LocationSettings.Location.TransitParameters>();
        var requirements = new Il2CppSystem.Collections.Generic.Dictionary<int, bool>();
        var enabled = new List<int>();
        var registeredPoint = -1;
        foreach (var entry in Native.pointsById)
        {
            if (entry.Value.parameters.active) points.Add(entry.Key, entry.Value.parameters);
            // Raw native result disabled the point in every client UI, so report it as
            // met; the per-player validation stays with the client and with
            // EFT.ClientTransitController.HasMetEntryRequirements on the host.
            requirements.Add(entry.Key, true);
            if (entry.Value.Enabled) enabled.Add(entry.Key);
            if (Native.transitPlayers.ContainsKey(player.ProfileId) && entry.Value._playerGroups.ContainsKey(player.ProfileId))
                registeredPoint = entry.Key;
        }
        Native.profileKeys.TryGetValue(player.ProfileId, out var key);
        var time = Native.profileTime.TryGetValue(player.ProfileId, out var selected) ? selected : Native._localRaidSettings.timeVariant;
        return new TransitEventPacket
        {
            EventType = TransitEventPacket.ETransitEventType.Init,
            TransitEvent = new TransitInitEvent
            {
                PlayerRaidId = player.RaidId, Points = points, CompletedQuestRequirementMetByPointId = requirements,
                TransitionCount = (ushort)history.count, EventPlayer = history.events
            },
            EnabledPointIds = enabled.ToArray(), RegisteredPointId = registeredPoint, KeyId = key, SelectedTime = time
        };
    }

    public void Transit(TransitPoint point, int playersCount, string hash, Il2CppSystem.Collections.Generic.Dictionary<string, ProfileKey> keys, Player player)
    {
        if (_transittedPlayers.Contains(player.RaidId) ||
            !Native.summonedTransits.TryGetValue(player.ProfileId, out var history)) return;
        var destination = new LocationTransit
        {
            hash = hash, playersCount = playersCount, ip = "", location = point.parameters.location,
            profiles = keys, transitionRaidId = history.raidId, raidMode = ERaidMode.Local,
            side = player.Side == EPlayerSide.Savage ? ESideType.Savage : ESideType.Pmc,
            dayTime = Native.profileTime.TryGetValue(player.ProfileId, out var time) ? time : Native._localRaidSettings.timeVariant
        };
        if (player.IsYourPlayer)
        {
            var fikaPlayer = player.TryCast<FikaPlayer>();
            if (fikaPlayer == null || FikaSingleton<IFikaGame>.Instance is not CoopGame game) return;
            if (TarkovApplication.Exist(out var application))
                application.TransitionStatus = new(destination.location, false, destination.side, destination.raidMode, destination.dayTime);
            Native.alreadyTransits[player.ProfileId] = destination;
            _transittedPlayers.Add(player.RaidId);
            game.Extract(fikaPlayer, null, point);

            // Extract() only extracts the player. A client ends the raid itself in
            // ClientGameController.Extract, but the host never did: it stayed in the half-extracted
            // state (gravity switched off, teleported 500 m up) with the raid still running, which is
            // exactly why the host never loaded the next map and could not move afterwards.
            var stopGame = game;
            var stopProfile = fikaPlayer.ProfileId;
            var stopPointName = point.parameters.name;
            MainThreadDispatcher.RunAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(4000);
                MainThreadDispatcher.Post(() =>
                {
                    if (ReferenceEquals(FikaSingleton<IFikaGame>.Instance, stopGame))
                    {
                        FikaGlobals.LogWarning($"[TransitDiag] host raid stop after its own transit (point={stopPointName})");
                        stopGame.Stop(stopProfile, ExitStatus.Transit, stopPointName, 0f);
                    }
                });
            }).Forget();
            return;
        }
        var packet = new TransitEventPacket
        {
            EventType = TransitEventPacket.ETransitEventType.Extract, PlayerId = player.RaidId,
            TransitId = point.parameters.id, Destination = destination
        };
        Native.alreadyTransits[player.ProfileId] = destination;
        _transittedPlayers.Add(player.RaidId);
        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    protected override void DisposeNative() => Native.Dispose();

    public void Init()
    {
        var enabledFlags = string.Empty;
        foreach (var entry in Native.pointsById)
        {
            enabledFlags += entry.Key + ":" + entry.Value.Enabled + " ";
        }
        FikaGlobals.LogWarning($"[TransitDiag] Host transit Init: points={Native.pointsById.Count} flags={enabledFlags}");
        Native.EnablePoints(true);
        if (!FikaBackendUtils.IsHeadless && GamePlayerOwner.MyPlayer is { } player)
        {
            Native.SetTimers(Native.pointsById.Values, player, false);
            Native.HandleExits(Native.pointsById.Values, player);
        }
    }
}
