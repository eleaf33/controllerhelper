using ControllerHelper.Models;
using ControllerHelper.Services;
using Microsoft.UI.Xaml;

namespace ControllerHelper;

public sealed partial class SettingsWindow
{
    private CancellationTokenSource? _lgInputsCancellation;

    private void InitializeLgInputControls()
    {
        SetLgInputs([]);
        Closed += (_, _) => _lgInputsCancellation?.Cancel();
        TelevisionIpBox.TextChanged += (_, _) => _lgInputsCancellation?.Cancel();
        TelevisionMacBox.TextChanged += (_, _) => _lgInputsCancellation?.Cancel();
        TvNetworkProviderBox.SelectionChanged += (_, _) => _lgInputsCancellation?.Cancel();
        TelevisionBox.SelectionChanged += (_, _) => _lgInputsCancellation?.Cancel();
        AllowInsecureWebOsBox.Checked += (_, _) => _lgInputsCancellation?.Cancel();
        AllowInsecureWebOsBox.Unchecked += (_, _) => _lgInputsCancellation?.Cancel();
    }

    private void SetLgInputs(IReadOnlyList<LgInput> inputs)
    {
        string? selected = (LgInputBox.SelectedItem as LgInput)?.Id ?? _settings.LgInputId;
        List<LgInput> choices = [new("", ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_248")), .. inputs];
        if (!string.IsNullOrEmpty(selected) && choices.All(input => input.Id != selected))
            choices.Add(new(selected, (_settings.LgInputName ?? ControllerHelper.Localization.Text.Get("SettingsWindow_Hisense_249")) + ControllerHelper.Localization.Text.Get("SettingsWindow_LgInputs_270")));
        LgInputBox.ItemsSource = choices;
        LgInputBox.SelectedItem = choices.FirstOrDefault(input => input.Id == selected) ?? choices[0];
    }

    private async void RefreshLgInputs_Click(object sender, RoutedEventArgs args)
    {
        if (_lgInputsCancellation is not null || _pairingCancellation is not null || _certificateCancellation is not null || _hisenseCancellation is not null) return;
        using IDisposable operation = ErrorLog.BeginOperation("lg.inputs-refresh");
        using CancellationTokenSource cancellation = new();
        try
        {
            CommitSettings(ReadSelections());
            TelevisionNetworkSettings.RequireLg(_settings);
            AppSettings snapshot = _settings.Copy();
            var target = TelevisionPairingTarget.From(snapshot);
            _lgInputsCancellation = cancellation;
            RefreshLgInputsButton.IsEnabled = false;
            CancelLgInputsButton.IsEnabled = true;
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_LgInputs_271");
            var inputs = await _webOs.GetInputsAsync(snapshot, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            LgInputSettings.EnsureCurrent(target, snapshot.TelevisionClientKey, _settings);
            if (ReadTelevisionTarget() != target)
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("SettingsWindow_LgInputs_272"));
            SetLgInputs(inputs);
            StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_LgInputs_273");
        }
        catch (OperationCanceledException) { ErrorLog.Event("lg.inputs-refresh.canceled"); StatusText.Text = ControllerHelper.Localization.Text.Get("SettingsWindow_LgInputs_274"); }
        catch (Exception error) { ErrorLog.Write("lg.inputs-refresh", error); StatusText.Text = "LG: " + error.Message; }
        finally
        {
            _lgInputsCancellation = null;
            RefreshLgInputsButton.IsEnabled = true;
            CancelLgInputsButton.IsEnabled = false;
        }
    }

    private void CancelLgInputs_Click(object sender, RoutedEventArgs args) => _lgInputsCancellation?.Cancel();
}
