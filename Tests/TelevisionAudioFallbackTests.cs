using ControllerHelper.Services;

internal static class TelevisionAudioFallbackTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("ТВ получает HDMI после включения экрана", HdmiSuccess),
        ("Отказ HDMI выбирает ПК и сохраняет изображение ТВ", PcFallback),
        ("Частичное назначение HDMI заменяется ПК для всех ролей", PartialHdmi),
        ("Отказ обоих выходов восстанавливает звук без отката экрана", BothFail),
        ("Отказ восстановления звука не откатывает экран", RestoreFails),
        ("Недоступный снимок звука не блокирует переход на ТВ", CaptureFails),
        ("Одинаковые выходы не ожидаются повторно", SameEndpoint),
        ("Не выбранный резервный выход не откатывает экран", MissingPc),
        ("Отмена HDMI сохраняет штатный откат", Cancellation)
    ];

    private sealed class Rig
    {
        internal string Display = "pc";
        internal string?[] Roles = ["old", "old", "old"];
        internal readonly List<string> Calls = [];
        internal bool FailPrimary, FailPc, FailRestore, FailCapture, Partial;
        internal string? PcId = "pc-audio";
        internal string TvId = "hdmi";
        internal bool Finished;
        internal CancellationTokenSource? CancelPrimary;
        internal string? Warning;
        internal async Task Run()
        {
            AudioDefaults? before = null;
            CancellationToken token = CancelPrimary?.Token ?? default;
            await ProfileSwitchTransaction.ApplyAsync<string, AudioDefaults?>(
                () => Display, () => { Calls.Add("display"); Display = "tv"; }, snapshot => Display = snapshot,
                () => before = TelevisionAudioFallback.Capture(() =>
                    FailCapture ? throw new IOException("capture") : new AudioDefaults([.. Roles])),
                () => Warning = TelevisionAudioFallback.Apply(TvId, PcId, before, Set, Restore, token),
                snapshot => { if (snapshot is not null) Restore(snapshot); },
                finish: () => Finished = true, cancellation: token);
        }
        private void Set(string id, CancellationToken token)
        {
            Check(Display == "tv", "Audio attempted before display activation");
            Calls.Add(id);
            if (id == TvId && CancelPrimary is not null)
            { Roles[0] = id; CancelPrimary.Cancel(); token.ThrowIfCancellationRequested(); }
            if (id == TvId && FailPrimary)
            { if (Partial) Roles[0] = id; throw new IOException("HDMI unavailable"); }
            if (id == PcId && FailPc) throw new IOException("PC unavailable");
            AudioRoleOperations.Apply(id, (endpoint, role) => Roles[role] = endpoint, token);
        }
        private void Restore(AudioDefaults before)
        {
            Calls.Add("restore");
            if (FailRestore) throw new IOException("Restore unavailable");
            Roles = [.. before.EndpointIds];
        }
    }

    private static async Task HdmiSuccess()
    {
        Rig rig = new(); await rig.Run();
        Check(rig.Calls.SequenceEqual(["display", "hdmi"]) && rig.Roles.All(r => r == "hdmi") &&
            rig.Warning is null && rig.Finished && rig.Display == "tv", "HDMI flow failed");
    }
    private static async Task PcFallback()
    {
        Rig rig = new() { FailPrimary = true }; await rig.Run();
        Check(rig.Calls.SequenceEqual(["display", "hdmi", "pc-audio"]) && rig.Roles.All(r => r == "pc-audio") &&
            rig.Warning is not null && rig.Display == "tv" && rig.Finished && rig.TvId == "hdmi", "Fallback rolled back or overwrote preference");
    }
    private static async Task PartialHdmi()
    {
        Rig rig = new() { FailPrimary = true, Partial = true }; await rig.Run();
        Check(rig.Roles.All(r => r == "pc-audio") && rig.Display == "tv", "Mixed roles left after fallback");
    }
    private static async Task BothFail()
    {
        Rig rig = new() { FailPrimary = true, FailPc = true, Partial = true }; await rig.Run();
        Check(rig.Roles.All(r => r == "old") && rig.Display == "tv" && rig.Finished && rig.Warning is not null,
            "Audio failures reverted image or lost warning");
    }
    private static async Task RestoreFails()
    {
        Rig rig = new() { FailPrimary = true, FailPc = true, FailRestore = true }; await rig.Run();
        Check(rig.Display == "tv" && rig.Finished && rig.Warning is not null, "Restore failure undid image");
    }
    private static async Task CaptureFails()
    {
        Rig rig = new() { FailCapture = true, FailPrimary = true }; await rig.Run();
        Check(rig.Display == "tv" && rig.Roles.All(r => r == "pc-audio"), "Capture blocked fallback");
    }
    private static async Task SameEndpoint()
    {
        Rig rig = new() { PcId = "hdmi", FailPrimary = true }; await rig.Run();
        Check(rig.Calls.Count(c => c == "hdmi") == 1 && rig.Display == "tv", "Same endpoint retried");
    }
    private static async Task MissingPc()
    {
        Rig rig = new() { PcId = null, FailPrimary = true }; await rig.Run();
        Check(rig.Display == "tv" && rig.Warning is not null && rig.Roles.All(r => r == "old"), "Missing PC failed switch");
    }
    private static async Task Cancellation()
    {
        using CancellationTokenSource cancellation = new();
        Rig rig = new() { CancelPrimary = cancellation };
        try { await rig.Run(); throw new Exception("Cancellation swallowed"); }
        catch (OperationCanceledException)
        {
            Check(rig.Display == "pc" && rig.Roles.All(r => r == "old") && !rig.Finished &&
                !rig.Calls.Contains("pc-audio"), "Cancellation was treated as fallback");
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
