using ControllerHelper.Models;
using ControllerHelper.Services;
using Microsoft.UI.Dispatching;
using System.Diagnostics;
using System.Text.Json;

internal static class EmergencyReturnTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Аварийный хоткей выключен по умолчанию и сохраняется", Settings),
        ("Одинаковые и обратные комбинации отклоняются", Validation),
        ("Повреждённый аварийный хоткей отключается", Normalization),
        ("Возврат требует 2 секунды и полного отпускания", HoldAndRelease),
        ("Уже зажатая при подключении комбинация не срабатывает", InitiallyHeld),
        ("Выключенный хоткей не выполняет возврат", Disabled),
        ("Аварийный хоткей работает без фокуса оверлея", Background),
        ("Смена владельца ввода не прерывает аварийное удержание", Ownership),
        ("Потеря чтения сбрасывает аварийное удержание", InvalidReading),
        ("Отключение геймпада сбрасывает аварийное удержание", Disconnect),
        ("Обучение кнопок подавляет аварийный возврат", Learning),
        ("Аварийная A не превращается в выбор меню", NoMenuAction),
        ("Аварийный хоткей читает сырые кнопки выбранного геймпада", RawButtons),
        ("Другой геймпад не выполняет аварийный возврат", SelectedOnly),
        ("Возврат ждёт отмену и откат, повторы объединяются", RecoveryWaits),
        ("Ошибка предыдущей операции не блокирует возврат", PriorFailure),
        ("Выход отменяет ожидающий возврат и ждёт откат", StopQueuedRecovery),
        ("Выход при ожидающем возврате сохраняет ошибку отката", StopQueuedRecoveryFailure),
        ("Выход отменяет выполняющийся возврат", StopActiveRecovery),
        ("Возврат восстанавливает дисплеи перед звуком и Steam", RecoveryOrder),
        ("Ошибки звука и Steam не отменяют восстановленные дисплеи", RecoveryWarnings),
        ("Ошибка дисплея прекращает аварийный возврат", DisplayFailure),
        ("Отмена до восстановления не меняет дисплеи", CanceledBeforeDisplay),
        ("Отмена после восстановления не откатывает дисплеи", CanceledAfterDisplay)
    ];

    private static Task Settings()
    {
        AppSettings old = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Check(!old.EmergencyReturnEnabled && old.EmergencyReturnModifier == "LeftShoulder" &&
            old.EmergencyReturnTrigger == "RightShoulder", "Unsafe defaults");
        old.EmergencyReturnEnabled = true;
        old.EmergencyReturnModifier = "Button:4";
        old.EmergencyReturnTrigger = "Menu";
        AppSettings restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(old.Copy()))!;
        Check(restored.EmergencyReturnEnabled && restored.EmergencyReturnModifier == "Button:4" &&
            restored.EmergencyReturnTrigger == "Menu", "Settings lost");
        return Task.CompletedTask;
    }

    private static Task Validation()
    {
        Reject(() => EmergencyHotkey.Validate(true, "A", "A", "A", "View"));
        Reject(() => EmergencyHotkey.Validate(true, "View", "A", "A", "View"));
        Reject(() => EmergencyHotkey.Validate(true, "A", "View", "A", "View"));
        EmergencyHotkey.Validate(false, "A", "View", "A", "View");
        EmergencyHotkey.Validate(true, "A", "Menu", "A", "View");
        return Task.CompletedTask;
    }

    private static Task Normalization()
    {
        ControllerButtonCatalog catalog = new();
        AppSettings settings = new() { EmergencyReturnEnabled = true, EmergencyReturnModifier = "invalid" };
        Check(catalog.Normalize(settings) && !settings.EmergencyReturnEnabled, "Invalid key accepted");
        settings.EmergencyReturnEnabled = true;
        settings.EmergencyReturnModifier = "View";
        settings.EmergencyReturnTrigger = "A";
        Check(catalog.Normalize(settings) && !settings.EmergencyReturnEnabled, "Conflicting pair accepted");
        return Task.CompletedTask;
    }

    private sealed class Rig : IDisposable
    {
        internal readonly DispatcherQueue Queue = new();
        internal readonly ControllerMonitor Monitor;
        internal long Now;
        internal int Returns, Overlays;
        internal Rig(bool enabled = true, string first = "LeftShoulder", string second = "RightShoulder")
        {
            GameInputService.Devices = [];
            Monitor = new(Queue) { Timestamp = () => Now };
            Monitor.Configure("selected", "fingerprint", "A", "View", enabled, first, second);
            Monitor.EmergencyReturnPressed += () => Returns++;
            Monitor.HotkeyPressed += () => Overlays++;
        }
        internal void Read(uint buttons = 0, double seconds = 0, bool valid = true, bool[]? raw = null)
        {
            Now = (long)(seconds * Stopwatch.Frequency);
            GameInputService.Devices = [Device("selected", buttons, raw, valid)];
            Fire();
        }
        internal void Fire()
        {
            Queue.Timer.Fire();
            Check(Monitor.Error is null, Monitor.Error ?? "Input failed");
        }
        public void Dispose() => Monitor.Stop();
    }

    private const uint Shoulders = 0x400 | 0x800;
    private static ControllerDevice Device(string id, uint buttons, bool[]? raw = null, bool valid = true) =>
        new(id, "Gamepad", id == "selected" ? "fingerprint" : "other", 1, 2, buttons, raw ?? [], default) { HasReading = valid };

    private static Task HoldAndRelease()
    {
        using Rig rig = new();
        rig.Read(); rig.Read(Shoulders, 1); rig.Read(Shoulders, 2.99);
        Check(rig.Returns == 0, "Early trigger");
        rig.Read(Shoulders, 3); rig.Read(Shoulders, 8);
        rig.Read(0x400, 9); rig.Read(Shoulders, 10); rig.Read(Shoulders, 15);
        Check(rig.Returns == 1, "Repeated without neutral");
        rig.Read(0, 16); rig.Read(Shoulders, 17); rig.Read(Shoulders, 19);
        Check(rig.Returns == 2, "Full release did not rearm");
        return Task.CompletedTask;
    }

    private static Task InitiallyHeld()
    {
        using Rig rig = new();
        rig.Read(Shoulders); rig.Read(Shoulders, 5);
        Check(rig.Returns == 0, "Held reconnect triggered");
        rig.Read(0, 6); rig.Read(Shoulders, 7); rig.Read(Shoulders, 9);
        Check(rig.Returns == 1, "Did not arm");
        return Task.CompletedTask;
    }

    private static Task Disabled()
    {
        using Rig rig = new(false);
        rig.Read(); rig.Read(Shoulders, 1); rig.Read(Shoulders, 4);
        Check(rig.Returns == 0, "Disabled hotkey triggered");
        return Task.CompletedTask;
    }

    private static Task Background()
    {
        using Rig rig = new();
        using var scope = rig.Monitor.AcquireInput(ControllerInputMode.Overlay, hasFocus: () => false);
        rig.Read(); rig.Read(Shoulders, 1); rig.Read(Shoulders, 3);
        Check(rig.Returns == 1, "Background recovery blocked");
        return Task.CompletedTask;
    }

    private static Task Ownership()
    {
        using Rig rig = new();
        rig.Read(); rig.Read(Shoulders, 1);
        var scope = rig.Monitor.AcquireInput(ControllerInputMode.Overlay);
        rig.Read(Shoulders, 2); scope.Dispose(); rig.Read(Shoulders, 3);
        Check(rig.Returns == 1, "Ownership interrupted emergency hold");
        return Task.CompletedTask;
    }

    private static Task InvalidReading()
    {
        using Rig rig = new();
        rig.Read(); rig.Read(Shoulders, 1); rig.Read(Shoulders, 3, valid: false);
        rig.Read(Shoulders, 6);
        Check(rig.Returns == 0, "Invalid reading preserved hold");
        rig.Read(0, 7); rig.Read(Shoulders, 8); rig.Read(Shoulders, 10);
        Check(rig.Returns == 1, "Reading recovery failed");
        return Task.CompletedTask;
    }

    private static Task Disconnect()
    {
        using Rig rig = new();
        rig.Read(); rig.Read(Shoulders, 1);
        GameInputService.Devices = []; rig.Fire();
        rig.Read(Shoulders, 3); rig.Read(Shoulders, 6);
        Check(rig.Returns == 0, "Disconnect preserved hold");
        rig.Read(0, 7); rig.Read(Shoulders, 8); rig.Read(Shoulders, 10);
        Check(rig.Returns == 1, "Reconnect failed");
        return Task.CompletedTask;
    }

    private static Task Learning()
    {
        using Rig rig = new();
        rig.Read(); rig.Monitor.LearnButton(_ => { });
        rig.Read(); rig.Read(); rig.Read(Shoulders, 1); rig.Read(Shoulders, 5);
        Check(rig.Returns == 0, "Learning triggered recovery");
        return Task.CompletedTask;
    }

    private static Task NoMenuAction()
    {
        using Rig rig = new(first: "A", second: "Menu");
        int actions = 0;
        using var scope = rig.Monitor.AcquireInput(ControllerInputMode.Overlay, confirm: () => actions++);
        rig.Read(); rig.Read(0x4, 1); rig.Read(0x5, 2); rig.Read(0x5, 4);
        rig.Read(0x4, 5); rig.Read(0, 6);
        Check(rig.Returns == 1 && actions == 0 && rig.Overlays == 0, "Recovery leaked to menu");
        return Task.CompletedTask;
    }

    private static Task RawButtons()
    {
        using Rig rig = new(first: "Button:0", second: "Button:1");
        rig.Read(raw: [false, false]); rig.Read(seconds: 1, raw: [true, true]);
        rig.Read(seconds: 3, raw: [true, true]);
        Check(rig.Returns == 1 && GameInputService.LastCreated!.RawRequested, "Raw keys not read");
        return Task.CompletedTask;
    }

    private static Task SelectedOnly()
    {
        using Rig rig = new();
        rig.Read();
        GameInputService.Devices = [Device("selected", 0), Device("other", Shoulders)];
        rig.Now = Stopwatch.Frequency; rig.Fire();
        rig.Now = 4 * Stopwatch.Frequency; rig.Fire();
        Check(rig.Returns == 0, "Wrong controller triggered recovery");
        return Task.CompletedTask;
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task RecoveryWaits()
    {
        ProfileOperationCoordinator coordinator = new();
        var canceled = Gate(); var rollback = Gate(); var entered = Gate(); var finish = Gate();
        Task active = coordinator.RunAsync(async token =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            await canceled.Task;
            await rollback.Task;
            token.ThrowIfCancellationRequested();
        });
        Task recovery = coordinator.RecoverAsync(async _ => { entered.SetResult(); await finish.Task; });
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!entered.Task.IsCompleted && coordinator.IsRecovering, "Recovery overtook rollback");
        Check(ReferenceEquals(recovery, coordinator.RecoverAsync(_ => throw new Exception("Duplicate"))), "Recovery not coalesced");
        Reject(() => coordinator.RunAsync(_ => Task.CompletedTask));
        rollback.SetResult(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Canceled(active);
        Reject(() => coordinator.RunAsync(_ => Task.CompletedTask));
        finish.SetResult(); await recovery;
        Check(!coordinator.IsRunning, "Busy state retained");
        await coordinator.RunAsync(_ => Task.CompletedTask);
    }

    private static async Task PriorFailure()
    {
        ProfileOperationCoordinator coordinator = new();
        var finish = Gate();
        Task active = coordinator.RunAsync(async _ => { await finish.Task; throw new IOException("Rollback failed"); });
        bool restored = false;
        Task recovery = coordinator.RecoverAsync(_ => { restored = true; return Task.CompletedTask; });
        finish.SetResult();
        await Throws<IOException>(() => active);
        await recovery;
        Check(restored, "Prior failure blocked rescue");
    }

    private static async Task StopQueuedRecovery()
    {
        ProfileOperationCoordinator coordinator = new();
        var finish = Gate();
        Task active = coordinator.RunAsync(async token => { await finish.Task; token.ThrowIfCancellationRequested(); });
        bool restored = false;
        Task recovery = coordinator.RecoverAsync(_ => { restored = true; return Task.CompletedTask; });
        Task stop = coordinator.StopAsync();
        Check(!stop.IsCompleted, "Exit did not wait for rollback");
        finish.SetResult();
        await stop; await Canceled(active); await Canceled(recovery);
        Check(!restored && !coordinator.IsRunning, "Canceled rescue changed displays");
    }

    private static async Task StopActiveRecovery()
    {
        ProfileOperationCoordinator coordinator = new();
        Task recovery = coordinator.RecoverAsync(token => Task.Delay(Timeout.Infinite, token));
        await coordinator.StopAsync(); await Canceled(recovery);
        Check(!coordinator.IsRunning && coordinator.IsStopping, "Exit did not settle recovery");
    }

    private static async Task StopQueuedRecoveryFailure()
    {
        ProfileOperationCoordinator coordinator = new();
        var finish = Gate();
        IOException rollbackFailure = new("Rollback failed");
        Task active = coordinator.RunAsync(async _ => { await finish.Task; throw rollbackFailure; });
        bool restored = false;
        Task recovery = coordinator.RecoverAsync(_ => { restored = true; return Task.CompletedTask; });
        Task stop = coordinator.StopAsync();
        Check(!stop.IsCompleted, "Exit did not wait for rollback");
        finish.SetResult();
        await Throws<IOException>(() => active);
        await Throws<InvalidOperationException>(() => recovery);
        await Throws<InvalidOperationException>(() => stop);
        Check(!restored && !coordinator.IsRunning && !coordinator.IsStopping,
            "Failed rollback allowed exit or left coordinator blocked");
        Check(ReferenceEquals(recovery.Exception!.InnerException!.InnerException, rollbackFailure),
            "Original rollback error lost");
        await coordinator.RecoverAsync(_ => { restored = true; return Task.CompletedTask; });
        Check(restored, "Retry recovery blocked");
    }

    private static async Task RecoveryOrder()
    {
        List<string> calls = [];
        string? warning = await PcRecoveryOperation.RunAsync(() => calls.Add("display"),
            _ => calls.Add("audio"), () => calls.Add("steam"), default);
        Check(warning is null && calls.SequenceEqual(["display", "audio", "steam"]), "Wrong restoration order");
    }

    private static async Task RecoveryWarnings()
    {
        int restores = 0; bool steam = false;
        string? warning = await PcRecoveryOperation.RunAsync(() => restores++,
            _ => throw new IOException("audio unavailable"), () => { steam = true; throw new IOException("steam unavailable"); }, default);
        Check(restores == 1 && steam && warning!.Contains("audio unavailable") && warning.Contains("steam unavailable"), "Recovery rolled back or lost warning");
    }

    private static async Task DisplayFailure()
    {
        bool downstream = false;
        await Throws<IOException>(() => PcRecoveryOperation.RunAsync(() => throw new IOException("display"),
            _ => downstream = true, () => downstream = true, default));
        Check(!downstream, "Display failure still changed audio/Steam");
    }

    private static async Task CanceledBeforeDisplay()
    {
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        bool changed = false;
        await Canceled(PcRecoveryOperation.RunAsync(() => changed = true, _ => changed = true, () => changed = true, cancellation.Token));
        Check(!changed, "Canceled operation changed hardware");
    }

    private static async Task CanceledAfterDisplay()
    {
        using CancellationTokenSource cancellation = new();
        int restores = 0; bool downstream = false;
        await Canceled(PcRecoveryOperation.RunAsync(() => { restores++; cancellation.Cancel(); },
            _ => downstream = true, () => downstream = true, cancellation.Token));
        Check(restores == 1 && !downstream, "Cancellation undid recovery or continued");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return; }
        throw new Exception("Expected rejection");
    }
    private static Task Canceled(Task task) => Throws<OperationCanceledException>(() => task);
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}
