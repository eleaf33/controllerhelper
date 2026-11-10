using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ControllerHelper.Models;
using ControllerHelper.Services;

internal static partial class HisenseMqttTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Hisense: тесты используют временные сертификаты без встроенных ключей ТВ", TestCertificateIsolation),
        ("Hisense: реальный MQTT/TLS выполняет PIN и выбор входа", RealBrokerFlow),
        ("Hisense: RemoteNOW требует MQTT 3.1, свой сертификат и строковый PIN", RemoteNowBrokerFlow),
        ("Hisense: успешный профиль RemoteNOW сохраняется и используется первым", RemoteNowProfileReuse),
        ("Hisense: занятый пульт отменяет окно PIN без отправки кода", BusyRemoteCancelsPin),
        ("Hisense: истёкший PIN отменяет окно без отправки кода", ExpiredPinCancelsPrompt),
        ("Hisense: отмена диалога останавливает обработчик сообщений", CancelWaitingPin),
        ("Hisense: RemoteNOW 3.1 проверяет сертификат сервера до CONNECT", RemoteNowWrongServerCertificate),
        ("Hisense: новый профиль не передаёт отображаемое имя HDMI в changesource", SourceNameNotTransmitted),
        ("Hisense: другой сертификат блокирует MQTT CONNECT", WrongCertificate),
        ("Hisense: отсутствующее доверие блокирует подключение", MissingTrust),
        ("Hisense: неверный PIN не сохраняет сопряжение", WrongPin),
        ("Hisense: retained и чужие ответы не подтверждают сопряжение", StaleResponses),
        ("Hisense: отмена PIN не отправляет код", CancelPairing),
        ("Hisense: список входов не использует фиксированные HDMI ID", DynamicInputs),
        ("Hisense: неизвестный вход не отправляется телевизору", MissingInput),
        ("Hisense: отказ выбора входа остаётся предупреждением", InputFailureIsWarning),
        ("Hisense: отмена не превращается в предупреждение", CancellationPropagates),
        ("Hisense: тайм-аут имеет отдельный результат", Deadline),
        ("Hisense: смена адреса сбрасывает доверие и выбранный вход", TargetChange),
        ("Hisense: поздний результат другого ТВ отклоняется", StaleTarget),
        ("Hisense: PIN, ответы и адреса не попадают в журнал", PrivateLogging),
        .. AuthCases, .. MobileCases, .. PowerCases
    ];
    private const string Client = "ControllerHelper_Test";
    private static Task TestCertificateIsolation()
    {
        Check(!typeof(HisenseMqttTests).Assembly.GetManifestResourceNames()
            .Any(name => name.StartsWith("ControllerHelper.ThirdParty.Hisense.", StringComparison.Ordinal)),
            "Vendor credentials embedded in test executable");
        var fingerprints = new HashSet<string>();
        foreach (var (remoteNow, ios) in new[] { (false, false), (true, false), (false, true) })
        {
            using var certificate = HisenseTestCertificates.Load(remoteNow, ios);
            Check(certificate.HasPrivateKey && certificate.Subject.Contains("TestOnly", StringComparison.Ordinal),
                "Missing independent test identity");
            fingerprints.Add(certificate.GetCertHashString());
        }
        Check(fingerprints.Count == 3, "Certificate profiles collapsed into one identity");
        return Task.CompletedTask;
    }
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
    private const string Inputs = "[{\"sourceid\":\"27\",\"sourcename\":\"HDMI PC\"},{\"sourceid\":91,\"sourcename\":\"HDMI ARC\"}]";
    private static AppSettings Settings() => new()
    {
        TelevisionNetworkProvider = TelevisionNetworkProvider.Hisense, HisenseIpAddress = "192.0.2.40",
        HisenseMacAddress = "02:11:22:33:44:55", HisenseClientId = Client, HisensePaired = true,
        HisenseCertificateSha256 = new string('A', 64), HisenseInputId = "27", HisenseInputName = "HDMI PC"
    };
    private static string Response(string kind) => $"/remoteapp/mobile/{Client}/ui_service/data/{kind}";
    private static HisenseMessage Reply(string kind, string payload, bool retained = false) => new(Response(kind), Utf8(payload), retained);
    private sealed class Fake : IHisenseTransport
    {
        public HisenseCredentials? Connection { get; init; }
        internal readonly Channel<HisenseMessage> Messages = Channel.CreateUnbounded<HisenseMessage>();
        internal readonly List<(string Topic, string Payload)> Sent = [];
        internal Action<string, string>? OnSend;
        internal Exception? ConnectError;
        public Task ConnectAsync(AppSettings s, CancellationToken c) { c.ThrowIfCancellationRequested(); if (ConnectError is not null) throw ConnectError; return Task.CompletedTask; }
        public Task PublishAsync(string topic, string payload, CancellationToken c)
        { c.ThrowIfCancellationRequested(); Sent.Add((topic, payload)); OnSend?.Invoke(topic, payload); return Task.CompletedTask; }
        public ValueTask<HisenseMessage> ReadAsync(CancellationToken c) => Messages.Reader.ReadAsync(c);
        public bool TryRead(out HisenseMessage? message) => Messages.Reader.TryRead(out message);
        public void Drain() { while (Messages.Reader.TryRead(out _)) { } }
        public void Dispose() { }
        internal void Put(HisenseMessage message) => Messages.Writer.TryWrite(message);
    }
    private static Fake Sources() { Fake fake = new(); fake.OnSend = (t, _) => { if (t.EndsWith("/sourcelist")) fake.Put(Reply("sourcelist", Inputs)); }; return fake; }
    private static async Task WrongPin()
    {
        Fake fake = new();
        fake.OnSend = (t, _) => fake.Put(t.EndsWith("/authenticationcode") ? Reply("authenticationcode", "{\"result\":0}") : Reply("authentication", "{}"));
        using HisenseSession session = new(fake, Client);
        await Expect<InvalidOperationException>(() => session.PairAsync(_ => Task.FromResult<string?>("1234"), default));
        Check(!fake.Sent.Any(s => s.Topic.EndsWith("/changesource")), "Wrong PIN changed input");
    }
    private static async Task StaleResponses()
    {
        Fake fake = new();
        fake.OnSend = (t, _) =>
        {
            if (!t.EndsWith("/sourcelist")) return;
            fake.Put(Reply("sourcelist", Inputs, true));
            fake.Put(new(Response("sourcelist").Replace(Client, "OtherClient"), Utf8(Inputs)));
            fake.Put(Reply("authentication", "{}"));
        };
        using HisenseSession session = new(fake, Client);
        bool asked = false;
        await Expect<OperationCanceledException>(() => session.PairAsync(_ => { asked = true; return Task.FromResult<string?>(null); }, default));
        Check(asked, "Retained/foreign data accepted as paired");
    }
    private static async Task CancelPairing()
    {
        Fake fake = new();
        fake.OnSend = (_, _) => fake.Put(Reply("authentication", "{}"));
        using HisenseSession session = new(fake, Client);
        await Expect<OperationCanceledException>(() => session.PairAsync(_ => Task.FromResult<string?>(null), default));
        Check(!fake.Sent.Any(s => s.Topic.EndsWith("/authenticationcode")), "Canceled PIN sent");
    }
    private static async Task DynamicInputs()
    {
        Check(HisenseSession.ParseInputs(Utf8(Inputs)).Select(i => i.Id).SequenceEqual(["27", "91"]), "IDs hardcoded");
        await Expect<InvalidDataException>(() => Task.FromResult(HisenseSession.ParseInputs(Utf8("{}"))));
        await Expect<InvalidDataException>(() => Task.FromResult(HisenseSession.ParseInputs(new byte[65537])));
        Check(!HisenseSession.StateMatches(Utf8("{\"sourceid\":91}"), "27"), "Wrong state accepted");
        Check(HisenseSession.StateMatches(Utf8("{\"sourceid\":27}"), "27"), "Numeric source not supported");
    }
    private static async Task MissingInput()
    {
        Fake fake = Sources(); using HisenseSession session = new(fake, Client);
        await Expect<InvalidOperationException>(() => session.SelectInputAsync("3", default));
        Check(!fake.Sent.Any(s => s.Topic.EndsWith("/changesource")), "Missing source selected");
    }
    private static async Task InputFailureIsWarning()
    {
        Fake fake = new() { ConnectError = new IOException("unavailable") };
        string? warning = await new HisenseTelevisionService(() => fake).PrepareInputAsync(Settings(), default);
        Check(warning is not null, "Failure not converted to warning");
        AppSettings disabled = Settings(); disabled.HisenseInputId = null;
        Check(await new HisenseTelevisionService(() => throw new Exception("Should not connect")).PrepareInputAsync(disabled, default) is null, "Disabled input connected");
    }
    private static async Task CancellationPropagates()
    {
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        await Expect<OperationCanceledException>(() => new HisenseTelevisionService(() => new Fake()).PrepareInputAsync(Settings(), canceled.Token));
    }
    private static async Task Deadline() => await Expect<TimeoutException>(() => HisenseSession.Stage("test-timeout",
        async c => { await Task.Delay(Timeout.Infinite, c); return true; }, default, TimeSpan.FromMilliseconds(20)));
    private static Task TargetChange()
    {
        AppSettings before = Settings(), after = before.Copy(); after.HisenseIpAddress = "192.0.2.41";
        HisenseSettings.Reconcile(before, after);
        Check(after.HisenseCertificateSha256 is null && after.HisenseClientId is null && !after.HisensePaired && after.HisenseInputId is null, "Trust leaked to new TV");
        return Task.CompletedTask;
    }
    private static async Task StaleTarget()
    {
        AppSettings before = Settings(), after = before.Copy(); after.HisenseClientId += "changed";
        await Expect<InvalidOperationException>(() => { HisenseTarget.From(before).EnsureCurrent(after); return Task.CompletedTask; });
    }
    private static async Task MissingTrust()
    {
        AppSettings s = Settings(); s.HisenseCertificateSha256 = null;
        using HisenseMqttTransport transport = new();
        await Expect<InvalidOperationException>(() => transport.ConnectEndpointAsync(s, "127.0.0.1", 1, default));
    }
    private static async Task PrivateLogging()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ControllerHelper.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "errors.jsonl"); ErrorJournal old = ErrorLog.Journal;
        ErrorLog.Journal = new(path);
        try
        {
            await WrongPin();
            string log = await File.ReadAllTextAsync(path);
            Check(!log.Contains("1234") && !log.Contains("authNum") && !log.Contains("multimqttservice") && !log.Contains(Client), "Sensitive protocol data logged");
            Check(log.Contains("hisense.pair.result"), "Diagnostic event missing");
        }
        finally { ErrorLog.Journal = old; Directory.Delete(folder, true); }
    }

    // Tiny deterministic broker fixture: real TCP/TLS and MQTT packets, never a TV or the running app.
    private sealed class Broker : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(12));
        internal readonly X509Certificate2 Certificate;
        internal readonly Task Completion;
        internal bool ConnectReceived, PinReceived, InputReceived, ClientCertificateReceived;
        internal readonly List<(string Client, string Username, string Password)> Connections = [];
        private readonly byte[] _rejections;
        private readonly bool _dynamicFlow;
        private readonly bool _remoteNow;
        private readonly bool _modernDevice;
        private readonly bool _initiallyPaired;
        private bool _remoteCertificate;
        private bool _iosCertificate;
        internal readonly string? MobileProfile;
        private readonly bool _mobileMqtt31;
        private readonly bool _apkSubscriptions;
        private readonly bool _bootstrapAcl;
        private readonly bool _powerControl;
        private readonly bool _retainedPowerState, _stateBeforeSuback;
        private readonly string? _rejectSubscription;
        internal readonly List<string[]> SubscriptionBatches = [];
        internal readonly List<byte[]> SubscriptionCodes = [];
        internal int ProtocolRefusals;
        internal int PowerKeyCount;
        internal bool PairRequestReceived, TokenRequested, AuthClosed;
        internal const string Access = "fixture-access-secret", Refresh = "fixture-refresh-secret";
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal Broker(byte[]? rejections = null, bool dynamicFlow = false, bool remoteNow = false, bool initiallyPaired = false, bool modernDevice = false,
            string? mobileProfile = null, bool mobileMqtt31 = false, bool apkSubscriptions = false, string? rejectSubscription = null,
            bool bootstrapAcl = false, bool powerControl = false, bool retainedPowerState = false, bool stateBeforeSuback = false)
        {
            _rejections = rejections ?? [];
            _dynamicFlow = dynamicFlow;
            _remoteNow = remoteNow;
            _modernDevice = modernDevice;
            MobileProfile = mobileProfile; _mobileMqtt31 = mobileMqtt31;
            _apkSubscriptions = apkSubscriptions; _rejectSubscription = rejectSubscription;
            _bootstrapAcl = bootstrapAcl;
            _powerControl = powerControl;
            _retainedPowerState = retainedPowerState; _stateBeforeSuback = stateBeforeSuback;
            _initiallyPaired = initiallyPaired;
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=ControllerHelper.Hisense.Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            Certificate = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
            _listener.Start(); Completion = Serve();
        }
        private async Task Serve()
        {
            try
            {
                using TcpClient socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                using X509Certificate2 expectedClient = HisenseTestCertificates.Load();
                string remoteNowFingerprint = HisenseTestCertificates.Fingerprint(remoteNow: true);
                string iosFingerprint = HisenseTestCertificates.Fingerprint(ios: true);
                // Distinct generated identities still exercise profile selection and mutual TLS.
                using SslStream tls = new(socket.GetStream(), false, (_, certificate, _, _) =>
                {
                    _remoteCertificate = certificate?.GetCertHashString() == remoteNowFingerprint;
                    _iosCertificate = certificate?.GetCertHashString() == iosFingerprint;
                    ClientCertificateReceived = certificate is not null && (_remoteCertificate || _iosCertificate || WebOsSecurity.Fingerprint(certificate) == WebOsSecurity.Fingerprint(expectedClient));
                    return ClientCertificateReceived;
                });
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    { ServerCertificate = Certificate, ClientCertificateRequired = true, EnabledSslProtocols = SslProtocols.Tls12 }, _stop.Token);
                bool paired = false, selected = false, standby = false;
                string currentClient = Client;
                string ReplyTopic(string kind) => $"/remoteapp/mobile/{currentClient}/ui_service/data/{kind}";
                HashSet<string> grantedTopics = new(StringComparer.Ordinal);
                Task Reply(string topic, string payload) => !_bootstrapAcl || grantedTopics.Contains(topic)
                    ? Publish(tls, topic, payload, _stop.Token) : Task.CompletedTask;
                while (!_stop.IsCancellationRequested)
                {
                    (int kind, byte[] body) = await ReadPacket(tls, _stop.Token);
                    if (kind == 1)
                    {
                        ConnectReceived = true;
                        int protocolLength = body[0] * 256 + body[1];
                        string protocol = Encoding.UTF8.GetString(body, 2, protocolLength);
                        byte level = body[2 + protocolLength];
                        Check(protocol == "MQTT" && level == 4 || protocol == "MQIsdp" && level == 3, "Invalid CONNECT protocol");
                        int offset = 2 + protocolLength + 4;
                        string Field()
                        {
                            int length = body[offset] * 256 + body[offset + 1]; offset += 2;
                            string text = Encoding.UTF8.GetString(body, offset, length); offset += length; return text;
                        }
                        currentClient = Field();
                        byte flags = body[3 + protocolLength];
                        string? willTopic = null, willPayload = null;
                        if ((flags & 4) != 0) { willTopic = Field(); willPayload = Field(); }
                        string username = Field(), password = Field();
                        Connections.Add((currentClient, username, password));
                        if (MobileProfile is not null &&
                            (level != (_mobileMqtt31 ? 3 : 4) ||
                             (MobileProfile == "VIDAA_V2" ? !_iosCertificate : !_remoteCertificate) ||
                             !ValidMobileConnect(MobileProfile, currentClient, username, password) ||
                             flags != 0xE6 || body[4 + protocolLength] != 0 || body[5 + protocolLength] != 90 ||
                             willTopic?.StartsWith("/mobile/", StringComparison.Ordinal) != true || willPayload != ""))
                        {
                            ProtocolRefusals++;
                            await SendPacket(tls, 0x20, [0, 5], _stop.Token);
                            tls.Dispose(); socket.Dispose(); await Serve(); return;
                        }
                        // Match the captured VIDAA 3290 wire format, not the production generator.
                        if (_modernDevice && (protocol != "MQTT" || level != 4 || _remoteCertificate ||
                            !ValidModernConnect(currentClient, username, password)))
                        {
                            ProtocolRefusals++;
                            await SendPacket(tls, 0x20, [0, 5], _stop.Token);
                            tls.Dispose(); socket.Dispose(); await Serve(); return;
                        }
                        if (_remoteNow && (protocol != "MQIsdp" || level != 3 || !_remoteCertificate))
                        {
                            ProtocolRefusals++;
                            await SendPacket(tls, 0x20, [0, 5], _stop.Token);
                            tls.Dispose(); socket.Dispose(); await Serve(); return;
                        }
                        if (_remoteNow)
                            Check(currentClient.EndsWith("$normal") && username == "hisenseservice" && password == "multimqttservice", "Incorrect RemoteNOW identity/credentials");
                        if (Connections.Count <= _rejections.Length)
                        {
                            await SendPacket(tls, 0x20, [0, _rejections[Connections.Count - 1]], _stop.Token);
                            tls.Dispose(); socket.Dispose();
                            // Only credential refusals may be retried by the client.
                            if (_rejections[Connections.Count - 1] is 4 or 5) await Serve();
                            return;
                        }
                        paired = _initiallyPaired || password is Access or Refresh;
                        await SendPacket(tls, 0x20, [0, 0], _stop.Token);
                    }
                    else if (kind == 8)
                    {
                        List<byte> response = [body[0], body[1]];
                        List<string> topics = [];
                        for (int offset = 2; offset < body.Length;)
                        {
                            int length = body[offset] * 256 + body[offset + 1];
                            string subscribed = Encoding.UTF8.GetString(body, offset + 2, length);
                            Check(_dynamicFlow || MobileProfile is not null || !subscribed.EndsWith("/tokenissuance"), "Legacy connection subscribed to token protocol");
                            Check(body[offset + 2 + length] == 0, "APK subscription QoS must be zero");
                            topics.Add(subscribed);
                            offset += length + 3; response.Add(0);
                        }
                        SubscriptionBatches.Add(topics.ToArray());
                        if (_apkSubscriptions && !topics.SequenceEqual(ApkSubscriptionTopics(currentClient)))
                        {
                            int unsupported = topics.FindIndex(topic => !ApkSubscriptionTopics(currentClient).Contains(topic));
                            response[2 + Math.Max(0, unsupported)] = 128;
                        }
                        if (_rejectSubscription is not null)
                            for (int i = 0; i < topics.Count; i++)
                                if (topics[i].EndsWith("/" + _rejectSubscription, StringComparison.Ordinal)) response[i + 2] = 128;
                        // Captured on the user's U7QF before PIN: 0,0,0,0,128,128,128,128,128.
                        // A refresh credential also permits token exchange, not control. Optional
                        // app/TV-info feeds stay denied even after access-token authentication.
                        if (_bootstrapAcl)
                            for (int i = 0; i < topics.Count; i++)
                                if (topics[i].EndsWith("/gettvinfo") || topics[i].EndsWith("/applist") || topics[i].EndsWith("/applisticon") ||
                                    Connections.Last().Password != Access && (topics[i].EndsWith("/sourcelist") || topics[i] == HisenseSession.StateTopic))
                                    response[i + 2] = 128;
                        SubscriptionCodes.Add(response.Skip(2).ToArray());
                        for (int i = 0; i < topics.Count; i++)
                            if (response[i + 2] < 128) grantedTopics.Add(topics[i]);
                        bool sendSnapshot = _retainedPowerState && grantedTopics.Contains(HisenseSession.StateTopic);
                        const string snapshot = "{\"statetype\":\"sourceswitch\",\"sourceid\":\"27\",\"is_signal\":0}";
                        if (sendSnapshot && _stateBeforeSuback) await Publish(tls, HisenseSession.StateTopic, snapshot, _stop.Token, retained: true);
                        await SendPacket(tls, 0x90, [.. response], _stop.Token);
                        if (sendSnapshot && !_stateBeforeSuback) await Publish(tls, HisenseSession.StateTopic, snapshot, _stop.Token, retained: true);
                    }
                    else if (kind == 3)
                    {
                        int length = body[0] * 256 + body[1];
                        string topic = Encoding.UTF8.GetString(body, 2, length), payload = Encoding.UTF8.GetString(body, 2 + length, body.Length - length - 2);
                        if (topic.EndsWith("/sourcelist"))
                        {
                            bool authorized = paired && (MobileProfile is null || Connections.Last().Password is Access or Refresh);
                            await Reply(authorized ? ReplyTopic("sourcelist") : ReplyTopic("authentication"), authorized ? Inputs : "{}");
                        }
                        else if (topic.EndsWith("/vidaa_app_connect"))
                        {
                            PairRequestReceived = true;
                            await Reply(ReplyTopic("authentication"), "{}");
                        }
                        else if (topic.EndsWith("/authenticationcodeclose")) AuthClosed = true;
                        else if (topic.EndsWith("/gettoken"))
                        {
                            Check((_dynamicFlow || MobileProfile is not null) && paired && topic == $"/remoteapp/tv/platform_service/{currentClient}/data/gettoken", "Invalid token request");
                            using var json = JsonDocument.Parse(payload);
                            string? refresh = json.RootElement.GetProperty("refreshtoken").GetString();
                            Check(refresh is "" or Refresh, "Wrong refresh credential");
                            TokenRequested = true;
                            await Reply($"/remoteapp/mobile/{currentClient}/platform_service/data/tokenissuance",
                                JsonSerializer.Serialize(new { accesstoken = Access, refreshtoken = Refresh }));
                        }
                        else if (topic.EndsWith("/authenticationcode"))
                        {
                            using var json = JsonDocument.Parse(payload);
                            var pin = json.RootElement.GetProperty("authNum");
                            Check(_remoteNow ? pin.ValueKind == JsonValueKind.String && pin.GetString() == "0123"
                                : pin.ValueKind == JsonValueKind.Number && pin.GetInt32() == 123, "Incorrect PIN wire format");
                            PinReceived = paired = true;
                            await Reply(ReplyTopic("authenticationcode"), MobileProfile is not null ? "{\"result\":0}" : "{\"result\":1}");
                        }
                        else if (topic.EndsWith("/changesource"))
                        {
                            using var json = JsonDocument.Parse(payload);
                            Check(json.RootElement.GetProperty("sourceid").GetString() == "27", "Hardcoded HDMI ID");
                            InputReceived = selected = true;
                        }
                        else if (topic.EndsWith("/sendkey"))
                        {
                            Check(_powerControl && Connections.Last().Password == Access &&
                                topic == $"/remoteapp/tv/remote_service/{currentClient}/actions/sendkey" && payload == "KEY_POWER",
                                "Power command differs from APK or lacks access token");
                            PowerKeyCount++;
                            standby = !standby;
                            await Reply(HisenseSession.StateTopic, standby ? "{\"statetype\":\"fake_sleep\"}" : "{\"statetype\":\"source\",\"sourceid\":\"27\"}");
                        }
                        else if (topic.EndsWith("/gettvstate") && (selected || _powerControl) && !_retainedPowerState)
                            await Reply(HisenseSession.StateTopic, standby ? "{\"statetype\":\"fake_sleep_0\"}" : "{\"statetype\":\"sourceswitch\",\"sourceid\":\"27\",\"is_signal\":0}");
                    }
                    else if (kind == 12) await SendPacket(tls, 0xD0, [], _stop.Token);
                    else if (kind == 14)
                    {
                        if (MobileProfile is not null) { tls.Dispose(); socket.Dispose(); await Serve(); }
                        return;
                    }
                }
            }
            catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or SocketException) { }
        }
        public async ValueTask DisposeAsync()
        { _stop.Cancel(); _listener.Stop(); await Completion; Certificate.Dispose(); _stop.Dispose(); }
    }
    private sealed class LoopbackTransport(Broker broker) : IHisenseTransport
    {
        private readonly HisenseMqttTransport _inner = new(HisenseTestCertificates.Load);
        public HisenseCredentials? Connection => _inner.Connection;
        public Task ConnectAsync(AppSettings s, CancellationToken c)
        {
            // Existing tests pin pre-APK profiles; MobileCases exercise production auto-selection.
            var settings = s.Copy();
            if (broker.MobileProfile is null && settings.HisensePreferredAuthMethod is null && !settings.HisensePaired)
                settings.HisensePreferredAuthMethod = nameof(HisenseAuthMethod.Modern);
            return _inner.ConnectEndpointAsync(settings, "127.0.0.1", broker.Port, c, version: 3290);
        }
        public Task ReconnectWithTokenAsync(HisenseAuthentication authentication, CancellationToken c) => _inner.ReconnectWithTokenAsync(authentication, c);
        public Task PublishAsync(string t, string p, CancellationToken c) => _inner.PublishAsync(t, p, c);
        public ValueTask<HisenseMessage> ReadAsync(CancellationToken c) => _inner.ReadAsync(c);
        public bool TryRead(out HisenseMessage? message) => _inner.TryRead(out message);
        public void Drain() => _inner.Drain();
        public void Dispose() => _inner.Dispose();
    }
    private static async Task RealBrokerFlow()
    {
        await using Broker broker = new(); AppSettings s = Settings(); s.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
        using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await session.ConnectAsync(s, default);
        var inputs = await session.PairAsync(_ => Task.FromResult<string?>("0123"), default);
        Check(inputs.Count == 2 && broker.PinReceived && broker.ClientCertificateReceived, "Pairing or client certificate failed");
        await session.SelectInputAsync("27", default);
        Check(broker.InputReceived, "No source command");
    }
    private static async Task RemoteNowBrokerFlow()
    {
        await using Broker broker = new(remoteNow: true);
        AppSettings settings = Settings(); settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
        using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await session.ConnectAsync(settings, default);
        await session.PairAsync(_ => Task.FromResult<string?>("0123"), default);
        await session.SelectInputAsync("27", default);
        Check(broker.PinReceived && broker.InputReceived && !broker.TokenRequested, "RemoteNOW pairing/input failed");
        Check(broker.ProtocolRefusals > 0, "Fixture did not reproduce the previous MQTT refusal");
    }
    private static async Task RemoteNowProfileReuse()
    {
        AppSettings settings = Settings();
        settings.HisensePreferredAuthMethod = HisenseAuthMethod.RemoteNow31.ToString();
        await using Broker broker = new(remoteNow: true, initiallyPaired: true);
        settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
        using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await session.ConnectAsync(settings, default);
        await session.GetInputsAsync(default);
        session.ApplyConnectionSettings(settings);
        Check(broker.Connections.Count == 1 && broker.ProtocolRefusals == 0 && !broker.PinReceived, "Saved profile retried incompatible methods");
        var loaded = SettingsStore.Deserialize(SettingsStore.Serialize(settings), out _)!;
        Check(loaded.HisensePreferredAuthMethod == "RemoteNow31" && loaded.HisenseClientId == Client, "Profile or stable identity not saved");
        var changed = loaded.Copy(); changed.HisenseIpAddress = "192.0.2.99";
        HisenseSettings.Reconcile(loaded, changed);
        Check(changed.HisensePreferredAuthMethod is null, "Profile reused for different TV");
    }
    private static Task BusyRemoteCancelsPin() => PairingStatusCancelsPrompt("authenticationcodetoast");
    private static Task ExpiredPinCancelsPrompt() => PairingStatusCancelsPrompt("authenticationcodeclose");
    private static async Task PairingStatusCancelsPrompt(string status)
    {
        Fake fake = new();
        fake.OnSend = (topic, _) => { if (topic.EndsWith("/sourcelist")) fake.Put(Reply("authentication", "{}")); };
        bool promptCanceled = false;
        using HisenseSession session = new(fake, Client);
        await Expect<InvalidOperationException>(() => session.PairAsync(async token =>
        {
            fake.Put(Reply(status, "\"\""));
            try { await Task.Delay(Timeout.Infinite, token); return "0123"; }
            finally { promptCanceled = token.IsCancellationRequested; }
        }, default));
        Check(promptCanceled && !fake.Sent.Any(s => s.Topic.EndsWith("/authenticationcode")), "Busy/expired PIN was submitted or prompt remained active");
    }
    private static async Task CancelWaitingPin()
    {
        Fake fake = new();
        fake.OnSend = (_, _) => fake.Put(Reply("authentication", "{}"));
        using HisenseSession session = new(fake, Client);
        using CancellationTokenSource cancel = new();
        await Expect<OperationCanceledException>(() => session.PairAsync(async token =>
        { cancel.Cancel(); await Task.Delay(Timeout.Infinite, token); return "0123"; }, cancel.Token));
        Check(!fake.Sent.Any(s => s.Topic.EndsWith("/authenticationcode")), "Cancellation sent PIN");
    }
    private static async Task RemoteNowWrongServerCertificate()
    {
        await using Broker broker = new(remoteNow: true);
        var settings = Settings(); settings.HisensePreferredAuthMethod = "RemoteNow31";
        using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await Expect<TelevisionCertificateException>(() => session.ConnectAsync(settings, default));
        Check(!broker.ConnectReceived, "Legacy profile bypassed server trust");
    }
    private static async Task SourceNameNotTransmitted()
    {
        Fake fake = Sources();
        var sourceHandler = fake.OnSend;
        fake.OnSend = (topic, payload) =>
        {
            sourceHandler!(topic, payload);
            if (topic.EndsWith("/changesource"))
            {
                using var json = JsonDocument.Parse(payload);
                Check(json.RootElement.EnumerateObject().Count() == 1 && json.RootElement.GetProperty("sourceid").GetString() == "27", "Display name sent as protocol input name");
                fake.Put(new(HisenseSession.StateTopic, Utf8("{\"sourceid\":\"27\"}")));
            }
        };
        using HisenseSession session = new(fake, Client);
        await session.SelectInputAsync("27", default);
    }
    private static async Task WrongCertificate()
    {
        await using Broker broker = new(); using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await Expect<TelevisionCertificateException>(() => session.ConnectAsync(Settings(), default));
        await broker.Completion;
        Check(!broker.ConnectReceived, "MQTT credentials sent before trust validation");
    }
    private static async Task<(int, byte[])> ReadPacket(Stream stream, CancellationToken c)
    {
        byte[] header = new byte[1]; await stream.ReadExactlyAsync(header, c); int kind = header[0] >> 4, length = 0, multiplier = 1;
        for (int i = 0; i < 4; i++)
        {
            await stream.ReadExactlyAsync(header, c); length += (header[0] & 127) * multiplier;
            if ((header[0] & 128) == 0) break; multiplier *= 128;
        }
        if (length > 65536) throw new InvalidDataException();
        byte[] body = new byte[length]; await stream.ReadExactlyAsync(body, c); return (kind, body);
    }
    private static async Task SendPacket(Stream stream, byte header, byte[] body, CancellationToken c)
    {
        List<byte> bytes = [header]; int length = body.Length;
        do { int value = length % 128; length /= 128; bytes.Add((byte)(value | (length > 0 ? 128 : 0))); } while (length > 0);
        bytes.AddRange(body); await stream.WriteAsync(bytes.ToArray(), c);
    }
    private static Task Publish(Stream stream, string topic, string payload, CancellationToken c, bool retained = false)
    { byte[] text = Utf8(topic); return SendPacket(stream, (byte)(retained ? 0x31 : 0x30), [(byte)(text.Length >> 8), (byte)text.Length, .. text, .. Utf8(payload)], c); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
}
