using System.Buffers;
using System.Diagnostics;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using ControllerHelper.Models;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Diagnostics.PacketInspection;
using MQTTnet.Protocol;

namespace ControllerHelper.Services;

internal sealed record HisenseMessage(string Topic, byte[] Payload, bool Retained = false);

internal interface IHisenseTransport : IDisposable
{
    HisenseCredentials? Connection => null;
    Task ConnectAsync(AppSettings settings, CancellationToken cancellation);
    Task ReconnectWithTokenAsync(HisenseAuthentication authentication, CancellationToken cancellation) => throw new NotSupportedException();
    Task PublishAsync(string topic, string payload, CancellationToken cancellation);
    ValueTask<HisenseMessage> ReadAsync(CancellationToken cancellation);
    bool TryRead(out HisenseMessage? message);
    void Drain();
}

// MQTT is handled by MQTTnet; this class only supplies the RemoteNOW connection policy.
internal sealed class HisenseMqttTransport(Func<bool, bool, X509Certificate2>? certificateFactory = null) : IHisenseTransport
{
    // Production always uses the locally supplied embedded credentials. Tests
    // inject independent temporary certificates; no vendor keys are needed there.
    private readonly Func<bool, bool, X509Certificate2> _certificateFactory = certificateFactory ?? LoadClientCertificate;
    internal const int Port = 36669;
    private IMqttClient? _client;
    private Channel<HisenseMessage> _messages = Channel.CreateBounded<HisenseMessage>(32);
    public HisenseCredentials? Connection { get; private set; }
    private X509Certificate2? _certificate;
    private volatile bool _disposed;
    private AppSettings? _settings;
    private string? _address;
    private int _port;
    private readonly string _willIdentity = Guid.NewGuid().ToString();
    private string? _connectionTrace;

    internal static X509Certificate2 LoadClientCertificate(bool remoteNow = false, bool ios = false)
    {
        string Read(string name)
        {
            using Stream resource = typeof(HisenseMqttTransport).Assembly.GetManifestResourceStream(
                "ControllerHelper.ThirdParty.Hisense." + name) ?? throw new InvalidOperationException(
                    ControllerHelper.Localization.Text.Get("HisenseMqttTransport_106"));
            using StreamReader reader = new(resource);
            return reader.ReadToEnd();
        }
        string certificate = Read(ios ? "vidaa_ios_client.pem" : remoteNow ? "remotenow_client.pem" : "vidaa_client.pem");
        using X509Certificate2 pem = X509Certificate2.CreateFromPem(certificate, remoteNow || ios ? certificate : Read("vidaa_client.key"));
        // Schannel requires a persisted provider handle; disposal removes this temporary key.
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    }

    public async Task ConnectAsync(AppSettings settings, CancellationToken cancellation)
    {
        HisenseSettings.Validate(settings);
        string address = TelevisionWakeService.ParseAddress(settings.HisenseIpAddress).ToString();
        int? version = await HisenseAuthenticationProtocol.DetectVersionAsync(address, cancellation);
        await ConnectEndpointAsync(settings, address, Port, cancellation, version);
    }

    internal async Task ConnectEndpointAsync(AppSettings settings, string address, int port, CancellationToken cancellation, int? version = null)
    {
        HisenseSettings.Validate(settings);
        _settings = settings.Copy(); _address = address; _port = port;
        int attempt = 0;
        foreach (var credentials in HisenseAuthenticationProtocol.Candidates(settings, version, DateTimeOffset.UtcNow))
        {
            cancellation.ThrowIfCancellationRequested();
            ErrorLog.Event("hisense.auth.attempt", new { attempt = ++attempt, method = credentials.Method.ToString(), source = credentials.Source,
                transportVersion = version, identityFormat = HisenseAuthenticationProtocol.IdentityFormat(credentials),
                clientIdLength = credentials.ClientId.Length, utc = DateTimeOffset.UtcNow });
            if (await TryConnectAsync(settings, address, port, credentials, cancellation))
            {
                Connection = credentials;
                ErrorLog.Event("hisense.auth.accepted", new { method = credentials.Method.ToString(), source = credentials.Source });
                return;
            }
        }
        ErrorLog.Event("hisense.auth.exhausted", new { attempts = attempt, transportVersion = version, stage = "mqtt-connect-before-pin" });
        throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("HisenseMqttTransport_107"));
    }

    public async Task ReconnectWithTokenAsync(HisenseAuthentication authentication, CancellationToken cancellation)
    {
        if (_settings is null || _address is null || Connection is null || authentication.ClientId != Connection.ClientId ||
            authentication.Method != Connection.Method || !authentication.IsValid())
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("HisenseMqttTransport_108"));
        if (_client is { IsConnected: true })
            await _client.DisconnectAsync(new MqttClientDisconnectOptions(), cancellation);
        _client?.Dispose(); _client = null;
        ErrorLog.Event("hisense.token.reconnect.begin", new { method = authentication.Method.ToString() });
        var credentials = HisenseAuthenticationProtocol.TokenCredentials(authentication, false, DateTimeOffset.UtcNow);
        if (!await TryConnectAsync(_settings, _address, _port, credentials, cancellation))
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("HisenseMqttTransport_109"));
        Connection = credentials;
        ErrorLog.Event("hisense.token.reconnect.ok");
    }

    private async Task<bool> TryConnectAsync(AppSettings settings, string address, int port, HisenseCredentials credentials, CancellationToken cancellation)
    {
        string trace = _connectionTrace = Guid.NewGuid().ToString("N");
        long started = Stopwatch.GetTimestamp();
        string phase = "client-certificate";
        int packets = 0, incoming = 0;
        _certificate?.Dispose();
        X509Certificate2 certificate = _certificate = _certificateFactory(credentials.UsesRemoteNowCertificate, credentials.UsesIosCertificate);
        string certificatePreset = credentials.UsesIosCertificate ? "VIDAA-IOS-2024" : credentials.UsesRemoteNowCertificate ? "RemoteNOW-2018" : "VIDAA-2024";
        ErrorLog.Event("hisense.tls.client-certificate", new { preset = certificatePreset, notAfter = certificate.NotAfter.ToUniversalTime(), hasPrivateKey = certificate.HasPrivateKey });
        // Rejected CONNECT can raise DisconnectedAsync. Each attempt owns its queue;
        // callbacks from an old connection must never close the new connection's queue.
        IMqttClient client = new MqttClientFactory().CreateMqttClient();
        var messages = Channel.CreateBounded<HisenseMessage>(32);
        _client = client;
        _messages = messages;
        bool connected = false;
        client.InspectPacketAsync += args =>
        {
            int count = Interlocked.Increment(ref packets);
            if (count <= 256)
                ErrorLog.Event("hisense.mqtt.packet", new { connection = trace, sequence = count,
                    direction = args.Direction.ToString(), packet = HisenseDiagnostics.Packet(args.Buffer, args.Direction == MqttPacketFlowDirection.Inbound) });
            else if (count == 257) ErrorLog.Event("hisense.mqtt.trace-limit", new { connection = trace, limit = 256 });
            return Task.CompletedTask;
        };
        client.ApplicationMessageReceivedAsync += args =>
        {
            var message = args.ApplicationMessage;
            string topic = HisenseDiagnostics.Topic(message.Topic, credentials.ClientId);
            int count = Interlocked.Increment(ref incoming);
            if (message.Payload.Length > 65536)
            {
                ErrorLog.Event("hisense.mqtt.receive.rejected", new { connection = trace, topic, bytes = message.Payload.Length, reason = "payload-too-large" });
                messages.Writer.TryComplete(new InvalidDataException(ControllerHelper.Localization.Text.Get("HisenseMqttTransport_110")));
            }
            else
            {
                byte[] payload = message.Payload.ToArray();
                bool queued = messages.Writer.TryWrite(new(message.Topic, payload, message.Retain));
                if (count <= 64)
                    ErrorLog.Event("hisense.mqtt.receive.queued", new { connection = trace, sequence = count, topic, bytes = payload.Length,
                        retained = message.Retain, qos = (int)message.QualityOfServiceLevel, queued,
                        reply = HisenseDiagnostics.Reply(topic, payload) });
                if (!queued)
                {
                    ErrorLog.Event("hisense.mqtt.receive.rejected", new { connection = trace, topic, reason = "queue-full-or-closed" });
                    messages.Writer.TryComplete(new InvalidDataException(ControllerHelper.Localization.Text.Get("HisenseMqttTransport_111")));
                }
            }
            return Task.CompletedTask;
        };
        client.DisconnectedAsync += args =>
        {
            if (!_disposed)
            {
                ErrorLog.Event("hisense.mqtt.disconnected", new { connection = trace, phase, reason = args.Reason.ToString(),
                    code = (int)args.Reason, wasConnected = args.ClientWasConnected, exceptionType = args.Exception?.GetType().FullName,
                    elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                messages.Writer.TryComplete(new IOException(ControllerHelper.Localization.Text.Get("HisenseMqttTransport_112"), args.Exception));
            }
            return Task.CompletedTask;
        };
        bool rejectedCertificate = false;
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(address, port)
            .WithClientId(credentials.ClientId)
            .WithProtocolVersion(credentials.UsesMqtt31 ? MqttProtocolVersion.V310 : MqttProtocolVersion.V311)
            .WithCleanSession().WithKeepAlivePeriod(TimeSpan.FromSeconds(credentials.MobileApp ? 90 : 15)).WithTimeout(TimeSpan.FromSeconds(8))
            .WithCredentials(credentials.Username, credentials.Password)
            .WithTlsOptions(tls => tls.WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13)
                .WithClientCertificates(new[] { certificate })
                .WithCertificateSelectionHandler(_ =>
                {
                    ErrorLog.Event("hisense.tls.client-certificate-selected", new { preset = certificatePreset });
                    return certificate;
                })
                .WithCertificateValidationHandler(args =>
                {
                    bool accepted = WebOsSecurity.AcceptCertificate(args.Certificate, settings.HisenseCertificateSha256, DateTimeOffset.UtcNow);
                    rejectedCertificate |= !accepted;
                    ErrorLog.Event("hisense.tls.validation", new { accepted, errors = args.SslPolicyErrors.ToString() });
                    return accepted;
                }));
        if (credentials.MobileApp)
            builder.WithWillTopic("/mobile/" + _willIdentity).WithWillPayload(Array.Empty<byte>()).WithWillRetain();
        var options = builder.Build();
        ErrorLog.Event("hisense.mqtt.connect.begin", new { connection = trace, port, protocol = credentials.UsesMqtt31 ? "3.1" : "3.1.1",
            auth = credentials.Method.ToString(), source = credentials.Source, certificatePreset });
        try
        {
            phase = "connect";
            var result = await client.ConnectAsync(options, cancellation);
            ErrorLog.Event("hisense.mqtt.connack", new { connection = trace, code = result.ResultCode.ToString(),
                numericCode = (int)result.ResultCode, sessionPresent = result.IsSessionPresent, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            if (result.ResultCode is MqttClientConnectResultCode.NotAuthorized or MqttClientConnectResultCode.BadUserNameOrPassword or MqttClientConnectResultCode.UnsupportedProtocolVersion)
                return false; // Only explicit credential rejection permits a different auth method.
            if (result.ResultCode != MqttClientConnectResultCode.Success)
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("HisenseMqttTransport_113") + result.ResultCode);
            ErrorLog.Event("hisense.auth.connect-accepted", new { connection = trace, method = credentials.Method.ToString(), source = credentials.Source });
            phase = "subscribe";
            // The U7QF grants only PIN/token topics to a generated credential. The
            // APK's Paho callback proceeds with that partial SUBACK, then reconnects
            // and subscribes again with the access token. A refresh token has the
            // same bootstrap role; it must not be mistaken for control access.
            bool bootstrap = credentials.MobileApp && credentials.Source is "generated" or "refresh-token";
            string subscriptionStage = !credentials.MobileApp ? "legacy" : credentials.Source switch
            {
                "generated" => "pairing", "refresh-token" => "refresh", _ => "control"
            };
            string[] topics = HisenseSession.ResponseTopics(credentials.ClientId, credentials.Dynamic, credentials.MobileApp);
            MqttClientSubscribeOptionsBuilder subscriptions = new();
            foreach (string topic in topics)
                subscriptions.WithTopicFilter(topic, MqttQualityOfServiceLevel.AtMostOnce);
            ErrorLog.Event("hisense.mqtt.subscribe.begin", new { connection = trace, subscriptionStage, plan = credentials.MobileApp ? "mobile-apk-9" : "legacy",
                qos = 0, count = topics.Length, topics = topics.Select(topic => HisenseDiagnostics.Topic(topic, credentials.ClientId)).ToArray() });
            long subscribeStarted = Stopwatch.GetTimestamp();
            var subscribed = await client.SubscribeAsync(subscriptions.Build(), cancellation);
            // Record every result before throwing, including partial successes and the
            // packet identifier needed to correlate the raw numerical SUBACK trace.
            var results = subscribed.Items.Select((item, index) =>
            {
                string topic = HisenseDiagnostics.Topic(item.TopicFilter.Topic, credentials.ClientId);
                return new { index, topic, qos = (int)item.TopicFilter.QualityOfServiceLevel,
                    code = (int)item.ResultCode, name = item.ResultCode.ToString(), accepted = (int)item.ResultCode is >= 0 and <= 2,
                    requirement = SubscriptionRequirement(topic, credentials.MobileApp, bootstrap) };
            }).ToArray();
            ErrorLog.Event("hisense.mqtt.suback", new { connection = trace, subscriptionStage, packetId = subscribed.PacketIdentifier,
                requested = topics.Length, received = results.Length, results, elapsedMs = Stopwatch.GetElapsedTime(subscribeStarted).TotalMilliseconds });
            if (results.Length != topics.Length)
                throw new InvalidDataException(ControllerHelper.Localization.Text.Get("HisenseMqttTransport_114"));
            // Only a well-formed MQTT 3.x denial of a known non-required channel
            // is tolerated. PIN/token failures and post-token control failures
            // remain fatal, as do unknown response codes and legacy failures.
            var rejected = results.FirstOrDefault(item => !item.accepted && (item.requirement == "required" || item.code != 128));
            if (rejected is not null)
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Format("HisenseMqttTransport_115", rejected.topic, rejected.name, rejected.code, subscriptionStage));
            int acceptedCount = results.Count(item => item.accepted);
            if (acceptedCount != results.Length)
                ErrorLog.Event("hisense.mqtt.subscribe.partial", new { connection = trace, subscriptionStage,
                    accepted = acceptedCount, deferred = results.Where(item => !item.accepted && item.requirement == "after-token").Select(item => item.topic).ToArray(),
                    optional = results.Where(item => !item.accepted && item.requirement == "optional").Select(item => item.topic).ToArray(),
                    next = bootstrap ? "token-exchange-and-resubscribe" : "verify-source-response" });
            ErrorLog.Event("hisense.mqtt.subscribe.ok", new { connection = trace, subscriptionStage, requested = topics.Length, accepted = acceptedCount });
            phase = "ready";
            connected = true;
            return true;
        }
        catch (Exception error)
        {
            ErrorLog.Event("hisense.mqtt.failed", new { connection = trace, phase, exceptionType = error.GetType().FullName,
                canceled = error is OperationCanceledException, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            if (error is OperationCanceledException) throw;
            ErrorLog.Write("hisense.mqtt." + phase, error);
            if (rejectedCertificate) throw new TelevisionCertificateException(ControllerHelper.Localization.Text.Get("HisenseMqttTransport_116"));
            throw;
        }
        finally
        {
            if (!connected)
            {
                client.Dispose();
                messages.Writer.TryComplete();
                if (ReferenceEquals(_client, client)) _client = null;
            }
        }
    }

    private static string SubscriptionRequirement(string topic, bool mobileApp, bool bootstrap)
    {
        if (!mobileApp) return "required";
        return topic switch
        {
            // These are present in the APK's batch but unused by ControllerHelper.
            "platform_service/data/gettvinfo" or "ui_service/data/applist" or "ui_service/data/applisticon" => "optional",
            "ui_service/data/sourcelist" or "broadcast/ui_service/state" when bootstrap => "after-token",
            _ => "required"
        };
    }

    public async Task PublishAsync(string topic, string payload, CancellationToken cancellation)
    {
        string label = HisenseDiagnostics.Topic(topic, Connection?.ClientId ?? "");
        long started = Stopwatch.GetTimestamp();
        ErrorLog.Event("hisense.mqtt.publish.begin", new { connection = _connectionTrace, topic = label, bytes = System.Text.Encoding.UTF8.GetByteCount(payload), qos = 0 });
        try
        {
            var result = await (_client ?? throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("HisenseMqttTransport_117"))).PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
                .WithRetainFlag(false).Build(), cancellation);
            ErrorLog.Event("hisense.mqtt.publish.sent", new { connection = _connectionTrace, topic = label, confirmedByTv = false,
                code = (int)result.ReasonCode, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
        }
        catch (Exception error)
        {
            ErrorLog.Event("hisense.mqtt.publish.failed", new { connection = _connectionTrace, topic = label, exceptionType = error.GetType().FullName,
                elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            throw;
        }
    }
    public ValueTask<HisenseMessage> ReadAsync(CancellationToken cancellation) => _messages.Reader.ReadAsync(cancellation);
    public bool TryRead(out HisenseMessage? message) => _messages.Reader.TryRead(out message);
    public void Drain()
    {
        int count = 0;
        while (_messages.Reader.TryRead(out _)) count++;
        ErrorLog.Event("hisense.mqtt.queue.drained", new { connection = _connectionTrace, discarded = count });
    }
    public void Dispose()
    {
        _disposed = true;
        _client?.Dispose();
        _messages.Writer.TryComplete();
        _certificate?.Dispose();
    }
}
