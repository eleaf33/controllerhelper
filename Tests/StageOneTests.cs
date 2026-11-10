using System.Diagnostics;
using ControllerHelper.Services;
using Microsoft.UI.Dispatching;

internal static class StageOneTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Подтверждение владеет вводом независимо от порядка открытия окон", ConfirmationOwnsInput),
        ("Закрытый оверлей не восстанавливает владение вводом", ClosedOwnerStaysClosed),
        ("A и B в одном чтении отменяют без подтверждения", CancelWins),
        ("Подключение другого геймпада не повторяет A", UnrelatedHotplug),
        ("Переподключение с удерживаемой A требует отпускания", ReconnectRequiresRelease),
        ("Подтверждение не принимает A из предыдущего окна", OwnershipRequiresRelease),
        ("Подтверждение приостанавливает ввод Flydigi", ConfirmationSuspendsFlydigi),
        ("Подтверждение отменяет обучение кнопки", ConfirmationCancelsLearning),
        ("Отмена до переключения не трогает устройства", CancellationBeforeSwitch),
        ("Отмена при ответе окна не позволяет сохранить раскладку", CancellationAtConfirmation),
        ("Отмена во время ожидания звука откатывает обе подсистемы", CancellationDuringAudio),
        ("Отмена во время смены дисплея ждёт операцию и откат", ExitDuringDisplay),
        ("Выход при подтверждении ждёт восстановления дисплея и звука", ExitWaitsForRollback),
        ("Ошибка отката блокирует выход и не теряется", RollbackFailureBlocksExit),
        ("Координатор отклоняет одновременный запуск", CoordinatorRejectsOverlap),
        ("Синхронное завершение освобождает координатор", CoordinatorSynchronousCompletion)
    ];

    private sealed class InputRig : IDisposable
    {
        internal readonly DispatcherQueue Dispatcher = new();
        internal readonly ControllerMonitor Monitor;
        internal long Now;
        internal static ControllerDevice Selected(uint buttons = 0) =>
            new("selected", "Gamepad", "fingerprint", 1, 2, buttons, [], default);

        internal InputRig()
        {
            GameInputService.Devices = [];
            Monitor = new ControllerMonitor(Dispatcher) { Timestamp = () => Now };
            Monitor.Configure("selected", "fingerprint", "A", "View");
        }

        internal void Read(uint buttons = 0, bool other = false)
        {
            GameInputService.Devices = other
                ? [Selected(buttons), new("other", "Other", "other-fingerprint", 3, 4, 0, [], default)]
                : [Selected(buttons)];
            Dispatcher.Timer.Fire();
            Check(Monitor.Error is null, Monitor.Error ?? "Unexpected input error");
        }

        internal void Disconnect()
        {
            GameInputService.Devices = [];
            Dispatcher.Timer.Fire();
        }

        internal void HoldHotkey()
        {
            Read();
            Read(0x6);
            // Advance the hotkey's clock without sleeping or touching hardware.
            Now += Stopwatch.Frequency;
            Read(0x6);
        }

        public void Dispose() => Monitor.Stop();
    }

    private static Task ConfirmationOwnsInput()
    {
        using InputRig rig = new();
        int overlayActions = 0, confirmations = 0;
        using var confirmation = rig.Monitor.AcquireInput(ControllerInputMode.Confirmation, () => confirmations++);
        using var overlay = rig.Monitor.AcquireInput(ControllerInputMode.Overlay, () => overlayActions++);
        rig.Read();
        rig.Read(0x4);
        rig.Read();
        Check(confirmations == 1 && overlayActions == 0, "A was delivered to the wrong owner.");
        return Task.CompletedTask;
    }

    private static Task ClosedOwnerStaysClosed()
    {
        using InputRig rig = new();
        int overlayCancel = 0, confirmationCancel = 0, hotkeys = 0;
        var overlay = rig.Monitor.AcquireInput(ControllerInputMode.Overlay, cancel: () => overlayCancel++);
        ControllerInputScope? confirmation = null;
        confirmation = rig.Monitor.AcquireInput(ControllerInputMode.Confirmation, cancel: () =>
        {
            confirmationCancel++;
            confirmation!.Dispose();
        });
        rig.Read();
        overlay.Dispose(); // Window closed through another entry point during confirmation.
        rig.Read();
        rig.Read(0x8);
        rig.Read();
        Check(confirmationCancel == 1 && overlayCancel == 0 && !rig.Monitor.OverlayOpen,
            "Disposed overlay regained ownership or received cancellation.");
        rig.Monitor.HotkeyPressed += () => hotkeys++;
        rig.HoldHotkey();
        Check(hotkeys == 1, "Hotkey did not recover after both windows closed.");
        return Task.CompletedTask;
    }

    private static Task CancelWins()
    {
        using InputRig rig = new();
        int accepted = 0, canceled = 0;
        using var scope = rig.Monitor.AcquireInput(ControllerInputMode.Confirmation, () => accepted++, () => canceled++);
        rig.Read();
        rig.Read(0xC);
        rig.Read();
        Check(canceled == 1 && accepted == 0, "Simultaneous A+B accepted the display.");
        return Task.CompletedTask;
    }

    private static Task UnrelatedHotplug()
    {
        using InputRig rig = new();
        int accepted = 0;
        using var scope = rig.Monitor.AcquireInput(ControllerInputMode.Confirmation, () => accepted++);
        rig.Read(); rig.Read(0x4); rig.Read(0x4, true); rig.Read(0x4);
        rig.Read();
        Check(accepted == 1, "Unrelated device hotplug repeated a held button.");
        rig.Read(); rig.Read(0x4); rig.Read();
        Check(accepted == 2, "Fresh button press was lost.");
        return Task.CompletedTask;
    }

    private static Task ReconnectRequiresRelease()
    {
        using InputRig rig = new();
        int accepted = 0;
        using var scope = rig.Monitor.AcquireInput(ControllerInputMode.Confirmation, () => accepted++);
        rig.Read(); rig.Disconnect(); rig.Read(0x4); rig.Read(0x4);
        Check(accepted == 0, "Held A accepted on reconnect.");
        rig.Read(); rig.Read(0x4); rig.Read();
        Check(accepted == 1, "Released and re-pressed A did not work.");
        return Task.CompletedTask;
    }

    private static Task OwnershipRequiresRelease()
    {
        using InputRig rig = new();
        int accepted = 0;
        using var overlay = rig.Monitor.AcquireInput(ControllerInputMode.Overlay);
        rig.Read(); rig.Read(0x4);
        using var confirmation = rig.Monitor.AcquireInput(ControllerInputMode.Confirmation, () => accepted++);
        rig.Read(0x4);
        Check(accepted == 0, "Held A crossed the ownership boundary.");
        rig.Read(); rig.Read(0x4); rig.Read();
        Check(accepted == 1, "Fresh confirmation was lost.");
        return Task.CompletedTask;
    }

    private static Task ConfirmationSuspendsFlydigi()
    {
        using InputRig rig = new();
        int inputs = 0, accepted = 0;
        using var flydigi = rig.Monitor.AcquireInput(ControllerInputMode.Flydigi, flydigi: (_, _) => inputs++);
        rig.Read(); rig.Read();
        int before = inputs;
        var confirmation = rig.Monitor.AcquireInput(ControllerInputMode.Confirmation, () => accepted++);
        rig.Read(); rig.Read(0x4); rig.Read();
        Check(inputs == before && accepted == 1 && !rig.Monitor.FlydigiControlActive, "Flydigi received modal input.");
        confirmation.Dispose();
        rig.Read(); rig.Read();
        Check(inputs == before + 1 && rig.Monitor.FlydigiControlActive, "Live Flydigi owner was not restored.");
        return Task.CompletedTask;
    }

    private static Task ConfirmationCancelsLearning()
    {
        using InputRig rig = new();
        int learned = 0, accepted = 0;
        rig.Monitor.LearnButton(_ => learned++);
        using var confirmation = rig.Monitor.AcquireInput(ControllerInputMode.Confirmation, () => accepted++);
        rig.Read(); rig.Read(0x4); rig.Read();
        Check(accepted == 1 && learned == 0, "Button learning consumed modal confirmation.");
        return Task.CompletedTask;
    }

    private static async Task CancellationBeforeSwitch()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        int touched = 0;
        await Throws<OperationCanceledException>(() => ProfileSwitchTransaction.ApplyAsync<int, int>(
            () => ++touched, () => touched++, _ => touched++, cancellation: cancellation.Token));
        Check(touched == 0, "Canceled operation touched hardware.");
    }

    private static async Task ExitDuringDisplay()
    {
        ProfileOperationCoordinator coordinator = new();
        var entered = Signal();
        using ManualResetEventSlim release = new();
        bool restored = false, audioChanged = false;
        Task operation = coordinator.RunAsync(cancellation => ProfileSwitchTransaction.ApplyAsync(
            () => "display", () => { entered.TrySetResult(); Check(release.Wait(5000), "Display test timed out."); },
            _ => restored = true, () => "audio", () => audioChanged = true, _ => { }, cancellation: cancellation));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task stop = coordinator.StopAsync();
        Check(!stop.IsCompleted && !restored, "Exit abandoned in-flight native operation.");
        release.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await Throws<OperationCanceledException>(() => operation);
        Check(restored && !audioChanged, "Cancellation did not restore display or changed audio afterwards.");
    }

    private static async Task CancellationAtConfirmation()
    {
        using CancellationTokenSource cancellation = new();
        bool restored = false, finished = false;
        await Throws<OperationCanceledException>(() => ProfileSwitchTransaction.ApplyAsync<int, int>(
            () => 0, () => { }, _ => restored = true,
            confirmDisplay: () => { cancellation.Cancel(); return Task.FromResult(true); },
            finish: () => finished = true, cancellation: cancellation.Token));
        Check(restored && !finished, "Confirmation raced shutdown and committed the profile.");
    }

    private static async Task CancellationDuringAudio()
    {
        ProfileOperationCoordinator coordinator = new();
        var audioEntered = Signal();
        bool displayRestored = false, audioRestored = false, confirmationShown = false;
        Task operation = coordinator.RunAsync(cancellation => ProfileSwitchTransaction.ApplyAsync(
            () => "display", () => { }, _ => displayRestored = true,
            () => "audio", () =>
            {
                audioEntered.TrySetResult();
                Check(cancellation.WaitHandle.WaitOne(5000), "Audio cancellation test timed out.");
                cancellation.ThrowIfCancellationRequested();
            }, _ => audioRestored = true,
            () => { confirmationShown = true; return Task.FromResult(true); }, cancellation: cancellation));
        await audioEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Throws<OperationCanceledException>(() => operation);
        Check(displayRestored && audioRestored && !confirmationShown, "Audio cancellation skipped rollback or opened confirmation.");
    }

    private static async Task ExitWaitsForRollback()
    {
        ProfileOperationCoordinator coordinator = new();
        var confirming = Signal();
        var restoring = Signal();
        using ManualResetEventSlim releaseRollback = new();
        List<string> calls = [];
        Task operation = coordinator.RunAsync(cancellation => ProfileSwitchTransaction.ApplyAsync(
            () => "display", () => calls.Add("display"),
            _ => { restoring.TrySetResult(); Check(releaseRollback.Wait(5000), "Rollback test timed out."); calls.Add("restore display"); },
            () => "audio", () => calls.Add("audio"), _ => calls.Add("restore audio"),
            async () => { confirming.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellation); return true; },
            () => calls.Add("finish"), cancellation));
        await confirming.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task stop = coordinator.StopAsync();
        await restoring.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!stop.IsCompleted, "Exit did not wait for rollback.");
        await Throws<InvalidOperationException>(() => coordinator.RunAsync(_ => Task.CompletedTask));
        releaseRollback.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await Throws<OperationCanceledException>(() => operation);
        Check(calls.SequenceEqual(["display", "audio", "restore display", "restore audio"]),
            "Shutdown skipped rollback or committed a canceled profile.");
        Check(!coordinator.IsRunning && coordinator.IsStopping, "Shutdown coordinator has invalid final state.");
    }

    private static async Task RollbackFailureBlocksExit()
    {
        ProfileOperationCoordinator coordinator = new();
        var confirming = Signal();
        Task operation = coordinator.RunAsync(cancellation => ProfileSwitchTransaction.ApplyAsync<int, int>(
            () => 0, () => { }, _ => throw new InvalidOperationException("rollback failed"),
            confirmDisplay: async () => { confirming.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellation); return true; },
            cancellation: cancellation));
        await confirming.Task.WaitAsync(TimeSpan.FromSeconds(5));
        InvalidOperationException error = await Throws<InvalidOperationException>(() => coordinator.StopAsync());
        await Throws<InvalidOperationException>(() => operation);
        Check(error.InnerException is AggregateException && error.Message.Contains("rollback failed"), "Rollback failure was lost.");
        Check(!coordinator.IsStopping && !coordinator.IsRunning, "Application cannot recover after failed exit.");
        await coordinator.RunAsync(_ => Task.CompletedTask);
    }

    private static async Task CoordinatorRejectsOverlap()
    {
        ProfileOperationCoordinator coordinator = new();
        var release = Signal();
        Task first = coordinator.RunAsync(_ => release.Task);
        await Throws<InvalidOperationException>(() => coordinator.RunAsync(_ => Task.CompletedTask));
        release.TrySetResult();
        await first;
        await coordinator.StopAsync();
        await Throws<InvalidOperationException>(() => coordinator.RunAsync(_ => Task.CompletedTask));
    }

    private static async Task CoordinatorSynchronousCompletion()
    {
        ProfileOperationCoordinator coordinator = new();
        Check(await coordinator.RunAsync(_ => Task.FromResult(42)) == 42 && !coordinator.IsRunning, "Synchronous completion leaked active operation.");
        await Throws<InvalidOperationException>(() => coordinator.RunAsync((Func<CancellationToken, Task>)(_ => throw new InvalidOperationException("sync failure"))));
        Check(!coordinator.IsRunning, "Synchronous exception leaked active operation.");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
