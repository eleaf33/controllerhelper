using ControllerHelper.Models;

namespace ControllerHelper.Services;

public sealed record GamepadButtonChoice(string Key, string Label)
{
    public override string ToString() => Label;
}

public sealed class ControllerButtonCatalog
{
    private readonly List<GamepadButtonChoice> _choices =
    [
        new("A", "A"), new("B", "B"), new("X", "X"), new("Y", "Y"),
        new("View", "View / Select"), new("Menu", "Menu / Start"),
        new("LeftShoulder", "LB"), new("RightShoulder", "RB"),
        new("DPadUp", ControllerHelper.Localization.Text.Get("ControllerButtonCatalog_045")), new("DPadDown", ControllerHelper.Localization.Text.Get("ControllerButtonCatalog_046")),
        new("DPadLeft", ControllerHelper.Localization.Text.Get("ControllerButtonCatalog_047")), new("DPadRight", ControllerHelper.Localization.Text.Get("ControllerButtonCatalog_048")),
        new("LeftThumbstick", ControllerHelper.Localization.Text.Get("ControllerButtonCatalog_049")), new("RightThumbstick", ControllerHelper.Localization.Text.Get("ControllerButtonCatalog_050"))
    ];

    public IReadOnlyList<GamepadButtonChoice> Choices => _choices;

    public bool Normalize(AppSettings settings)
    {
        bool corrected = false;
        if (!IsSupported(settings.HotkeyModifier))
        {
            settings.HotkeyModifier = "A";
            corrected = true;
        }
        if (!IsSupported(settings.HotkeyTrigger) || settings.HotkeyModifier == settings.HotkeyTrigger)
        {
            settings.HotkeyTrigger = settings.HotkeyModifier == "View" ? "A" : "View";
            corrected = true;
        }
        if (!IsSupported(settings.EmergencyReturnModifier) || !IsSupported(settings.EmergencyReturnTrigger) ||
            settings.EmergencyReturnModifier == settings.EmergencyReturnTrigger)
        {
            settings.EmergencyReturnEnabled = false;
            settings.EmergencyReturnModifier = "LeftShoulder";
            settings.EmergencyReturnTrigger = "RightShoulder";
            corrected = true;
        }
        try { EmergencyHotkey.Validate(settings.EmergencyReturnEnabled, settings.EmergencyReturnModifier,
            settings.EmergencyReturnTrigger, settings.HotkeyModifier, settings.HotkeyTrigger); }
        catch (ArgumentException) { settings.EmergencyReturnEnabled = false; corrected = true; }
        return corrected;
    }

    public void Ensure(string key)
    {
        if (_choices.Any(choice => choice.Key == key)) return;
        string label = key.StartsWith("Button:", StringComparison.Ordinal)
            ? ControllerHelper.Localization.Text.Format("ControllerButtonCatalog_051", key[7..]) : key;
        _choices.Add(new GamepadButtonChoice(key, label));
    }

    public void EnsureRawButtons(int count)
    {
        for (int index = 0; index < count; index++) Ensure($"Button:{index}");
    }

    private bool IsSupported(string? key) =>
        _choices.Any(choice => choice.Key == key) ||
        key is not null && key.StartsWith("Button:", StringComparison.Ordinal) &&
        int.TryParse(key.AsSpan(7), out int index) && index is >= 0 and < 256;
}
