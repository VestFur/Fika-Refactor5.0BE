using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Interactive;
using EFT.Interactive.SecretExfiltrations;
using Fika.Core;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Il2CppInterop.Runtime.Injection;

namespace Fika.Headless.Classes.GameMode;

/// <summary>
/// Owns the host-side state of shared exits. Remote players extract through
/// their own FikaExfilManager; a headless host has no local player to extract.
/// </summary>
public sealed class HeadlessExfilManager : MonoBehaviour
{
    public HeadlessExfilManager(IntPtr pointer) : base(pointer) { }

    public HeadlessExfilManager() : this(ClassInjector.DerivedConstructorPointer<HeadlessExfilManager>())
    {
        ClassInjector.DerivedConstructorBody(this);
    }

    private HeadlessGame _game;
    private System.Action<ExfiltrationPoint, EExfiltrationStatus> _statusChanged;
    private readonly Dictionary<IntPtr, ExfiltrationPoint> _boundPoints = [];
    private readonly Dictionary<IntPtr, ExfiltrationPoint> _countdownPoints = [];

    private void Awake()
    {
        _game = gameObject.GetComponent<HeadlessGame>();
        _statusChanged = (System.Action<ExfiltrationPoint, EExfiltrationStatus>)OnStatusChanged;
    }

    public void Run(ExfiltrationPoint[] points, SecretExfiltrationPoint[] secretPoints)
    {
        // Which exits wait for a manual activation is what tells a green flare exit from a plain
        // one; the classification also feeds the countdown suppression below.
        Fika.Core.Main.Components.FikaFlareExfil.CaptureInitialStatus(points, secretPoints);
        ReleaseBindings(false);

        foreach (var point in points ?? [])
        {
            Bind(point);
            if (FikaPlugin.Instance.Settings.DynamicVExfils && point.Settings.PlayersCount > 0 &&
                point.Settings.PlayersCount < FikaSingleton<IFikaNetworkManager>.Instance.PlayerAmount)
            {
                point.Settings.PlayersCount = FikaSingleton<IFikaNetworkManager>.Instance.PlayerAmount;
            }
        }

        foreach (var point in secretPoints ?? [])
            Bind(point);
    }

    private void Bind(ExfiltrationPoint point)
    {
        if (point == null || !_boundPoints.TryAdd(point.Pointer, point))
            return;

        point.OnStatusChanged += _statusChanged;
        // A point may already be counting down when the host finishes setup.
        if (point.Status == EExfiltrationStatus.Countdown)
            _countdownPoints[point.Pointer] = point;
    }

    private void Update()
    {
        if (_game == null || _game.Status != GameStatus.Started)
            return;

        // A headless host has no FikaExfilManager, so this is the only place that re-sends the
        // current flare grants to the peers (late joiners, lost packets).
        Fika.Core.Main.Components.FikaFlareExfil.HostTick();

        foreach (var point in _countdownPoints.Values.ToArray())
        {
            if (_game.PastTime - point.ExfiltrationStartTime > point.Settings.ExfiltrationTime &&
                _countdownPoints.Remove(point.Pointer))
            {
                point.ExternalSetStatus(point.Reusable
                    ? EExfiltrationStatus.UncompleteRequirements
                    : EExfiltrationStatus.NotPresent);
            }
        }
    }

    private void OnStatusChanged(ExfiltrationPoint point, EExfiltrationStatus previousStatus)
    {
        if (_countdownPoints.ContainsKey(point.Pointer) && point.Status != EExfiltrationStatus.Countdown)
        {
            point.ExfiltrationStartTime = -100;
            _countdownPoints.Remove(point.Pointer);
        }

        if (point.Status != EExfiltrationStatus.Countdown || _countdownPoints.ContainsKey(point.Pointer))
            return;

        if (point.ExfiltrationStartTime is <= 0 and > -90)
        {
            point.ExfiltrationStartTime = _game.PastTime;
            if (Fika.Core.Main.Components.FikaFlareExfil.IsGatedPoint(point.Settings.Name))
            {
                // Identity-less countdown replication would open a flare exit on every peer. The
                // activation reaches the covered players as a grant instead.
                Fika.Core.Main.Components.FikaFlareExfil.LogCountdownSuppressed(point.Settings.Name);
            }
            else
            {
                FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.ExfilCountdown,
                    ExfilCountdown.FromValue(point.Settings.Name, point.ExfiltrationStartTime), true);
            }
        }

        _countdownPoints[point.Pointer] = point;
    }

    public void Stop() => ReleaseBindings(true);

    /// <summary>
    /// Releases every point this manager bound. Teardown runs from <see cref="OnDestroy"/>
    /// and from <see cref="Run"/> (rebinding), so it must never throw. By the time the
    /// manager is destroyed the game can already have destroyed the points (scene unload,
    /// transit, application quit); the native <c>ExfiltrationPoint.Disable()</c> then
    /// throws a NullReferenceException from inside IL2CPP. Destroyed points are skipped and
    /// every point is released on its own, so one bad point cannot abort the release of the
    /// others. The dictionary is consumed before the loop, which keeps repeated calls a no-op.
    /// </summary>
    private void ReleaseBindings(bool disablePoints)
    {
        _countdownPoints.Clear();

        // Take the bindings first: a point that fails must not leave a partially
        // released dictionary behind for a later Stop() call.
        var boundPoints = _boundPoints.Values.ToArray();
        _boundPoints.Clear();

        var skipped = 0;
        var failures = 0;
        string firstFailure = null;

        for (var i = 0; i < boundPoints.Length; i++)
        {
            var point = boundPoints[i];
            // Unity reports a destroyed point as null through the Unity object
            // operator; the native Disable() on it throws from inside IL2CPP.
            if (point == null)
            {
                skipped++;
                continue;
            }

            try
            {
                point.OnStatusChanged -= _statusChanged;
                if (disablePoints)
                    point.Disable();
            }
            catch (System.Exception exception)
            {
                failures++;
                firstFailure ??= DescribePoint(point) + ": " + exception.Message;
            }
        }

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

    private void OnDestroy() => Stop();
}
