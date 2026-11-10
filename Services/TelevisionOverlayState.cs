namespace ControllerHelper.Services;

public enum TelevisionPowerState { Unknown, On, Off }

public sealed record TelevisionOverlayState(bool WakeEnabled, bool NetworkReachable,
    TelevisionPowerState PowerState, bool TelevisionActive, bool MonitorsActive, bool PowerCheckPending = false,
    string? ConnectionWarning = null, bool PowerControlSupported = true, bool PowerOffOnReturn = false);
