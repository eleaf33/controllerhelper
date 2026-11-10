using System.ComponentModel;
using System.Text.Json;
using ControllerHelper.Services;

internal static class ErrorJournalTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Логи публикации находятся рядом с корневым EXE", () => Run(PublishedLocation)),
        ("Прямой запуск пишет логи рядом с приложением", () => Run(DirectLocation)),
        ("Этапы операции и ошибка связаны после перехода в рабочий поток", Correlation),
        ("Недоступный журнал не ломает успешный этап", UnwritableStep),
        ("Журнал сохраняет этап и native-код без сообщений с ключами", () => Run(NoSecrets)),
        ("Журнал ошибок ограничен двумя файлами", () => Run(Rotation)),
        ("Недоступный журнал не скрывает исходную ошибку", () => Run(Unwritable))
    ];
    private static string DirectoryForTest()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ControllerHelper.JournalTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }

    private static void PublishedLocation()
    {
        string root = DirectoryForTest();
        File.WriteAllText(Path.Combine(root, "ControllerHelper.exe"), "test marker");
        string resolved = LogLocation.Resolve(Path.Combine(root, "lib") + Path.DirectorySeparatorChar);
        Check(resolved == Path.Combine(root, "logs"), "Logs went into lib or current directory");
        new ErrorJournal(Path.Combine(resolved, "errors.jsonl")).Write("startup");
        Check(File.Exists(Path.Combine(root, "logs", "errors.jsonl")), "Logs were not created automatically");
    }

    private static void DirectLocation()
    {
        string root = DirectoryForTest();
        Check(LogLocation.Resolve(root) == Path.Combine(root, "logs"), "Direct launch logs misplaced");
        Check(LogLocation.Resolve(Path.Combine(root, "lib")) == Path.Combine(root, "lib", "logs"),
            "Unrelated directory named lib treated as publication");
    }

    private static async Task Correlation()
    {
        ErrorJournal previous = ErrorLog.Journal;
        string file = Path.Combine(DirectoryForTest(), "errors.jsonl");
        ErrorLog.Journal = new ErrorJournal(file);
        try
        {
            using (ErrorLog.BeginOperation("test.switch"))
            {
                await Task.Run(() => ErrorLog.Step("display.apply", () => { }));
                try { ErrorLog.Step("audio.apply", () => { throw new IOException("secret-key"); }); }
                catch (IOException) { }
            }
            ErrorLog.Event("outside");
            string[] lines = File.ReadAllLines(file);
            Check(lines.Length == 7 && !string.Join("", lines).Contains("secret-key"), "Wrong records or leaked message");
            string? correlation = null;
            for (int i = 0; i < lines.Length; i++)
            {
                using JsonDocument record = JsonDocument.Parse(lines[i]);
                string? id = record.RootElement.GetProperty("operationId").GetString();
                if (i == 0) correlation = id;
                if (i < 6) Check(id is not null && id == correlation, "Operation correlation lost");
                else Check(id is null, "Operation context leaked");
            }
        }
        finally { ErrorLog.Journal = previous; }
    }

    private static Task UnwritableStep()
    {
        ErrorJournal previous = ErrorLog.Journal;
        string file = Path.Combine(DirectoryForTest(), "blocked"); File.WriteAllText(file, "blocked");
        ErrorLog.Journal = new ErrorJournal(Path.Combine(file, "errors.jsonl"));
        try { Check(ErrorLog.Step("test", () => 42) == 42, "Logging changed operation result"); }
        finally { ErrorLog.Journal = previous; }
        return Task.CompletedTask;
    }
    private static void NoSecrets()
    {
        string file = Path.Combine(DirectoryForTest(), "errors.jsonl");
        new ErrorJournal(file).Write("display.apply", new InvalidOperationException("secret-client-key",
            new Win32Exception(87, "remote-secret-response")));
        string text = File.ReadAllText(file);
        Check(!text.Contains("secret") && !text.Contains("response"), "Sensitive exception message leaked.");
        using JsonDocument record = JsonDocument.Parse(text);
        Check(record.RootElement.GetProperty("operation").GetString() == "display.apply" &&
            record.RootElement.GetProperty("exceptions")[1].GetProperty("NativeCode").GetInt32() == 87, "Useful diagnostic details lost.");
    }
    private static void Rotation()
    {
        string directory = DirectoryForTest(); string file = Path.Combine(directory, "errors.jsonl");
        ErrorJournal log = new(file, 1500);
        for (int index = 0; index < 50; index++) log.Write("display.apply", new IOException("ignore"));
        Check(File.Exists(file + ".1") && Directory.GetFiles(directory).Length == 2, "Rotation is unbounded.");
        foreach (string path in Directory.GetFiles(directory))
        {
            Check(new FileInfo(path).Length <= 1500, "Journal size exceeded.");
            foreach (string line in File.ReadLines(path)) using (JsonDocument.Parse(line)) { }
        }
    }
    private static void Unwritable()
    {
        string file = Path.Combine(DirectoryForTest(), "blocked"); File.WriteAllText(file, "file-not-directory");
        new ErrorJournal(Path.Combine(file, "errors.jsonl")).Write("display.apply", new IOException("original"));
    }
    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
