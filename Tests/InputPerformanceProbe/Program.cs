using System.Diagnostics;
using System.Text.Json;
using ControllerHelper.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new ProbeApp();
        });
    }
}

internal sealed class ProbeApp : Application
{
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            string settingsPath = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "ControllerHelper", "settings.json");
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            string? id = settings.RootElement.GetProperty("ControllerDeviceId").GetString();
            string? fingerprint = settings.RootElement.GetProperty("ControllerFingerprint").GetString();
            using GameInputService input = new();
            await Task.Delay(1000);
            foreach (var mode in new[] {
                (Name: "background", Raw: false, Axes: false, Interval: ControllerPollingCadence.Background),
                (Name: "overlay", Raw: false, Axes: false, Interval: ControllerPollingCadence.Interactive),
                (Name: "raw-buttons", Raw: true, Axes: false, Interval: ControllerPollingCadence.Interactive),
                (Name: "Flydigi", Raw: false, Axes: true, Interval: ControllerPollingCadence.Interactive) })
            {
                for (int i = 0; i < 20; i++) input.ReadDevices(id, fingerprint, mode.Raw, mode.Axes);
                List<double> reads = [], intervals = [];
                long allocated = 0, previous = 0;
                int valid = 0;
                using Process process = Process.GetCurrentProcess();
                TimeSpan cpu = process.TotalProcessorTime;
                Stopwatch wall = Stopwatch.StartNew();
                TaskCompletionSource done = new();
                var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                timer.Interval = mode.Interval;
                timer.Tick += (_, _) =>
                {
                    try
                    {
                        long start = Stopwatch.GetTimestamp();
                        if (previous != 0) intervals.Add(Stopwatch.GetElapsedTime(previous, start).TotalMilliseconds);
                        previous = start;
                        long bytes = GC.GetAllocatedBytesForCurrentThread();
                        var devices = input.ReadDevices(id, fingerprint, mode.Raw, mode.Axes);
                        allocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
                        reads.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                        if (ControllerSelection.Resolve(devices, id, fingerprint)?.HasReading == true) valid++;
                        if (wall.Elapsed >= TimeSpan.FromSeconds(10)) { timer.Stop(); done.TrySetResult(); }
                    }
                    catch (Exception error) { timer.Stop(); done.TrySetException(error); }
                };
                timer.Start();
                await done.Task;
                Console.WriteLine(JsonSerializer.Serialize(new {
                    mode = mode.Name, requestedIntervalMs = mode.Interval.TotalMilliseconds,
                    samples = reads.Count, validReadings = valid,
                    readMeanMs = reads.Average(), readP95Ms = Percentile(reads, .95),
                    readP99Ms = Percentile(reads, .99), readMaxMs = reads.Max(),
                    readAllocatedBytesPerPoll = (double)allocated / reads.Count,
                    intervalP95Ms = Percentile(intervals, .95), intervalMaxMs = intervals.Max(),
                    processCpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                    elapsedMs = wall.Elapsed.TotalMilliseconds,
                    scope = "WinUI DispatcherQueueTimer + native GameInput; no visible window or UI workload"
                }));
                if (valid == 0) throw new InvalidOperationException("No valid selected-controller readings.");
            }
        }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
        finally { Exit(); }
    }

    private static double Percentile(List<double> samples, double percentile)
    {
        double[] sorted = samples.Order().ToArray();
        return sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * percentile) - 1)];
    }
}
