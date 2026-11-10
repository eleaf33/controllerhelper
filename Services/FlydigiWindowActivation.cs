namespace ControllerHelper.Services;

internal static class FlydigiWindowActivation
{
    // Ask the application's own single-instance handler to show its UI. Showing
    // a hidden HWND ourselves bypasses Electron's BrowserWindow/renderer state.
    internal static async Task<FlydigiWindowCandidate> OpenAsync(Action activate,
        Func<FlydigiWindowCandidate?> findVisibleWindow, CancellationToken cancellation = default,
        int attempts = 80, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        cancellation.ThrowIfCancellationRequested();
        activate();
        delay ??= Task.Delay;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            var candidate = findVisibleWindow();
            if (candidate is { Visible: true } && FlydigiWindowSelection.RejectionReason(candidate) is null)
                return candidate;
            await delay(TimeSpan.FromMilliseconds(250), cancellation);
        }
        throw new InvalidOperationException("Flydigi не показал главное окно после штатного запуска. " +
            "Проверьте, открывается ли клиент его обычным ярлыком. Подробности в logs.");
    }
}
