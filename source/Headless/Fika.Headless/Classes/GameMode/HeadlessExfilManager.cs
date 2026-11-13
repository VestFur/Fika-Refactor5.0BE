using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Interactive;
using EFT.Interactive.SecretExfiltrations;
using Fika.Core;
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
    private Il2CppSystem.Action<ExfiltrationPoint, EExfiltrationStatus> _statusChanged;
    private readonly Dictionary<IntPtr, ExfiltrationPoint> _boundPoints = [];
    private readonly Dictionary<IntPtr, ExfiltrationPoint> _countdownPoints = [];

    private void Awake()
    {
        _game = gameObject.GetComponent<HeadlessGame>();
        _statusChanged = (Il2CppSystem.Action<ExfiltrationPoint, EExfiltrationStatus>)OnStatusChanged;
    }

    public void Run(ExfiltrationPoint[] points, SecretExfiltrationPoint[] secretPoints)
    {
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
            FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.ExfilCountdown,
                ExfilCountdown.FromValue(point.Settings.Name, point.ExfiltrationStartTime), true);
        }

        _countdownPoints[point.Pointer] = point;
    }

    public void Stop() => ReleaseBindings(true);

    private void ReleaseBindings(bool disablePoints)
    {
        _countdownPoints.Clear();
        foreach (var point in _boundPoints.Values.ToArray())
        {
            point.OnStatusChanged -= _statusChanged;
            if (disablePoints)
                point.Disable();
        }
        _boundPoints.Clear();
    }

    private void OnDestroy() => Stop();
}
