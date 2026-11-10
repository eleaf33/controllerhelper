using ControllerHelper.Models;

namespace ControllerHelper.Services;

internal sealed record HisenseTarget(string Display, string Address, string Mac, string Client, string Certificate,
    TelevisionNetworkProvider Provider)
{
    internal static HisenseTarget From(AppSettings s) => new(s.TelevisionId ?? "",
        (s.HisenseIpAddress ?? "").Trim(), (s.HisenseMacAddress ?? "").Replace(":", "").Replace("-", "").Replace(" ", "").ToUpperInvariant(),
        s.HisenseClientId ?? "", WebOsSecurity.NormalizeFingerprint(s.HisenseCertificateSha256), s.TelevisionNetworkProvider);

    internal void EnsureCurrent(AppSettings settings)
    {
        if (From(settings) != this) throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("HisenseSettings_137"));
    }
}

internal static class HisenseSettings
{
    internal static void Reconcile(AppSettings previous, AppSettings candidate)
    {
        HisenseTarget old = HisenseTarget.From(previous), next = HisenseTarget.From(candidate);
        if (old.Display != next.Display || old.Address != next.Address || old.Mac != next.Mac)
        {
            candidate.HisenseCertificateSha256 = null;
            candidate.HisenseClientId = null;
            candidate.HisenseInputId = candidate.HisenseInputName = null;
            candidate.HisensePaired = false;
            ClearAuthentication(candidate);
        }
        else if (old.Client != next.Client || old.Certificate != next.Certificate)
        {
            candidate.HisensePaired = false;
            ClearAuthentication(candidate);
        }
    }

    internal static void ClearAuthentication(AppSettings settings)
    {
        settings.HisensePreferredAuthMethod = null;
        settings.HisenseAuthentication = null;
        settings.HisenseAuthenticationProtected = null;
    }

    internal static void Validate(AppSettings settings)
    {
        if (settings.TelevisionNetworkProvider != TelevisionNetworkProvider.Hisense)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("HisenseSettings_138"));
        TelevisionWakeService.ParseAddress(settings.HisenseIpAddress);
        if (settings.HisenseClientId is not { Length: >= 8 and <= 64 } client ||
            client.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("HisenseSettings_139"));
        if (!WebOsSecurity.HasFingerprint(settings.HisenseCertificateSha256))
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("HisenseSettings_140"));
    }
}
