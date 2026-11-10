using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ControllerHelper.Services;

internal sealed class FlydigiGamepadInput
{
    private readonly FlydigiCursorPump _cursor = new();
    private long _xPressedTimestamp;
    private bool _xHoldHandled;
    private bool _keyboardNavigation;

    internal void StartPointer() => _cursor.Start();
    internal void StopPointer() => _cursor.Stop();
    internal void SetCursorSpeed(double multiplier) => _cursor.SetSpeed(multiplier);

    internal void Reset()
    {
        _cursor.Update(default);
        _xPressedTimestamp = 0;
        _xHoldHandled = false;
        _keyboardNavigation = false;
    }

    internal bool Send(ControllerDevice current, ControllerDevice? previous, bool hasFocus,
        bool pointerOverClient, bool speedSelected)
    {
        long timestamp = Stopwatch.GetTimestamp();
        if ((current.Buttons & 0x10) != 0)
        {
            if (_xPressedTimestamp == 0) _xPressedTimestamp = timestamp;
            if (!_xHoldHandled && Stopwatch.GetElapsedTime(_xPressedTimestamp, timestamp) >= TimeSpan.FromSeconds(1))
            {
                _xHoldHandled = true;
                return true;
            }
        }
        else if (_xPressedTimestamp != 0)
        {
            if (!_xHoldHandled && (hasFocus || pointerOverClient)) Click(0x0008, 0x0010);
            _xPressedTimestamp = 0;
            _xHoldHandled = false;
        }

        _cursor.Update(current.Axes);
        if (Math.Abs(current.Axes.LeftX) >= 0.16f || Math.Abs(current.Axes.LeftY) >= 0.16f)
            _keyboardNavigation = false;

        if (!speedSelected && (hasFocus || pointerOverClient) && Rising(current, previous, 0x4))
        {
            if (_keyboardNavigation) PressKey(0x0D);
            else Click(0x0002, 0x0004);
        }
        if (hasFocus)
        {
            if (Rising(current, previous, 0x8)) PressKey(0x1B);
            if (Rising(current, previous, 0x40) || Rising(current, previous, 0x100)) Navigate(false);
            if (Rising(current, previous, 0x80) || Rising(current, previous, 0x200)) Navigate(true);
        }
        if (hasFocus || pointerOverClient)
        {
            if (Rising(current, previous, 0x400)) SendMouse(0, 0, 120, 0x0800);
            if (Rising(current, previous, 0x800)) SendMouse(0, 0, unchecked((uint)-120), 0x0800);
        }
        return false;
    }

    private static bool Rising(ControllerDevice current, ControllerDevice? previous, uint mask) =>
        (current.Buttons & mask) != 0 && (previous is null || (previous.Buttons & mask) == 0);

    private void Navigate(bool forward)
    {
        _keyboardNavigation = true;
        if (forward) PressKey(0x09);
        else
        {
            PressKey(0x10, true);
            PressKey(0x09);
            PressKey(0x10, false);
        }
    }

    private static void Click(uint down, uint up)
    {
        SendMouse(0, 0, 0, down);
        SendMouse(0, 0, 0, up);
    }

    private static void PressKey(ushort key)
    {
        PressKey(key, true);
        PressKey(key, false);
    }

    private static void PressKey(ushort key, bool down)
    {
        Input input = new() { Type = 1, Keyboard = new KeyboardInput { VirtualKey = key, Flags = down ? 0u : 0x0002u } };
        SendInput(1, ref input, InputSize);
    }

    private static void SendMouse(int horizontal, int vertical, uint data, uint flags)
    {
        Input input = new() { Type = 0, Mouse = new MouseInput
        {
            Horizontal = horizontal, Vertical = vertical, Data = data, Flags = flags
        } };
        SendInput(1, ref input, InputSize);
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct Input
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal MouseInput Mouse;
        [FieldOffset(8)] internal KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int Horizontal, Vertical;
        internal uint Data, Flags, Time;
        internal nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey, ScanCode;
        internal uint Flags, Time;
        internal nint ExtraInfo;
    }

    private static readonly int InputSize = Marshal.SizeOf<Input>();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, ref Input input, int size);
}
