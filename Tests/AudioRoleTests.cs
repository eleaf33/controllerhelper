using ControllerHelper.Services;

internal static class AudioRoleTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Частичная смена аудиоролей откатывается транзакцией", PartialApply),
        ("Отмена между аудиоролями не отменяет их восстановление", CancelApply),
        ("Ошибка первой аудиороли не мешает восстановлению остальных", ContinueRestore),
        ("Недоступный аудиовыход ожидается один раз и не блокирует другие", MissingEndpoint),
        ("Исходная ошибка и ошибки аудиоотката сохраняются вместе", CombinedFailures),
        ("Отсутствующая аудиороль пропускается, неверный снимок отклоняется", SnapshotValidation)
    ];
    private static readonly AudioDefaults Before = new(["pc", "music", "calls"]);
    private static Task Transaction(string?[] current, Action change, Action<string, int>? restore = null) =>
        ProfileSwitchTransaction.ApplyAsync(() => 1, () => { }, _ => { },
            () => Before, change,
            snapshot => AudioRoleOperations.Restore(snapshot, _ => { }, restore ?? ((id, role) => current[role] = id)));
    private static async Task PartialApply()
    {
        string?[] current = [.. Before.EndpointIds];
        var original = new InvalidOperationException("role 2 failed");
        try
        {
            await Transaction(current, () => AudioRoleOperations.Apply("tv", (id, role) =>
            { if (role == 2) throw original; current[role] = id; }, default));
        }
        catch (InvalidOperationException error) when (ReferenceEquals(error, original))
        { Check(current.SequenceEqual(Before.EndpointIds), "Partial apply wasn't restored"); return; }
        throw new Exception("Expected original error");
    }
    private static async Task CancelApply()
    {
        string?[] current = [.. Before.EndpointIds]; using CancellationTokenSource cancel = new();
        try
        {
            await Transaction(current, () => AudioRoleOperations.Apply("tv", (id, role) =>
            { current[role] = id; cancel.Cancel(); }, cancel.Token));
        }
        catch (OperationCanceledException)
        { Check(current.SequenceEqual(Before.EndpointIds), "Cancelled apply skipped restoration"); return; }
        throw new Exception("Expected cancellation");
    }
    private static Task ContinueRestore()
    {
        List<int> attempts = [];
        try { AudioRoleOperations.Restore(Before, _ => { }, (_, role) => { attempts.Add(role); if (role == 0) throw new IOException(); }); }
        catch (AggregateException error)
        { Check(attempts.SequenceEqual(new[] { 0, 1, 2 }) && error.InnerExceptions.Count == 1, "Restore stopped early"); return Task.CompletedTask; }
        throw new Exception("Expected rollback error");
    }
    private static Task MissingEndpoint()
    {
        List<string> waits = []; List<int> restored = [];
        try { AudioRoleOperations.Restore(new(["missing", "missing", "working"]), id =>
            { waits.Add(id); if (id == "missing") throw new IOException(); }, (_, role) => restored.Add(role)); }
        catch (AggregateException error)
        { Check(waits.SequenceEqual(new[] { "missing", "working" }) && restored.SequenceEqual(new[] { 2 }) && error.InnerExceptions.Count == 2, "Missing device blocked other roles"); return Task.CompletedTask; }
        throw new Exception("Expected unavailable device error");
    }
    private static async Task CombinedFailures()
    {
        var original = new IOException("apply"); List<int> attempted = [];
        try { await Transaction(["tv", "tv", "tv"], () => throw original,
            (_, role) => { attempted.Add(role); throw new IOException($"restore {role}"); }); }
        catch (InvalidOperationException error) when (error.InnerException is AggregateException aggregate)
        { Check(aggregate.Flatten().InnerExceptions.Contains(original) && attempted.Count == 3, "Error chain or role attempt lost"); return; }
        throw new Exception("Expected combined error");
    }
    private static Task SnapshotValidation()
    {
        List<int> attempted = [];
        AudioRoleOperations.Restore(new([null, "pc", null]), _ => { }, (_, role) => attempted.Add(role));
        Check(attempted.SequenceEqual(new[] { 1 }), "Attempted to restore an absent default");
        try { AudioRoleOperations.Restore(new(["pc"]), _ => throw new Exception("Must validate first"), (_, _) => { }); }
        catch (ArgumentException) { return Task.CompletedTask; }
        throw new Exception("Invalid snapshot accepted");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
