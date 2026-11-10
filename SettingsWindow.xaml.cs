using ControllerHelper.Models;
using ControllerHelper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace ControllerHelper;

public sealed partial class SettingsWindow : Window
{
    private AppSettings _settings;
    private readonly DisplayService _displays;
    private readonly AudioService _audio;
    private readonly ProfileSwitchService _profileSwitches;
    private readonly TelevisionWakeService _televisionWake = new();
    private readonly WebOsTelevisionService _webOs = new();
    private readonly TelevisionPowerProbe _powerProbe;
    private CancellationTokenSource? _pairingCancellation;
    private CancellationTokenSource? _certificateCancellation;
    private DisplayDevice[] _screens = [];
    private bool _refreshingDisplays;
    private readonly ProfileOperationCoordinator _profileOperations = new();
    private bool _switching => _profileOperations.IsRunning;
    public bool IsSwitching => _switching;
    public bool IsRecovering => _profileOperations.IsRecovering;
    public event Action? ProfileSwitchFinished;
    private bool _initialSizeApplied;
    private ControllerMonitor? _controller;
    private readonly ControllerButtonCatalog _buttonCatalog = new();
    private bool _updatingControllers;
    private bool _updatingTvTarget, _updatingTvVideo;
    private int _tvCatalogVersion;
    private string? _tvOptionsDisplayId;
    private TelevisionVideoMode? _chosenTelevisionMode;
    private int? _chosenTelevisionScale;
    private TelevisionDisplayCatalog? _tvCatalog;

    private sealed record ResolutionChoice(int Width, int Height)
    {
        public override string ToString() => Width == 0 ? ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_275") : $"{Width} × {Height}";
    }
    private sealed record ScaleChoice(int? Percent)
    {
        public override string ToString() => Percent is int value ? $"{value}%" : ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_276");
    }

    public SettingsWindow(AppSettings settings, DisplayService displays, AudioService audio)
    {
        StartupTrace.Write("SettingsWindow constructor");
        InitializeComponent();
        ((FrameworkElement)Content).Language = ControllerHelper.Localization.Text.LanguageTag;
        LanguageBox.SelectedIndex = ControllerHelper.Localization.Text.NormalizeLanguage(settings.Language) == "ru" ? 1 : 0;
        StartupTrace.Write("SettingsWindow XAML initialized");
        Title = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_277");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(440, 740));
        Activated += (_, _) =>
        {
            if (_initialSizeApplied) return;
            _initialSizeApplied = true;
            DispatcherQueue.TryEnqueue(ResizeForCurrentDpi);
        };
        AppWindow.TitleBar.BackgroundColor = Windows.UI.Color.FromArgb(255, 16, 18, 24);
        AppWindow.TitleBar.ForegroundColor = Windows.UI.Color.FromArgb(255, 244, 241, 255);
        AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(255, 16, 18, 24);
        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 244, 241, 255);
        AppWindow.TitleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 16, 18, 24);
        AppWindow.TitleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 168, 173, 189);
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 16, 18, 24);
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 168, 173, 189);
        string iconPath = Path.Combine(AppContext.BaseDirectory, "controller.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        _settings = settings;
        _tvOptionsDisplayId = settings.TelevisionId;
        _chosenTelevisionMode = settings.TelevisionMode;
        _chosenTelevisionScale = settings.TelevisionScalePercent;
        _displays = displays;
        _audio = audio;
        PcAudioBox.DropDownOpened += (_, _) => RefreshAudioChoicesSafely();
        TvAudioBox.DropDownOpened += (_, _) => RefreshAudioChoicesSafely();
        _powerProbe = new TelevisionPowerProbe(_webOs.GetPowerStateAsync);
        Closed += (_, _) => { _pairingCancellation?.Cancel(); _certificateCancellation?.Cancel(); _tvCatalogVersion++; };
        TelevisionBox.SelectionChanged += (_, _) =>
        {
            if (!_updatingTvTarget) _ = RefreshTvVideoOptionsAsync();
        };
        _profileSwitches = new ProfileSwitchService(displays, audio);
        bool hotkeyCorrected = _buttonCatalog.Normalize(_settings);
        WakeTelevisionToggle.IsOn = _settings.WakeTelevision;
        TvNetworkProviderBox.SelectedIndex = _settings.TelevisionNetworkProvider == TelevisionNetworkProvider.Hisense ? 1 : 0;
        WakeHisenseToggle.IsOn = _settings.WakeHisense;
        HisenseIpBox.Text = _settings.HisenseIpAddress ?? string.Empty;
        HisenseMacBox.Text = _settings.HisenseMacAddress ?? string.Empty;
        LgNetworkExpander.IsExpanded = TvNetworkProviderBox.SelectedIndex == 0;
        HisenseNetworkExpander.IsExpanded = TvNetworkProviderBox.SelectedIndex == 1;
        TelevisionIpBox.Text = _settings.TelevisionIpAddress ?? string.Empty;
        TelevisionMacBox.Text = _settings.TelevisionMacAddress ?? string.Empty;
        AllowInsecureWebOsBox.IsChecked = _settings.AllowInsecureWebOs;
        UpdateCertificateStatus();
        InitializeHisenseControls();
        InitializeLgInputControls();
        MonitorList.SelectionChanged += (_, _) =>
        {
            if (!_refreshingDisplays)
                UpdateTelevisionChoices((TelevisionBox.SelectedItem as DisplayDevice)?.Id);
        };
        _buttonCatalog.Ensure(_settings.HotkeyModifier);
        _buttonCatalog.Ensure(_settings.HotkeyTrigger);
        _buttonCatalog.Ensure(_settings.EmergencyReturnModifier);
        _buttonCatalog.Ensure(_settings.EmergencyReturnTrigger);
        EmergencyModifierBox.ItemsSource = _buttonCatalog.Choices;
        EmergencyTriggerBox.ItemsSource = _buttonCatalog.Choices;
        EmergencyModifierBox.SelectedItem = _buttonCatalog.Choices.First(choice => choice.Key == _settings.EmergencyReturnModifier);
        EmergencyTriggerBox.SelectedItem = _buttonCatalog.Choices.First(choice => choice.Key == _settings.EmergencyReturnTrigger);
        EmergencyReturnToggle.IsOn = _settings.EmergencyReturnEnabled;
        EmergencyReturnOptions.IsEnabled = EmergencyReturnToggle.IsOn;
        ModifierBox.ItemsSource = _buttonCatalog.Choices;
        TriggerBox.ItemsSource = _buttonCatalog.Choices;
        ModifierBox.SelectedItem = _buttonCatalog.Choices.FirstOrDefault(choice => choice.Key == _settings.HotkeyModifier) ?? _buttonCatalog.Choices[0];
        TriggerBox.SelectedItem = _buttonCatalog.Choices.FirstOrDefault(choice => choice.Key == _settings.HotkeyTrigger) ?? _buttonCatalog.Choices[4];
        if (ReferenceEquals(ModifierBox.SelectedItem, TriggerBox.SelectedItem))
        {
            ModifierBox.SelectedItem = _buttonCatalog.Choices[0];
            TriggerBox.SelectedItem = _buttonCatalog.Choices[4];
        }
        UpdateHotkeyDescription();
        StartupTrace.Write("SettingsWindow controls initialized");
        RefreshDevices();
        if (SettingsStore.LoadWarning is string warning) StatusText.Text = warning;
        if (hotkeyCorrected)
            StatusText.Text += ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_278");
        StartupTrace.Write("SettingsWindow devices refreshed");
    }

    public void AttachController(ControllerMonitor controller)
    {
        _controller = controller;
        _controller.DevicesChanged += RefreshControllers;
        RefreshControllers();
        _controller.Configure(_settings.ControllerDeviceId, _settings.ControllerFingerprint,
            ((GamepadButtonChoice)ModifierBox.SelectedItem).Key,
            ((GamepadButtonChoice)TriggerBox.SelectedItem).Key,
            _settings.EmergencyReturnEnabled, _settings.EmergencyReturnModifier, _settings.EmergencyReturnTrigger);
    }

    public void RefreshDevices()
    {
        try
        {
            DisplayDevice[] screens = _displays.Enumerate().ToArray();
            AudioEndpoint[] endpoints = RefreshAudioChoices();
            bool missingTvAudio = !string.IsNullOrWhiteSpace(_settings.TvAudioId) &&
                !endpoints.Any(endpoint => endpoint.Id.Equals(_settings.TvAudioId, StringComparison.OrdinalIgnoreCase));
            ErrorLog.Event("devices.audio-selection", new {
                tvEndpointPresent = !missingTvAudio && !string.IsNullOrWhiteSpace(_settings.TvAudioId),
                tvEndpointActive = endpoints.Any(endpoint => endpoint.IsActive &&
                    endpoint.Id.Equals(_settings.TvAudioId, StringComparison.OrdinalIgnoreCase)) });
            StartupTrace.Write($"Detected screens: {screens.Length}, audio outputs: {endpoints.Length}");
            _screens = screens;
            _refreshingDisplays = true;
            try
            {
                MonitorList.SelectedItems.Clear();
                MonitorList.ItemsSource = screens;
                foreach (DisplayDevice screen in screens.Where(screen => _settings.MonitorIds.Contains(screen.Id, StringComparer.OrdinalIgnoreCase)))
                    MonitorList.SelectedItems.Add(screen);
                UpdateTelevisionChoices(_settings.TelevisionId);
            }
            finally { _refreshingDisplays = false; }
            bool conflictingSelection = _settings.MonitorIds.Contains(_settings.TelevisionId ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
            StatusText.Text = conflictingSelection
                ? ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_279")
                : ControllerHelper.Localization.Text.Format("SettingsWindow_xaml_280", screens.Length, endpoints.Count(endpoint => endpoint.IsActive));
            if (missingTvAudio)
                StatusText.Text += ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_281");
        }
        catch (Exception error)
        {
            StartupTrace.Write($"Device enumeration failed: {error}");
            ErrorLog.Write("devices.enumerate", error);
            StatusText.Text = error.Message;
        }
    }

    private AudioEndpoint[] RefreshAudioChoices()
    {
        AudioEndpoint[] endpoints = _audio.Enumerate().ToArray();
        Update(PcAudioBox, _settings.PcAudioId);
        Update(TvAudioBox, _settings.TvAudioId);
        return endpoints;

        void Update(ComboBox box, string? savedId)
        {
            // Preserve an unsaved user choice, including an endpoint that disappeared.
            string? selectedId = (box.SelectedItem as AudioEndpoint)?.Id ?? savedId;
            AudioEndpoint[] choices = AudioEndpointChoices.Create(endpoints, selectedId);
            box.ItemsSource = choices;
            box.SelectedItem = choices.FirstOrDefault(device =>
                string.Equals(device.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void RefreshAudioChoicesSafely()
    {
        try { RefreshAudioChoices(); }
        catch (Exception error)
        {
            ErrorLog.Write("audio.choices-refresh", error);
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_282") + error.Message;
        }
    }

    private void UpdateTelevisionChoices(string? preferredId)
    {
        HashSet<string> monitorIds = MonitorList.SelectedItems.Cast<DisplayDevice>()
            .Select(screen => screen.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        DisplayDevice[] choices = _screens.Where(screen => !monitorIds.Contains(screen.Id)).ToArray();
        if (preferredId is not null && !monitorIds.Contains(preferredId) &&
            !choices.Any(screen => screen.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase)))
            choices = [.. choices, new(preferredId, ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_283"), false)];
        _updatingTvTarget = true;
        try
        {
            TelevisionBox.ItemsSource = choices;
            TelevisionBox.SelectedItem = choices.FirstOrDefault(screen =>
                screen.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase))
                ?? (choices.Length == 1 ? choices[0] : null);
        }
        finally { _updatingTvTarget = false; }
        _ = RefreshTvVideoOptionsAsync();
    }

    private async Task RefreshTvVideoOptionsAsync()
    {
        string? id = (TelevisionBox.SelectedItem as DisplayDevice)?.Id;
        int version = ++_tvCatalogVersion;
        if (!string.Equals(id, _tvOptionsDisplayId, StringComparison.OrdinalIgnoreCase))
        {
            _tvOptionsDisplayId = id;
            _chosenTelevisionMode = null;
            _chosenTelevisionScale = null;
        }
        _tvCatalog = null;
        RenderTvVideoChoices();
        TvVideoStatusText.Text = id is null ? ControllerHelper.Localization.Text.Get("DisplayProfileCodec_058") : ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_284");
        if (id is null) return;
        try
        {
            var catalog = await Task.Run(() => _displays.GetTelevisionDisplayCatalog(id));
            if (version != _tvCatalogVersion) return;
            _tvCatalog = catalog;
            RenderTvVideoChoices();
            TvVideoStatusText.Text = catalog.ModeWarning ?? (catalog.CurrentMode is { } current
                ? ControllerHelper.Localization.Text.Format("SettingsWindow_xaml_285", current) : ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_286"));
            if (_chosenTelevisionMode is { } chosen && !catalog.Modes.Contains(chosen))
                TvVideoStatusText.Text += ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_287");
        }
        catch (Exception error)
        {
            if (version != _tvCatalogVersion) return;
            TvVideoStatusText.Text = error.Message + ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_288");
            ErrorLog.Write("television.video.enumerate", error);
        }
    }

    private void RenderTvVideoChoices()
    {
        _updatingTvVideo = true;
        try
        {
            var modes = (_tvCatalog?.Modes ?? []).ToList();
            if (_chosenTelevisionMode is { } saved && !modes.Contains(saved)) modes.Add(saved);
            ResolutionChoice[] resolutions = [new(0, 0), .. modes.Select(mode => new ResolutionChoice(mode.Width, mode.Height))
                .Distinct().OrderByDescending(mode => (long)mode.Width * mode.Height)];
            TvResolutionBox.ItemsSource = resolutions;
            TvResolutionBox.SelectedItem = _chosenTelevisionMode is { } chosen
                ? resolutions.First(mode => mode.Width == chosen.Width && mode.Height == chosen.Height) : resolutions[0];
            TelevisionVideoMode[] rates = _chosenTelevisionMode is { } selection
                ? modes.Where(mode => mode.Width == selection.Width && mode.Height == selection.Height)
                    .OrderByDescending(mode => mode.Frequency).ThenBy(mode => mode.Interlaced).ToArray() : [];
            TvRefreshBox.DisplayMemberPath = nameof(TelevisionVideoMode.RefreshLabel);
            TvRefreshBox.ItemsSource = rates;
            TvRefreshBox.SelectedItem = rates.FirstOrDefault(mode => mode == _chosenTelevisionMode);
            TvRefreshBox.IsEnabled = rates.Length > 0;
            var percentages = (_tvCatalog?.Scale?.Choices ?? []).ToList();
            if (_chosenTelevisionScale is int scale && !percentages.Contains(scale)) percentages.Add(scale);
            ScaleChoice[] scales = [new(null), .. percentages.Distinct().Order().Select(value => new ScaleChoice(value))];
            TvScaleBox.ItemsSource = scales;
            TvScaleBox.SelectedItem = scales.First(choice => choice.Percent == _chosenTelevisionScale);
            TvScaleStatusText.Text = _tvCatalog?.ScaleWarning ?? (_tvCatalog?.Scale is { } range
                ? ControllerHelper.Localization.Text.Format("SettingsWindow_xaml_289", range.CurrentPercent)
                : ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_290"));
        }
        finally { _updatingTvVideo = false; }
    }

    private void TvResolution_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingTvVideo || TvResolutionBox.SelectedItem is not ResolutionChoice resolution) return;
        var modes = (_tvCatalog?.Modes ?? []).ToList();
        if (_chosenTelevisionMode is { } saved && !modes.Contains(saved)) modes.Add(saved);
        _chosenTelevisionMode = resolution.Width == 0 ? null : modes
            .Where(mode => mode.Width == resolution.Width && mode.Height == resolution.Height)
            .OrderBy(mode => mode.Interlaced).ThenByDescending(mode => mode.Frequency).FirstOrDefault();
        RenderTvVideoChoices();
    }

    private void TvRefresh_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_updatingTvVideo && TvRefreshBox.SelectedItem is TelevisionVideoMode mode) _chosenTelevisionMode = mode;
    }

    private void TvScale_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_updatingTvVideo && TvScaleBox.SelectedItem is ScaleChoice scale) _chosenTelevisionScale = scale.Percent;
    }

    private async void RefreshTvVideo_Click(object sender, RoutedEventArgs args) => await RefreshTvVideoOptionsAsync();

    public void ReportError(string message) => StatusText.Text = message;

    // public void ReportController(string message) => ControllerStatus.Text = message;

    private Task<T> RunProfileSwitchAsync<T>(Func<CancellationToken, Task<T>> operation) =>
        _profileOperations.RunAsync(cancellation => ExecuteProfileSwitchAsync(operation, cancellation));

    private async Task<T> ExecuteProfileSwitchAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellation)
        {
            _hisenseCancellation?.Cancel();
            _lgInputsCancellation?.Cancel();
            using IDisposable diagnostic = ErrorLog.BeginOperation("profile.operation");
            ErrorLog.Event("profile.configuration", new {
                monitorCount = _settings.MonitorIds.Count, hasPcProfile = _settings.HasPcProfile,
                hasTvProfile = _settings.TvProfile is not null,
                televisionSelected = !string.IsNullOrWhiteSpace(_settings.TelevisionId),
                pcAudioSelected = !string.IsNullOrWhiteSpace(_settings.PcAudioId),
                tvAudioSelected = !string.IsNullOrWhiteSpace(_settings.TvAudioId),
                wake = TelevisionNetworkSettings.WakeEnabled(_settings), networkProvider = _settings.TelevisionNetworkProvider.ToString(), mode = _settings.TelevisionMode,
                scale = _settings.TelevisionScalePercent });
            TvButton.IsEnabled = PcButton.IsEnabled = false;
            TvVideoPanel.IsEnabled = false;
            _powerProbe.Invalidate();
            try
            {
                T result = await operation(cancellation);
                ErrorLog.Event("profile.operation.ok");
                return result;
            }
            catch (Exception error)
            {
                if (error is not OperationCanceledException) ErrorLog.Write("profile.switch", error);
                else ErrorLog.Event("profile.operation.canceled");
                StatusText.Text = error is OperationCanceledException ? ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_291") : error.Message;
                throw;
            }
            finally
            {
                _powerProbe.Invalidate();
                TvButton.IsEnabled = PcButton.IsEnabled = true;
                TvVideoPanel.IsEnabled = true;
                ProfileSwitchFinished?.Invoke();
            }
        }

    private Task RunProfileSwitchAsync(Func<CancellationToken, Task> operation) =>
        RunProfileSwitchAsync(async cancellation => { await operation(cancellation); return true; });

    public Task StopProfileSwitchesAsync()
    {
        StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_292");
        _pairingCancellation?.Cancel();
        _certificateCancellation?.Cancel();
        _hisenseCancellation?.Cancel();
        _lgInputsCancellation?.Cancel();
        return _profileOperations.StopAsync();
    }

    public Task SwitchToTvAsync() => RunProfileSwitchAsync(async cancellation =>
    {
        ErrorLog.Event("profile.to-tv");
        AppSettings settings = _settings;
        _profileSwitches.ValidateTelevisionProfile(settings);
        if (TelevisionNetworkSettings.WakeEnabled(settings))
        {
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_293");
            ErrorLog.Event("television.wake.begin");
            await _televisionWake.SendWakeAsync(settings, cancellation);
            ErrorLog.Event("television.wake.ok");
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_294");
        }
        cancellation.ThrowIfCancellationRequested();
        HisenseTarget hisenseTarget = HisenseTarget.From(settings);
        string? inputWarning = await _hisenseService.PrepareInputAsync(settings, cancellation, connection =>
            PersistHisenseConnection(settings, hisenseTarget, connection, cancellation));
        string? lgInputWarning = await _webOs.PrepareInputAsync(settings, cancellation);
        cancellation.ThrowIfCancellationRequested();
        string? audioWarning = await _profileSwitches.SwitchToTelevisionAsync(settings, cancellation);
        string? warning = string.Join(" ", new[] { inputWarning, lgInputWarning, audioWarning }.Where(text => text is not null));
        StatusText.Text = string.IsNullOrEmpty(warning)
            ? ControllerHelper.Localization.Text.Format("SettingsWindow_xaml_295", settings.HotkeyModifier, settings.HotkeyTrigger)
            : ControllerHelper.Localization.Text.Format("SettingsWindow_xaml_296", warning);
        if (string.IsNullOrEmpty(warning)) AppWindow.Hide();
    });

    public Task SwitchToPcAsync() => RunProfileSwitchAsync(async cancellation =>
    {
        ErrorLog.Event("profile.to-pc");
        AppSettings settings = _settings;
        bool powerOff = TelevisionNetworkSettings.HasHisensePowerControl(settings) &&
            _displays.GetMonitorToggleState(settings).TelevisionActive;
        await ReturnToPcWithPowerAsync(settings, powerOff, cancellation);
    });

    public Task RecoverToPcAsync() => _profileOperations.RecoverAsync(cancellation =>
        ExecuteProfileSwitchAsync(async token =>
        {
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_297");
            string? warning = await _profileSwitches.RecoverToPcAsync(_settings.Copy(), token);
            StatusText.Text = warning is null ? ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_298")
                : ControllerHelper.Localization.Text.Format("SettingsWindow_xaml_299", warning);
            return true;
        }, cancellation));

    private async Task<string?> SwitchToPcCoreAsync(AppSettings settings, CancellationToken cancellation)
    {
        string? steamError = await _profileSwitches.SwitchToPcAsync(settings, cancellation);
        StatusText.Text = steamError is null
            ? ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_300")
            : ControllerHelper.Localization.Text.Format("SettingsWindow_xaml_301", steamError);
        return steamError;
    }

    public (bool TelevisionActive, bool MonitorsActive) GetMonitorToggleState() =>
        _displays.GetMonitorToggleState(_settings);

    public Windows.Graphics.RectInt32? GetOverlayTelevisionBounds() =>
        _displays.GetTelevisionBounds(_settings.TelevisionId);

    public TelevisionOverlayState GetOverlayLocalState()
    {
        AppSettings settings = _settings;
        var displayState = _displays.GetMonitorToggleState(settings);
        return TelevisionNetworkSettings.LocalState(settings, displayState.TelevisionActive, displayState.MonitorsActive);
    }

    public async Task<TelevisionOverlayState> GetOverlayStateAsync(CancellationToken cancellation)
    {
        AppSettings settings = _settings.Copy();
        TelevisionOverlayState local = GetOverlayLocalState();
        if (!local.PowerCheckPending) return local;
        TelevisionPowerState power = await _powerProbe.ReadAsync(settings, cancellation);
        cancellation.ThrowIfCancellationRequested();
        TelevisionOverlayState current = GetOverlayLocalState();
        if (TelevisionPairingTarget.From(settings) != TelevisionPairingTarget.From(_settings) ||
            settings.TelevisionClientKey != _settings.TelevisionClientKey ||
            local.TelevisionActive != current.TelevisionActive || local.MonitorsActive != current.MonitorsActive ||
            settings.WakeTelevision != _settings.WakeTelevision)
            return current with { PowerCheckPending = false };
        return current with { NetworkReachable = power != TelevisionPowerState.Unknown,
            PowerState = power, PowerCheckPending = false };
    }

    public Task SwitchToPcAndPowerOffAsync() => RunProfileSwitchAsync(async cancellation =>
    {
        AppSettings settings = _settings;
        await ReturnToPcWithPowerAsync(settings, true, cancellation);
    });

    private async Task ReturnToPcWithPowerAsync(AppSettings settings, bool powerOff, CancellationToken cancellation)
    {
        var result = await TelevisionReturnOperation.RunAsync(
            token => SwitchToPcCoreAsync(settings, token),
            powerOff ? token => PowerOffTelevisionAsync(settings, token) : null, cancellation);
        if (result.PowerOffStatus is null) return;
        StatusText.Text = result.PowerOffStatus;
        if (result.PcWarning is not null)
            StatusText.Text += ControllerHelper.Localization.Text.Format("SettingsWindow_xaml_302", result.PcWarning);
    }

    private async Task<string> PowerOffTelevisionAsync(AppSettings settings, CancellationToken cancellation)
    {
        StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_303");
        if (settings.TelevisionNetworkProvider == TelevisionNetworkProvider.Hisense)
        {
            HisenseTarget target = HisenseTarget.From(settings);
            var result = await _hisenseService.PowerOffAsync(settings, cancellation, connection =>
                PersistHisenseConnection(settings, target, connection, cancellation));
            return result switch
            {
                HisensePowerOffResult.AlreadyOff => ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_304"),
                HisensePowerOffResult.Confirmed => ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_305"),
                _ => ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_306")
            };
        }
        TelevisionNetworkSettings.RequireLg(settings);
        bool confirmed = await _webOs.PowerOffAsync(settings, cancellation);
        return confirmed ? ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_307")
            : ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_308");
    }

    private void PersistHisenseConnection(AppSettings settings, HisenseTarget target, HisenseSession connection, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        target.EnsureCurrent(_settings);
        // Runs inside the profile coordinator. Save refreshed tokens first, then
        // update the settings instance used by subsequent profile operations.
        AppSettings updated = settings.Copy();
        connection.ApplyConnectionSettings(updated);
        SettingsStore.Save(updated);
        settings.HisenseAuthentication = updated.HisenseAuthentication;
        settings.HisenseAuthenticationProtected = updated.HisenseAuthenticationProtected;
        settings.HisensePreferredAuthMethod = updated.HisensePreferredAuthMethod;
    }

    public Task<bool> ToggleMonitorsAsync() => RunProfileSwitchAsync(async cancellation =>
    {
        AppSettings settings = _settings;
        bool enabled = await _profileSwitches.ToggleMonitorsAsync(settings, cancellation);
        StatusText.Text = enabled
            ? ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_309")
            : ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_310");
        return enabled;
    });

    private AppSettings ReadSelections()
    {
        List<string> monitors = MonitorList.SelectedItems.Cast<DisplayDevice>().Select(screen => screen.Id).ToList();
        string? televisionId = (TelevisionBox.SelectedItem as DisplayDevice)?.Id;
        if (televisionId is not null && monitors.Contains(televisionId, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayService_080"));
        string modifier = ((GamepadButtonChoice)ModifierBox.SelectedItem).Key;
        string trigger = ((GamepadButtonChoice)TriggerBox.SelectedItem).Key;
        if (modifier == trigger) throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("ControllerMonitor_052"));
        AppSettings candidate = _settings.Copy();
        if (!monitors.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(_settings.MonitorIds))
        {
            candidate.PcPaths = null;
            candidate.PcModes = null;
            candidate.PcProfile = null;
        }
        if (!string.Equals(televisionId, _settings.TelevisionId, StringComparison.OrdinalIgnoreCase))
        {
            candidate.TvPath = null;
            candidate.TvModes = null;
            candidate.TvProfile = null;
        }
        candidate.MonitorIds = monitors;
        candidate.TelevisionId = televisionId;
        candidate.TelevisionMode = _chosenTelevisionMode;
        candidate.Language = LanguageBox.SelectedIndex == 1 ? "ru" : "en";
        candidate.TelevisionScalePercent = _chosenTelevisionScale;
        TelevisionDisplayOptions.Validate(candidate);
        candidate.PcAudioId = (PcAudioBox.SelectedItem as AudioEndpoint)?.Id;
        candidate.TvAudioId = (TvAudioBox.SelectedItem as AudioEndpoint)?.Id;
        candidate.TelevisionNetworkProvider = SelectedNetworkProvider;
        if (WakeTelevisionToggle.IsOn && SelectedNetworkProvider == TelevisionNetworkProvider.LG)
        {
            candidate.TelevisionIpAddress = TelevisionWakeService.ParseAddress(TelevisionIpBox.Text).ToString();
            string macAddress = TelevisionWakeService.NormalizeMacAddress(TelevisionMacBox.Text);
            candidate.TelevisionMacAddress = macAddress;
        }
        else
        {
            candidate.TelevisionIpAddress = TelevisionIpBox.Text.Trim();
            candidate.TelevisionMacAddress = TelevisionMacBox.Text.Trim();
        }
        candidate.WakeTelevision = WakeTelevisionToggle.IsOn;
        candidate.WakeHisense = WakeHisenseToggle.IsOn;
        candidate.HisenseIpAddress = HisenseIpBox.Text.Trim();
        candidate.HisenseMacAddress = HisenseMacBox.Text.Trim();
        if (candidate.WakeHisense && SelectedNetworkProvider == TelevisionNetworkProvider.Hisense)
        {
            candidate.HisenseIpAddress = TelevisionWakeService.ParseAddress(candidate.HisenseIpAddress).ToString();
            candidate.HisenseMacAddress = TelevisionWakeService.NormalizeMacAddress(candidate.HisenseMacAddress);
        }
        candidate.AllowInsecureWebOs = AllowInsecureWebOsBox.IsChecked == true;
        candidate.HotkeyModifier = modifier;
        candidate.HotkeyTrigger = trigger;
        candidate.EmergencyReturnEnabled = EmergencyReturnToggle.IsOn;
        candidate.EmergencyReturnModifier = ((GamepadButtonChoice)EmergencyModifierBox.SelectedItem).Key;
        candidate.EmergencyReturnTrigger = ((GamepadButtonChoice)EmergencyTriggerBox.SelectedItem).Key;
        EmergencyHotkey.Validate(candidate.EmergencyReturnEnabled, candidate.EmergencyReturnModifier,
            candidate.EmergencyReturnTrigger, modifier, trigger);
        if (ControllerBox.SelectedItem is ControllerDevice selectedController)
        {
            candidate.ControllerDeviceId = selectedController.Id;
            candidate.ControllerFingerprint = selectedController.Fingerprint;
        }
        WebOsSecurity.ReconcileSettings(_settings, candidate);
        candidate.LgInputId = (LgInputBox.SelectedItem as LgInput)?.Id;
        candidate.LgInputName = (LgInputBox.SelectedItem as LgInput)?.Name;
        LgInputSettings.Reconcile(_settings, candidate);
        candidate.HisenseInputId = (HisenseInputBox.SelectedItem as HisenseInput)?.Id;
        candidate.HisenseInputName = (HisenseInputBox.SelectedItem as HisenseInput)?.Name;
        HisenseSettings.Reconcile(_settings, candidate);
        return candidate;
    }

    private void CommitSettings(AppSettings candidate)
    {
        if (_switching || _profileOperations.IsStopping)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_251"));
        HisenseTarget oldHisense = HisenseTarget.From(_settings), newHisense = HisenseTarget.From(candidate);
        bool hisenseDeviceChanged = oldHisense.Display != newHisense.Display || oldHisense.Address != newHisense.Address || oldHisense.Mac != newHisense.Mac;
        bool lgDeviceChanged = !LgInputSettings.SameDevice(_settings, candidate);
        SettingsStore.Save(candidate);
        if (TelevisionPairingTarget.From(candidate) != TelevisionPairingTarget.From(_settings) || candidate.TelevisionClientKey != _settings.TelevisionClientKey)
            _lgInputsCancellation?.Cancel();
        if (!_hisenseCommitting && HisenseTarget.From(candidate) != HisenseTarget.From(_settings))
            _hisenseCancellation?.Cancel();
        if (TelevisionPairingTarget.From(candidate) != TelevisionPairingTarget.From(_settings))
        {
            _pairingCancellation?.Cancel();
            _certificateCancellation?.Cancel();
        }
        if (TelevisionPairingTarget.From(candidate) != TelevisionPairingTarget.From(_settings) ||
            candidate.TelevisionClientKey != _settings.TelevisionClientKey || candidate.WakeTelevision != _settings.WakeTelevision)
            _powerProbe.Invalidate();
        _settings = candidate;
        if (hisenseDeviceChanged) { HisenseInputBox.SelectedItem = null; SetHisenseInputs([]); }
        if (lgDeviceChanged) { LgInputBox.SelectedItem = null; SetLgInputs([]); }
        UpdateHisenseStatus();
        UpdateCertificateStatus();
        _controller?.Configure(candidate.ControllerDeviceId, candidate.ControllerFingerprint,
            candidate.HotkeyModifier, candidate.HotkeyTrigger,
            candidate.EmergencyReturnEnabled, candidate.EmergencyReturnModifier, candidate.EmergencyReturnTrigger);
        UpdateHotkeyDescription();
    }

    public double GetFlydigiCursorSpeed() => Math.Clamp(_settings.FlydigiCursorSpeed, 0.25, 2.5);

    public void SetFlydigiCursorSpeed(double multiplier)
    {
        if (_switching)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_251"));
        AppSettings candidate = _settings.Copy();
        candidate.FlydigiCursorSpeed = Math.Clamp(multiplier, 0.25, 2.5);
        SettingsStore.Save(candidate);
        _settings = candidate;
    }

    private void UpdateHotkeyDescription()
    {
        string modifier = (ModifierBox.SelectedItem as GamepadButtonChoice)?.Key ?? _settings.HotkeyModifier;
        string trigger = (TriggerBox.SelectedItem as GamepadButtonChoice)?.Key ?? _settings.HotkeyTrigger;
        HotkeyDescription.Text = ControllerHelper.Localization.Text.Format("SettingsWindow_xaml_311", modifier, trigger);
    }

    private void RefreshControllers()
    {
        if (_controller is null) return;
        ControllerDevice? previousSelection = ControllerBox.SelectedItem as ControllerDevice;
        string? wantedId = previousSelection?.Id ?? _settings.ControllerDeviceId;
        string? fingerprint = previousSelection?.Fingerprint ?? _settings.ControllerFingerprint;
        ControllerDevice[] devices = _controller.Devices.ToArray();
        ControllerDevice? selected = ControllerSelection.Resolve(devices, wantedId, fingerprint);
        _updatingControllers = true;
        ControllerBox.ItemsSource = devices;
        ControllerBox.SelectedItem = selected;
        _updatingControllers = false;
        UpdateControllerButtons();
        UpdateControllerStatus();
        if (!_switching && selected is not null && _settings.ControllerDeviceId is not null &&
            (previousSelection is null || previousSelection.Id == _settings.ControllerDeviceId) &&
            (selected.Id != _settings.ControllerDeviceId || selected.Fingerprint != _settings.ControllerFingerprint))
        {
            AppSettings candidate = _settings.Copy();
            candidate.ControllerDeviceId = selected.Id;
            candidate.ControllerFingerprint = selected.Fingerprint;
            try { CommitSettings(candidate); }
            catch (Exception error) { ControllerStatus.Text = ControllerHelper.Localization.Text.Format("SettingsWindow_xaml_312", error.Message); }
        }
    }

    private void ControllerBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingControllers || _controller is null) return;
        UpdateControllerButtons();
        UpdateControllerStatus();
        if (ControllerBox.SelectedItem is ControllerDevice device)
            _controller.Configure(device.Id, device.Fingerprint,
                _settings.HotkeyModifier, _settings.HotkeyTrigger,
                _settings.EmergencyReturnEnabled, _settings.EmergencyReturnModifier, _settings.EmergencyReturnTrigger);
    }

    private void UpdateControllerStatus()
    {
        ControllerStatus.Text = _controller?.Error ?? (_controller?.Devices.Count == 0
            ? ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_313")
            : ControllerBox.SelectedItem is ControllerDevice selected
                ? ControllerHelper.Localization.Text.Format("SettingsWindow_xaml_314", selected.Name)
                : ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_315"));
    }

    private void UpdateControllerButtons()
    {
        if (ControllerBox.SelectedItem is not ControllerDevice selected) return;
        _buttonCatalog.EnsureRawButtons(selected.ControllerButtons.Length);
        RefreshButtonBoxes();
    }

    private void RefreshButtonBoxes()
    {
        foreach (ComboBox box in new[] { ModifierBox, TriggerBox, EmergencyModifierBox, EmergencyTriggerBox })
        {
            string? key = (box.SelectedItem as GamepadButtonChoice)?.Key;
            box.ItemsSource = _buttonCatalog.Choices.ToArray();
            box.SelectedItem = ((GamepadButtonChoice[])box.ItemsSource).FirstOrDefault(choice => choice.Key == key);
        }
    }

    private void LearnModifier_Click(object sender, RoutedEventArgs args) => LearnButton(ModifierBox, ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_316"));
    private void LearnTrigger_Click(object sender, RoutedEventArgs args) => LearnButton(TriggerBox, ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_317"));
    private void LearnEmergencyModifier_Click(object sender, RoutedEventArgs args) => LearnButton(EmergencyModifierBox, ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_318"));
    private void LearnEmergencyTrigger_Click(object sender, RoutedEventArgs args) => LearnButton(EmergencyTriggerBox, ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_319"));
    private void EmergencyReturn_Toggled(object sender, RoutedEventArgs args)
    {
        if (EmergencyReturnOptions is not null) EmergencyReturnOptions.IsEnabled = EmergencyReturnToggle.IsOn;
    }

    private void LearnButton(ComboBox box, string label)
    {
        if (_controller is null || ControllerBox.SelectedItem is not ControllerDevice)
        {
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_320");
            return;
        }
        StatusText.Text = ControllerHelper.Localization.Text.Format("SettingsWindow_xaml_321", label);
        _controller.LearnButton(key =>
        {
            _buttonCatalog.Ensure(key);
            RefreshButtonBoxes();
            box.SelectedItem = ((GamepadButtonChoice[])box.ItemsSource).First(choice => choice.Key == key);
            StatusText.Text = ControllerHelper.Localization.Text.Format("SettingsWindow_xaml_322", key);
        });
    }

    private void Save_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            CommitSettings(ReadSelections());
            StatusText.Text = ControllerHelper.Localization.Text.Get(_settings.Language == ControllerHelper.Localization.Text.Language
                ? "SettingsWindow_xaml_323" : "Language_RestartSaved");
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private TelevisionNetworkProvider SelectedNetworkProvider => TvNetworkProviderBox.SelectedIndex == 1
        ? TelevisionNetworkProvider.Hisense : TelevisionNetworkProvider.LG;

    private async void CheckTvConnection_Click(object sender, RoutedEventArgs args) =>
        await CheckConnectionAsync(CheckTvConnectionButton, TelevisionIpBox, TelevisionMacBox);

    private async void CheckHisenseConnection_Click(object sender, RoutedEventArgs args) =>
        await CheckConnectionAsync(CheckHisenseConnectionButton, HisenseIpBox, HisenseMacBox);

    private async Task CheckConnectionAsync(Button button, TextBox addressBox, TextBox macBox)
    {
        button.IsEnabled = false;
        string requestedAddress = addressBox.Text;
        string previousMac = macBox.Text;
        try
        {
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_324");
            TelevisionConnectionResult result = await _televisionWake.CheckConnectionAsync(requestedAddress);
            if (addressBox.Text != requestedAddress || macBox.Text != previousMac) return;
            if (result.MacAddress is not null && string.IsNullOrWhiteSpace(macBox.Text))
                macBox.Text = result.MacAddress;
            StatusText.Text = result.Reachable
                ? result.MacAddress is not null
                    ? ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_325")
                    : ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_326")
                : ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_327");
        }
        catch (Exception error) { StatusText.Text = error.Message; }
        finally { button.IsEnabled = true; }
    }

    private async void PairTv_Click(object sender, RoutedEventArgs args)
    {
        if (_pairingCancellation is not null || _certificateCancellation is not null || _hisenseCancellation is not null || _lgInputsCancellation is not null) return;
        PairTvButton.IsEnabled = false;
        using CancellationTokenSource cancellation = new();
        try
        {
            CommitSettings(ReadSelections());
            TelevisionNetworkSettings.RequireLg(_settings);
            TelevisionPairingTarget target = TelevisionPairingTarget.From(_settings);
            AppSettings pairingSettings = _settings.Copy();
            _pairingCancellation = cancellation;
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_328");
            string key = await _webOs.PairAsync(pairingSettings, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            TelevisionPairingTarget editedTarget = ReadTelevisionTarget();
            if (editedTarget != target)
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_329"));
            CommitSettings(target.ApplyKey(_settings, key));
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_330");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_331"); }
        catch (Exception error)
        {
            ErrorLog.Write("television.pair", error);
            StatusText.Text = ControllerHelper.Localization.Text.Format("SettingsWindow_xaml_332", error.Message);
        }
        finally { _pairingCancellation = null; PairTvButton.IsEnabled = true; }
    }

    private TelevisionPairingTarget ReadTelevisionTarget() => TelevisionPairingTarget.Create(
        (TelevisionBox.SelectedItem as DisplayDevice)?.Id, TelevisionIpBox.Text, TelevisionMacBox.Text,
        AllowInsecureWebOsBox.IsChecked == true, _settings.TelevisionCertificateSha256, SelectedNetworkProvider);

    private void UpdateCertificateStatus()
    {
        CertificateStatusText.Text = _settings.AllowInsecureWebOs
            ? ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_333")
            : WebOsSecurity.HasFingerprint(_settings.TelevisionCertificateSha256)
                ? ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_334") + _settings.TelevisionCertificateSha256
                : ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_335");
    }

    private async void CheckCertificate_Click(object sender, RoutedEventArgs args)
    {
        if (_certificateCancellation is not null || _pairingCancellation is not null || _hisenseCancellation is not null || _lgInputsCancellation is not null) return;
        CheckCertificateButton.IsEnabled = PairTvButton.IsEnabled = false;
        using CancellationTokenSource cancellation = new();
        try
        {
            if (AllowInsecureWebOsBox.IsChecked == true)
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_336"));
            CommitSettings(ReadSelections());
            TelevisionNetworkSettings.RequireLg(_settings);
            TelevisionPairingTarget target = TelevisionPairingTarget.From(_settings);
            string? address = _settings.TelevisionIpAddress;
            _certificateCancellation = cancellation;
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_337");
            TelevisionCertificate certificate = await TelevisionCertificateProbe.ReadAsync(address, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (ReadTelevisionTarget() != target || TelevisionPairingTarget.From(_settings) != target)
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_338"));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now < certificate.NotBefore || now > certificate.NotAfter)
                throw new TelevisionCertificateException(ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_339"));
            bool changed = WebOsSecurity.HasFingerprint(target.Certificate) && target.Certificate != certificate.Sha256;
            ContentDialog dialog = new()
            {
                XamlRoot = ((FrameworkElement)Content).XamlRoot,
                Title = changed ? ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_340") : ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_341"),
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Text = ControllerHelper.Localization.Text.Format("SettingsWindow_xaml_342", address, certificate.Sha256, certificate.NotAfter.LocalDateTime) +
                        (changed ? ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_343") : "") +
                        ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_344"),
                    MaxWidth = 380
                },
                PrimaryButtonText = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_345"),
                CloseButtonText = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_263"),
                DefaultButton = ContentDialogButton.Close
            };
            using (cancellation.Token.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide())))
            {
                ContentDialogResult result = await dialog.ShowAsync();
                cancellation.Token.ThrowIfCancellationRequested();
                if (result != ContentDialogResult.Primary) { StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_346"); return; }
            }
            if (ReadTelevisionTarget() != target)
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_347"));
            // The dialog confirmed this exact snapshot; merge into the latest settings only now.
            CommitSettings(WebOsSecurity.ApplyCertificate(_settings, target, certificate.Sha256));
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_348");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_349"); }
        catch (Exception error)
        {
            ErrorLog.Write("television.certificate-check", error);
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_350") + error.Message;
        }
        finally { _certificateCancellation = null; CheckCertificateButton.IsEnabled = PairTvButton.IsEnabled = true; }
    }

    private void CaptureTv_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (_switching || _profileOperations.IsStopping)
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_251"));
            AppSettings candidate = ReadSelections();
            _displays.CaptureTelevisionLayout(candidate);
            CommitSettings(candidate);
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_351");
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private void Capture_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            AppSettings candidate = ReadSelections();
            _displays.CapturePcLayout(candidate);
            CommitSettings(candidate);
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_xaml_352");
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private void Refresh_Click(object sender, RoutedEventArgs args) => RefreshDevices();

    private void PcNav_Click(object sender, RoutedEventArgs args) => ShowPage(0);
    private void TvNav_Click(object sender, RoutedEventArgs args) => ShowPage(1);
    private void ControllerNav_Click(object sender, RoutedEventArgs args) => ShowPage(2);

    private void ShowPage(int page)
    {
        PcPanel.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        TvPanel.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        ControllerPanel.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        Button[] buttons = [PcNav, TvNav, ControllerNav];
        PathIcon[] icons = [PcNavIcon, TvNavIcon, ControllerNavIcon];
        TextBlock[] labels = [PcNavText, TvNavText, ControllerNavText];
        for (int index = 0; index < buttons.Length; index++)
        {
            bool selected = index == page;
            buttons[index].Background = new SolidColorBrush(selected
                ? Windows.UI.Color.FromArgb(255, 52, 48, 68)
                : Windows.UI.Color.FromArgb(0, 0, 0, 0));
            SolidColorBrush foreground = new(selected
                ? Windows.UI.Color.FromArgb(255, 184, 166, 255)
                : Windows.UI.Color.FromArgb(255, 168, 173, 189));
            icons[index].Foreground = foreground;
            labels[index].Foreground = foreground;
        }
    }

    private void ResizeForCurrentDpi()
    {
        uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
        double scale = dpi == 0 ? 1 : dpi / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Round(440 * scale), (int)Math.Round(740 * scale)));
        StartupTrace.Write($"Settings window DPI: {dpi}, scale: {scale:F2}");
    }

    private async void Tv_Click(object sender, RoutedEventArgs args)
    {
        try { CommitSettings(ReadSelections()); await SwitchToTvAsync(); }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private async void Pc_Click(object sender, RoutedEventArgs args)
    {
        try { await SwitchToPcAsync(); }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
