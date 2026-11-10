namespace ControllerHelper.Services;

internal static class PcRecoveryOperation
{
    internal static async Task<string?> RunAsync(Action restoreDisplays, Action<CancellationToken> restoreAudio,
        Action closeSteam, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        await Task.Run(() => ErrorLog.Step("recovery.displays", restoreDisplays));
        // Once monitors are restored, later failures must never undo them.
        List<string> warnings = [];
        cancellation.ThrowIfCancellationRequested();
        try { await Task.Run(() => ErrorLog.Step("recovery.audio", () => restoreAudio(cancellation))); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error) { ErrorLog.Write("recovery.audio", error); warnings.Add(ControllerHelper.Localization.Text.Format("PcRecoveryOperation_164", error.Message)); }
        cancellation.ThrowIfCancellationRequested();
        try { await Task.Run(closeSteam); }
        catch (Exception error) { ErrorLog.Write("recovery.steam", error); warnings.Add($"Steam: {error.Message}"); }
        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }
}
