using ControllerHelper.Services;

internal static class FlydigiWindowActivationTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Flydigi: скрытое окно ждёт штатного показа клиентом", HiddenUntilApplicationShows),
        ("Flydigi: видимый HWND тоже получает штатную активацию клиента", ExistingVisible),
        ("Flydigi: холодный запуск ждёт UI дольше запуска процесса и службы", ColdStart),
        ("Flydigi: скрытый HWND не считается готовым при тайм-ауте", HiddenTimeout),
        ("Flydigi: отменённое открытие не запускает EXE", CanceledBeforeLaunch),
        ("Flydigi: закрытие оверлея отменяет ожидание UI", CanceledWhileWaiting),
        ("Flydigi: ошибка запуска не превращается в успех найденного HWND", LaunchFailure)
    ];

    private static FlydigiWindowCandidate Window(bool visible) => new(123, 42, visible, false, true,
        new(10, 20, 1000, 900), "Chrome_WidgetWin_1", "Flydigi Space Station", 0, 0x100);
    private static Task ImmediateDelay(TimeSpan _, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private static async Task HiddenUntilApplicationShows()
    {
        int launches = 0, polls = 0;
        var result = await FlydigiWindowActivation.OpenAsync(() => launches++, () =>
        {
            Check(launches == 1, "Window queried before the application's own activation");
            return Window(++polls >= 3);
        }, delay: ImmediateDelay);
        Check(result.Visible && polls == 3 && launches == 1, "Hidden HWND accepted or EXE repeatedly launched");
    }

    private static async Task ExistingVisible()
    {
        bool applicationActivated = false;
        await FlydigiWindowActivation.OpenAsync(() => applicationActivated = true, () =>
        {
            Check(applicationActivated, "Native visible flag bypassed application activation");
            return Window(true);
        }, delay: (_, _) => throw new Exception("Unnecessary delay for an existing window"));
    }

    private static async Task ColdStart()
    {
        int polls = 0;
        var result = await FlydigiWindowActivation.OpenAsync(() => { }, () =>
        {
            polls++;
            return polls < 3 ? null : Window(polls >= 50);
        }, delay: ImmediateDelay);
        Check(result.Visible && polls == 50, "Startup completed before UI appeared or timed out at the old 10 s limit");
    }

    private static async Task HiddenTimeout()
    {
        int launches = 0, polls = 0;
        try
        {
            await FlydigiWindowActivation.OpenAsync(() => launches++, () => { polls++; return Window(false); },
                attempts: 3, delay: ImmediateDelay);
            throw new Exception("Hidden window was accepted as rendered UI");
        }
        catch (InvalidOperationException)
        { Check(launches == 1 && polls == 3, "Timeout not bounded or relaunch loop occurred"); }
    }

    private static async Task CanceledBeforeLaunch()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try
        {
            await FlydigiWindowActivation.OpenAsync(() => throw new Exception("Launched after cancellation"),
                () => Window(true), cancel.Token);
            throw new Exception("Cancellation ignored");
        }
        catch (OperationCanceledException) { }
    }

    private static async Task CanceledWhileWaiting()
    {
        using var cancel = new CancellationTokenSource();
        int polls = 0;
        try
        {
            await FlydigiWindowActivation.OpenAsync(() => { }, () => { polls++; return Window(false); },
                cancel.Token, delay: (_, token) => { cancel.Cancel(); return Task.FromCanceled(token); });
            throw new Exception("Closed overlay still waits for the client");
        }
        catch (OperationCanceledException) { Check(polls == 1, "Cancellation was delayed"); }
    }

    private static async Task LaunchFailure()
    {
        var expected = new IOException("test launch failure");
        try
        {
            await FlydigiWindowActivation.OpenAsync(() => throw expected,
                () => throw new Exception("Window lookup masked failed launch"));
            throw new Exception("Launch error ignored");
        }
        catch (IOException actual) { Check(ReferenceEquals(actual, expected), "Original failure lost"); }
    }
}
