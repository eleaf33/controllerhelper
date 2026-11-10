namespace ControllerHelper.Services;

public static class ControllerSelection
{
    public static ControllerDevice? Resolve(IReadOnlyList<ControllerDevice> devices,
        string? deviceId, string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(deviceId) && string.IsNullOrWhiteSpace(fingerprint)) return null;
        foreach (ControllerDevice device in devices)
            if (device.Id == deviceId && !string.IsNullOrWhiteSpace(deviceId)) return device;

        if (string.IsNullOrWhiteSpace(fingerprint)) return null;
        ControllerDevice? match = null;
        foreach (ControllerDevice device in devices)
        {
            if (device.Fingerprint != fingerprint) continue;
            if (match is not null) return null;
            match = device;
        }
        return match;
    }
}
