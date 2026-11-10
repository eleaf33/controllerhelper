namespace ControllerHelper.Services;

internal sealed record FlydigiWindowCandidate(nint Window, uint ProcessId, bool Visible, bool Owned,
    bool PlacementRead, ScreenBounds Bounds, string WindowClass, string Title, long Style, long ExtendedStyle,
    bool MetadataRead = true)
{
    internal bool TitleMatches => FlydigiWindowSelection.IsMainTitle(Title);
}

internal static class FlydigiWindowSelection
{
    internal static bool IsMainTitle(string title) =>
        title.Contains("Flydigi", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("飞智空间站", StringComparison.Ordinal) ||
        title.Contains("飛智空間站", StringComparison.Ordinal) ||
        title.Contains("스페이스 스테이션", StringComparison.Ordinal);

    internal static string? RejectionReason(FlydigiWindowCandidate window)
    {
        if (window.Window == 0 || window.ProcessId == 0) return "invalid-window";
        if (window.Owned) return "owned-window";
        if (!window.PlacementRead) return "placement-unavailable";
        if (!window.MetadataRead) return "metadata-unavailable";
        if ((window.Style & 0x40000000L) != 0) return "child-window";
        if ((window.ExtendedStyle & 0x80L) != 0) return "tool-window";
        if ((window.ExtendedStyle & 0x08000000L) != 0) return "no-activate-window";
        // Electron creates an extra hidden host window which can be larger than
        // its real BrowserWindow. It must never win by area (seen in user logs).
        if (window.WindowClass == "Chrome_WidgetWin_0" ||
            window.WindowClass == "Electron_NotifyIconHostWindow" ||
            window.WindowClass == "Base_PowerMessageWindow" ||
            window.WindowClass.StartsWith("crashpad_", StringComparison.Ordinal) ||
            window.WindowClass is "IME" or "MSCTFIME UI") return "helper-window";
        if (window.Title.Contains("DevTools", StringComparison.OrdinalIgnoreCase) ||
            window.Title.Contains("Developer Tools", StringComparison.OrdinalIgnoreCase)) return "developer-tools";
        if (window.Bounds.Width < 400 || window.Bounds.Height < 300) return "too-small";
        // Visible windows retain the old fallback; hidden windows need a positive
        // product identity so that an arbitrary background window is not exposed.
        if (!window.Visible && !window.TitleMatches) return "unidentified-hidden-window";
        return null;
    }

    internal static FlydigiWindowCandidate? Select(IEnumerable<FlydigiWindowCandidate> windows) => windows
        .Where(window => RejectionReason(window) is null)
        .OrderByDescending(window => window.TitleMatches)
        .ThenByDescending(window => window.Visible)
        .ThenByDescending(window => window.WindowClass == "Chrome_WidgetWin_1")
        .ThenByDescending(window => (long)window.Bounds.Width * window.Bounds.Height)
        .ThenBy(window => window.Window.ToInt64())
        .FirstOrDefault();
}
