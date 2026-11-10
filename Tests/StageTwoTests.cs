using System.IO.Pipes;
using System.Text;
using ControllerHelper.Models;
using ControllerHelper.Services;

internal static class StageTwoTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Ответ сопряжения сохраняет более свежие поля", PairingPreservesUpdates),
        ("Ответ другого ТВ не меняет настройки", PairingRejectsDifferentTarget),
        ("Сопряжение сравнивает нормализованные адреса", PairingNormalizesTarget),
        ("Локальный возврат доступен до ответа сети", LocalStateIsImmediate),
        ("Закрытие оверлея отменяет сеть и игнорирует поздний ответ", ClosedOverlayIgnoresReply),
        ("Новое обновление оверлея отвергает устаревший ответ", NewRefreshWins),
        ("Ошибка сети сохраняет локальное действие", NetworkFailureKeepsLocalAction),
        ("Начатое действие отменяет фоновое обновление", ActionCancelsRefresh),
        ("Проверка питания ограничена общим сроком", PowerDeadline),
        ("Отмена проверки питания не заполняет кэш", PowerCancellation),
        ("Кэш питания привязан к ТВ, ключу и сроку", PowerCacheIdentityAndExpiry),
        ("Поздний ответ не восстанавливает сброшенный кэш", PowerInvalidation),
        ("Несопряжённый ТВ не вызывает сетевой опрос", UnpairedPower),
        ("IPC принимает допустимые LF и CRLF команды", ValidCommands),
        ("IPC отвергает неизвестные, длинные и оборванные команды", InvalidCommands),
        ("IPC ограничивает общее время чтения, включая медленные байты", SlowCommandDeadline),
        ("Отмена приложения прерывает чтение IPC", CommandCancellation),
        ("После молчащего IPC-клиента следующий получает обслуживание", PipeRecoversAfterIdlePeer)
    ];

    private static AppSettings Settings() => new()
    {
        TelevisionId = "display-tv", TelevisionIpAddress = "192.0.2.10",
        TelevisionMacAddress = "00:11:22:33:44:55", TelevisionClientKey = "test-key", WakeTelevision = true
    };
    private static readonly TelevisionOverlayState Local = new(true, false, TelevisionPowerState.Unknown, true, false, true);
    private static readonly TelevisionOverlayState Online = Local with { NetworkReachable = true, PowerState = TelevisionPowerState.On, PowerCheckPending = false };

    private static Task PairingPreservesUpdates()
    {
        AppSettings initial = Settings();
        TelevisionPairingTarget pending = TelevisionPairingTarget.From(initial);
        AppSettings latest = initial.Copy();
        latest.PcAudioId = "new-audio";
        latest.HotkeyModifier = "Y";
        latest.FlydigiCursorSpeed = 2.0;
        latest.PcPaths = "new-layout";
        latest.MonitorIds.Add("new-monitor");
        AppSettings updated = pending.ApplyKey(latest, "new-key");
        Check(updated.PcAudioId == "new-audio" && updated.HotkeyModifier == "Y" && updated.FlydigiCursorSpeed == 2 &&
            updated.PcPaths == "new-layout" && updated.MonitorIds.SequenceEqual(latest.MonitorIds), "Pairing overwrote fresh settings.");
        Check(updated.TelevisionClientKey == "new-key" && latest.TelevisionClientKey == "test-key", "Pairing mutated the old snapshot.");
        updated.MonitorIds.Clear();
        Check(latest.MonitorIds.Count == 1, "The copied settings share a mutable monitor list.");
        return Task.CompletedTask;
    }

    private static async Task PairingRejectsDifferentTarget()
    {
        AppSettings initial = Settings();
        TelevisionPairingTarget pending = TelevisionPairingTarget.From(initial);
        foreach (Action<AppSettings> change in new Action<AppSettings>[]
        { s => s.TelevisionId = "other", s => s.TelevisionIpAddress = "192.0.2.11", s => s.TelevisionMacAddress = "00:11:22:33:44:66" })
        {
            AppSettings latest = initial.Copy(); change(latest);
            await Throws<InvalidOperationException>(() => { pending.ApplyKey(latest, "stale-key"); return Task.CompletedTask; });
            Check(latest.TelevisionClientKey == "test-key", "Mismatching pairing changed the key.");
        }
    }

    private static Task PairingNormalizesTarget()
    {
        Check(TelevisionPairingTarget.Create("DISPLAY-tv", "192.0.2.10 ", "AA-bb-22-33-44-55") ==
            TelevisionPairingTarget.Create("display-TV", "192.0.2.10", "aa:BB:22:33:44:55"), "Equivalent targets do not match.");
        return Task.CompletedTask;
    }

    private static async Task LocalStateIsImmediate()
    {
        var reply = Signal<TelevisionOverlayState>();
        List<(TelevisionOverlayState State, bool Focus)> shown = [];
        using OverlayStateRefresh refresh = new(() => Local, _ => reply.Task,
            (state, focus) => shown.Add((state, focus)), _ => throw new Exception("Unexpected network error"));
        refresh.Refresh();
        Check(shown.Count == 1 && !refresh.Completion.IsCompleted, "Local actions waited for the network.");
        Check(OverlayActionPolicy.Resolve(shown[0].State) == OverlayPrimaryAction.ReturnToPc &&
            OverlayPresentation.FromState(shown[0].State).Warning is null, "No immediate safe return action.");
        reply.SetResult(Online);
        await refresh.Completion;
        Check(shown.Count == 2 && !shown[1].Focus && OverlayActionPolicy.Resolve(shown[1].State) == OverlayPrimaryAction.ReturnAndPowerOff,
            "Network result failed to enhance the action without changing focus.");
    }

    private static async Task ClosedOverlayIgnoresReply()
    {
        var reply = Signal<TelevisionOverlayState>();
        CancellationToken token = default;
        int shown = 0;
        OverlayStateRefresh refresh = new(() => Local, cancellation => { token = cancellation; return reply.Task; },
            (_, _) => shown++, _ => { });
        refresh.Refresh();
        refresh.Dispose();
        Check(token.IsCancellationRequested, "Closing the overlay did not cancel network work.");
        reply.SetResult(Online);
        await refresh.Completion;
        Check(shown == 1, "Closed overlay received a late result.");
    }

    private static async Task NewRefreshWins()
    {
        var oldReply = Signal<TelevisionOverlayState>();
        var newReply = Signal<TelevisionOverlayState>();
        int requested = 0;
        List<TelevisionOverlayState> shown = [];
        using OverlayStateRefresh refresh = new(() => Local, _ => ++requested == 1 ? oldReply.Task : newReply.Task,
            (state, _) => shown.Add(state), _ => { });
        refresh.Refresh(); Task oldCompletion = refresh.Completion;
        refresh.Refresh(); Task newCompletion = refresh.Completion;
        newReply.SetResult(Online); await newCompletion;
        oldReply.SetResult(Local with { PowerState = TelevisionPowerState.Off }); await oldCompletion;
        Check(shown.Count == 3 && shown[^1] == Online, "Stale response replaced the latest state.");
    }

    private static async Task NetworkFailureKeepsLocalAction()
    {
        var reply = Signal<TelevisionOverlayState>();
        int errors = 0;
        List<TelevisionOverlayState> shown = [];
        using OverlayStateRefresh refresh = new(() => Local, _ => reply.Task, (state, _) => shown.Add(state), _ => errors++);
        refresh.Refresh(); reply.SetException(new IOException("network failure")); await refresh.Completion;
        Check(shown.Count == 1 && OverlayActionPolicy.Resolve(shown[0]) == OverlayPrimaryAction.ReturnToPc && errors == 1,
            "Network error removed the local action.");
    }

    private static async Task ActionCancelsRefresh()
    {
        var reply = Signal<TelevisionOverlayState>();
        int shown = 0;
        using OverlayStateRefresh refresh = new(() => Local, _ => reply.Task, (_, _) => shown++, _ => { });
        refresh.Refresh(); refresh.Cancel(); reply.SetResult(Online); await refresh.Completion;
        Check(shown == 1, "Network changed the primary action after an action started.");
    }

    private static async Task PowerDeadline()
    {
        var reply = Signal<TelevisionPowerState>();
        CancellationToken token = default;
        TelevisionPowerProbe probe = new((_, cancellation) => { token = cancellation; return reply.Task; }, TimeSpan.FromMilliseconds(40));
        Check(await probe.ReadAsync(Settings(), default).WaitAsync(TimeSpan.FromSeconds(5)) == TelevisionPowerState.Unknown,
            "Unresponsive network did not fall back to Unknown.");
        Check(token.IsCancellationRequested, "Deadline did not cancel the underlying operation.");
        reply.SetResult(TelevisionPowerState.On);
        Check(await probe.ReadAsync(Settings(), default) == TelevisionPowerState.Unknown, "Late reply replaced timed-out result.");
    }

    private static async Task PowerCancellation()
    {
        var reply = Signal<TelevisionPowerState>();
        int requests = 0;
        TelevisionPowerProbe probe = new((_, _) => ++requests == 1 ? reply.Task : Task.FromResult(TelevisionPowerState.On));
        using CancellationTokenSource cancellation = new();
        Task<TelevisionPowerState> operation = probe.ReadAsync(Settings(), cancellation.Token);
        cancellation.Cancel();
        await Throws<OperationCanceledException>(() => operation);
        reply.SetResult(TelevisionPowerState.Off);
        Check(await probe.ReadAsync(Settings(), default) == TelevisionPowerState.On && requests == 2, "Canceled request populated cache.");
    }

    private sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task PowerCacheIdentityAndExpiry()
    {
        ManualClock clock = new(); int requests = 0;
        TelevisionPowerProbe probe = new((_, _) => { requests++; return Task.FromResult(TelevisionPowerState.On); }, clock: clock);
        AppSettings settings = Settings();
        await probe.ReadAsync(settings, default); await probe.ReadAsync(settings.Copy(), default);
        Check(requests == 1, "Fresh matching result was not cached.");
        clock.Now += TimeSpan.FromSeconds(4); await probe.ReadAsync(settings, default);
        settings.TelevisionClientKey = "changed-key"; await probe.ReadAsync(settings, default);
        settings.TelevisionIpAddress = "192.0.2.11"; await probe.ReadAsync(settings, default);
        Check(requests == 4, "Expired/different television credentials reused cached power.");
    }

    private static async Task PowerInvalidation()
    {
        var reply = Signal<TelevisionPowerState>(); int requests = 0;
        TelevisionPowerProbe probe = new((_, _) => ++requests == 1 ? reply.Task : Task.FromResult(TelevisionPowerState.Off));
        Task<TelevisionPowerState> operation = probe.ReadAsync(Settings(), default);
        probe.Invalidate(); reply.SetResult(TelevisionPowerState.On);
        Check(await operation == TelevisionPowerState.Unknown, "Invalidated in-flight result was accepted.");
        Check(await probe.ReadAsync(Settings(), default) == TelevisionPowerState.Off && requests == 2, "Invalidation failed to clear cache.");
    }

    private static async Task UnpairedPower()
    {
        TelevisionPowerProbe probe = new((_, _) => throw new Exception("Must not call network without pairing."));
        AppSettings settings = Settings(); settings.TelevisionClientKey = null;
        Check(await probe.ReadAsync(settings, default) == TelevisionPowerState.Unknown, "Unpaired TV should have unknown power.");
    }

    private static async Task ValidCommands()
    {
        foreach (string command in new[] { "show", "--to-pc", "--to-tv", "--overlay" })
            foreach (string newline in new[] { "\n", "\r\n" })
            {
                using MemoryStream stream = new(Encoding.UTF8.GetBytes(command + newline));
                Check(await CommandProtocol.ReadAsync(stream, default) == command, "Valid IPC command rejected.");
            }
    }

    private static async Task InvalidCommands()
    {
        foreach (string command in new[] { "", "\n", "show", "--unknown\n", "show\0\n", "show\rjunk\n", "шоу\n", new string('x', 1000) + "\n" })
        {
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(command));
            Check(await CommandProtocol.ReadAsync(stream, default) is null, "Invalid command accepted.");
            Check(stream.Position <= CommandProtocol.MaximumCommandBytes + 1, "Oversized input read without a bound.");
        }
    }

    private sealed class SlowStream(int delayMilliseconds) : MemoryStream(Encoding.ASCII.GetBytes("--overlay\n"))
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delayMilliseconds, cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private static async Task SlowCommandDeadline()
    {
        using SlowStream stream = new(25);
        Check(await CommandProtocol.ReadAsync(stream, default, TimeSpan.FromMilliseconds(60)).WaitAsync(TimeSpan.FromSeconds(5)) is null,
            "Slow peer extended the overall deadline.");
    }

    private static async Task CommandCancellation()
    {
        using SlowStream stream = new(5000);
        using CancellationTokenSource cancellation = new();
        Task<string?> read = CommandProtocol.ReadAsync(stream, cancellation.Token);
        cancellation.Cancel();
        await Throws<OperationCanceledException>(() => read);
    }

    private static async Task PipeRecoversAfterIdlePeer()
    {
        string name = "ControllerHelper.Tests." + Guid.NewGuid().ToString("N");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        async Task<string?> AcceptAsync()
        {
            using NamedPipeServerStream server = new(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await server.WaitForConnectionAsync(timeout.Token);
            return await CommandProtocol.ReadAsync(server, timeout.Token, TimeSpan.FromMilliseconds(80));
        }
        Task<string?> first = AcceptAsync();
        using (NamedPipeClientStream idle = new(".", name, PipeDirection.Out, PipeOptions.Asynchronous))
        {
            await idle.ConnectAsync(timeout.Token);
            Check(await first is null, "Idle IPC peer did not time out.");
        }
        Task<string?> second = AcceptAsync();
        using NamedPipeClientStream next = new(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        await next.ConnectAsync(timeout.Token);
        await next.WriteAsync(Encoding.ASCII.GetBytes("--overlay\n"), timeout.Token);
        Check(await second == "--overlay", "Server did not service the next client.");
    }

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
