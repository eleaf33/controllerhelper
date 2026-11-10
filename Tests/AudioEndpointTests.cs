using ControllerHelper.Services;

internal static class AudioEndpointTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Статусы аудио различают отсутствие, отключение и отсоединение", () => Run(States)),
        ("Старые выходы скрыты, текущий выбор сохраняется", () => Run(Choices)),
        ("Обновление находит новый HDMI без автоматической подмены", () => Run(Reconnect))
        ,("HDMI можно выбрать до активации экрана", () => Run(SelectOfflineHdmi))
    ];
    private static void States()
    {
        Check(new AudioEndpoint("a", "TV", 1).ToString() == "TV", "Active mislabeled");
        var texts = new uint[] { 0, 1, 2, 4, 8, 16 }.Select(state => new AudioEndpoint("a", "TV", state).ToString());
        Check(texts.Distinct().Count() == 6, "Statuses conflated");
    }
    private static void Choices()
    {
        AudioEndpoint[] devices = [new("active", "TV", 1), new("old", "Old", 4), new("unplugged", "USB", 8)];
        Check(AudioEndpointChoices.Create(devices, null).Select(d => d.Id).SequenceEqual(["active", "unplugged"]), "Disconnected endpoint hidden or stale devices leaked");
        var selected = AudioEndpointChoices.Create(devices, "unplugged");
        Check(selected.Length == 2 && selected[1].State == 8, "Offline selection discarded");
    }
    private static void Reconnect()
    {
        var first = AudioEndpointChoices.Create([], "saved");
        Check(first.Length == 1 && first[0].State == 0, "Missing selection discarded");
        var refreshed = AudioEndpointChoices.Create([new("saved", "TV", 1)], "SAVED");
        Check(refreshed.Length == 1 && refreshed[0].IsActive, "Status did not refresh");
        var changedId = AudioEndpointChoices.Create([new("new", "TV", 1)], "saved");
        Check(changedId.Length == 2 && changedId.Any(d => d.Id == "saved" && d.State == 0), "Selection silently replaced");
    }

    private static void SelectOfflineHdmi()
    {
        AudioEndpoint[] devices = [new("pc", "Speakers", 1), new("hdmi", "TV", 8), new("old", "Old HDMI", 4)];
        var choices = AudioEndpointChoices.Create(devices, "old");
        Check(choices.Any(d => d.Id == "hdmi" && d.State == 8), "Cannot select offline TV");
        var afterSelection = AudioEndpointChoices.Create(devices, "hdmi");
        Check(afterSelection.Any(d => d.Id == "hdmi") && !afterSelection.Any(d => d.Id == "old"), "Historical selection was not replaced");
        var enabled = AudioEndpointChoices.Create([new("pc", "Speakers", 8), new("hdmi", "TV", 1)], "hdmi");
        Check(enabled.First().Id == "hdmi" && enabled.First().IsActive, "TV did not become ready after activation");
    }
    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
