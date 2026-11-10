using ControllerHelper.Services;
using Microsoft.UI.Dispatching;
using System.Diagnostics;

internal static class InputIsolationTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Хоткей выдерживает 500 мс и требует нового нажатия", HotkeyThreshold),
        ("Эксклюзивная политика сохраняется при вложенных окнах и Flydigi", NestedScopes),
        ("Stop снимает политику, позднее закрытие окна безопасно", StopWithOpenWindow),
        ("A выполняется один раз после отпускания всех кнопок", ConfirmOnRelease),
        ("B отменяет ожидающую A, даже если нажата позже", StaggeredCancel),
        ("Фоновое окно не принимает кнопки, возврат требует отпускания", BackgroundInput),
        ("Потеря фокуса отменяет отложенное подтверждение", FocusLoss),
        ("Ошибка чтения не считается отпусканием A", UnavailableReading),
        ("Отключение геймпада отменяет отложенную A", Disconnect),
        ("Закрытие владельца не передаёт отложенную A другому окну", OwnerChange),
        ("Хоткей отменяет отложенную A", HotkeyCancelsAction),
        ("Потеря фокуса и модальное окно сбрасывают ввод Flydigi", SuspendFlydigi),
        ("Хоткей повторно активирует оверлей без мыши и не запускает A", RecoverUnfocusedOverlay),
        ("Хоткей не обходит подтверждение без фокуса", UnfocusedConfirmation),
        ("Невалидное чтение не активирует оверлей хоткеем", InvalidHotkeyReading)
    ];

    private sealed class Rig : IDisposable
    {
        internal readonly DispatcherQueue Queue = new();
        internal readonly ControllerMonitor Monitor;
        internal readonly GameInputService Input;
        internal bool Focused = true;
        internal long Now;

        internal Rig()
        {
            GameInputService.Devices = [];
            Monitor = new(Queue) { Timestamp = () => Now };
            Input = GameInputService.LastCreated!;
            Monitor.Configure("selected", "fingerprint", "A", "View");
        }

        internal ControllerInputScope Open(Action? confirm = null, Action? cancel = null) =>
            Monitor.AcquireInput(ControllerInputMode.Overlay, confirm, cancel, hasFocus: () => Focused);

        internal void Read(uint buttons = 0, bool valid = true)
        {
            GameInputService.Devices =
            [new("selected", "Gamepad", "fingerprint", 1, 2, buttons, [], default) { HasReading = valid }];
            Queue.Timer.Fire();
            Check(Monitor.Error is null, Monitor.Error ?? "Input error");
        }

        internal void AdvanceHotkeyClock() => Now += Stopwatch.Frequency;

        public void Dispose() => Monitor.Stop();
    }

    private static Task HotkeyThreshold()
    {
        using Rig rig = new();
        int activations = 0;
        rig.Monitor.HotkeyPressed += () => activations++;
        rig.Read(); rig.Read(0x6);
        rig.Now = Stopwatch.Frequency * 499 / 1000; rig.Read(0x6);
        Check(activations == 0, "Hotkey fired before threshold");
        rig.Now = Stopwatch.Frequency / 2; rig.Read(0x6);
        Check(activations == 1, "Hotkey missed threshold");
        rig.AdvanceHotkeyClock(); rig.Read(0x6);
        Check(activations == 1, "Held hotkey repeated");
        rig.Read(); rig.Read(0x6); rig.AdvanceHotkeyClock(); rig.Read(0x6);
        Check(activations == 2, "Release did not rearm hotkey");
        return Task.CompletedTask;
    }

    private static Task NestedScopes()
    {
        using Rig rig = new();
        Check(!rig.Input.Exclusive, "Background hotkeys acquired exclusivity.");
        var overlay = rig.Open();
        Check(rig.Input.Exclusive, "Opening the overlay did not acquire policy synchronously.");
        var confirmation = rig.Monitor.AcquireInput(ControllerInputMode.Confirmation);
        overlay.SetMode(ControllerInputMode.Flydigi);
        overlay.Dispose();
        Check(rig.Input.Exclusive, "Closing a covered owner released policy.");
        confirmation.Dispose();
        Check(!rig.Input.Exclusive, "Last window did not restore background policy.");
        confirmation.Dispose();
        return Task.CompletedTask;
    }

    private static Task StopWithOpenWindow()
    {
        using Rig rig = new();
        var overlay = rig.Open();
        rig.Monitor.Stop();
        overlay.Dispose();
        rig.Queue.Timer.Fire();
        Check(!rig.Input.Exclusive && rig.Input.Disposed, "Shutdown retained input ownership.");
        try { rig.Open(); }
        catch (ObjectDisposedException) { return Task.CompletedTask; }
        throw new Exception("Stopped monitor acquired another window.");
    }

    private static Task ConfirmOnRelease()
    {
        using Rig rig = new();
        int actions = 0;
        using var scope = rig.Open(() => actions++);
        rig.Read(); rig.Read(0x4); rig.Read(0x4);
        Check(actions == 0 && rig.Input.Exclusive, "Action executed while A was held.");
        rig.Read(); rig.Read();
        Check(actions == 1, "Release was lost or repeated.");
        return Task.CompletedTask;
    }

    private static Task StaggeredCancel()
    {
        using Rig rig = new();
        int accepted = 0, canceled = 0;
        using var scope = rig.Open(() => accepted++, () => canceled++);
        rig.Read(); rig.Read(0x4); rig.Read(0xC); rig.Read(0x4);
        Check(accepted == 0 && canceled == 0, "Action ran before complete release.");
        rig.Read();
        Check(accepted == 0 && canceled == 1, "B did not override pending A.");
        return Task.CompletedTask;
    }

    private static Task BackgroundInput()
    {
        using Rig rig = new();
        int actions = 0, moves = 0;
        using var scope = rig.Monitor.AcquireInput(ControllerInputMode.Overlay, () => actions++,
            move: _ => moves++, hasFocus: () => rig.Focused);
        rig.Read(); rig.Focused = false;
        rig.Read(0x104); rig.Read(); rig.Read(0x4);
        rig.Focused = true;
        rig.Read(0x4); rig.Read();
        Check(actions == 0 && moves == 0, "Background input or held A was handled.");
        rig.Read(0x4); rig.Read();
        Check(actions == 1, "New foreground press was lost.");
        return Task.CompletedTask;
    }

    private static Task FocusLoss()
    {
        using Rig rig = new();
        int actions = 0;
        using var scope = rig.Open(() => actions++);
        rig.Read(); rig.Read(0x4);
        rig.Focused = false; rig.Read(0x4);
        rig.Focused = true; rig.Read(); rig.Read();
        Check(actions == 0, "Pending A survived focus loss.");
        return Task.CompletedTask;
    }

    private static Task UnavailableReading()
    {
        using Rig rig = new();
        int actions = 0;
        using var scope = rig.Open(() => actions++);
        rig.Read(); rig.Read(0x4); rig.Read(valid: false); rig.Read(); rig.Read();
        Check(actions == 0, "Failed native reading became a confirmation.");
        rig.Read(0x4); rig.Read();
        Check(actions == 1, "Input did not recover after a valid neutral reading.");
        return Task.CompletedTask;
    }

    private static Task Disconnect()
    {
        using Rig rig = new();
        int actions = 0;
        using var scope = rig.Open(() => actions++);
        rig.Read(); rig.Read(0x4);
        GameInputService.Devices = [];
        rig.Queue.Timer.Fire();
        rig.Read(); rig.Read();
        Check(actions == 0, "Disconnect accepted pending A.");
        return Task.CompletedTask;
    }

    private static Task OwnerChange()
    {
        using Rig rig = new();
        int actions = 0;
        using var overlay = rig.Open(() => actions++);
        var modal = rig.Monitor.AcquireInput(ControllerInputMode.Confirmation, () => actions++);
        rig.Read(); rig.Read(0x4); modal.Dispose(); rig.Read(); rig.Read();
        Check(actions == 0, "Pending action crossed ownership boundary.");
        Check(rig.Input.Exclusive, "Closing the modal released the overlay's policy.");
        return Task.CompletedTask;
    }

    private static Task HotkeyCancelsAction()
    {
        using Rig rig = new();
        int actions = 0;
        using var scope = rig.Open(() => actions++);
        rig.Read(); rig.Read(0x4); rig.Read(0x6); rig.Read(0x4); rig.Read();
        Check(actions == 0, "Overlay hotkey became an A action.");
        return Task.CompletedTask;
    }

    private static Task SuspendFlydigi()
    {
        using Rig rig = new();
        bool moving = false;
        using var flydigi = rig.Monitor.AcquireInput(ControllerInputMode.Flydigi,
            flydigi: (_, _) => moving = true, hasFocus: () => rig.Focused, suspend: () => moving = false);
        rig.Read(); rig.Read();
        Check(moving, "Flydigi input never started.");
        rig.Focused = false; rig.Read();
        Check(!moving, "Pointer continued after focus loss.");
        rig.Focused = true; rig.Read(); rig.Read();
        using var modal = rig.Monitor.AcquireInput(ControllerInputMode.Confirmation);
        Check(!moving, "Modal confirmation did not stop the pointer synchronously.");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static Task RecoverUnfocusedOverlay()
    {
        using Rig rig = new();
        int activations = 0, actions = 0;
        using var overlay = rig.Open(() => actions++);
        rig.Focused = false;
        rig.Monitor.HotkeyPressed += () => { activations++; rig.Focused = true; };
        rig.Read(); rig.Read(0x6); rig.AdvanceHotkeyClock(); rig.Read(0x6);
        Check(activations == 1, "Unfocused overlay swallowed the global hotkey.");
        rig.Read(0x6); rig.Read(0x4); rig.Read();
        Check(activations == 1 && actions == 0, "Held recovery chord repeated or became A.");
        rig.Read(0x4); rig.Read();
        Check(actions == 1, "Menu did not resume after focus recovery and release.");
        return Task.CompletedTask;
    }

    private static Task UnfocusedConfirmation()
    {
        using Rig rig = new();
        int activations = 0, confirmations = 0;
        using var modal = rig.Monitor.AcquireInput(ControllerInputMode.Confirmation, () => confirmations++,
            hasFocus: () => rig.Focused);
        rig.Focused = false;
        rig.Monitor.HotkeyPressed += () => activations++;
        rig.Read(); rig.Read(0x6); rig.AdvanceHotkeyClock(); rig.Read(0x6); rig.Read();
        Check(activations == 0 && confirmations == 0, "Recovery hotkey bypassed the display confirmation.");
        return Task.CompletedTask;
    }

    private static Task InvalidHotkeyReading()
    {
        using Rig rig = new();
        int activations = 0;
        using var overlay = rig.Open();
        rig.Focused = false;
        rig.Monitor.HotkeyPressed += () => activations++;
        rig.Read(); rig.Read(0x6); rig.AdvanceHotkeyClock(); rig.Read(0x6, valid: false);
        rig.Read(0x6); rig.Read(0x6);
        Check(activations == 0, "A failed reading or recovery with held buttons triggered the hotkey.");
        rig.Read(); rig.Read(0x6); rig.AdvanceHotkeyClock(); rig.Read(0x6);
        Check(activations == 1, "Hotkey did not recover after release.");
        return Task.CompletedTask;
    }
}
