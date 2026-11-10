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
        List<LgInput> choices = [new("", "Не переключать вход"), .. inputs];
        if (!string.IsNullOrEmpty(selected) && choices.All(input => input.Id != selected))
            choices.Add(new(selected, (_settings.LgInputName ?? "Сохранённый вход") + " — обновите список"));
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
            StatusText.Text = "Получение входов LG… Телевизор должен быть включён и сопряжён.";
            var inputs = await _webOs.GetInputsAsync(snapshot, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            LgInputSettings.EnsureCurrent(target, snapshot.TelevisionClientKey, _settings);
            if (ReadTelevisionTarget() != target)
                throw new InvalidOperationException("Параметры LG в форме изменились. Обновите список входов заново.");
            SetLgInputs(inputs);
            StatusText.Text = "Входы получены от LG. Выберите HDMI компьютера и нажмите «Сохранить настройки».";
        }
        catch (OperationCanceledException) { ErrorLog.Event("lg.inputs-refresh.canceled"); StatusText.Text = "Обновление входов LG отменено."; }
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
