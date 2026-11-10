namespace ControllerHelper.Services;

public sealed record AudioEndpoint(string Id, string Name, uint State)
{
    public bool IsActive => State == 1;
    public override string ToString() => State switch
    {
        1 => Name,
        2 => $"{Name} (отключено в Windows)",
        4 => $"{Name} (устройство отсутствует)",
        8 => $"{Name} (не подключено)",
        0 => $"{Name} (сохранённый выход не найден)",
        _ => $"{Name} (состояние неизвестно: 0x{State:X})"
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
            choices.Add(new AudioEndpoint(selectedId, "Ранее выбранное устройство", 0));
        return choices.ToArray();
    }
}
