using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using CommonAssets.Scripts.Cutscenes;
using EFT;
using EFT.GlobalEvents;
using EFT.Interactive;
using EFT.Quests;
using EFT.UI;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.ObservedClasses;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Main.Components;

/// <summary>
/// One mission per bound raid. The host owns seats, zone membership and outcomes;
/// each player plays and saves their own ending. A client never supplies an exit status.
/// </summary>
internal static class FikaFinalMissionDirector
{
    private sealed class Ending
    {
        internal FinalMissionPacket Packet;
        internal bool Completed;
    }

    private static readonly Dictionary<string, Ending> Endings = new(StringComparer.Ordinal);
    private static readonly Queue<(FinalMissionPacket Packet, float Received)> Incoming = new();
    private static readonly Dictionary<NetPeer, int> PendingDepartures = new();
    private static readonly Dictionary<string, Dictionary<string, EQuestStatus>> QuestStates = new(StringComparer.Ordinal);
    private static Dictionary<string, EQuestStatus> _sentQuests;
    private static HashSet<string> _relevantQuests;
    private static CoopHandler _handler;
    private static FinallExitZone _zone;
    private static MapTimelinesBank _bank;
    private static Collider[] _colliders;
    private static WorldInteractiveObject[] _gates;
    private static bool _discovered, _introStarted, _armed, _departed, _localStarted, _localComplete;
    private static bool _exitAuthorized, _stopping, _localPrepared;
    private static int _frames, _nextOutcome;
    private static float _nextState, _nextError, _nextQuestState, _nextQuestRefresh;
    private static float _evacuationDuration, _evacuationElapsed;
    private static FinalMissionPacket? _localEnding;
    private static EFinalExitStatus? _localEligibility;
    private static bool _localZoneUi;
    private static GameWorld World => RaidInteractionSync.World;
    private static IFikaNetworkManager Network => FikaSingleton<IFikaNetworkManager>.Instance;
    private static CoopHandler Handler => _handler ??= Network?.CoopHandler;
    internal static bool Bound => World != null;
    internal static bool Owns(FinallExitZone zone) => Bound && (_zone == null || _zone.Pointer == zone.Pointer);

    internal static void Reset()
    {
        Endings.Clear();
        Incoming.Clear();
        PendingDepartures.Clear();
        QuestStates.Clear();
        _sentQuests = null;
        _relevantQuests = null;
        _handler = null;
        _zone = null;
        _bank = null;
        _colliders = null;
        _gates = null;
        _localEnding = null;
        _localEligibility = null;
        _localZoneUi = false;
        _discovered = _introStarted = _armed = _departed = _localStarted = _localComplete = false;
        _exitAuthorized = _stopping = _localPrepared = false;
        _frames = _nextOutcome = 0;
        _nextState = _nextError = _nextQuestState = _nextQuestRefresh = 0;
        _evacuationDuration = _evacuationElapsed = 0;
    }

    internal static void Receive(FinalMissionPacket packet)
    {
        if (Incoming.Count >= 256) throw new InvalidOperationException("Final mission startup queue exceeded its limit");
        Incoming.Enqueue((packet, Time.unscaledTime));
    }

    internal static void Tick()
    {
        if (!Bound || !RaidInteractionSync.Ready || Handler?.MyPlayer == null || !Handler.ShouldSync ||
            Handler.LocalGameInstance is not CoopGame game || game.Status != GameStatus.Started || _stopping) return;
        try { TickMission(game); }
        catch (Exception error)
        {
            // Preserve the state for retry, but do not flood the log every frame.
            if (Time.unscaledTime >= _nextError)
            {
                _nextError = Time.unscaledTime + 5;
                FikaGlobals.LogError($"Final mission update failed: {error}");
            }
        }
    }

    private static void TickMission(CoopGame game)
    {
        if (!_discovered)
        {
            _zone = UnityEngine.Object.FindObjectOfType<FinallExitZone>();
            _bank = UnityEngine.Object.FindObjectOfType<MapTimelinesBank>();
            _discovered = true;
            if (_zone != null)
            {
                _colliders = _zone.GetComponentsInChildren<Collider>(true).ToArray();
                _gates = UnityEngine.Object.FindObjectsOfType<WorldInteractiveObject>(true)
                    .Where(door => door.KeyId == "6866adbe09b973bf45094339").ToArray();
            }
        }
        if (_zone == null || _bank == null || World.CutscenesClientController?._timelinesMapBank == null) return;
        SyncQuestStates();
        while (Incoming.TryPeek(out var message))
        {
            var packet = message.Packet;
            if (FikaBackendUtils.IsClient && packet.Message == FinalMissionMessage.State)
                packet.Remaining = Math.Max(0, packet.Remaining - (Time.unscaledTime - message.Received));
            Apply(packet);
            Incoming.Dequeue();
        }
        if (FikaBackendUtils.IsServer)
        {
            if (World.CutscenesServerController?._timelinesMapBank == null) return;
            var players = Participants();
            if (!_introStarted && ++_frames >= 120 && players.Length > 0)
            {
                var marker = UnityEngine.Object.FindObjectOfType<StartCutsceneByStartRaid>();
                if (marker == null) _introStarted = true;
                else if (_bank.timelines.Count > 0)
                {
                    World.CutscenesServerController._lagTimeCompensation = Math.Max(2,
                        Singleton<GlobalConfiguration>.Instance.FinalMissionSettings.LagTimeCompensation);
                    _introStarted = World.CutscenesServerController.StartCutscene(_bank.timelines[0].cutsceneId,
                        PlayerIds(players), false, false, 0) != null;
                }
            }
            if (!_armed && _introStarted && (_gates.Any(gate => gate != null && gate.DoorState == EDoorState.Open) ||
                (_gates.Length == 0 && players.Any(InZone)))) Arm(_zone);
            if (_armed && !_departed)
            {
                // Native UI initialization also writes these zone fields. Keep
                // the authoritative clock separately so a delayed UI coroutine
                // cannot shorten/extend the host's mission timer.
                _evacuationElapsed += Time.deltaTime;
                _zone.evacuateTimer = _evacuationDuration;
                _zone._evacuateTimerElapsed = _evacuationElapsed;
                var eligible = players.Count(player => InZone(player) && HasRequiredItems(player));
                if (Time.unscaledTime >= _nextState)
                {
                    _nextState = Time.unscaledTime + 1;
                    foreach (var player in players) SendState(player, eligible);
                }
                if (_zone._waitingEvacuateTimeLeft || _evacuationElapsed >= _evacuationDuration)
                    Depart(ConnectedHumans(), eligible);
            }
        }
        else if (_armed && !_departed)
            _zone._evacuateTimerElapsed = Math.Min(_zone.evacuateTimer, _zone._evacuateTimerElapsed + Time.deltaTime);

        if (_localEnding.HasValue && !_localStarted) StartLocalEnding();
        if (_localComplete) PrepareLocal(game);
        if (FikaBackendUtils.IsClient && _localComplete &&
            FikaSingleton<FikaClient>.Instance?.ServerConnection?.ConnectionState != ConnectionState.Connected)
            _exitAuthorized = true; // Save the already-issued host outcome after connection loss.
        if (FikaBackendUtils.IsServer && _departed && (FikaBackendUtils.IsHeadless || _localComplete))
        {
            // Wait for clients to leave after their own save/exit. An acknowledgement
            // alone does not prove that the host's reliable response has arrived.
            foreach (var peer in PendingDepartures.Keys.ToArray())
                if (peer.ConnectionState != ConnectionState.Connected) PendingDepartures.Remove(peer);
            if (PendingDepartures.Count == 0)
            {
                if (FikaBackendUtils.IsHeadless && !_localEnding.HasValue)
                {
                    _localEnding = new FinalMissionPacket { ExitStatus = ExitStatus.Survived };
                    PrepareLocal(game);
                }
                _exitAuthorized = true;
            }
        }
        if (_exitAuthorized && _localEnding is { } ending)
        {
            _stopping = true;
            if (FikaBackendUtils.IsClient && FikaSingleton<FikaClient>.Instance?.ServerConnection?.ConnectionState == ConnectionState.Connected)
                Network.SendGenericPacket(EGenericSubPacketType.ClientExtract, ClientExtract.FromValue(Handler.MyPlayer.NetId), true);
            game.Stop(Handler.MyPlayer.ProfileId, ending.ExitStatus, FinallExitZone.FINAL_EXIT_NAME, 0);
        }
    }

    private static List<NetPeer> ConnectedPeers()
    {
        var peers = new List<NetPeer>();
        FikaSingleton<FikaServer>.Instance?.NetServer.GetConnectedPeers(peers);
        return peers.Where(peer => peer.ConnectionState == ConnectionState.Connected).ToList();
    }

    private static FikaPlayer[] ConnectedHumans()
    {
        var peers = ConnectedPeers();
        return Handler.HumanPlayers.Where(player => player != null &&
            !Handler.ExtractedPlayers.Contains(player.NetId) &&
            (player.IsYourPlayer ? !FikaBackendUtils.IsHeadless : peers.Any(peer => peer.Player?.NetId == player.NetId))).ToArray();
    }

    private static FikaPlayer[] Participants() => ConnectedHumans().Where(player => player.HealthController?.IsAlive == true).ToArray();

    private static Il2CppSystem.Collections.Generic.List<int> PlayerIds(IEnumerable<FikaPlayer> players)
    {
        var ids = new Il2CppSystem.Collections.Generic.List<int>();
        foreach (var player in players) ids.Add(player.PlayerId);
        return ids;
    }

    internal static void TickTimer(StartCutsceneByTimer timer)
    {
        if (!FikaBackendUtils.IsServer || !timer.timerOn || Handler?.ShouldSync != true ||
            Handler.LocalGameInstance is not CoopGame game || game.Status != GameStatus.Started) return;
        timer._timeElapsed += Time.deltaTime;
        if (timer._timeElapsed < timer.time) return;
        var ids = PlayerIds(Participants());
        if (ids.Count == 0 || World.CutscenesServerController == null) return;
        if (World.CutscenesServerController.StartCutscene(timer.cutsceneId, ids, true, true, 0) == null) return;
        timer.timerOn = false;
        timer._timeElapsed = 0;
        timer.gameObject.SetActive(false);
    }

    internal static void Arm(FinallExitZone zone)
    {
        if (!FikaBackendUtils.IsServer || _armed || Handler?.ShouldSync != true) return;
        _zone = zone;
        var settings = Singleton<GlobalConfiguration>.Instance.FinalMissionSettings;
        var count = Participants().Length;
        if (count == 0) return;
        zone._playersToEvacuateRandom = settings.SeatsRandom;
        zone._minPLayersToEvacuate = settings.MinPlayersToEvac;
        zone._playersToEvacuate = Math.Min(count, Math.Max(settings.MinPlayersToEvac,
            count - (int)UnityEngine.Random.Range(settings.SeatsRandom.x, settings.SeatsRandom.y)));
        var configuredEvacuationTime = Math.Max(0, settings.EvacuateTime);
        // Seed the native field first: GetEvacuateTimerByEndRaid() uses the
        // configured evacuation value when applying the raid-end boundary.
        zone.evacuateTimer = configuredEvacuationTime;
        // The final mission timer may not extend beyond the native raid end.
        // GetEvacuateTimerByEndRaid() is the remaining raid time, not a
        // replacement for the configured final-mission duration.
        var remainingRaidTime = Math.Max(0, zone.GetEvacuateTimerByEndRaid());
        zone.evacuateTimer = Math.Min(configuredEvacuationTime, remainingRaidTime);
        if (settings.FinalCutscenesByQuest != null)
        {
            zone.finalCutscenesVariants = new();
            foreach (var variant in settings.FinalCutscenesByQuest) zone.finalCutscenesVariants.Add(variant);
        }
        zone.BuildCutsceneMapping();
        _evacuationDuration = zone.evacuateTimer;
        _evacuationElapsed = 0;
        zone._evacuateTimerElapsed = 0;
        zone._evacuateInitialized = zone._waitingEvacuateTimeLeft = false;
        zone._zoneActive = true;
        _armed = true;
        FikaGlobals.LogInfo($"Final mission armed: {count} players, {zone._playersToEvacuate} seats, {zone.evacuateTimer}s");
    }

    private static bool InZone(FikaPlayer player)
    {
        if (_colliders == null) _colliders = _zone.GetComponentsInChildren<Collider>(true).ToArray();
        // ClosestPoint also handles rotated/non-box trigger shapes without admitting
        // the empty corners of their world-space axis-aligned bounds.
        return _colliders.Any(collider => collider != null && collider.enabled &&
            (collider.ClosestPoint(player.Position) - player.Position).sqrMagnitude < 0.01f);
    }

    private static bool ActiveQuest(FikaPlayer player, string questId)
    {
        if (string.IsNullOrEmpty(questId)) return true;
        if (!player.IsYourPlayer && QuestStates.TryGetValue(player.ProfileId, out var statuses))
            // The report is a complete snapshot of this map's relevant quests.
            // An absent quest was not accepted by the client; the host's copy of
            // its profile can be stale after an in-raid quest transition.
            return statuses.TryGetValue(questId, out var status) &&
                status is EQuestStatus.Started or EQuestStatus.AvailableForFinish;
        foreach (var quest in player.Profile.QuestsData)
            if (quest.Id == questId) return quest.Status is EQuestStatus.Started or EQuestStatus.AvailableForFinish;
        return false;
    }

    private static void SyncQuestStates()
    {
        if (_relevantQuests == null)
        {
            _relevantQuests = new(StringComparer.Ordinal);
            var variants = Singleton<GlobalConfiguration>.Instance.FinalMissionSettings.FinalCutscenesByQuest;
            if (variants != null)
                foreach (var variant in variants) if (!string.IsNullOrEmpty(variant.questId)) _relevantQuests.Add(variant.questId);
            if (_zone.finalCutscenesVariants != null)
                foreach (var variant in _zone.finalCutscenesVariants) if (!string.IsNullOrEmpty(variant.questId)) _relevantQuests.Add(variant.questId);
            if (_zone.questItemsRequirements != null)
                foreach (var requirement in _zone.questItemsRequirements) if (!string.IsNullOrEmpty(requirement.questId)) _relevantQuests.Add(requirement.questId);
        }
        if (!FikaBackendUtils.IsClient || Time.unscaledTime < _nextQuestState || _departed ||
            FikaSingleton<FikaClient>.Instance?.ServerConnection?.ConnectionState != ConnectionState.Connected) return;
        _nextQuestState = Time.unscaledTime + 1;
        var statuses = new Dictionary<string, EQuestStatus>(StringComparer.Ordinal);
        foreach (var quest in Handler.MyPlayer.Profile.QuestsData)
            if (_relevantQuests.Contains(quest.Id)) statuses[quest.Id] = quest.Status;
        if (Time.unscaledTime < _nextQuestRefresh && _sentQuests != null && _sentQuests.Count == statuses.Count &&
            statuses.All(quest => _sentQuests.TryGetValue(quest.Key, out var status) && status == quest.Value)) return;
        var packet = new FinalMissionQuestPacket { NetId = Handler.MyPlayer.NetId, Statuses = statuses };
        Network.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        _sentQuests = statuses;
        // A reliable packet can arrive before the host's world/peer is ready.
        // Periodically repeat the bounded snapshot, even without a quest change.
        _nextQuestRefresh = Time.unscaledTime + 5;
    }

    internal static void ReceiveQuestStates(FinalMissionQuestPacket packet, FikaPlayer player)
    {
        if (!FikaBackendUtils.IsServer || player == null || packet.NetId != player.NetId || _departed ||
            _relevantQuests == null || packet.Statuses == null) return;
        if (packet.Statuses.Any(quest => !_relevantQuests.Contains(quest.Key))) return;
        // Quest progression is client-owned in Fika. Keep the report in a scoped
        // table; never mutate the observed profile or trust a client-supplied ending.
        QuestStates[player.ProfileId] = packet.Statuses;
    }

    private static string VariantFor(FikaPlayer player)
    {
        // Match native GetVariantCutsceneId: the first Started/AvailableForFinish
        // quest in profile order whose ID is in the map's cutscene mapping.
        foreach (var quest in player.Profile.QuestsData)
            if (ActiveQuest(player, quest.Id) && _zone._cutsceneByQuest.TryGetValue(quest.Id, out var cutscene)) return cutscene;
        // A quest accepted in this raid may not exist in the host's initial profile.
        if (!player.IsYourPlayer && QuestStates.TryGetValue(player.ProfileId, out var reported))
            foreach (var quest in reported)
                if ((quest.Value is EQuestStatus.Started or EQuestStatus.AvailableForFinish) &&
                    _zone._cutsceneByQuest.TryGetValue(quest.Key, out var cutscene)) return cutscene;
        return null;
    }

    private static bool HasRequiredItems(FikaPlayer player)
    {
        if (_zone.questItemsRequirements == null) return true;
        foreach (var requirement in _zone.questItemsRequirements)
        {
            if (!ActiveQuest(player, requirement.questId) || string.IsNullOrEmpty(requirement.itemId)) continue;
            var items = player.Profile.Inventory.GetAllItemByTemplate(requirement.itemId);
            if (items?.GetEnumerator()?.MoveNext() == true) continue;
            // Quest pickups are private to each player and are synchronized through
            // QuestSyncPacket rather than observed equipment inventory operations.
            if (player.FikaQuests is ObservedQuestController observed && observed.HasCollectedQuestItem(requirement.itemId)) continue;
            return false;
        }
        return true;
    }

    private static EFinalExitStatus Eligibility(FikaPlayer player, int eligible) =>
        !InZone(player) ? EFinalExitStatus.notInZone : !HasRequiredItems(player) ? EFinalExitStatus.noItem :
        eligible > _zone._playersToEvacuate ? EFinalExitStatus.overCount : EFinalExitStatus.available;

    private static void SendState(FikaPlayer player, int eligible, NetPeer peer = null)
    {
        var packet = new FinalMissionPacket { Message = FinalMissionMessage.State, NetId = player.NetId,
            Seats = _zone._playersToEvacuate, EligiblePlayers = eligible,
            Remaining = Math.Max(0, _evacuationDuration - _evacuationElapsed), Eligibility = Eligibility(player, eligible) };
        Send(player, packet, peer);
    }

    private static void Send(FikaPlayer player, FinalMissionPacket packet, NetPeer peer = null)
    {
        if (player.IsYourPlayer) { Receive(packet); return; }
        peer ??= ConnectedPeers().FirstOrDefault(candidate => candidate.Player?.NetId == player.NetId);
        if (peer != null) FikaSingleton<FikaServer>.Instance.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
    }

    private static void Depart(FikaPlayer[] players, int eligible)
    {
        if (!FikaBackendUtils.IsHeadless && !players.Any(player => player.IsYourPlayer))
            players = players.Append(Handler.MyPlayer).ToArray();
        var decisions = new Dictionary<string, Ending>(StringComparer.Ordinal);
        // Determine every outcome before invoking any local native ending/action.
        foreach (var player in players)
        {
            var alive = player.HealthController?.IsAlive == true;
            var cutscene = alive && Eligibility(player, eligible) == EFinalExitStatus.available ? VariantFor(player) : null;
            var status = !alive ? ExitStatus.Killed : string.IsNullOrEmpty(cutscene) ? ExitStatus.MissingInAction : EndingStatus(cutscene);
            decisions.Add(player.ProfileId, new Ending { Packet = new FinalMissionPacket {
                Message = FinalMissionMessage.Outcome, NetId = player.NetId, OutcomeId = ++_nextOutcome,
                CutsceneId = cutscene ?? string.Empty, ExitStatus = status } });
        }
        foreach (var decision in decisions) Endings.Add(decision.Key, decision.Value);
        foreach (var peer in ConnectedPeers())
            if (peer.Player != null && decisions.ContainsKey(peer.Player.ProfileId)) PendingDepartures[peer] = peer.Player.NetId;
        _zone._evacuateInitialized = _zone._waitingEvacuateTimeLeft = true;
        _departed = true;
        foreach (var player in players) Send(player, Endings[player.ProfileId].Packet);
    }

    private static ExitStatus EndingStatus(string cutscene)
    {
        foreach (var timeline in _bank.timelines)
        {
            if (timeline.cutsceneId != cutscene) continue;
            foreach (var id in timeline.endActionsId ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(0))
                if (World.CutscenesClientController._actions.TryGetValue(id, out var action) &&
                    action.TryCast<CutsceneActionExitAfterCutscene>() is { } exit) return exit.ExitStatus;
        }
        // Do not invent a successful save if the map's exit action is missing.
        throw new InvalidOperationException($"Final cutscene '{cutscene}' has no registered exit action");
    }

    internal static bool SendSnapshot(NetPeer peer)
    {
        if (!_armed || peer.Player == null || _zone == null) return false;
        var player = peer.Player;
        SendState(player, Participants().Count(candidate => InZone(candidate) && HasRequiredItems(candidate)), peer);
        if (Endings.TryGetValue(player.ProfileId, out var ending))
        {
            PendingDepartures[peer] = player.NetId;
            ending.Packet.NetId = player.NetId;
            Send(player, ending.Packet, peer);
            // The reconnecting game needs to prepare its local state before exiting.
            // A duplicate Complete will receive the same authorization below.
        }
        else if (_departed)
        {
            // Reconnecting after the ship departed cannot obtain a new seat.
            var packet = new FinalMissionPacket { Message = FinalMissionMessage.Outcome, NetId = player.NetId,
                OutcomeId = ++_nextOutcome, CutsceneId = string.Empty,
                ExitStatus = player.HealthController?.IsAlive == true ? ExitStatus.MissingInAction : ExitStatus.Killed };
            Endings.Add(player.ProfileId, new Ending { Packet = packet });
            PendingDepartures[peer] = player.NetId;
            Send(player, packet, peer);
        }
        return _departed;
    }

    private static void Apply(FinalMissionPacket packet)
    {
        if (packet.NetId != Handler.MyPlayer.NetId) return;
        switch (packet.Message)
        {
            case FinalMissionMessage.State:
                if (!_localZoneUi)
                {
                    if (FikaBackendUtils.IsClient)
                    {
                        _zone.InitZoneClient(packet.Seats);
                        _zone.evacuateTimer = packet.Remaining;
                    }
                    // The native UI coroutine writes evacuateTimer from this
                    // event. Keep the host's authoritative total and elapsed time.
                    var duration = _zone.evacuateTimer;
                    GlobalEventsController.CreateEvent<FinalExitZoneInitEvent>().Invoke(packet.Seats, (int)Math.Ceiling(packet.Remaining));
                    if (FikaBackendUtils.IsServer) _zone.evacuateTimer = duration;
                    _localZoneUi = true;
                }
                _armed = true;
                _zone._playersToEvacuate = packet.Seats;
                if (FikaBackendUtils.IsClient)
                    _zone._evacuateTimerElapsed = Math.Max(0, _zone.evacuateTimer - packet.Remaining);
                if (_localEligibility != packet.Eligibility)
                {
                    GlobalEventsController.CreateEvent<ChangeFinalExitStateEvent>().Invoke(Handler.MyPlayer.PlayerId, packet.Eligibility);
                    _localEligibility = packet.Eligibility;
                }
                var panel = GameUI.Instance?.TimerPanel;
                if (panel?.FinalExitTimerIsExist() == true)
                {
                    panel.SetFinalExitZoneConditionText($"{FinallExitZone.FINAL_EXIT_CONDITION_SEATS_COUNT.Localized()} {packet.Seats} " +
                        $"{FinallExitZone.FINAL_EXIT_CONDITION_SEATS_PLAYERS.Localized()} {packet.EligiblePlayers}");
                    panel.SetFinalExitZoneLockedText(packet.EligiblePlayers > packet.Seats);
                }
                break;
            case FinalMissionMessage.Outcome:
                if (_localEnding?.OutcomeId == packet.OutcomeId) { if (_localComplete) RequestCompletion(); return; }
                if (_localEnding.HasValue) throw new InvalidOperationException("Host changed a final mission outcome");
                _departed = true;
                _localEnding = packet;
                break;
            case FinalMissionMessage.ExitAuthorized:
                if (_localComplete && _localEnding?.OutcomeId == packet.OutcomeId) _exitAuthorized = true;
                break;
        }
    }

    private static void StartLocalEnding()
    {
        var ending = _localEnding.Value;
        if (string.IsNullOrEmpty(ending.CutsceneId))
        {
            _localStarted = true;
            CompleteLocal();
            return;
        }
        // The native final ending is a client-only view (no server CutsceneProcess).
        // Play from the start on reconnect so its local quest/action effects run too.
        _localStarted = true;
        try
        {
            GlobalEventsController.CreateEvent<CutSceneStartEvent>().Invoke(ending.CutsceneId, 0,
                new[] { Handler.MyPlayer.PlayerId }, 0, false, true);
        }
        catch { _localStarted = false; throw; }
    }

    internal static void LocalTimelineComplete(string cutscene)
    {
        if (_localStarted && _localEnding?.CutsceneId == cutscene) CompleteLocal();
    }

    internal static bool EndLocalOnTimeout()
    {
        if (_localEnding is not { } ending || !_localStarted) return false;
        if (_localComplete) return true;
        var controller = World?.CutscenesClientController;
        if (controller?._currentTimelineData is { } timeline && timeline.cutsceneId == ending.CutsceneId &&
            controller._directorsByScene.TryGetValue(timeline.sceneName, out var director))
            controller.StopCutscene(director, false);
        else FikaGlobals.LogError($"Cannot finish timed-out final cutscene '{ending.CutsceneId}': its director is unavailable");
        // Never signal success without running the native end-actions/cleanup.
        return true;
    }

    internal static void LocalExitAction(CutscenesClientController controller, string actionId)
    {
        if (_localStarted && _localEnding is { } ending && controller._cutsceneId == ending.CutsceneId &&
            controller._actions.TryGetValue(actionId, out var action) && action.TryCast<CutsceneActionExitAfterCutscene>() != null)
            CompleteLocal();
    }

    internal static bool SuppressExitAction(string cutsceneId) =>
        _departed && !string.IsNullOrEmpty(cutsceneId) &&
        Endings.Values.Any(ending => string.Equals(ending.Packet.CutsceneId, cutsceneId, StringComparison.Ordinal));

    internal static bool IsLocalFinalExitAction(CutscenesClientController controller, string actionId)
    {
        if (!_localEnding.HasValue || controller == null || controller._actions == null ||
            !controller._actions.TryGetValue(actionId, out var action) ||
            action.TryCast<CutsceneActionExitAfterCutscene>() == null) return false;
        return string.Equals(controller._cutsceneId, _localEnding.Value.CutsceneId, StringComparison.Ordinal);
    }

    private static void CompleteLocal()
    {
        if (_localComplete) return;
        _localComplete = true;
        RequestCompletion();
    }

    private static void RequestCompletion()
    {
        if (_localEnding is not { } ending) return;
        var packet = new FinalMissionPacket { Message = FinalMissionMessage.Complete,
            NetId = Handler.MyPlayer.NetId, OutcomeId = ending.OutcomeId };
        if (FikaBackendUtils.IsServer) HandleCompletion(packet, Handler.MyPlayer, null);
        else if (FikaSingleton<FikaClient>.Instance?.ServerConnection?.ConnectionState == ConnectionState.Connected)
            Network.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    internal static void HandleCompletion(FinalMissionPacket packet, FikaPlayer player, NetPeer peer)
    {
        if (packet.Message != FinalMissionMessage.Complete || player == null || packet.NetId != player.NetId ||
            !Endings.TryGetValue(player.ProfileId, out var ending) || packet.OutcomeId != ending.Packet.OutcomeId) return;
        ending.Completed = true;
        if (peer != null)
        {
            var response = new FinalMissionPacket { Message = FinalMissionMessage.ExitAuthorized,
                NetId = player.NetId, OutcomeId = ending.Packet.OutcomeId };
            FikaSingleton<FikaServer>.Instance.SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
        }
    }

    internal static void PlayerExtracted(int netId)
    {
        // The client's reliable ClientExtract follows receipt of ExitAuthorized.
        // Its local save no longer needs the gameplay host at this point.
        if (!Endings.Values.Any(ending => ending.Packet.NetId == netId && ending.Completed)) return;
        foreach (var pending in PendingDepartures.Where(entry => entry.Value == netId).ToArray())
            PendingDepartures.Remove(pending.Key);
    }

    private static void PrepareLocal(CoopGame game)
    {
        if (_localPrepared) return;
        var player = Handler.MyPlayer;
        game.ExitStatus = _localEnding.Value.ExitStatus;
        game.ExitLocation = FinallExitZone.FINAL_EXIT_NAME;
        game.GameController.LocalTriggerZones.Clear();
        game.GameController.LocalTriggerZones.AddRange(player.TriggerZones.ToArray());
        if (!game.ExtractedPlayers.Contains(player.NetId)) game.ExtractedPlayers.Add(player.NetId);
        if (!Handler.ExtractedPlayers.Contains(player.NetId)) Handler.ExtractedPlayers.Add(player.NetId);
        // Do not use normal Extract: it teleports during the cinematic and can
        // rewrite a mission's result to Runner based on XP/time.
        player.ActiveHealthController.SetDamageCoeff(0);
        player.ActiveHealthController.DamageMultiplier = 0;
        player.ActiveHealthController.DisableMetabolism();
        player.ActiveHealthController.PauseAllEffects();
        if (player.FikaQuests is Fika.Core.Main.ClientClasses.ClientQuestController clientQuests) clientQuests.ToggleSend(false);
        if (player.FikaQuests is Fika.Core.Main.ClientClasses.ClientSharedQuestController sharedQuests) sharedQuests.ToggleQuestSharing(false);
        game.DiagnosticPlayerOwner?.Stop();
        if (game.GameController is HostGameController host) host.ClearHostAI(player);
        _localPrepared = true;
    }

    // Keep native raid-time expiry from tearing down a host while peers are still
    // playing/saving. The explicit authorized stop above bypasses this guard.
    internal static bool DeferStop => !_stopping && (_localEnding.HasValue || (FikaBackendUtils.IsServer && _departed));
}
