using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Comfort.Common;
using CommonAssets.Scripts.Game;
using EFT;
using EFT.GlobalEvents;
using EFT.Interactive;
using EFT.InventoryLogic;
using Fika.Core;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Communication;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using JsonType;

namespace Fika.Headless.Classes;

/// <summary>
/// Transit controller for the <see cref="GameMode.HeadlessGame"/>
/// </summary>
public class FikaHeadlessTransitController : TransitController
{
    public FikaHeadlessTransitController(IntPtr pointer) : base(pointer)
    {
    }

    public FikaHeadlessTransitController(TransitGlobalSettings settings, Il2CppReferenceArray<LocationSettings.Location.TransitParameters> parameters,
        LocalRaidSettings localRaidSettings) : base(ClassInjector.DerivedConstructorPointer<FikaHeadlessTransitController>())
    {
        ClassInjector.DerivedConstructorBody(this);
        _server = FikaSingleton<FikaServer>.Instance;
        _playersInTransitZone = [];
        _transittedPlayers = [];
        ClassInjector.InvokeBaseConstructor<TransitController>(this, settings, parameters);
        _localRaidSettings = localRaidSettings;
        IsEvent = localRaidSettings.transitionType.HasFlagNoBox(ELocationTransition.Event);
        var visited = localRaidSettings.transition.visitedLocations;
        var length = visited?.Length ?? 0;
        var maps = new Il2CppStringArray(length + 1);
        for (var i = 0; i < length; i++) maps[i] = visited[i];
        maps[length] = localRaidSettings.location;
        if (TarkovApplication.Exist(out var app) && app.Session?.Profile is { } profile)
        {
            summonedTransits[profile.Id] = new Transit(localRaidSettings.transition.transitionRaidId,
                localRaidSettings.transition.transitionCount, maps, IsEvent);
        }

        TransferItemsController.InitItemControllerServer(FikaGlobals.TransitTraderId, FikaGlobals.TransitTraderName);
        _onHeadlessPlayerEnter = new Action<TransitPoint, Player>(HeadlessOnPlayerEnter);
        _onHeadlessPlayerExit = new Action<TransitPoint, Player>(HeadlessOnPlayerExit);
        OnPlayerEnter += _onHeadlessPlayerEnter;
        OnPlayerExit += _onHeadlessPlayerExit;
    }

    public bool IsEvent { get; }

    public LocalRaidSettings LocalRaidSettings
    {
        get
        {
            return _localRaidSettings;
        }
    }

    private readonly LocalRaidSettings _localRaidSettings;
    private readonly FikaServer _server;
    private readonly Il2CppSystem.Action<TransitPoint, Player> _onHeadlessPlayerEnter;
    private readonly Il2CppSystem.Action<TransitPoint, Player> _onHeadlessPlayerExit;
    private readonly Dictionary<string, int> _playersInTransitZone;
    private bool _headlessTransit;
    private readonly List<int> _transittedPlayers;
    private string _usedPoint;

    public int AliveTransitPlayers
    {
        get
        {
            return _transittedPlayers.Count;
        }
    }

    private void HeadlessOnPlayerEnter(TransitPoint point, Player player)
    {
#if DEBUG
        FikaGlobals.LogInfo($"{player.Profile.Info.Nickname} entered transit point {point.Description}");
#endif

        if (!TryGetAccessToLocation(player, point.parameters.id, out _))
        {
#if DEBUG
            FikaGlobals.LogWarning("Player is not eligible for this transit point");
#endif
            return;
        }

        _playersInTransitZone[player.ProfileId] = point.parameters.id;

        if (!transitPlayers.ContainsKey(player.ProfileId))
        {
            var fikaPlayer = player.TryCast<FikaPlayer>();
            if (fikaPlayer != null)
            {
                fikaPlayer.UpdateBtrTraderServiceData().HandleExceptions();
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

        pointsById[point.parameters.id].GroupEnter(player);
    }

    private bool TryGetAccessToLocation(Player player, int pointId, out string keyId)
    {
        keyId = string.Empty;
        if (!TarkovApplication.Exist(out var application)) return false;
        var session = application.Session;
        var locations = session?.TryCast<ClientBackendSession>()?.LocationSettings?.locations;
        if (locations == null || !pointsById.TryGetValue(pointId, out var point) ||
            !locations.TryGetValue(point.parameters.target, out var destination) || destination == null)
            return false;

        var requirements = destination.GetAccessRequirements(session.GameMode,
            LocationSettings.Location.EAccessUsageType.Transit);
        if (requirements == null || requirements.Count == 0) return true;

        if (player.Side == EPlayerSide.Savage)
        {
            return false;
        }

        var profile = player.Profile;
        var items = profile.Inventory.GetPlayerItems(EPlayerItems.Equipment);
        if (!LocationAccessRequirementsUtility.TryFindSatisfiedRequirement(items, requirements, new Func<Item, bool>(profile.Examined),
            out var item, out _, out _) || item == null)
        {
            return false;
        }

        keyId = item.Id;
        return true;
    }

    private void HeadlessOnPlayerExit(TransitPoint point, Player player)
    {
#if DEBUG
        FikaGlobals.LogInfo($"{player.Profile.Info.Nickname} left transit point {point.Description}");
#endif

        if (_playersInTransitZone.TryGetValue(player.ProfileId, out var value))
        {
            if (value == point.parameters.id)
            {
                _playersInTransitZone.Remove(player.ProfileId);
            }
        }

        if (transitPlayers.ContainsKey(player.ProfileId))
        {
            point.GroupExit(player);
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

    public override void InactivePointNotification(int playerId, int pointId)
    {
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

    public override void Sizes(int pointId, Il2CppSystem.Collections.Generic.Dictionary<int, byte> sizes)
    {
        _currentTransitPointId = pointId;
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

    public override void Timers(int pointId, Il2CppSystem.Collections.Generic.Dictionary<int, ushort> timers)
    {
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

    public override void InteractWithTransit(Player player, InteractWithTransitPacket packet)
    {
        // Match the host's native eligibility checks before mutating transit
        // state. A remote request can arrive after a point has been disabled,
        // or carry a stale/mismatched key and time selection.
        if (player == null || packet == null || !packet.hasInteraction ||
            player.HealthController?.IsAlive != true ||
            !pointsById.TryGetValue(packet.pointId, out var point) || !point.Enabled ||
            !_playersInTransitZone.TryGetValue(player.ProfileId, out var currentPoint) ||
            currentPoint != packet.pointId || transitPlayers.ContainsKey(player.ProfileId) ||
            !TryGetAccessToLocation(player, packet.pointId, out var keyId) ||
            !EFT.ClientTransitController.HasMetEntryRequirements(point, player) ||
            (!string.IsNullOrEmpty(point.parameters.completedQuestId) &&
             !new TransitCompletedQuestRequirement(point.parameters.completedQuestId).Met(player)) ||
            !CheckForPlayers(player, packet.pointId)) return;

        profileTime[player.ProfileId] = packet.time;
        transitPlayers[player.ProfileId] = player.RaidId;
        profileKeys[player.ProfileId] = keyId;
        point.GroupEnter(player);
        ExfiltrationController.Instance.BannedPlayers.Add(player.RaidId);
        ExfiltrationController.Instance.CancelExtractionForPlayer(player);

        TransitEventPacket confirmPacket = new()
        {
            EventType = TransitEventPacket.ETransitEventType.Interaction,
            TransitEvent = new TransitInteractionEvent()
            {
                PlayerRaidId = player.RaidId,
                PointId = packet.pointId,
                Type = TransitInteractionEvent.EType.Confirm
            }
        };

        _server.SendData(ref confirmPacket, DeliveryMethod.ReliableOrdered);
    }

    private bool CheckForPlayers(Player player, int pointId)
    {
        var humanPlayers = 0;
        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        if (coopHandler == null)
        {
            FikaGlobals.LogError("Cannot validate headless transit without a CoopHandler");
            return false;
        }

        foreach (var fikaPlayer in coopHandler.HumanPlayers)
        {
            if (fikaPlayer.HealthController?.IsAlive == true &&
                !fikaPlayer.IsYourPlayer &&
                !_transittedPlayers.Contains(fikaPlayer.RaidId) &&
                !coopHandler.ExtractedPlayers.Contains(fikaPlayer.NetId))
            {
                humanPlayers++;
            }
        }

        var playersInPoint = 0;
        foreach (var fikaPlayer in coopHandler.HumanPlayers)
        {
            var inPoint = _playersInTransitZone.TryGetValue(fikaPlayer.ProfileId, out var zone) && zone == pointId;
            if (fikaPlayer.HealthController?.IsAlive == true &&
                !fikaPlayer.IsYourPlayer &&
                !_transittedPlayers.Contains(fikaPlayer.RaidId) &&
                !coopHandler.ExtractedPlayers.Contains(fikaPlayer.NetId) && inPoint)
            {
                playersInPoint++;
            }
        }

        if (playersInPoint < humanPlayers)
        {
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

    public override Il2CppSystem.Threading.Tasks.Task Transit(TransitPoint point, int playersCount, string hash,
        Il2CppSystem.Collections.Generic.Dictionary<string, ProfileKey> keys, Player player)
    {
        if (point == null || player == null || keys == null ||
            !keys.ContainsKey(player.ProfileId) ||
            !pointsById.TryGetValue(point.parameters.id, out var registeredPoint) ||
            !registeredPoint.Enabled || _transittedPlayers.Contains(player.RaidId))
        {
            return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        }

        if (!summonedTransits.TryGetValue(player.ProfileId, out var history))
        {
            FikaGlobals.LogError($"Cannot transit {player.ProfileId}: missing transit history");
            return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        }

        var destination = new LocationTransit
        {
            hash = hash,
            playersCount = playersCount,
            ip = string.Empty,
            location = point.parameters.location,
            profiles = keys,
            transitionRaidId = history.raidId,
            raidMode = ERaidMode.Local,
            side = player.Side == EPlayerSide.Savage ? ESideType.Savage : ESideType.Pmc,
            dayTime = profileTime.TryGetValue(player.ProfileId, out var selectedTime)
                ? selectedTime : _localRaidSettings.timeVariant
        };
        TransitEventPacket packet = new()
        {
            EventType = TransitEventPacket.ETransitEventType.Extract,
            PlayerId = player.RaidId,
            TransitId = point.parameters.id,
            Destination = destination
        };

        alreadyTransits[player.ProfileId] = destination;
        _transittedPlayers.Add(player.RaidId);

        _server.SendData(ref packet, DeliveryMethod.ReliableOrdered);

        if (!_headlessTransit)
        {
            ExtractHeadlessClient(point, destination);
        }

        return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
    }

    private void ExtractHeadlessClient(TransitPoint point, LocationTransit destination)
    {
        _headlessTransit = true;
        _usedPoint = point.parameters.name;

        if (TarkovApplication.Exist(out var tarkovApplication))
        {
            // A dedicated host has no local player transit entry. Follow the
            // first remote player's validated destination for the next raid.
            tarkovApplication.TransitionStatus = new TransitionStatus(destination.location, false,
                destination.side, destination.raidMode, destination.dayTime);
        }

        FikaBackendUtils.IsTransit = true;
        // Keep every continuation on Unity's update thread while checking
        // native player state across the transit wait.
        MainThreadDispatcher.RunAsync(DelayHeadlessExtract).Forget();
    }

    private async Task DelayHeadlessExtract()
    {
        var game = FikaSingleton<IFikaGame>.Instance;
        var usedPoint = _usedPoint;
        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
        if (coopHandler == null)
        {
            FikaGlobals.LogError("CoopHandler was null, quitting after 3 seconds");
            await Task.Delay(3000);
        }
        else
        {
            while (coopHandler.HumanPlayers.Exists(player =>
                !player.IsYourPlayer && player.HealthController?.IsAlive == true &&
                !_transittedPlayers.Contains(player.RaidId) &&
                !coopHandler.ExtractedPlayers.Contains(player.NetId)))
            {
                await Task.Delay(1000);
            }
        }

        // Task.Delay resumes on a pool thread. Game teardown touches Unity and
        // native EFT objects, so hand it back to the game update thread. A
        // delayed completion from an old raid must not stop a newer raid.
        MainThreadDispatcher.Post(() =>
        {
            if (ReferenceEquals(FikaSingleton<IFikaGame>.Instance, game))
                game?.Stop(string.Empty, ExitStatus.Transit, usedPoint);
        });
    }

    public override void Dispose()
    {
        OnPlayerEnter -= _onHeadlessPlayerEnter;
        OnPlayerExit -= _onHeadlessPlayerExit;
    }

    public void Init()
    {
        EnablePoints(true);
    }
}

