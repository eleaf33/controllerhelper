using System.Text.Json;

namespace ControllerHelper.Services;

// A protocol trace, not a packet dump: credentials, PINs, identities and arbitrary
// remote strings must never enter the journal, including malformed/foreign replies.
internal static class HisenseDiagnostics
{
    private static readonly string[] ResponseSuffixes = [
        "ui_service/data/authentication", "ui_service/data/authenticationcode", "ui_service/data/authenticationcodeclose",
        "ui_service/data/authenticationcodetoast", "platform_service/data/tokenissuance", "platform_service/data/gettvinfo",
        "ui_service/data/sourcelist", "ui_service/data/applist", "ui_service/data/applisticon"];
    private static readonly string[] Actions = ["vidaa_app_connect", "authenticationcode", "authenticationcodeclose",
        "gettvstate", "sourcelist", "changesource"];

    internal static string Topic(string topic, string client)
    {
        if (topic == HisenseSession.StateTopic) return "broadcast/ui_service/state";
        foreach (string suffix in ResponseSuffixes)
            if (topic == $"/remoteapp/mobile/{client}/{suffix}") return suffix;
        foreach (string action in Actions)
            if (topic == $"/remoteapp/tv/ui_service/{client}/actions/{action}") return "send/ui_service/" + action;
        if (topic == $"/remoteapp/tv/platform_service/{client}/data/gettoken") return "send/platform_service/gettoken";
        if (topic == $"/remoteapp/tv/remote_service/{client}/actions/sendkey") return "send/remote_service/sendkey";
        return "unknown-or-foreign";
    }

    internal static object? Reply(string label, byte[] payload)
    {
        if (label == "unknown-or-foreign" || label.StartsWith("send/", StringComparison.Ordinal)) return null;
        if (payload.Length == 0) return new { empty = true };
        if (payload.Length > 65536) return new { oversized = true };
        try
        {
            using var json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Array) return new { validJson = true, shape = "array", count = root.GetArrayLength() };
            if (root.ValueKind != JsonValueKind.Object) return new { validJson = true, shape = root.ValueKind.ToString() };
            int? Integer(string name) => root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number &&
                item.TryGetInt32(out int number) ? number : null;
            bool NonEmpty(string name) => root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String && item.GetString()?.Length > 0;
            if (label == "ui_service/data/authenticationcode")
                return new { validJson = true, shape = "object", hasResult = root.TryGetProperty("result", out _), resultCode = Integer("result") };
            if (label == "platform_service/data/tokenissuance")
                return new { validJson = true, shape = "object", hasAccessToken = NonEmpty("accesstoken"), hasRefreshToken = NonEmpty("refreshtoken"),
                    accessDays = Integer("accesstoken_duration_day"), refreshDays = Integer("refreshtoken_duration_day") };
            if (label == "broadcast/ui_service/state")
            {
                string? state = root.TryGetProperty("statetype", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
                return new { validJson = true, shape = "object", state = state is "authentication" or "authenticationcode" or "fake_sleep" or "fake_sleep_0" or "fake_sleep_1"
                    or "app" or "source" or "sourceswitch" or "livetv" or "remote_launcher" or "remote_setting" ? state : "other",
                    hasSourceId = root.TryGetProperty("sourceid", out _), power = HisenseSession.ParsePowerState(payload).ToString() };
            }
            return new { validJson = true, shape = "object" };
        }
        catch (JsonException) { return new { validJson = false }; }
    }

    internal static object Packet(byte[] packet, bool inbound)
    {
        int kind = packet.Length > 0 ? packet[0] >> 4 : 0;
        string type = kind switch { 1 => "CONNECT", 2 => "CONNACK", 3 => "PUBLISH", 4 => "PUBACK", 8 => "SUBSCRIBE",
            9 => "SUBACK", 12 => "PINGREQ", 13 => "PINGRESP", 14 => "DISCONNECT", _ => "other" };
        // Only the numerical SUBACK result array is inspected. Never log CONNECT,
        // SUBSCRIBE topics, PUBLISH data, hex buffers, or arbitrary error strings.
        if (inbound && kind == 9)
        {
            int length = 0, shift = 0, offset = 1;
            while (offset < packet.Length && offset <= 4)
            {
                byte value = packet[offset++]; length |= (value & 127) << shift; shift += 7;
                if ((value & 128) != 0) continue;
                if (length >= 2 && length <= 66 && offset + length == packet.Length)
                    return new { type, bytes = packet.Length, packetId = packet[offset] * 256 + packet[offset + 1],
                        returnCodes = packet.AsSpan(offset + 2).ToArray().Select(code => (int)code).ToArray() };
                break;
            }
            return new { type, bytes = packet.Length, malformed = true };
        }
        return new { type, bytes = packet.Length };
    }
}
