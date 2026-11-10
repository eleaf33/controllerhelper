using System.Text.Json;
using ControllerHelper.Services;

internal static class FlydigiDiagnosticsTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Flydigi: неизменные таймерные снимки не заполняют журнал", Deduplication),
        ("Flydigi: попытка связана с поздним callback и следующая получает новый ID", AttemptCorrelation),
        ("Flydigi: недоступный журнал не прерывает диагностику открытия", Unwritable)
    ];

    private static Task InJournal(Action<string> test)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ControllerHelper.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "errors.jsonl");
        ErrorJournal previous = ErrorLog.Journal;
        ErrorLog.Journal = new(path);
        try { test(path); }
        finally { ErrorLog.Journal = previous; if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        return Task.CompletedTask;
    }

    private static Task Deduplication() => InJournal(path =>
    {
        long clock = 0;
        FlydigiDiagnostics log = new(() => clock);
        log.Begin();
        for (int i = 0; i < 1000; i++) log.Changed("fit.result", new { fits = true });
        log.Changed("fit.result", new { fits = false });
        log.Changed("search.snapshot", new { processCount = 1 }, 2000);
        for (int i = 0; i < 8; i++)
        {
            clock = i * 250;
            log.Changed("search.snapshot", new { processCount = 1 }, 2000);
        }
        clock = 2000;
        log.Changed("search.snapshot", new { processCount = 1 }, 2000);
        if (File.ReadAllLines(path).Length != 5) throw new Exception("Unchanged frames flood logs, changed failure or wait progress lost");
    });

    private static Task AttemptCorrelation() => InJournal(path =>
    {
        long clock = 10;
        FlydigiDiagnostics log = new(() => clock);
        using (ErrorLog.BeginOperation("test-open"))
        {
            log.Begin();
            log.Event("window.ready");
        }
        string? first = log.AttemptId;
        clock = 110;
        log.Event("watch.window-lost"); // A timer is outside the original async scope.
        log.Begin();
        log.Event("window.ready");
        var rows = File.ReadAllLines(path).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var callback = rows.Single(row => row.RootElement.GetProperty("operation").GetString() == "flydigi.watch.window-lost").RootElement;
            if (callback.GetProperty("operationId").ValueKind != JsonValueKind.Null ||
                callback.GetProperty("details").GetProperty("attemptId").GetString() != first ||
                callback.GetProperty("details").GetProperty("elapsedMs").GetInt64() != 100 || log.AttemptId == first)
                throw new Exception("Attempt association lost across timer/new attempt");
        }
        finally { foreach (var row in rows) row.Dispose(); }
    });

    private static Task Unwritable() => InJournal(path =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "blocks-directory");
        ErrorLog.Journal = new(Path.Combine(path, "errors.jsonl"));
        FlydigiDiagnostics log = new();
        log.Begin(); log.Event("window.ready"); log.Changed("fit.result", new { fits = true });
        if (log.AttemptId is null) throw new Exception("Unavailable log aborted the operation");
    });
}
