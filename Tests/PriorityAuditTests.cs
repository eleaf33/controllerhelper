using ControllerHelper.Services;

internal static class PriorityAuditTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("IPC разделён по пользователю и сеансу", PipeScope),
        ("Ошибка видна после закрытия оверлея", ReportAfterClose),
        ("Отмена и восстановление не активируют окно ошибки", SuppressError),
        ("Успешная операция оверлея не показывает ошибку", Success)
    ];

    private static Task PipeScope()
    {
        string first = CommandProtocol.PipeName("S-1-5-21-1", 1);
        Check(first == CommandProtocol.PipeName("S-1-5-21-1", 1), "Unstable name");
        Check(first != CommandProtocol.PipeName("S-1-5-21-1", 2), "Sessions collide");
        Check(first != CommandProtocol.PipeName("S-1-5-21-2", 1), "Users collide");
        return Task.CompletedTask;
    }

    private static async Task ReportAfterClose()
    {
        bool closed = false;
        Exception original = new IOException("Display failed");
        Exception? reported = null;
        try
        {
            await OverlayOperation.RunAsync<bool>(async () =>
            {
                try { await Task.Yield(); throw original; }
                finally { closed = true; }
            }, () => false, error => { Check(closed, "Reported before close"); reported = error; });
            throw new Exception("Failure swallowed");
        }
        catch (IOException error) { Check(ReferenceEquals(error, original), "Failure replaced"); }
        Check(ReferenceEquals(reported, original), "Failure not presented");
    }

    private static async Task SuppressError()
    {
        int reports = 0;
        try { await OverlayOperation.RunAsync<bool>(() => throw new OperationCanceledException(), () => false, _ => reports++); }
        catch (OperationCanceledException) { }
        try { await OverlayOperation.RunAsync<bool>(() => throw new IOException(), () => true, _ => reports++); }
        catch (IOException) { }
        Check(reports == 0, "Cancellation/recovery stole focus");
    }

    private static async Task Success()
    {
        bool result = await OverlayOperation.RunAsync(() => Task.FromResult(true), () => false,
            _ => throw new Exception("Unexpected report"));
        Check(result, "Return value lost");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
