using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Globalization;
using System.Xml;
using ControllerHelper.Models;
using ControllerHelper.Services;

internal static partial class HisenseMqttTests
{
    private static (string Name, Func<Task> Run)[] AuthCases =>
    [
        ("Hisense: VIDAA 3290 проверяет формат идентичности в настоящем CONNECT", ModernDeviceBrokerFlow),
        ("Hisense: формула совпадает с захваченными credentials мобильного VIDAA", CapturedMobileCredentials),
        ("Hisense: идентичность клиента постоянна и не заимствует MAC телевизора", StableDeviceIdentity),
        ("Hisense: отклонённая старая идентичность переходит на формат VIDAA", OldIdentityFallback),
        ("Hisense: MQTT NotAuthorized вызывает переход к другому протоколу", AuthFallback),
        ("Hisense: динамическое сопряжение выдаёт токен и использует новый clientId", DynamicPairing),
        ("Hisense: access token используется для повторного подключения", TokenReconnect),
        ("Hisense: истёкший access token обновляется через refresh token", TokenRefresh),
        ("Hisense: отказ доступа к токену допускает refresh", RejectedTokenRefresh),
        ("Hisense: отказ сервера не запускает перебор авторизации", ServerUnavailable),
        ("Hisense: отказ всех методов ограничивает количество попыток", AllAuthRejected),
        ("Hisense: версия протокола читается без DTD и внешних сущностей", ProtocolVersions),
        ("Hisense: credentials совпадают с независимыми векторами VIDAA", CredentialVectors),
        ("Hisense: токены шифруются и очищаются при смене ТВ", TokenPersistence),
        ("Hisense: некорректные и просроченные токены не используются", InvalidTokens),
        ("Hisense: broadcast authentication вызывает PIN", BroadcastAuthentication),
        ("Hisense: динамические credentials и токены отсутствуют в логах", DynamicPrivateLogging)
    ];

    private static bool ValidModernConnect(string client, string username, string password)
    {
        var match = Regex.Match(client, @"\A(?<device>[0-9a-f]{2}(?::[0-9a-f]{2}){5})\$his\$(?<race>[0-9A-F]{6})_vidaacommon_001\z");
        if (!match.Success || !username.StartsWith("his$", StringComparison.Ordinal) ||
            !long.TryParse(username.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out long encoded)) return false;
        string Md5(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text)));
        if (match.Groups["race"].Value != Md5("38D65DC30F45109A369A86FCE866A85B$" + match.Groups["device"].Value)[..6]) return false;
        if (password is Broker.Access or Broker.Refresh) return true;
        long timestamp = encoded ^ 6239759785777146216;
        if (Math.Abs((double)timestamp - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) > 30) return false;
        string seconds = timestamp.ToString(CultureInfo.InvariantCulture);
        int digit = seconds.Sum(c => c - '0') % 10;
        return password == Md5(seconds + "$" + Md5("his" + digit + "h!i@s#$v%i^d&a*a")[..6]);
    }

    private static async Task ModernDeviceBrokerFlow()
    {
        await using Broker broker = new(dynamicFlow: true, modernDevice: true);
        var settings = Settings(); settings.HisensePaired = false;
        settings.HisenseClientId = "ControllerHelper_0123456789abcdef0123456789abcdef";
        settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
        using HisenseSession session = new(new LoopbackTransport(broker), settings.HisenseClientId);
        await session.ConnectAsync(settings, default);
        await session.PairAsync(_ => Task.FromResult<string?>("0123"), default);
        await session.SelectInputAsync("27", default);
        Check(broker.Connections.Count == 1 && broker.ProtocolRefusals == 0 && broker.PinReceived && broker.InputReceived,
            "The current VIDAA profile did not complete pairing/input in one attempt");
        session.ApplyConnectionSettings(settings);
        var reloaded = SettingsStore.Deserialize(SettingsStore.Serialize(settings), out _)!;
        var reconnect = HisenseAuthenticationProtocol.Candidates(reloaded, 3290, DateTimeOffset.UtcNow).First();
        Check(reconnect.Source == "access-token" && reconnect.ClientId == broker.Connections[0].Client,
            "New wire identity/token lost after saving settings");
    }

    private static Task CapturedMobileCredentials()
    {
        // Published logcat capture: https://github.com/nikagl/hisense/issues/14.
        // These are historical public protocol vectors, never used for production identity.
        foreach (var vector in new[] {
            (1764588704L, "his$6239759786165851592", "83754D6E1CBB35BF506288810B7ABD68"),
            (1764588725L, "his$6239759786165851613", "F552BF38B24FB9D8BBCACC0FFDBF0F8D"),
            (1764589244L, "his$6239759786165852116", "E5AE6E6511A86CE7CA6B87B4AAB5323E") })
        {
            var credentials = HisenseAuthenticationProtocol.Generate("dd:0d:49:eb:a2:8a", HisenseAuthMethod.Modern, vector.Item1);
            Check(credentials.ClientId == "dd:0d:49:eb:a2:8a$his$8F34B2_vidaacommon_001" &&
                credentials.Username == vector.Item2 && credentials.Password == vector.Item3, "Mobile capture vector mismatch");
        }
        return Task.CompletedTask;
    }

    private static Task StableDeviceIdentity()
    {
        var settings = Settings(); settings.HisensePaired = false;
        settings.HisensePreferredAuthMethod = nameof(HisenseAuthMethod.Modern);
        string seed = settings.HisenseClientId!;
        var now = DateTimeOffset.UtcNow;
        var first = HisenseAuthenticationProtocol.Candidates(settings, 3290, now).First();
        var changed = settings.Copy(); changed.HisenseMacAddress = "00:AA:BB:CC:DD:EE";
        var next = HisenseAuthenticationProtocol.Candidates(changed, 3290, now.AddSeconds(2)).First();
        string device = first.ClientId.Split('$')[0];
        Check(first.ClientId == next.ClientId && first.Password != next.Password &&
            first.ClientId != HisenseAuthenticationProtocol.Generate(seed, HisenseAuthMethod.Modern, now.ToUnixTimeSeconds()).ClientId,
            "Identity changed with time/TV MAC, or raw app seed was sent");
        Check(Regex.IsMatch(device, @"\A[0-9a-f]{2}(:[0-9a-f]{2}){5}\z") &&
            (Convert.ToByte(device[..2], 16) & 3) == 2, "Not a local unicast device identity");
        changed.HisenseClientId += "different";
        Check(HisenseAuthenticationProtocol.Candidates(changed, 3290, now).First().ClientId != first.ClientId, "Separate installs share identity");
        Check(settings.HisenseClientId == seed && !first.ClientId.Contains(seed), "Stored seed mutated or exposed on wire");
        Check(HisenseAuthenticationProtocol.IdentityFormat(first) == "device-mac", "Wrong diagnostic format");
        return Task.CompletedTask;
    }

    private static async Task OldIdentityFallback()
    {
        await using Broker broker = new(dynamicFlow: true, modernDevice: true);
        var settings = Settings();
        settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
        settings.HisenseAuthentication = SavedToken(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
        settings.HisensePreferredAuthMethod = nameof(HisenseAuthMethod.Modern);
        using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await session.ConnectAsync(settings, default);
        await session.PairAsync(_ => Task.FromResult<string?>("0123"), default);
        session.ApplyConnectionSettings(settings);
        Check(broker.Connections.Count == 2 && broker.ProtocolRefusals == 1 &&
            broker.Connections[0].Client.StartsWith(Client + "$his$", StringComparison.Ordinal), "Old paired identity was not tried first");
        var reconnect = HisenseAuthenticationProtocol.Candidates(settings, 3290, DateTimeOffset.UtcNow).First();
        Check(reconnect.Source == "access-token" && reconnect.ClientId == broker.Connections[1].Client,
            "New pairing kept stale identity");
    }

    private static async Task AuthFallback()
    {
        await using Broker broker = new([5, 4], dynamicFlow: true);
        var settings = Settings(); settings.HisensePaired = false;
        settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
        using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await session.ConnectAsync(settings, default);
        await session.PairAsync(_ => Task.FromResult<string?>("0123"), default);
        Check(broker.Connections.Count == 3 && broker.Connections[0].Client == broker.Connections[2].Client, "Fallback identity changed");
        Check(broker.Connections[0].Password != broker.Connections[1].Password && broker.Connections[1].Username != broker.Connections[2].Username,
            "Fallback did not change auth variant");
        Check(session.Authentication?.Method == HisenseAuthMethod.Legacy, "Wrong accepted variant");
    }

    private static async Task DynamicPairing()
    {
        await using Broker broker = new(dynamicFlow: true);
        var settings = Settings(); settings.HisensePaired = false;
        settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
        using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await session.ConnectAsync(settings, default);
        await session.PairAsync(_ => Task.FromResult<string?>("0123"), default);
        await session.SelectInputAsync("27", default);
        Check(broker.PairRequestReceived && broker.TokenRequested && broker.AuthClosed && broker.InputReceived, "Dynamic handshake incomplete");
        Check(session.Authentication?.AccessToken == Broker.Access && session.Authentication.ClientId == broker.Connections[0].Client,
            "Token not bound to accepted client ID");
    }

    private static HisenseAuthentication SavedToken(DateTimeOffset accessExpires, DateTimeOffset refreshExpires)
    {
        var credentials = HisenseAuthenticationProtocol.Generate(Client, HisenseAuthMethod.Modern, 1700000000);
        return new()
        {
            ClientId = credentials.ClientId, Username = credentials.Username, Method = credentials.Method,
            AccessToken = Broker.Access, RefreshToken = Broker.Refresh, AccessExpires = accessExpires, RefreshExpires = refreshExpires
        };
    }

    private static Task TokenReconnect() => TokenFlow(expired: false, rejected: false);
    private static Task TokenRefresh() => TokenFlow(expired: true, rejected: false);
    private static Task RejectedTokenRefresh() => TokenFlow(expired: false, rejected: true);
    private static async Task TokenFlow(bool expired, bool rejected)
    {
        await using Broker broker = new(rejected ? [5] : [], dynamicFlow: true);
        var settings = Settings(); settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
        settings.HisenseAuthentication = SavedToken(DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1), DateTimeOffset.UtcNow.AddDays(30));
        using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await session.ConnectAsync(settings, default);
        await session.GetInputsAsync(default);
        Check(broker.Connections.Last().Password == (expired || rejected ? Broker.Refresh : Broker.Access), "Incorrect token for reconnect");
        Check(broker.Connections.Last().Username == settings.HisenseAuthentication.Username, "Saved username was regenerated");
        Check(broker.TokenRequested == (expired || rejected) && !broker.PinReceived && !broker.PairRequestReceived, "Unexpected token refresh or PIN");
        Check(session.Authentication?.AccessExpires > DateTimeOffset.UtcNow, "New token not returned");
    }

    private static async Task ServerUnavailable()
    {
        await using Broker broker = new([3]);
        var settings = Settings(); settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
        using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await Expect<InvalidOperationException>(() => session.ConnectAsync(settings, default));
        Check(broker.Connections.Count == 1, "Server error caused auth retries");
    }

    private static async Task AllAuthRejected()
    {
        await using Broker broker = new(Enumerable.Repeat((byte)5, 13).ToArray());
        var settings = Settings(); settings.HisensePaired = false;
        settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
        using HisenseSession session = new(new LoopbackTransport(broker), Client);
        await Expect<InvalidOperationException>(() => session.ConnectAsync(settings, default));
        Check(broker.Connections.Count == 13 && !broker.PinReceived, "Unbounded retries or PIN sent after refusal");
    }

    private static async Task ProtocolVersions()
    {
        Check(HisenseAuthenticationProtocol.ParseVersion("<root xmlns:v='urn:test'><v:transport_protocol>3290</v:transport_protocol></root>") == 3290, "Namespaced descriptor failed");
        Check(HisenseAuthenticationProtocol.ParseVersion("<root><modelDescription>transport_protocol=3000</modelDescription></root>") == 3000, "Embedded version failed");
        Check(HisenseAuthenticationProtocol.ParseVersion("<root>unknown</root>") is null, "Invented version");
        await Expect<XmlException>(() => Task.FromResult(HisenseAuthenticationProtocol.ParseVersion("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///never-read'>]><root>&x;</root>")));
        foreach (var (version, method) in new[] { (2999, HisenseAuthMethod.MobileV1), (3000, HisenseAuthMethod.MobileV1), (3290, HisenseAuthMethod.MobileV2) })
        {
            var settings = Settings(); settings.HisensePaired = false;
            Check(HisenseAuthenticationProtocol.Candidates(settings, version, DateTimeOffset.UtcNow).First().Method == method, "Wrong auth preference");
        }
    }

    private static Task CredentialVectors()
    {
        // Known answers computed independently from the pinned reference formula.
        var modern = HisenseAuthenticationProtocol.Generate(Client, HisenseAuthMethod.Modern, 1700000000);
        var middle = HisenseAuthenticationProtocol.Generate(Client, HisenseAuthMethod.Middle, 1700000000);
        var legacy = HisenseAuthenticationProtocol.Generate(Client, HisenseAuthMethod.Legacy, 1700000000);
        Check(modern.ClientId == "ControllerHelper_Test$his$58B2D0_vidaacommon_001", "Client ID vector mismatch");
        Check(modern.Username == "his$6239759786369374312" && middle.Username == modern.Username && legacy.Username == "his$1700000000", "Username vector mismatch");
        Check(modern.Password == "E4B16BE39DA56468522CACF7411FB50F" && middle.Password == "30CC23251954D2B965504DC81E944805" && legacy.Password == middle.Password, "Password vector mismatch");
        return Task.CompletedTask;
    }

    private static async Task TokenPersistence()
    {
        var settings = Settings(); settings.HisenseAuthentication = SavedToken(DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(30));
        string json = SettingsStore.Serialize(settings);
        Check(!json.Contains(Broker.Access) && !json.Contains(Broker.Refresh) && !json.Contains(settings.HisenseAuthentication.Username), "Plaintext credentials saved");
        var loaded = SettingsStore.Deserialize(json, out bool plaintext)!;
        Check(!plaintext && loaded.HisenseAuthentication?.AccessToken == Broker.Access, "DPAPI roundtrip failed");
        var changed = loaded.Copy(); changed.HisenseCertificateSha256 = new string('B', 64);
        HisenseSettings.Reconcile(loaded, changed);
        Check(changed.HisenseAuthentication is null && changed.HisenseAuthenticationProtected is null && !changed.HisensePaired, "Changed trust retained token");
        var broken = Settings(); broken.HisenseAuthenticationProtected = "invalid-ciphertext";
        var unavailable = SettingsFileStore.Deserialize(JsonSerializer.Serialize(broken), out _, out string? warning)!;
        Check(warning is not null && !unavailable.HisensePaired && unavailable.HisenseAuthenticationProtected == "invalid-ciphertext", "Failed DPAPI erased ciphertext or claimed pairing");
        await Task.CompletedTask;
    }

    private static async Task InvalidTokens()
    {
        var credentials = HisenseAuthenticationProtocol.Generate(Client, HisenseAuthMethod.Modern, 1700000000);
        foreach (string json in new[] { "{}", "[]", "{\"accesstoken\":\"\"}", "{\"accesstoken\":\"a\",\"accesstoken_duration_day\":-1}" })
            await Expect<InvalidDataException>(() => Task.FromResult(HisenseAuthentication.FromReply(Utf8(json), credentials, DateTimeOffset.UtcNow)));
        var settings = Settings(); settings.HisenseAuthentication = SavedToken(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
        Check(HisenseAuthenticationProtocol.Candidates(settings, null, DateTimeOffset.UtcNow).All(c => c.Source == "generated"), "Expired token reused");
        settings.HisenseClientId += "changed";
        settings.HisenseAuthentication = SavedToken(DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow.AddDays(30));
        Check(HisenseAuthenticationProtocol.Candidates(settings, null, DateTimeOffset.UtcNow).All(c => c.Source == "generated"), "Token reused for different identity");
    }

    private static async Task BroadcastAuthentication()
    {
        var fake = new Fake();
        fake.OnSend = (_, _) => fake.Put(new(HisenseSession.StateTopic, Utf8("{\"statetype\":\"authentication\"}")));
        using HisenseSession session = new(fake, Client);
        bool requested = false;
        await Expect<OperationCanceledException>(() => session.PairAsync(_ => { requested = true; return Task.FromResult<string?>(null); }, default));
        Check(requested, "Broadcast auth state was ignored");
    }

    private static async Task DynamicPrivateLogging()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ControllerHelper.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "errors.jsonl"); ErrorJournal old = ErrorLog.Journal; ErrorLog.Journal = new(path);
        try
        {
            await DynamicPairing(); await TokenRefresh();
            string log = await File.ReadAllTextAsync(path);
            Check(!log.Contains(Broker.Access) && !log.Contains(Broker.Refresh) && !log.Contains(Client) &&
                !log.Contains(HisenseAuthenticationProtocol.DeviceIdentity(Client)) && !log.Contains("his$") && !log.Contains("authNum"), "Secrets logged");
            Check(log.Contains("hisense.token.received") && log.Contains("hisense.auth.accepted"), "Missing auth diagnostics");
            Check(log.Contains("\"identityFormat\":\"device-mac\"") && log.Contains("\"transportVersion\":3290"), "Missing wire identity diagnostics");
        }
        finally { ErrorLog.Journal = old; Directory.Delete(folder, true); }
    }
}
