using System.Net.WebSockets;
using System.Text.Json;

namespace ControllerHelper.Services;

internal sealed class LgInputCommandException(string message) : InvalidOperationException(message);

public sealed partial class WebOsTelevisionService
{
    internal sealed record InputResponse(bool Accepted, bool Rejected, string Reason, int? Code);

    internal static InputResponse ReadInputResponse(JsonElement root, string request)
    {
        string type = "invalid";
        JsonElement payload = default, returned = default;
        int? code = null;
        string errorText = "";
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                type = t.GetString() is "response" or "error" ? t.GetString()! : "other";
            root.TryGetProperty("payload", out payload);
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                errorText = e.GetString() ?? "";
        }
        if (payload.ValueKind == JsonValueKind.Object)
        {
            payload.TryGetProperty("returnValue", out returned);
            if (payload.TryGetProperty("errorCode", out var c))
            {
                if (c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int number)) code = number;
                else if (c.ValueKind == JsonValueKind.String && int.TryParse(c.GetString(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out number)) code = number;
            }
            if (payload.TryGetProperty("errorText", out var e) && e.ValueKind == JsonValueKind.String)
                errorText = e.GetString() ?? "";
        }
        // Log only the numeric code and a known category, never arbitrary TV text/payload.
        if (code is null && errorText.Length >= 3 && errorText.Take(3).All(char.IsAsciiDigit) &&
            (errorText.Length == 3 || char.IsWhiteSpace(errorText[3]))) code = int.Parse(errorText[..3], System.Globalization.CultureInfo.InvariantCulture);
        bool accepted = type == "response" && returned.ValueKind == JsonValueKind.True;
        bool rejected = type == "error" || (type == "response" && returned.ValueKind == JsonValueKind.False);
        string reason = accepted ? "accepted" : !rejected ? "invalid-response" : code is 401 or 403 ? "permission-denied" :
            code == 404 ? "unsupported-method" : code == 500 ? "application-error" : "rejected";
        ErrorLog.Event("lg.input.response", new { request, accepted, rejected, responseType = type,
            payloadKind = payload.ValueKind.ToString(), returnValueKind = returned.ValueKind.ToString(), code, reason });
        return new(accepted, rejected, reason, code);
    }

    private static LgInputCommandException InputResponseError(InputResponse result)
    {
        string code = result.Code is int value ? ControllerHelper.Localization.Text.Format("WebOsTelevisionService_InputResponses_209", value) : "";
        return new(result.Reason switch
        {
            "permission-denied" => ControllerHelper.Localization.Text.Get("WebOsTelevisionService_InputResponses_210") + code + ControllerHelper.Localization.Text.Get("WebOsTelevisionService_InputResponses_211"),
            "unsupported-method" => ControllerHelper.Localization.Text.Get("WebOsTelevisionService_InputResponses_212") + code + ".",
            "invalid-response" => ControllerHelper.Localization.Text.Get("WebOsTelevisionService_InputResponses_213"),
            _ => ControllerHelper.Localization.Text.Get("WebOsTelevisionService_InputResponses_214") + code + "."
        });
    }

    private static async Task ConfirmInputAsync(ClientWebSocket socket, string expectedAppId, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string id = "lg_input_state_" + attempt;
                await SendAsync(socket, new { type = "request", id, uri = "ssap://com.webos.applicationManager/getForegroundAppInfo", payload = new { } }, deadline.Token);
                using var response = await ReceiveMatchingAsync(socket, id, deadline.Token);
                var result = ReadInputResponse(response.RootElement, "current-input");
                if (!result.Accepted)
                {
                    ErrorLog.Event("lg.input.unconfirmed", new { reason = "state-query-failed", commandAccepted = true, code = result.Code });
                    throw new LgInputCommandException(ControllerHelper.Localization.Text.Get("WebOsTelevisionService_InputResponses_215") +
                        (result.Code is int code ? ControllerHelper.Localization.Text.Format("WebOsTelevisionService_InputResponses_209", code) : "") + "." +
                        (result.Reason == "permission-denied" ? ControllerHelper.Localization.Text.Get("WebOsTelevisionService_InputResponses_216") : ""));
                }
                var payload = response.RootElement.GetProperty("payload");
                if (!payload.TryGetProperty("appId", out var app) || app.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(app.GetString()))
                    throw new LgInputCommandException(ControllerHelper.Localization.Text.Get("WebOsTelevisionService_InputResponses_217"));
                bool matches = app.GetString() == expectedAppId;
                ErrorLog.Event("lg.input.observed", new { attempt, matches });
                if (matches)
                {
                    ErrorLog.Event("lg.input.confirmed", new { confirmation = "foreground-app-matched" });
                    return;
                }
                await Task.Delay(250, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            ErrorLog.Event("lg.input.unconfirmed", new { reason = "foreground-not-confirmed", commandAccepted = true });
            throw new LgInputCommandException(ControllerHelper.Localization.Text.Get("WebOsTelevisionService_InputResponses_218"));
        }
    }
}
