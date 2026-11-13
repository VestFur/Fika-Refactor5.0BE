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
    private System.Action<ExfiltrationPoint, Player> _startExtraction;
    private System.Action<ExfiltrationPoint, Player> _cancelExtraction;
    private System.Action<ExfiltrationPoint, EExfiltrationStatus> _statusChanged;
    private System.Action<ExfiltrationPoint, EExfiltrationStatus> _gameStatusChanged;
    private System.Action<ExfiltrationPoint, EExfiltrationStatus> _newSecretExit;
    private System.Action<string, bool> _pointFound;
    private readonly Dictionary<System.IntPtr, ExfiltrationPoint> _boundPoints = [];

    /// <summary>
    /// Reused snapshots for the two walks in <see cref="Update"/>. Those walks need a copy of the
    /// list they consume, because Extract and ExternalSetStatus can cancel handlers or Stop the
    /// manager while the walk runs - but ToArray() built that copy as a fresh managed array on every
    /// frame for the whole raid. The buffers keep the copy and drop the allocation
    /// (analysis/midraid-clr-failfast/REPORT.md).
    /// </summary>
    private readonly List<ExtractionPlayerHandler> _handlerSnapshot = [];
    private readonly List<ExfiltrationPoint> _countdownSnapshot = [];

    protected void Awake()
    {
        _game = gameObject.GetComponent<CoopGame>();
        _startExtraction = (System.Action<ExfiltrationPoint, Player>)ExfiltrationPoint_OnStartExtraction;
        _cancelExtraction = (System.Action<ExfiltrationPoint, Player>)ExfiltrationPoint_OnCancelExtraction;
        // ExfiltrationPoint.OnStatusChanged is a managed event in 47888.
        _statusChanged = ExfiltrationPoint_OnStatusChanged;
        _gameStatusChanged = _game.OnStatusChangedHandler;
        _newSecretExit = _game.ShowNewSecretExit;
        _pointFound = (System.Action<string, bool>)SecretExfiltrationPoint_OnPointFoundEvent;
        _playerHandlers = [];
        _countdownPoints = [];
        _exfiltrationPoints = [];
        _secretExfiltrationPoints = [];
    }

    /// <summary>
    /// (analysis/perf-round2) The extraction manager's per-frame walk is one slot of the frame-cost probe.
    /// </summary>
    protected void Update()
    {
        
        try
        {
            UpdateMeasured();
        }
        finally
        {
            
        }
    }

    private void UpdateMeasured()
    {
        if (_exfiltrationPoints == null || _secretExfiltrationPoints == null)
        {
            return;
        }

        // (analysis/stutter-frame-skip) Three slots instead of one: the 2026-10-13 20:01 raid read
        // exfilManager=554.29ms max=552.99ms, i.e. a single call of this method held a whole frame, and the
        // three things below have nothing in common. The flare pair and the 1 Hz [ExtractDiag] sample are
        // diagnostics; 

        // Host only: re-send the current flare grants so a player that joined after the flare
        // still sees the activated point.
        FikaFlareExfil.HostTick();

        // Both machines: takes a flare-success banner this machine displayed off the screen again
        // after its display time (nothing in the game owns a notification Fika displayed).
        FikaFlareExfil.FeedbackTick();
        

        // One [ExtractDiag] attempt line per second at most. The same call releases a transit's
        // exfiltration ban that no longer has a transit to belong to, which is what keeps a
        // transfer that never took the player from stranding him at every exit of the raid.
        if (FikaExtractDiag.ShouldSample())
        {
            
            FikaExtractDiag.Sample(_game, _exfiltrationPoints, DescribeExtractionHandlers(), _countdownPoints.Count);
            
        }

        

        // Extract/ExternalSetStatus can synchronously cancel handlers or Stop
        // the manager. Iterate snapshots, consuming each entry before callbacks.
        _handlerSnapshot.Clear();
        _handlerSnapshot.AddRange(_playerHandlers);
        for (var i = 0; i < _handlerSnapshot.Count; i++)
        {
            var playerHandler = _handlerSnapshot[i];
            if (playerHandler.StartTime + playerHandler.ExfilPoint.Settings.ExfiltrationTime - _game.PastTime > 0)
            {
                continue;
            }

            // A flare gated exit only opens for the players the activation covers. The handler
            // stays in the list, so a grant that arrives later still extracts this player.
            if (!FikaFlareExfil.MayExtract(playerHandler.Player.ProfileId, playerHandler.ExfilPoint.Settings.Name))
            {
                continue;
            }

            if (_playerHandlers.Remove(playerHandler))
            {
                _game.ExitLocation = playerHandler.ExfilPoint.Settings.Name;
                FikaExtractDiag.ExtractCall(_game, playerHandler.ExfilPoint.Settings.Name, playerHandler.Player, "handler");
                _game.Extract(playerHandler.Player, playerHandler.ExfilPoint);
                FikaExtractDiag.ExtractReturned(_game, playerHandler.ExfilPoint.Settings.Name, playerHandler.Player);
            }
        }

        _countdownSnapshot.Clear();
        _countdownSnapshot.AddRange(_countdownPoints);
        for (var i = 0; i < _countdownSnapshot.Count; i++)
        {
            var exfiltrationPoint = _countdownSnapshot[i];
            if (_game.PastTime - exfiltrationPoint.ExfiltrationStartTime > exfiltrationPoint.Settings.ExfiltrationTime &&
                _countdownPoints.Remove(exfiltrationPoint))
            {
                var extracted = false;
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

                    if (!exfiltrationPoint.UnmetRequirements(player).AsManaged().Any()
                        && FikaFlareExfil.MayExtract(player.ProfileId, exfiltrationPoint.Settings.Name))
                    {
                        _game.ExitLocation = exfiltrationPoint.Settings.Name;
                        if (player.TryCast<FikaPlayer>() is { } localPlayer)
                        {
                            FikaExtractDiag.ExtractCall(_game, exfiltrationPoint.Settings.Name, localPlayer, "countdown");
                            _game.Extract(localPlayer, exfiltrationPoint);
                            FikaExtractDiag.ExtractReturned(_game, exfiltrationPoint.Settings.Name, localPlayer);
                            // Only the local player is extracted by this manager;
                            // extraction can mutate Entered or stop the manager.
                            extracted = true;
                            break;
                        }
                    }
                }

                // Keep a gated point on the countdown list while this player is not covered by
                // the flare activation: a later grant extracts from the same countdown.
                if (!extracted
                    && FikaFlareExfil.IsGatedPoint(exfiltrationPoint.Settings.Name)
                    && !FikaFlareExfil.LocalPlayerAllowed(exfiltrationPoint.Settings.Name))
                {
                    _countdownPoints.Add(exfiltrationPoint);
                    continue;
                }

                exfiltrationPoint.ExternalSetStatus(exfiltrationPoint.Reusable
                    ? EExfiltrationStatus.UncompleteRequirements
                    : EExfiltrationStatus.NotPresent);
            }
        }

        
    }

    public void Run(ExfiltrationPoint[] exfilPoints, SecretExfiltrationPoint[] secretExfilPoints)
    {
        // Read the statuses before anything in this manager touches them: which exits wait for a
        // manual activation is what tells a green flare point from a plain one.
        FikaFlareExfil.CaptureInitialStatus(exfilPoints, secretExfilPoints);
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

        // A location reached through a transit can inherit the exfiltration ban and the disabled
        // exit interaction the previous location applied when the player committed to the
        // transfer. Release that pair before he can walk to an exit.
        FikaExtractDiag.OnRaidBound(_game, exfilPoints.Length, secretExfilPoints.Length);
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

    /// <summary>
    /// Releases every point this manager bound: the Fika handlers come off and the
    /// points that are not retained are disabled natively. Teardown runs from
    /// <see cref="OnDestroy"/>, from <c>CoopGame.StopExit</c> and from
    /// <c>CoopGame.StopFromCancel</c>, and those callers keep running the raid exit
    /// sequence right after this call, so this method must never throw. By the time
    /// the manager is destroyed the game can already have destroyed the points
    /// (scene unload, transit, application quit); the native
    /// <c>ExfiltrationPoint.Disable()</c> then throws a NullReferenceException from
    /// inside IL2CPP. Destroyed points are skipped and every point is released on
    /// its own, so one bad point cannot abort the release of the others. The arrays
    /// are consumed before the loops, which keeps repeated calls a no-op.
    /// </summary>
    private void ReleaseBindings(HashSet<System.IntPtr> retainedPoints)
    {
        _playerHandlers?.Clear();
        _countdownPoints?.Clear();

        // Take the arrays first: a point that fails must not leave a partially
        // released list behind for a later Stop() call.
        var exfiltrationPoints = _exfiltrationPoints;
        var secretExfiltrationPoints = _secretExfiltrationPoints;
        _exfiltrationPoints = [];
        _secretExfiltrationPoints = [];

        var skipped = 0;
        var failures = 0;
        string firstFailure = null;

        if (exfiltrationPoints != null)
        {
            for (var i = 0; i < exfiltrationPoints.Length; i++)
            {
                var exfiltrationPoint = exfiltrationPoints[i];
                // Unity reports a destroyed point as null through the Unity object
                // operator; the native Disable() on it throws from inside IL2CPP.
                if (exfiltrationPoint == null)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    UpdateExfilPointFromServer(exfiltrationPoint, false);
                    exfiltrationPoint.OnStatusChanged -= _gameStatusChanged;
                    if (retainedPoints == null || !retainedPoints.Contains(exfiltrationPoint.Pointer))
                        exfiltrationPoint.Disable();
                }
                catch (System.Exception exception)
                {
                    failures++;
                    firstFailure ??= DescribePoint(exfiltrationPoint) + ": " + exception.Message;
                }
            }
        }

        if (secretExfiltrationPoints != null)
        {
            for (var i = 0; i < secretExfiltrationPoints.Length; i++)
            {
                var secretExfiltrationPoint = secretExfiltrationPoints[i];
                if (secretExfiltrationPoint == null)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    UpdateExfilPointFromServer(secretExfiltrationPoint, false);
                    secretExfiltrationPoint.OnStatusChanged -= _gameStatusChanged;
                    secretExfiltrationPoint.OnStatusChanged -= _newSecretExit;
                    secretExfiltrationPoint.OnPointFoundEvent -= _pointFound;
                    if (retainedPoints == null || !retainedPoints.Contains(secretExfiltrationPoint.Pointer))
                        secretExfiltrationPoint.Disable();
                }
                catch (System.Exception exception)
                {
                    failures++;
                    firstFailure ??= DescribePoint(secretExfiltrationPoint) + ": " + exception.Message;
                }
            }
        }

        foreach (var point in _boundPoints.Values.ToArray())
        {
            try
            {
                UpdateExfilPointFromServer(point, false);
            }
            catch (System.Exception exception)
            {
                failures++;
                firstFailure ??= DescribePoint(point) + ": " + exception.Message;
            }
        }

        _boundPoints.Clear();

        if (failures > 0)
        {
            FikaGlobals.LogWarning($"[ExfilTeardown] released exfil bindings: {skipped} destroyed, {failures} failed; first: {firstFailure}");
        }
        else if (skipped > 0)
        {
            FikaGlobals.LogInfo($"[ExfilTeardown] skipped {skipped} already destroyed exfiltration point(s)");
        }
    }

    /// <summary>
    /// Best-effort point name for the teardown log lines: a point that is already
    /// destroyed can throw while reading its own settings.
    /// </summary>
    private static string DescribePoint(ExfiltrationPoint point)
    {
        try
        {
            var name = point?.Settings?.Name;
            return string.IsNullOrEmpty(name) ? "<unnamed>" : name;
        }
        catch
        {
            return "<unreadable>";
        }
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

    /// <summary>
    /// Point names of the extraction handlers this manager currently holds for the local player,
    /// for the <c>[ExtractDiag]</c> attempt line. Read only: a point that is already destroyed
    /// reports nothing, and the extraction path itself never depends on this list.
    /// </summary>
    internal List<string> DescribeExtractionHandlers()
    {
        var names = new List<string>();
        for (var i = 0; i < _playerHandlers.Count; i++)
        {
            try
            {
                var name = _playerHandlers[i].ExfilPoint?.Settings?.Name;
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }
            }
            catch (System.Exception)
            {
                // A destroyed point reports nothing.
            }
        }

        return names;
    }

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
                if (FikaFlareExfil.IsGatedPoint(point.Settings.Name))
                {
                    // An identity-less countdown would open the point on every peer. The flare
                    // activation reaches each covered player as a grant instead.
                    FikaFlareExfil.LogCountdownSuppressed(point.Settings.Name);
                }
                else
                {
                    FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.ExfilCountdown,
                            ExfilCountdown.FromValue(point.Settings.Name, point.ExfiltrationStartTime), true);
                }
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