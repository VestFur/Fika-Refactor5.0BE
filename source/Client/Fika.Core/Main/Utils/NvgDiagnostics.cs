using System;
using BSG.CameraEffects;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.InputSystem;
using UnityEngine;

namespace Fika.Core.Main.Utils;

/// <summary>
/// <c>[NvgDiag]</c> - night vision / thermal vision diagnostics and a last-resort
/// self-heal for the raid-resident camera effects.
/// <para>
/// EFT renders night vision inside <see cref="NightVision"/> and thermal vision
/// inside <see cref="ThermalVision"/>. Both are MonoBehaviours that blit their
/// material from <c>OnPreCull</c>, so Unity stops applying them the moment the
/// component is disabled - and <c>OnDisable</c> additionally tears the material,
/// the command buffer and the <c>_NightVisionOn</c> shader global down. Toggling
/// the headwear device only drives <c>Switch(bool)</c> on the already running
/// component; nothing inside the effect re-enables itself. A component that was
/// disabled once therefore stays dark for the rest of the raid even though the
/// headwear component reports "on".
/// </para>
/// <para>
/// Every line this class writes starts with <c>[NvgDiag]</c>, so a single grep
/// over <c>LogOutput.log</c> answers the three questions that separate the
/// possible causes: did the key reach the input owner, did the headwear device
/// toggle, and did the camera effect follow it.
/// </para>
/// </summary>
internal static class NvgDiagnostics
{
    /// <summary>How often the raid state is sampled, in seconds of game time.</summary>
    private const float PollInterval = 0.25f;

    /// <summary>How long device and camera effect may disagree before the self-heal fires.</summary>
    private const float MismatchGrace = 2f;

    private static float _nextPoll;
    private static string _lastState;
    private static readonly EffectWatch NightVisionWatch = new("NightVision");
    private static readonly EffectWatch ThermalVisionWatch = new("ThermalVision");

    /// <summary>
    /// Samples the device state and the camera effects once per <see cref="PollInterval"/>,
    /// logs every change and repairs the states that cannot recover on their own.
    /// Driven from <c>FikaPluginBehaviour.Update</c>.
    /// </summary>
    internal static void Tick()
    {
        // (analysis/perf-diagnostics-cleanup) The 0.25 s sample of this class feeds two things: the [NvgDiag]
        // lines and the self-heal below. With the master switch off the self-heal keeps running (it restores a
        // camera effect the game left disabled) but nothing is sampled and nothing is written.
        try
        {
            if (Time.time < _nextPoll)
            {
                return;
            }

            _nextPoll = Time.time + PollInterval;

            if (!CameraManager.Exist)
            {
                return;
            }

            var cameraManager = CameraManager.Instance;
            if (cameraManager == null)
            {
                return;
            }

            var player = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
            if (player == null)
            {
                return;
            }

            Repair(cameraManager.NightVision, NightVisionWatch, ReadDevice(player, nightVision: true));
            Repair(cameraManager.ThermalVision, ThermalVisionWatch, ReadDevice(player, nightVision: false));
            ReconcileDepthOfField(cameraManager);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Brings one vision effect back in step with its headwear device. Both rules
    /// only ever restore what the native apply path would have done anyway, and
    /// every action is logged with its reason.
    /// </summary>
    private static void Repair(Behaviour effect, EffectWatch watch, bool? deviceOn)
    {
        if (effect == null)
        {
            return;
        }

        // 1) A disabled effect can never render again. Re-enable it instead of
        //    letting the raid run on without the screen effect.
        if (!effect.enabled)
        {
            effect.enabled = true;
        }

        if (deviceOn == null)
        {
            return;
        }

        // 2) A device that just changed state gets one grace window before the
        //    effect is measured against it, so a running switch is never fought.
        if (!watch.Known || watch.LastDeviceState != deviceOn.Value)
        {
            watch.Known = true;
            watch.LastDeviceState = deviceOn.Value;
            watch.RepairTried = false;
            watch.MismatchSince = 0f;
            return;
        }

        var effectOn = IsOn(effect);
        if (effectOn == deviceOn.Value || IsSwitching(effect))
        {
            watch.MismatchSince = 0f;
            return;
        }

        if (watch.MismatchSince == 0f)
        {
            watch.MismatchSince = Time.time;
            return;
        }

        if (!watch.RepairTried && Time.time - watch.MismatchSince >= MismatchGrace)
        {
            watch.RepairTried = true;
            Switch(effect, deviceOn.Value);
        }
    }

    /// <summary>
    /// The night-vision depth of field (NightVisionZBlur) is applied by the NVG effect itself and the
    /// component has no OnDisable of its own: anything that turns the effect off without going through
    /// NightVision.Switch (the free camera writes NightVision.On directly) leaves the blur applied on the
    /// camera for the rest of the raid - the reported 'hazy, looks like depth of field' frame. This only
    /// removes the blur the NVG left behind; it never applies one and never touches either component.
    /// </summary>
    private static void ReconcileDepthOfField(CameraManager cameraManager)
    {
        try
        {
            var zBlur = cameraManager?.NightVisionZBlur;
            if (zBlur == null || !zBlur.IsApplied)
            {
                return;
            }

            var nightVision = cameraManager.NightVision;
            if (nightVision == null || nightVision.On || nightVision.InProcessSwitching)
            {
                return;
            }

            zBlur.Apply(false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Reads the headwear device state of the local player: null when no night
    /// vision / thermal device is equipped, else the state of its togglable.
    /// </summary>
    private static bool? ReadDevice(Player player, bool nightVision)
    {
        try
        {
            if (nightVision)
            {
                var component = player.NightVisionObserver?.Component;
                return component == null ? null : component.Togglable.On;
            }

            var thermal = player.ThermalVisionObserver?.Component;
            return thermal == null ? null : thermal.Togglable.On;
        }
        catch (Exception exception)
        {
            return null;
        }
    }

    private static string DescribeDevices()
    {
        var player = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
        if (player == null)
        {
            return "player=none";
        }

        return "player=" + player.ProfileId
            + " nvgDevice=" + Describe(ReadDevice(player, nightVision: true))
            + " thermalDevice=" + Describe(ReadDevice(player, nightVision: false));
    }

    private static string DescribeEffects()
    {
        if (!CameraManager.Exist)
        {
            return "camera=none";
        }

        return "nvEffect=" + DescribeEffect(CameraManager.Instance?.NightVision)
            + " thermalEffect=" + DescribeEffect(CameraManager.Instance?.ThermalVision);
    }

    private static string DescribeEffect(Behaviour effect)
    {
        if (effect == null)
        {
            return "missing";
        }

        return "(enabled=" + effect.enabled + ",on=" + IsOn(effect) + ",switching=" + IsSwitching(effect) + ")";
    }

    private static string Describe(bool? on) => on == null ? "none" : (on.Value ? "on" : "off");

    private static bool IsOn(Behaviour effect)
    {
        if (effect.TryCast<NightVision>() is { } nightVision)
        {
            return nightVision.On;
        }

        if (effect.TryCast<ThermalVision>() is { } thermal)
        {
            return thermal.On;
        }

        return false;
    }

    private static bool IsSwitching(Behaviour effect)
    {
        if (effect.TryCast<NightVision>() is { } nightVision)
        {
            return nightVision.InProcessSwitching;
        }

        if (effect.TryCast<ThermalVision>() is { } thermal)
        {
            return thermal.InProcessSwitching;
        }

        return false;
    }

    private static void Switch(Behaviour effect, bool on)
    {
        if (effect.TryCast<NightVision>() is { } nightVision)
        {
            nightVision.Switch(on);
            return;
        }

        if (effect.TryCast<ThermalVision>() is { } thermal)
        {
            thermal.Switch(on);
        }
    }

    /// <summary>Change tracker for one vision effect.</summary>
    private sealed class EffectWatch(string label)
    {
        internal string Label { get; } = label;

        internal bool Known { get; set; }

        internal bool LastDeviceState { get; set; }

        internal float MismatchSince { get; set; }

        internal bool RepairTried { get; set; }
    }
}
