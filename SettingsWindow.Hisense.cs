using ControllerHelper.Models;
using ControllerHelper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ControllerHelper;

public sealed partial class SettingsWindow
{
    private readonly HisenseTelevisionService _hisenseService = new();
    private CancellationTokenSource? _hisenseCancellation;
    private bool _hisenseCommitting;

    private void InitializeHisenseControls()
    {
        SetHisenseInputs([]);
        UpdateHisenseStatus();
        Closed += (_, _) => _hisenseCancellation?.Cancel();
        HisenseIpBox.TextChanged += (_, _) => _hisenseCancellation?.Cancel();
        HisenseMacBox.TextChanged += (_, _) => _hisenseCancellation?.Cancel();
        TvNetworkProviderBox.SelectionChanged += (_, _) => _hisenseCancellation?.Cancel();
        TelevisionBox.SelectionChanged += (_, _) => _hisenseCancellation?.Cancel();
    }

    private void UpdateHisenseStatus()
    {
        HisensePairStatusText.Text = _settings.HisensePaired
            ? ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_246")
            : ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_247");
    }

    private void SetHisenseInputs(IReadOnlyList<HisenseInput> inputs)
    {
        string? selected = (HisenseInputBox.SelectedItem as HisenseInput)?.Id ?? _settings.HisenseInputId;
        List<HisenseInput> choices = [new("", ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_248")), .. inputs];
        if (!string.IsNullOrEmpty(selected) && !choices.Any(i => i.Id == selected))
            choices.Add(new(selected, (_settings.HisenseInputName ?? ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_249")) + ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_250")));
        HisenseInputBox.ItemsSource = choices;
        HisenseInputBox.SelectedItem = choices.FirstOrDefault(i => i.Id == selected) ?? choices[0];
    }

    private void CommitHisenseSettings(AppSettings settings)
    {
        _hisenseCommitting = true;
        try { CommitSettings(settings); }
        finally { _hisenseCommitting = false; }
    }

    private async Task RunHisenseSetupAsync(string name, Func<AppSettings, CancellationToken, Task> action)
    {
        if (_hisenseCancellation is not null || _pairingCancellation is not null || _certificateCancellation is not null || _lgInputsCancellation is not null) return;
        using IDisposable operation = ErrorLog.BeginOperation(name);
        using CancellationTokenSource cancellation = new();
        try
        {
            if (_switching || _profileOperations.IsStopping)
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_251"));
            CommitSettings(ReadSelections());
            if (_settings.TelevisionNetworkProvider != TelevisionNetworkProvider.Hisense)
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_252"));
            TelevisionWakeService.ParseAddress(_settings.HisenseIpAddress);
            _hisenseCancellation = cancellation;
            PairHisenseButton.IsEnabled = RefreshHisenseInputsButton.IsEnabled = TestHisenseWakeButton.IsEnabled = false;
            CancelHisenseButton.IsEnabled = true;
            await action(_settings.Copy(), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            ErrorLog.Event(name + ".ok");
        }
        catch (OperationCanceledException)
        {
            ErrorLog.Event(name + ".canceled");
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_253");
        }
        catch (Exception error)
        {
            ErrorLog.Write(name, error);
            StatusText.Text = "Hisense: " + error.Message;
        }
        finally
        {
            _hisenseCancellation = null;
            PairHisenseButton.IsEnabled = RefreshHisenseInputsButton.IsEnabled = TestHisenseWakeButton.IsEnabled = true;
            CancelHisenseButton.IsEnabled = false;
        }
    }

    private async void TestHisenseWake_Click(object sender, RoutedEventArgs args) =>
        await RunHisenseSetupAsync("hisense.wake-test", async (settings, token) =>
        {
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_254");
            await _televisionWake.SendWakeAsync(settings, token);
            if (!string.IsNullOrWhiteSpace(settings.TelevisionId))
            {
                StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_255");
                await Task.Run(() => new TelevisionReadiness().WaitUntilReady(() => _displays.IsTelevisionAvailable(settings.TelevisionId), token), token);
                StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_256");
            }
            else StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_257");
        });

    private void CancelHisense_Click(object sender, RoutedEventArgs args) => _hisenseCancellation?.Cancel();

    private async void PairHisense_Click(object sender, RoutedEventArgs args) =>
        await RunHisenseSetupAsync("hisense.pair", async (settings, token) =>
        {
            if (string.IsNullOrEmpty(settings.HisenseClientId))
            {
                settings.HisenseClientId = "ControllerHelper_" + Guid.NewGuid().ToString("N");
                settings.HisensePaired = false;
                CommitHisenseSettings(settings);
            }
            HisenseTarget expected = HisenseTarget.From(settings);
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_258");
            TelevisionCertificate certificate = await HisenseSession.Stage("tls.probe", ct =>
                TelevisionCertificateProbe.ReadEndpointAsync(TelevisionWakeService.ParseAddress(settings.HisenseIpAddress).ToString(), HisenseMqttTransport.Port, ct), token);
            token.ThrowIfCancellationRequested();
            expected.EnsureCurrent(_settings);
            if (DateTimeOffset.UtcNow < certificate.NotBefore || DateTimeOffset.UtcNow > certificate.NotAfter)
                throw new TelevisionCertificateException(ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_259"));
            if (certificate.Sha256 != WebOsSecurity.NormalizeFingerprint(settings.HisenseCertificateSha256))
            {
                ContentDialog trust = new()
                {
                    XamlRoot = ((FrameworkElement)Content).XamlRoot,
                    Title = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_260"),
                    Content = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                        Text = ControllerHelper.Localization.Text.Format("SettingsWindow_Hisense_261", settings.HisenseIpAddress, certificate.Sha256), MaxWidth = 380 },
                    PrimaryButtonText = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_262"), CloseButtonText = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_263"), DefaultButton = ContentDialogButton.Close
                };
                if (await ShowHisenseDialog(trust, token) != ContentDialogResult.Primary) throw new OperationCanceledException(token);
                expected.EnsureCurrent(_settings);
                AppSettings updated = _settings.Copy();
                updated.HisenseCertificateSha256 = certificate.Sha256;
                updated.HisensePaired = false;
                HisenseSettings.ClearAuthentication(updated);
                CommitHisenseSettings(updated);
                settings = updated.Copy();
                expected = HisenseTarget.From(settings);
            }
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_264");
            using HisenseSession session = _hisenseService.CreateSession(settings);
            await session.ConnectAsync(settings, token);
            var inputs = await session.PairAsync(async ct =>
            {
                PasswordBox pin = new() { MaxLength = 4, Header = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_265") };
                ContentDialog dialog = new()
                {
                    XamlRoot = ((FrameworkElement)Content).XamlRoot, Title = "PIN Hisense", Content = pin,
                    PrimaryButtonText = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_266"), CloseButtonText = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_263"), DefaultButton = ContentDialogButton.Primary
                };
                return await ShowHisenseDialog(dialog, ct) == ContentDialogResult.Primary ? pin.Password : null;
            }, token);
            token.ThrowIfCancellationRequested();
            expected.EnsureCurrent(_settings);
            AppSettings paired = _settings.Copy();
            paired.HisenseAuthentication = session.Authentication;
            paired.HisenseAuthenticationProtected = null;
            session.ApplyConnectionSettings(paired);
            paired.HisensePaired = true;
            CommitHisenseSettings(paired);
            SetHisenseInputs(inputs);
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_267");
        });

    private async void RefreshHisenseInputs_Click(object sender, RoutedEventArgs args) =>
        await RunHisenseSetupAsync("hisense.inputs-refresh", async (settings, token) =>
        {
            HisenseSettings.Validate(settings);
            HisenseTarget expected = HisenseTarget.From(settings);
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_268");
            using HisenseSession session = _hisenseService.CreateSession(settings);
            await session.ConnectAsync(settings, token);
            var inputs = await session.GetInputsAsync(token);
            token.ThrowIfCancellationRequested();
            expected.EnsureCurrent(_settings);
            var updated = _settings.Copy();
            session.ApplyConnectionSettings(updated);
            CommitHisenseSettings(updated);
            SetHisenseInputs(inputs);
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_269");
        });

    private async Task<ContentDialogResult> ShowHisenseDialog(ContentDialog dialog, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var registration = cancellation.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
        ContentDialogResult result = await dialog.ShowAsync();
        cancellation.ThrowIfCancellationRequested();
        return result;
    }
}
