using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ControllerHelper.Services;

internal sealed record TelevisionCertificate(string Sha256, DateTimeOffset NotBefore, DateTimeOffset NotAfter);

internal static class TelevisionCertificateProbe
{
    internal static Task<TelevisionCertificate> ReadAsync(string? address, CancellationToken cancellation) =>
        ReadEndpointAsync(TelevisionWakeService.ParseAddress(address).ToString(), 3001, cancellation);

    // Discovery rejects the TLS handshake after observing the certificate. No WebSocket or pairing key is sent.
    internal static async Task<TelevisionCertificate> ReadEndpointAsync(string address, int port, CancellationToken cancellation)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using TcpClient client = new();
        TelevisionCertificate? observed = null;
        try
        {
            ErrorLog.Event("television.certificate.tcp.begin", new { port });
            await client.ConnectAsync(address, port, timeout.Token);
            ErrorLog.Event("television.certificate.tcp.ok", new { port });
            using SslStream tls = new(client.GetStream(), false, (_, certificate, _, _) =>
            {
                if (certificate is not null)
                {
                    using var copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    observed = new(WebOsSecurity.Fingerprint(copy), copy.NotBefore.ToUniversalTime(), copy.NotAfter.ToUniversalTime());
                    ErrorLog.Event("television.certificate.observed", new { port, notAfter = copy.NotAfter.ToUniversalTime() });
                }
                return false;
            });
            try { await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = address }, timeout.Token); }
            catch (AuthenticationException) when (observed is not null) { }
            timeout.Token.ThrowIfCancellationRequested();
            return observed ?? throw new TelevisionCertificateException(ControllerHelper.Localization.Text.Get("TelevisionCertificateProbe_178"));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException(ControllerHelper.Localization.Text.Get("TelevisionCertificateProbe_179")); }
    }
}
