using System.Text.Json;

namespace ControllerHelper.Services;

// One attempt survives async continuations and subsequent dispatcher/watch callbacks.
// Callers provide only diagnostic metadata, never paths, window titles or key input.
internal sealed class FlydigiDiagnostics(Func<long>? milliseconds = null)
{
    private readonly Func<long> _milliseconds = milliseconds ?? (() => Environment.TickCount64);
    private readonly Dictionary<string, (string Value, long At)> _last = [];
    private long _started;
    internal string? AttemptId { get; private set; }

    internal void Begin()
    {
        AttemptId = Guid.NewGuid().ToString("N");
        _started = _milliseconds();
        _last.Clear();
        Event("attempt.begin");
    }

    internal void Event(string phase, object? data = null)
    {
        if (AttemptId is null) return;
        ErrorLog.Event("flydigi." + phase, new { attemptId = AttemptId, elapsedMs = Math.Max(0, _milliseconds() - _started), data });
    }

    internal void Changed(string phase, object data, int repeatAfterMs = 0)
    {
        if (AttemptId is null) return;
        string value = JsonSerializer.Serialize(data);
        long now = _milliseconds();
        if (_last.TryGetValue(phase, out var old) && old.Value == value &&
            (repeatAfterMs <= 0 || now - old.At < repeatAfterMs)) return;
        _last[phase] = (value, now);
        Event(phase, data);
    }
}
