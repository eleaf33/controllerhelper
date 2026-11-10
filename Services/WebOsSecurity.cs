using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ControllerHelper.Models;

namespace ControllerHelper.Services;

internal sealed class TelevisionCertificateException(string message) : InvalidOperationException(message);

internal static class WebOsSecurity
{
    internal static string Fingerprint(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    internal static string NormalizeFingerprint(string? fingerprint) =>
        (fingerprint ?? "").Replace(":", "").Replace(" ", "").Trim().ToUpperInvariant();

    internal static bool HasFingerprint(string? fingerprint)
    {
        string normalized = NormalizeFingerprint(fingerprint);
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit);
    }

    internal static bool AcceptCertificate(X509Certificate? certificate, string? expected, DateTimeOffset now)
    {
        if (certificate is null || !HasFingerprint(expected)) return false;
        using X509Certificate2 copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        if (now.UtcDateTime < copy.NotBefore.ToUniversalTime() || now.UtcDateTime > copy.NotAfter.ToUniversalTime()) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(NormalizeFingerprint(expected)),
            SHA256.HashData(copy.RawData));
    }

    internal static Uri Endpoint(AppSettings settings)
    {
        TelevisionNetworkSettings.RequireLg(settings);
        string address = TelevisionWakeService.ParseAddress(settings.TelevisionIpAddress).ToString();
        if (settings.AllowInsecureWebOs) return new Uri($"ws://{address}:3000/");
        if (!HasFingerprint(settings.TelevisionCertificateSha256))
            throw new TelevisionCertificateException(ControllerHelper.Localization.Text.Get("WebOsSecurity_198"));
        return new Uri($"wss://{address}:3001/");
    }

    internal static string? SetupWarning(AppSettings settings) => !settings.AllowInsecureWebOs && !HasFingerprint(settings.TelevisionCertificateSha256)
        ? ControllerHelper.Localization.Text.Get("WebOsSecurity_199")
        : null;

    internal static AppSettings ApplyCertificate(AppSettings current, TelevisionPairingTarget expected, string fingerprint)
    {
        TelevisionNetworkSettings.RequireLg(current);
        if (TelevisionPairingTarget.From(current) != expected || current.AllowInsecureWebOs)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("WebOsSecurity_200"));
        if (!HasFingerprint(fingerprint)) throw new TelevisionCertificateException(ControllerHelper.Localization.Text.Get("WebOsSecurity_201"));
        AppSettings updated = current.Copy();
        updated.TelevisionCertificateSha256 = NormalizeFingerprint(fingerprint);
        if (NormalizeFingerprint(current.TelevisionCertificateSha256) != updated.TelevisionCertificateSha256)
        {
            updated.TelevisionClientKey = null;
            updated.TelevisionClientKeyProtected = null;
        }
        return updated;
    }

    internal static void ReconcileSettings(AppSettings previous, AppSettings candidate)
    {
        if (!TelevisionPairingTarget.From(candidate).SameDevice(TelevisionPairingTarget.From(previous)))
            candidate.TelevisionCertificateSha256 = null;
        if (TelevisionPairingTarget.From(candidate) != TelevisionPairingTarget.From(previous))
        {
            candidate.TelevisionClientKey = null;
            candidate.TelevisionClientKeyProtected = null;
        }
    }
}
