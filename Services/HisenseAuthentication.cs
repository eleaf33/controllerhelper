using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ControllerHelper.Models;

namespace ControllerHelper.Services;

// Append values: existing encrypted token records store these numeric enum values.
internal enum HisenseAuthMethod
{
    Modern, Middle, Legacy, Static, RemoteNow, RemoteNow31, RemoteNow311,
    MobileV1, MobileV1b, MobileV2, MobileV1Mqtt31, MobileV1bMqtt31, MobileV2Mqtt31
}

// Classes intentionally have no generated ToString containing credentials.
internal sealed class HisenseCredentials(string clientId, string username, string password,
    HisenseAuthMethod method, string source = "generated")
{
    internal string ClientId { get; } = clientId;
    internal string Username { get; } = username;
    internal string Password { get; } = password;
    internal HisenseAuthMethod Method { get; } = method;
    internal string Source { get; } = source;
    internal bool MobileApp => IsMobileApp(Method);
    internal static bool IsMobileApp(HisenseAuthMethod method) => method is >= HisenseAuthMethod.MobileV1 and <= HisenseAuthMethod.MobileV2Mqtt31;
    internal bool Dynamic => MobileApp || Method is HisenseAuthMethod.Modern or HisenseAuthMethod.Middle or HisenseAuthMethod.Legacy;
    internal bool UsesIosCertificate => Method is HisenseAuthMethod.MobileV2 or HisenseAuthMethod.MobileV2Mqtt31;
    internal bool UsesStringPin => Method is HisenseAuthMethod.RemoteNow31 or HisenseAuthMethod.RemoteNow311;
    internal bool UsesRemoteNowCertificate => UsesStringPin || MobileApp && !UsesIosCertificate;
    internal bool UsesMqtt31 => Method is HisenseAuthMethod.RemoteNow31 or HisenseAuthMethod.MobileV1Mqtt31 or HisenseAuthMethod.MobileV1bMqtt31 or HisenseAuthMethod.MobileV2Mqtt31;
}

internal sealed class HisenseAuthentication
{
    public string ClientId { get; init; } = "";
    public string Username { get; init; } = "";
    public HisenseAuthMethod Method { get; init; }
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public DateTimeOffset AccessExpires { get; init; }
    public DateTimeOffset RefreshExpires { get; init; }

    internal bool IsValid() => ClientId.Length is > 0 and <= 160 &&
        !ClientId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '$' and not ':' and not '-') &&
        Username.Length is > 0 and <= 128 && !Username.Any(char.IsControl) &&
        AccessToken.Length is > 0 and <= 4096 && RefreshToken.Length <= 4096 &&
        !AccessToken.Any(char.IsControl) && !RefreshToken.Any(char.IsControl) &&
        (HisenseCredentials.IsMobileApp(Method) || Method is HisenseAuthMethod.Modern or HisenseAuthMethod.Middle or HisenseAuthMethod.Legacy);

    internal static HisenseAuthentication FromReply(byte[] payload, HisenseCredentials connection, DateTimeOffset now)
    {
        using var json = HisenseSession.Parse(payload);
        var root = json.RootElement;
        string ReadToken(string key, bool optional = false) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : optional ? "" : throw new InvalidDataException("Hisense не выдал токен доступа.");
        int Days(string key, int fallback)
        {
            if (!root.TryGetProperty(key, out var value)) return fallback;
            string text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int days) || days is < 1 or > 3650)
                throw new InvalidDataException("Некорректный срок токена Hisense.");
            return days;
        }
        DateTimeOffset Issued(string key)
        {
            if (!connection.MobileApp || !root.TryGetProperty(key, out var value)) return now;
            string text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) || seconds is < 946684800 or > 253000000000)
                throw new InvalidDataException("Некорректное время выдачи токена Hisense.");
            DateTimeOffset issued = DateTimeOffset.FromUnixTimeSeconds(seconds);
            // Never extend a token's lifetime because the TV clock is ahead of this PC.
            return issued < now ? issued : now;
        }
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Некорректный ответ авторизации Hisense.");
        HisenseAuthentication auth = new()
        {
            ClientId = connection.ClientId, Username = connection.Username, Method = connection.Method,
            AccessToken = ReadToken("accesstoken"), RefreshToken = ReadToken("refreshtoken", true),
            AccessExpires = Issued("accesstoken_time").AddDays(Days("accesstoken_duration_day", connection.MobileApp ? 2 : 7)),
            RefreshExpires = Issued("refreshtoken_time").AddDays(Days("refreshtoken_duration_day", 30))
        };
        if (!auth.IsValid()) throw new InvalidDataException("Некорректный токен Hisense.");
        return auth;
    }
}

internal static class HisenseAuthenticationProtocol
{
    // The app's persisted random seed is NOT the wire UUID. VIDAA clients use a
    // six-octet device identity, including its delimiters, in the signed client ID.
    // This is local protocol identity, unrelated to the TV/NIC MAC used for WoL.
    internal static string DeviceIdentity(string seed)
    {
        byte[] device = SHA256.HashData(Encoding.UTF8.GetBytes(seed))[..6];
        device[0] = (byte)((device[0] | 2) & 0xfe);
        return string.Join(":", device.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    }

    internal static string IdentityFormat(HisenseCredentials credentials)
    {
        if (!credentials.Dynamic) return credentials.UsesRemoteNowCertificate ? "remotenow-device" : "legacy-static";
        string device = credentials.ClientId.Split('$')[0];
        return device.Length == 17 && device.Where((_, i) => i % 3 == 2).All(c => c == ':') &&
            device.Where((_, i) => i % 3 != 2).All(Uri.IsHexDigit) ? "device-mac" : "legacy-app-id";
    }

    // VIDAA wire algorithm, adapted from vidaa-control (MIT); attribution in ThirdParty/Hisense.
    // MD5 is required by the TV protocol, not used for local secret protection or TLS trust.
    internal static HisenseCredentials Generate(string identity, HisenseAuthMethod method, long timestamp)
    {
        if (method is HisenseAuthMethod.RemoteNow31 or HisenseAuthMethod.RemoteNow311)
        {
            // Stable locally administered identity; never copy a phone/TV MAC or change NIC settings.
            byte[] device = SHA256.HashData(Encoding.UTF8.GetBytes(identity))[..6];
            device[0] = (byte)((device[0] | 2) & 0xfe);
            // 19 characters also fits the MQTT 3.1 client ID limit.
            return new(Convert.ToHexString(device) + "$normal", "hisenseservice", "multimqttservice", method);
        }
        if (method == HisenseAuthMethod.RemoteNow) return new(identity, "hisenseservice", "multimqttservice", method);
        if (method == HisenseAuthMethod.Static)
            return new(identity.Replace(":", "").Replace("-", "").ToUpperInvariant() + "$vidaa_common", "hisenseservice", "multimqttservice", method);
        static string Hash(string value) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value)));
        if (!identity.Contains(':') && !identity.Contains('-') && identity.Length == 12)
            identity = string.Join(":", Enumerable.Range(0, 6).Select(i => identity.Substring(i * 2, 2)));
        string time = timestamp.ToString(CultureInfo.InvariantCulture);
        string client = identity + "$his$" + Hash("38D65DC30F45109A369A86FCE866A85B$" + identity)[..6] + "_vidaacommon_001";
        bool legacy = method is HisenseAuthMethod.Legacy or HisenseAuthMethod.MobileV1 or HisenseAuthMethod.MobileV1Mqtt31;
        string username = "his$" + (legacy ? timestamp : timestamp ^ 0x569814772b03a968L).ToString(CultureInfo.InvariantCulture);
        int remainder = time.Sum(c => c - '0') % 10;
        string suffix = method == HisenseAuthMethod.Modern || HisenseCredentials.IsMobileApp(method) && !legacy ? "h!i@s#$v%i^d&a*a" : "h*i&s%e!r^v0i1c9";
        string password = Hash(time + "$" + Hash("his" + remainder.ToString(CultureInfo.InvariantCulture) + suffix)[..6]);
        return new(client, username, password, method);
    }

    internal static HisenseCredentials TokenCredentials(HisenseAuthentication auth, bool refresh, DateTimeOffset now)
    {
        // The tested mobile app regenerates token-session usernames. V1b uses plain
        // seconds here, despite using XOR for its initial password-based CONNECT.
        string username = auth.Username;
        if (HisenseCredentials.IsMobileApp(auth.Method))
        {
            long seconds = now.ToUnixTimeSeconds();
            if (auth.Method is HisenseAuthMethod.MobileV2 or HisenseAuthMethod.MobileV2Mqtt31) seconds ^= 0x569814772b03a968L;
            username = "his$" + seconds.ToString(CultureInfo.InvariantCulture);
        }
        return new(auth.ClientId, username, refresh ? auth.RefreshToken : auth.AccessToken, auth.Method, refresh ? "refresh-token" : "access-token");
    }

    internal static IEnumerable<HisenseCredentials> Candidates(AppSettings settings, int? version, DateTimeOffset now)
    {
        HisenseAuthentication? saved = settings.HisenseAuthentication;
        string seed = settings.HisenseClientId!;
        string device = DeviceIdentity(seed);
        bool savedMatchesOriginal = saved is not null && saved.IsValid() && Generate(seed, saved.Method, 0).ClientId == saved.ClientId;
        string mobileDevice = device.ToUpperInvariant();
        bool savedMatchesDevice = saved is not null && saved.IsValid() &&
            Generate(HisenseCredentials.IsMobileApp(saved.Method) ? mobileDevice : device, saved.Method, 0).ClientId == saved.ClientId;
        if (saved is not null && (savedMatchesOriginal || savedMatchesDevice))
        {
            if (saved.AccessExpires > now.AddMinutes(1))
                yield return TokenCredentials(saved, false, now);
            if (saved.RefreshExpires > now.AddMinutes(1) && saved.RefreshToken.Length > 0)
                yield return TokenCredentials(saved, true, now);
        }
        HisenseAuthMethod preferred = version is null or >= 3290 ? HisenseAuthMethod.Modern :
            version >= 3000 ? HisenseAuthMethod.Middle : HisenseAuthMethod.Legacy;
        List<HisenseAuthMethod> order = [];
        if (Enum.TryParse<HisenseAuthMethod>(settings.HisensePreferredAuthMethod, out var known) && Enum.IsDefined(known))
            order.Add(known);
        else if (settings.HisensePaired && saved is null) order.Add(HisenseAuthMethod.RemoteNow); // Preserve prior pairing identity.
        HisenseAuthMethod[] older = [preferred, HisenseAuthMethod.Modern, HisenseAuthMethod.Middle, HisenseAuthMethod.Legacy,
            HisenseAuthMethod.Static, HisenseAuthMethod.RemoteNow, HisenseAuthMethod.RemoteNow31, HisenseAuthMethod.RemoteNow311];
        // Preserve fallback order for installations which already accepted an older profile.
        if (order.Count > 0 && !HisenseCredentials.IsMobileApp(order[0])) order.AddRange(older);
        if (version is null or >= 3290) order.AddRange([HisenseAuthMethod.MobileV2, HisenseAuthMethod.MobileV2Mqtt31]);
        order.AddRange([HisenseAuthMethod.MobileV1, HisenseAuthMethod.MobileV1Mqtt31,
            HisenseAuthMethod.MobileV1b, HisenseAuthMethod.MobileV1bMqtt31, HisenseAuthMethod.MobileV2, HisenseAuthMethod.MobileV2Mqtt31]);
        if (version is < 3000) order.AddRange([HisenseAuthMethod.RemoteNow31, HisenseAuthMethod.RemoteNow311]);
        order.AddRange(older);
        // Preserve already paired identities/tokens from older releases. Never rewrite a
        // saved token's client ID: the TV binds it to that exact identity and username.
        if (savedMatchesOriginal)
            yield return Generate(seed, saved!.Method, now.ToUnixTimeSeconds());
        foreach (var method in order.Distinct())
        {
            bool dynamic = method is HisenseAuthMethod.Modern or HisenseAuthMethod.Middle or HisenseAuthMethod.Legacy;
            yield return Generate(HisenseCredentials.IsMobileApp(method) ? mobileDevice : dynamic ? device : seed, method, now.ToUnixTimeSeconds());
        }
    }

    internal static int? ParseVersion(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
        var document = XDocument.Load(reader);
        foreach (var element in document.Descendants())
        {
            if (!element.Name.LocalName.Equals("transport_protocol", StringComparison.OrdinalIgnoreCase)) continue;
            string value = element.Value.Trim();
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int version) && version is > 0 and <= 999999)
                return version;
        }
        string embedded = Regex.Match(document.Root?.Value ?? "", @"transport_protocol[=:]\s*(\d{1,6})(?!\d)",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)).Groups[1].Value;
        return int.TryParse(embedded, NumberStyles.None, CultureInfo.InvariantCulture, out int detected) && detected > 0 ? detected : null;
    }

    internal static async Task<int?> DetectVersionAsync(string address, CancellationToken cancellation)
    {
        ErrorLog.Event("hisense.protocol.probe.begin", new { port = 38400 });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };
            using var http = new HttpClient(handler) { MaxResponseContentBufferSize = 65536 };
            foreach (int port in new[] { 38400, 18400 })
            {
                using var portTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                portTimeout.CancelAfter(TimeSpan.FromMilliseconds(1750));
                try
                {
                    DateTimeOffset requestUtc = DateTimeOffset.UtcNow;
                    using var response = await http.GetAsync($"http://{address}:{port}/MediaServer/rendererdevicedesc.xml", portTimeout.Token);
                    response.EnsureSuccessStatusCode();
                    DateTimeOffset receivedUtc = DateTimeOffset.UtcNow;
                    // Diagnostic only: unauthenticated UPnP time never changes credentials or system time.
                    var serverUtc = response.Headers.Date;
                    ErrorLog.Event("hisense.protocol.clock", new { port, source = "upnp-http-date", trusted = false,
                        dateAvailable = serverUtc.HasValue, serverUtc, localUtc = receivedUtc,
                        offsetSeconds = serverUtc.HasValue ? (double?)Math.Round((serverUtc.Value - requestUtc - (receivedUtc - requestUtc) / 2).TotalSeconds, 1) : null });
                    int? version = ParseVersion(await response.Content.ReadAsStringAsync(portTimeout.Token));
                    ErrorLog.Event("hisense.protocol.detected", new { version, port });
                    if (version is not null) return version;
                }
                catch (HttpRequestException error)
                { ErrorLog.Event("hisense.protocol.port-unavailable", new { port, reason = error.GetType().Name }); }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
                { ErrorLog.Event("hisense.protocol.port-unavailable", new { port, reason = "timeout" }); }
            }
            return null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or XmlException or RegexMatchTimeoutException or OperationCanceledException)
        {
            ErrorLog.Event("hisense.protocol.unavailable", new { reason = error is OperationCanceledException ? "timeout" : error.GetType().Name });
            return null; // Discovery is a hint; every attempt still requires the pinned TLS certificate.
        }
    }
}
