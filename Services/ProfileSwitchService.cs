using ControllerHelper.Models;

namespace ControllerHelper.Services;

public sealed class ProfileSwitchService(DisplayService displays, AudioService audio)
{
    public void ValidateTelevisionProfile(AppSettings settings) =>
        ErrorLog.Step("preflight.tv", () => PrepareTelevisionProfile(settings));

    private AppSettings PrepareTelevisionProfile(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.TelevisionId))
            throw new InvalidOperationException("Выберите телевизор.");
        if (settings.MonitorIds.Contains(settings.TelevisionId, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Телевизор не должен входить в список мониторов ПК.");
        if (string.IsNullOrWhiteSpace(settings.TvAudioId))
            throw new InvalidOperationException("Выберите устройство звука для телевизора.");
        AppSettings working = ErrorLog.Step("preflight.pc-profile", () => displays.PreparePcForTelevision(settings));
        ErrorLog.Step("preflight.tv-options", () => displays.ValidateTelevisionOptions(settings));
        if (settings.TelevisionMode is null) DisplayProfileCodec.ValidateVersion(settings.TvProfile);
        ErrorLog.Step("preflight.steam", SteamBigPictureService.EnsureAvailable);
        return working;
    }

    public async Task<string?> SwitchToTelevisionAsync(AppSettings settings, CancellationToken cancellation = default)
    {
        AppSettings working = ErrorLog.Step("preflight.tv", () => PrepareTelevisionProfile(settings));
        bool profileSaved = false;
        DisplaySettingsRollback videoRollback = new();
        AudioDefaults? audioBefore = null;
        string? audioWarning = null;
        try
        {
            await ProfileSwitchTransaction.ApplyAsync<DisplayTopologySnapshot, AudioDefaults?>(
                displays.CaptureActiveTopology, () =>
                {
                    ErrorLog.Step("television.topology", () => displays.SwitchToTelevision(working, cancellation));
                    ErrorLog.Step("television.video-options", () => displays.ApplyTelevisionOptions(working, videoRollback, cancellation));
                    cancellation.ThrowIfCancellationRequested();
                    displays.CaptureTelevisionLayout(working);
                },
                snapshot => videoRollback.Restore(() => displays.RestoreTopology(snapshot)),
                captureAudio: () => audioBefore = TelevisionAudioFallback.Capture(audio.CaptureDefaults),
                changeAudio: () => audioWarning = TelevisionAudioFallback.Apply(
                    working.TvAudioId!, working.PcAudioId, audioBefore, audio.SetDefault, audio.RestoreDefaults, cancellation),
                restoreAudio: snapshot => { if (snapshot is not null) audio.RestoreDefaults(snapshot); },
                finish: () =>
                {
                    SettingsStore.Save(working);
                    profileSaved = true;
                    SteamBigPictureService.Open();
                }, cancellation: cancellation);
            settings.TvPath = working.TvPath;
            settings.TvModes = working.TvModes;
            settings.PcPaths = working.PcPaths;
            settings.PcModes = working.PcModes;
            settings.PcProfile = working.PcProfile?.Copy();
            settings.TvProfile = working.TvProfile?.Copy();
            return audioWarning;
        }
        catch (Exception original)
        {
            if (profileSaved)
            {
                try { SettingsStore.Save(settings); }
                catch (Exception error)
                {
                    throw new InvalidOperationException(
                        $"{original.Message} Не удалось восстановить прежний профиль ТВ: {error.Message}",
                        new AggregateException(original, error));
                }
            }
            throw;
        }
    }

    public async Task<string?> SwitchToPcAsync(AppSettings settings, CancellationToken cancellation = default)
    {
        ErrorLog.Event("preflight.pc.begin");
        if (!settings.HasPcProfile)
            throw new InvalidOperationException("Сначала сохраните раскладку мониторов ПК.");
        if (string.IsNullOrWhiteSpace(settings.PcAudioId))
            throw new InvalidOperationException("Выберите устройство звука для ПК.");
        ErrorLog.Step("preflight.audio-interface", audio.EnsureSwitchSupported);
        AppSettings working = settings.Copy();
        await Task.Run(() => ErrorLog.Step("preflight.pc-profile", () => displays.ValidatePcProfile(working)));
        ErrorLog.Event("preflight.pc.ok");
        await ApplyWithRollbackAsync(() => displays.SwitchToPc(working),
            () => audio.SetDefault(working.PcAudioId, cancellation), () => SettingsStore.Save(working), cancellation);
        settings.PcProfile = working.PcProfile?.Copy();
        try { SteamBigPictureService.Close(); }
        catch (Exception error)
        {
            StartupTrace.Write($"Could not close Steam Big Picture: {error}");
            ErrorLog.Write("steam.close", error);
            return error.Message;
        }
        return null;
    }

    public async Task<bool> ToggleMonitorsAsync(AppSettings settings, CancellationToken cancellation = default)
    {
        ErrorLog.Event("profile.toggle-monitors");
        bool enabled = false;
        AppSettings working = settings.Copy();
        await ApplyWithRollbackAsync(() => enabled = displays.ToggleMonitors(working),
            finish: () => SettingsStore.Save(working), cancellation: cancellation);
        settings.PcProfile = working.PcProfile?.Copy();
        return enabled;
    }

    public Task<string?> RecoverToPcAsync(AppSettings settings, CancellationToken cancellation) =>
        PcRecoveryOperation.RunAsync(() => displays.SwitchToPc(settings.Copy()),
            token => audio.SetDefault(settings.PcAudioId, token), SteamBigPictureService.Close, cancellation);

    private Task ApplyWithRollbackAsync(Action changeDisplay, Action? changeAudio = null,
        Action? finish = null, CancellationToken cancellation = default,
        Action<DisplayTopologySnapshot>? restoreDisplay = null) =>
        ProfileSwitchTransaction.ApplyAsync<DisplayTopologySnapshot, AudioDefaults>(
            displays.CaptureActiveTopology, changeDisplay, restoreDisplay ?? displays.RestoreTopology,
            changeAudio is null ? null : audio.CaptureDefaults, changeAudio,
            changeAudio is null ? null : audio.RestoreDefaults, finish: finish, cancellation: cancellation);
}
