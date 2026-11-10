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
            OverlayPrimaryAction.PlayOnTv => "Играть на ТВ",
            OverlayPrimaryAction.ReturnAndPowerOff => "Выключить телевизор и вернуться к мониторам",
            OverlayPrimaryAction.ReturnToPc => "Вернуться к мониторам",
            _ => "ТВ недоступен"
        };
        string primaryText = action switch
        {
            OverlayPrimaryAction.PlayOnTv => "Играть\nна ТВ",
            OverlayPrimaryAction.ReturnAndPowerOff => "Выключить ТВ\nи вернуться\nк мониторам",
            OverlayPrimaryAction.ReturnToPc => "Вернуться к\nмониторам",
            _ => "ТВ\nнедоступен"
        };
        string? warning = null;
        if (state.PowerControlSupported && !state.PowerOffOnReturn && !state.PowerCheckPending && state.WakeEnabled && state.PowerState == TelevisionPowerState.Unknown && state.TelevisionActive)
            warning = state.NetworkReachable
                ? "Питание ТВ не определено: для выключения по сети сопрягите его во вкладке ТВ."
                : "Питание ТВ не подтверждено. Вернуться к мониторам можно без сетевого выключения; проверьте сеть, сертификат и сопряжение ТВ.";
        else if (action == OverlayPrimaryAction.None && state.PowerControlSupported && !state.PowerOffOnReturn && state.WakeEnabled)
            warning = state.NetworkReachable
                ? "Не удалось определить состояние ТВ. Сопрягите его во вкладке ТВ."
                : "Телевизор не отвечает по сети. Проверьте подключение.";

        warning = state.ConnectionWarning ?? warning;
        return new OverlayPresentation(action, primaryLabel, primaryText,
            state.MonitorsActive ? "Выключить мониторы" : "Включить мониторы",
            state.MonitorsActive ? "Выключить\nмониторы" : "Включить\nмониторы",
            state.TelevisionActive ? "Продолжить игру" : "Закрыть оверлей",
            state.TelevisionActive ? "Продолжить\nигру" : "Закрыть\nоверлей",
            state.TelevisionActive, warning);
    }
}
