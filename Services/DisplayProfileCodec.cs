using System.Runtime.InteropServices;
using ControllerHelper.Models;
using ControllerHelper.Native;

namespace ControllerHelper.Services;

internal sealed record DecodedDisplayProfile(DisplayPath[] Paths, DisplayMode[] Modes, DisplayProfile Profile);

internal static class DisplayProfileCodec
{
    internal const int CurrentVersion = 1;
    internal const int MaximumPaths = 64;
    internal const int MaximumModes = 4096;
    private const string Recovery = " Включите нужные экраны в параметрах Windows и сохраните профиль заново.";

    internal static DisplayProfile Capture(DisplayPath[] paths, DisplayMode[] modes, Func<DisplayTarget, string> identify)
    {
        ValidateStructure(paths, modes);
        string[] ids = paths.Select(path => identify(path.TargetInfo)).ToArray();
        ValidateIds(ids, paths.Length);
        return new DisplayProfile
        {
            Version = CurrentVersion, PathSize = Marshal.SizeOf<DisplayPath>(), ModeSize = Marshal.SizeOf<DisplayMode>(),
            Paths = Encode(paths), Modes = Encode(modes), DisplayIds = ids
        };
    }

    // A present envelope is authoritative: malformed/newer versions must never fall back to old fields.
    internal static DecodedDisplayProfile Read(DisplayProfile? profile, string? legacyPaths, string? legacyModes,
        IReadOnlyCollection<string> expectedIds, DisplayPath[] currentPaths, Func<DisplayTarget, string> identify,
        bool allowSourceRemap = false, bool requireAvailable = true)
    {
        ValidateVersion(profile);
        if (profile is not null && (profile.PathSize != Marshal.SizeOf<DisplayPath>() || profile.ModeSize != Marshal.SizeOf<DisplayMode>()))
            throw Invalid("Формат структур сохранённого профиля несовместим с этой сборкой.");
        DisplayPath[] paths = Decode<DisplayPath>(profile is null ? legacyPaths : profile.Paths, MaximumPaths);
        DisplayMode[] modes = Decode<DisplayMode>(profile is null ? legacyModes : profile.Modes, MaximumModes);
        ValidateStructure(paths, modes);
        string[] ids = profile is null ? paths.Select(path => identify(path.TargetInfo)).ToArray() : profile.DisplayIds ?? [];
        ValidateIds(ids, paths.Length);
        ValidateIds(expectedIds.ToArray(), paths.Length);
        if (!ids.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedIds))
            throw Invalid("Сохранённый профиль не соответствует выбранным дисплеям.");
        for (int index = 0; index < paths.Length; index++)
        {
            DisplayPath saved = paths[index];
            bool found = currentPaths.Any(current =>
                (!requireAvailable || current.TargetInfo.TargetAvailable != 0) &&
                SameAdapter(current.SourceInfo.AdapterId, saved.SourceInfo.AdapterId) &&
                (allowSourceRemap || current.SourceInfo.Id == saved.SourceInfo.Id) &&
                SameAdapter(current.TargetInfo.AdapterId, saved.TargetInfo.AdapterId) &&
                current.TargetInfo.Id == saved.TargetInfo.Id &&
                string.Equals(identify(current.TargetInfo), ids[index], StringComparison.OrdinalIgnoreCase));
            if (!found) throw Invalid("Дисплей недоступен или подключение видеокарты изменилось.");
        }
        DisplayProfile validated = profile?.Copy() ?? Capture(paths, modes, identify);
        return new DecodedDisplayProfile(paths, modes, validated);
    }

    internal static void ValidateVersion(DisplayProfile? profile)
    {
        if (profile is not null && profile.Version != CurrentVersion)
            throw Invalid($"Версия профиля дисплеев {profile.Version} не поддерживается.");
    }

    internal static AppSettings PreparePcForTelevision(AppSettings settings, DisplayPath[] activePaths,
        DisplayMode[] activeModes, DisplayPath[] availablePaths, Func<DisplayTarget, string> identify)
    {
        AppSettings working = settings.Copy();
        ValidateVersion(working.PcProfile);
        HashSet<string> selected = new(working.MonitorIds, StringComparer.OrdinalIgnoreCase);
        DisplayPath[] monitors = activePaths.Where(path => selected.Contains(identify(path.TargetInfo))).ToArray();
        bool televisionActive = activePaths.Any(path =>
            string.Equals(identify(path.TargetInfo), working.TelevisionId, StringComparison.OrdinalIgnoreCase));
        if (!televisionActive && selected.Count > 0 && monitors.Length == selected.Count)
            StorePc(working, Capture(monitors, activeModes, identify));
        Read(working.PcProfile, working.PcPaths, working.PcModes, working.MonitorIds,
            availablePaths, identify, requireAvailable: false);
        return working;
    }

    internal static DecodedDisplayProfile? TryReadTelevision(AppSettings settings, DisplayPath[] availablePaths,
        Func<DisplayTarget, string> identify)
    {
        if (settings.TelevisionMode is null) ValidateVersion(settings.TvProfile);
        if (string.IsNullOrWhiteSpace(settings.TelevisionId))
            throw new InvalidOperationException("Выберите телевизор.");
        try
        {
            return Read(settings.TvProfile, settings.TvPath, settings.TvModes,
                [settings.TelevisionId], availablePaths, identify);
        }
        catch (InvalidOperationException) { return null; }
    }

    internal static void StorePc(AppSettings settings, DisplayProfile profile)
    {
        settings.PcProfile = profile.Copy();
        settings.PcPaths = profile.Paths;
        settings.PcModes = profile.Modes;
    }

    internal static void StoreTv(AppSettings settings, DisplayProfile profile)
    {
        settings.TvProfile = profile.Copy();
        settings.TvPath = profile.Paths;
        settings.TvModes = profile.Modes;
    }

    internal static void ValidateStructure(DisplayPath[] paths, DisplayMode[] modes)
    {
        if (paths.Length is 0 or > MaximumPaths || modes.Length is 0 or > MaximumModes)
            throw Invalid("Недопустимый размер сохранённой раскладки.");
        HashSet<(int, uint, uint)> targets = [];
        foreach (DisplayPath path in paths)
        {
            if ((path.Flags & DisplayConfig.PathActive) == 0 ||
                !targets.Add((path.TargetInfo.AdapterId.HighPart, path.TargetInfo.AdapterId.LowPart, path.TargetInfo.Id)))
                throw Invalid("Профиль содержит неактивный или повторяющийся видеопуть.");
            bool virtualMode = (path.Flags & DisplayConfig.PathSupportsVirtualMode) != 0;
            uint sourceIndex = virtualMode ? path.SourceInfo.ModeInfoIdx >> 16 : path.SourceInfo.ModeInfoIdx;
            DisplayMode source = RequireMode(sourceIndex, 1, path.SourceInfo.AdapterId, path.SourceInfo.Id);
            uint width = (uint)source.Data0, height = (uint)(source.Data0 >> 32);
            int x = unchecked((int)(source.Data1 >> 32)), y = unchecked((int)(uint)source.Data2);
            if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue ||
                (long)x + width > int.MaxValue || (long)y + height > int.MaxValue)
                throw Invalid("Недопустимые размеры или положение дисплея.");
            uint targetIndex = virtualMode ? path.TargetInfo.ModeInfoIdx >> 16 : path.TargetInfo.ModeInfoIdx;
            if (targetIndex != (virtualMode ? 0xFFFF : uint.MaxValue))
                RequireMode(targetIndex, 2, path.TargetInfo.AdapterId, path.TargetInfo.Id);
            if (virtualMode && (path.TargetInfo.ModeInfoIdx & 0xFFFF) != 0xFFFF)
                RequireMode(path.TargetInfo.ModeInfoIdx & 0xFFFF, 3, path.SourceInfo.AdapterId, null);
        }

        DisplayMode RequireMode(uint index, uint type, AdapterLuid adapter, uint? id)
        {
            if (index >= modes.Length) throw Invalid("Индекс режима выходит за границы сохранённого профиля.");
            DisplayMode mode = modes[index];
            if (mode.InfoType != type || !SameAdapter(mode.AdapterId, adapter) || (id is not null && mode.Id != id))
                throw Invalid("Режим дисплея не соответствует видеопути.");
            return mode;
        }
    }

    private static void ValidateIds(string[] ids, int count)
    {
        if (ids.Length != count || ids.Any(string.IsNullOrWhiteSpace) || ids.Any(id => id.Length > 1024) ||
            ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != count)
            throw Invalid("Некорректный список дисплеев в профиле.");
    }

    private static bool SameAdapter(AdapterLuid a, AdapterLuid b) => a.HighPart == b.HighPart && a.LowPart == b.LowPart;
    private static string Encode<T>(T[] values) where T : unmanaged => Convert.ToBase64String(MemoryMarshal.AsBytes(values.AsSpan()));
    private static T[] Decode<T>(string? encoded, int maximumCount) where T : unmanaged
    {
        int size = Marshal.SizeOf<T>();
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > ((maximumCount * size + 2) / 3) * 4)
            throw Invalid("Сохранённая раскладка отсутствует или превышает допустимый размер.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw Invalid("Сохранённая раскладка содержит некорректные данные."); }
        if (bytes.Length == 0 || bytes.Length % size != 0 || bytes.Length / size > maximumCount)
            throw Invalid("Сохранённая раскладка дисплеев повреждена.");
        return MemoryMarshal.Cast<byte, T>(bytes).ToArray();
    }
    private static InvalidOperationException Invalid(string reason) => new(reason + Recovery);
}
