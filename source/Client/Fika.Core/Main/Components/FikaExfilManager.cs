using System.Collections.Generic;
using System.Linq;
using CommonAssets.Scripts.Game;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.Utils;
using EFT.Interactive.SecretExfiltrations;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;

namespace Fika.Core.Main.Components;

public class FikaExfilManager : MonoBehaviour
{
    public FikaExfilManager(System.IntPtr pointer) : base(pointer) { }


    public FikaExfilManager() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaExfilManager>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private CoopGame _game;
    private List<ExtractionPlayerHandler> _playerHandlers;
    private List<ExfiltrationPoint> _countdownPoints;
    private ExfiltrationPoint[] _exfiltrationPoints;
    private SecretExfiltrationPoint[] _secretExfiltrationPoints;
    private Il2CppSystem.Action<ExfiltrationPoint, Player> _startExtraction;
    private Il2CppSystem.Action<ExfiltrationPoint, Player> _cancelExtraction;
    private Il2CppSystem.Action<ExfiltrationPoint, EExfiltrationStatus> _statusChanged;
    private Il2CppSystem.Action<ExfiltrationPoint, EExfiltrationStatus> _gameStatusChanged;
    private Il2CppSystem.Action<ExfiltrationPoint, EExfiltrationStatus> _newSecretExit;
    private Il2CppSystem.Action<string, bool> _pointFound;
    private readonly Dictionary<System.IntPtr, ExfiltrationPoint> _boundPoints = [];

    protected void Awake()
    {
        _game = gameObject.GetComponent<CoopGame>();
        _startExtraction = (Il2CppSystem.Action<ExfiltrationPoint, Player>)ExfiltrationPoint_OnStartExtraction;
        _cancelExtraction = (Il2CppSystem.Action<ExfiltrationPoint, Player>)ExfiltrationPoint_OnCancelExtraction;
        _statusChanged = (Il2CppSystem.Action<ExfiltrationPoint, EExfiltrationStatus>)ExfiltrationPoint_OnStatusChanged;
        _gameStatusChanged = (Il2CppSystem.Action<ExfiltrationPoint, EExfiltrationStatus>)_game.OnStatusChangedHandler;
        _newSecretExit = (Il2CppSystem.Action<ExfiltrationPoint, EExfiltrationStatus>)_game.ShowNewSecretExit;
        _pointFound = (Il2CppSystem.Action<string, bool>)SecretExfiltrationPoint_OnPointFoundEvent;
        _playerHandlers = [];
        _countdownPoints = [];
        _exfiltrationPoints = [];
        _secretExfiltrationPoints = [];
    }

    protected void Update()
    {
        if (_exfiltrationPoints == null || _secretExfiltrationPoints == null)
        {
            return;
        }

        // Extract/ExternalSetStatus can synchronously cancel handlers or Stop
        // the manager. Iterate snapshots, consuming each entry before callbacks.
        foreach (var playerHandler in _playerHandlers.ToArray())
        {
            if (playerHandler.StartTime + playerHandler.ExfilPoint.Settings.ExfiltrationTime - _game.PastTime <= 0 &&
                _playerHandlers.Remove(playerHandler))
            {
                _game.ExitLocation = playerHandler.ExfilPoint.Settings.Name;
                _game.Extract(playerHandler.Player, playerHandler.ExfilPoint);
            }
        }

        foreach (var exfiltrationPoint in _countdownPoints.ToArray())
        {
            if (_game.PastTime - exfiltrationPoint.ExfiltrationStartTime > exfiltrationPoint.Settings.ExfiltrationTime &&
                _countdownPoints.Remove(exfiltrationPoint))
            {
                foreach (var player in exfiltrationPoint.Entered)
                {
                    if (player == null)
                    {
                        continue;
                    }

                    if (!player.IsYourPlayer)
                    {
                        continue;
                    }

                    if (!player.HealthController.IsAlive)
                    {
                        continue;
                    }

                    if (!exfiltrationPoint.UnmetRequirements(player).AsManaged().Any())
                    {
                        _game.ExitLocation = exfiltrationPoint.Settings.Name;
                        if (player.TryCast<FikaPlayer>() is { } localPlayer)
                        {
                            _game.Extract(localPlayer, exfiltrationPoint);
                            // Only the local player is extracted by this manager;
                            // extraction can mutate Entered or stop the manager.
                            break;
                        }
                    }
                }

                exfiltrationPoint.ExternalSetStatus(exfiltrationPoint.Reusable
                    ? EExfiltrationStatus.UncompleteRequirements
                    : EExfiltrationStatus.NotPresent);
            }
        }
    }

    public void Run(ExfiltrationPoint[] exfilPoints, SecretExfiltrationPoint[] secretExfilPoints)
    {
        // Rebinding must not disable points retained by the new set. Disable
        // turns off native triggers, and subscribing again does not enable them.
        var retainedPoints = new HashSet<System.IntPtr>(exfilPoints.Select(point => point.Pointer));
        retainedPoints.UnionWith(secretExfilPoints.Select(point => point.Pointer));
        ReleaseBindings(retainedPoints);
        for (var i = 0; i < exfilPoints.Length; i++)
        {
            var exfiltrationPoint = exfilPoints[i];
            UpdateExfilPointFromServer(exfiltrationPoint, true);
            exfiltrationPoint.OnStatusChanged += _gameStatusChanged;
            _game.UpdateExfiltrationUi(exfiltrationPoint, false, true);
            if (FikaPlugin.Instance.Settings.DynamicVExfils && exfiltrationPoint.Settings.PlayersCount > 0 && exfiltrationPoint.Settings.PlayersCount < FikaSingleton<IFikaNetworkManager>.Instance.PlayerAmount)
            {
                exfiltrationPoint.Settings.PlayersCount = FikaSingleton<IFikaNetworkManager>.Instance.PlayerAmount;
            }
        }

        for (var i = 0; i < secretExfilPoints.Length; i++)
        {
            var secretExfiltrationPoint = secretExfilPoints[i];
            UpdateExfilPointFromServer(secretExfiltrationPoint, true);
            secretExfiltrationPoint.OnStatusChanged += _gameStatusChanged;
            secretExfiltrationPoint.OnStatusChanged += _newSecretExit;
            _game.UpdateExfiltrationUi(secretExfiltrationPoint, false, true);
            secretExfiltrationPoint.OnPointFoundEvent += _pointFound;
        }

        _exfiltrationPoints = exfilPoints;
        _secretExfiltrationPoints = secretExfilPoints;
    }

    private void SecretExfiltrationPoint_OnPointFoundEvent(string exitName, bool sharedExit)
    {
        var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer?.TryCast<FikaPlayer>();
        if (mainPlayer == null) return;
        var teamId = string.IsNullOrEmpty(mainPlayer.TeamId) ? mainPlayer.GroupId : mainPlayer.TeamId;
        FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.SecretExfilFound,
            SecretExfilFound.FromValue(mainPlayer.GroupId, teamId, exitName), true);
    }

    public void Stop() => ReleaseBindings(null);

    private void ReleaseBindings(HashSet<System.IntPtr> retainedPoints)
    {
        _playerHandlers?.Clear();
        _countdownPoints?.Clear();

        if (_exfiltrationPoints != null)
        {
            for (var i = 0; i < _exfiltrationPoints.Length; i++)
            {
                var exfiltrationPoint = _exfiltrationPoints[i];
                UpdateExfilPointFromServer(exfiltrationPoint, false);
                exfiltrationPoint.OnStatusChanged -= _gameStatusChanged;
                if (retainedPoints == null || !retainedPoints.Contains(exfiltrationPoint.Pointer))
                    exfiltrationPoint.Disable();
            }
        }

        if (_secretExfiltrationPoints != null)
        {
            for (var i = 0; i < _secretExfiltrationPoints.Length; i++)
            {
                var secretExfiltrationPoint = _secretExfiltrationPoints[i];
                UpdateExfilPointFromServer(secretExfiltrationPoint, false);
                secretExfiltrationPoint.OnStatusChanged -= _gameStatusChanged;
                secretExfiltrationPoint.OnStatusChanged -= _newSecretExit;
                secretExfiltrationPoint.OnPointFoundEvent -= _pointFound;
                if (retainedPoints == null || !retainedPoints.Contains(secretExfiltrationPoint.Pointer))
                    secretExfiltrationPoint.Disable();
            }
        }
        foreach (var point in _boundPoints.Values.ToArray())
            UpdateExfilPointFromServer(point, false);
        _exfiltrationPoints = [];
        _secretExfiltrationPoints = [];
    }

    public void UpdateExfilPointFromServer(ExfiltrationPoint point, bool enable)
    {
        if (enable)
        {
            // Initial/reconnect snapshots can set Countdown before Run binds
            // events. Run also clears this list while retaining event bindings,
            // so restore the countdown even if the point was already bound.
            if (_countdownPoints != null && point.Status == EExfiltrationStatus.Countdown && !_countdownPoints.Contains(point))
                _countdownPoints.Add(point);
            if (!_boundPoints.TryAdd(point.Pointer, point)) return;
            point.OnStartExtraction += _startExtraction;
            point.OnCancelExtraction += _cancelExtraction;
            point.OnStatusChanged += _statusChanged;
        }
        else
        {
            if (!_boundPoints.Remove(point.Pointer)) return;
            point.OnStartExtraction -= _startExtraction;
            point.OnCancelExtraction -= _cancelExtraction;
            point.OnStatusChanged -= _statusChanged;
        }
    }

    private void OnDestroy() => Stop();

    private void ExfiltrationPoint_OnCancelExtraction(ExfiltrationPoint point, Player player)
    {
        if (!player.IsYourPlayer)
        {
            return;
        }

        var extractionPlayerHandler = _playerHandlers.FirstOrDefault(x => x.Player == player);
        if (extractionPlayerHandler != null)
        {
            _playerHandlers.Remove(extractionPlayerHandler);
        }
    }

    private void ExfiltrationPoint_OnStartExtraction(ExfiltrationPoint point, Player player)
    {
        if (!player.IsYourPlayer)
        {
            return;
        }

        if (_playerHandlers.All(x => x.Player != player))
        {
            _playerHandlers.Add(new(player, point, _game.PastTime));
        }
    }

    private void ExfiltrationPoint_OnStatusChanged(ExfiltrationPoint point, EExfiltrationStatus prevStatus)
    {
        var isCounting = _countdownPoints.Contains(point);
        if (isCounting && point.Status != EExfiltrationStatus.Countdown)
        {
            point.ExfiltrationStartTime = -100;
            _countdownPoints.Remove(point);
            if (IsPaidExfiltrationPoint(point))
            {
                FikaGlobals.LogInfo($"[CarExtract] point '{point.Settings.Name}' left the countdown " +
                    $"(status {point.Status}, queued {point.QueuedPlayers?.Count ?? 0})");
            }
        }

        if (!isCounting && point.Status == EExfiltrationStatus.Countdown)
        {
            if (point.ExfiltrationStartTime is <= 0 and > -90)
            {
                point.ExfiltrationStartTime = _game.PastTime;
                if (IsPaidExfiltrationPoint(point))
                {
                    FikaGlobals.LogInfo($"[CarExtract] activation broadcast: point '{point.Settings.Name}' " +
                        $"started its countdown at {point.ExfiltrationStartTime}, queued {point.QueuedPlayers?.Count ?? 0}");
                }
                // A countdown unlocked by the local player's own flare is scoped
                // to that player (or its squad in the default mode); every other
                // exit keeps the point wide countdown it always had.
                FikaFlareExfilManager.TryGetLocalActivationScope(point, out var scopeOwner, out var scopeSquadWide);
                FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.ExfilCountdown,
                        ExfilCountdown.FromValue(point.Settings.Name, point.ExfiltrationStartTime, scopeOwner, scopeSquadWide), true);
            }
            _countdownPoints.Add(point);
        }
    }

    /// <summary>
    /// True when the point is a paid exfiltration point ("car extract"): its only
    /// requirement is the cash handover to the driver.
    /// </summary>
    internal static bool IsPaidExfiltrationPoint(ExfiltrationPoint point)
    {
        try
        {
            return point != null && point.TransferItemRequirement != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the point already counts this player as paid. Two native signals are
    /// combined, because <c>[CarExtract]</c> must never let a second handover
    /// through: the requirement bookkeeping is keyed by profile id
    /// (<see cref="ExfiltrationPoint.HasMetRequirements(string)"/>), while the
    /// payment queue holds per-raid numeric player ids. A check that throws never
    /// blocks a payment; it only loses the duplicate protection for that click.
    /// </summary>
    internal static bool HasPlayerPaid(ExfiltrationPoint point, Player player)
    {
        try
        {
            if (point == null || player == null)
            {
                return false;
            }

            if (point.HasMetRequirements(player.ProfileId))
            {
                return true;
            }

            var queued = point.QueuedPlayers;
            if (queued == null)
            {
                return false;
            }

            return queued.Contains(player.Id) || queued.Contains(player.RaidId);
        }
        catch (System.Exception exception)
        {
            FikaGlobals.LogWarning($"[CarExtract] failed to read the paid state of " +
                $"'{point?.Settings?.Name}': {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Finds an exfiltration point (regular or secret) by its trigger name.
    /// Used by the <c>[CarExtract]</c> payment guard on the host.
    /// </summary>
    internal static ExfiltrationPoint FindExfiltrationPoint(string pointName)
    {
        if (string.IsNullOrEmpty(pointName))
        {
            return null;
        }

        var controller = ExfiltrationController.Instance;
        if (controller == null)
        {
            return null;
        }

        var points = controller.ExfiltrationPoints;
        if (points != null)
        {
            for (var i = 0; i < points.Length; i++)
            {
                if (points[i] != null && points[i].Settings?.Name == pointName)
                {
                    return points[i];
                }
            }
        }

        var secretPoints = controller.SecretExfiltrationPoints;
        if (secretPoints != null)
        {
            for (var i = 0; i < secretPoints.Length; i++)
            {
                if (secretPoints[i] != null && secretPoints[i].Settings?.Name == pointName)
                {
                    return secretPoints[i];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Describes the items of a car extract payment descriptor for the
    /// <c>[CarExtract]</c> log lines.
    /// </summary>
    internal static string DescribePaymentItems(EFT.InventoryLogic.Operations.TransferExfiltrationPaymentOperationDescriptor descriptor)
    {
        try
        {
            var items = descriptor?.Items;
            if (items == null || items.Length == 0)
            {
                return "no items";
            }

            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (i > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(item.ItemId).Append(" x").Append(item.NumberToDestroy);
            }
            return builder.ToString();
        }
        catch (System.Exception exception)
        {
            return $"items unavailable ({exception.Message})";
        }
    }

    private class ExtractionPlayerHandler(Player player, ExfiltrationPoint point, float startTime)
    {
        public readonly FikaPlayer Player = player.TryCast<FikaPlayer>()
            ?? throw new System.InvalidOperationException("Extraction handler received a non-Fika player");
        public readonly ExfiltrationPoint ExfilPoint = point;
        public readonly float StartTime = startTime;
    }
}
