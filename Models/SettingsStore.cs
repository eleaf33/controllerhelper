using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ControllerHelper.Services;

namespace ControllerHelper.Models;

public static class SettingsStore
{
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly SettingsFileStore Store = new(Path.Combine(Local, "ControllerHelper"),
        Path.Combine(Local, "BackToPC", "settings.json"));
    public static string? LoadWarning => Store.LoadWarning;
    public static AppSettings Load() => Store.Load();
    public static void Save(AppSettings settings) => Store.Save(settings);
    internal static string Serialize(AppSettings settings) => SettingsFileStore.Serialize(settings);
    internal static AppSettings? Deserialize(string json, out bool plaintextKey) =>
        SettingsFileStore.Deserialize(json, out plaintextKey, out _);
}

// An instance owns its paths and lock. Tests use a temporary directory and never
// redirect or change the application's actual settings or Windows profile.
internal sealed class SettingsFileStore(string directoryPath, string? legacyFilePath = null)
{
    private readonly object _sync = new();
    private readonly string _filePath = Path.Combine(directoryPath, "settings.json");
    private string BackupPath => _filePath + ".bak";
    internal string? LoadWarning { get; private set; }

    internal AppSettings Load()
    {
        lock (_sync)
        {
            LoadWarning = null;
            if (TryRead(_filePath, out var current, out bool plain, out string? warning))
            {
                LoadWarning = warning;
                if (plain) Migrate(current);
                return current;
            }
            bool mainExists = File.Exists(_filePath);
            if (TryRead(BackupPath, out var backup, out plain, out warning))
            {
                LoadWarning = (mainExists ? ControllerHelper.Localization.Text.Get("SettingsStore_006")
                    : ControllerHelper.Localization.Text.Get("SettingsStore_007")) + ControllerHelper.Localization.Text.Get("SettingsStore_008");
                AppendWarning(warning);
                if (plain) Migrate(backup);
                return backup;
            }
            if (mainExists || File.Exists(BackupPath))
            {
                LoadWarning = ControllerHelper.Localization.Text.Get("SettingsStore_009");
                return new();
            }
            if (legacyFilePath is null || !TryRead(legacyFilePath, out var legacy, out plain, out warning)) return new();
            LoadWarning = warning;
            Migrate(legacy);
            if (plain) AppendWarning(ControllerHelper.Localization.Text.Get("SettingsStore_010"));
            return legacy;
        }
    }

    internal void Save(AppSettings settings)
    {
        lock (_sync)
        {
            // Serialize/protect before touching the old files. A failure leaves
            // both recovery sources intact, and no plaintext temp file is made.
            string json = Serialize(settings);
            Directory.CreateDirectory(directoryPath);
            bool mainValid = TryRead(_filePath, out var previous, out bool mainPlain, out _);
            if (mainValid && !mainPlain)
                AtomicWrite(_filePath, json, BackupPath);
            else
            {
                // Never rotate a corrupt main file over the last good backup.
                // Protect legacy backups BEFORE committing the new main file,
                // so a cleanup failure cannot report failure after a commit.
                if (mainValid) AtomicWrite(BackupPath, Serialize(previous));
                else if (TryRead(BackupPath, out var backup, out bool backupPlain, out _) && backupPlain)
                    AtomicWrite(BackupPath, Serialize(backup));
                AtomicWrite(_filePath, json);
            }
            LoadWarning = null;
        }
    }

    private static void AtomicWrite(string path, string json, string? backupPath = null)
    {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using (FileStream file = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            if (backupPath is not null) File.Replace(temporaryPath, path, backupPath);
            else File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static string Serialize(AppSettings settings)
    {
        AppSettings saved = settings.Copy();
        // A null decrypted key may mean DPAPI failed. Preserve its ciphertext;
        // changing TV/trust explicitly clears both fields in WebOsSecurity.
        if (saved.TelevisionClientKey is not null)
            saved.TelevisionClientKeyProtected = saved.TelevisionClientKey.Length == 0
                ? null : WebOsKeyProtector.Protect(saved.TelevisionClientKey);
        if (saved.HisenseAuthentication is not null)
            saved.HisenseAuthenticationProtected = WebOsKeyProtector.Protect(JsonSerializer.Serialize(saved.HisenseAuthentication));
        return JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true });
    }

    private void Migrate(AppSettings settings)
    {
        string? priorWarning = LoadWarning;
        try { Save(settings); LoadWarning = priorWarning; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            LoadWarning = priorWarning;
            AppendWarning(ControllerHelper.Localization.Text.Get("SettingsStore_011"));
        }
    }

    private void AppendWarning(string? warning)
    {
        if (warning is not null) LoadWarning = LoadWarning is null ? warning : LoadWarning + " " + warning;
    }

    private static bool TryRead(string path, out AppSettings settings, out bool plaintextKey, out string? warning)
    {
        settings = new(); plaintextKey = false; warning = null;
        try
        {
            AppSettings? loaded = Deserialize(File.ReadAllText(path), out plaintextKey, out warning);
            if (loaded is null) return false;
            settings = loaded;
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
    }

    internal static AppSettings? Deserialize(string json, out bool plaintextKey, out string? warning)
    {
        plaintextKey = false; warning = null;
        AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json);
        if (loaded is null || loaded.MonitorIds is null) return null;
        loaded.Language = ControllerHelper.Localization.Text.NormalizeLanguage(loaded.Language);
        using JsonDocument document = JsonDocument.Parse(json);
        if (!string.IsNullOrEmpty(loaded.TelevisionClientKeyProtected))
        {
            try { loaded.TelevisionClientKey = WebOsKeyProtector.Unprotect(loaded.TelevisionClientKeyProtected); }
            catch (Exception error) when (error is Win32Exception or FormatException)
            { warning = ControllerHelper.Localization.Text.Get("SettingsStore_012"); }
        }
        else if (document.RootElement.TryGetProperty("TelevisionClientKey", out JsonElement oldKey) &&
            oldKey.ValueKind == JsonValueKind.String)
        {
            loaded.TelevisionClientKey = oldKey.GetString();
            plaintextKey = !string.IsNullOrEmpty(loaded.TelevisionClientKey);
        }
        if (!string.IsNullOrEmpty(loaded.HisenseAuthenticationProtected))
        {
            try
            {
                var auth = JsonSerializer.Deserialize<HisenseAuthentication>(WebOsKeyProtector.Unprotect(loaded.HisenseAuthenticationProtected));
                if (auth is null || !auth.IsValid()) throw new FormatException();
                loaded.HisenseAuthentication = auth;
            }
            catch (Exception error) when (error is Win32Exception or FormatException or JsonException)
            {
                loaded.HisensePaired = false;
                warning = (warning is null ? "" : warning + " ") + ControllerHelper.Localization.Text.Get("SettingsStore_013");
            }
        }
        return loaded;
    }
}
