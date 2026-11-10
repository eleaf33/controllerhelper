namespace ControllerHelper.Models;

// Values reported by EnumDisplaySettingsEx; 59 and 60 Hz remain separate choices.
public sealed record TelevisionVideoMode(int Width, int Height, int Frequency, bool Interlaced = false)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string RefreshLabel => ControllerHelper.Localization.Text.Format("TelevisionVideoMode_015", Frequency, (Interlaced ? ControllerHelper.Localization.Text.Get("TelevisionVideoMode_014") : ""));
    public override string ToString() => $"{Width} × {Height}, {RefreshLabel}";

    internal void Validate()
    {
        if (Width is < 320 or > 32768 || Height is < 200 or > 32768 || Frequency is < 1 or > 2000)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("TelevisionVideoMode_016"));
    }
}
