using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using ControllerHelper.Models;

namespace ControllerHelper.Services;

internal sealed record LgInput(string Id, string Name, bool? Connected = null, string? AppId = null)
{
    public override string ToString() => string.IsNullOrEmpty(Id) ? Name :
        $"{Name} ({Id})" + (Connected == false ? ControllerHelper.Localization.Text.Get("WebOsTelevisionService_Inputs_219") : "");
}

public sealed partial class WebOsTelevisionService
{
    internal Task<IReadOnlyList<LgInput>> GetInputsAsync(AppSettings settings, CancellationToken cancellation) =>
        InputStage("inputs", async token =>
        {
            ValidateInputSettings(settings);
            using ClientWebSocket socket = await _connect(settings, token);
            await RegisterAsync(socket, settings.TelevisionClientKey, token, inputControl: true, allowPairingPrompt: false);
            return await ReadInputsAsync(socket, token);
        }, cancellation);

    internal Task SelectInputAsync(AppSettings settings, CancellationToken cancellation) =>
        InputStage("input.select", async token =>
        {
            ValidateInputSettings(settings);
            if (string.IsNullOrWhiteSpace(settings.LgInputId)) throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("WebOsTelevisionService_Inputs_220"));
            using ClientWebSocket socket = await _connect(settings, token);
            await RegisterAsync(socket, settings.TelevisionClientKey, token, inputControl: true, allowPairingPrompt: false);
            var inputs = await ReadInputsAsync(socket, token);
            // IDs come from this TV. An inactive Windows HDMI path may be reported
            // disconnected; it must remain selectable before the PC display switch.
            var selected = inputs.FirstOrDefault(input => input.Id == settings.LgInputId)
                ?? throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("WebOsTelevisionService_Inputs_221"));
            if (selected.AppId is not null && inputs.Count(input => input.AppId == selected.AppId) != 1)
            {
                ErrorLog.Event("lg.input.app-unavailable", new { reason = "ambiguous-input-app" });
                selected = selected with { AppId = null };
            }
            token.ThrowIfCancellationRequested();
            ErrorLog.Event("lg.input.send", new { method = "switchInput", inputFound = true, connected = selected.Connected, hasAppId = selected.AppId is not null });
            await SendAsync(socket, new { type = "request", id = "lg_switch_input", uri = "ssap://tv/switchInput", payload = new { inputId = selected.Id } }, token);
            using var response = await ReceiveMatchingAsync(socket, "lg_switch_input", token);
            var result = ReadInputResponse(response.RootElement, "switch-input");
            string method = "switchInput";
            if (!result.Accepted)
            {
                // Only an explicit failure allows the alternate command. A timeout,
                // disconnect or malformed reply must not trigger another mutation.
                if (!result.Rejected || selected.AppId is null) throw InputResponseError(result);
                token.ThrowIfCancellationRequested();
                method = "launch-input-app";
                ErrorLog.Event("lg.input.fallback", new { reason = result.Reason, code = result.Code, method });
                // appId is supplied by this TV for this input, never derived from HDMI_N.
                await SendAsync(socket, new { type = "request", id = "lg_launch_input", uri = "ssap://system.launcher/launch", payload = new { id = selected.AppId } }, token);
                using var launched = await ReceiveMatchingAsync(socket, "lg_launch_input", token);
                RequireInputResponse(launched.RootElement, "launch-input-app");
            }
            ErrorLog.Event("lg.input.accepted", new { method, confirmation = "command-returnValue" });
            if (selected.AppId is not null)
                await ConfirmInputAsync(socket, selected.AppId, token);
            else
                ErrorLog.Event("lg.input.unconfirmed", new { reason = "input-app-id-unavailable", commandAccepted = true });
            return true;
        }, cancellation);

    internal async Task<string?> PrepareInputAsync(AppSettings settings, CancellationToken cancellation)
    {
        if (settings.TelevisionNetworkProvider != TelevisionNetworkProvider.LG || string.IsNullOrEmpty(settings.LgInputId)) return null;
        try
        {
            ValidateInputSettings(settings);
            if (settings.WakeTelevision) await _waitForInputEndpoint(settings, cancellation);
            await SelectInputAsync(settings, cancellation);
            return null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            ErrorLog.Write("lg.input.warning", error);
            return error is LgInputCommandException
                ? error.Message + ControllerHelper.Localization.Text.Get("WebOsTelevisionService_Inputs_222")
                : ControllerHelper.Localization.Text.Get("WebOsTelevisionService_Inputs_223");
        }
    }

    private static void ValidateInputSettings(AppSettings settings)
    {
        TelevisionNetworkSettings.RequireLg(settings);
        if (string.IsNullOrWhiteSpace(settings.TelevisionClientKey))
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("WebOsTelevisionService_Inputs_224"));
        _ = WebOsSecurity.Endpoint(settings);
    }

    private static async Task<IReadOnlyList<LgInput>> ReadInputsAsync(ClientWebSocket socket, CancellationToken cancellation)
    {
        ErrorLog.Event("lg.inputs.request");
        await SendAsync(socket, new { type = "request", id = "lg_inputs", uri = "ssap://tv/getExternalInputList", payload = new { } }, cancellation);
        using var response = await ReceiveMatchingAsync(socket, "lg_inputs", cancellation);
        return ParseInputs(RequireInputResponse(response.RootElement, "list-inputs"));
    }

    private static JsonElement RequireInputResponse(JsonElement root, string request)
    {
        var result = ReadInputResponse(root, request);
        if (!result.Accepted) throw InputResponseError(result);
        return root.GetProperty("payload");
    }

    internal static IReadOnlyList<LgInput> ParseInputs(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("devices", out var devices) ||
            devices.ValueKind != JsonValueKind.Array || devices.GetArrayLength() > 64)
            throw new InvalidDataException(ControllerHelper.Localization.Text.Get("WebOsTelevisionService_Inputs_225"));
        List<LgInput> inputs = [];
        foreach (var device in devices.EnumerateArray())
        {
            if (device.ValueKind != JsonValueKind.Object || !device.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException(ControllerHelper.Localization.Text.Get("WebOsTelevisionService_Inputs_226"));
            string id = idValue.GetString()!;
            string label = device.TryGetProperty("label", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : id;
            if (string.IsNullOrWhiteSpace(label)) label = id;
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl) || label.Length > 256 || label.Any(char.IsControl) ||
                inputs.Any(input => input.Id == id)) throw new InvalidDataException(ControllerHelper.Localization.Text.Get("WebOsTelevisionService_Inputs_227"));
            bool? connected = device.TryGetProperty("connected", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : null;
            string? appId = device.TryGetProperty("appId", out var app) && app.ValueKind == JsonValueKind.String ? app.GetString() : null;
            if (string.IsNullOrWhiteSpace(appId)) appId = null;
            if (appId is not null && (appId.Length > 256 || appId.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))))
                throw new InvalidDataException(ControllerHelper.Localization.Text.Get("WebOsTelevisionService_Inputs_228"));
            inputs.Add(new(id, label, connected, appId));
        }
        if (inputs.Count == 0) throw new InvalidDataException(ControllerHelper.Localization.Text.Get("WebOsTelevisionService_Inputs_229"));
        ErrorLog.Event("lg.inputs.parsed", new { count = inputs.Count, connected = inputs.Count(input => input.Connected == true), withAppId = inputs.Count(input => input.AppId is not null) });
        return inputs;
    }

    private static Task<bool> WaitForInputEndpointAsync(AppSettings settings, CancellationToken cancellation) =>
        InputStage("input.wake-wait", async token =>
        {
            Uri endpoint = WebOsSecurity.Endpoint(settings);
            for (int attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                using TcpClient tcp = new();
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
                probe.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await tcp.ConnectAsync(endpoint.Host, endpoint.Port, probe.Token);
                    ErrorLog.Event("lg.input.endpoint-ready", new { attempt, port = endpoint.Port });
                    return true;
                }
                catch (SocketException error) { ErrorLog.Event("lg.input.endpoint-wait", new { attempt, code = error.SocketErrorCode.ToString() }); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { ErrorLog.Event("lg.input.endpoint-wait", new { attempt, code = "timeout" }); }
                await Task.Delay(500, token);
            }
        }, cancellation, TimeSpan.FromSeconds(30));

    private static async Task<T> InputStage<T>(string phase, Func<CancellationToken, Task<T>> action, CancellationToken cancellation, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(12));
        long started = Stopwatch.GetTimestamp();
        ErrorLog.Event("lg." + phase + ".begin");
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            T result = await action(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            ErrorLog.Event("lg." + phase + ".ok", new { elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            return result;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { ErrorLog.Event("lg." + phase + ".timeout"); throw new TimeoutException(ControllerHelper.Localization.Text.Get("WebOsTelevisionService_Inputs_230")); }
        catch (OperationCanceledException) { ErrorLog.Event("lg." + phase + ".canceled"); throw; }
        catch (Exception error) { ErrorLog.Write("lg." + phase, error); throw; }
    }
}
