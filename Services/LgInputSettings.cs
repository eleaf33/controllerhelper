using ControllerHelper.Models;

namespace ControllerHelper.Services;

internal static class LgInputSettings
{
    internal static bool SameDevice(AppSettings first, AppSettings second)
    {
        var a = TelevisionPairingTarget.From(first);
        var b = TelevisionPairingTarget.From(second);
        return a.DisplayId == b.DisplayId && a.Address == b.Address && a.Mac == b.Mac;
    }

    internal static void Reconcile(AppSettings previous, AppSettings candidate)
    {
        if (SameDevice(previous, candidate)) return;
        candidate.LgInputId = candidate.LgInputName = null;
    }

    internal static void EnsureCurrent(TelevisionPairingTarget expected, string? key, AppSettings current)
    {
        if (expected != TelevisionPairingTarget.From(current) || key != current.TelevisionClientKey)
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("LgInputSettings_144"));
    }
}
