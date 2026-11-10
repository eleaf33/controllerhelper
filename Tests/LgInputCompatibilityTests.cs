using System.Net.WebSockets;
using System.Text.Json;
using ControllerHelper.Services;

internal static partial class WebOsSecurityTests
{
    private static (string Name, Func<Task> Run)[] InputCompatibilityCases =>
    [
        ("LG: явный отказ switchInput запускает полученное приложение входа", LgInputFallback),
        ("LG: успешный switchInput подтверждается текущим источником", LgInputDirectConfirmation),
        ("LG: отказ резервной команды остаётся предупреждением с кодом", LgInputFallbackRejected),
        ("LG: неизвестный ответ не запускает резервную команду", LgInputInvalidReply),
        ("LG: отсутствие ответа не запускает резервную команду после отмены", LgInputReplyCancellation),
        ("LG: принятие команды без смены источника не считается подтверждением", LgInputNotConfirmed),
        ("LG: ошибка чтения состояния отличается от отказа переключения", LgInputStateRejected),
        ("LG: appId берётся из списка и проверяется без вывода в журнал", LgInputAppParsing),
        ("LG: общий appId двух портов не запускается как конкретный HDMI", LgInputAmbiguousApp),
        ("LG: журнал различает код отказа и неверный формат без секретов", LgInputResponseDiagnostics)
    ];

    private const string PcInputApp = "vendor.input.session_7";
    private const string SwitchOk = "{\"type\":\"response\",\"id\":\"lg_switch_input\",\"payload\":{\"returnValue\":true}}";
    private const string SwitchDenied = "{\"type\":\"error\",\"id\":\"lg_switch_input\",\"error\":\"500 Application error\"}";

    private static async Task SendAppInputList(WebSocket socket, CancellationToken token, bool ambiguous = false)
    {
        using var request = await Receive(socket, token);
        Check(request.RootElement.GetProperty("uri").GetString() == "ssap://tv/getExternalInputList", "No fresh input list");
        await Send(socket, JsonSerializer.Serialize(new { type = "response", id = "lg_inputs", payload = new { returnValue = true, devices = new[] {
            new { id = "EXTERNAL_PC_7", label = "Not an app ID", connected = false, appId = PcInputApp },
            new { id = "OTHER_PORT", label = "Other source", connected = true, appId = ambiguous ? PcInputApp : "vendor.apple_input" }
        } } }), token);
    }

    private static async Task ReceiveInputLaunch(WebSocket socket, CancellationToken token, bool accepted = true)
    {
        using var request = await Receive(socket, token);
        Check(request.RootElement.GetProperty("uri").GetString() == "ssap://system.launcher/launch", "Wrong fallback endpoint");
        Check(request.RootElement.GetProperty("id").GetString() == "lg_launch_input", "Wrong fallback request ID");
        var payload = request.RootElement.GetProperty("payload");
        Check(payload.EnumerateObject().Count() == 1 && payload.GetProperty("id").GetString() == PcInputApp, "App ID guessed or taken from label");
        await Send(socket, accepted ? "{\"type\":\"response\",\"id\":\"lg_launch_input\",\"payload\":{\"returnValue\":true}}" :
            "{\"type\":\"error\",\"id\":\"lg_launch_input\",\"error\":\"403 access denied\"}", token);
    }

    private static async Task SendForeground(WebSocket socket, CancellationToken token, string appId)
    {
        using var request = await Receive(socket, token);
        Check(request.RootElement.GetProperty("uri").GetString() == "ssap://com.webos.applicationManager/getForegroundAppInfo", "Unexpected command instead of state query");
        await Send(socket, JsonSerializer.Serialize(new { type = "response", id = request.RootElement.GetProperty("id").GetString(), payload = new { returnValue = true, appId } }), token);
    }

    private static async Task LgInputFallback()
    {
        foreach (var denied in new[] { SwitchDenied,
            "{\"type\":\"response\",\"id\":\"lg_switch_input\",\"payload\":{\"returnValue\":false,\"errorCode\":500}}",
            "{\"type\":\"error\",\"id\":\"lg_switch_input\",\"error\":\"404 no such service or method\"}" })
        {
            using var cert = Certificate();
            await using Server server = new(cert, async (socket, token) =>
            {
                await Register(socket, token, "private-test-key", inputControl: true);
                await SendAppInputList(socket, token);
                await RespondToSwitch(socket, token, denied);
                await ReceiveInputLaunch(socket, token);
                await SendForeground(socket, token, "vendor.apple_input");
                await SendForeground(socket, token, PcInputApp);
            });
            var settings = Settings(WebOsSecurity.Fingerprint(cert)); settings.LgInputId = "EXTERNAL_PC_7"; settings.WakeTelevision = false;
            Check(await Service(server).PrepareInputAsync(settings, default) is null, "Fallback did not confirm the chosen source");
            await server.Completion;
        }
    }

    private static async Task LgInputDirectConfirmation()
    {
        using var cert = Certificate();
        await using Server server = new(cert, async (socket, token) =>
        {
            await Register(socket, token, "private-test-key"); await SendAppInputList(socket, token);
            await RespondToSwitch(socket, token, SwitchOk);
            await SendForeground(socket, token, "vendor.apple_input");
            await SendForeground(socket, token, PcInputApp);
        });
        var settings = Settings(WebOsSecurity.Fingerprint(cert)); settings.LgInputId = "EXTERNAL_PC_7";
        await Service(server).SelectInputAsync(settings, default); await server.Completion;
    }

    private static async Task LgInputFallbackRejected()
    {
        using var cert = Certificate();
        await using Server server = new(cert, async (socket, token) =>
        {
            await Register(socket, token, "private-test-key"); await SendAppInputList(socket, token);
            await RespondToSwitch(socket, token, SwitchDenied); await ReceiveInputLaunch(socket, token, accepted: false);
        });
        var settings = Settings(WebOsSecurity.Fingerprint(cert)); settings.LgInputId = "EXTERNAL_PC_7"; settings.WakeTelevision = false;
        string? warning = await Service(server).PrepareInputAsync(settings, default);
        Check(warning is not null && warning.Contains("403") && warning.Contains("Сопрячь") && warning.Contains("продолжается"), "Denied fallback swallowed or failed display transition");
        await server.Completion;
    }

    private static async Task LgInputInvalidReply()
    {
        foreach (string payload in new[] { "{}", "{\"returnValue\":\"true\"}" })
        {
            using var cert = Certificate();
            await using Server server = new(cert, async (socket, token) =>
            {
                await Register(socket, token, "private-test-key"); await SendAppInputList(socket, token);
                await RespondToSwitch(socket, token, "{\"type\":\"response\",\"id\":\"lg_switch_input\",\"payload\":" + payload + "}");
            });
            var settings = Settings(WebOsSecurity.Fingerprint(cert)); settings.LgInputId = "EXTERNAL_PC_7";
            await Throws<LgInputCommandException>(() => Service(server).SelectInputAsync(settings, default)); await server.Completion;
        }
    }

    private static async Task LgInputReplyCancellation()
    {
        using var cert = Certificate(); using CancellationTokenSource cancellation = new();
        await using Server server = new(cert, async (socket, token) =>
        {
            await Register(socket, token, "private-test-key"); await SendAppInputList(socket, token);
            using var request = await Receive(socket, token);
            Check(request.RootElement.GetProperty("uri").GetString() == "ssap://tv/switchInput", "Wrong initial command");
            cancellation.Cancel();
            await ExpectNoMoreCommands(socket, token);
        });
        var settings = Settings(WebOsSecurity.Fingerprint(cert)); settings.LgInputId = "EXTERNAL_PC_7";
        await Throws<OperationCanceledException>(() => Service(server).SelectInputAsync(settings, cancellation.Token)); await server.Completion;
    }

    private static async Task ExpectNoMoreCommands(WebSocket socket, CancellationToken token)
    {
        try
        {
            var result = await socket.ReceiveAsync(new byte[4096], token);
            Check(result.MessageType == WebSocketMessageType.Close, "Unexpected additional command");
        }
        catch (Exception error) when (error is WebSocketException or IOException) { }
    }

    private static async Task LgInputNotConfirmed()
    {
        using var cert = Certificate();
        await using Server server = new(cert, async (socket, token) =>
        {
            await Register(socket, token, "private-test-key"); await SendAppInputList(socket, token);
            await RespondToSwitch(socket, token, SwitchOk);
            try { while (true) await SendForeground(socket, token, "vendor.apple_input"); }
            catch (Exception error) when (error is WebSocketException or IOException or JsonException) { }
        });
        var settings = Settings(WebOsSecurity.Fingerprint(cert)); settings.LgInputId = "EXTERNAL_PC_7"; settings.WakeTelevision = false;
        string? warning = await Service(server).PrepareInputAsync(settings, default);
        Check(warning is not null && warning.Contains("не подтвердил") && warning.Contains("4 секунды"), "Different source treated as confirmed");
        await server.Completion;
    }

    private static async Task LgInputStateRejected()
    {
        using var cert = Certificate();
        await using Server server = new(cert, async (socket, token) =>
        {
            await Register(socket, token, "private-test-key"); await SendAppInputList(socket, token);
            await RespondToSwitch(socket, token, SwitchOk);
            using var request = await Receive(socket, token);
            Check(request.RootElement.GetProperty("uri").GetString() == "ssap://com.webos.applicationManager/getForegroundAppInfo", "No state query");
            await Send(socket, JsonSerializer.Serialize(new { type = "error", id = request.RootElement.GetProperty("id").GetString(), error = "401 insufficient permissions" }), token);
        });
        var settings = Settings(WebOsSecurity.Fingerprint(cert)); settings.LgInputId = "EXTERNAL_PC_7"; settings.WakeTelevision = false;
        string? warning = await Service(server).PrepareInputAsync(settings, default);
        Check(warning is not null && warning.Contains("принял команду") && warning.Contains("401"), "State refusal reported as input-command refusal");
        await server.Completion;
    }

    private static Task LgInputAppParsing()
    {
        using var valid = JsonDocument.Parse("{\"devices\":[{\"id\":\"arbitrary_port\",\"appId\":\"vendor.custom.input\"}]}");
        Check(WebOsTelevisionService.ParseInputs(valid.RootElement).Single().AppId == "vendor.custom.input", "App mapping not preserved");
        foreach (var appId in new[] { "bad\napp", "with space", new string('x', 257) })
        {
            using var invalid = JsonDocument.Parse(JsonSerializer.Serialize(new { devices = new[] { new { id = "port", appId } } }));
            ThrowsSync<InvalidDataException>(() => WebOsTelevisionService.ParseInputs(invalid.RootElement));
        }
        return Task.CompletedTask;
    }

    private static async Task LgInputAmbiguousApp()
    {
        using var cert = Certificate();
        await using Server server = new(cert, async (socket, token) =>
        {
            await Register(socket, token, "private-test-key"); await SendAppInputList(socket, token, ambiguous: true);
            await RespondToSwitch(socket, token, SwitchDenied); await ExpectNoMoreCommands(socket, token);
        });
        var settings = Settings(WebOsSecurity.Fingerprint(cert)); settings.LgInputId = "EXTERNAL_PC_7";
        await Throws<LgInputCommandException>(() => Service(server).SelectInputAsync(settings, default)); await server.Completion;
    }

    private static async Task LgInputResponseDiagnostics()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ControllerHelper.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "errors.jsonl");
        ErrorJournal previous = ErrorLog.Journal; ErrorLog.Journal = new(path);
        try
        {
            foreach (var (json, reason, code) in new (string, string, int?)[] {
                ("{\"type\":\"error\",\"error\":\"401 secret-user-token\"}", "permission-denied", 401),
                ("{\"type\":\"response\",\"payload\":{\"returnValue\":false,\"errorCode\":\"500\",\"errorText\":\"secret-user-token\"}}", "application-error", 500),
                ("{\"type\":\"error\",\"error\":\"404 no such service or method\"}", "unsupported-method", 404),
                ("{\"type\":\"response\",\"payload\":{}}", "invalid-response", null) })
            {
                using var doc = JsonDocument.Parse(json);
                var result = WebOsTelevisionService.ReadInputResponse(doc.RootElement, "switch-input");
                Check(!result.Accepted && result.Reason == reason && result.Code == code, "Failure reason lost");
            }
            await LgInputFallback();
            string log = File.ReadAllText(path);
            Check(log.Contains("lg.input.fallback") && log.Contains("lg.input.confirmed") && log.Contains("permission-denied") && log.Contains("returnValueKind") &&
                !log.Contains("secret-user-token") && !log.Contains(PcInputApp) && !log.Contains("EXTERNAL_PC_7") && !log.Contains("private-test-key"), "Trace missing or contains secrets");
        }
        finally { ErrorLog.Journal = previous; if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
