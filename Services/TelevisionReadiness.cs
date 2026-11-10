namespace ControllerHelper.Services;

// Runs on the display worker, before applying a topology. Never retries an applied topology.
internal sealed class TelevisionReadiness(TimeProvider? clock = null,
    Action<TimeSpan, CancellationToken>? delay = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StablePeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    internal void WaitUntilReady(Func<bool> isAvailable, CancellationToken cancellation)
    {
        long started = _clock.GetTimestamp();
        long? availableSince = null;
        bool? lastAvailable = null;
        int polls = 0;
        ErrorLog.Event("television.hdmi.wait.begin", new { timeoutSeconds = 30, stableSeconds = 1 });
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            bool available = isAvailable();
            polls++;
            cancellation.ThrowIfCancellationRequested();
            long now = _clock.GetTimestamp();
            TimeSpan elapsed = _clock.GetElapsedTime(started, now);
            if (available != lastAvailable || polls % 20 == 0)
                ErrorLog.Event("television.hdmi.wait.poll", new { available, polls, elapsedMs = elapsed.TotalMilliseconds });
            lastAvailable = available;
            if (elapsed >= Timeout)
            {
                ErrorLog.Event("television.hdmi.wait.timeout", new { polls, elapsedMs = elapsed.TotalMilliseconds });
                throw new TimeoutException(ControllerHelper.Localization.Text.Get("TelevisionReadiness_189"));
            }
            if (available)
            {
                availableSince ??= now;
                if (_clock.GetElapsedTime(availableSince.Value, now) >= StablePeriod)
                {
                    ErrorLog.Event("television.hdmi.wait.ok", new { polls, elapsedMs = elapsed.TotalMilliseconds });
                    return;
                }
            }
            else availableSince = null;

            TimeSpan remaining = Timeout - elapsed;
            TimeSpan pause = remaining < PollInterval ? remaining : PollInterval;
            if (delay is not null) delay(pause, cancellation);
            else if (cancellation.WaitHandle.WaitOne(pause)) cancellation.ThrowIfCancellationRequested();
        }
    }
}
