namespace ControllerHelper.Services;

internal static class ControllerPollingCadence
{
    internal static readonly TimeSpan Background = TimeSpan.FromMilliseconds(40);
    // 16 ms crossed a timer-resolution boundary on the measured Windows setup
    // (~31 ms between ticks). 15 ms is a requested interval, not a deadline.
    internal static readonly TimeSpan Interactive = TimeSpan.FromMilliseconds(15);
}
