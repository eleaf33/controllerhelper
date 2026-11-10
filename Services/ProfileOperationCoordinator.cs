namespace ControllerHelper.Services;

// UI-thread coordinator: every entry point (window, overlay, tray and IPC) uses it.
internal sealed class ProfileOperationCoordinator
{
    private Task? _active;
    private CancellationTokenSource? _cancellation;
    private Task? _recovery;
    private CancellationTokenSource? _recoveryCancellation;
    internal bool IsRunning => _active is not null || _recovery is not null;
    internal bool IsRecovering => _recovery is not null;
    internal bool IsStopping { get; private set; }

    internal Task RunAsync(Func<CancellationToken, Task> operation) =>
        RunAsync(async cancellation => { await operation(cancellation); return true; });

    internal Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation)
    {
        if (IsStopping) throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("ProfileOperationCoordinator_165"));
        if (IsRunning) throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("ProfileOperationCoordinator_166"));
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource cancellation = new();
        CancellationToken token = cancellation.Token;
        _cancellation = cancellation;
        _active = completion.Task;
        _ = ExecuteAsync();
        return completion.Task;

        async Task ExecuteAsync()
        {
            T result = default!;
            Exception? failure = null;
            try { result = await operation(token); }
            catch (Exception error) { failure = error; }
            finally
            {
                _active = null;
                _cancellation = null;
                cancellation.Dispose();
            }
            if (failure is OperationCanceledException canceled && canceled.CancellationToken == token)
                completion.TrySetCanceled(token);
            else if (failure is not null) completion.TrySetException(failure);
            else completion.TrySetResult(result);
        }
    }

    internal Task RecoverAsync(Func<CancellationToken, Task> operation)
    {
        if (IsStopping) throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("ProfileOperationCoordinator_165"));
        if (_recovery is not null) return _recovery;
        Task? prior = _active;
        CancellationTokenSource? priorCancellation = _cancellation;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource cancellation = new();
        CancellationToken token = cancellation.Token;
        _recoveryCancellation = cancellation;
        _recovery = completion.Task;
        _ = ExecuteAsync();
        return completion.Task;

        async Task ExecuteAsync()
        {
            Exception? failure = null;
            try
            {
                priorCancellation?.Cancel();
                Exception? priorFailure = null;
                if (prior is not null)
                {
                    try { await prior; }
                    catch (OperationCanceledException) when (prior.IsCanceled) { }
                    catch (Exception error)
                    {
                        priorFailure = error;
                        ErrorLog.Write("recovery.previous-operation", error);
                    }
                }
                // If exit canceled the queued rescue, no operation will repair a
                // failed rollback. Preserve that failure so StopAsync refuses exit.
                if (token.IsCancellationRequested && priorFailure is not null)
                    throw new InvalidOperationException(
                        ControllerHelper.Localization.Text.Get("ProfileOperationCoordinator_167"),
                        priorFailure);
                token.ThrowIfCancellationRequested();
                await operation(token);
            }
            catch (Exception error) { failure = error; }
            finally { _recovery = null; _recoveryCancellation = null; cancellation.Dispose(); }
            if (failure is OperationCanceledException canceled && canceled.CancellationToken == token)
                completion.TrySetCanceled(token);
            else if (failure is not null) completion.TrySetException(failure);
            else completion.TrySetResult();
        }
    }

    internal async Task StopAsync()
    {
        IsStopping = true;
        Task? active = _recovery ?? _active;
        CancellationTokenSource? cancellation = _recoveryCancellation ?? _cancellation;
        try
        {
            cancellation?.Cancel();
            if (active is not null) await active;
        }
        catch (OperationCanceledException) when (active?.IsCanceled == true) { }
        catch
        {
            // A rollback failure keeps the application available for recovery.
            IsStopping = false;
            throw;
        }
    }
}
