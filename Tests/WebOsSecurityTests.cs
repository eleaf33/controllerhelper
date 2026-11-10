using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ControllerHelper.Models;
using ControllerHelper.Services;

internal static partial class WebOsSecurityTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Старые настройки выбирают TLS без автоматического открытого канала", SecureDefault),
        ("Открытый канал доступен только при явном выборе", ExplicitCompatibility),
        ("Закрепление проверяет сертификат и срок действия", CertificatePolicy),
        ("Доверие к сертификату сохраняет свежие настройки", TrustMerge),
        ("Ответ проверки другого ТВ не сохраняет доверие", RejectStaleTrust),
        ("Смена устройства сбрасывает сертификат и ключ", DeviceChange),
        ("Смена режима связи не принимает старое сопряжение", TransportChange),
        ("Кэш питания учитывает сертификат и режим связи", SecurityCache),
        ("Без доверия возврат к мониторам остаётся доступен", LocalReturn),
        ("TLS сопряжение работает с подтверждённым самоподписанным сертификатом", SecurePairing),
        ("TLS запрос питания передаёт ключ после проверки сертификата", SecurePowerRead),
        ("TLS выключение получает подтверждение команды", SecurePowerOff),
        ("Другой сертификат не получает ни HTTP, ни ключ сопряжения", WrongCertificate),
        ("Просроченный сертификат блокирует сопряжение", ExpiredCertificate),
        ("Проверка сертификата не отправляет прикладные данные", CertificateDiscovery),
        ("Ожидание сертификата отменяется", DiscoveryCancellation),
        ("Закрытие TLS-порта не приводит к открытому подключению", NoDowngrade),
        ("Перенаправление TLS не меняет адрес и не открывает HTTP", RejectRedirect),
        ("Без отпечатка сеть не открывается даже при сохранённом ключе", MissingPinDoesNotConnect),
        ("Режим совместимости сохраняет явный открытый протокол", PlaintextCompatibility),
        .. InputCases
    ];
    private static AppSettings Settings(string? fingerprint = null) => new()
    {
        TelevisionId = "tv", TelevisionIpAddress = "192.0.2.10", TelevisionMacAddress = "AA:BB:CC:DD:EE:FF",
        TelevisionCertificateSha256 = fingerprint, TelevisionClientKey = "private-test-key", WakeTelevision = true
    };
    private static X509Certificate2 Certificate(bool expired = false)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=ControllerHelper.Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 10));
        // Windows Schannel requires an imported provider key for the TLS server; no trust-store installation.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    }
    private static Task SecureDefault()
    {
        var settings = SettingsStore.Deserialize("{\"TelevisionIpAddress\":\"192.0.2.10\"}", out _)!;
        Check(!settings.AllowInsecureWebOs, "Old JSON silently enables plaintext.");
        ThrowsSync<TelevisionCertificateException>(() => WebOsSecurity.Endpoint(settings));
        settings.TelevisionCertificateSha256 = new string('A', 64);
        Check(WebOsSecurity.Endpoint(settings).Scheme == "wss" && WebOsSecurity.Endpoint(settings).Port == 3001, "Wrong secure endpoint.");
        return Task.CompletedTask;
    }
    private static Task ExplicitCompatibility()
    {
        var settings = Settings(); settings.AllowInsecureWebOs = true;
        Check(WebOsSecurity.Endpoint(settings).Scheme == "ws" && WebOsSecurity.Endpoint(settings).Port == 3000, "Explicit compatibility unavailable.");
        settings.AllowInsecureWebOs = false;
        ThrowsSync<TelevisionCertificateException>(() => WebOsSecurity.Endpoint(settings));
        return Task.CompletedTask;
    }
    private static Task CertificatePolicy()
    {
        using var cert = Certificate(); using var other = Certificate(); using var expired = Certificate(true);
        string fingerprint = WebOsSecurity.Fingerprint(cert);
        Check(WebOsSecurity.AcceptCertificate(cert, fingerprint.ToLowerInvariant(), DateTimeOffset.UtcNow), "Pinned self-signed certificate rejected.");
        Check(!WebOsSecurity.AcceptCertificate(other, fingerprint, DateTimeOffset.UtcNow), "Different certificate accepted.");
        Check(!WebOsSecurity.AcceptCertificate(null, fingerprint, DateTimeOffset.UtcNow), "Missing certificate accepted.");
        Check(!WebOsSecurity.AcceptCertificate(cert, "bad", DateTimeOffset.UtcNow), "Malformed pin accepted.");
        Check(!WebOsSecurity.AcceptCertificate(expired, WebOsSecurity.Fingerprint(expired), DateTimeOffset.UtcNow), "Expired certificate accepted.");
        Check(!WebOsSecurity.AcceptCertificate(cert, fingerprint, DateTimeOffset.UtcNow.AddDays(-30)), "Not-yet-valid certificate accepted.");
        return Task.CompletedTask;
    }
    private static Task TrustMerge()
    {
        var settings = Settings(); var target = TelevisionPairingTarget.From(settings);
        settings.PcAudioId = "new-audio";
        var result = WebOsSecurity.ApplyCertificate(settings, target, new string('A', 64));
        Check(result.PcAudioId == "new-audio" && result.TelevisionClientKey is null && settings.TelevisionCertificateSha256 is null,
            "Trust overwrote fresh settings, retained old key, or mutated original.");
        result.TelevisionClientKey = "new-key";
        var same = WebOsSecurity.ApplyCertificate(result, TelevisionPairingTarget.From(result), new string('a', 64));
        Check(same.TelevisionClientKey == "new-key", "Unchanged certificate erased current pairing.");
        return Task.CompletedTask;
    }
    private static Task RejectStaleTrust()
    {
        var settings = Settings(); var target = TelevisionPairingTarget.From(settings);
        settings.TelevisionIpAddress = "192.0.2.11";
        ThrowsSync<InvalidOperationException>(() => WebOsSecurity.ApplyCertificate(settings, target, new string('A', 64)));
        Check(settings.TelevisionCertificateSha256 is null, "Stale response changed trust.");
        return Task.CompletedTask;
    }
    private static Task DeviceChange()
    {
        foreach (Action<AppSettings> change in new Action<AppSettings>[]
        { s => s.TelevisionIpAddress = "192.0.2.11", s => s.TelevisionId = "other", s => s.TelevisionMacAddress = "11:22:33:44:55:66" })
        {
            var previous = Settings(new string('A', 64)); var next = previous.Copy(); change(next);
            WebOsSecurity.ReconcileSettings(previous, next);
            Check(next.TelevisionCertificateSha256 is null && next.TelevisionClientKey is null && next.TelevisionClientKeyProtected is null,
                "Different device inherited credentials.");
        }
        return Task.CompletedTask;
    }
    private static Task TransportChange()
    {
        var previous = Settings(new string('A', 64)); var target = TelevisionPairingTarget.From(previous);
        var next = previous.Copy(); next.AllowInsecureWebOs = true;
        WebOsSecurity.ReconcileSettings(previous, next);
        Check(next.TelevisionClientKey is null, "Mode change retained pairing.");
        ThrowsSync<InvalidOperationException>(() => target.ApplyKey(next, "stale-key"));
        next = previous.Copy(); next.TelevisionCertificateSha256 = new string('B', 64);
        ThrowsSync<InvalidOperationException>(() => target.ApplyKey(next, "stale-key"));
        return Task.CompletedTask;
    }
    private static async Task SecurityCache()
    {
        int requests = 0;
        TelevisionPowerProbe probe = new((_, _) => { requests++; return Task.FromResult(TelevisionPowerState.On); });
        var settings = Settings(new string('A', 64));
        await probe.ReadAsync(settings, default);
        settings.TelevisionCertificateSha256 = new string('B', 64); await probe.ReadAsync(settings, default);
        settings.AllowInsecureWebOs = true; await probe.ReadAsync(settings, default);
        Check(requests == 3, "Cache ignored trust or transport changes.");
    }
    private static Task LocalReturn()
    {
        var state = new TelevisionOverlayState(true, false, TelevisionPowerState.Unknown, true, false,
            ConnectionWarning: WebOsSecurity.SetupWarning(Settings()));
        var presentation = OverlayPresentation.FromState(state);
        Check(presentation.PrimaryAction == OverlayPrimaryAction.ReturnToPc && presentation.Warning!.Contains("сертификат"), "Missing safe local return/setup warning.");
        return Task.CompletedTask;
    }

    private static async Task SecurePairing()
    {
        using var cert = Certificate();
        bool registered = false;
        await using Server server = new(cert, async (socket, cancellation) =>
        {
            await Register(socket, cancellation, expectedKey: null, inputControl: true); registered = true;
        });
        var settings = Settings(WebOsSecurity.Fingerprint(cert));
        var service = Service(server);
        string key = await service.PairAsync(settings).WaitAsync(TimeSpan.FromSeconds(8));
        await server.Completion;
        Check(key == "paired-test-key" && registered, "TLS pairing failed.");
    }
    private static async Task SecurePowerRead()
    {
        using var cert = Certificate();
        await using Server server = new(cert, async (socket, cancellation) =>
        {
            await Register(socket, cancellation, "private-test-key", inputControl: false);
            using JsonDocument request = await Receive(socket, cancellation);
            Check(request.RootElement.GetProperty("id").GetString() == "power_state", "Wrong power request.");
            await Send(socket, "{\"type\":\"response\",\"id\":\"power_state\",\"payload\":{\"returnValue\":true,\"state\":\"Active\"}}", cancellation);
        });
        var result = await Service(server).GetPowerStateAsync(Settings(WebOsSecurity.Fingerprint(cert))).WaitAsync(TimeSpan.FromSeconds(8));
        await server.Completion;
        Check(result == TelevisionPowerState.On, "Secure power read failed.");
    }
    private static async Task SecurePowerOff()
    {
        using var cert = Certificate();
        await using Server server = new(cert, async (socket, cancellation) =>
        {
            await Register(socket, cancellation, "private-test-key", inputControl: false);
            using JsonDocument request = await Receive(socket, cancellation);
            Check(request.RootElement.GetProperty("id").GetString() == "power_off", "Wrong power-off request.");
            await Send(socket, "{\"type\":\"response\",\"id\":\"power_off\",\"payload\":{\"returnValue\":true}}", cancellation);
        });
        Check(await Service(server).PowerOffAsync(Settings(WebOsSecurity.Fingerprint(cert))).WaitAsync(TimeSpan.FromSeconds(8)), "Secure command not acknowledged.");
        await server.Completion;
    }
    private static async Task WrongCertificate()
    {
        using var cert = Certificate(); using var other = Certificate();
        await using Server server = new(other, (_, _) => throw new Exception("Untrusted server received WebSocket traffic"), expectRejected: true);
        await Throws<TelevisionCertificateException>(() => Service(server).PairAsync(Settings(WebOsSecurity.Fingerprint(cert))));
        await server.Completion;
        Check(!server.HttpReceived, "Certificate mismatch reached application protocol.");
    }
    private static async Task ExpiredCertificate()
    {
        using var cert = Certificate(true);
        await using Server server = new(cert, (_, _) => throw new Exception("Expired certificate used"), expectRejected: true);
        await Throws<TelevisionCertificateException>(() => Service(server).PairAsync(Settings(WebOsSecurity.Fingerprint(cert))));
        await server.Completion;
        Check(!server.HttpReceived, "Expired certificate reached HTTP.");
    }
    private static async Task CertificateDiscovery()
    {
        using var cert = Certificate();
        await using Server server = new(cert, (_, _) => throw new Exception("Discovery sent application data"), expectRejected: true);
        var observed = await TelevisionCertificateProbe.ReadEndpointAsync("127.0.0.1", server.Port, default).WaitAsync(TimeSpan.FromSeconds(8));
        await server.Completion;
        Check(observed.Sha256 == WebOsSecurity.Fingerprint(cert) && !server.HttpReceived, "Discovery failed or transmitted application data.");
    }
    private static async Task DiscoveryCancellation()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0); listener.Start();
        using CancellationTokenSource cancellation = new();
        var read = TelevisionCertificateProbe.ReadEndpointAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, cancellation.Token);
        using TcpClient peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Throws<OperationCanceledException>(() => read);
    }
    private static async Task NoDowngrade()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        int attempts = 0;
        var service = new WebOsTelevisionService((settings, cancellation) =>
        {
            attempts++;
            return WebOsConnection.ConnectEndpointAsync(settings, new Uri($"wss://127.0.0.1:{port}/"), cancellation);
        });
        await Throws<InvalidOperationException>(() => service.PairAsync(Settings(new string('A', 64))));
        Check(attempts == 1, "Failed TLS silently retried.");
        await Throws<TelevisionCertificateException>(() => WebOsConnection.ConnectEndpointAsync(Settings(new string('A', 64)), new Uri($"ws://127.0.0.1:{port}/"), default));
    }
    private static async Task PlaintextCompatibility()
    {
        await using Server server = new(null, (socket, cancellation) => Register(socket, cancellation, null));
        var settings = Settings(); settings.AllowInsecureWebOs = true;
        Check(await Service(server).PairAsync(settings).WaitAsync(TimeSpan.FromSeconds(8)) == "paired-test-key", "Explicit legacy mode failed.");
        await server.Completion;
    }

    private static async Task RejectRedirect()
    {
        using var cert = Certificate();
        using TcpListener destination = new(IPAddress.Loopback, 0); destination.Start();
        string redirect = $"http://127.0.0.1:{((IPEndPoint)destination.LocalEndpoint).Port}/";
        await using Server server = new(cert, (_, _) => throw new Exception("Redirect reached webOS protocol"), redirect: redirect);
        await Throws<InvalidOperationException>(() => Service(server).PairAsync(Settings(WebOsSecurity.Fingerprint(cert))));
        await server.Completion;
        Check(!destination.Pending(), "TLS redirect opened an insecure connection.");
    }

    private static async Task MissingPinDoesNotConnect()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0); listener.Start();
        Uri endpoint = new($"wss://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        await Throws<TelevisionCertificateException>(() => WebOsConnection.ConnectEndpointAsync(Settings(), endpoint, default));
        Check(!listener.Pending(), "Unconfirmed certificate caused network activity.");
    }

    private static WebOsTelevisionService Service(Server server) =>
        new((settings, cancellation) => WebOsConnection.ConnectEndpointAsync(settings, server.Endpoint, cancellation));
    private static async Task Register(WebSocket socket, CancellationToken cancellation, string? expectedKey, bool? inputControl = null, bool prompt = false)
    {
        using (JsonDocument hello = await Receive(socket, cancellation))
            Check(hello.RootElement.GetProperty("type").GetString() == "hello", "No hello.");
        await Send(socket, "{\"type\":\"hello\"}", cancellation);
        using (JsonDocument info = await Receive(socket, cancellation))
            Check(info.RootElement.GetProperty("id").GetString() == "system_info", "No system-info request.");
        await Send(socket, "{\"type\":\"response\",\"id\":\"system_info\",\"payload\":{}}", cancellation);
        using (JsonDocument register = await Receive(socket, cancellation))
        {
            JsonElement payload = register.RootElement.GetProperty("payload");
            bool hasKey = payload.TryGetProperty("client-key", out JsonElement key);
            Check(expectedKey is null ? !hasKey : hasKey && key.GetString() == expectedKey, "Unexpected credentials in registration.");
            if (inputControl is not null)
            {
                var permissions = payload.GetProperty("manifest").GetProperty("permissions").EnumerateArray().Select(item => item.GetString()).ToArray();
                Check(permissions.Contains("CONTROL_INPUT_TV") == inputControl && permissions.Contains("READ_INPUT_DEVICE_LIST") == inputControl &&
                    permissions.Contains("LAUNCH") == inputControl && permissions.Contains("READ_RUNNING_APPS") == inputControl,
                    "Wrong input permissions in pairing/registration");
                Check(permissions.Contains("CONTROL_POWER") && permissions.Contains("READ_POWER_STATE"), "Lost power permissions");
            }
        }
        if (prompt) { await Send(socket, "{\"type\":\"response\",\"id\":\"register_0\",\"payload\":{\"pairingType\":\"PROMPT\"}}", cancellation); return; }
        await Send(socket, "{\"type\":\"registered\",\"payload\":{\"client-key\":\"paired-test-key\"}}", cancellation);
    }
    private static async Task<JsonDocument> Receive(WebSocket socket, CancellationToken cancellation)
    {
        byte[] buffer = new byte[8192]; int count = 0;
        ValueWebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer.AsMemory(count), cancellation); count += received.Count;
            Check(count < buffer.Length && received.MessageType == WebSocketMessageType.Text, "Invalid test message.");
        } while (!received.EndOfMessage);
        return JsonDocument.Parse(buffer.AsMemory(0, count));
    }
    private static Task Send(WebSocket socket, string text, CancellationToken cancellation) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellation);

    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(10));
        internal int Port { get; }
        internal Uri Endpoint { get; }
        internal Task Completion { get; }
        internal bool HttpReceived;

        internal Server(X509Certificate2? certificate, Func<WebSocket, CancellationToken, Task> handler, bool expectRejected = false,
            string? redirect = null)
        {
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Endpoint = new Uri($"{(certificate is null ? "ws" : "wss")}://127.0.0.1:{Port}/");
            Completion = Run();
            async Task Run()
            {
                try
                {
                    using TcpClient peer = await _listener.AcceptTcpClientAsync(_timeout.Token);
                    Stream stream = peer.GetStream();
                    using SslStream? tls = certificate is null ? null : new SslStream(stream, false);
                    if (tls is not null)
                    {
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        { ServerCertificate = certificate, ClientCertificateRequired = false }, _timeout.Token);
                        stream = tls;
                    }
                    byte[] buffer = new byte[8192]; int count = 0;
                    while (true)
                    {
                        int read = await stream.ReadAsync(buffer.AsMemory(count, 1), _timeout.Token);
                        if (read == 0) { if (expectRejected) return; throw new IOException("Unexpected EOF"); }
                        count += read; HttpReceived = true;
                        Check(count < buffer.Length, "Large HTTP header.");
                        if (count >= 4 && Encoding.ASCII.GetString(buffer, count - 4, 4) == "\r\n\r\n") break;
                    }
                    string headers = Encoding.ASCII.GetString(buffer, 0, count);
                    if (redirect is not null)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {redirect}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _timeout.Token);
                        return;
                    }
                    string key = headers.Split("\r\n").Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
                    string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _timeout.Token);
                    using WebSocket websocket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(30));
                    await handler(websocket, _timeout.Token);
                }
                catch (Exception error) when (expectRejected && error is AuthenticationException or IOException) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            _timeout.Cancel(); _listener.Stop();
            try { await Completion; }
            catch (OperationCanceledException) { }
            finally { _timeout.Dispose(); }
        }
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static void ThrowsSync<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
