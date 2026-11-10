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
        string code = result.Code is int value ? $" (код {value})" : "";
        return new(result.Reason switch
        {
            "permission-denied" => "LG отказал в разрешении управления входом" + code + ". Повторите «Сопрячь телевизор» в блоке LG и подтвердите запрос на ТВ.",
            "unsupported-method" => "LG не поддерживает команду выбора входа" + code + ".",
            "invalid-response" => "LG вернул неизвестный ответ на команду входа. Успех не подтверждён.",
            _ => "LG отклонил команду входа" + code + "."
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
                    throw new LgInputCommandException("LG принял команду входа, но проверка активного источника недоступна" +
                        (result.Code is int code ? $" (код {code})" : "") + "." +
                        (result.Reason == "permission-denied" ? " Повторите сопряжение и подтвердите разрешение на ТВ." : ""));
                }
                var payload = response.RootElement.GetProperty("payload");
                if (!payload.TryGetProperty("appId", out var app) || app.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(app.GetString()))
                    throw new LgInputCommandException("LG принял команду, но не сообщил активный вход. Переключение не подтверждено.");
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
            throw new LgInputCommandException("LG принял команду, но не подтвердил выбранный HDMI за 4 секунды. Проверьте активный вход на телевизоре.");
        }
    }
}
