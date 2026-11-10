using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ControllerHelper.Services;

internal static class OverlayInputGuard
{
    internal static void Attach(UIElement root)
    {
        // GameInput/ControllerMonitor owns gamepad navigation. WinUI's built-in
        // Button activation and XY navigation must not handle the same press.
        root.PreviewKeyDown += SuppressGamepadKey;
        root.PreviewKeyUp += SuppressGamepadKey;
    }

    private static void SuppressGamepadKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.OriginalKey is >= VirtualKey.GamepadA and <= VirtualKey.GamepadRightThumbstickLeft)
            args.Handled = true;
    }

    internal static bool HasFocus(nint window) => window != 0 && GetForegroundWindow() == window;

    // Called only on an explicit request to show the window, never on a poll or
    // deactivation event. Being topmost alone does not give a WinUI window focus.
    internal static bool TryActivate(nint window)
    {
        if (window == 0) return false;
        if (HasFocus(window)) return true;
        SetForegroundWindow(window);
        if (HasFocus(window)) return true;
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint currentThread = GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == currentThread ||
            !AttachThreadInput(currentThread, foregroundThread, true)) return false;
        try
        {
            SetForegroundWindow(window);
            return HasFocus(window);
        }
        finally { AttachThreadInput(currentThread, foregroundThread, false); }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint thread, uint otherThread, [MarshalAs(UnmanagedType.Bool)] bool attach);
}
