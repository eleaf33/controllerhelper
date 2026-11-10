using ControllerHelper.Models;
using System.Text.Json;

internal static class SettingsFileTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Первое сохранение настроек и ротация резервной копии", Rotation),
        ("Повреждённые настройки восстанавливаются из копии без её порчи", CorruptMain),
        ("Отсутствующий основной файл восстанавливается из копии", MissingMain),
        ("Повреждение обоих файлов даёт предупреждение и значения по умолчанию", BothCorrupt),
        ("Заблокированный файл не теряет настройки и не оставляет временный файл", LockedMain),
        ("Заблокированная резервная копия не допускает частичного сохранения", LockedBackup),
        ("Повреждённая единственная копия не заменяется молча старым каталогом", CorruptOrphan),
        ("Ошибка сериализации не затрагивает предыдущие настройки", SerializationFailure),
        ("Недоступная папка не считается успешным сохранением", InvalidDirectory),
        ("Миграция ключа защищает основной файл и резервную копию", PlaintextMigration),
        ("Миграция резервной копии сохраняет предупреждение о восстановлении", BackupMigration),
        ("Недоступный DPAPI-ключ переживает сохранение остальных настроек", UnavailableKey),
        ("Параллельные сохранения не повреждают основной файл и копию", ConcurrentSaves),
        ("Старый каталог переносится только при отсутствии текущих настроек", Legacy)
    ];

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "ControllerHelper.SettingsTests", Guid.NewGuid().ToString("N"));
        internal readonly SettingsFileStore Store;
        internal string Main => Path.Combine(Root, "settings.json");
        internal string Backup => Main + ".bak";
        internal Fixture() { Directory.CreateDirectory(Root); Store = new(Root); }
        internal void Seed() { Store.Save(Value("first")); Store.Save(Value("second")); }
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static AppSettings Value(string marker) => new() { PcAudioId = marker };
    private static AppSettings Read(string path) => SettingsStore.Deserialize(File.ReadAllText(path), out _)!;
    private static Task Rotation()
    {
        using Fixture f = new(); f.Seed();
        Check(f.Store.Load().PcAudioId == "second" && Read(f.Backup).PcAudioId == "first", "Rotation lost previous values");
        Check(Directory.GetFiles(f.Root, "*.tmp").Length == 0, "Temp file left behind");
        return Task.CompletedTask;
    }
    private static Task CorruptMain()
    {
        using Fixture f = new(); f.Seed(); File.WriteAllText(f.Main, "{broken");
        Check(f.Store.Load().PcAudioId == "first" && f.Store.LoadWarning is not null, "Backup recovery failed");
        f.Store.Save(Value("third"));
        Check(Read(f.Main).PcAudioId == "third" && Read(f.Backup).PcAudioId == "first", "Corrupt file replaced good backup");
        return Task.CompletedTask;
    }
    private static Task MissingMain()
    {
        using Fixture f = new(); f.Seed(); File.Delete(f.Main);
        Check(f.Store.Load().PcAudioId == "first" && f.Store.LoadWarning is not null, "Orphaned backup ignored");
        return Task.CompletedTask;
    }
    private static Task BothCorrupt()
    {
        using Fixture f = new(); File.WriteAllText(f.Main, "null"); File.WriteAllText(f.Backup, "{\"MonitorIds\":null}");
        Check(f.Store.Load().PcAudioId is null && f.Store.LoadWarning is not null, "Invalid data accepted");
        return Task.CompletedTask;
    }
    private static Task LockedMain()
    {
        using Fixture f = new(); f.Seed();
        string main = File.ReadAllText(f.Main), backup = File.ReadAllText(f.Backup);
        using (FileStream locked = new(f.Main, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            bool rejected = false;
            try { f.Store.Save(Value("new")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
            Check(rejected, "A locked file was overwritten");
        }
        Check(File.ReadAllText(f.Main) == main && File.ReadAllText(f.Backup) == backup, "Failed save changed recovery data");
        Check(Directory.GetFiles(f.Root, "*.tmp").Length == 0, "Failed save left temp files");
        return Task.CompletedTask;
    }
    private static Task SerializationFailure()
    {
        using Fixture f = new(); f.Seed(); string before = File.ReadAllText(f.Main);
        Throws<ArgumentException>(() => f.Store.Save(new() { FlydigiCursorSpeed = double.NaN }));
        Check(File.ReadAllText(f.Main) == before && Directory.GetFiles(f.Root, "*.tmp").Length == 0, "Serialization touched files");
        return Task.CompletedTask;
    }
    private static Task LockedBackup()
    {
        using Fixture f = new(); f.Seed();
        string main = File.ReadAllText(f.Main), backup = File.ReadAllText(f.Backup);
        using (FileStream locked = new(f.Backup, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            bool rejected = false;
            try { f.Store.Save(Value("new")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
            Check(rejected, "A locked backup was overwritten");
        }
        Check(File.ReadAllText(f.Main) == main && File.ReadAllText(f.Backup) == backup, "Failed backup rotation partially committed");
        Check(Directory.GetFiles(f.Root, "*.tmp").Length == 0, "Failed rotation left temp files");
        return Task.CompletedTask;
    }
    private static Task CorruptOrphan()
    {
        using Fixture f = new(); File.WriteAllText(f.Backup, "broken");
        string legacy = Path.Combine(f.Root, "legacy.json"); File.WriteAllText(legacy, Plain);
        SettingsFileStore store = new(f.Root, legacy);
        Check(store.Load().PcAudioId is null && store.LoadWarning is not null, "Corrupt current backup silently fell back to stale legacy data");
        Check(!File.Exists(f.Main), "Loading corrupt settings created a replacement");
        return Task.CompletedTask;
    }
    private static Task InvalidDirectory()
    {
        using Fixture f = new(); string path = Path.Combine(f.Root, "file"); File.WriteAllText(path, "unchanged");
        Throws<IOException>(() => new SettingsFileStore(path).Save(Value("new")));
        Check(File.ReadAllText(path) == "unchanged", "Directory failure changed file");
        return Task.CompletedTask;
    }
    private const string Plain = "{\"PcAudioId\":\"legacy\",\"TelevisionClientKey\":\"test-secret-key\"}";
    private static Task PlaintextMigration()
    {
        using Fixture f = new(); File.WriteAllText(f.Main, Plain);
        Check(f.Store.Load().TelevisionClientKey == "test-secret-key", "Migration lost key");
        foreach (string path in new[] { f.Main, f.Backup })
            Check(!File.ReadAllText(path).Contains("test-secret-key") && Read(path).TelevisionClientKey == "test-secret-key", "Plaintext persisted or key damaged");
        return Task.CompletedTask;
    }
    private static Task BackupMigration()
    {
        using Fixture f = new(); File.WriteAllText(f.Main, "{broken"); File.WriteAllText(f.Backup, Plain);
        Check(f.Store.Load().PcAudioId == "legacy" && f.Store.LoadWarning?.Contains("резервная") == true, "Migration erased recovery warning");
        Check(!File.ReadAllText(f.Backup).Contains("test-secret-key"), "Backup key stayed plaintext");
        return Task.CompletedTask;
    }
    private static Task UnavailableKey()
    {
        using Fixture f = new(); const string cipher = "invalid-base64!";
        File.WriteAllText(f.Main, JsonSerializer.Serialize(new AppSettings { TelevisionClientKeyProtected = cipher }));
        AppSettings loaded = f.Store.Load();
        Check(loaded.TelevisionClientKey is null && f.Store.LoadWarning is not null, "Failed DPAPI was not reported");
        loaded.PcAudioId = "new"; f.Store.Save(loaded);
        Check(Read(f.Main).TelevisionClientKeyProtected == cipher, "Unrelated save destroyed encrypted key");
        loaded.TelevisionClientKeyProtected = null; f.Store.Save(loaded);
        Check(Read(f.Main).TelevisionClientKeyProtected is null, "Explicit reset did not clear key");
        return Task.CompletedTask;
    }
    private static Task ConcurrentSaves()
    {
        using Fixture f = new();
        Parallel.For(0, 20, i => f.Store.Save(Value(i.ToString())));
        Check(Read(f.Main).PcAudioId is not null && Read(f.Backup).PcAudioId is not null, "Concurrent writes corrupted files");
        Check(Directory.GetFiles(f.Root, "*.tmp").Length == 0, "Concurrent writes left temp files");
        return Task.CompletedTask;
    }
    private static Task Legacy()
    {
        using Fixture f = new(); string legacy = Path.Combine(f.Root, "legacy.json"); File.WriteAllText(legacy, Plain);
        SettingsFileStore store = new(f.Root, legacy);
        Check(store.Load().PcAudioId == "legacy" && store.LoadWarning is not null, "Legacy migration failed");
        store.Save(Value("current"));
        Check(store.Load().PcAudioId == "current", "Legacy replaced current settings");
        return Task.CompletedTask;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
}
