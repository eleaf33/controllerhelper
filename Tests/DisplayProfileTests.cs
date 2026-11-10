using System.Runtime.InteropServices;
using System.Text.Json;
using ControllerHelper.Models;
using ControllerHelper.Native;
using ControllerHelper.Services;

internal static class DisplayProfileTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Профиль сохраняет режимы и отрицательные координаты", () => Run(RoundTrip)),
        ("Старый профиль проверяется и получает версию", () => Run(LegacyMigration)),
        ("Неизвестная версия не использует старые поля", () => Run(UnknownVersion)),
        ("Несовместимые размеры структур отклоняются", () => Run(WrongAbi)),
        ("Пустые, оборванные и чрезмерные данные отклоняются", () => Run(BadPayload)),
        ("Индексы и типы режимов проверяются до применения", () => Run(BadReferences)),
        ("Виртуальные пути и необязательные индексы проверяются", () => Run(VirtualPaths)),
        ("Неподходящий адаптер и ID режима отклоняются", () => Run(ModeIdentity)),
        ("Повторяющиеся пути и идентификаторы отклоняются", () => Run(DuplicatePaths)),
        ("Выбранные дисплеи должны точно совпадать с профилем", () => Run(SelectionMismatch)),
        ("Смена порта, адаптера или устройства отклоняется", () => Run(HardwareMismatch)),
        ("Недоступный дисплей не применяется", () => Run(Unavailable)),
        ("Смена источника разрешена только при добавлении мониторов", () => Run(SourceRemap)),
        ("Клонированные дисплеи могут делить исходный режим", () => Run(ClonePaths)),
        ("Размеры и переполнение координат проверяются", () => Run(BadDimensions)),
        ("Копия настроек не разделяет изменяемый профиль", () => Run(SettingsCopy)),
        ("JSON хранит новую схему и читает старые настройки", () => Run(SettingsJson)),
        ("Отмена подтверждения не сохраняет мигрированный профиль", CanceledMigration),
        ("Перед ТВ активные мониторы заменяют устаревший профиль ПК", () => Run(RefreshActivePc)),
        ("Неполная раскладка ПК не заменяет сохранённый профиль", () => Run(IncompletePc)),
        ("Работа на ТВ не перезаписывает раскладку ПК", () => Run(KeepPcWhileOnTv)),
        ("Отключённый ТВ без профиля использует автоматический режим", () => Run(TvWithoutProfile)),
        ("Устаревший профиль ТВ допускает автоматическое включение", () => Run(StaleTvProfile)),
        ("Корректный профиль ТВ сохраняет прежнюю частоту", () => Run(ReuseTvProfile))
    ];

    private static readonly AdapterLuid Adapter = new() { LowPart = 10, HighPart = 2 };
    private static string Identify(DisplayTarget target) => "display-" + target.Id;
    private static (DisplayPath[] Paths, DisplayMode[] Modes) Fixture(bool virtualMode = false)
    {
        DisplayPath path = new()
        {
            Flags = 1u | (virtualMode ? 8u : 0u),
            SourceInfo = new() { AdapterId = Adapter, Id = 3, ModeInfoIdx = virtualMode ? 0xFFFFu : 0 },
            TargetInfo = new() { AdapterId = Adapter, Id = 7, ModeInfoIdx = virtualMode ? 0x00010002u : 1,
                TargetAvailable = 1, Rotation = 1, Scaling = 1, RefreshNumerator = 60, RefreshDenominator = 1 }
        };
        DisplayMode source = new() { InfoType = 1, AdapterId = Adapter, Id = 3,
            Data0 = 1920UL | (1080UL << 32), Data1 = 4UL | (unchecked((ulong)(uint)-1920) << 32), Data2 = 0 };
        return ([path], [source, new() { InfoType = 2, AdapterId = Adapter, Id = 7 },
            new() { InfoType = 3, AdapterId = Adapter, Id = 3 }]);
    }

    private static DisplayProfile Profile() { var f = Fixture(); return DisplayProfileCodec.Capture(f.Paths, f.Modes, Identify); }
    private static DecodedDisplayProfile Read(DisplayProfile profile)
    { var f = Fixture(); return DisplayProfileCodec.Read(profile, null, null, ["display-7"], f.Paths, Identify); }
    private static string Encode<T>(T[] values) where T : unmanaged => Convert.ToBase64String(MemoryMarshal.AsBytes(values.AsSpan()));
    private static void RoundTrip()
    {
        var f = Fixture(); DisplayProfile profile = Profile();
        var decoded = Read(profile);
        Check(Encode(decoded.Paths) == Encode(f.Paths) && Encode(decoded.Modes) == Encode(f.Modes), "Native fields changed.");
        decoded.Modes[0].Data0 = 0;
        Check(Read(profile).Modes[0].Data0 != 0, "Decoded arrays share storage.");
    }
    private static void LegacyMigration()
    {
        var f = Fixture();
        var decoded = DisplayProfileCodec.Read(null, Encode(f.Paths), Encode(f.Modes), ["display-7"], f.Paths, Identify);
        Check(decoded.Profile.Version == 1 && decoded.Profile.DisplayIds!.SequenceEqual(new[] { "display-7" }), "No migration metadata.");
    }
    private static void UnknownVersion()
    {
        var f = Fixture(); var profile = Profile();
        foreach (int version in new[] { 0, -1, 2 })
        {
            profile.Version = version;
            Reject(() => DisplayProfileCodec.Read(profile, Encode(f.Paths), Encode(f.Modes), ["display-7"], f.Paths, Identify));
        }
        profile.Version = 1; profile.Paths = null;
        Reject(() => DisplayProfileCodec.Read(profile, Encode(f.Paths), Encode(f.Modes), ["display-7"], f.Paths, Identify));
    }
    private static void WrongAbi()
    {
        var profile = Profile(); profile.PathSize++; Reject(() => Read(profile));
        profile = Profile(); profile.ModeSize--; Reject(() => Read(profile));
    }
    private static void BadPayload()
    {
        foreach (string? value in new[] { null, "", "%%%", "AQ==", new string('A', 600000) })
        {
            var profile = Profile(); profile.Paths = value; Reject(() => Read(profile));
            profile = Profile(); profile.Modes = value; Reject(() => Read(profile));
        }
    }
    private static void BadReferences()
    {
        var f = Fixture(); f.Paths[0].SourceInfo.ModeInfoIdx = uint.MaxValue;
        Reject(() => DisplayProfileCodec.ValidateStructure(f.Paths, f.Modes));
        f = Fixture(); f.Paths[0].TargetInfo.ModeInfoIdx = 500;
        Reject(() => DisplayProfileCodec.ValidateStructure(f.Paths, f.Modes));
        f = Fixture(); f.Modes[0].InfoType = 2;
        Reject(() => DisplayProfileCodec.ValidateStructure(f.Paths, f.Modes));
    }
    private static void VirtualPaths()
    {
        var f = Fixture(true); DisplayProfileCodec.ValidateStructure(f.Paths, f.Modes);
        f.Paths[0].TargetInfo.ModeInfoIdx = uint.MaxValue; DisplayProfileCodec.ValidateStructure(f.Paths, f.Modes);
        f.Paths[0].SourceInfo.ModeInfoIdx = uint.MaxValue; Reject(() => DisplayProfileCodec.ValidateStructure(f.Paths, f.Modes));
        f = Fixture(true); f.Modes[2].InfoType = 1; Reject(() => DisplayProfileCodec.ValidateStructure(f.Paths, f.Modes));
    }
    private static void ModeIdentity()
    {
        var f = Fixture(); f.Modes[0].AdapterId.LowPart++; Reject(() => DisplayProfileCodec.ValidateStructure(f.Paths, f.Modes));
        f = Fixture(); f.Modes[1].Id++; Reject(() => DisplayProfileCodec.ValidateStructure(f.Paths, f.Modes));
    }
    private static void DuplicatePaths()
    {
        var f = Fixture(); Reject(() => DisplayProfileCodec.Capture([f.Paths[0], f.Paths[0]], f.Modes, Identify));
        var p = Profile(); p.DisplayIds = ["display-7", "display-7"]; Reject(() => Read(p));
        p.DisplayIds = [null!]; Reject(() => Read(p));
    }
    private static void SelectionMismatch()
    {
        var f = Fixture(); var p = Profile();
        foreach (string[] ids in new[] { Array.Empty<string>(), new[] { "display-8" }, new[] { "display-7", "display-7" } })
            Reject(() => DisplayProfileCodec.Read(p, null, null, ids, f.Paths, Identify));
    }
    private static void HardwareMismatch()
    {
        var f = Fixture(); f.Paths[0].TargetInfo.Id++; Reject(() => DisplayProfileCodec.Read(Profile(), null, null, ["display-7"], f.Paths, Identify));
        f = Fixture(); f.Paths[0].TargetInfo.AdapterId.LowPart++; Reject(() => DisplayProfileCodec.Read(Profile(), null, null, ["display-7"], f.Paths, Identify));
        f = Fixture(); Reject(() => DisplayProfileCodec.Read(Profile(), null, null, ["display-7"], f.Paths, _ => "another-device"));
    }
    private static void Unavailable()
    {
        var f = Fixture(); f.Paths[0].TargetInfo.TargetAvailable = 0;
        Reject(() => DisplayProfileCodec.Read(Profile(), null, null, ["display-7"], f.Paths, Identify));
        DisplayProfileCodec.Read(Profile(), null, null, ["display-7"], f.Paths, Identify, requireAvailable: false);
    }
    private static void SourceRemap()
    {
        var f = Fixture(); f.Paths[0].SourceInfo.Id++;
        Reject(() => DisplayProfileCodec.Read(Profile(), null, null, ["display-7"], f.Paths, Identify));
        DisplayProfileCodec.Read(Profile(), null, null, ["display-7"], f.Paths, Identify, allowSourceRemap: true);
    }
    private static void ClonePaths()
    {
        var f = Fixture(); DisplayPath second = f.Paths[0]; second.TargetInfo.Id = 8; second.TargetInfo.ModeInfoIdx = 3;
        DisplayMode target = f.Modes[1]; target.Id = 8;
        DisplayProfileCodec.ValidateStructure([f.Paths[0], second], [.. f.Modes, target]);
    }
    private static void BadDimensions()
    {
        var f = Fixture(); f.Modes[0].Data0 = 0; Reject(() => DisplayProfileCodec.ValidateStructure(f.Paths, f.Modes));
        f = Fixture(); f.Modes[0].Data1 = (ulong)int.MaxValue << 32; Reject(() => DisplayProfileCodec.ValidateStructure(f.Paths, f.Modes));
    }
    private static void SettingsCopy()
    {
        AppSettings settings = new(); DisplayProfileCodec.StorePc(settings, Profile());
        var copy = settings.Copy(); copy.PcProfile!.DisplayIds![0] = "changed";
        Check(settings.PcProfile!.DisplayIds![0] == "display-7", "Settings share mutable profile identity.");
    }
    private static void SettingsJson()
    {
        AppSettings settings = new(); DisplayProfileCodec.StorePc(settings, Profile());
        AppSettings loaded = SettingsStore.Deserialize(SettingsStore.Serialize(settings), out _)!;
        Check(loaded.HasPcProfile && loaded.PcProfile!.Version == 1, "Profile not serialized.");
        var old = SettingsStore.Deserialize("{\"PcPaths\":\"old\",\"PcModes\":\"old\"}", out _)!;
        Check(old.HasPcProfile && old.PcProfile is null, "Old JSON unreadable.");
    }
    private static async Task CanceledMigration()
    {
        AppSettings settings = new() { PcPaths = Profile().Paths, PcModes = Profile().Modes };
        AppSettings working = settings.Copy(); bool saved = false;
        try
        {
            await ProfileSwitchTransaction.ApplyAsync<int, int>(() => 0,
                () => DisplayProfileCodec.StorePc(working, Profile()), _ => { },
                confirmDisplay: () => Task.FromResult(false), finish: () => saved = true);
            throw new Exception("Missing rejection.");
        }
        catch (TimeoutException) { Check(!saved && settings.PcProfile is null, "Rejected migration escaped working copy."); }
    }

    private static void RefreshActivePc()
    {
        var fixture = Fixture();
        fixture.Paths[0].TargetInfo.RefreshNumerator = 120;
        AppSettings settings = new() { MonitorIds = ["display-7"], TelevisionId = "display-8", PcProfile = Profile() };
        settings.PcProfile.DisplayIds = ["old-display"];
        AppSettings working = DisplayProfileCodec.PreparePcForTelevision(settings,
            fixture.Paths, fixture.Modes, fixture.Paths, Identify);
        var decoded = DisplayProfileCodec.Read(working.PcProfile, null, null, working.MonitorIds, fixture.Paths, Identify);
        Check(decoded.Paths[0].TargetInfo.RefreshNumerator == 120, "Active refresh rate lost.");
        Check(settings.PcProfile.DisplayIds[0] == "old-display", "Preparation modified committed settings.");
    }

    private static void IncompletePc()
    {
        var fixture = Fixture();
        AppSettings settings = new() { MonitorIds = ["display-7", "display-8"], TelevisionId = "display-9", PcProfile = Profile() };
        Reject(() => DisplayProfileCodec.PreparePcForTelevision(settings,
            fixture.Paths, fixture.Modes, fixture.Paths, Identify));
    }

    private static void KeepPcWhileOnTv()
    {
        var fixture = Fixture();
        DisplayPath television = fixture.Paths[0];
        television.TargetInfo.Id = 8;
        AppSettings settings = new() { MonitorIds = ["display-7"], TelevisionId = "display-8", PcProfile = Profile() };
        AppSettings working = DisplayProfileCodec.PreparePcForTelevision(settings,
            [television], fixture.Modes, [.. fixture.Paths, television], Identify);
        Check(working.PcProfile!.Paths == settings.PcProfile.Paths, "TV layout replaced PC layout.");
    }

    private static void TvWithoutProfile()
    {
        var fixture = Fixture();
        AppSettings settings = new() { TelevisionId = "display-7" };
        Check(DisplayProfileCodec.TryReadTelevision(settings, fixture.Paths, Identify) is null,
            "Missing profile must permit automatic activation.");
    }

    private static void StaleTvProfile()
    {
        var fixture = Fixture();
        AppSettings settings = new() { TelevisionId = "display-7", TvProfile = Profile() };
        settings.TvProfile.DisplayIds = ["old-display"];
        Check(DisplayProfileCodec.TryReadTelevision(settings, fixture.Paths, Identify) is null,
            "Stale profile blocked automatic activation.");
        settings.TvProfile.Version = 99;
        Reject(() => DisplayProfileCodec.TryReadTelevision(settings, fixture.Paths, Identify));
    }

    private static void ReuseTvProfile()
    {
        var fixture = Fixture();
        AppSettings settings = new() { TelevisionId = "display-7", TvProfile = Profile() };
        var result = DisplayProfileCodec.TryReadTelevision(settings, fixture.Paths, Identify);
        Check(result is not null && Encode(result.Paths) == settings.TvProfile.Paths,
            "Valid saved timings should be reused.");
    }

    internal static int ValidateTelevisionPreparationFile(string file)
    {
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file))!;
        var (active, modes) = DisplayConfig.Query(DisplayConfig.ActivePaths);
        var (available, _) = DisplayConfig.Query(DisplayConfig.AllPaths);
        AppSettings working = DisplayProfileCodec.PreparePcForTelevision(settings, active, modes, available, DisplayConfig.Key);
        var pc = DisplayProfileCodec.Read(working.PcProfile, working.PcPaths, working.PcModes,
            working.MonitorIds, available, DisplayConfig.Key);
        DisplayConfig.ValidateTopology(pc.Paths, pc.Modes);
        Console.WriteLine($"PC preparation: valid; monitors={pc.Paths.Length}; restore validated.");
        var tv = DisplayProfileCodec.TryReadTelevision(working, available, DisplayConfig.Key);
        if (tv is null)
        {
            var target = available.First(path => path.TargetInfo.TargetAvailable != 0 &&
                string.Equals(DisplayConfig.Key(path.TargetInfo), working.TelevisionId, StringComparison.OrdinalIgnoreCase));
            DisplayConfig.ValidateTopology([DisplayConfig.PrepareSingleTarget(target)], []);
            Console.WriteLine("TV: detected while disabled; automatic activation validated.");
        }
        else Console.WriteLine("TV: saved profile valid.");
        return 0;
    }

    // Explicit read-only diagnostic. Reads existing settings, queries Windows, never saves or applies a topology.
    internal static int ValidateFile(string file)
    {
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file))!;
        var (current, _) = DisplayConfig.Query(DisplayConfig.AllPaths);
        int failed = 0;
        CheckOne("PC", settings.PcProfile, settings.PcPaths, settings.PcModes, settings.MonitorIds);
        if (settings.TelevisionId is string id) CheckOne("TV", settings.TvProfile, settings.TvPath, settings.TvModes, [id]);
        return failed;
        void CheckOne(string name, DisplayProfile? profile, string? paths, string? modes, IReadOnlyCollection<string> ids)
        {
            try
            {
                var result = DisplayProfileCodec.Read(profile, paths, modes, ids, current, DisplayConfig.Key, requireAvailable: false);
                Console.WriteLine($"{name}: valid, paths={result.Paths.Length}, modes={result.Modes.Length}, legacy={profile is null}");
            }
            catch (Exception error) { Console.WriteLine($"{name}: {error.Message}"); failed++; }
        }
    }
    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException error) { Check(error.Message.Contains("заново"), "No recovery advice."); return; }
        throw new Exception("Unsafe profile accepted.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
