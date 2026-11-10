namespace ControllerHelper.Services;

internal readonly record struct ScreenBounds(int X, int Y, int Width, int Height);

internal static class OverlayGeometry
{
    // Round inward so the foreign window cannot cover the adjacent controls.
    internal static ScreenBounds ToScreen(ScreenBounds client, double scale,
        double x, double y, double width, double height)
    {
        if (!double.IsFinite(scale) || scale <= 0 ||
            !double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 ||
            client.Width <= 0 || client.Height <= 0) return default;
        int left = (int)Math.Clamp(Math.Ceiling(x * scale), 0, client.Width);
        int top = (int)Math.Clamp(Math.Ceiling(y * scale), 0, client.Height);
        int right = (int)Math.Clamp(Math.Floor((x + width) * scale), left, client.Width);
        int bottom = (int)Math.Clamp(Math.Floor((y + height) * scale), top, client.Height);
        return new(client.X + left, client.Y + top, right - left, bottom - top);
    }

    internal static ScreenBounds Fit(ScreenBounds viewport, int width, int height)
    {
        width = Math.Clamp(width, 0, Math.Max(0, viewport.Width));
        height = Math.Clamp(height, 0, Math.Max(0, viewport.Height));
        return new(viewport.X + (viewport.Width - width) / 2,
            viewport.Y + (viewport.Height - height) / 2, width, height);
    }

    internal static bool Contains(ScreenBounds viewport, ScreenBounds window) =>
        window.Width > 0 && window.Height > 0 && window.X >= viewport.X && window.Y >= viewport.Y &&
        (long)window.X + window.Width <= (long)viewport.X + viewport.Width &&
        (long)window.Y + window.Height <= (long)viewport.Y + viewport.Height;
}
