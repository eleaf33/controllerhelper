namespace ControllerHelper.Services;

internal static class ProfileSwitchTransaction
{
    internal static async Task ApplyAsync<TDisplaySnapshot, TAudioSnapshot>(
        Func<TDisplaySnapshot> captureDisplay, Action changeDisplay, Action<TDisplaySnapshot> restoreDisplay,
        Func<TAudioSnapshot>? captureAudio = null, Action? changeAudio = null,
        Action<TAudioSnapshot>? restoreAudio = null, Func<Task<bool>>? confirmDisplay = null,
        Action? finish = null, CancellationToken cancellation = default)
    {
        if (changeAudio is not null && (captureAudio is null || restoreAudio is null))
            throw new ArgumentException(ControllerHelper.Localization.Text.Get("ProfileSwitchTransaction_171"));

        cancellation.ThrowIfCancellationRequested();
        TDisplaySnapshot displayBefore = await Task.Run(() => ErrorLog.Step("display.capture", captureDisplay));
        cancellation.ThrowIfCancellationRequested();
        TAudioSnapshot audioBefore = default!;
        if (changeAudio is not null) audioBefore = await Task.Run(() => ErrorLog.Step("audio.capture", captureAudio!));
        cancellation.ThrowIfCancellationRequested();
        string phase = "display.apply";
        try
        {
            await Task.Run(() => ErrorLog.Step("display.apply", changeDisplay));
            cancellation.ThrowIfCancellationRequested();
            phase = "audio.apply";
            if (changeAudio is not null) await Task.Run(() => ErrorLog.Step("audio.apply", changeAudio));
            cancellation.ThrowIfCancellationRequested();
            phase = "display.confirm";
            bool confirmed = confirmDisplay is null || await confirmDisplay();
            cancellation.ThrowIfCancellationRequested();
            if (!confirmed)
                throw new TimeoutException(ControllerHelper.Localization.Text.Get("ProfileSwitchTransaction_172"));
            phase = "profile.finish";
            if (finish is not null) ErrorLog.Step("profile.finish", finish);
        }
        catch (Exception original)
        {
            if (original is not OperationCanceledException) ErrorLog.Write(phase, original);
            List<Exception> rollbackErrors = [];
            ErrorLog.Event("profile.rollback.begin");
            try { await Task.Run(() => ErrorLog.Step("display.rollback", () => restoreDisplay(displayBefore))); }
            catch (Exception error) { ErrorLog.Write("display.rollback", error); rollbackErrors.Add(error); }
            if (changeAudio is not null)
            {
                try { await Task.Run(() => ErrorLog.Step("audio.rollback", () => restoreAudio!(audioBefore))); }
                catch (Exception error) { ErrorLog.Write("audio.rollback", error); rollbackErrors.Add(error); }
            }
            ErrorLog.Event("profile.rollback.end", new { failures = rollbackErrors.Count });
            if (rollbackErrors.Count == 0) throw;
            throw new InvalidOperationException(
                ControllerHelper.Localization.Text.Format("ProfileSwitchTransaction_173", original.Message, string.Join("; ", rollbackErrors.Select(error => error.Message))),
                new AggregateException([original, .. rollbackErrors]));
        }
    }
}
