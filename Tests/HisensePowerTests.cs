using ControllerHelper.Models;
using ControllerHelper.Services;

internal static partial class HisenseMqttTests
{
    private static (string Name, Func<Task> Run)[] PowerCases =>
    [
        ("Hisense: выключение по TLS/MQTT использует KEY_POWER из APK и подтверждение standby", () => PowerWireFlow(false)),
        ("Hisense: выключение обновляет токен без PIN и пробуждения", () => PowerWireFlow(true)),
        ("Hisense: уже спящий ТВ не получает переключение питания", PowerAlreadyOff),
        ("Hisense: реальные состояния HDMI/VIDAA и переход пробуждения различаются", PowerStateFormats),
        ("Hisense: неизвестное состояние и запрос PIN блокируют питание", PowerUnknown),
        ("Hisense: новый standby заменяет retained ON, чужое состояние игнорируется", PowerStaleState),
        ("Hisense U7QF: retained sourceswitch и молчание gettvstate из журнала не блокируют выключение", () => PowerRetainedWire(false)),
        ("Hisense U7QF: retained состояние до SUBACK не теряется", () => PowerRetainedWire(true)),
        ("Hisense: retained standby не отправляет KEY_POWER", PowerRetainedOff),
        ("Hisense: retained неизвестное состояние не разрешает KEY_POWER", PowerRetainedUnknown),
        ("Hisense: поздний retained ON не заменяет новое событие standby", PowerLateRetained),
        ("Hisense: snapshot в очереди до проверки не удаляется", PowerQueuedSnapshot),
        ("Hisense: retained OFF после команды не служит подтверждением", PowerRetainedConfirmation),
        ("Hisense: обрыв после KEY_POWER не вызывает повтор или ложное подтверждение", () => PowerUnconfirmed(true)),
        ("Hisense: тайм-аут после KEY_POWER не вызывает повтор", () => PowerUnconfirmed(false)),
        ("Hisense: отмена до команды и после неё сохраняет однократность", PowerCancellation),
        ("Hisense: без сопряжения и для LG не отправляется MQTT выключения", PowerRequiresPairing),
        ("Возврат на ПК: мониторы восстанавливаются до сетевого выключения", ReturnBeforePower),
        ("Возврат на ПК: ошибка мониторов или отмена блокируют выключение", ReturnFailureBlocksPower),
        ("Возврат на ПК: сбой сети не откатывает мониторы и не повторяет питание", ReturnPowerFailure),
        ("Hisense: сопряжённый ТВ предлагает выключение без опроса LG", PairedHisenseOverlay)
    ];

    private static async Task PowerWireFlow(bool refresh)
    {
        await using Broker broker = new(mobileProfile: "VIDAA_V2", bootstrapAcl: true, apkSubscriptions: true, powerControl: true);
        var settings = Settings();
        settings.HisenseInputId = null; // Power must not depend on selecting an HDMI input.
        settings.WakeHisense = true;
        settings.HisenseAuthentication = MobileToken(HisenseAuthMethod.MobileV2, DateTimeOffset.UtcNow.AddDays(refresh ? -1 : 1));
        settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
        bool persisted = false;
        var result = await new HisenseTelevisionService(() => new LoopbackTransport(broker)).PowerOffAsync(settings, default, session =>
        {
            session.ApplyConnectionSettings(settings);
            persisted = settings.HisenseAuthentication?.AccessToken == Broker.Access;
        });
        Check(result == HisensePowerOffResult.Confirmed && broker.PowerKeyCount == 1 && !broker.PinReceived && !broker.InputReceived && persisted,
            "Power flow did not confirm standby or sent extra commands");
        Check(broker.Connections.Count == (refresh ? 2 : 1) && broker.Connections.Last().Password == Broker.Access, "Wrong power connection sequence");
    }

    private static Fake PowerFake(string initialState, Action<Fake>? onPower = null)
    {
        Fake fake = new();
        fake.OnSend = (topic, _) =>
        {
            if (topic.EndsWith("/gettvstate")) fake.Put(new(HisenseSession.StateTopic, Utf8(initialState)));
            if (topic.EndsWith("/sendkey")) onPower?.Invoke(fake);
        };
        return fake;
    }

    private static async Task PowerRetainedWire(bool beforeSuback)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ControllerHelper.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "errors.jsonl");
        ErrorJournal previous = ErrorLog.Journal; ErrorLog.Journal = new(path);
        try
        {
            // Exact observed class of response: a retained sourceswitch snapshot
            // on subscription, and no direct reply at all to gettvstate.
            await using Broker broker = new(mobileProfile: "VIDAA_V2", bootstrapAcl: true, powerControl: true,
                retainedPowerState: true, stateBeforeSuback: beforeSuback);
            var settings = Settings();
            settings.HisenseAuthentication = MobileToken(HisenseAuthMethod.MobileV2, DateTimeOffset.UtcNow.AddDays(1));
            settings.HisenseCertificateSha256 = WebOsSecurity.Fingerprint(broker.Certificate);
            var result = await new HisenseTelevisionService(() => new LoopbackTransport(broker)).PowerOffAsync(settings, default);
            Check(result == HisensePowerOffResult.Confirmed && broker.PowerKeyCount == 1 && broker.Connections.Count == 1,
                "Retained state did not reach exactly one power command and live confirmation");
            string log = File.ReadAllText(path);
            Check(log.Contains("retained-broker-snapshot") && log.Contains("hisense.power.off.confirmed"), "Snapshot decision missing from trace");
            Check(!log.Contains(Broker.Access) && !log.Contains(broker.Connections[0].Client), "Power snapshot trace leaks credentials");
        }
        finally { ErrorLog.Journal = previous; if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task PowerRetainedOff()
    {
        Fake fake = new();
        fake.OnSend = (_, _) => fake.Put(new(HisenseSession.StateTopic, Utf8("{\"statetype\":\"fake_sleep_0\"}"), true));
        using HisenseSession session = new(fake, Client);
        Check(await session.PowerOffAsync(default) == HisensePowerOffResult.AlreadyOff && fake.Sent.Count == 1, "Retained standby toggled TV");
    }

    private static async Task PowerRetainedUnknown()
    {
        Fake fake = new();
        fake.OnSend = (_, _) => fake.Put(new(HisenseSession.StateTopic, Utf8("{\"statetype\":\"fake_sleep_1\"}"), true));
        using HisenseSession session = new(fake, Client);
        await Expect<InvalidOperationException>(() => session.PowerOffAsync(default));
        Check(fake.Sent.Count == 1, "Unknown retained state toggled TV");
    }

    private static async Task PowerLateRetained()
    {
        Fake fake = new();
        fake.OnSend = (_, _) =>
        {
            fake.Put(new(HisenseSession.StateTopic, Utf8("{\"statetype\":\"fake_sleep_0\"}")));
            fake.Put(new(HisenseSession.StateTopic, Utf8("{\"statetype\":\"sourceswitch\"}"), true));
        };
        using HisenseSession session = new(fake, Client);
        Check(await session.PowerOffAsync(default) == HisensePowerOffResult.AlreadyOff && fake.Sent.Count == 1, "Late snapshot overwrote live standby");
    }

    private static async Task PowerQueuedSnapshot()
    {
        Fake fake = new();
        // MQTTnet can deliver the retained snapshot while awaiting SUBACK or
        // while the caller saves refreshed tokens, before PowerOffAsync starts.
        fake.Put(new(HisenseSession.StateTopic, Utf8("{\"statetype\":\"sourceswitch\"}"), true));
        fake.OnSend = (topic, _) =>
        {
            if (topic.EndsWith("/sendkey")) fake.Put(new(HisenseSession.StateTopic, Utf8("{\"statetype\":\"fake_sleep_0\"}")));
        };
        using HisenseSession session = new(fake, Client);
        Check(await session.PowerOffAsync(default) == HisensePowerOffResult.Confirmed && fake.Sent.Count == 2, "Initial queue drain lost snapshot");
    }

    private static async Task PowerRetainedConfirmation()
    {
        Fake fake = new();
        fake.OnSend = (topic, _) => fake.Put(new(HisenseSession.StateTopic,
            Utf8(topic.EndsWith("/sendkey") ? "{\"statetype\":\"fake_sleep_0\"}" : "{\"statetype\":\"sourceswitch\"}"), true));
        using HisenseSession session = new(fake, Client);
        Check(await session.PowerOffAsync(default, TimeSpan.FromMilliseconds(30)) == HisensePowerOffResult.SentUnconfirmed && fake.Sent.Count == 2,
            "Retained snapshot was confused with a new acknowledgement");
    }

    private static async Task PowerAlreadyOff()
    {
        foreach (string state in new[] { "fake_sleep", "fake_sleep_0" })
        {
            Fake fake = PowerFake("{\"statetype\":\"" + state + "\"}");
            using HisenseSession session = new(fake, Client);
            Check(await session.PowerOffAsync(default) == HisensePowerOffResult.AlreadyOff && fake.Sent.Count == 1, "Standby TV was toggled on");
        }
    }

    private static async Task PowerUnknown()
    {
        foreach (string payload in new[] { "{}", "{\"statetype\":\"unrecognised\"}", "{\"statetype\":\"fake_sleep_1\"}", "{\"statetype\":\"authentication\"}", "{\"statetype\":\"authenticationcode\"}" })
        {
            Fake fake = PowerFake(payload);
            using HisenseSession session = new(fake, Client);
            await Expect<InvalidOperationException>(() => session.PowerOffAsync(default));
            Check(fake.Sent.Count == 1, "Unconfirmed/auth state triggered a power command or PIN");
        }
    }

    private static async Task PowerStaleState()
    {
        Fake fake = new();
        fake.OnSend = (_, _) =>
        {
            fake.Put(new(HisenseSession.StateTopic, Utf8("{\"statetype\":\"source\"}"), true));
            fake.Put(new("/remoteapp/mobile/another-client/ui_service/state", Utf8("{\"statetype\":\"source\"}")));
            fake.Put(new(HisenseSession.StateTopic, Utf8("{\"statetype\":\"fake_sleep\"}")));
        };
        using HisenseSession session = new(fake, Client);
        Check(await session.PowerOffAsync(default) == HisensePowerOffResult.AlreadyOff && fake.Sent.Count == 1, "Retained/foreign power state accepted");
    }

    private static Task PowerStateFormats()
    {
        // Independent state names from the supplied APK and captured protocol
        // examples in Krazy998/mqtt-hisensetv and warrenrees/pyvidaa.
        Check(HisenseSession.ParsePowerState(Utf8("{\"statetype\":\"sourceswitch\",\"sourceid\":\"4\",\"is_signal\":0}")) == TelevisionPowerState.On,
            "Losing HDMI signal was mistaken for panel standby");
        foreach (string state in new[] { "livetv", "app", "source", "remote_launcher", "remote_setting" })
            Check(HisenseSession.ParsePowerState(Utf8("{\"statetype\":\"" + state + "\"}")) == TelevisionPowerState.On, "Active VIDAA state not recognised");
        Check(HisenseSession.ParsePowerState(Utf8("{\"statetype\":\"fake_sleep_1\",\"sourceid\":\"4\"}")) == TelevisionPowerState.Unknown,
            "Waking transition falsely confirms OFF/authorizes toggle");
        return Task.CompletedTask;
    }

    private static async Task PowerUnconfirmed(bool disconnect)
    {
        Fake fake = PowerFake("{\"statetype\":\"app\"}", f =>
        {
            f.Put(new(HisenseSession.StateTopic, Utf8("{\"statetype\":\"fake_sleep\"}"), true));
            if (disconnect) f.Messages.Writer.TryComplete(new IOException("fixture-disconnect"));
        });
        using HisenseSession session = new(fake, Client);
        Check(await session.PowerOffAsync(default, TimeSpan.FromMilliseconds(30)) == HisensePowerOffResult.SentUnconfirmed, "Close/timeout/retained falsely confirmed standby");
        Check(fake.Sent.Count == 2 && fake.Sent[1] == ($"/remoteapp/tv/remote_service/{Client}/actions/sendkey", "KEY_POWER"), "Power command retried or wrong topic/payload");
        Check(HisenseDiagnostics.Topic(fake.Sent[1].Topic, Client) == "send/remote_service/sendkey", "Power command not identifiable in safe trace");
    }

    private static async Task PowerCancellation()
    {
        foreach (bool afterSend in new[] { false, true })
        {
            using CancellationTokenSource cancellation = new();
            Fake fake = PowerFake("{\"statetype\":\"source\"}", _ => cancellation.Cancel());
            if (!afterSend) cancellation.Cancel();
            using HisenseSession session = new(fake, Client);
            await Expect<OperationCanceledException>(() => session.PowerOffAsync(cancellation.Token));
            Check(fake.Sent.Count(message => message.Topic.EndsWith("/sendkey")) == (afterSend ? 1 : 0), "Canceled command was sent/repeated");
        }
    }

    private static async Task PowerRequiresPairing()
    {
        var service = new HisenseTelevisionService(() => throw new Exception("Must not connect"));
        var settings = Settings(); settings.HisensePaired = false;
        await Expect<InvalidOperationException>(() => service.PowerOffAsync(settings, default));
        settings.HisensePaired = true; settings.TelevisionNetworkProvider = TelevisionNetworkProvider.LG;
        await Expect<InvalidOperationException>(() => service.PowerOffAsync(settings, default));
    }

    private static async Task ReturnBeforePower()
    {
        List<string> steps = [];
        var result = await TelevisionReturnOperation.RunAsync(_ => { steps.Add("pc"); return Task.FromResult<string?>("steam-warning"); },
            _ => { steps.Add("power"); return Task.FromResult("standby"); }, default);
        Check(steps.SequenceEqual(new[] { "pc", "power" }) && result.PcWarning == "steam-warning" && result.PowerOffStatus == "standby", "Return order or warning lost");
    }

    private static async Task ReturnFailureBlocksPower()
    {
        int offCalls = 0;
        Task<string> Power(CancellationToken _) { offCalls++; return Task.FromResult("off"); }
        await Expect<IOException>(() => TelevisionReturnOperation.RunAsync(_ => throw new IOException("display failed"), Power, default));
        using CancellationTokenSource cancel = new();
        await Expect<OperationCanceledException>(() => TelevisionReturnOperation.RunAsync(_ => { cancel.Cancel(); return Task.FromResult<string?>(null); }, Power, cancel.Token));
        var result = await TelevisionReturnOperation.RunAsync(_ => Task.FromResult<string?>(null), null, default);
        Check(offCalls == 0 && result.PowerOffStatus is null, "Failed/canceled/local return sent a power command");
    }

    private static async Task ReturnPowerFailure()
    {
        bool pcReady = false; int powerCalls = 0;
        try
        {
            await TelevisionReturnOperation.RunAsync(_ => { pcReady = true; return Task.FromResult<string?>(null); },
                _ => { powerCalls++; throw new IOException("fixture network failure"); }, default);
            throw new Exception("Network failure was hidden");
        }
        catch (InvalidOperationException error) { Check(error.Message.StartsWith("Мониторы включены"), "Lost partial success message"); }
        Check(pcReady && powerCalls == 1, "Network failure reversed/retried return");
    }

    private static async Task PairedHisenseOverlay()
    {
        var settings = Settings(); settings.TelevisionClientKey = "leftover-LG-key";
        foreach (bool wake in new[] { false, true })
        foreach (bool monitors in new[] { false, true })
        {
            settings.WakeHisense = wake;
            var state = TelevisionNetworkSettings.LocalState(settings, true, monitors);
            var presentation = OverlayPresentation.FromState(state);
            Check(state.PowerOffOnReturn && !state.PowerCheckPending && state.PowerState == TelevisionPowerState.Unknown &&
                presentation.PrimaryAction == OverlayPrimaryAction.ReturnAndPowerOff && presentation.Warning is null, "Paired Hisense lacks power return or probes LG");
        }
        Check(OverlayActionPolicy.Resolve(TelevisionNetworkSettings.LocalState(settings, false, true)) == OverlayPrimaryAction.PlayOnTv, "PC-only state attempts power-off");
        var probe = new TelevisionPowerProbe((_, _) => throw new Exception("Hisense must not probe LG"));
        Check(await probe.ReadAsync(settings, default) == TelevisionPowerState.Unknown, "Hisense reached LG power probe");
        await Expect<InvalidOperationException>(() => { TelevisionNetworkSettings.RequireLg(settings); return Task.CompletedTask; });
    }
}
