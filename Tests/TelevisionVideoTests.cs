using System.Runtime.InteropServices;
using System.Text.Json;
using ControllerHelper.Models;
using ControllerHelper.Native;
using ControllerHelper.Services;

internal static class TelevisionVideoTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("ТВ: явные 4K/120 заменяют текущие 1080p без перезаписи выбора", ExplicitMode),
        ("ТВ: совпадающие режим и DPI не применяются повторно даже при откате", MatchingSettings),
        ("ТВ: при совпадающем режиме меняется только нужный DPI", MatchingMode),
        ("ТВ: различие 119 и 120 Гц требует смены режима", DifferentFrequency),
        ("ТВ: масштаб применяется после режима к выбранному устройству", ApplyOrder),
        ("ТВ: недоступный режим не заменяется ближайшим", MissingMode),
        ("ТВ: отказ драйвера не запускает масштабирование", ModeRejected),
        ("ТВ: успех API без смены режима считается ошибкой", ModeNotApplied),
        ("ТВ: успех API без смены DPI считается ошибкой", ScaleNotApplied),
        ("ТВ: диапазон DPI перечитывается после смены разрешения", ScaleRangeChanges),
        ("ТВ: при откате режим восстанавливается перед диапазоном DPI", ScaleRangeRollback),
        ("ТВ: начальная топология не использует старые режимы", BootstrapTopology),
        ("ТВ: пользовательский глобальный DPI блокирует отдельное изменение", CustomScaleBlocked),
        ("ТВ: режим без изменения DPI работает при общем масштабе", CustomScaleModeOnly),
        ("ТВ: отмена после смены режима не запускает DPI", CancelAfterMode),
        ("ТВ: отказ подтверждения возвращает DPI, топологию и звук", ConfirmationRollback),
        ("ТВ: ошибка отката DPI не мешает откату топологии и звука", ScaleRollbackFails),
        ("ТВ: 59 и 60 Гц и чересстрочность хранятся раздельно", ModeIdentity),
        ("ТВ: параметры сохраняются в JSON и копии настроек", SettingsRoundtrip),
        ("ТВ: старые настройки сохраняют прежнее поведение", LegacySettings),
        ("ТВ: диапазон масштаба не округляет неподдерживаемые значения", ScaleValidation),
        ("ТВ: структуры DEVMODE, DisplayDevice и DPI соответствуют ABI", NativeAbi)
    ];

    private static readonly TelevisionVideoMode Hd = new(1920, 1080, 60);
    private static readonly TelevisionVideoMode Uhd = new(3840, 2160, 120);
    private static AppSettings Settings(int? scale = null) => new()
    { TelevisionId = "tv", TelevisionMode = Uhd, TelevisionScalePercent = scale };

    private sealed class Api : ITelevisionDisplayApi
    {
        internal readonly List<string> Calls = [];
        internal TelevisionVideoMode Current = Hd;
        internal TelevisionVideoMode[] Modes = [Hd, Uhd];
        internal DisplayScaleRange Range = new(-2, 0, 2);
        internal bool RejectMode, IgnoreMode, IgnoreScale, RejectScaleRestore;
        internal Action? AfterMode;
        public string? ScaleBlockReason { get; set; }
        public TelevisionVideoMode[] GetModes(string id) { CheckId(id); return Modes; }
        public TelevisionVideoMode GetCurrentMode(string id) { CheckId(id); return Current; }
        public void SetMode(string id, TelevisionVideoMode mode)
        {
            CheckId(id); Calls.Add("mode");
            if (RejectMode) throw new InvalidOperationException("driver rejected mode");
            if (!IgnoreMode) Current = mode;
            AfterMode?.Invoke();
        }
        public DisplayScaleRange GetScale(string id) { CheckId(id); Calls.Add("read scale"); return Range; }
        public void SetScale(string id, int relative)
        {
            CheckId(id); Calls.Add($"scale {relative}");
            if (RejectScaleRestore && relative == 0) throw new InvalidOperationException("DPI restore failed");
            if (!IgnoreScale) Range = Range with { CurrentRelative = relative };
        }
        private static void CheckId(string id) => Check(id == "tv", "A PC monitor was targeted.");
    }

    private static Task ExplicitMode()
    {
        Api api = new(); var settings = Settings();
        new TelevisionDisplayOptions(api).Apply(settings, _ => { }, default);
        Check(api.Current == Uhd && settings.TelevisionMode == Uhd, "Explicit mode was lost to today's desktop.");
        return Task.CompletedTask;
    }

    private static Task ApplyOrder()
    {
        Api api = new(); DisplaySettingsRollback rollback = new();
        new TelevisionDisplayOptions(api).Apply(Settings(100), rollback.Add, default);
        Check(api.Calls.SequenceEqual(["read scale", "mode", "read scale", "scale -2", "read scale"]), "Wrong apply order.");
        rollback.Restore(() => api.Calls.Add("topology"));
        Check(api.Range.CurrentRelative == 0 && api.Calls[^1] == "topology", "DPI was not restored before deactivating TV.");
        return Task.CompletedTask;
    }

    private static Task MatchingSettings()
    {
        Api api = new() { Current = Uhd, Range = new(-2, -2, 2) };
        DisplaySettingsRollback rollback = new();
        new TelevisionDisplayOptions(api).Apply(Settings(100), rollback.Add, default);
        rollback.Restore(() => api.Calls.Add("topology"));
        Check(!api.Calls.Contains("mode") && !api.Calls.Any(call => call.StartsWith("scale ")) &&
            api.Calls[^1] == "topology", "Unchanged video options still caused a native mutation.");
        return Task.CompletedTask;
    }

    private static Task MatchingMode()
    {
        Api api = new() { Current = Uhd };
        new TelevisionDisplayOptions(api).Apply(Settings(100), _ => { }, default);
        Check(!api.Calls.Contains("mode") && api.Range.CurrentPercent == 100,
            "A DPI-only change reset the existing display timing.");
        return Task.CompletedTask;
    }

    private static Task DifferentFrequency()
    {
        Api api = new() { Current = Uhd with { Frequency = 119 } };
        new TelevisionDisplayOptions(api).Apply(Settings(), _ => { }, default);
        Check(api.Calls.SequenceEqual(["mode"]) && api.Current == Uhd,
            "The unchanged-mode optimization ignored a refresh-rate difference.");
        return Task.CompletedTask;
    }

    private static Task MissingMode()
    {
        Api api = new() { Modes = [Hd] };
        Throws(() => new TelevisionDisplayOptions(api).Apply(Settings(), _ => { }, default));
        Check(api.Calls.Count == 0 && api.Current == Hd, "Unavailable mode silently fell back.");
        return Task.CompletedTask;
    }

    private static Task ModeRejected()
    {
        Api api = new() { RejectMode = true };
        Throws(() => new TelevisionDisplayOptions(api).Apply(Settings(100), _ => { }, default));
        Check(!api.Calls.Any(call => call.StartsWith("scale ")), "DPI applied after mode failure.");
        return Task.CompletedTask;
    }

    private static Task ModeNotApplied()
    {
        Api api = new() { IgnoreMode = true };
        Throws(() => new TelevisionDisplayOptions(api).Apply(Settings(), _ => { }, default));
        return Task.CompletedTask;
    }

    private static Task ScaleNotApplied()
    {
        Api api = new() { IgnoreScale = true };
        Throws(() => new TelevisionDisplayOptions(api).Apply(Settings(100), _ => { }, default));
        return Task.CompletedTask;
    }

    private static Task ScaleRangeChanges()
    {
        Api api = new() { Range = new(0, 0, 0) };
        api.AfterMode = () => api.Range = new(-4, 0, 2);
        new TelevisionDisplayOptions(api).Apply(Settings(200), _ => { }, default);
        Check(api.Range.CurrentPercent == 200, "Scale resolved against old resolution's range.");
        return Task.CompletedTask;
    }

    private static Task CustomScaleBlocked()
    {
        Api api = new() { ScaleBlockReason = "global custom scaling" };
        Throws(() => new TelevisionDisplayOptions(api).Preflight(Settings(100)));
        Check(api.Calls.Count == 0, "Preflight changed displays.");
        return Task.CompletedTask;
    }

    private static Task ScaleRangeRollback()
    {
        Api api = new() { Range = new(-4, 0, 2) };
        api.AfterMode = () => api.Range = api.Current == Uhd ? new(-2, -2, 0) : new(-4, -4, 2);
        DisplaySettingsRollback rollback = new();
        new TelevisionDisplayOptions(api).Apply(Settings(100), rollback.Add, default);
        rollback.Restore(() => api.Calls.Add("topology"));
        Check(api.Current == Hd && api.Range.CurrentPercent == 200 &&
            api.Calls.TakeLast(3).SequenceEqual(["mode", "scale 0", "topology"]),
            "Original DPI was not restored in its original resolution's range.");
        return Task.CompletedTask;
    }

    private static Task BootstrapTopology()
    {
        var original = new DisplayPath
        {
            Flags = 0x49, SourceInfo = new() { Id = 3, ModeInfoIdx = 17 },
            TargetInfo = new() { Id = 9, ModeInfoIdx = 12, RefreshNumerator = 60, RefreshDenominator = 1, ScanLineOrdering = 1 }
        };
        var prepared = DisplayConfig.PrepareSingleTarget(original);
        Check(prepared.SourceInfo.Id == 3 && prepared.TargetInfo.Id == 9 && prepared.Flags == DisplayConfig.PathActive &&
            prepared.SourceInfo.ModeInfoIdx == uint.MaxValue && prepared.TargetInfo.ModeInfoIdx == uint.MaxValue &&
            prepared.TargetInfo.RefreshNumerator == 0 && prepared.TargetInfo.RefreshDenominator == 0 &&
            original.SourceInfo.ModeInfoIdx == 17, "Bootstrap retained stale mode indices or changed target identity.");
        return Task.CompletedTask;
    }

    private static Task CustomScaleModeOnly()
    {
        Api api = new() { ScaleBlockReason = "global custom scaling" };
        new TelevisionDisplayOptions(api).Apply(Settings(), _ => { }, default);
        Check(api.Calls.SequenceEqual(["mode"]), "Resolution-only switch touched global DPI.");
        return Task.CompletedTask;
    }

    private static Task CancelAfterMode()
    {
        Api api = new(); using CancellationTokenSource cancellation = new();
        api.AfterMode = cancellation.Cancel;
        try { new TelevisionDisplayOptions(api).Apply(Settings(100), _ => { }, cancellation.Token); }
        catch (OperationCanceledException)
        {
            Check(!api.Calls.Any(call => call.StartsWith("scale ")), "Cancellation still changed DPI.");
            return Task.CompletedTask;
        }
        throw new Exception("Cancellation ignored.");
    }

    private static async Task ConfirmationRollback()
    {
        Api api = new(); DisplaySettingsRollback rollback = new(); bool topology = false, audio = false;
        try
        {
            await ProfileSwitchTransaction.ApplyAsync<int, int>(() => 0,
                () => new TelevisionDisplayOptions(api).Apply(Settings(100), rollback.Add, default),
                _ => rollback.Restore(() => topology = true), () => 0, () => { }, _ => audio = true,
                () => Task.FromResult(false));
        }
        catch (TimeoutException)
        {
            Check(api.Range.CurrentRelative == 0 && topology && audio, "Cancel did not restore every subsystem.");
            return;
        }
        throw new Exception("Confirmation rejection was accepted.");
    }

    private static async Task ScaleRollbackFails()
    {
        Api api = new() { RejectScaleRestore = true }; DisplaySettingsRollback rollback = new();
        bool topology = false, audio = false;
        try
        {
            await ProfileSwitchTransaction.ApplyAsync<int, int>(() => 0,
                () => new TelevisionDisplayOptions(api).Apply(Settings(100), rollback.Add, default),
                _ => rollback.Restore(() => topology = true), () => 0, () => { }, _ => audio = true,
                () => Task.FromResult(false));
        }
        catch (InvalidOperationException error)
        {
            Check(topology && audio && error.InnerException is AggregateException, "DPI failure hid or prevented other rollback.");
            return;
        }
        throw new Exception("Rollback failure was hidden.");
    }

    private static Task ModeIdentity()
    {
        Check(Hd != Hd with { Frequency = 59 } && Hd != Hd with { Interlaced = true }, "Distinct Windows modes were merged.");
        return Task.CompletedTask;
    }

    private static Task SettingsRoundtrip()
    {
        AppSettings original = Settings(100);
        AppSettings restored = SettingsStore.Deserialize(SettingsStore.Serialize(original), out _)!;
        Check(restored.TelevisionMode == Uhd && restored.TelevisionScalePercent == 100, "Video preferences lost in JSON.");
        var copy = original.Copy(); copy.TelevisionMode = Hd;
        Check(original.TelevisionMode == Uhd, "Editing copied settings changed saved mode.");
        Check(!SettingsStore.Serialize(original).Contains("RefreshLabel"), "Derived UI text leaked into preferences.");
        return Task.CompletedTask;
    }

    private static Task LegacySettings()
    {
        AppSettings settings = SettingsStore.Deserialize("{}", out _)!;
        Check(settings.TelevisionMode is null && settings.TelevisionScalePercent is null, "Legacy configuration gained a forced mode.");
        return Task.CompletedTask;
    }

    private static Task ScaleValidation()
    {
        var range = new DisplayScaleRange(-2, 0, 2);
        Check(range.CurrentPercent == 150 && range.RelativeFor(100) == -2 && range.RelativeFor(200) == 2, "Relative DPI mapping is wrong.");
        Throws(() => range.RelativeFor(135));
        Throws(() => range.RelativeFor(300));
        try { new DisplayScaleRange(int.MinValue, 0, int.MaxValue).Validate(); }
        catch (NotSupportedException) { return Task.CompletedTask; }
        throw new Exception("Invalid native DPI range was accepted.");
    }

    private static Task NativeAbi()
    {
        Check(Marshal.SizeOf<DisplayVideoNative.DevMode>() == 220, "DEVMODEW ABI mismatch.");
        Check(Marshal.SizeOf<DisplayVideoNative.Device>() == 840, "DISPLAY_DEVICEW ABI mismatch.");
        Check(Marshal.SizeOf<DisplayVideoNative.SourceName>() == 84, "Source name ABI mismatch.");
        Check(Marshal.SizeOf<DisplayVideoNative.DpiGet>() == 32 && Marshal.SizeOf<DisplayVideoNative.DpiSet>() == 24, "DPI ABI mismatch.");
        return Task.CompletedTask;
    }

    internal static int Probe()
    {
        if (!OperatingSystem.IsWindows()) return 1;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ControllerHelper", "settings.json")));
        string id = json.RootElement.GetProperty("TelevisionId").GetString()!;
        var api = new WindowsTelevisionDisplayApi();
        var catalog = api.GetCatalog(id);
        Console.WriteLine($"Windows mode count: {catalog.Modes.Length}; current: {catalog.CurrentMode}");
        foreach (var mode in catalog.Modes.Where(mode => mode.Width >= 1920)) Console.WriteLine(mode);
        Console.WriteLine($"Mode warning: {catalog.ModeWarning}; scale warning: {catalog.ScaleWarning}");
        if (catalog.Scale is { } scale) Console.WriteLine($"Scale: {scale.CurrentPercent}; available: {string.Join(", ", scale.Choices)}");
        var (paths, _) = DisplayConfig.Query(DisplayConfig.AllPaths);
        var targets = paths.Where(path => path.TargetInfo.TargetAvailable != 0 &&
            string.Equals(DisplayConfig.Key(path.TargetInfo), id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => (path.Flags & DisplayConfig.PathActive) != 0).ToArray();
        if (targets.Length > 0)
        {
            // SDC_VALIDATE only: checks the exact bootstrap without applying it.
            DisplayConfig.ValidateTopology([DisplayConfig.PrepareSingleTarget(targets[0])], []);
            Console.WriteLine("TV-only bootstrap: SDC_VALIDATE accepted (not applied).");
        }
        else Console.WriteLine("TV-only bootstrap validation skipped: target unavailable.");
        return 0;
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Throws(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected a rejected video setting.");
    }
}
