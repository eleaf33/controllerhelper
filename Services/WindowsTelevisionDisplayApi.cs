using System.ComponentModel;
using System.Runtime.InteropServices;
using ControllerHelper.Models;
using ControllerHelper.Native;
using Microsoft.Win32;

namespace ControllerHelper.Services;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsTelevisionDisplayApi : ITelevisionDisplayApi
{
    public string? ScaleBlockReason
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key?.GetValue("Win8DpiScaling") is int custom && custom == 1)
                return ControllerHelper.Localization.Text.Get("WindowsTelevisionDisplayApi_231");
            return null;
        }
    }

    internal TelevisionDisplayCatalog GetCatalog(string id)
    {
        TelevisionVideoMode[] modes = [];
        string? modeWarning = null;
        try { modes = GetModes(id); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        { modeWarning = error.Message; }
        TelevisionVideoMode? current = null;
        try { current = GetCurrentMode(id); } catch (InvalidOperationException) { }
        DisplayScaleRange? scale = null;
        string? warning = ScaleBlockReason;
        if (warning is null)
        {
            try { scale = GetScale(id); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
            { warning = error.Message; }
        }
        return new(modes, current, scale, warning, modeWarning);
    }

    public TelevisionVideoMode[] GetModes(string televisionId)
    {
        string source = FindSourceName(televisionId);
        List<TelevisionVideoMode> modes = [];
        for (int index = 0; index < 4096; index++)
        {
            var native = DisplayVideoNative.DevMode.Create();
            if (!DisplayVideoNative.EnumModes(source, index, ref native, 0)) break;
            if (native.BitsPerPel != 32 || native.Width < 320 || native.Height < 200 || native.Frequency <= 1) continue;
            var mode = Convert(native);
            mode.Validate();
            modes.Add(mode);
        }
        if (modes.Count == 0)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("WindowsTelevisionDisplayApi_232"));
        return modes.Distinct().OrderByDescending(mode => (long)mode.Width * mode.Height)
            .ThenByDescending(mode => mode.Width).ThenByDescending(mode => mode.Frequency).ThenBy(mode => mode.Interlaced).ToArray();
    }

    public TelevisionVideoMode GetCurrentMode(string televisionId)
    {
        string name = SourceName(ActivePath(televisionId, exclusive: false).SourceInfo);
        var mode = DisplayVideoNative.DevMode.Create();
        if (!DisplayVideoNative.EnumModes(name, -1, ref mode, 0))
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("WindowsTelevisionDisplayApi_233"));
        return Convert(mode);
    }

    public void SetMode(string televisionId, TelevisionVideoMode mode)
    {
        mode.Validate();
        string name = SourceName(ActivePath(televisionId, exclusive: true).SourceInfo);
        var native = DisplayVideoNative.DevMode.Create();
        native.Fields = 0x7C0000; // BitsPerPel, PelsWidth/Height, DisplayFlags, DisplayFrequency.
        native.BitsPerPel = 32;
        native.Width = (uint)mode.Width;
        native.Height = (uint)mode.Height;
        native.Frequency = (uint)mode.Frequency;
        native.Flags = mode.Interlaced ? 2u : 0;
        int result = DisplayVideoNative.ChangeMode(name, ref native, 0, 2, 0); // CDS_TEST
        if (result != 0) throw ModeError(result);
        // Re-resolve after test: never let an old GDI name mutate another screen.
        if (name != SourceName(ActivePath(televisionId, exclusive: true).SourceInfo))
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("WindowsTelevisionDisplayApi_234"));
        result = DisplayVideoNative.ChangeMode(name, ref native, 0, 0, 0);
        if (result != 0) throw ModeError(result);
    }

    public DisplayScaleRange GetScale(string televisionId)
    {
        if (ScaleBlockReason is string reason) throw new InvalidOperationException(reason);
        var path = ActivePath(televisionId, exclusive: false);
        var request = new DisplayVideoNative.DpiGet { Header = Header(path.SourceInfo, unchecked((uint)-3), 32) };
        if (Marshal.SizeOf<DisplayVideoNative.DpiGet>() != 32 || Marshal.SizeOf<DisplayVideoNative.DpiSet>() != 24)
            throw new NotSupportedException(ControllerHelper.Localization.Text.Get("WindowsTelevisionDisplayApi_235"));
        int result = DisplayVideoNative.GetDpi(ref request);
        if (result != 0) throw new Win32Exception(result, ControllerHelper.Localization.Text.Get("WindowsTelevisionDisplayApi_236"));
        var scale = new DisplayScaleRange(request.Minimum, request.Current, request.Maximum);
        scale.Validate();
        return scale;
    }

    public void SetScale(string televisionId, int relative)
    {
        DisplayScaleRange scale = GetScale(televisionId);
        if (relative < scale.MinimumRelative || relative > scale.MaximumRelative)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("WindowsTelevisionDisplayApi_237"));
        var path = ActivePath(televisionId, exclusive: true);
        var request = new DisplayVideoNative.DpiSet
        {
            Header = Header(path.SourceInfo, unchecked((uint)-4), 24), Relative = relative
        };
        int result = DisplayVideoNative.SetDpi(ref request);
        if (result != 0) throw new Win32Exception(result, ControllerHelper.Localization.Text.Get("WindowsTelevisionDisplayApi_238"));
        if (GetScale(televisionId).CurrentRelative != relative)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("WindowsTelevisionDisplayApi_239"));
    }

    private static DisplayDeviceHeader Header(DisplaySource source, uint type, uint size) =>
        new() { Type = type, Size = size, AdapterId = source.AdapterId, Id = source.Id };

    private static TelevisionVideoMode Convert(DisplayVideoNative.DevMode mode) =>
        new(checked((int)mode.Width), checked((int)mode.Height), checked((int)mode.Frequency), (mode.Flags & 2) != 0);

    private static Exception ModeError(int result) => new InvalidOperationException(
        ControllerHelper.Localization.Text.Format("WindowsTelevisionDisplayApi_240", result));

    private static DisplayPath ActivePath(string televisionId, bool exclusive)
    {
        var (paths, _) = DisplayConfig.Query(DisplayConfig.ActivePaths);
        var matches = paths.Where(path => string.Equals(DisplayConfig.Key(path.TargetInfo), televisionId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("WindowsTelevisionDisplayApi_241"));
        if (exclusive && paths.Length != 1)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("WindowsTelevisionDisplayApi_242"));
        var selected = matches[0];
        if (paths.Count(path => path.SourceInfo.AdapterId.Equals(selected.SourceInfo.AdapterId) &&
            path.SourceInfo.Id == selected.SourceInfo.Id) != 1)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("WindowsTelevisionDisplayApi_243"));
        return selected;
    }

    private static string SourceName(DisplaySource source)
    {
        var request = new DisplayVideoNative.SourceName
        {
            Header = Header(source, 1, (uint)Marshal.SizeOf<DisplayVideoNative.SourceName>()), Name = ""
        };
        int result = DisplayVideoNative.GetSourceName(ref request);
        if (result != 0 || string.IsNullOrWhiteSpace(request.Name))
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("WindowsTelevisionDisplayApi_244"));
        return request.Name;
    }

    private static string FindSourceName(string televisionId)
    {
        var (paths, _) = DisplayConfig.Query(DisplayConfig.ActivePaths);
        foreach (var path in paths)
            if (string.Equals(DisplayConfig.Key(path.TargetInfo), televisionId, StringComparison.OrdinalIgnoreCase))
                return SourceName(path.SourceInfo);
        // Match the monitor interface identity, not the first available adapter/source.
        for (uint index = 0; index < 64; index++)
        {
            var adapter = DisplayVideoNative.Device.Create();
            if (!DisplayVideoNative.EnumDevices(null, index, ref adapter, 0)) break;
            for (uint monitor = 0; monitor < 64; monitor++)
            {
                var display = DisplayVideoNative.Device.Create();
                if (!DisplayVideoNative.EnumDevices(adapter.Name, monitor, ref display, 1)) break;
                if (string.Equals(display.Id, televisionId, StringComparison.OrdinalIgnoreCase)) return adapter.Name;
            }
        }
        throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("WindowsTelevisionDisplayApi_245"));
    }
}
