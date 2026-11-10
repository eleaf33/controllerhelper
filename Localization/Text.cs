using System.Globalization;
using System.Text.Json;

namespace ControllerHelper.Localization;

// UI language is independent of protocol formatting and Windows regional settings.
public static class Text
{
    private static readonly IReadOnlyDictionary<string, string> English = Load("en");
    private static readonly IReadOnlyDictionary<string, string> Russian = Load("ru");
    private static string _language = "en";
    public static string Language => Volatile.Read(ref _language);
    public static string LanguageTag => Language == "ru" ? "ru-RU" : "en-US";
    public static string NormalizeLanguage(string? language) =>
        language is not null && (language.Equals("ru", StringComparison.OrdinalIgnoreCase) ||
        language.StartsWith("ru-", StringComparison.OrdinalIgnoreCase)) ? "ru" : "en";

    public static void SetLanguage(string? language)
    {
        Volatile.Write(ref _language, NormalizeLanguage(language));
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(LanguageTag);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture;
    }

    public static string Get(string key)
    {
        if (Language == "ru" && Russian.TryGetValue(key, out string? russian)) return russian;
        if (English.TryGetValue(key, out string? english)) return english;
        throw new KeyNotFoundException("Missing UI string: " + key);
    }

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.GetCultureInfo(LanguageTag), Get(key), arguments);

    // Read only the language before loading settings so recovery/IPC messages use it too.
    // No tokens are decrypted, settings are not rewritten, and invalid files are ignored.
    public static string ReadLanguagePreference(string settingsPath)
    {
        foreach (string path in new[] { settingsPath, settingsPath + ".bak" })
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) continue;
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                return document.RootElement.TryGetProperty("Language", out var language) && language.ValueKind == JsonValueKind.String
                    ? NormalizeLanguage(language.GetString()) : "en";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return "en";
    }

    internal static IReadOnlyDictionary<string, string> Catalog(string language) => language == "ru" ? Russian : English;
    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        using Stream stream = typeof(Text).Assembly.GetManifestResourceStream("ControllerHelper.Localization." + language + ".json")
            ?? throw new InvalidOperationException("Missing localization resource: " + language);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("Invalid localization resource: " + language);
    }
}
