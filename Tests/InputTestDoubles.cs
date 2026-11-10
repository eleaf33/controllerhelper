// Only compiled into the test runner. No real devices, timers or windows are used.
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public DispatcherQueueTimer Timer { get; } = new();
        public DispatcherQueueTimer CreateTimer() => Timer;
    }

    public sealed class DispatcherQueueTimer
    {
        public bool IsRunning { get; private set; }
        public TimeSpan Interval { get; set; }
        public event Action<object?, object?>? Tick;
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Fire() => Tick?.Invoke(this, null);
    }
}

namespace ControllerHelper.Services
{
    internal sealed class GameInputService : IDisposable
    {
        internal static ControllerDevice[] Devices = [];
        internal static GameInputService? LastCreated;
        internal bool Exclusive { get; private set; }
        internal bool Disposed { get; private set; }
        internal GameInputService() => LastCreated = this;
        internal void SetExclusiveForegroundInput(bool enabled)
        {
            if (Disposed) throw new ObjectDisposedException(nameof(GameInputService));
            Exclusive = enabled;
        }
        internal string? Error => null;
        internal bool RawRequested { get; private set; }
        internal ControllerDevice[] ReadDevices(string? id, string? fingerprint, bool raw, bool axes)
        {
            RawRequested = raw;
            return Devices;
        }
        public void Dispose() { Exclusive = false; Disposed = true; }
    }
}
