namespace ControllerHelper.Services;

public sealed record AudioEndpoint(string Id, string Name, uint State)
{
    public bool IsActive => State == 1;
    public override string ToString() => State switch
    {
        1 => Name,
        2 => ControllerHelper.Localization.Text.Format("AudioEndpoint_032", Name),
        4 => ControllerHelper.Localization.Text.Format("AudioEndpoint_033", Name),
        8 => ControllerHelper.Localization.Text.Format("AudioEndpoint_034", Name),
        0 => ControllerHelper.Localization.Text.Format("AudioEndpoint_035", Name),
        _ => ControllerHelper.Localization.Text.Format("AudioEndpoint_036", Name, State)
    };
}

internal static class AudioEndpointChoices
{
    internal static AudioEndpoint[] Create(IEnumerable<AudioEndpoint> devices, string? selectedId)
    {
        AudioEndpoint[] all = devices.ToArray();
        // A disconnected HDMI endpoint must remain selectable before enabling its display.
        // Only absent historical endpoints are hidden, unless they are the user's selection.
        List<AudioEndpoint> choices = all.Where(device => device.State is 1 or 2 or 8 ||
            string.Equals(device.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(device => device.IsActive).ThenBy(device => device.Name).ToList();
        if (!string.IsNullOrWhiteSpace(selectedId) && !choices.Any(device =>
            string.Equals(device.Id, selectedId, StringComparison.OrdinalIgnoreCase)))
            choices.Add(new AudioEndpoint(selectedId, ControllerHelper.Localization.Text.Get("AudioEndpoint_037"), 0));
        return choices.ToArray();
    }
}
