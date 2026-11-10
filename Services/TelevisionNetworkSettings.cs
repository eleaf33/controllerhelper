using ControllerHelper.Models;

namespace ControllerHelper.Services;

internal static class TelevisionNetworkSettings
{
    internal static bool SupportsPowerControl(AppSettings settings) =>
        settings.TelevisionNetworkProvider == TelevisionNetworkProvider.LG || HasHisensePowerControl(settings);

    internal static bool HasHisensePowerControl(AppSettings settings) =>
        settings.TelevisionNetworkProvider == TelevisionNetworkProvider.Hisense && settings.HisensePaired;

    internal static bool WakeEnabled(AppSettings settings) => settings.TelevisionNetworkProvider switch
    {
        TelevisionNetworkProvider.LG => settings.WakeTelevision,
        TelevisionNetworkProvider.Hisense => settings.WakeHisense,
        _ => false
    };

    internal static (string? Address, string? Mac) WakeTarget(AppSettings settings) => settings.TelevisionNetworkProvider switch
    {
        TelevisionNetworkProvider.LG => (settings.TelevisionIpAddress, settings.TelevisionMacAddress),
        TelevisionNetworkProvider.Hisense => (settings.HisenseIpAddress, settings.HisenseMacAddress),
        _ => throw new InvalidOperationException("Выберите LG или Hisense в настройках сетевого управления ТВ.")
    };

    internal static TelevisionOverlayState LocalState(AppSettings settings, bool televisionActive, bool monitorsActive)
    {
        bool wake = WakeEnabled(settings);
        bool powerControl = SupportsPowerControl(settings);
        bool webOs = settings.TelevisionNetworkProvider == TelevisionNetworkProvider.LG;
        string? warning = wake && webOs ? WebOsSecurity.SetupWarning(settings) : null;
        bool pending = webOs && wake && warning is null && televisionActive &&
            !string.IsNullOrWhiteSpace(settings.TelevisionClientKey);
        return new(wake, false, TelevisionPowerState.Unknown, televisionActive, monitorsActive,
            pending, warning, powerControl, PowerOffOnReturn: HasHisensePowerControl(settings));
    }

    internal static void RequireLg(AppSettings settings)
    {
        if (settings.TelevisionNetworkProvider != TelevisionNetworkProvider.LG)
            throw new InvalidOperationException("Сопряжение и управление питанием webOS доступны только для LG. Для Hisense используется отдельное сопряжение RemoteNOW.");
    }
}
