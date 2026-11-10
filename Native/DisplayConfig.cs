using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ControllerHelper.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct AdapterLuid
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplaySource
{
    public AdapterLuid AdapterId;
    public uint Id;
    public uint ModeInfoIdx;
    public uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayTarget
{
    public AdapterLuid AdapterId;
    public uint Id;
    public uint ModeInfoIdx;
    public uint OutputTechnology;
    public uint Rotation;
    public uint Scaling;
    public uint RefreshNumerator;
    public uint RefreshDenominator;
    public uint ScanLineOrdering;
    public int TargetAvailable;
    public uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayPath
{
    public DisplaySource SourceInfo;
    public DisplayTarget TargetInfo;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayMode
{
    public uint InfoType;
    public uint Id;
    public AdapterLuid AdapterId;
    public ulong Data0;
    public ulong Data1;
    public ulong Data2;
    public ulong Data3;
    public ulong Data4;
    public ulong Data5;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayDeviceHeader
{
    public uint Type;
    public uint Size;
    public AdapterLuid AdapterId;
    public uint Id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DisplayTargetName
{
    public DisplayDeviceHeader Header;
    public uint Flags;
    public uint OutputTechnology;
    public ushort EdidManufactureId;
    public ushort EdidProductCodeId;
    public uint ConnectorInstance;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string FriendlyName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string DevicePath;
}

internal static class DisplayConfig
{
    internal const uint PathActive = 1;
    internal const uint PathSupportsVirtualMode = 8;
    internal const uint AllPaths = 1;
    internal const uint ActivePaths = 2;
    private const uint VirtualModeAware = 0x10;
    private const uint VirtualRefreshAware = 0x40;
    private const uint UseSupplied = 0x20;
    private const uint Validate = 0x40;
    private const uint Apply = 0x80;
    private const uint AllowChanges = 0x400;
    private const uint SetVirtualModeAware = 0x8000;
    private const uint SetVirtualRefreshAware = 0x20000;
    private const int InsufficientBuffer = 122;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
        [Out] DisplayPath[] paths, ref uint modeCount, [Out] DisplayMode[] modes, nint topologyId);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, [In] DisplayPath[] paths,
        uint modeCount, [In] DisplayMode[]? modes, uint flags);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)]
    private static extern int GetTargetName(ref DisplayTargetName name);

    internal static (DisplayPath[] Paths, DisplayMode[] Modes) Query(uint selection)
    {
        uint flags = selection | VirtualModeAware | VirtualRefreshAware;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            ThrowOnError(GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount));
            DisplayPath[] paths = new DisplayPath[pathCount];
            DisplayMode[] modes = new DisplayMode[modeCount];
            int error = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, 0);
            if (error == InsufficientBuffer) continue;
            ThrowOnError(error);
            Array.Resize(ref paths, (int)pathCount);
            Array.Resize(ref modes, (int)modeCount);
            return (paths, modes);
        }
        throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayConfig_017"));
    }

    internal static (string Name, string Path) Describe(DisplayTarget target)
    {
        DisplayTargetName name = new()
        {
            Header = new DisplayDeviceHeader
            {
                Type = 2,
                Size = (uint)Marshal.SizeOf<DisplayTargetName>(),
                AdapterId = target.AdapterId,
                Id = target.Id
            },
            FriendlyName = string.Empty,
            DevicePath = string.Empty
        };
        return GetTargetName(ref name) == 0
            ? (name.FriendlyName.TrimEnd('\0'), name.DevicePath.TrimEnd('\0'))
            : (string.Empty, string.Empty);
    }

    internal static string Key(DisplayTarget target)
    {
        string path = Describe(target).Path;
        return string.IsNullOrWhiteSpace(path)
            ? $"{target.AdapterId.HighPart:X8}:{target.AdapterId.LowPart:X8}:{target.Id:X8}"
            : path;
    }

    internal static void ApplyTopology(DisplayPath[] paths, DisplayMode[] modes)
    {
        ValidateTopology(paths, modes);
        uint flags = TopologyFlags(modes);
        ThrowOnError(SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes.Length == 0 ? null : modes, flags | Apply));
    }

    internal static void ActivateSingleTarget(DisplayPath path)
        => ApplyTopology([PrepareSingleTarget(path)], []);

    internal static DisplayPath PrepareSingleTarget(DisplayPath path)
    {
        // Ask Windows for a valid initial mode for this target only. The explicit
        // resolution/refresh is then applied and verified before confirmation.
        path.Flags = PathActive;
        path.SourceInfo.ModeInfoIdx = uint.MaxValue;
        path.TargetInfo.ModeInfoIdx = uint.MaxValue;
        path.TargetInfo.RefreshNumerator = 0;
        path.TargetInfo.RefreshDenominator = 0;
        path.TargetInfo.ScanLineOrdering = 0;
        return path;
    }

    internal static void ValidateTopology(DisplayPath[] paths, DisplayMode[] modes)
    {
        if (paths.Length == 0) throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayConfig_018"));
        uint flags = TopologyFlags(modes);
        ThrowOnError(SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes.Length == 0 ? null : modes, flags | Validate));
    }

    // A path-only bootstrap has no virtual mode table. Windows rejects the
    // virtual-aware modifiers in this case (ERROR_INVALID_PARAMETER).
    private static uint TopologyFlags(DisplayMode[] modes) => UseSupplied | AllowChanges |
        (modes.Length == 0 ? 0u : SetVirtualModeAware | SetVirtualRefreshAware);

    private static void ThrowOnError(int error)
    {
        if (error != 0) throw new Win32Exception(error);
    }
}
