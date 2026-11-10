using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ControllerHelper.Services;

internal static class ErrorLog
{
    private static readonly AsyncLocal<string?> CurrentOperation = new();
    internal static ErrorJournal Journal { get; set; } = new(Path.Combine(LogLocation.DirectoryPath, "errors.jsonl"), maximumBytes: 2 * 1024 * 1024);
    internal static void Write(string operation, Exception error) => Journal.Write(operation, error, operationId: CurrentOperation.Value);
    internal static void Event(string operation, object? details = null) =>
        Journal.Write(operation, details: details, operationId: CurrentOperation.Value);

    internal static IDisposable BeginOperation(string name)
    {
        string? previous = CurrentOperation.Value;
        CurrentOperation.Value = Guid.NewGuid().ToString("N");
        Event(name + ".begin");
        return new Scope(previous, name);
    }

    private sealed class Scope(string? previous, string name) : IDisposable
    {
        public void Dispose() { Event(name + ".end"); CurrentOperation.Value = previous; }
    }

    internal static T Step<T>(string phase, Func<T> action)
    {
        Event(phase + ".begin");
        long started = Stopwatch.GetTimestamp();
        try
        {
            T result = action();
            Event(phase + ".ok", new { elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            return result;
        }
        catch (OperationCanceledException) { Event(phase + ".canceled"); throw; }
        catch (Exception error) { Write(phase, error); throw; }
    }

    internal static void Step(string phase, Action action) => Step(phase, () => { action(); return true; });
}

// Messages, settings and remote responses are deliberately excluded: they can contain the pairing key.
internal sealed class ErrorJournal(string path, int maximumBytes = 256 * 1024)
{
    private readonly object _gate = new();

    internal void Write(string operation, Exception? error = null, object? details = null, string? operationId = null)
    {
        try
        {
            var exceptions = error is null ? [] : Describe(error, 0).ToArray();
            string line = JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.Now,
                process = Environment.ProcessId,
                version = typeof(ErrorLog).Assembly.GetName().Version?.ToString(),
                build = typeof(ErrorLog).Assembly.ManifestModule.ModuleVersionId,
                level = error is null ? "info" : "error",
                operationId,
                operation,
                details,
                exceptions
            }) + Environment.NewLine;
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            if (bytes.Length > maximumBytes) return;
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > maximumBytes)
                    File.Move(path, path + ".1", true);
                using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(bytes);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or NotSupportedException)
        { /* Diagnostics must not replace an operation or rollback error. */ }
    }

    private sealed record ErrorDetails(string Type, int HResult, int? NativeCode, string? NativeMessage, string[] Frames);

    private static IEnumerable<ErrorDetails> Describe(Exception error, int depth)
    {
        if (depth >= 4) yield break;
        int? native = error is Win32Exception win32 ? win32.NativeErrorCode : null;
        yield return new(error.GetType().FullName ?? error.GetType().Name, error.HResult, native,
            native is int code ? new Win32Exception(code).Message : null,
            new StackTrace(error, false).GetFrames().Take(12)
                .Select(frame => frame.GetMethod())
                .Select(method => $"{method?.DeclaringType?.FullName}.{method?.Name}").ToArray());
        IEnumerable<Exception> inner = error is AggregateException aggregate
            ? aggregate.InnerExceptions.Take(4) : error.InnerException is Exception cause ? [cause] : [];
        foreach (Exception nested in inner)
            foreach (ErrorDetails details in Describe(nested, depth + 1)) yield return details;
    }
}
