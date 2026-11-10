namespace ControllerHelper.Services;

internal sealed record OverlayPresentation(OverlayPrimaryAction PrimaryAction,
    string PrimaryLabel, string PrimaryText, string MonitorLabel, string MonitorText,
    string CloseLabel, string CloseText, bool MonitorEnabled, string? Warning)
{
    internal static OverlayPresentation FromState(TelevisionOverlayState state)
    {
        OverlayPrimaryAction action = OverlayActionPolicy.Resolve(state);
        string primaryLabel = action switch
        {
            OverlayPrimaryAction.PlayOnTv => ControllerHelper.Localization.Text.Get("TrayIcon_022"),
            OverlayPrimaryAction.ReturnAndPowerOff => ControllerHelper.Localization.Text.Get("OverlayPresentation_145"),
            OverlayPrimaryAction.ReturnToPc => ControllerHelper.Localization.Text.Get("OverlayPresentation_146"),
            _ => ControllerHelper.Localization.Text.Get("OverlayPresentation_147")
        };
        string primaryText = action switch
        {
            OverlayPrimaryAction.PlayOnTv => ControllerHelper.Localization.Text.Get("OverlayPresentation_148"),
            OverlayPrimaryAction.ReturnAndPowerOff => ControllerHelper.Localization.Text.Get("OverlayPresentation_149"),
            OverlayPrimaryAction.ReturnToPc => ControllerHelper.Localization.Text.Get("OverlayPresentation_150"),
            _ => ControllerHelper.Localization.Text.Get("OverlayPresentation_151")
        };
        string? warning = null;
        if (state.PowerControlSupported && !state.PowerOffOnReturn && !state.PowerCheckPending && state.WakeEnabled && state.PowerState == TelevisionPowerState.Unknown && state.TelevisionActive)
            warning = state.NetworkReachable
                ? ControllerHelper.Localization.Text.Get("OverlayPresentation_152")
                : ControllerHelper.Localization.Text.Get("OverlayPresentation_153");
        else if (action == OverlayPrimaryAction.None && state.PowerControlSupported && !state.PowerOffOnReturn && state.WakeEnabled)
            warning = state.NetworkReachable
                ? ControllerHelper.Localization.Text.Get("OverlayPresentation_154")
                : ControllerHelper.Localization.Text.Get("OverlayPresentation_155");

        warning = state.ConnectionWarning ?? warning;
        return new OverlayPresentation(action, primaryLabel, primaryText,
            state.MonitorsActive ? ControllerHelper.Localization.Text.Get("OverlayPresentation_156") : ControllerHelper.Localization.Text.Get("OverlayPresentation_157"),
            state.MonitorsActive ? ControllerHelper.Localization.Text.Get("OverlayPresentation_158") : ControllerHelper.Localization.Text.Get("OverlayPresentation_159"),
            state.TelevisionActive ? ControllerHelper.Localization.Text.Get("OverlayPresentation_160") : ControllerHelper.Localization.Text.Get("OverlayPresentation_161"),
            state.TelevisionActive ? ControllerHelper.Localization.Text.Get("OverlayPresentation_162") : ControllerHelper.Localization.Text.Get("OverlayPresentation_163"),
            state.TelevisionActive, warning);
    }
}
