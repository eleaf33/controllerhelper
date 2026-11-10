using System.Runtime.InteropServices;

namespace ControllerHelper.Native;

internal static class GameInputNative
{
    internal const int GamepadKind = 0x40000;
    internal const int ControllerButtonKind = 0x4;
    // GameInput.dll's original (v0) ABI: default policy permits background hotkeys.
    // ExclusiveForegroundInput only suppresses other GameInput consumers, not XInput/HID.
    internal const int DefaultFocusPolicy = 0;
    internal const int ExclusiveForegroundInput = 2;
    internal const int SetFocusPolicySlot = 21;

    [DllImport("GameInput.dll", ExactSpelling = true)]
    internal static extern int GameInputCreate(out nint gameInput);

    internal static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    internal static void Release(nint instance)
    {
        if (instance != 0) Marshal.Release(instance);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetCurrentReading(nint instance, int kind, nint device, out nint reading);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void SetFocusPolicy(nint instance, int policy);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int RegisterDeviceCallback(nint instance, nint device, int kind, uint statusFilter,
        int enumerationKind, nint context, DeviceCallback callback, out ulong token);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void StopCallback(nint instance, ulong token);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal delegate bool UnregisterCallback(nint instance, ulong token, ulong timeoutMicroseconds);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void DeviceCallback(ulong token, nint context, nint device, ulong timestamp,
        uint currentStatus, uint previousStatus);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate nint GetDeviceInfo(nint device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal delegate bool GetGamepadState(nint reading, out GamepadState state);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate uint GetControllerButtonCount(nint reading);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate uint GetControllerButtonState(nint reading, uint count, [Out] byte[] states);

    [StructLayout(LayoutKind.Sequential)]
    internal struct GamepadState
    {
        internal uint Buttons;
        internal float LeftTrigger;
        internal float RightTrigger;
        internal float LeftThumbstickX;
        internal float LeftThumbstickY;
        internal float RightThumbstickX;
        internal float RightThumbstickY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfo
    {
        internal uint InfoSize;
        internal ushort VendorId;
        internal ushort ProductId;
        internal ushort RevisionNumber;
        internal byte InterfaceNumber;
        internal byte CollectionNumber;
        internal uint Usage;
        internal ulong HardwareVersion;
        internal ulong FirmwareVersion;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] DeviceId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] DeviceRootId;
        internal int DeviceFamily;
        internal uint Capabilities;
        internal uint SupportedInput;
        internal uint SupportedRumbleMotors;
        internal uint InputReportCount;
        internal uint OutputReportCount;
        internal uint FeatureReportCount;
        internal uint ControllerAxisCount;
        internal uint ControllerButtonCount;
        internal uint ControllerSwitchCount;
        internal uint TouchPointCount;
        internal uint TouchSensorCount;
        internal uint ForceFeedbackMotorCount;
        internal uint HapticFeedbackMotorCount;
        internal uint DeviceStringCount;
        internal uint DeviceDescriptorSize;
        internal nint InputReportInfo;
        internal nint OutputReportInfo;
        internal nint FeatureReportInfo;
        internal nint ControllerAxisInfo;
        internal nint ControllerButtonInfo;
        internal nint ControllerSwitchInfo;
        internal nint KeyboardInfo;
        internal nint MouseInfo;
        internal nint TouchSensorInfo;
        internal nint MotionInfo;
        internal nint ArcadeStickInfo;
        internal nint FlightStickInfo;
        internal nint GamepadInfo;
        internal nint RacingWheelInfo;
        internal nint UiNavigationInfo;
        internal nint ForceFeedbackMotorInfo;
        internal nint HapticFeedbackMotorInfo;
        internal nint DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GameInputString
    {
        internal uint SizeInBytes;
        internal uint CodePointCount;
        internal nint Data;
    }
}
