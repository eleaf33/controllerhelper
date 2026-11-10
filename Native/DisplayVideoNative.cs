using System.Runtime.InteropServices;

namespace ControllerHelper.Native;

internal static class DisplayVideoNative
{
    // DEVMODEW (display variant), fixed native size 220 bytes, no driver-private tail.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, Width, Height, Flags, Frequency;
        public uint ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        internal static DevMode Create() => new() { Size = (ushort)Marshal.SizeOf<DevMode>(), DeviceName = "", FormName = "" };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Device
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
        internal static Device Create() => new() { Size = (uint)Marshal.SizeOf<Device>(), Name = "", Description = "", Id = "", Key = "" };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SourceName
    {
        public DisplayDeviceHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }

    // Undocumented DisplayConfig extension. Reject unknown ranges/ABI, never fall
    // back to changing global registry DPI. See TV-VIDEO.md for provenance/limits.
    [StructLayout(LayoutKind.Sequential)]
    internal struct DpiGet
    {
        public DisplayDeviceHeader Header;
        public int Minimum, Current, Maximum;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DpiSet
    {
        public DisplayDeviceHeader Header;
        public int Relative;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumModes(string device, int number, ref DevMode mode, uint flags);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDevices(string? device, uint number, ref Device result, uint flags);

    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
    internal static extern int ChangeMode(string device, ref DevMode mode, nint window, uint flags, nint parameter);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)]
    internal static extern int GetSourceName(ref SourceName name);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static extern int GetDpi(ref DpiGet request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
    internal static extern int SetDpi(ref DpiSet request);
}
