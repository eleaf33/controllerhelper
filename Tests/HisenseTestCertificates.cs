using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

// Generated only in the test process. Never accepted as credentials by a real TV,
// never exported to the repository, and never used by the application build.
internal static class HisenseTestCertificates
{
    private static readonly byte[] Legacy = Create("legacy");
    private static readonly byte[] RemoteNow = Create("remotenow");
    private static readonly byte[] Ios = Create("ios");

    internal static X509Certificate2 Load(bool remoteNow = false, bool ios = false) =>
        X509CertificateLoader.LoadPkcs12(ios ? Ios : remoteNow ? RemoteNow : Legacy, null, X509KeyStorageFlags.UserKeySet);

    internal static string Fingerprint(bool remoteNow = false, bool ios = false)
    {
        using var certificate = Load(remoteNow, ios);
        return certificate.GetCertHashString();
    }

    private static byte[] Create(string profile)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=ControllerHelper.TestOnly." + profile,
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return certificate.Export(X509ContentType.Pfx);
    }
}
