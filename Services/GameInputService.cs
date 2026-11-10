using System.Runtime.InteropServices;
using ControllerHelper.Native;

namespace ControllerHelper.Services;

internal sealed class GameInputService : IDisposable
{
    private sealed class DeviceEntry(nint pointer, ControllerDevice snapshot, int supportedInput)
    {
        internal nint Pointer = pointer;
        internal ControllerDevice Snapshot = snapshot;
        internal byte[] ButtonBuffer = [];
        internal int SupportedInput = supportedInput;
    }

    private readonly object _sync = new();
    private readonly NativeMethodCache<GameInputNative.GetGamepadState> _gamepadState = new(22);
    private readonly NativeMethodCache<GameInputNative.GetControllerButtonCount> _buttonCount = new(10);
    private readonly NativeMethodCache<GameInputNative.GetControllerButtonState> _buttonState = new(11);
    private readonly Dictionary<string, DeviceEntry> _devices = [];
    private readonly GameInputNative.DeviceCallback _deviceCallback;
    private readonly GameInputNative.GetCurrentReading _getCurrentReading;
    private readonly GameInputNative.RegisterDeviceCallback _registerDeviceCallback;
    private readonly GameInputNative.StopCallback _stopCallback;
    private readonly GameInputNative.UnregisterCallback _unregisterCallback;
    private readonly GameInputNative.SetFocusPolicy _setFocusPolicy;
    private bool _exclusive;
    private ControllerDevice[] _snapshots = [];
    private bool _snapshotsDirty = true;
    private nint _gameInput;
    private ulong _deviceToken;
    private bool _disposed;

    internal string? Error { get; private set; }

    internal GameInputService()
    {
        int result = GameInputNative.GameInputCreate(out _gameInput);
        if (result < 0 || _gameInput == 0)
            throw new COMException(ControllerHelper.Localization.Text.Get("GameInputService_097"), result);

        _getCurrentReading = GameInputNative.Method<GameInputNative.GetCurrentReading>(_gameInput, 4);
        _registerDeviceCallback = GameInputNative.Method<GameInputNative.RegisterDeviceCallback>(_gameInput, 9);
        _stopCallback = GameInputNative.Method<GameInputNative.StopCallback>(_gameInput, 12);
        _unregisterCallback = GameInputNative.Method<GameInputNative.UnregisterCallback>(_gameInput, 13);
        _setFocusPolicy = GameInputNative.Method<GameInputNative.SetFocusPolicy>(
            _gameInput, GameInputNative.SetFocusPolicySlot);
        _deviceCallback = OnDeviceChanged;

        result = _registerDeviceCallback(_gameInput, 0,
            GameInputNative.GamepadKind | GameInputNative.ControllerButtonKind, 1, 2, 0,
            _deviceCallback, out _deviceToken);
        if (result < 0)
        {
            GameInputNative.Release(_gameInput);
            _gameInput = 0;
            throw new COMException(ControllerHelper.Localization.Text.Get("GameInputService_098"), result);
        }

    }

    internal void SetExclusiveForegroundInput(bool enabled)
    {
        if (_disposed || _exclusive == enabled) return;
        _setFocusPolicy(_gameInput, enabled
            ? GameInputNative.ExclusiveForegroundInput : GameInputNative.DefaultFocusPolicy);
        _exclusive = enabled;
    }

    internal ControllerDevice[] ReadDevices(string? selectedId, string? selectedFingerprint,
        bool includeControllerButtons, bool includeAxes)
    {
        lock (_sync)
        {
            DeviceEntry? selected = null;
            if (!string.IsNullOrWhiteSpace(selectedId)) _devices.TryGetValue(selectedId, out selected);
            if (selected is null && !string.IsNullOrWhiteSpace(selectedFingerprint))
            {
                foreach (DeviceEntry entry in _devices.Values)
                {
                    if (entry.Snapshot.Fingerprint != selectedFingerprint) continue;
                    if (selected is not null) { selected = null; break; }
                    selected = entry;
                }
            }
            foreach (DeviceEntry entry in _devices.Values)
            {
                if (!ReferenceEquals(entry, selected)) continue;
                uint buttons = 0;
                ControllerAxes axes = default;
                bool[] controllerButtons = [];
                bool gamepadRead = false, rawRead = false;
                if (_getCurrentReading(_gameInput, GameInputNative.GamepadKind, entry.Pointer, out nint reading) >= 0)
                {
                    try
                    {
                        if (_gamepadState.Get(reading)(reading, out var state))
                        {
                            gamepadRead = true;
                            buttons = state.Buttons;
                            if (includeAxes)
                                axes = new ControllerAxes(state.LeftThumbstickX, state.LeftThumbstickY,
                                    state.RightThumbstickX, state.RightThumbstickY);
                        }
                    }
                    finally { GameInputNative.Release(reading); }
                }

                if (includeControllerButtons &&
                    _getCurrentReading(_gameInput, GameInputNative.ControllerButtonKind, entry.Pointer, out reading) >= 0)
                {
                    try
                    {
                        uint count = _buttonCount.Get(reading)(reading);
                        rawRead = count == 0;
                        if (count is > 0 and <= 256)
                        {
                            if (entry.ButtonBuffer.Length != count) entry.ButtonBuffer = new byte[count];
                            Array.Clear(entry.ButtonBuffer);
                            uint reported = _buttonState.Get(reading)(
                                reading, count, entry.ButtonBuffer);
                            int actualCount = (int)Math.Min(count, reported);
                            rawRead = reported == count;
                            controllerButtons = entry.Snapshot.ControllerButtons;
                            bool changed = controllerButtons.Length != actualCount;
                            if (!changed)
                                for (int index = 0; index < controllerButtons.Length; index++)
                                    if (controllerButtons[index] != (entry.ButtonBuffer[index] != 0))
                                    {
                                        changed = true;
                                        break;
                                    }
                            if (changed)
                            {
                                controllerButtons = new bool[actualCount];
                                for (int index = 0; index < controllerButtons.Length; index++)
                                    controllerButtons[index] = entry.ButtonBuffer[index] != 0;
                            }
                        }
                        else controllerButtons = [];
                    }
                    finally { GameInputNative.Release(reading); }
                }
                bool hasReading = ((entry.SupportedInput & GameInputNative.GamepadKind) == 0 || gamepadRead) &&
                    (!includeControllerButtons || (entry.SupportedInput & GameInputNative.ControllerButtonKind) == 0 || rawRead);
                if (entry.Snapshot.HasReading != hasReading || entry.Snapshot.Buttons != buttons || entry.Snapshot.Axes != axes ||
                    !ReferenceEquals(entry.Snapshot.ControllerButtons, controllerButtons))
                {
                    entry.Snapshot = entry.Snapshot with
                    {
                        Buttons = buttons, ControllerButtons = controllerButtons, Axes = axes, HasReading = hasReading
                    };
                    _snapshotsDirty = true;
                }
            }
            if (_snapshotsDirty)
            {
                _snapshots = _devices.Values.Select(entry => entry.Snapshot).ToArray();
                _snapshotsDirty = false;
            }
            return _snapshots;
        }
    }

    private void OnDeviceChanged(ulong token, nint context, nint device, ulong timestamp,
        uint currentStatus, uint previousStatus)
    {
        try
        {
            nint infoPointer = GameInputNative.Method<GameInputNative.GetDeviceInfo>(device, 3)(device);
            if (infoPointer == 0 || (uint)Marshal.ReadInt32(infoPointer) < Marshal.SizeOf<GameInputNative.DeviceInfo>()) return;
            var info = Marshal.PtrToStructure<GameInputNative.DeviceInfo>(infoPointer);
            string id = Convert.ToHexString(info.DeviceId);
            lock (_sync)
            {
                if ((currentStatus & 1) == 0)
                {
                    if (_devices.Remove(id, out DeviceEntry? removed))
                    {
                        GameInputNative.Release(removed.Pointer);
                        _snapshotsDirty = true;
                    }
                    return;
                }
                if (_devices.ContainsKey(id)) return;
                string name = ControllerHelper.Localization.Text.Get("GameInputService_099");
                if (info.DisplayName != 0)
                {
                    var gameInputString = Marshal.PtrToStructure<GameInputNative.GameInputString>(info.DisplayName);
                    if (gameInputString.Data != 0 && gameInputString.SizeInBytes is > 0 and < 1024)
                        name = Marshal.PtrToStringUTF8(gameInputString.Data, (int)gameInputString.SizeInBytes)?.TrimEnd('\0') ?? name;
                }
                string fingerprint = $"{info.VendorId:X4}:{info.ProductId:X4}:{name}";
                Marshal.AddRef(device);
                _devices.Add(id, new DeviceEntry(device,
                    new ControllerDevice(id, name, fingerprint, info.VendorId, info.ProductId, 0, [], default)
                    { HasReading = false }, (int)info.SupportedInput));
                _snapshotsDirty = true;
                Error = null;
            }
        }
        catch (Exception error) { Error = ControllerHelper.Localization.Text.Format("GameInputService_100", error.Message); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        SetExclusiveForegroundInput(false);
        _disposed = true;
        if (_deviceToken != 0) { _stopCallback(_gameInput, _deviceToken); _unregisterCallback(_gameInput, _deviceToken, 1_000_000); }
        lock (_sync)
        {
            foreach (DeviceEntry entry in _devices.Values) GameInputNative.Release(entry.Pointer);
            _devices.Clear();
        }
        GameInputNative.Release(_gameInput);
        _gameInput = 0;
    }
}
