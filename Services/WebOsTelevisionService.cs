using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ControllerHelper.Models;

namespace ControllerHelper.Services;

public sealed partial class WebOsTelevisionService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly Func<AppSettings, CancellationToken, Task<ClientWebSocket>> _connect;
    private readonly Func<AppSettings, CancellationToken, Task> _waitForInputEndpoint;

    public WebOsTelevisionService() : this(WebOsConnection.ConnectAsync) { }
    internal WebOsTelevisionService(Func<AppSettings, CancellationToken, Task<ClientWebSocket>> connect,
        Func<AppSettings, CancellationToken, Task>? waitForInputEndpoint = null)
    { _connect = connect; _waitForInputEndpoint = waitForInputEndpoint ?? WaitForInputEndpointAsync; }

    public async Task<string> PairAsync(AppSettings settings, CancellationToken cancellation = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using ClientWebSocket socket = await _connect(settings, timeout.Token);
        return await RegisterAsync(socket, null, timeout.Token, inputControl: true);
    }

    public async Task<TelevisionPowerState> GetPowerStateAsync(AppSettings settings, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(settings.TelevisionClientKey)) return TelevisionPowerState.Unknown;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using ClientWebSocket socket = await _connect(settings, timeout.Token);
            await RegisterAsync(socket, settings.TelevisionClientKey, timeout.Token);
            await SendAsync(socket, new
            {
                type = "request", id = "power_state", uri = "ssap://com.webos.service.tvpower/power/getPowerState",
                payload = new { }
            }, timeout.Token);
            using JsonDocument response = await ReceiveMatchingAsync(socket, "power_state", timeout.Token);
            if (response.RootElement.GetProperty("type").GetString() != "response" ||
                !response.RootElement.TryGetProperty("payload", out JsonElement payload) ||
                !payload.TryGetProperty("returnValue", out JsonElement success) || !success.GetBoolean() ||
                !payload.TryGetProperty("state", out JsonElement state))
                return TelevisionPowerState.Unknown;
            return state.GetString() switch
            {
                "Active" or "Screen Off" => TelevisionPowerState.On,
                "Power Off" or "Suspend" or "Active Standby" => TelevisionPowerState.Off,
                _ => TelevisionPowerState.Unknown
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (TelevisionCertificateException error)
        {
            ErrorLog.Write("television.certificate", error);
            return TelevisionPowerState.Unknown;
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            StartupTrace.Write($"Could not read TV power state: {error}");
            return TelevisionPowerState.Unknown;
        }
    }

    public async Task<bool> PowerOffAsync(AppSettings settings, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(settings.TelevisionClientKey))
            throw new InvalidOperationException("Сначала сопрягите телевизор во вкладке ТВ.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(RequestTimeout);
        using ClientWebSocket socket = await _connect(settings, timeout.Token);
        await RegisterAsync(socket, settings.TelevisionClientKey, timeout.Token);
        await SendAsync(socket, new
        {
            type = "request", id = "power_off", uri = "ssap://system/turnOff", payload = new { }
        }, timeout.Token);
        using CancellationTokenSource responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        responseTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using JsonDocument response = await ReceiveMatchingAsync(socket, "power_off", responseTimeout.Token);
            if (response.RootElement.GetProperty("type").GetString() != "response" ||
                !response.RootElement.TryGetProperty("payload", out JsonElement payload) ||
                !payload.TryGetProperty("returnValue", out JsonElement accepted) || !accepted.GetBoolean())
                throw new InvalidOperationException("Телевизор отклонил команду выключения.");
            return true;
        }
        catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { return false; }
        catch (WebSocketException) when (socket.State != WebSocketState.Open) { return false; }
        catch (InvalidOperationException error) when (
            error.Message == "Телевизор закрыл соединение webOS." &&
            (socket.State is WebSocketState.CloseReceived or WebSocketState.Closed))
        {
            return false;
        }
    }

    private static async Task<string> RegisterAsync(ClientWebSocket socket, string? clientKey,
        CancellationToken cancellation, bool inputControl = false, bool allowPairingPrompt = true)
    {
        await SendAsync(socket, new { type = "hello", id = "hello", payload = new { } }, cancellation);
        using (JsonDocument hello = await ReceiveAsync(socket, cancellation))
        {
            if (hello.RootElement.GetProperty("type").GetString() != "hello")
                throw new InvalidOperationException("Телевизор не ответил на приветствие webOS.");
        }

        await SendAsync(socket, new
        {
            type = "request", id = "system_info", uri = "ssap://system/getSystemInfo", payload = new { }
        }, cancellation);
        using (JsonDocument info = await ReceiveAsync(socket, cancellation)) { }

        var payload = new Dictionary<string, object?>
        {
            ["forcePairing"] = false,
            ["pairingType"] = "PROMPT",
            ["manifest"] = new
            {
                appVersion = "1.1", manifestVersion = 1,
                permissions = inputControl
                    ? new[] { "CONTROL_POWER", "READ_POWER_STATE", "READ_INPUT_DEVICE_LIST", "CONTROL_INPUT_TV", "LAUNCH", "READ_RUNNING_APPS" }
                    : new[] { "CONTROL_POWER", "READ_POWER_STATE" }
            }
        };
        if (clientKey is not null) payload["client-key"] = clientKey;
        await SendAsync(socket, new { type = "register", id = "register_0", payload }, cancellation);
        while (true)
        {
            using JsonDocument response = await ReceiveAsync(socket, cancellation);
            string? type = response.RootElement.GetProperty("type").GetString();
            if (type == "registered" &&
                response.RootElement.GetProperty("payload").TryGetProperty("client-key", out JsonElement key))
            {
                if (inputControl) ErrorLog.Event("lg.input.registration", new { accepted = true, inputControl = true, launch = true, readRunningApps = true });
                return key.GetString() ?? throw new InvalidOperationException("Телевизор не выдал ключ сопряжения.");
            }
            if (type == "error")
            {
                if (inputControl) throw InputResponseError(ReadInputResponse(response.RootElement, "register-input-control"));
                throw new InvalidOperationException("Сопряжение с телевизором отклонено: " +
                    (response.RootElement.TryGetProperty("error", out JsonElement error) ? error.GetString() : type));
            }
            if (!allowPairingPrompt && type == "response" && response.RootElement.TryGetProperty("payload", out var reply) &&
                reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("pairingType", out var pairingType) &&
                pairingType.ValueKind == JsonValueKind.String && pairingType.GetString() == "PROMPT")
            {
                if (inputControl) ErrorLog.Event("lg.input.registration", new { accepted = false, reason = "pairing-required" });
                throw new InvalidOperationException("LG требует разрешение управления входами. Нажмите «Сопрячь телевизор» в блоке LG и подтвердите запрос на ТВ.");
            }
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, object message, CancellationToken cancellation)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellation);
    }

    private static async Task<JsonDocument> ReceiveMatchingAsync(ClientWebSocket socket, string id,
        CancellationToken cancellation)
    {
        while (true)
        {
            JsonDocument message = await ReceiveAsync(socket, cancellation);
            if (message.RootElement.TryGetProperty("id", out JsonElement responseId) &&
                responseId.GetString() == id)
                return message;
            message.Dispose();
        }
    }

    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellation)
    {
        using MemoryStream stream = new();
        byte[] buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellation);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Телевизор закрыл соединение webOS.");
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 1024 * 1024)
                throw new InvalidOperationException("Слишком большой ответ телевизора.");
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(stream.ToArray());
    }
}
