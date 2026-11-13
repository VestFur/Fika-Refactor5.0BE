using Diz.Binding;
using EFT.Settings.Sound;
using System;
using System.Collections.Generic;
using System.Threading;
using Dissonance;
using EFT;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using static EFT.Player;

namespace Fika.Core.Networking.VOIP;

class FikaVOIPController : Il2CppSystem.Object, IPlayerVoipController, IDisposable
{

    public FikaVOIPController(System.IntPtr pointer) : base(pointer) { }


    // SPT 5 exposes DateTime/TimeSpan through Il2CppSystem.  Keep the VOIP
    // state machine on managed primitives and convert only at API boundaries;
    // this preserves the original arithmetic and avoids mixing the two
    // otherwise incompatible struct types.
    private static DateTime NowManaged => DateTimeExtensions.UtcNow.ToManaged();
    private static readonly TimeSpan _hearingDetectionTime = TimeSpan.FromSeconds(2.0);

    public FikaVOIPController(FikaPlayer localPlayer, SoundSettingsGroup soundSettings)
        : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaVOIPController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Il2CppSystem.Object>(this, []);
        LocalPlayer = localPlayer;
        SoundSettings = soundSettings;
        var pushToTalkSettings = FikaGlobals.VOIPHandler.PushToTalkSettings;
        BlockingTime = TimeSpan.FromSeconds(pushToTalkSettings.BlockingTime);
        SpeakingSecondsInterval = TimeSpan.FromSeconds(pushToTalkSettings.SpeakingSecondsInterval);
        SpeakingSecondsLimit = TimeSpan.FromSeconds(pushToTalkSettings.SpeakingSecondsLimit);
        SpeakDelayBetweenLimit = SpeakingSecondsInterval - SpeakingSecondsLimit;
        Status = new();
        HasInteraction = new();
        TalkDetected = new();

        _checker = LimitChecker.Create(pushToTalkSettings.ActivationsLimit,
            pushToTalkSettings.ActivationsInterval, pushToTalkSettings.SpeakingSecondsInterval);
        _offState = new();
        _readyState = new();
        _talkingState = new();
        _limitedState = new();
        _blockedState = new();
        _bannedState = BannedState.Create(this);
        _microphoneFailState = new();
        _currentState = _offState;
        _currentState.Controller = this;
        _enabled = soundSettings.VoipEnabled.Value;
        _requestedDevice = soundSettings.VoipDevice.Value;
        RefreshMicrophone();

        // Keep the exact native delegates and unsubscribe actions alive. Bindings
        // exist even when no microphone is present, so plugging one in can recover.
        _voipToggle = (Action<bool>)(value => MainThreadDispatcher.Post(() => ToggleVOIP(value)));
        _deviceChanged = (Action<string>)(value => MainThreadDispatcher.Post(() => ChangeDevice(value)));
        try { _unbindEnabled = soundSettings.VoipEnabled.Subscribe(_voipToggle); }
        catch (Exception ex) { FikaGlobals.LogError($"Failed to subscribe VOIP setting: {ex}"); }
        try { _unbindDevice = soundSettings.VoipDevice.Subscribe(_deviceChanged); }
        catch (Exception ex) { FikaGlobals.LogError($"Failed to subscribe VOIP device: {ex}"); }
    }

    private readonly MicrophoneFailState _microphoneFailState;
    private readonly Il2CppSystem.Action<bool> _voipToggle;
    private readonly Il2CppSystem.Action<string> _deviceChanged;
    private Il2CppSystem.Action _unbindEnabled;
    private Il2CppSystem.Action _unbindDevice;
    private string _requestedDevice;
    private string _activeDevice;
    private IntPtr _activeComms;
    private DissonanceComms _boundComms;
    private bool _microphoneAvailable;
    private bool _deviceUnavailable;
    private float _nextDeviceCheck;
    private VOIPState _suspendedLimit;
    internal bool IsTalking => !_disposed && ReferenceEquals(_currentState, _talkingState);

    private void ChangeDevice(string device)
    {
        if (_disposed) return;
        _requestedDevice = device;
        RefreshMicrophone();
    }

    private void RefreshMicrophone()
    {
        if (_disposed) return;
        _nextDeviceCheck = UnityEngine.Time.unscaledTime + 1f;
        var comms = DissonanceComms;
        _deviceUnavailable = string.Equals(_requestedDevice, "Settings/UnavailablePressType", StringComparison.Ordinal);
        string selected = null;
        // Unity's live device list does not reuse SoundSettingsGroup's cached
        // startup CheckMicrophone result after an unplug/replug.
        var devices = UnityEngine.Microphone.devices;
        if (devices != null && devices.Length > 0)
        {
            if (!string.IsNullOrEmpty(_requestedDevice) && _requestedDevice != "Default" &&
                _requestedDevice != "Settings/UnavailablePressType")
            {
                foreach (var device in devices)
                    if (string.Equals(device, _requestedDevice, StringComparison.Ordinal)) { selected = device; break; }
            }
            if (selected == null && !_deviceUnavailable)
            {
                var preferred = SoundSettingsGroup.DefaultMicrophone;
                if (!string.IsNullOrEmpty(preferred))
                {
                    foreach (var device in devices)
                        if (string.Equals(device, preferred, StringComparison.Ordinal)) { selected = device; break; }
                }
                selected ??= devices[0];
            }
        }
        var available = !_deviceUnavailable && comms != null && selected != null;
        var changed = available != _microphoneAvailable || selected != _activeDevice ||
            (comms == null ? IntPtr.Zero : comms.Pointer) != _activeComms;
        if (changed)
        {
            EndCurrentTalk();
            if (_boundComms != null && _boundComms.Pointer != (comms == null ? IntPtr.Zero : comms.Pointer))
                _boundComms.IsMuted = true;
            _microphoneAvailable = available;
            _activeDevice = selected;
            _activeComms = comms == null ? IntPtr.Zero : comms.Pointer;
            _boundComms = comms;
            if (comms != null)
            {
                comms.IsMuted = true;
                if (available)
                {
                    comms.MicrophoneName = selected;
                    FikaGlobals.LogInfo($"VoipMicrophone set device: {selected}");
                }
            }
        }
        if (changed || _currentState == _offState || _currentState == _microphoneFailState)
            ApplyAvailability();
    }

    private void EndCurrentTalk()
    {
        if (_currentState == _talkingState) _currentState.StopTalk();
        if (DissonanceComms != null) DissonanceComms.IsMuted = true;
    }

    private void ApplyAvailability()
    {
        var limit = (_currentState == _limitedState || _currentState == _blockedState)
            ? _currentState : _suspendedLimit;
        if (limit != null && limit.TimeSpan_0 <= TimeSpan.Zero) limit = null;
        VOIPState state;
        if (!_enabled || _forceMute || _deviceUnavailable) state = _offState;
        else if (!_microphoneAvailable) state = _microphoneFailState;
        else if (!method_10().Boolean_0) state = _bannedState;
        else if (limit != null) state = limit;
        else
        {
            _suspendedLimit = null;
            state = _readyState;
        }
        _suspendedLimit = limit;
        // Re-entering a suspended cooldown must not restart its duration.
        if (ReferenceEquals(state, _suspendedLimit) && !ReferenceEquals(state, _currentState))
        {
            _currentState.Controller = null;
            _currentState = state;
            state.Controller = this;
            method_4(state.Status);
            Status.Value = state.Status;
        }
        else method_3(state);
        if (DissonanceComms != null)
        {
            DissonanceComms.IsMuted = true;
            DissonanceComms.MuteAllRemoteVoices = !_enabled || _forceMute || _deviceUnavailable;
        }
        // method_3 intentionally skips same-state entry. Initial Off still needs
        // to update the native player flag and the UI binding.
        method_4(_currentState.Status);
        Status.Value = _currentState.Status;
    }

    private void ToggleVOIP(bool enabled)
    {
        if (_disposed) return;
        EndCurrentTalk();
        _enabled = enabled;
        ApplyAvailability();
    }

    public abstract class VOIPState
    {
        public abstract EVoipControllerStatus Status { get; }
        public FikaVOIPController Controller { get; set; }
        public LimitChecker LimitChecker
        {
            get
            {
                return Controller._checker;
            }
        }

        public bool Bool1
        {
            get
            {
                return this == (Controller?._currentState);
            }
        }

        public DissonanceComms DissonanceComms
        {
            get
            {
                return Controller.DissonanceComms;
            }
        }

        public abstract TimeSpan TimeSpan_0 { get; }

        public virtual EVoipControllerStatus vmethod_0()
        {
            return Status;
        }

        public virtual EVoipControllerStatus ToggleTalk()
        {
            return Status;
        }

        public virtual EVoipControllerStatus StopTalk()
        {
            return Status;
        }

        public virtual void vmethod_3()
        {
        }

        public virtual void vmethod_4()
        {
            Controller.method_3(Controller._offState);
        }

        public virtual void Update()
        {

        }
    }

    public abstract class AbstractOffState : VOIPState
    {
        public override EVoipControllerStatus vmethod_0()
        {
            if (DissonanceComms != null) DissonanceComms.IsMuted = true;
            return base.vmethod_0();
        }
    }

    public abstract class AbstractOffState2 : AbstractOffState
    {
        public override TimeSpan TimeSpan_0
        {
            get
            {
                return struct510.TimeSpan_0;
            }
        }

        public override void Update()
        {
            if (struct510.Boolean_0)
            {
                Controller.method_3(Controller._readyState);
            }
        }

        protected Struct510 struct510;
    }

    public class OffState : AbstractOffState
    {
        public override EVoipControllerStatus Status
        {
            get
            {
                return EVoipControllerStatus.Off;
            }
        }

        public override TimeSpan TimeSpan_0
        {
            get
            {
                return TimeSpan.Zero;
            }
        }

        public override void vmethod_3()
        {
            Controller.TalkDetected.Value = false;
            if (Controller._bannedState.Boolean_1)
            {
                Controller.method_3(Controller._readyState);
                return;
            }
            Controller.method_3(Controller._bannedState);
        }

        public override void Update()
        {
            Controller.method_12();
        }
    }

    public class ReadyState : AbstractOffState
    {
        public override EVoipControllerStatus Status
        {
            get
            {
                return EVoipControllerStatus.Ready;
            }
        }

        public override TimeSpan TimeSpan_0
        {
            get
            {
                return TimeSpan.Zero;
            }
        }

        public override EVoipControllerStatus vmethod_0()
        {
            Controller.TalkDetected.Value = false;
            if (Controller.HasInteraction)
            {
                Controller.method_2();
            }
            return base.vmethod_0();
        }

        public override EVoipControllerStatus ToggleTalk()
        {
            if (!Controller._checker.method_2())
            {
                return method_0();
            }
            return Controller.method_3(Controller._talkingState);
        }

        private EVoipControllerStatus method_0()
        {
            return Controller.method_3(Controller._limitedState);
        }
    }

    public class TalkingState : VOIPState
    {
        public override EVoipControllerStatus Status
        {
            get
            {
                return EVoipControllerStatus.Talking;
            }
        }

        public override TimeSpan TimeSpan_0
        {
            get
            {
                return struct510.TimeSpan_0;
            }
        }

        public override EVoipControllerStatus vmethod_0()
        {
            struct510 = Controller.method_8();
            if (struct510.Boolean_0)
            {
                return Controller.method_3(Controller._limitedState);
            }
            DissonanceComms.IsMuted = false;
            Controller._nullable_0 = null;
            Controller.HasInteraction.Value = true;
            return base.vmethod_0();
        }

        public override EVoipControllerStatus ToggleTalk()
        {
            return StopTalk();
        }

        public override EVoipControllerStatus StopTalk()
        {
            LimitChecker.InsertTimeState(struct510.DateTime_1, NowManaged);
            return Controller.method_3(Controller._readyState);
        }

        public override void Update()
        {
            if (struct510.Boolean_0)
            {
                method_0();
            }
        }

        private void method_0()
        {
            var utcNow = NowManaged;
            LimitChecker.InsertTimeState(struct510.DateTime_1, utcNow);
            Controller.method_3(Controller._limitedState);
        }

        private Struct510 struct510;
    }

    public class LimitedState : AbstractOffState2
    {
        public override EVoipControllerStatus Status
        {
            get
            {
                return EVoipControllerStatus.Limited;
            }
        }

        public override EVoipControllerStatus vmethod_0()
        {
            struct510 = Controller.method_9();
            return base.vmethod_0();
        }
    }

    public class BlockedState : AbstractOffState2
    {
        public override EVoipControllerStatus Status
        {
            get
            {
                return EVoipControllerStatus.Blocked;
            }
        }

        public override EVoipControllerStatus vmethod_0()
        {
            struct510 = Controller.method_11();
            return base.vmethod_0();
        }
    }

    public class MicrophoneFailState : AbstractOffState
    {
        public override EVoipControllerStatus Status
        {
            get
            {
                return EVoipControllerStatus.MicrophoneFail;
            }
        }

        public override TimeSpan TimeSpan_0 => TimeSpan.Zero;

        public override void Update()
        {
            Controller.method_12();
        }
    }

    public class BannedState : AbstractOffState2
    {
        public static BannedState Create(FikaVOIPController controller)
        {
            return new()
            {
                struct510 = controller.method_10()
            };
        }

        public override EVoipControllerStatus Status
        {
            get
            {
                return EVoipControllerStatus.Banned;
            }
        }

        public override EVoipControllerStatus vmethod_0()
        {
            struct510 = Controller.method_10();
            Controller.method_2();
            Controller.HasInteraction.Value = true;
            return base.vmethod_0();
        }

        public bool Boolean_1
        {
            get
            {
                return struct510.Boolean_0;
            }
        }

        public override TimeSpan TimeSpan_0
        {
            get
            {
                return struct510.TimeSpan_0;
            }
        }
    }

    private Struct510 method_8()
    {
        var num = 0;
        var utcNow = NowManaged;
        var dateTime = utcNow;
        var dateTime2 = dateTime;
        var timeSpan = TimeSpan.Zero;
        for (; ; )
        {
            num++;
            var dateTime3 = dateTime - SpeakingSecondsInterval;
            var timeSpan2 = _checker.method_1(dateTime3);
            var timeSpan3 = SpeakingSecondsLimit - timeSpan2;
            if (timeSpan3.TotalSeconds < 0.1)
            {
                goto IL_00B4;
            }
            timeSpan3 -= timeSpan;
            if (timeSpan3 <= TimeSpan.Zero)
            {
                goto IL_00B4;
            }
            dateTime2 += timeSpan3;
            if (dateTime2 - utcNow >= SpeakingSecondsLimit)
            {
                break;
            }
            timeSpan += timeSpan3;
            dateTime = dateTime2;
            if (num >= 20)
            {
                goto IL_00B4;
            }
        }
        dateTime2 = utcNow + SpeakingSecondsLimit;
    IL_00B4:
        Struct510 @struct = new(in dateTime2, in utcNow);
        return @struct;
    }

    private readonly LimitChecker _checker;
    private bool _enabled = true;
    private bool _forceMute;

    private DateTime? _nullable_0;

    public FikaPlayer LocalPlayer { get; set; }
    public SoundSettingsGroup SoundSettings { get; set; }

    public DissonanceComms DissonanceComms
    {
        get
        {
            return LocalPlayer == null ? null : LocalPlayer.DissonanceComms;
        }
    }

    public TimeSpan BlockingTime { get; set; }
    public TimeSpan SpeakingSecondsInterval { get; set; }
    public TimeSpan SpeakingSecondsLimit { get; set; }
    public TimeSpan SpeakDelayBetweenLimit { get; set; }

    public BindableState<EVoipControllerStatus> Status { get; }
    public BindableState<bool> HasInteraction { get; }
    public BindableState<bool> TalkDetected { get; }
    public Il2CppSystem.TimeSpan TimeToNextStatus
    {
        get
        {
            return _currentState.TimeSpan_0.ToNative();
        }
    }
    public event Action<string> AbuseNotification;
    private readonly NativeActionEvent _nativeAbuseNotification = new();
    public void add_AbuseNotification(Il2CppSystem.Action handler) => _nativeAbuseNotification.Add(handler);
    public void remove_AbuseNotification(Il2CppSystem.Action handler) => _nativeAbuseNotification.Remove(handler);

    private readonly OffState _offState;
    private readonly ReadyState _readyState;
    private readonly TalkingState _talkingState;
    private readonly LimitedState _limitedState;
    private readonly BlockedState _blockedState;
    private readonly BannedState _bannedState;
    private VOIPState _currentState;

    public void method_2()
    {
        _nullable_0 = new DateTime?(NowManaged);
    }

    public EVoipControllerStatus method_3(VOIPState state)
    {
        if (_currentState == state)
        {
            return state.Status;
        }
        state.Controller = this;
        var @class = Interlocked.Exchange(ref _currentState, state);
        if (@class != null)
        {
            @class.Controller = null;
        }
        method_4(state.Status);
        Status.Value = state.Status;
        if (_currentState != state)
        {
            FikaGlobals.LogError("State changed on Status event!");
        }
        if (!state.Bool1)
        {
            return _currentState.Status;
        }
        return state.vmethod_0();
    }

    public void method_4(EVoipControllerStatus status)
    {
        var evoipState = LocalPlayer.VoipState;
        if (status != EVoipControllerStatus.Off)
        {
            if (status != EVoipControllerStatus.Banned)
            {
                if (status != EVoipControllerStatus.MicrophoneFail)
                {
                    if (evoipState == EVoipState.Available)
                    {
                        return;
                    }
                    evoipState = EVoipState.Available;
                }
                else
                {
                    evoipState = EVoipState.MicrophoneFail;
                }
            }
            else
            {
                evoipState = EVoipState.Banned;
            }
        }
        else
        {
            evoipState = EVoipState.Off;
        }
        LocalPlayer.VoipState = evoipState;
    }

    private Struct510 method_10()
    {
        var ban = LocalPlayer.Profile.Info.GetBan(EBanType.Voip);
        if (ban == null)
        {
            return default;
        }

        var banUntil = ban.BanUntil.ToManaged();
        return new(in banUntil);
    }

    private Struct510 method_11()
    {
        var timeSpan = BlockingTime;
        return new(timeSpan);
    }

    private Struct510 method_9()
    {
        var timeSpan = SpeakDelayBetweenLimit;
        return new(in timeSpan);
    }

    public void method_12()
    {
        var timeSpan = NowManaged - LocalPlayer.HearingDateTime.ToManaged();
        var flag = timeSpan <= _hearingDetectionTime;
        TalkDetected.Value = flag;
        if (!flag)
        {
            return;
        }
        var timeSpan2 = _hearingDetectionTime - timeSpan;
        _nullable_0 = new DateTime?(NowManaged + timeSpan2);
        HasInteraction.Value = true;
    }

    public class LimitChecker
    {
        public static LimitChecker Create(byte activationsLimit, float activationsInterval, float speakingInterval)
        {
            var maxActivations = Math.Max(10, (int)activationsLimit);
            return new LimitChecker()
            {
                _activationsInterval = TimeSpan.FromSeconds(activationsInterval),
                _timeStates = (activationsLimit > 0 ? new(maxActivations) : null),
                _activationsLimit = activationsLimit,
                _interval = TimeSpan.FromSeconds(Math.Max(activationsInterval, speakingInterval))
            };
        }

        private TimeSpan _activationsInterval;
        private List<TimeState> _timeStates;
        private byte _activationsLimit;
        private TimeSpan _interval;

        public void InsertTimeState(DateTime from, DateTime to)
        {
            if (_timeStates == null)
            {
                return;
            }

            var dateTime = NowManaged - _interval;
            var num = _timeStates.Count - 1;
            while (num >= 0 && !(_timeStates[num].To >= dateTime))
            {
                _timeStates.RemoveAt(num);
                num--;
            }
            _timeStates.Insert(0, new()
            {
                From = from,
                To = to
            });
        }

        public TimeSpan method_1(DateTime dateTime)
        {
            if (_timeStates == null)
            {
                return TimeSpan.Zero;
            }
            var timeSpan = TimeSpan.Zero;
            var i = 0;
            while (i < _timeStates.Count)
            {
                var state = _timeStates[i];
                if (!(state.To <= dateTime))
                {
                    if (!(state.From <= dateTime))
                    {
                        timeSpan += state.Duration;
                        i++;
                        continue;
                    }
                    timeSpan += state.To - dateTime;
                }
                return timeSpan;
            }
            return timeSpan;
        }

        public bool method_2()
        {
            if (_timeStates == null)
            {
                return true;
            }
            if (_timeStates.Count < _activationsLimit)
            {
                return true;
            }
            var dateTime = NowManaged - _activationsInterval;
            var from = _timeStates[(_activationsLimit - 1)].From;
            return dateTime >= from;
        }

        public struct TimeState
        {
            public readonly TimeSpan Duration
            {
                get
                {
                    return To - From;
                }
            }

            public DateTime From;
            public DateTime To;
        }
    }

    public struct Struct510
    {
        public Struct510(in DateTime dateTime, in DateTime created)
        {
            DateTime_1 = created;
            Created = dateTime;
        }

        public Struct510(in DateTime dateTime)
        {
            DateTime_1 = NowManaged;
            Created = dateTime;
        }

        public Struct510(in TimeSpan timeSpan)
        {
            DateTime_1 = NowManaged;
            Created = DateTime_1 + timeSpan;
        }

        public readonly bool Boolean_0
        {
            get
            {
                return TimeSpan_0 <= TimeSpan.Zero;
            }
        }

        // Created
        public DateTime Created;
        public DateTime DateTime_1;

        public readonly TimeSpan TimeSpan_0
        {
            get
            {
                var timeSpan = Created - NowManaged;
                if (!(timeSpan > TimeSpan.Zero))
                {
                    return TimeSpan.Zero;
                }
                return timeSpan;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (DissonanceComms != null) DissonanceComms.IsMuted = true;
        if (_boundComms != null) _boundComms.IsMuted = true;
        _boundComms = null;
        _nativeAbuseNotification.Clear();
        AbuseNotification = null;
        try { _unbindEnabled?.Invoke(); }
        catch (Exception exception) { FikaGlobals.LogError($"Unbind VOIP enabled: {exception}"); }
        try { _unbindDevice?.Invoke(); }
        catch (Exception exception) { FikaGlobals.LogError($"Unbind VOIP device: {exception}"); }
        _unbindEnabled = null;
        _unbindDevice = null;
        HasInteraction.Value = false;
        TalkDetected.Value = false;
        Status.Value = EVoipControllerStatus.Off;
        if (LocalPlayer != null && LocalPlayer.VoipController?.Pointer == Pointer)
            LocalPlayer.VoipController = null;
        LocalPlayer = null;
        SoundSettings = null;
    }

    public void Update()
    {
        if (_disposed) return;
        if (UnityEngine.Time.unscaledTime >= _nextDeviceCheck)
        {
            // Also recover from an unavailable native binding subscription.
            var enabled = SoundSettings.VoipEnabled.Value;
            if (enabled != _enabled) ToggleVOIP(enabled);
            _requestedDevice = SoundSettings.VoipDevice.Value;
            RefreshMicrophone();
        }
        if (_forceMute) return;
        _currentState.Update();
        method_1();
    }

    public void method_1()
    {
        if (_nullable_0 != null && !(_nullable_0.Value > NowManaged))
        {
            _nullable_0 = null;
            HasInteraction.Value = false;
        }
    }

    public void ForceMuteVoIP(bool enable)
    {
        if (_disposed) return;
        EndCurrentTalk();
        _forceMute = enable;
        ApplyAvailability();
    }

    public void ReportAbuse()
    {
        // Do nothing
    }

    public EVoipControllerStatus StopTalk()
    {
        return _disposed ? EVoipControllerStatus.Off : _currentState.StopTalk();
    }

    public EVoipControllerStatus ToggleTalk()
    {
        if (_disposed) return EVoipControllerStatus.Off;
        if (_forceMute || !_enabled || !_microphoneAvailable) return _currentState.Status;
        return _currentState.ToggleTalk();
    }

    private bool _disposed;

    public void ReceiveAbuseNotification(string reporterId)
    {
        if (_disposed) return;
        AbuseNotification?.Invoke(reporterId);
        _nativeAbuseNotification.Invoke();
    }
}
