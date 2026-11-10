using System.Net.WebSockets;
using System.Text.Json;
using ControllerHelper.Models;
using ControllerHelper.Services;

internal static partial class WebOsSecurityTests
{
    private static (string Name, Func<Task> Run)[] InputCases =>
    [
        ("LG: TLS список входов сохраняет реальные ID и отключённый HDMI", LgInputsList),
        ("LG: switchInput отправляет полученный ID и ждёт свой returnValue", LgInputSwitch),
        ("LG: отклонённый switchInput не считается успехом", LgInputRejected),
        ("LG: исчезнувший вход не отправляется", LgInputMissing),
        ("LG: некорректный список входов отклоняется", LgInputParsing),
        ("LG: выбор входа сохраняется отдельно от Hisense и старого JSON", LgInputPersistence),
        ("LG: смена телевизора очищает вход, смена профиля сохраняет его", LgInputTargetChange),
        ("LG: поздний список другого ТВ или сопряжения отклоняется", LgInputStaleTarget),
        ("LG: отсутствие выбора и профиль Hisense не открывают сеть", LgInputDisabled),
        ("LG: ошибка выбора входа остаётся предупреждением", LgInputWarning),
        ("LG: отмена ожидания пробуждения останавливает дальнейшие команды", LgInputCanceled),
        ("LG: после WoL ждём сеть до запроса входа", LgInputWakeOrder),
        ("LG: сертификат другого ТВ блокирует запрос входов и ключ", LgInputCertificate),
        ("LG: требование нового разрешения не запускает скрытое сопряжение", LgInputPairingPrompt),
        ("LG: журнал входов не содержит ключ, названия и идентификаторы", LgInputLogPrivacy),
        .. InputCompatibilityCases
    ];

    private const string InputReply = """
        {"type":"response","id":"lg_inputs","payload":{"returnValue":true,"devices":[
          {"id":"EXTERNAL_PC_7","label":"Рабочий компьютер","connected":false},
          {"id":"PORT_APPLE_2","label":"Apple TV","connected":true}]}}
        """;
    private static async Task SendInputList(WebSocket socket, CancellationToken token)
    {
        using var request = await Receive(socket, token);
        Check(request.RootElement.GetProperty("uri").GetString() == "ssap://tv/getExternalInputList" &&
            request.RootElement.GetProperty("id").GetString() == "lg_inputs", "Wrong list request");
        await Send(socket, InputReply, token);
    }

    private static async Task RespondToSwitch(WebSocket socket, CancellationToken token, string response)
    {
        using var request = await Receive(socket, token);
        Check(request.RootElement.GetProperty("uri").GetString() == "ssap://tv/switchInput" &&
            request.RootElement.GetProperty("id").GetString() == "lg_switch_input", "Wrong switch URI/id");
        var payload = request.RootElement.GetProperty("payload");
        Check(payload.EnumerateObject().Count() == 1 && payload.GetProperty("inputId").GetString() == "EXTERNAL_PC_7", "Hardcoded ID or label sent as ID");
        await Send(socket, response, token);
    }

    private static async Task LgInputsList()
    {
        using var cert = Certificate();
        await using Server server = new(cert, async (socket, token) =>
        { await Register(socket, token, "private-test-key", inputControl: true); await SendInputList(socket, token); });
        var inputs = await Service(server).GetInputsAsync(Settings(WebOsSecurity.Fingerprint(cert)), default);
        await server.Completion;
        Check(inputs.Count == 2 && inputs[0].Id == "EXTERNAL_PC_7" && inputs[0].Name == "Рабочий компьютер" && inputs[0].Connected == false,
            "Inactive input hidden or ID inferred from port number");
    }

    private static async Task LgInputSwitch()
    {
        using var cert = Certificate();
        await using Server server = new(cert, async (socket, token) =>
        {
            await Register(socket, token, "private-test-key", inputControl: true);
            await SendInputList(socket, token);
            await Send(socket, "{\"type\":\"response\",\"id\":\"other_request\",\"payload\":{\"returnValue\":false}}", token);
            await RespondToSwitch(socket, token, "{\"type\":\"response\",\"id\":\"lg_switch_input\",\"payload\":{\"returnValue\":true}}");
        });
        var settings = Settings(WebOsSecurity.Fingerprint(cert)); settings.LgInputId = "EXTERNAL_PC_7";
        settings.LgInputName = "This display name must not be sent";
        await Service(server).SelectInputAsync(settings, default); await server.Completion;
    }

    private static async Task LgInputRejected()
    {
        foreach (string response in new[] {
            "{\"type\":\"response\",\"id\":\"lg_switch_input\",\"payload\":{\"returnValue\":false}}",
            "{\"type\":\"error\",\"id\":\"lg_switch_input\",\"error\":\"403 access denied\"}",
            "{\"type\":\"response\",\"id\":\"lg_switch_input\",\"payload\":{}}" })
        {
            using var cert = Certificate();
            await using Server server = new(cert, async (socket, token) =>
            { await Register(socket, token, "private-test-key"); await SendInputList(socket, token); await RespondToSwitch(socket, token, response); });
            var settings = Settings(WebOsSecurity.Fingerprint(cert)); settings.LgInputId = "EXTERNAL_PC_7";
            await Throws<InvalidOperationException>(() => Service(server).SelectInputAsync(settings, default));
            await server.Completion;
        }
    }

    private static async Task LgInputMissing()
    {
        using var cert = Certificate();
        await using Server server = new(cert, async (socket, token) =>
        { await Register(socket, token, "private-test-key"); await SendInputList(socket, token); });
        var settings = Settings(WebOsSecurity.Fingerprint(cert)); settings.LgInputId = "old-input";
        try { await Service(server).SelectInputAsync(settings, default); throw new Exception("Missing input sent"); }
        catch (InvalidOperationException error) { Check(error.Message.Contains("отсутствует"), "Missing ID was not detected before switchInput"); }
        await server.Completion;
    }

    private static Task LgInputParsing()
    {
        foreach (string json in new[] { "{}", "{\"devices\":[]}", "{\"devices\":[{\"label\":\"no ID\"}]}",
            "{\"devices\":[{\"id\":\"same\"},{\"id\":\"same\"}]}",
            "{\"devices\":[{\"id\":\"invalid\\nID\"}]}" })
        {
            using var document = JsonDocument.Parse(json);
            ThrowsSync<InvalidDataException>(() => WebOsTelevisionService.ParseInputs(document.RootElement));
        }
        using var large = JsonDocument.Parse(JsonSerializer.Serialize(new { devices = Enumerable.Range(0, 65).Select(n => new { id = n.ToString() }) }));
        ThrowsSync<InvalidDataException>(() => WebOsTelevisionService.ParseInputs(large.RootElement));
        using var noLabel = JsonDocument.Parse("{\"devices\":[{\"id\":\"vendor_input\"}]}");
        var input = WebOsTelevisionService.ParseInputs(noLabel.RootElement).Single();
        Check(input.Name == "vendor_input" && input.Connected is null, "Optional label/status rejected");
        return Task.CompletedTask;
    }

    private static Task LgInputPersistence()
    {
        var settings = Settings(); settings.LgInputId = "pc-port"; settings.LgInputName = "ПК"; settings.HisenseInputId = "27";
        var copy = SettingsStore.Deserialize(SettingsStore.Serialize(settings.Copy()), out _)!;
        Check(copy.LgInputId == "pc-port" && copy.LgInputName == "ПК" && copy.HisenseInputId == "27", "Brand inputs lost or mixed");
        Check(SettingsStore.Deserialize("{}", out _)!.LgInputId is null, "Old settings unexpectedly switch HDMI");
        return Task.CompletedTask;
    }

    private static Task LgInputTargetChange()
    {
        var previous = Settings(); previous.LgInputId = "pc-port"; previous.LgInputName = "ПК";
        foreach (Action<AppSettings> change in new Action<AppSettings>[] { s => s.TelevisionId = "another",
            s => s.TelevisionIpAddress = "192.0.2.12", s => s.TelevisionMacAddress = "11:22:33:44:55:66" })
        {
            var next = previous.Copy(); change(next); LgInputSettings.Reconcile(previous, next);
            Check(next.LgInputId is null && next.LgInputName is null, "Input leaked to different TV");
        }
        var otherBrand = previous.Copy(); otherBrand.TelevisionNetworkProvider = TelevisionNetworkProvider.Hisense;
        LgInputSettings.Reconcile(previous, otherBrand);
        Check(otherBrand.LgInputId == "pc-port", "Inactive LG preference discarded");
        return Task.CompletedTask;
    }

    private static Task LgInputStaleTarget()
    {
        var settings = Settings(new string('A', 64)); var target = TelevisionPairingTarget.From(settings);
        var changed = settings.Copy(); changed.TelevisionIpAddress = "192.0.2.11";
        ThrowsSync<InvalidOperationException>(() => LgInputSettings.EnsureCurrent(target, settings.TelevisionClientKey, changed));
        changed = settings.Copy(); changed.TelevisionClientKey = "new-key";
        ThrowsSync<InvalidOperationException>(() => LgInputSettings.EnsureCurrent(target, settings.TelevisionClientKey, changed));
        LgInputSettings.EnsureCurrent(target, settings.TelevisionClientKey, settings.Copy());
        return Task.CompletedTask;
    }

    private static async Task LgInputDisabled()
    {
        var service = new WebOsTelevisionService((_, _) => throw new Exception("Must not connect"), (_, _) => throw new Exception("Must not wait"));
        var settings = Settings();
        Check(await service.PrepareInputAsync(settings, default) is null, "Old settings connected for input");
        settings.LgInputId = "pc-port"; settings.TelevisionNetworkProvider = TelevisionNetworkProvider.Hisense;
        Check(await service.PrepareInputAsync(settings, default) is null, "Hisense connected to webOS");
    }

    private static async Task LgInputWarning()
    {
        var settings = Settings(new string('A', 64)); settings.LgInputId = "pc-port"; settings.WakeTelevision = false;
        var service = new WebOsTelevisionService((_, _) => throw new IOException("network down"));
        Check(await service.PrepareInputAsync(settings, default) is not null, "Network failure blocked the display switch");
    }

    private static async Task LgInputCanceled()
    {
        var settings = Settings(new string('A', 64)); settings.LgInputId = "pc-port";
        using CancellationTokenSource cancellation = new();
        var service = new WebOsTelevisionService((_, _) => throw new Exception("Must not connect after canceled wake"),
            (_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await Throws<OperationCanceledException>(() => service.PrepareInputAsync(settings, cancellation.Token));
    }

    private static async Task LgInputWakeOrder()
    {
        using var cert = Certificate();
        await using Server server = new(cert, async (socket, token) =>
        {
            await Register(socket, token, "private-test-key"); await SendInputList(socket, token);
            await RespondToSwitch(socket, token, "{\"type\":\"response\",\"id\":\"lg_switch_input\",\"payload\":{\"returnValue\":true}}");
        });
        List<string> steps = [];
        var service = new WebOsTelevisionService((settings, token) => { steps.Add("connect"); return WebOsConnection.ConnectEndpointAsync(settings, server.Endpoint, token); },
            (_, _) => { steps.Add("wait-ready"); return Task.CompletedTask; });
        var settings = Settings(WebOsSecurity.Fingerprint(cert)); settings.LgInputId = "EXTERNAL_PC_7";
        Check(await service.PrepareInputAsync(settings, default) is null && steps.SequenceEqual(new[] { "wait-ready", "connect" }), "Wrong wake/input ordering");
        await server.Completion;
    }

    private static async Task LgInputCertificate()
    {
        using var cert = Certificate(); using var other = Certificate();
        await using Server server = new(cert, (_, _) => throw new Exception("Key must not reach untrusted server"), expectRejected: true);
        await Throws<TelevisionCertificateException>(() => Service(server).GetInputsAsync(Settings(WebOsSecurity.Fingerprint(other)), default));
        await server.Completion;
        Check(!server.HttpReceived, "Input request bypassed TLS pin");
    }

    private static async Task LgInputPairingPrompt()
    {
        using var cert = Certificate();
        await using Server server = new(cert, (socket, token) => Register(socket, token, "private-test-key", inputControl: true, prompt: true));
        await Throws<InvalidOperationException>(() => Service(server).GetInputsAsync(Settings(WebOsSecurity.Fingerprint(cert)), default));
        await server.Completion;
    }

    private static async Task LgInputLogPrivacy()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ControllerHelper.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "errors.jsonl");
        ErrorJournal previous = ErrorLog.Journal; ErrorLog.Journal = new(path);
        try
        {
            await LgInputSwitch();
            string log = File.ReadAllText(path);
            Check(log.Contains("lg.input.accepted") && !log.Contains("private-test-key") && !log.Contains("EXTERNAL_PC_7") &&
                !log.Contains("Apple TV") && !log.Contains("Рабочий компьютер"), "Input trace missing or contains private data");
        }
        finally { ErrorLog.Journal = previous; if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
