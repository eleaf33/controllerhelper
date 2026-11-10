using ControllerHelper.Models;
using ControllerHelper.Native;

namespace ControllerHelper.Services;

public sealed record DisplayDevice(string Id, string Name, bool IsActive)
{
    public override string ToString() => $"{Name}{(IsActive ? ControllerHelper.Localization.Text.Get("DisplayService_070") : "")}";
}

internal sealed record DisplayTopologySnapshot(DisplayPath[] Paths, DisplayMode[] Modes);

public sealed class DisplayService
{
    private readonly WindowsTelevisionDisplayApi _televisionVideo = new();

    public TelevisionDisplayCatalog GetTelevisionDisplayCatalog(string televisionId) =>
        _televisionVideo.GetCatalog(televisionId);

    internal void ValidateTelevisionOptions(AppSettings settings) =>
        new TelevisionDisplayOptions(_televisionVideo).Preflight(settings);

    internal void ApplyTelevisionOptions(AppSettings settings, DisplaySettingsRollback rollback, CancellationToken cancellation) =>
        new TelevisionDisplayOptions(_televisionVideo).Apply(settings, rollback.Add, cancellation);

    internal DisplayTopologySnapshot CaptureActiveTopology()
    {
        var (paths, modes) = DisplayConfig.Query(DisplayConfig.ActivePaths);
        return new DisplayTopologySnapshot(paths, modes);
    }

    internal void RestoreTopology(DisplayTopologySnapshot snapshot) =>
        DisplayConfig.ApplyTopology(snapshot.Paths, snapshot.Modes);

    public bool IsTelevisionAvailable(string? televisionId)
    {
        if (string.IsNullOrWhiteSpace(televisionId)) return false;
        var (paths, _) = DisplayConfig.Query(DisplayConfig.AllPaths);
        return paths.Any(path => path.TargetInfo.TargetAvailable != 0 &&
            DisplayConfig.Key(path.TargetInfo).Equals(televisionId, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<DisplayDevice> Enumerate()
    {
        var (paths, _) = DisplayConfig.Query(DisplayConfig.AllPaths);
        return paths.Where(path => path.TargetInfo.TargetAvailable != 0 || (path.Flags & DisplayConfig.PathActive) != 0)
            .GroupBy(path => DisplayConfig.Key(path.TargetInfo), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                DisplayPath first = group.First();
                string name = DisplayConfig.Describe(first.TargetInfo).Name;
                return new DisplayDevice(group.Key,
                    $"{(string.IsNullOrWhiteSpace(name) ? ControllerHelper.Localization.Text.Get("DisplayService_071") : name)} · {first.TargetInfo.AdapterId.LowPart:X4}:{first.TargetInfo.Id}",
                    group.Any(path => (path.Flags & DisplayConfig.PathActive) != 0));
            }).ToArray();
    }

    public void CapturePcLayout(AppSettings settings)
    {
        var (paths, modes) = DisplayConfig.Query(DisplayConfig.ActivePaths);
        HashSet<string> selected = new(settings.MonitorIds, StringComparer.OrdinalIgnoreCase);
        DisplayPath[] pcPaths = paths.Where(path => selected.Contains(DisplayConfig.Key(path.TargetInfo))).ToArray();
        if (selected.Count == 0 || pcPaths.Length != selected.Count)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayService_072"));
        DisplayProfileCodec.StorePc(settings, DisplayProfileCodec.Capture(pcPaths, modes, DisplayConfig.Key));
    }

    internal AppSettings PreparePcForTelevision(AppSettings settings)
    {
        var (paths, modes) = DisplayConfig.Query(DisplayConfig.ActivePaths);
        var (available, _) = DisplayConfig.Query(DisplayConfig.AllPaths);
        return DisplayProfileCodec.PreparePcForTelevision(settings, paths, modes, available, DisplayConfig.Key);
    }

    public void CaptureTelevisionLayout(AppSettings settings)
    {
        var (paths, modes) = DisplayConfig.Query(DisplayConfig.ActivePaths);
        DisplayPath[] selected = paths.Where(path =>
            string.Equals(DisplayConfig.Key(path.TargetInfo), settings.TelevisionId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length != 1)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayService_073"));
        DisplayProfileCodec.StoreTv(settings, DisplayProfileCodec.Capture(selected, modes, DisplayConfig.Key));
    }

    public void SwitchToTelevision(AppSettings settings, CancellationToken cancellation = default)
    {
        if (settings.TelevisionMode is null) DisplayProfileCodec.ValidateVersion(settings.TvProfile);
        if (string.IsNullOrWhiteSpace(settings.TelevisionId))
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayProfileCodec_058"));
        if (TelevisionNetworkSettings.WakeEnabled(settings))
            new TelevisionReadiness().WaitUntilReady(() => IsTelevisionAvailable(settings.TelevisionId), cancellation);
        else if (!IsTelevisionAvailable(settings.TelevisionId))
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayService_074"));

        cancellation.ThrowIfCancellationRequested();
        var (active, activeModes) = DisplayConfig.Query(DisplayConfig.ActivePaths);
        DisplayPath path = active.FirstOrDefault(candidate =>
            DisplayConfig.Key(candidate.TargetInfo).Equals(settings.TelevisionId, StringComparison.OrdinalIgnoreCase));
        DisplayMode[] modes;
        if ((path.Flags & DisplayConfig.PathActive) != 0)
        {
            DisplayProfileCodec.StoreTv(settings, DisplayProfileCodec.Capture([path], activeModes, DisplayConfig.Key));
            // Already on TV only: the options stage checks the actual mode and
            // DPI. Reapplying this topology would needlessly reset the output.
            if (active.Length == 1) return;
            modes = activeModes;
        }
        else
        {
            var (availablePaths, _) = DisplayConfig.Query(DisplayConfig.AllPaths);
            DecodedDisplayProfile? saved = DisplayProfileCodec.TryReadTelevision(settings, availablePaths, DisplayConfig.Key);
            if (saved is null)
            {
                DisplayPath[] candidates = availablePaths.Where(candidate => candidate.TargetInfo.TargetAvailable != 0 &&
                    DisplayConfig.Key(candidate.TargetInfo).Equals(settings.TelevisionId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length == 0) throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayService_075"));
                cancellation.ThrowIfCancellationRequested();
                DisplayConfig.ActivateSingleTarget(candidates[0]);
                return;
            }
            DisplayProfileCodec.StoreTv(settings, saved.Profile);
            path = saved.Paths[0];
            modes = saved.Modes;
        }

        if (!DisplayConfig.Key(path.TargetInfo).Equals(settings.TelevisionId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayService_076"));
        int sourceModeIndex = SourceModeIndex(path);
        if (sourceModeIndex < 0 || sourceModeIndex >= modes.Length || modes[sourceModeIndex].InfoType != 1)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayService_077"));
        modes[sourceModeIndex].Data1 &= uint.MaxValue;
        modes[sourceModeIndex].Data2 &= 0xFFFFFFFF00000000UL;
        cancellation.ThrowIfCancellationRequested();
        DisplayConfig.ApplyTopology([path], modes);
    }

    public void SwitchToPc(AppSettings settings)
    {
        DecodedDisplayProfile saved = ReadPcProfile(settings);
        DisplayConfig.ApplyTopology(saved.Paths, saved.Modes);
        DisplayProfileCodec.StorePc(settings, saved.Profile);
    }

    internal void ValidatePcProfile(AppSettings settings) => ReadPcProfile(settings, requireAvailable: false);

    private static DecodedDisplayProfile ReadPcProfile(AppSettings settings, bool requireAvailable = true, bool allowSourceRemap = false)
    {
        if (!settings.HasPcProfile) throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayService_078"));
        var (available, _) = DisplayConfig.Query(DisplayConfig.AllPaths);
        return DisplayProfileCodec.Read(settings.PcProfile, settings.PcPaths, settings.PcModes, settings.MonitorIds,
            available, DisplayConfig.Key, allowSourceRemap, requireAvailable);
    }

    public (bool TelevisionActive, bool MonitorsActive) GetMonitorToggleState(AppSettings settings)
    {
        var (paths, _) = DisplayConfig.Query(DisplayConfig.ActivePaths);
        HashSet<string> active = paths.Select(path => DisplayConfig.Key(path.TargetInfo))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (active.Contains(settings.TelevisionId ?? string.Empty),
            settings.MonitorIds.Any(active.Contains));
    }

    public Windows.Graphics.RectInt32? GetTelevisionBounds(string? televisionId)
    {
        if (string.IsNullOrWhiteSpace(televisionId)) return null;
        var (paths, modes) = DisplayConfig.Query(DisplayConfig.ActivePaths);
        foreach (DisplayPath path in paths)
        {
            if (!DisplayConfig.Key(path.TargetInfo).Equals(televisionId, StringComparison.OrdinalIgnoreCase))
                continue;
            int sourceIndex = SourceModeIndex(path);
            if (sourceIndex < 0 || sourceIndex >= modes.Length || modes[sourceIndex].InfoType != 1)
                return null;
            DisplayMode mode = modes[sourceIndex];
            uint width = (uint)mode.Data0;
            uint height = (uint)(mode.Data0 >> 32);
            int positionX = SourceX(mode);
            int positionY = SourceY(mode);
            StartupTrace.Write($"TV bounds: flags={path.Flags:X}, sourceMode={SourceModeIndex(path)}, " +
                $"raw={mode.Data0:X16}, position={positionX},{positionY}");
            if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue ||
                (long)positionX + width > int.MaxValue || (long)positionY + height > int.MaxValue)
                return null;
            return new Windows.Graphics.RectInt32(positionX, positionY, (int)width, (int)height);
        }
        return null;
    }

    public bool ToggleMonitors(AppSettings settings)
    {
        if (settings.MonitorIds.Count == 0 || !settings.HasPcProfile)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayService_078"));
        var (activePaths, activeModes) = DisplayConfig.Query(DisplayConfig.ActivePaths);
        HashSet<string> monitorIds = new(settings.MonitorIds, StringComparer.OrdinalIgnoreCase);
        if (!activePaths.Any(path => DisplayConfig.Key(path.TargetInfo)
            .Equals(settings.TelevisionId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayService_079"));
        if (monitorIds.Contains(settings.TelevisionId ?? string.Empty))
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayService_080"));

        if (activePaths.Any(path => monitorIds.Contains(DisplayConfig.Key(path.TargetInfo))))
        {
            DisplayPath[] remaining = activePaths.Where(path => !monitorIds.Contains(DisplayConfig.Key(path.TargetInfo))).ToArray();
            DisplayConfig.ApplyTopology(remaining, activeModes);
            return false;
        }

        DecodedDisplayProfile savedProfile = ReadPcProfile(settings, allowSourceRemap: true);
        DisplayPath[] savedPaths = savedProfile.Paths;
        DisplayMode[] savedModes = savedProfile.Modes;

        var (availablePaths, _) = DisplayConfig.Query(DisplayConfig.AllPaths);
        DisplayPath[] combinedPaths = new DisplayPath[activePaths.Length + savedPaths.Length];
        activePaths.CopyTo(combinedPaths, 0);
        List<DisplayMode> combinedModes = [.. activeModes];
        Dictionary<int, int> copiedModes = [];
        HashSet<(int High, uint Low, uint Source)> usedSources = activePaths
            .Select(path => SourceKey(path.SourceInfo)).ToHashSet();
        int rightEdge = activePaths.Select(path => GetSourceMode(activeModes, path))
            .Max(mode => checked(SourceX(mode) + (int)unchecked((uint)mode.Data0)));
        int topEdge = activePaths.Select(path => SourceY(GetSourceMode(activeModes, path))).Min();
        int savedLeftEdge = savedPaths.Select(path => SourceX(GetSourceMode(savedModes, path))).Min();
        int savedTopEdge = savedPaths.Select(path => SourceY(GetSourceMode(savedModes, path))).Min();
        int xOffset = checked(rightEdge - savedLeftEdge);
        int yOffset = checked(topEdge - savedTopEdge);

        foreach (var group in savedPaths.Select((path, index) => (Path: path, Index: index))
            .GroupBy(item => SourceKey(item.Path.SourceInfo)))
        {
            DisplayPath first = group.First().Path;
            uint[] candidates = availablePaths.Where(candidate =>
                    SameAdapter(candidate.SourceInfo.AdapterId, first.SourceInfo.AdapterId) &&
                    candidate.TargetInfo.TargetAvailable != 0 &&
                    group.All(item => availablePaths.Any(option =>
                        SameAdapter(option.SourceInfo.AdapterId, candidate.SourceInfo.AdapterId) &&
                        option.SourceInfo.Id == candidate.SourceInfo.Id &&
                        SameTarget(option.TargetInfo, item.Path.TargetInfo) &&
                        option.TargetInfo.TargetAvailable != 0)))
                .Select(candidate => candidate.SourceInfo.Id).Distinct().Order().ToArray();
            uint? sourceId = candidates.Contains(first.SourceInfo.Id) &&
                !usedSources.Contains((group.Key.High, group.Key.Low, first.SourceInfo.Id))
                ? first.SourceInfo.Id
                : candidates.Where(candidate => !usedSources.Contains((group.Key.High, group.Key.Low, candidate)))
                    .Select(candidate => (uint?)candidate).FirstOrDefault();
            if (sourceId is null)
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayService_081"));
            usedSources.Add((group.Key.High, group.Key.Low, sourceId.Value));

            foreach (var (saved, savedIndex) in group)
            {
                DisplayPath path = saved;
                path.SourceInfo.Id = sourceId.Value;
                int sourceIndex = SourceModeIndex(path);
                int copiedSourceIndex = CopyMode(sourceIndex, sourceId.Value, true);
                path.SourceInfo.ModeInfoIdx = SetSourceModeIndex(path, copiedSourceIndex);
                int targetIndex = TargetModeIndex(path);
                if (targetIndex >= 0)
                    path.TargetInfo.ModeInfoIdx = SetTargetModeIndex(path, CopyMode(targetIndex, sourceId.Value, false));
                int desktopIndex = DesktopModeIndex(path);
                if (desktopIndex >= 0)
                    path.TargetInfo.ModeInfoIdx = SetDesktopModeIndex(path, CopyMode(desktopIndex, sourceId.Value, true));
                combinedPaths[activePaths.Length + savedIndex] = path;
            }
        }

        DisplayConfig.ApplyTopology(combinedPaths, [.. combinedModes]);
        DisplayProfileCodec.StorePc(settings, savedProfile.Profile);
        return true;

        int CopyMode(int savedIndex, uint sourceId, bool sourceMode)
        {
            if (copiedModes.TryGetValue(savedIndex, out int existing)) return existing;
            if (savedIndex < 0 || savedIndex >= savedModes.Length)
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayProfileCodec_067"));
            DisplayMode mode = savedModes[savedIndex];
            if (sourceMode && mode.InfoType == 1)
            {
                mode.Id = sourceId;
                int positionX = checked(SourceX(mode) + xOffset);
                int positionY = checked(SourceY(mode) + yOffset);
                mode.Data1 = (mode.Data1 & uint.MaxValue) | ((ulong)(uint)positionX << 32);
                mode.Data2 = (mode.Data2 & 0xFFFFFFFF00000000UL) | (uint)positionY;
            }
            int index = combinedModes.Count;
            combinedModes.Add(mode);
            copiedModes.Add(savedIndex, index);
            return index;
        }
    }

    private static (int High, uint Low, uint Source) SourceKey(DisplaySource source) =>
        (source.AdapterId.HighPart, source.AdapterId.LowPart, source.Id);

    private static bool SameAdapter(AdapterLuid left, AdapterLuid right) =>
        left.HighPart == right.HighPart && left.LowPart == right.LowPart;

    private static bool SameTarget(DisplayTarget left, DisplayTarget right) =>
        SameAdapter(left.AdapterId, right.AdapterId) && left.Id == right.Id;

    private static int SourceModeIndex(DisplayPath path) =>
        (path.Flags & DisplayConfig.PathSupportsVirtualMode) != 0
            ? (int)(path.SourceInfo.ModeInfoIdx >> 16) : (int)path.SourceInfo.ModeInfoIdx;

    private static int TargetModeIndex(DisplayPath path)
    {
        uint index = (path.Flags & DisplayConfig.PathSupportsVirtualMode) != 0
            ? path.TargetInfo.ModeInfoIdx >> 16 : path.TargetInfo.ModeInfoIdx;
        return index == ((path.Flags & DisplayConfig.PathSupportsVirtualMode) != 0 ? 0xFFFF : uint.MaxValue)
            ? -1 : (int)index;
    }

    private static int DesktopModeIndex(DisplayPath path)
    {
        if ((path.Flags & DisplayConfig.PathSupportsVirtualMode) == 0) return -1;
        uint index = path.TargetInfo.ModeInfoIdx & 0xFFFF;
        return index == 0xFFFF ? -1 : (int)index;
    }

    private static uint SetSourceModeIndex(DisplayPath path, int index) =>
        (path.Flags & DisplayConfig.PathSupportsVirtualMode) != 0
            ? (path.SourceInfo.ModeInfoIdx & 0xFFFF) | ((uint)index << 16)
            : (uint)index;

    private static uint SetTargetModeIndex(DisplayPath path, int index) =>
        (path.Flags & DisplayConfig.PathSupportsVirtualMode) != 0
            ? (path.TargetInfo.ModeInfoIdx & 0xFFFF) | ((uint)index << 16)
            : (uint)index;

    private static uint SetDesktopModeIndex(DisplayPath path, int index) =>
        (path.TargetInfo.ModeInfoIdx & 0xFFFF0000) | (uint)index;

    private static DisplayMode GetSourceMode(DisplayMode[] modes, DisplayPath path)
    {
        int index = SourceModeIndex(path);
        if (index < 0 || index >= modes.Length || modes[index].InfoType != 1)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayService_082"));
        return modes[index];
    }

    private static int SourceX(DisplayMode mode) => unchecked((int)(mode.Data1 >> 32));
    private static int SourceY(DisplayMode mode) => unchecked((int)(uint)mode.Data2);

}
