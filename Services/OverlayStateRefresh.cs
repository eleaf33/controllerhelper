namespace ControllerHelper.Services;

// Used on the UI thread. Local actions are published synchronously; network results never steal focus.
internal sealed class OverlayStateRefresh(
    Func<TelevisionOverlayState> readLocal,
    Func<CancellationToken, Task<TelevisionOverlayState>> readNetwork,
    Action<TelevisionOverlayState, bool> present,
    Action<Exception> networkError) : IDisposable
{
    private CancellationTokenSource? _pending;
    private long _generation;
    private bool _disposed;
    internal Task Completion { get; private set; } = Task.CompletedTask;

    internal void Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Cancel();
        TelevisionOverlayState local = readLocal();
        present(local, true);
        if (!local.PowerCheckPending) return;
        CancellationTokenSource pending = new();
        _pending = pending;
        long generation = _generation;
        Completion = UpdateAsync();

        async Task UpdateAsync()
        {
            try
            {
                TelevisionOverlayState state = await readNetwork(pending.Token);
                if (!_disposed && !pending.IsCancellationRequested && generation == _generation)
                    present(state, false);
            }
            catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!_disposed && generation == _generation) networkError(error);
            }
            finally
            {
                if (ReferenceEquals(_pending, pending)) _pending = null;
                pending.Dispose();
            }
        }
    }

    internal void Cancel()
    {
        _generation++;
        CancellationTokenSource? pending = _pending;
        _pending = null;
        pending?.Cancel(); // The awaiting task owns disposal.
    }

    public void Dispose() { _disposed = true; Cancel(); }
}
