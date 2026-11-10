namespace ControllerHelper.Services;

internal enum OverlayPrimaryAction { None, PlayOnTv, ReturnToPc, ReturnAndPowerOff }

internal static class OverlayActionPolicy
{
    internal static OverlayPrimaryAction Resolve(TelevisionOverlayState state)
    {
        // Hisense checks its live network state during the return operation.
        // An active HDMI path alone never authorizes sending a power toggle.
        if (state.TelevisionActive && state.PowerControlSupported && state.PowerOffOnReturn)
            return OverlayPrimaryAction.ReturnAndPowerOff;
        if (state.PowerControlSupported && state.WakeEnabled && state.MonitorsActive && state.PowerState == TelevisionPowerState.Off)
            return OverlayPrimaryAction.PlayOnTv;
        if (state.TelevisionActive)
            return state.PowerControlSupported && state.WakeEnabled && state.PowerState == TelevisionPowerState.On && !state.MonitorsActive
                ? OverlayPrimaryAction.ReturnAndPowerOff
                : OverlayPrimaryAction.ReturnToPc;
        // Local HDMI switching does not require network power control.
        // The profile switch checks the selected display before applying a topology.
        return state.MonitorsActive
            ? OverlayPrimaryAction.PlayOnTv
            : OverlayPrimaryAction.None;
    }
}
