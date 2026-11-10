using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ControllerHelper.Services;

internal sealed class FlydigiCursorPump
{
    private const int TickMilliseconds = 5;
    private Thread? _thread;
    private volatile bool _running;
    private float _leftX;
    private float _leftY;
    private long _lastReadingTimestamp;
    private double _speedMultiplier = 1.0;

    internal void SetSpeed(double multiplier) => Volatile.Write(ref _speedMultiplier, Math.Clamp(multiplier, 0.25, 2.5));

    internal void Start()
    {
        if (_thread is not null) return;
        Volatile.Write(ref _leftX, 0);
        Volatile.Write(ref _leftY, 0);
        Volatile.Write(ref _lastReadingTimestamp, 0);
        _running = true;
        Thread thread = new(Run) { IsBackground = true, Name = "Flydigi cursor" };
        _thread = thread;
        try { thread.Start(); }
        catch
        {
            _running = false;
            _thread = null;
            throw;
        }
    }

    internal void Update(ControllerAxes axes)
    {
        Volatile.Write(ref _leftX, axes.LeftX);
        Volatile.Write(ref _leftY, axes.LeftY);
        Volatile.Write(ref _lastReadingTimestamp, Stopwatch.GetTimestamp());
    }

    internal void Stop()
    {
        Thread? thread = _thread;
        if (thread is null) return;
        _running = false;
        thread.Join();
        _thread = null;
    }

    private void Run()
    {
        try { RunCore(); }
        catch (Exception error) { StartupTrace.Write($"Flydigi cursor pump failed: {error}"); }
    }

    private void RunCore()
    {
        SafeWaitHandle timer = CreateWaitableTimerEx(0, null, 0x2, 0x001F0003);
        bool highResolution = !timer.IsInvalid;
        if (timer.IsInvalid)
        {
            timer.Dispose();
            timer = CreateWaitableTimerEx(0, null, 0, 0x001F0003);
        }
        bool requestedTimerResolution = !highResolution && timeBeginPeriod(1) == 0;
        try
        {
            using (timer)
            {
                long dueTime = -TickMilliseconds * 10_000L;
                bool timerReady = !timer.IsInvalid && SetWaitableTimer(timer, ref dueTime,
                    TickMilliseconds, 0, 0, false);
                if (!timerReady && !requestedTimerResolution)
                    requestedTimerResolution = timeBeginPeriod(1) == 0;
                long previousTimestamp = Stopwatch.GetTimestamp();
                double horizontalRemainder = 0;
                double verticalRemainder = 0;
                while (_running)
                {
                    if (timerReady)
                    {
                        if (WaitForSingleObject(timer, 25) != 0)
                        {
                            timerReady = false;
                            if (!requestedTimerResolution)
                                requestedTimerResolution = timeBeginPeriod(1) == 0;
                            continue;
                        }
                    }
                    else Thread.Sleep(TickMilliseconds);

                    long timestamp = Stopwatch.GetTimestamp();
                    double elapsed = Math.Min(Stopwatch.GetElapsedTime(previousTimestamp, timestamp).TotalSeconds, 0.02);
                    previousTimestamp = timestamp;
                    long readingTimestamp = Volatile.Read(ref _lastReadingTimestamp);
                    if (!_running || readingTimestamp == 0 ||
                        Stopwatch.GetElapsedTime(readingTimestamp, timestamp) > TimeSpan.FromMilliseconds(100)) continue;

                    double multiplier = Volatile.Read(ref _speedMultiplier);
                    horizontalRemainder += Speed(Volatile.Read(ref _leftX)) * multiplier * elapsed;
                    verticalRemainder -= Speed(Volatile.Read(ref _leftY)) * multiplier * elapsed;
                    int horizontalPixels = (int)horizontalRemainder;
                    int verticalPixels = (int)verticalRemainder;
                    horizontalRemainder -= horizontalPixels;
                    verticalRemainder -= verticalPixels;
                    if ((horizontalPixels != 0 || verticalPixels != 0) && GetCursorPos(out Point point))
                        SetCursorPos(point.X + horizontalPixels, point.Y + verticalPixels);
                }
                if (timerReady) CancelWaitableTimer(timer);
            }
        }
        finally { if (requestedTimerResolution) timeEndPeriod(1); }
    }

    private static float Speed(float value)
    {
        float magnitude = Math.Abs(value);
        if (magnitude < 0.16f) return 0;
        float normalized = Math.Clamp((magnitude - 0.16f) / 0.84f, 0, 1);
        return Math.Sign(value) * (250 * normalized + 1450 * normalized * normalized);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X, Y; }

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period,
        nint completionRoutine, nint argument, bool resume);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int horizontal, int vertical);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
}
