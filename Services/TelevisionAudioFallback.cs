namespace ControllerHelper.Services;

internal static class TelevisionAudioFallback
{
    internal static AudioDefaults? Capture(Func<AudioDefaults> capture)
    {
        try { return capture(); }
        catch (Exception error)
        {
            ErrorLog.Write("audio.tv.capture-unavailable", error);
            return null;
        }
    }

    internal static string? Apply(string tvId, string? pcId, AudioDefaults? before,
        Action<string, CancellationToken> set, Action<AudioDefaults> restore, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            ErrorLog.Step("audio.tv.primary", () => set(tvId, cancellation));
            return null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error) { ErrorLog.Write("audio.tv.primary-failed", error); }

        cancellation.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(pcId) && !string.Equals(tvId, pcId, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                ErrorLog.Step("audio.tv.pc-fallback", () => set(pcId, cancellation));
                return ControllerHelper.Localization.Text.Get("TelevisionAudioFallback_175");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception error) { ErrorLog.Write("audio.tv.pc-fallback-failed", error); }
        }

        cancellation.ThrowIfCancellationRequested();
        if (before is not null)
        {
            try
            {
                ErrorLog.Step("audio.tv.restore-previous", () => restore(before));
                return ControllerHelper.Localization.Text.Get("TelevisionAudioFallback_176");
            }
            catch (Exception error) { ErrorLog.Write("audio.tv.restore-previous-failed", error); }
        }
        return ControllerHelper.Localization.Text.Get("TelevisionAudioFallback_177");
    }
}
