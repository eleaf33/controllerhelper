using System.Diagnostics;
using System.Text.Json;
using ControllerHelper.Models;

namespace ControllerHelper.Services;

internal sealed record HisenseInput(string Id, string Name)
{
    public override string ToString() => Name;
}

// One session has one in-flight request. Only exact response topics are accepted.
internal sealed partial class HisenseSession(IHisenseTransport transport, string clientId) : IDisposable
{
    private string Client => transport.Connection?.ClientId ?? clientId;
    internal HisenseAuthentication? Authentication { get; private set; }
    internal void ApplyConnectionSettings(AppSettings settings)
    {
        if (transport.Connection is not { } connection) return;
        if (!connection.Dynamic) HisenseSettings.ClearAuthentication(settings);
        // ClearAuthentication also resets the preference for a different TV; restore this accepted one.
        settings.HisensePreferredAuthMethod = connection.Method.ToString();
        if (Authentication is not null) settings.HisenseAuthentication = Authentication;
    }
    private string ActionTopic(string action) => $"/remoteapp/tv/ui_service/{Client}/actions/{action}";
    private string ResponseTopic(string kind) => $"/remoteapp/mobile/{Client}/ui_service/data/{kind}";
    private string TokenTopic => $"/remoteapp/mobile/{Client}/platform_service/data/tokenissuance";
    internal const string StateTopic = "/remoteapp/mobile/broadcast/ui_service/state";
    internal static string[] ResponseTopics(string client, bool useTokens = false, bool mobileApp = false) => mobileApp ? [
        $"/remoteapp/mobile/{client}/ui_service/data/authentication",
        $"/remoteapp/mobile/{client}/ui_service/data/authenticationcode",
        $"/remoteapp/mobile/{client}/ui_service/data/authenticationcodeclose",
        $"/remoteapp/mobile/{client}/platform_service/data/tokenissuance",
        $"/remoteapp/mobile/{client}/platform_service/data/gettvinfo",
        $"/remoteapp/mobile/{client}/ui_service/data/sourcelist",
        $"/remoteapp/mobile/{client}/ui_service/data/applist",
        $"/remoteapp/mobile/{client}/ui_service/data/applisticon", StateTopic] : [
        $"/remoteapp/mobile/{client}/ui_service/data/authentication",
        $"/remoteapp/mobile/{client}/ui_service/data/authenticationcode",
        $"/remoteapp/mobile/{client}/ui_service/data/authenticationcodetoast",
        $"/remoteapp/mobile/{client}/ui_service/data/authenticationcodeclose",
        $"/remoteapp/mobile/{client}/ui_service/data/sourcelist",
        .. (useTokens ? new[] { $"/remoteapp/mobile/{client}/platform_service/data/tokenissuance" } : []), StateTopic];

    internal async Task ConnectAsync(AppSettings settings, CancellationToken cancellation)
    {
        await Stage("connect", async token => { await transport.ConnectAsync(settings, token); return true; }, cancellation, TimeSpan.FromSeconds(65));
        if (transport.Connection?.Source == "refresh-token")
            await RequestTokenAsync(transport.Connection.Password, cancellation);
        else if (transport.Connection?.Source == "access-token") Authentication = settings.HisenseAuthentication;
    }

    internal async Task<IReadOnlyList<HisenseInput>> PairAsync(Func<CancellationToken, Task<string?>> requestPin, CancellationToken cancellation)
    {
        // A fresh sourcelist response also proves an existing client authorization.
        bool dynamicPairing = transport.Connection is { Dynamic: true, Source: "generated" };
        var first = await RequestSourcesOrAuthentication(cancellation, dynamicPairing);
        if (first.Topic == ResponseTopic("sourcelist"))
        {
            var inputs = ParseInputs(first.Payload);
            if (dynamicPairing) await RequestTokenAsync("", cancellation);
            if (dynamicPairing && transport.Connection?.MobileApp == true) return await GetInputsAsync(cancellation);
            return inputs;
        }
        ErrorLog.Event("hisense.pair.pin-required");
        string? pin = await AwaitPinAsync(requestPin, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (pin is null) throw new OperationCanceledException(cancellation);
        if (pin.Length != 4 || pin.Any(c => c < '0' || c > '9'))
            throw new InvalidOperationException("Введите четыре цифры PIN с экрана Hisense.");
        await Stage("pair.pin", async token =>
        {
            transport.Drain();
            object authNum = transport.Connection?.UsesStringPin == true ? pin : int.Parse(pin, System.Globalization.CultureInfo.InvariantCulture);
            await Send("authenticationcode", JsonSerializer.Serialize(new { authNum }), token);
            HisenseMessage reply = await Receive([ResponseTopic("authenticationcode")], token, pinPending: true);
            using JsonDocument json = Parse(reply.Payload);
            bool accepted = json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Number &&
                result.TryGetInt32(out int value) && (value == 1 || value == 0 && transport.Connection?.MobileApp == true);
            ErrorLog.Event("hisense.pair.result", new { accepted });
            if (!accepted) throw new InvalidOperationException("Hisense отклонил PIN. Повторите сопряжение.");
        }, cancellation);
        if (transport.Connection is { Dynamic: true }) await RequestTokenAsync("", cancellation);
        else if (transport.Connection?.UsesRemoteNowCertificate == true) await Send("authenticationcodeclose", "", cancellation);
        return await GetInputsAsync(cancellation);
    }

    private async Task<string?> AwaitPinAsync(Func<CancellationToken, Task<string?>> requestPin, CancellationToken cancellation)
    {
        using var prompt = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<string?> input = requestPin(prompt.Token);
        Task<HisenseMessage> monitor = Receive([], prompt.Token, pinPending: true);
        try
        {
            Task completed = await Task.WhenAny(input, monitor);
            if (completed == monitor) await monitor;
            return await input;
        }
        finally
        {
            prompt.Cancel();
            // The winning task's result was observed above; observe the canceled/losing task too.
            try { await monitor; } catch (Exception) { }
            // A TV error cancels the UI dialog as well. Observe its task before leaving the session.
            if (!input.IsCompletedSuccessfully)
                try { await input; } catch (Exception) { }
        }
    }

    private Task<HisenseMessage> RequestSourcesOrAuthentication(CancellationToken cancellation, bool startPairing = false) => Stage("sources", async token =>
    {
        transport.Drain();
        if (startPairing)
            await Send("vidaa_app_connect", "{\"app_version\":2,\"connect_result\":0,\"device_type\":\"Mobile App\"}", token);
        await Send("gettvstate", "", token);
        await Send("sourcelist", "", token);
        while (true)
        {
            var reply = await Receive([ResponseTopic("sourcelist"), ResponseTopic("authentication"), StateTopic], token);
            if (reply.Topic != StateTopic || RequiresAuthentication(reply.Payload)) return reply;
        }
    }, cancellation);

    private async Task RequestTokenAsync(string refreshToken, CancellationToken cancellation)
    {
        await Stage("token.request", async token =>
        {
            transport.Drain();
            ErrorLog.Event("hisense.token.request", new { refresh = refreshToken.Length > 0 });
            await transport.PublishAsync($"/remoteapp/tv/platform_service/{Client}/data/gettoken",
                JsonSerializer.Serialize(new { refreshtoken = refreshToken }), token);
            if (refreshToken.Length == 0) await Send("authenticationcodeclose", "", token);
            var reply = await Receive([TokenTopic], token);
            Authentication = HisenseAuthentication.FromReply(reply.Payload, transport.Connection!, DateTimeOffset.UtcNow);
            ErrorLog.Event("hisense.token.received", new { refreshAvailable = Authentication.RefreshToken.Length > 0 });
        }, cancellation);
        if (transport.Connection?.MobileApp == true)
            await Stage("token.reconnect", token => transport.ReconnectWithTokenAsync(Authentication!, token), cancellation);
    }

    private static bool RequiresAuthentication(byte[] payload)
    {
        using var json = Parse(payload);
        return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("statetype", out var type) &&
            type.ValueKind == JsonValueKind.String && type.GetString() is "authentication" or "authenticationcode";
    }

    internal async Task<IReadOnlyList<HisenseInput>> GetInputsAsync(CancellationToken cancellation)
    {
        var reply = await RequestSourcesOrAuthentication(cancellation);
        if (reply.Topic != ResponseTopic("sourcelist"))
            throw new InvalidOperationException("Hisense требует PIN. Выполните сопряжение в настройках приложения.");
        return ParseInputs(reply.Payload);
    }

    internal async Task SelectInputAsync(string id, CancellationToken cancellation)
    {
        var inputs = await GetInputsAsync(cancellation);
        HisenseInput selected = inputs.FirstOrDefault(input => input.Id == id)
            ?? throw new InvalidOperationException("Сохранённый вход отсутствует в списке Hisense. Обновите список и выберите HDMI заново.");
        await Stage("input.select", async token =>
        {
            transport.Drain();
            await Send("changesource", JsonSerializer.Serialize(new { sourceid = selected.Id }), token);
            ErrorLog.Event("hisense.input.sent");
            await Send("gettvstate", "", token);
            while (true)
            {
                HisenseMessage reply = await Receive([StateTopic, ResponseTopic("authentication")], token);
                if (reply.Topic == ResponseTopic("authentication") || RequiresAuthentication(reply.Payload))
                    throw new InvalidOperationException("Hisense запросил повторное сопряжение при выборе HDMI.");
                if (StateMatches(reply.Payload, selected.Id)) break;
            }
            ErrorLog.Event("hisense.input.confirmed");
        }, cancellation);
    }

    private async Task Send(string action, string payload, CancellationToken cancellation)
    {
        ErrorLog.Event("hisense.mqtt.publish", new { action }); // Never payloads or PIN.
        await transport.PublishAsync(ActionTopic(action), payload, cancellation);
    }

    private async Task<HisenseMessage> Receive(string[] expected, CancellationToken cancellation, bool pinPending = false)
    {
        while (true)
        {
            HisenseMessage reply = await transport.ReadAsync(cancellation);
            bool relevant = expected.Contains(reply.Topic, StringComparer.Ordinal);
            ErrorLog.Event("hisense.mqtt.receive", new { kind = Kind(reply.Topic), bytes = reply.Payload.Length, retained = reply.Retained, relevant });
            if (!reply.Retained && reply.Topic == ResponseTopic("authenticationcodetoast"))
                throw new InvalidOperationException("Hisense сообщает, что управление занято другим устройством. Закройте приложение-пульт на телефоне и повторите сопряжение.");
            if (pinPending && !reply.Retained && reply.Topic == ResponseTopic("authenticationcodeclose"))
                throw new InvalidOperationException("Hisense закрыл окно PIN или срок кода истёк. Повторите сопряжение.");
            if (!reply.Retained && relevant) return reply;
        }
    }

    private string Kind(string topic) => topic == TokenTopic ? "tokenissuance" : topic == StateTopic ? "state" :
        new[] { "sourcelist", "authentication", "authenticationcode", "authenticationcodetoast", "authenticationcodeclose" }.FirstOrDefault(k => topic == ResponseTopic(k)) ?? "other";

    internal static JsonDocument Parse(byte[] payload)
    {
        if (payload.Length > 65536) throw new InvalidDataException("Слишком большой ответ Hisense.");
        return JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
    }

    internal static IReadOnlyList<HisenseInput> ParseInputs(byte[] payload)
    {
        using JsonDocument json = Parse(payload);
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > 64)
            throw new InvalidDataException("Неизвестный формат списка входов Hisense. Передайте логи разработчику.");
        List<HisenseInput> inputs = [];
        foreach (var entry in json.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("sourceid", out var id) ||
                !entry.TryGetProperty("sourcename", out var name)) throw new InvalidDataException("Некорректный вход Hisense.");
            string? value = Scalar(id), label = name.ValueKind == JsonValueKind.String ? name.GetString() : null;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || string.IsNullOrWhiteSpace(label) || label.Length > 128 ||
                inputs.Any(i => i.Id == value)) throw new InvalidDataException("Некорректный или повторяющийся вход Hisense.");
            inputs.Add(new(value, label));
        }
        if (inputs.Count == 0) throw new InvalidDataException("Hisense вернул пустой список входов.");
        ErrorLog.Event("hisense.sources.parsed", new { count = inputs.Count });
        return inputs;
    }

    internal static bool StateMatches(byte[] payload, string id)
    {
        using var json = Parse(payload);
        return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("sourceid", out var current)
            && Scalar(current) == id;
    }
    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null
    };

    internal static async Task<T> Stage<T>(string phase, Func<CancellationToken, Task<T>> action, CancellationToken cancellation,
        TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        long start = Stopwatch.GetTimestamp();
        ErrorLog.Event("hisense." + phase + ".begin");
        try
        {
            T result = await action(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            ErrorLog.Event("hisense." + phase + ".ok", new { elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds });
            return result;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            ErrorLog.Event("hisense." + phase + ".timeout");
            throw new TimeoutException("Hisense не ответил на этапе «" + phase + "». Подробности в logs/errors.jsonl.");
        }
        catch (OperationCanceledException) { ErrorLog.Event("hisense." + phase + ".canceled"); throw; }
        catch (Exception error) { ErrorLog.Write("hisense." + phase, error); throw; }
    }
    private static Task Stage(string phase, Func<CancellationToken, Task> action, CancellationToken cancellation) =>
        Stage(phase, async token => { await action(token); return true; }, cancellation);

    public void Dispose() => transport.Dispose();
}
