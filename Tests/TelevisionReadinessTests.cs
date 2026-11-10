using ControllerHelper.Services;

internal static class TelevisionReadinessTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Холодный запуск ждёт HDMI дольше прежних 15 секунд", ColdStart),
        ("Краткое появление HDMI не начинает переключение", FlickeringConnection),
        ("Уже доступный HDMI требует только периода стабильности", AlreadyAvailable),
        ("Недоступный HDMI ограничен общим сроком", NeverAvailable),
        ("Отмена во время ожидания HDMI не меняет экран и откатывает транзакцию", CancellationRollsBack),
        ("Ошибка запроса HDMI не маскируется повторными попытками", QueryFailure)
    ];

    private sealed class Clock : TimeProvider
    {
        internal TimeSpan Elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        internal void Delay(TimeSpan pause, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); Elapsed += pause; }
    }

    private static Task ColdStart()
    {
        Clock clock = new();
        new TelevisionReadiness(clock, clock.Delay).WaitUntilReady(() => clock.Elapsed >= TimeSpan.FromSeconds(20), default);
        Check(clock.Elapsed == TimeSpan.FromSeconds(21), "Did not wait for a slow boot and stable HDMI.");
        return Task.CompletedTask;
    }

    private static Task FlickeringConnection()
    {
        Clock clock = new();
        new TelevisionReadiness(clock, clock.Delay).WaitUntilReady(
            () => clock.Elapsed < TimeSpan.FromMilliseconds(500) || clock.Elapsed >= TimeSpan.FromSeconds(2), default);
        Check(clock.Elapsed == TimeSpan.FromSeconds(3), "Transient availability was accepted or stability was not reset.");
        return Task.CompletedTask;
    }

    private static Task AlreadyAvailable()
    {
        Clock clock = new();
        new TelevisionReadiness(clock, clock.Delay).WaitUntilReady(() => true, default);
        Check(clock.Elapsed == TimeSpan.FromSeconds(1), "An available TV waits beyond the stability interval.");
        return Task.CompletedTask;
    }

    private static Task NeverAvailable()
    {
        Clock clock = new();
        try
        {
            new TelevisionReadiness(clock, clock.Delay).WaitUntilReady(() => false, default);
            throw new Exception("Missing timeout.");
        }
        catch (TimeoutException)
        { Check(clock.Elapsed == TimeSpan.FromSeconds(30), "Readiness exceeded its deadline."); }
        return Task.CompletedTask;
    }

    private static async Task CancellationRollsBack()
    {
        Clock clock = new();
        using CancellationTokenSource cancellation = new();
        bool displayChanged = false, confirmed = false, rolledBack = false;
        TelevisionReadiness readiness = new(clock, (pause, token) =>
        {
            clock.Delay(pause, token);
            cancellation.Cancel();
        });
        try
        {
            await ProfileSwitchTransaction.ApplyAsync<int, int>(() => 7, () =>
            {
                readiness.WaitUntilReady(() => false, cancellation.Token);
                displayChanged = true;
            }, snapshot => rolledBack = snapshot == 7,
                confirmDisplay: () => { confirmed = true; return Task.FromResult(true); }, cancellation: cancellation.Token);
            throw new Exception("Missing cancellation.");
        }
        catch (OperationCanceledException)
        { Check(!displayChanged && !confirmed && rolledBack, "Cancellation failed to stop switching or await rollback."); }
    }

    private static Task QueryFailure()
    {
        Clock clock = new();
        IOException original = new("Display query failed");
        try
        {
            new TelevisionReadiness(clock, clock.Delay).WaitUntilReady(() => throw original, default);
            throw new Exception("Missing query failure.");
        }
        catch (IOException error)
        { Check(ReferenceEquals(error, original) && clock.Elapsed == TimeSpan.Zero, "Query failure was hidden."); }
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
