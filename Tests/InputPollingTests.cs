using ControllerHelper.Services;
using Microsoft.UI.Dispatching;

internal static class InputPollingTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Опрос ускоряется сразу для открытого окна и замедляется после последнего", WindowCadence),
        ("Обучение ускоряет опрос и возвращает фоновый режим после кнопки", LearningCadence),
        ("Отмена обучения и Stop не оставляют ускоренный работающий таймер", StopAndConfigure)
    ];

    private sealed class Rig : IDisposable
    {
        internal readonly DispatcherQueue Queue = new();
        internal readonly ControllerMonitor Monitor;
        internal Rig()
        {
            GameInputService.Devices = [];
            Monitor = new(Queue);
            Configure();
        }
        internal void Configure() => Monitor.Configure("selected", "fingerprint", "A", "View");
        internal void Read(uint buttons = 0)
        {
            GameInputService.Devices = [new("selected", "Pad", "fingerprint", 1, 2, buttons, [], default)];
            Queue.Timer.Fire();
            Check(Monitor.Error is null, Monitor.Error ?? "Read failed");
        }
        internal void Expect(TimeSpan interval) => Check(Queue.Timer.Interval == interval, "Wrong polling interval");
        public void Dispose() => Monitor.Stop();
    }

    private static Task WindowCadence()
    {
        using Rig rig = new();
        rig.Expect(ControllerPollingCadence.Background);
        var overlay = rig.Monitor.AcquireInput(ControllerInputMode.Overlay);
        rig.Expect(ControllerPollingCadence.Interactive); // No tick needed to change cadence.
        var confirmation = rig.Monitor.AcquireInput(ControllerInputMode.Confirmation);
        overlay.SetMode(ControllerInputMode.Flydigi);
        rig.Read();
        rig.Expect(ControllerPollingCadence.Interactive);
        overlay.Dispose();
        rig.Expect(ControllerPollingCadence.Interactive);
        confirmation.Dispose();
        rig.Expect(ControllerPollingCadence.Background);
        return Task.CompletedTask;
    }

    private static Task LearningCadence()
    {
        using Rig rig = new();
        int learned = 0;
        rig.Monitor.LearnButton(button => { Check(button == "X", "Wrong learned button"); learned++; });
        rig.Expect(ControllerPollingCadence.Interactive);
        rig.Read(); rig.Read(); rig.Read(0x10); rig.Read(0x10);
        Check(learned == 1, "Learning was lost or repeated");
        rig.Expect(ControllerPollingCadence.Background);
        return Task.CompletedTask;
    }

    private static Task StopAndConfigure()
    {
        using Rig rig = new();
        rig.Monitor.LearnButton(_ => throw new Exception("Cancelled learning fired"));
        rig.Configure();
        rig.Expect(ControllerPollingCadence.Background);
        rig.Read(); rig.Read(); rig.Read(0x10);
        var overlay = rig.Monitor.AcquireInput(ControllerInputMode.Overlay);
        rig.Monitor.Stop();
        overlay.Dispose();
        rig.Queue.Timer.Fire();
        Check(!rig.Queue.Timer.IsRunning, "Shutdown restarted the timer");
        try { rig.Monitor.LearnButton(_ => { }); }
        catch (ObjectDisposedException) { return Task.CompletedTask; }
        throw new Exception("Learning accepted after shutdown");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
