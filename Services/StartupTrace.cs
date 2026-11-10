using System.Diagnostics;

namespace ControllerHelper.Services;

internal static class StartupTrace
{
    [Conditional("CONTROLLERHELPER_DIAGNOSTICS")]
    internal static void Write(string message)
    {
        string? path = Environment.GetEnvironmentVariable("CONTROLLERHELPER_TRACE");
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch (IOException) { }
    }
}
