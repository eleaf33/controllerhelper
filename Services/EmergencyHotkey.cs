using System.Diagnostics;

namespace ControllerHelper.Services;

internal sealed class EmergencyHotkey
{
    private bool _requireNeutral = true, _latched;
    private long? _started;
    internal void Reset() { _requireNeutral = true; _latched = false; _started = null; }

    internal bool Update(bool valid, bool neutral, bool chord, long timestamp)
    {
        if (!valid) { Reset(); return false; }
        if (_requireNeutral)
        {
            if (neutral) _requireNeutral = false;
            return false;
        }
        if (neutral) { _latched = false; _started = null; return false; }
        if (_latched) return false;
        if (!chord) { _started = null; return false; }
        _started ??= timestamp;
        if (Stopwatch.GetElapsedTime(_started.Value, timestamp) < TimeSpan.FromSeconds(2)) return false;
        _latched = true;
        return true;
    }

    internal static void Validate(bool enabled, string modifier, string trigger, string overlayModifier, string overlayTrigger)
    {
        if (!enabled) return;
        if (string.IsNullOrWhiteSpace(modifier) || string.IsNullOrWhiteSpace(trigger) || modifier == trigger)
            throw new ArgumentException(ControllerHelper.Localization.Text.Get("EmergencyHotkey_083"));
        if ((modifier == overlayModifier && trigger == overlayTrigger) ||
            (modifier == overlayTrigger && trigger == overlayModifier))
            throw new ArgumentException(ControllerHelper.Localization.Text.Get("EmergencyHotkey_084"));
    }
}
