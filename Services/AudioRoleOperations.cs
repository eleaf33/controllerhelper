namespace ControllerHelper.Services;

internal sealed record AudioDefaults(string?[] EndpointIds);

internal static class AudioRoleOperations
{
    internal const int RoleCount = 3;
    internal static void Apply(string id, Action<string, int> set, CancellationToken cancellation)
    {
        for (int role = 0; role < RoleCount; role++)
        {
            cancellation.ThrowIfCancellationRequested();
            set(id, role);
        }
    }

    internal static void Restore(AudioDefaults snapshot, Action<string> wait, Action<string, int> set)
    {
        if (snapshot.EndpointIds.Length != RoleCount) throw new ArgumentException("Неверный снимок аудиоролей.", nameof(snapshot));
        Dictionary<string, Exception?> readiness = new(StringComparer.Ordinal);
        List<Exception> failures = [];
        for (int role = 0; role < RoleCount; role++)
        {
            string? id = snapshot.EndpointIds[role];
            // Windows' policy interface cannot restore the absence of a default.
            if (id is null) continue;
            if (!readiness.TryGetValue(id, out Exception? unavailable))
            {
                try { wait(id); }
                catch (Exception error) { unavailable = error; }
                readiness.Add(id, unavailable);
            }
            Exception? failure = unavailable;
            if (failure is null)
            {
                try { set(id, role); }
                catch (Exception error) { failure = error; }
            }
            if (failure is not null)
            {
                failures.Add(new InvalidOperationException($"Не удалось восстановить аудиороль {role}.", failure));
            }
        }
        if (failures.Count > 0) throw new AggregateException("Не все аудиороли восстановлены.", failures);
    }
}
