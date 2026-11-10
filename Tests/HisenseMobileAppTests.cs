using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ControllerHelper.Models;
using ControllerHelper.Services;

internal static partial class HisenseMqttTests
{
    private static (string Name, Func<Task> Run)[] MobileCases =>
    [
        ("Hisense APK: SUBSCRIBE совпадает с девятью каналами пульта", MobileSubscriptionsMatchApk),
        ("Hisense: отказ подписки записывает все SUBACK и точный этап без секретов", MobileSubscriptionDiagnostics),
        ("Hisense U7QF: SUBACK из логов допускает PIN, токен и подтверждение HDMI", () => MobileBootstrapFlow(false)),
        ("Hisense U7QF: этапы подписки сохраняются при MQTT 3.1", () => MobileBootstrapFlow(true)),
        ("Hisense U7QF: сохранённый access token допускает отказ лишних каналов", MobileAccessSubscriptions),
        ("Hisense U7QF: обязательные каналы PIN нельзя игнорировать", MobileBootstrapRequired),
        ("Hisense U7QF: отказ каналов управления после токена останавливает сопряжение", MobileControlRequired),
        ("Hisense U7QF: отмена PIN не выдаётся за сопряжение", MobileBootstrapCancel),
        ("Hisense: подробный trace исключает PIN, токены и произвольный payload", SafeProtocolTrace),
        ("Hisense APK: V2 использует IOS-сертификат, PIN и отдельное соединение с токеном", () => MobileFlow("VIDAA_V2", false, HisenseAuthMethod.MobileV2, 2)),
        ("Hisense APK: V1 использует старый сертификат с динамическим паролем", () => MobileFlow("VIDAA_V1", false, HisenseAuthMethod.MobileV1, 4)),
        ("Hisense APK: V1b сочетает новый пароль и старый сертификат", () => MobileFlow("VIDAA_V1B", false, HisenseAuthMethod.MobileV1b, 6)),
        ("Hisense APK: Paho fallback MQTT 3.1 сохраняется для токена", () => MobileFlow("VIDAA_V2", true, HisenseAuthMethod.MobileV2Mqtt31, 3)),
        ("Hisense APK: неверный server pin блокирует IOS-профиль", MobileServerPin),
        ("Hisense APK: неверный PIN не запрашивает токен", MobileBadPin),
        ("Hisense APK: refresh восстанавливает отдельное соединение доступа", MobileRefresh),
        ("Hisense APK: токен привязан к идентичности и не содержит сохранённый пароль", MobileTokenIdentity)
    ];

    // ba.k.K in the supplied APK: independent exact ordering and topic list.
    private static string[] ApkSubscriptionTopics(string client) => new[] {
        "ui_service/data/authentication", "ui_service/data/authenticationcode", "ui_service/data/authenticationcodeclose",
        "platform_service/data/tokenissuance", "platform_service/data/gettvinfo", "ui_service/data/sourcelist",
        "ui_service/data/applist", "ui_service/data/applisticon"
    }.Select(suffix => "/remoteapp/mobile/" + client + "/" + suffix).Append("/remoteapp/mobile/broadcast/ui_service/state").ToArray();

    private static async Task MobileSubscriptionsMatchApk()
    {
        await using Broker broker = new(mobileProfile: "VIDAA_V2", apkSubscriptions: true);
        var settings = Settings(); settings.HisensePaired = false;
        settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
        using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await session.ConnectAsync(settings, default);
        await session.PairAsync(_ => Task.FromResult<string?>("0123"), default);
        Check(broker.SubscriptionBatches.Count == 2 && broker.SubscriptionBatches.All(batch => batch.SequenceEqual(ApkSubscriptionTopics(broker.Connections[0].Client))),
            "Initial and token subscriptions differ from APK");
    }

    private static async Task MobileSubscriptionDiagnostics()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ControllerHelper.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "errors.jsonl");
        ErrorJournal previous = ErrorLog.Journal; ErrorLog.Journal = new(path);
        try
        {
            await using Broker broker = new(mobileProfile: "VIDAA_V2", apkSubscriptions: true, rejectSubscription: "tokenissuance");
            var settings = Settings(); settings.HisensePaired = false;
            settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
            using HisenseSession session = new(new LoopbackTransport(broker), Client);
            try { await session.ConnectAsync(settings, default); throw new Exception("Rejected SUBACK accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("tokenissuance") && error.Message.Contains("128"), "UI lacks rejected channel/code"); }
            Check(broker.Connections.Count == 1 && broker.SubscriptionBatches.Count == 1 && !broker.PairRequestReceived && !broker.PinReceived,
                "Subscription failure retried auth or sent PIN request");
            string log = File.ReadAllText(path);
            Check(!log.Contains(Client) && !log.Contains(broker.Connections[0].Client) && !log.Contains(broker.Connections[0].Username) &&
                !log.Contains(broker.Connections[0].Password), "Protocol trace contains credentials");
            var records = File.ReadAllLines(path).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var suback = records.Single(record => record.RootElement.GetProperty("operation").GetString() == "hisense.mqtt.suback").RootElement.GetProperty("details");
                var results = suback.GetProperty("results");
                Check(results.GetArrayLength() == 9 && results[3].GetProperty("code").GetInt32() == 128 &&
                    results[3].GetProperty("topic").GetString() == "platform_service/data/tokenissuance" &&
                    results[8].GetProperty("accepted").GetBoolean(), "Partial SUBACK results missing or mislabeled");
                Check(records.Any(record => record.RootElement.GetProperty("operation").GetString() == "hisense.auth.connect-accepted"), "CONNECT success hidden by subscription failure");
                Check(records.Any(record => record.RootElement.GetProperty("operation").GetString() == "hisense.mqtt.failed" &&
                    record.RootElement.GetProperty("details").GetProperty("phase").GetString() == "subscribe"), "Wrong failure phase");
                Check(records.Any(record => record.RootElement.GetProperty("operation").GetString() == "hisense.mqtt.packet" &&
                    record.RootElement.GetProperty("details").GetProperty("packet").TryGetProperty("returnCodes", out _)), "Wire SUBACK codes missing");
            }
            finally { foreach (var record in records) record.Dispose(); }
        }
        finally { ErrorLog.Journal = previous; if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task MobileBootstrapFlow(bool mqtt31)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ControllerHelper.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "errors.jsonl");
        ErrorJournal previous = ErrorLog.Journal; ErrorLog.Journal = new(path);
        try
        {
            await using Broker broker = new(mobileProfile: "VIDAA_V2", mobileMqtt31: mqtt31, apkSubscriptions: true, bootstrapAcl: true);
            var settings = Settings(); settings.HisensePaired = false;
            settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
            using HisenseSession session = new(new LoopbackTransport(broker), Client);
            await session.ConnectAsync(settings, default);
            Check(!broker.PinReceived && session.Authentication is null, "Bootstrap connection claimed pairing success");
            int prompts = 0;
            var inputs = await session.PairAsync(_ => { prompts++; return Task.FromResult<string?>("0123"); }, default);
            await session.SelectInputAsync("27", default);
            Check(prompts == 1 && inputs.Count == 2 && broker.TokenRequested && broker.AuthClosed && broker.InputReceived,
                "Captured SUBACK did not reach PIN/token/control confirmation");
            Check(broker.SubscriptionCodes.Count == 2 &&
                broker.SubscriptionCodes[0].SequenceEqual(new byte[] { 0, 0, 0, 0, 128, 128, 128, 128, 128 }) &&
                broker.SubscriptionCodes[1].SequenceEqual(new byte[] { 0, 0, 0, 0, 128, 0, 128, 128, 0 }),
                "Fixture did not reproduce pre-PIN ACL and fresh access subscriptions");
            Check(broker.Connections.Last().Password == Broker.Access && session.Authentication is not null, "Missing authenticated reconnect");
            var records = File.ReadAllLines(path).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var stages = records.Where(r => r.RootElement.GetProperty("operation").GetString() == "hisense.mqtt.suback")
                    .Select(r => r.RootElement.GetProperty("details")).ToArray();
                Check(stages.Length == 2 && stages[0].GetProperty("subscriptionStage").GetString() == "pairing" &&
                    stages[1].GetProperty("subscriptionStage").GetString() == "control", "Subscription phase missing from trace");
                Check(stages[0].GetProperty("results")[5].GetProperty("requirement").GetString() == "after-token" &&
                    stages[1].GetProperty("results")[5].GetProperty("requirement").GetString() == "required" &&
                    stages[1].GetProperty("results")[4].GetProperty("requirement").GetString() == "optional", "Trace hides channel requirements");
                Check(records.Count(r => r.RootElement.GetProperty("operation").GetString() == "hisense.mqtt.subscribe.partial") == 2,
                    "Partial subscription decisions were not logged");
            }
            finally { foreach (var record in records) record.Dispose(); }
            string log = File.ReadAllText(path);
            Check(!log.Contains(Broker.Access) && !log.Contains(Broker.Refresh) && !log.Contains(broker.Connections[0].Client), "ACL trace leaks credentials");
        }
        finally { ErrorLog.Journal = previous; if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task MobileAccessSubscriptions()
    {
        await using Broker broker = new(mobileProfile: "VIDAA_V2", apkSubscriptions: true, bootstrapAcl: true);
        var settings = Settings(); settings.HisenseAuthentication = MobileToken(HisenseAuthMethod.MobileV2, DateTimeOffset.UtcNow.AddDays(1));
        settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
        using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await session.ConnectAsync(settings, default);
        await session.SelectInputAsync("27", default);
        Check(broker.Connections.Count == 1 && broker.Connections[0].Password == Broker.Access && !broker.PinReceived && broker.InputReceived,
            "Stored token did not reach control through optional subscription denials");
    }

    private static async Task MobileBootstrapRequired()
    {
        foreach (string topic in new[] { "authentication", "authenticationcode", "authenticationcodeclose", "tokenissuance" })
        {
            await using Broker broker = new(mobileProfile: "VIDAA_V2", bootstrapAcl: true, rejectSubscription: topic);
            var settings = Settings(); settings.HisensePaired = false;
            settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
            using HisenseSession session = new(new LoopbackTransport(broker), Client);
            try { await session.ConnectAsync(settings, default); throw new Exception("Required auth subscription accepted: " + topic); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("/" + topic + ":"), "Wrong required failure topic"); }
            Check(broker.Connections.Count == 1 && !broker.PairRequestReceived && !broker.TokenRequested, "Denied auth channel continued or retried");
        }
    }

    private static async Task MobileControlRequired()
    {
        foreach (string topic in new[] { "sourcelist", "state" })
        {
            await using Broker broker = new(mobileProfile: "VIDAA_V2", bootstrapAcl: true, rejectSubscription: topic);
            var settings = Settings(); settings.HisensePaired = false;
            settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
            using HisenseSession session = new(new LoopbackTransport(broker), Client);
            await session.ConnectAsync(settings, default);
            try { await session.PairAsync(_ => Task.FromResult<string?>("0123"), default); throw new Exception("Required control subscription accepted: " + topic); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("/" + topic + ":") && error.Message.Contains("128"), "Wrong control failure topic/code"); }
            Check(broker.Connections.Count == 2 && broker.PinReceived && broker.TokenRequested && !broker.InputReceived && !settings.HisensePaired,
                "Control denial was ignored or failed before PIN/token");
        }
    }

    private static async Task MobileBootstrapCancel()
    {
        await using Broker broker = new(mobileProfile: "VIDAA_V2", bootstrapAcl: true);
        var settings = Settings(); settings.HisensePaired = false;
        settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
        using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await session.ConnectAsync(settings, default);
        using CancellationTokenSource cancellation = new();
        await Expect<OperationCanceledException>(() => session.PairAsync(_ => { cancellation.Cancel(); return Task.FromResult<string?>(null); }, cancellation.Token));
        Check(!broker.PinReceived && !broker.TokenRequested && session.Authentication is null && !settings.HisensePaired, "Canceled bootstrap persisted pairing");
    }

    private static Task SafeProtocolTrace()
    {
        const string secret = "DO-NOT-LOG-PIN-0123-CLIENT-TOKEN-192.0.2.40";
        string trace = JsonSerializer.Serialize(new object?[] {
            HisenseDiagnostics.Reply("platform_service/data/tokenissuance", Utf8(JsonSerializer.Serialize(new { accesstoken = secret, refreshtoken = secret, authNum = secret }))),
            HisenseDiagnostics.Reply("ui_service/data/authenticationcode", Utf8(JsonSerializer.Serialize(new { result = 100, authNum = secret, message = secret }))),
            HisenseDiagnostics.Reply("ui_service/data/sourcelist", Utf8(JsonSerializer.Serialize(new[] { new { sourceid = secret, sourcename = secret } }))),
            HisenseDiagnostics.Reply("broadcast/ui_service/state", Utf8(JsonSerializer.Serialize(new { statetype = secret, sourceid = secret }))),
            HisenseDiagnostics.Reply("unknown-or-foreign", Utf8(secret)),
            HisenseDiagnostics.Reply("ui_service/data/authentication", Utf8(secret)),
            HisenseDiagnostics.Packet([0x10, .. Utf8(secret)], false),
            HisenseDiagnostics.Packet([0x30, .. Utf8(secret)], true),
            HisenseDiagnostics.Packet([0x90, 0x04, 0x12, 0x34, 0x80, 0], true)
        });
        Check(!trace.Contains(secret) && !trace.Contains("0123") && !trace.Contains("192.0.2.40"), "Secret/foreign/malformed payload logged");
        Check(trace.Contains("\"resultCode\":100") && trace.Contains("\"hasAccessToken\":true") &&
            trace.Contains("\"count\":1") && trace.Contains("\"returnCodes\":[128,0]"), "Safe protocol metadata missing");
        Check(HisenseDiagnostics.Topic($"/remoteapp/mobile/{secret}/ui_service/data/authentication", "another-client") == "unknown-or-foreign", "Foreign topic echoed");
        Check(HisenseSession.ResponseTopics(Client).Any(topic => topic.EndsWith("/authenticationcodetoast")) &&
            !HisenseSession.ResponseTopics(Client, true, true).Any(topic => topic.EndsWith("/authenticationcodetoast")), "Legacy/mobile subscription plans conflated");
        return Task.CompletedTask;
    }

    // Independent wire assertions transcribed from the supplied APK's ba.a/b/c,
    // af.a.n, ba.k.N/O/b0 and a1.a1, not from ControllerHelper's generator.
    private static bool ValidMobileConnect(string profile, string client, string username, string password)
    {
        var match = Regex.Match(client, @"\A(?<device>[0-9A-F]{2}(?::[0-9A-F]{2}){5})\$his\$(?<race>[0-9A-F]{6})_vidaacommon_001\z");
        if (!match.Success || !username.StartsWith("his$", StringComparison.Ordinal) ||
            !long.TryParse(username.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out long encoded)) return false;
        string Md5(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text)));
        if (match.Groups["race"].Value != Md5("38D65DC30F45109A369A86FCE866A85B$" + match.Groups["device"].Value)[..6]) return false;
        bool token = password is Broker.Access or Broker.Refresh;
        bool xor = token ? profile == "VIDAA_V2" : profile != "VIDAA_V1";
        long timestamp = xor ? encoded ^ 6239759785777146216 : encoded;
        if (Math.Abs((double)timestamp - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) > 30) return false;
        if (token) return true;
        string seconds = timestamp.ToString(CultureInfo.InvariantCulture);
        int digit = seconds.Sum(c => c - '0') % 10;
        string suffix = profile == "VIDAA_V1" ? "h*i&s%e!r^v0i1c9" : "h!i@s#$v%i^d&a*a";
        return password == Md5(seconds + "$" + Md5("his" + digit + suffix)[..6]);
    }

    private static async Task MobileFlow(string profile, bool mqtt31, HisenseAuthMethod method, int expectedConnections)
    {
        await using Broker broker = new(mobileProfile: profile, mobileMqtt31: mqtt31);
        var settings = Settings(); settings.HisensePaired = false;
        settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
        using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await session.ConnectAsync(settings, default);
        await session.PairAsync(_ => Task.FromResult<string?>("0123"), default);
        await session.SelectInputAsync("27", default);
        Check(broker.Connections.Count == expectedConnections && broker.PinReceived && broker.AuthClosed && broker.InputReceived,
            "APK handshake/fallback not completed");
        Check(broker.Connections.Last().Password == Broker.Access && session.Authentication?.Method == method, "Token reconnect/profile missing");
        session.ApplyConnectionSettings(settings); settings.HisensePaired = true;
        var loaded = SettingsStore.Deserialize(SettingsStore.Serialize(settings), out _)!;
        var next = HisenseAuthenticationProtocol.Candidates(loaded, 3290, DateTimeOffset.UtcNow).First();
        Check(next.Method == method && next.Source == "access-token" && next.ClientId == broker.Connections.Last().Client &&
            ValidMobileConnect(profile, next.ClientId, next.Username, next.Password), "Saved session cannot resume mobile wire protocol");
    }

    private static async Task MobileServerPin()
    {
        await using Broker broker = new(mobileProfile: "VIDAA_V2");
        var settings = Settings(); settings.HisensePaired = false;
        using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await Expect<TelevisionCertificateException>(() => session.ConnectAsync(settings, default));
        Check(!broker.ConnectReceived, "Mobile credentials leaked before server pin verification");
    }

    private static async Task MobileBadPin()
    {
        var connection = HisenseAuthenticationProtocol.Generate("02:11:22:33:44:55", HisenseAuthMethod.MobileV2, 1700000000);
        var fake = new Fake { Connection = connection };
        fake.OnSend = (topic, _) => fake.Put(new($"/remoteapp/mobile/{connection.ClientId}/ui_service/data/" +
            (topic.EndsWith("/authenticationcode") ? "authenticationcode" : "authentication"),
            Utf8(topic.EndsWith("/authenticationcode") ? "{\"result\":100}" : "{}")));
        using HisenseSession session = new(fake, Client);
        await Expect<InvalidOperationException>(() => session.PairAsync(_ => Task.FromResult<string?>("1234"), default));
        Check(!fake.Sent.Any(message => message.Topic.EndsWith("/gettoken")) && session.Authentication is null, "Invalid PIN accepted");
    }

    private static HisenseAuthentication MobileToken(HisenseAuthMethod method, DateTimeOffset expires)
    {
        var credentials = HisenseAuthenticationProtocol.Generate(HisenseAuthenticationProtocol.DeviceIdentity(Client).ToUpperInvariant(), method, 1700000000);
        return new() { ClientId = credentials.ClientId, Username = credentials.Username, Method = method,
            AccessToken = Broker.Access, RefreshToken = Broker.Refresh, AccessExpires = expires, RefreshExpires = DateTimeOffset.UtcNow.AddDays(30) };
    }

    private static async Task MobileRefresh()
    {
        await using Broker broker = new(mobileProfile: "VIDAA_V1B", bootstrapAcl: true);
        var settings = Settings(); settings.HisenseAuthentication = MobileToken(HisenseAuthMethod.MobileV1b, DateTimeOffset.UtcNow.AddDays(-1));
        settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
        using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await session.ConnectAsync(settings, default);
        await session.GetInputsAsync(default);
        Check(broker.Connections.Select(item => item.Password).SequenceEqual([Broker.Refresh, Broker.Access]) && !broker.PinReceived,
            "Refresh did not transition to access session without PIN");
    }

    private static Task MobileTokenIdentity()
    {
        var settings = Settings(); settings.HisenseAuthentication = MobileToken(HisenseAuthMethod.MobileV2, DateTimeOffset.UtcNow.AddDays(1));
        var now = DateTimeOffset.UtcNow;
        var next = HisenseAuthenticationProtocol.Candidates(settings, 3290, now).First();
        Check(next.Username != settings.HisenseAuthentication.Username && next.Password == Broker.Access, "Token session reused old timestamp");
        settings.HisenseClientId += "changed";
        Check(HisenseAuthenticationProtocol.Candidates(settings, 3290, now).All(c => c.Source == "generated"), "Foreign identity reused token");
        var credentials = HisenseAuthenticationProtocol.Generate("02:11:22:33:44:55", HisenseAuthMethod.MobileV2, 1700000000);
        var auth = HisenseAuthentication.FromReply(Utf8("{\"accesstoken\":\"sample-token\",\"accesstoken_duration_day\":\"2\"}"), credentials, now);
        Check(auth.AccessExpires == now.AddDays(2), "Numeric string duration rejected");
        long issued = now.AddDays(-1).ToUnixTimeSeconds();
        var issuedAuth = HisenseAuthentication.FromReply(Utf8(JsonSerializer.Serialize(new { accesstoken = "sample-token", accesstoken_time = issued, accesstoken_duration_day = 2 })), credentials, now);
        Check(issuedAuth.AccessExpires == DateTimeOffset.FromUnixTimeSeconds(issued).AddDays(2), "Token lifetime extended from receipt instead of issue time");
        return Task.CompletedTask;
    }
}
