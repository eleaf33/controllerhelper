using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ControllerHelper.Localization;
using ControllerHelper.Models;
using ControllerHelper.Services;

internal static class LocalizationTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Localization: both catalogs are complete and format arguments agree", Catalogs),
        ("Localization: English default and Russian settings round-trip", Settings),
        ("Localization: selected language is read before settings recovery", Preference),
        ("Localization: overlay, audio and controller labels use the selected language", UserMessages),
        ("Localization: formatting follows UI language without changing protocol culture", Formatting),
        ("Localization: all source and XAML resource references resolve", SourceReferences)
    ];

    private static Task Catalogs()
    {
        var english = Text.Catalog("en"); var russian = Text.Catalog("ru");
        Check(english.Count >= 430 && english.Keys.Order().SequenceEqual(russian.Keys.Order()), "Catalog keys differ");
        foreach ((string key, string value) in english)
        {
            Check(!string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(russian[key]), "Empty translation: " + key);
            Check(key == "Language_Title" || !Regex.IsMatch(value, "[А-Яа-яЁё]"), "Russian text in English catalog: " + key);
            Check(Placeholders(value).SequenceEqual(Placeholders(russian[key])), "Format arguments differ: " + key);
            if (Regex.IsMatch(value, @"\{\d"))
            {
                var enFormat = CompositeFormat.Parse(value); var ruFormat = CompositeFormat.Parse(russian[key]);
                Check(enFormat.MinimumArgumentCount == ruFormat.MinimumArgumentCount, "Argument count differs: " + key);
                object[] arguments = Enumerable.Range(0, enFormat.MinimumArgumentCount).Select(_ => (object)new FormatProbe()).ToArray();
                _ = string.Format(CultureInfo.InvariantCulture, enFormat, arguments);
                _ = string.Format(CultureInfo.InvariantCulture, ruFormat, arguments);
            }
        }
        return Task.CompletedTask;
    }
    private sealed class FormatProbe : IFormattable
    {
        public string ToString(string? format, IFormatProvider? provider) => "value";
        public override string ToString() => "value";
    }
    private static IEnumerable<string> Placeholders(string text) => Regex.Matches(text, @"\{\d+(?:,[^}:]+)?(?::[^}]+)?\}").Select(m => m.Value).Order();

    private static Task Settings()
    {
        Check(new AppSettings().Language == "en", "New settings must default to English");
        Check(SettingsStore.Deserialize("{}", out _)!.Language == "en", "Legacy settings must default to English");
        foreach (string input in new[] { "ru", "en", "ru-RU", "RU", "unknown" })
        {
            AppSettings settings = new() { Language = input, PcAudioId = "unchanged-device" };
            var loaded = SettingsStore.Deserialize(SettingsStore.Serialize(settings), out _)!;
            Check(loaded.Language == Text.NormalizeLanguage(input) && loaded.PcAudioId == settings.PcAudioId, "Language round-trip failed");
            Check(settings.Copy().Language == input, "Copy dropped language");
        }
        Check(SettingsStore.Deserialize("{\"Language\":null}", out _)!.Language == "en", "Null language must fall back to English");
        return Task.CompletedTask;
    }
    private static Task Preference()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ControllerHelper.LanguageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); string file = Path.Combine(folder, "settings.json");
        try
        {
            Check(Text.ReadLanguagePreference(file) == "en", "Missing settings language");
            File.WriteAllText(file, "{\"Language\":\"ru\",\"HisenseAuthenticationProtected\":\"unreadable\"}");
            string before = File.ReadAllText(file);
            Check(Text.ReadLanguagePreference(file) == "ru" && File.ReadAllText(file) == before, "Preference read modified settings");
            File.Copy(file, file + ".bak"); File.WriteAllText(file, "{broken");
            Check(Text.ReadLanguagePreference(file) == "ru", "Backup language lost");
            File.WriteAllText(file, "{}"); Check(Text.ReadLanguagePreference(file) == "en", "Valid main must take precedence over backup");
        }
        finally { Directory.Delete(folder, true); }
        return Task.CompletedTask;
    }
    private static Task UserMessages()
    {
        string previous = Text.Language;
        try
        {
            Text.SetLanguage("en");
            Check(Text.Get("TrayIcon_022") == "Play on TV", "English action missing");
            Check(new AudioEndpoint("id", "Device", 8).ToString().Contains("not connected"), "English endpoint label missing");
            Check(new ControllerButtonCatalog().Choices.Any(c => c.Label == "Left stick"), "English controller label missing");
            Text.SetLanguage("ru");
            Check(Text.Get("TrayIcon_022") == "Играть на ТВ", "Russian action missing");
            Check(new AudioEndpoint("id", "Device", 8).ToString().Contains("не подключено"), "Russian endpoint label missing");
        }
        finally { Text.SetLanguage(previous); }
        return Task.CompletedTask;
    }
    private static Task Formatting()
    {
        string previous = Text.Language; var culture = CultureInfo.CurrentCulture;
        try
        {
            Text.SetLanguage("en");
            Check(ReferenceEquals(CultureInfo.CurrentCulture, culture), "Language changed protocol culture");
            Check(Text.Format("AudioEndpoint_036", "Device", 255) == "Device (unknown state: 0xFF)", "Hex placeholder changed");
            string english = Text.Format("SettingsWindow_xaml_342", "address", "fingerprint", new DateTime(2026, 11, 13));
            Text.SetLanguage("ru");
            string russian = Text.Format("SettingsWindow_xaml_342", "address", "fingerprint", new DateTime(2026, 11, 13));
            Check(english.Contains("11/13/2026") && russian.Contains("13.11.2026"), "Dates did not follow UI language");
            Check(ReferenceEquals(CultureInfo.CurrentCulture, culture), "Language changed regional formatting");
        }
        finally { Text.SetLanguage(previous); }
        return Task.CompletedTask;
    }
    private static Task SourceReferences()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "ControllerHelper.csproj"))) folder = folder.Parent;
        Check(folder is not null, "Source tree not found");
        string root = folder!.FullName;
        var catalog = Text.Catalog("en");
        var files = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories).Where(file =>
            (file.EndsWith(".cs") || file.EndsWith(".xaml")) &&
            !Regex.IsMatch(Path.GetRelativePath(root, file).Replace('\\', '/'), @"^(\.work|\.dotnet-cli|Tests|publish[^/]*|obj|bin)/"));
        int count = 0;
        foreach (string file in files)
        {
            string source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source, "Text\\.(?:Get|Format)\\(\"([^\"]+)\"|\\{loc:Localize Key=([^}]+)\\}"))
            {
                string key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                Check(catalog.ContainsKey(key), "Unknown resource in " + Path.GetFileName(file) + ": " + key); count++;
            }
        }
        Check(count > 400, "Source resource scan did not run");
        return Task.CompletedTask;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
