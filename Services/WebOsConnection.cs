using System.Net.WebSockets;
using ControllerHelper.Models;

namespace ControllerHelper.Services;

internal static class WebOsConnection
{
    internal static Task<ClientWebSocket> ConnectAsync(AppSettings settings, CancellationToken cancellation) =>
        ConnectEndpointAsync(settings, WebOsSecurity.Endpoint(settings), cancellation);

    // Separate endpoint for a loopback TLS test server. The application always uses the policy above.
    internal static async Task<ClientWebSocket> ConnectEndpointAsync(AppSettings settings, Uri endpoint, CancellationToken cancellation)
    {
        if ((!settings.AllowInsecureWebOs && endpoint.Scheme != "wss") ||
            (settings.AllowInsecureWebOs && endpoint.Scheme != "ws"))
            throw new TelevisionCertificateException(ControllerHelper.Localization.Text.Get("WebOsConnection_194"));
        if (!settings.AllowInsecureWebOs && !WebOsSecurity.HasFingerprint(settings.TelevisionCertificateSha256))
            throw new TelevisionCertificateException(ControllerHelper.Localization.Text.Get("WebOsConnection_195"));
        ClientWebSocket socket = new();
        using SocketsHttpHandler handler = new() { UseProxy = false, UseCookies = false, AllowAutoRedirect = false };
        using HttpMessageInvoker invoker = new(handler, disposeHandler: false);
        bool rejectedCertificate = false;
        if (!settings.AllowInsecureWebOs)
        {
            string? expected = settings.TelevisionCertificateSha256;
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                bool accepted = WebOsSecurity.AcceptCertificate(certificate, expected, DateTimeOffset.UtcNow);
                rejectedCertificate = !accepted;
                return accepted;
            };
        }
        try
        {
            await socket.ConnectAsync(endpoint, invoker, cancellation);
            return socket;
        }
        catch (Exception error)
        {
            socket.Dispose();
            if (cancellation.IsCancellationRequested) cancellation.ThrowIfCancellationRequested();
            if (rejectedCertificate)
                throw new TelevisionCertificateException(ControllerHelper.Localization.Text.Get("WebOsConnection_196"));
            if (!settings.AllowInsecureWebOs && error is WebSocketException)
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("WebOsConnection_197"), error);
            throw;
        }
    }
}
