namespace ControllerHelper.Services;

internal static class TelevisionReturnOperation
{
    internal static async Task<(string? PcWarning, string? PowerOffStatus)> RunAsync(
        Func<CancellationToken, Task<string?>> restorePc, Func<CancellationToken, Task<string>>? powerOff,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string? warning = await restorePc(cancellation);
        ErrorLog.Event("television.return.pc-ready", new { powerOffRequested = powerOff is not null });
        if (powerOff is null) return (warning, null);
        cancellation.ThrowIfCancellationRequested();
        try { return (warning, await powerOff(cancellation)); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            // Display/audio rollback has already ended. A TV network error must
            // never reverse a successful return to PC, or send a second toggle.
            ErrorLog.Write("television.return.power-off", error);
            throw new InvalidOperationException($"Мониторы включены, но телевизор не удалось выключить: {error.Message}", error);
        }
    }
}
