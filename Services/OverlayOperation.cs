namespace ControllerHelper.Services;

internal static class OverlayOperation
{
    internal static async Task<T> RunAsync<T>(Func<Task<T>> operation, Func<bool> suppressError,
        Action<Exception> reportError)
    {
        try { return await operation(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            // The operation may already have closed its overlay in finally.
            if (!suppressError()) reportError(error);
            throw;
        }
    }
}
