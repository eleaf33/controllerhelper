using ControllerHelper.Models;

namespace ControllerHelper.Services;

internal sealed record TelevisionPairingTarget(string DisplayId, string Address, string Mac, bool Insecure, string Certificate,
    TelevisionNetworkProvider Provider = TelevisionNetworkProvider.LG)
{
    internal static TelevisionPairingTarget From(AppSettings settings) =>
        Create(settings.TelevisionId, settings.TelevisionIpAddress, settings.TelevisionMacAddress,
            settings.AllowInsecureWebOs, settings.TelevisionCertificateSha256, settings.TelevisionNetworkProvider);

    internal static TelevisionPairingTarget Create(string? displayId, string? address, string? mac,
        bool insecure = false, string? certificate = null, TelevisionNetworkProvider provider = TelevisionNetworkProvider.LG) =>
        new(Normalize(displayId), Normalize(address),
            Normalize(mac).Replace(":", "").Replace("-", "").Replace(" ", ""), insecure,
            WebOsSecurity.NormalizeFingerprint(certificate), provider);

    internal bool SameDevice(TelevisionPairingTarget other) =>
        Provider == other.Provider && DisplayId == other.DisplayId && Address == other.Address && Mac == other.Mac;

    internal AppSettings ApplyKey(AppSettings current, string key)
    {
        TelevisionNetworkSettings.RequireLg(current);
        if (From(current) != this)
            throw new InvalidOperationException("Параметры телевизора изменились. Повторите сопряжение для выбранного ТВ.");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Телевизор не выдал ключ сопряжения.");
        AppSettings updated = current.Copy();
        updated.TelevisionClientKey = key;
        updated.TelevisionClientKeyProtected = null;
        return updated;
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}
