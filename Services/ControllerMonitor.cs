using Microsoft.UI.Dispatching;
using System.Diagnostics;

namespace ControllerHelper.Services;

public sealed class ControllerMonitor
{
    private readonly DispatcherQueueTimer _timer;
    private readonly GameInputService? _gameInput;
    private readonly Dictionary<string, ControllerDevice> _previous = [];
    private long? _hotkeyStarted;
    private bool _latched;
    // private int _pollNumber;
    private string _modifier = "A";
    private string _trigger = "View";
    private string? _selectedId;
    private string? _selectedFingerprint;
    private Action<string>? _learning;
    private bool _learningArmed;
    private ControllerDevice[] _devices = [];
    private string? _error;
    private readonly ControllerInputRouter _input = new();
    private long _inputVersion = -1;
    private string? _lastSelectedId;
    private bool _requireNeutral = true;
    private bool _hotkeyRequiresNeutral = true;
    private uint _pendingAction;
    private bool _stopped;
    private readonly EmergencyHotkey _emergencyHotkey = new();
    private bool _emergencyEnabled;
    private string _emergencyModifier = "LeftShoulder", _emergencyTrigger = "RightShoulder";
    internal Func<long> Timestamp { get; set; } = Stopwatch.GetTimestamp;
#if CONTROLLERHELPER_DIAGNOSTICS
    private int _tracePollCount;
#endif

    public event Action? HotkeyPressed;
    public event Action? EmergencyReturnPressed;
    // public event Action<string>? StateChanged;
    public event Action? DevicesChanged;
    public bool OverlayOpen => _input.Current?.Mode is ControllerInputMode.Overlay or ControllerInputMode.Confirmation;
    public bool FlydigiControlActive => _input.Current?.Mode == ControllerInputMode.Flydigi;
    internal bool ConfirmationActive => _input.Current?.Mode == ControllerInputMode.Confirmation;
    internal ControllerInputScope AcquireInput(ControllerInputMode mode, Action? confirm = null,
        Action? cancel = null, Action<int>? move = null, Action<int>? verticalMove = null,
        Action<ControllerDevice, ControllerDevice?>? flydigi = null, Func<bool>? hasFocus = null,
        Action? suspend = null)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        _learning = null;
        return _input.Acquire(mode, confirm, cancel, move, verticalMove, flydigi, hasFocus, suspend);
    }
    public IReadOnlyList<ControllerDevice> Devices => _devices;

    public ControllerMonitor(DispatcherQueue dispatcher)
    {
        try { _gameInput = new GameInputService(); }
        catch (Exception error) { _error = error.Message; ErrorLog.Write("gameinput.initialize", error); }
        _input.Changed += UpdateInputPolicy;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = ControllerPollingCadence.Background;
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _input.Current?.Suspend?.Invoke();
        _input.Changed -= UpdateInputPolicy;
        _timer.Stop();
        _gameInput?.Dispose();
    }

    private void UpdateInputPolicy()
    {
        _gameInput?.SetExclusiveForegroundInput(_input.Current is not null);
        UpdatePollingInterval();
    }

    private void UpdatePollingInterval()
    {
        if (_stopped) return;
        TimeSpan interval = _input.Current is not null || _learning is not null
            ? ControllerPollingCadence.Interactive : ControllerPollingCadence.Background;
        if (_timer.Interval != interval) _timer.Interval = interval;
    }

    public void Configure(string? deviceId, string? fingerprint, string modifier, string trigger,
        bool emergencyEnabled = false, string emergencyModifier = "LeftShoulder", string emergencyTrigger = "RightShoulder")
    {
        if (modifier == trigger || string.IsNullOrWhiteSpace(modifier) || string.IsNullOrWhiteSpace(trigger))
            throw new ArgumentException(ControllerHelper.Localization.Text.Get("ControllerMonitor_052"));
        EmergencyHotkey.Validate(emergencyEnabled, emergencyModifier, emergencyTrigger, modifier, trigger);
        _emergencyEnabled = emergencyEnabled;
        _emergencyModifier = emergencyModifier;
        _emergencyTrigger = emergencyTrigger;
        _emergencyHotkey.Reset();
        _selectedId = deviceId;
        _input.Current?.Suspend?.Invoke();
        _selectedFingerprint = fingerprint;
        _modifier = modifier;
        _trigger = trigger;
        _learning = null;
        _learningArmed = false;
        _hotkeyStarted = null;
        _latched = false;
        _requireNeutral = true;
        _hotkeyRequiresNeutral = true;
        _pendingAction = 0;
        UpdatePollingInterval();
    }

    public void LearnButton(Action<string> onDetected)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        _learning = onDetected;
        _emergencyHotkey.Reset();
        _learningArmed = false;
        _hotkeyStarted = null;
        UpdatePollingInterval();
    }

    private void Poll()
    {
        if (_stopped) return;
        try
        {
            bool includeControllerButtons = _learning is not null ||
                _modifier.StartsWith("Button:", StringComparison.Ordinal) ||
                _trigger.StartsWith("Button:", StringComparison.Ordinal);
            includeControllerButtons |= _emergencyEnabled &&
                (_emergencyModifier.StartsWith("Button:", StringComparison.Ordinal) || _emergencyTrigger.StartsWith("Button:", StringComparison.Ordinal));
            ControllerDevice[] devices = _gameInput?.ReadDevices(_selectedId, _selectedFingerprint,
                includeControllerButtons, FlydigiControlActive) ?? [];
            if (!SameDevices(devices))
            {
                _devices = devices;
                foreach (string id in _previous.Keys.Where(id => !devices.Any(device => device.Id == id)).ToArray())
                    _previous.Remove(id);
                DevicesChanged?.Invoke();
            }
            else _devices = devices;

            ControllerDevice? selected = SelectDevice(devices);
            if (_lastSelectedId != selected?.Id) _emergencyHotkey.Reset();
            if (_lastSelectedId != selected?.Id || _inputVersion != _input.Version)
            {
                _lastSelectedId = selected?.Id;
                _inputVersion = _input.Version;
                _requireNeutral = true;
                _hotkeyRequiresNeutral = true;
                _input.Current?.Suspend?.Invoke();
                _pendingAction = 0;
                _hotkeyStarted = null;
                _latched = false;
                _learningArmed = false;
            }
            if (selected is not null)
            {
                _previous.TryGetValue(selected.Id, out ControllerDevice? previous);
                ControllerInputScope? owner = _input.Current;
                long version = _input.Version;
                bool hasFocus = owner?.HasFocus ?? true;
                bool emergencyChord = _emergencyEnabled && _learning is null &&
                    IsPressed(selected, _emergencyModifier) && IsPressed(selected, _emergencyTrigger);
                bool neutral = selected.Buttons == 0 && !selected.ControllerButtons.Any(pressed => pressed);
                bool emergency = _emergencyHotkey.Update(_emergencyEnabled && _learning is null && selected.HasReading,
                    neutral, emergencyChord, Timestamp());
                // Recovery has priority over both the overlay hotkey and menu
                // actions, regardless of focus. Do not turn its A/B into a click.
                if (emergencyChord)
                {
                    _pendingAction = 0;
                    _requireNeutral = true;
                    _hotkeyRequiresNeutral = true;
                    _hotkeyStarted = null;
                    owner?.Suspend?.Invoke();
                    foreach (ControllerDevice device in devices) _previous[device.Id] = device;
                    if (emergency) EmergencyReturnPressed?.Invoke();
                    return;
                }
                bool chord = IsPressed(selected, _modifier) && IsPressed(selected, _trigger);
                if (selected.HasReading)
                    PollHotkey(selected, chord, owner, hasFocus);
                else
                {
                    _hotkeyRequiresNeutral = true;
                    _hotkeyStarted = null;
                    _latched = false;
                }
                if (!selected.HasReading || !hasFocus)
                {
                    owner?.Suspend?.Invoke();
                    _requireNeutral = true;
                    _pendingAction = 0;
                }
                else if (_requireNeutral)
                {
                    // A held button on reconnect or ownership change is never an action.
                    _requireNeutral = selected.Buttons != 0 || selected.ControllerButtons.Any(pressed => pressed);
                }
                else if (_learning is not null && owner is null)
                {
                    if (_learningArmed)
                    {
                        string? detected = DetectRising(selected, previous);
                        if (detected is not null)
                        {
                            Action<string> callback = _learning;
                            _learning = null;
                            _learningArmed = false;
                            callback(detected);
                        }
                    }
                    else _learningArmed = true;
                }
                else
                {
                    if (_input.Version == version && owner is not null && !chord && !_latched)
                    {
                        if (owner.Mode == ControllerInputMode.Flydigi)
                            owner.Flydigi?.Invoke(selected, previous);
                        else
                        {
                            if (Rising(selected, previous, "DPadLeft")) owner.Move?.Invoke(-1);
                            if (_input.Version == version && Rising(selected, previous, "DPadRight")) owner.Move?.Invoke(1);
                            if (_input.Version == version && Rising(selected, previous, "DPadUp")) owner.VerticalMove?.Invoke(-1);
                            if (_input.Version == version && Rising(selected, previous, "DPadDown")) owner.VerticalMove?.Invoke(1);
                            if (_input.Version == version)
                            {
                                // Keep the foreground window/exclusive policy until the buttons
                                // are released. B wins even if A was pressed slightly earlier.
                                if (Rising(selected, previous, "B")) _pendingAction = 0x8;
                                else if (_pendingAction == 0 && Rising(selected, previous, "A")) _pendingAction = 0x4;
                                if (selected.Buttons == 0 && !selected.ControllerButtons.Any(pressed => pressed))
                                {
                                    uint action = _pendingAction;
                                    _pendingAction = 0;
                                    if (action == 0x8) owner.Cancel?.Invoke();
                                    else if (action == 0x4) owner.Confirm?.Invoke();
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                _emergencyHotkey.Reset();
                _input.Current?.Suspend?.Invoke();
                _pendingAction = 0;
                _hotkeyRequiresNeutral = true;
                _hotkeyStarted = null;
                _latched = false;
                _learningArmed = false;
            }
            foreach (ControllerDevice device in devices) _previous[device.Id] = device;
            if (_gameInput is not null) _error = null;
#if CONTROLLERHELPER_DIAGNOSTICS
            if (++_tracePollCount % 25 == 0)
                StartupTrace.Write($"GameInput poll: devices={devices.Length}, selected={selected is not null}, " +
                    $"buttons={selected?.Buttons:X}, raw={selected?.ControllerButtons.Length}, " +
                    $"overlay={OverlayOpen}, latched={_latched}, error={Error}");
#endif

            /* Button diagnostics: enable together with _pollNumber, StateChanged
               and the App.xaml.cs subscriptions only when debugging a specific controller.
            if (++_pollNumber % 8 == 0)
            {
                string active = selected is null ? "not selected" : selected.ToString();
                string pressed = selected is null ? "none" :
                    string.Join(", ", ButtonNames.Where(name => IsPressed(selected, name))
                        .Concat(selected.ControllerButtons.Select((value, index) => value ? $"Button:{index}" : null)
                            .OfType<string>()));
                StateChanged?.Invoke($"GameInput: {devices.Length}; selected: {active}; pressed: {pressed}; " +
                    (_gameInput?.Error is string error ? $"; {error}" : _error is string startupError ? $"; {startupError}" : ""));
            }
            */
        }
        catch (Exception error)
        {
            _emergencyHotkey.Reset();
            _input.Current?.Suspend?.Invoke();
            _requireNeutral = true;
            _hotkeyRequiresNeutral = true;
            _hotkeyStarted = null;
            _latched = false;
            _pendingAction = 0;
            _error = $"GameInput: {error.Message}";
        }
        finally { UpdatePollingInterval(); }
    }

    public string? Error => _error ?? _gameInput?.Error;

    private void PollHotkey(ControllerDevice selected, bool chord, ControllerInputScope? owner, bool hasFocus)
    {
        if (_hotkeyRequiresNeutral)
        {
            _hotkeyRequiresNeutral = selected.Buttons != 0 || selected.ControllerButtons.Any(pressed => pressed);
            return;
        }
        if (_learning is not null && owner is null) return;
        if (!chord)
        {
            _hotkeyStarted = null;
            _latched = false;
            return;
        }
        _pendingAction = 0;
        long now = Timestamp();
        _hotkeyStarted ??= now;
        if (_latched || Stopwatch.GetElapsedTime(_hotkeyStarted.Value, now) < TimeSpan.FromMilliseconds(500)) return;
        _latched = true;
        // The global hotkey may recover an unfocused overlay, but cannot bypass
        // a display confirmation. Menu actions still require foreground focus.
        if (owner?.Mode == ControllerInputMode.Confirmation ||
            (owner?.Mode == ControllerInputMode.Overlay && hasFocus)) return;
        _requireNeutral = true;
        StartupTrace.Write("HotkeyPressed event");
        HotkeyPressed?.Invoke();
    }

    private bool SameDevices(ControllerDevice[] devices)
    {
        if (ReferenceEquals(_devices, devices)) return true;
        if (_devices.Length != devices.Length) return false;
        for (int index = 0; index < devices.Length; index++)
            if (_devices[index].Id != devices[index].Id) return false;
        return true;
    }

    private ControllerDevice? SelectDevice(ControllerDevice[] devices) =>
        ControllerSelection.Resolve(devices, _selectedId, _selectedFingerprint);

    private static readonly string[] ButtonNames =
    [
        "A", "B", "X", "Y", "View", "Menu", "LeftShoulder", "RightShoulder",
        "DPadUp", "DPadDown", "DPadLeft", "DPadRight", "LeftThumbstick", "RightThumbstick"
    ];

    private static uint ButtonMask(string button) => button switch
    {
        "Menu" => 0x1, "View" => 0x2, "A" => 0x4, "B" => 0x8,
        "X" => 0x10, "Y" => 0x20, "DPadUp" => 0x40, "DPadDown" => 0x80,
        "DPadLeft" => 0x100, "DPadRight" => 0x200,
        "LeftShoulder" => 0x400, "RightShoulder" => 0x800,
        "LeftThumbstick" => 0x1000, "RightThumbstick" => 0x2000,
        _ => 0
    };

    private static bool IsPressed(ControllerDevice device, string button)
    {
        if (button.StartsWith("Button:", StringComparison.Ordinal) &&
            int.TryParse(button.AsSpan(7), out int index))
            return index >= 0 && index < device.ControllerButtons.Length && device.ControllerButtons[index];
        uint mask = ButtonMask(button);
        return mask != 0 && (device.Buttons & mask) != 0;
    }

    private static bool Rising(ControllerDevice current, ControllerDevice? previous, string button) =>
        previous is not null && IsPressed(current, button) && !IsPressed(previous, button);

    private static string? DetectRising(ControllerDevice current, ControllerDevice? previous)
    {
        foreach (string button in ButtonNames)
        {
            if (Rising(current, previous, button)) return button;
        }
        for (int index = 0; index < current.ControllerButtons.Length; index++)
        {
            string button = $"Button:{index}";
            if (Rising(current, previous, button)) return button;
        }
        return null;
    }
}
