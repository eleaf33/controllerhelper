using ControllerHelper.Services;
using ControllerHelper.Models;

ControllerHelper.Localization.Text.SetLanguage("ru");

if (args is ["--validate-profile-file", string profileFile])
    return DisplayProfileTests.ValidateFile(profileFile);
if (args is ["--tv-preflight-probe", string televisionProfileFile])
    return DisplayProfileTests.ValidateTelevisionPreparationFile(televisionProfileFile);
if (args is ["--tv-video-probe"])
    return TelevisionVideoTests.Probe();
ErrorLog.Journal = new ErrorJournal(Path.Combine(Path.GetTempPath(), "ControllerHelper.Tests", Guid.NewGuid().ToString("N"), "errors.jsonl"));

(string Name, Func<Task> Run)[] tests =
[
    ("Успешное переключение", SuccessfulSwitch),
    ("Ошибка дисплея откатывает дисплей и звук", DisplayFailure),
    ("Ошибка звука откатывает дисплей и звук", AudioFailure),
    ("Ошибка завершающего действия откатывает профиль", FinishFailure),
    ("Ошибки отката не скрывают исходную ошибку", RollbackFailure),
    ("Ошибка захвата не меняет устройства", CaptureFailure),
    ("Переключение без звука не трогает аудио", DisplayOnly),
    ("Неполные операции звука отклоняются", MissingAudioOperations),
    ("Выключенный ТВ предлагает запуск", TvOffAction),
    ("Активный ТВ без мониторов предлагает выключение", TvOnAction),
    ("Неизвестное питание предлагает обычный возврат", UnknownPowerAction),
    ("Без WoL оверлей разрешает переход с мониторов на ТВ", TvWithoutWakeAction),
    ("Без WoL активный ТВ предлагает возврат без выключения", TvWithoutWakeReturn),
    ("Без активных выбранных экранов оверлей не предлагает переход", NoSelectedDisplayAction),
    ("Навигация пропускает недоступные действия", NavigationSkipsDisabled),
    ("Подтверждение выполняется до завершающего действия", ConfirmationBeforeFinish),
    ("Отказ подтверждения откатывает профиль", ConfirmationRejected),
    ("Ключ webOS сохраняется защищённым", ProtectedTelevisionKey),
    ("Старый открытый ключ читается для миграции", LegacyTelevisionKey),
    .. StageOneTests.Cases,
    .. PriorityAuditTests.Cases,
    .. AudioEndpointTests.Cases,
    .. TelevisionAudioFallbackTests.Cases,
    .. TelevisionNetworkTests.Cases,
    .. HisenseMqttTests.Cases,
    .. EmergencyReturnTests.Cases,
    .. SettingsFileTests.Cases,
    .. AudioRoleTests.Cases,
    .. InputIsolationTests.Cases,
    .. NativeMethodCacheTests.Cases,
    .. InputPollingTests.Cases,
    .. OverlayGeometryTests.Cases,
    .. TelevisionVideoTests.Cases,
    .. StageTwoTests.Cases,
    .. TelevisionReadinessTests.Cases,
    .. DisplayProfileTests.Cases,
    .. ErrorJournalTests.Cases,
    .. FlydigiDiagnosticsTests.Cases,
    .. FlydigiWindowSelectionTests.Cases,
    .. FlydigiWindowActivationTests.Cases,
    .. LocalizationTests.Cases,
    .. WebOsSecurityTests.Cases
];

int failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {error}");
    }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} тестов пройдено");
return failures == 0 ? 0 : 1;

static async Task SuccessfulSwitch()
{
    List<string> calls = [];
    await ProfileSwitchTransaction.ApplyAsync(
        () => { calls.Add("capture display"); return "old display"; },
        () => calls.Add("change display"),
        snapshot => calls.Add($"restore {snapshot}"),
        () => { calls.Add("capture audio"); return "old audio"; },
        () => calls.Add("change audio"),
        snapshot => calls.Add($"restore {snapshot}"),
        finish: () => calls.Add("finish"));
    AssertCalls(calls, "capture display", "capture audio", "change display", "change audio", "finish");
}

static async Task DisplayFailure()
{
    List<string> calls = [];
    InvalidOperationException error = await ExpectThrows<InvalidOperationException>(() =>
        ProfileSwitchTransaction.ApplyAsync(
            () => { calls.Add("capture display"); return "old display"; },
            () => { calls.Add("change display"); throw new InvalidOperationException("display failed"); },
            snapshot => calls.Add($"restore {snapshot}"),
            () => { calls.Add("capture audio"); return "old audio"; },
            () => calls.Add("change audio"),
            snapshot => calls.Add($"restore {snapshot}")));
    Assert(error.Message == "display failed", "Исходная ошибка дисплея потеряна.");
    AssertCalls(calls, "capture display", "capture audio", "change display",
        "restore old display", "restore old audio");
}

static async Task AudioFailure()
{
    List<string> calls = [];
    InvalidOperationException error = await ExpectThrows<InvalidOperationException>(() =>
        ProfileSwitchTransaction.ApplyAsync(
            () => { calls.Add("capture display"); return "old display"; },
            () => calls.Add("change display"),
            snapshot => calls.Add($"restore {snapshot}"),
            () => { calls.Add("capture audio"); return "old audio"; },
            () => { calls.Add("change audio"); throw new InvalidOperationException("audio failed"); },
            snapshot => calls.Add($"restore {snapshot}")));
    Assert(error.Message == "audio failed", "Исходная ошибка звука потеряна.");
    AssertCalls(calls, "capture display", "capture audio", "change display",
        "change audio", "restore old display", "restore old audio");
}

static async Task FinishFailure()
{
    List<string> calls = [];
    await ExpectThrows<InvalidOperationException>(() => ProfileSwitchTransaction.ApplyAsync(
        () => "old display", () => calls.Add("change display"),
        snapshot => calls.Add($"restore {snapshot}"),
        () => "old audio", () => calls.Add("change audio"),
        snapshot => calls.Add($"restore {snapshot}"),
        finish: () => throw new InvalidOperationException("finish failed")));
    AssertCalls(calls, "change display", "change audio", "restore old display", "restore old audio");
}

static async Task RollbackFailure()
{
    InvalidOperationException error = await ExpectThrows<InvalidOperationException>(() =>
        ProfileSwitchTransaction.ApplyAsync(
            () => "old display", () => throw new InvalidOperationException("original"),
            _ => throw new InvalidOperationException("display rollback"),
            () => "old audio", () => { },
            _ => throw new InvalidOperationException("audio rollback")));
    Assert(error.InnerException is AggregateException aggregate && aggregate.InnerExceptions.Count == 3,
        "Ожидались исходная ошибка и обе ошибки отката.");
    Assert(error.Message.Contains("original") && error.Message.Contains("display rollback") &&
        error.Message.Contains("audio rollback"), "Сообщение не содержит причины сбоя.");
}

static async Task CaptureFailure()
{
    List<string> calls = [];
    await ExpectThrows<InvalidOperationException>(() => ProfileSwitchTransaction.ApplyAsync<string, string>(
        () => throw new InvalidOperationException("capture failed"),
        () => calls.Add("change display"),
        _ => calls.Add("restore display")));
    Assert(calls.Count == 0, "Устройства затронуты до успешного захвата.");
}

static async Task DisplayOnly()
{
    List<string> calls = [];
    await ProfileSwitchTransaction.ApplyAsync<string, string>(
        () => "old display", () => calls.Add("change display"),
        _ => calls.Add("restore display"));
    AssertCalls(calls, "change display");
}

static async Task MissingAudioOperations()
{
    List<string> calls = [];
    await ExpectThrows<ArgumentException>(() => ProfileSwitchTransaction.ApplyAsync<string, string>(
        () => "old display", () => calls.Add("change display"),
        _ => calls.Add("restore display"), changeAudio: () => calls.Add("change audio")));
    Assert(calls.Count == 0, "Устройства затронуты при неполных операциях звука.");
}

static Task TvOffAction()
{
    TelevisionOverlayState state = new(true, true, TelevisionPowerState.Off, false, true);
    OverlayPresentation presentation = OverlayPresentation.FromState(state);
    Assert(presentation.PrimaryAction == OverlayPrimaryAction.PlayOnTv, "Должен быть доступен переход на ТВ.");
    Assert(!presentation.MonitorEnabled, "Переключатель мониторов недоступен без активного ТВ.");
    return Task.CompletedTask;
}

static Task TvOnAction()
{
    TelevisionOverlayState state = new(true, true, TelevisionPowerState.On, true, false);
    OverlayPresentation presentation = OverlayPresentation.FromState(state);
    Assert(presentation.PrimaryAction == OverlayPrimaryAction.ReturnAndPowerOff,
        "Должен быть доступен возврат с выключением ТВ.");
    Assert(presentation.MonitorEnabled, "При активном ТВ мониторы можно включить.");
    return Task.CompletedTask;
}

static Task UnknownPowerAction()
{
    TelevisionOverlayState state = new(true, false, TelevisionPowerState.Unknown, true, false);
    OverlayPresentation presentation = OverlayPresentation.FromState(state);
    Assert(presentation.PrimaryAction == OverlayPrimaryAction.ReturnToPc,
        "При неизвестном питании нельзя предлагать сетевое выключение.");
    Assert(presentation.Warning is not null, "Пользователь должен увидеть предупреждение о сети.");
    return Task.CompletedTask;
}

static Task TvWithoutWakeAction()
{
    foreach (TelevisionPowerState power in Enum.GetValues<TelevisionPowerState>())
    {
        OverlayPresentation presentation = OverlayPresentation.FromState(new(false, false, power, false, true));
        Assert(presentation.PrimaryAction == OverlayPrimaryAction.PlayOnTv,
            "Отключённый WoL не должен блокировать локальное переключение HDMI.");
        Assert(presentation.PrimaryLabel == "Играть на ТВ" && presentation.Warning is null,
            "Без сетевого управления оверлей не должен объявлять ТВ недоступным.");
    }
    return Task.CompletedTask;
}

static Task TvWithoutWakeReturn()
{
    foreach (bool monitorsActive in new[] { false, true })
    foreach (TelevisionPowerState power in Enum.GetValues<TelevisionPowerState>())
    {
        OverlayPresentation presentation = OverlayPresentation.FromState(new(false, false, power, true, monitorsActive));
        Assert(presentation.PrimaryAction == OverlayPrimaryAction.ReturnToPc && presentation.MonitorEnabled,
            "Без WoL возврат с активного ТВ не должен выключать его по сети.");
    }
    return Task.CompletedTask;
}

static Task NoSelectedDisplayAction()
{
    foreach (bool wake in new[] { false, true })
        Assert(OverlayActionPolicy.Resolve(new(wake, false, TelevisionPowerState.Unknown, false, false)) == OverlayPrimaryAction.None,
            "Не следует предлагать переход, если ни один выбранный экран не активен.");
    return Task.CompletedTask;
}

static Task NavigationSkipsDisabled()
{
    OverlayNavigation navigation = new();
    Assert(navigation.MoveHorizontal(-1, false, false) == 2,
        "Навигация не должна попадать на недоступный монитор.");
    navigation.Select(0);
    Assert(navigation.MoveVertical(1, false, false) == 4, "Нижний ряд недоступен.");
    navigation.Select(4);
    Assert(navigation.MoveVertical(-1, false, false) == 2,
        "Возврат с нижнего ряда должен выбирать доступную кнопку.");
    return Task.CompletedTask;
}

static async Task ConfirmationBeforeFinish()
{
    List<string> calls = [];
    await ProfileSwitchTransaction.ApplyAsync<string, string>(
        () => "old display", () => calls.Add("change display"),
        _ => calls.Add("restore display"),
        confirmDisplay: () => { calls.Add("confirm"); return Task.FromResult(true); },
        finish: () => calls.Add("finish"));
    AssertCalls(calls, "change display", "confirm", "finish");
}

static async Task ConfirmationRejected()
{
    List<string> calls = [];
    await ExpectThrows<TimeoutException>(() => ProfileSwitchTransaction.ApplyAsync(
        () => "old display", () => calls.Add("change display"),
        _ => calls.Add("restore display"),
        () => "old audio", () => calls.Add("change audio"),
        _ => calls.Add("restore audio"),
        () => { calls.Add("confirm"); return Task.FromResult(false); },
        () => calls.Add("finish")));
    AssertCalls(calls, "change display", "change audio", "confirm", "restore display", "restore audio");
}

static Task ProtectedTelevisionKey()
{
    AppSettings settings = new() { TelevisionClientKey = "secret-webos-key" };
    string json = SettingsStore.Serialize(settings);
    Assert(!json.Contains("secret-webos-key"), "Ключ попал в файл открытым текстом.");
    AppSettings? restored = SettingsStore.Deserialize(json, out bool plaintext);
    Assert(restored?.TelevisionClientKey == settings.TelevisionClientKey && !plaintext,
        "Защищённый ключ не восстановлен.");
    return Task.CompletedTask;
}

static Task LegacyTelevisionKey()
{
    string json = "{\"TelevisionClientKey\":\"old-secret\",\"MonitorIds\":[]}";
    AppSettings? restored = SettingsStore.Deserialize(json, out bool plaintext);
    Assert(restored?.TelevisionClientKey == "old-secret" && plaintext,
        "Ключ из старых настроек не прочитан для миграции.");
    return Task.CompletedTask;
}

static async Task<TException> ExpectThrows<TException>(Func<Task> action) where TException : Exception
{
    try { await action(); }
    catch (TException error) { return error; }
    throw new Exception($"Ожидалась ошибка {typeof(TException).Name}.");
}

static void AssertCalls(List<string> actual, params string[] expected) =>
    Assert(actual.SequenceEqual(expected), $"Порядок операций: {string.Join(", ", actual)}");

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
