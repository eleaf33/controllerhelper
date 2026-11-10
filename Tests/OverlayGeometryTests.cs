using ControllerHelper.Services;

internal static class OverlayGeometryTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Flydigi: область на ТВ 1080p при DPI 130 оставляет место ползунку", Television1080),
        ("Flydigi: 4K при разных DPI остаётся в области клиента", FourKScales),
        ("Flydigi: дробные границы округляются внутрь на левом мониторе", FractionalNegativeOrigin),
        ("Flydigi: устаревший размер XAML ограничивается размером окна", StaleLayout),
        ("Flydigi: малое окно не растягивается", PreserveNativeSize),
        ("Flydigi: выросшее после смены DPI окно уменьшается до области", DpiResize),
        ("Flydigi: пустая область и невалидный масштаб отклоняются", InvalidBounds)
    ];

    private static Task Television1080()
    {
        ScreenBounds client = new(-4480, 0, 1920, 1080);
        // Measured logical header/footer positions for a constrained panel;
        // exercise the user's custom DPI rather than a standard 125/150% scale.
        ScreenBounds viewport = OverlayGeometry.ToScreen(client, 130.0 / 96, 26, 80, 1360, 640);
        ScreenBounds slider = OverlayGeometry.ToScreen(client, 130.0 / 96, 220, 742, 1100, 32);
        ScreenBounds window = OverlayGeometry.Fit(viewport, 1810, 1100);
        Check(OverlayGeometry.Contains(client, slider), "Slider extends past the physical screen.");
        Check(OverlayGeometry.Contains(viewport, window), "Flydigi exceeds its viewport.");
        Check(window.Y + window.Height < slider.Y, "Flydigi covers the slider.");
        return Task.CompletedTask;
    }

    private static Task FourKScales()
    {
        ScreenBounds client = new(2560, -400, 3840, 2160);
        foreach (double scale in new[] { 1.0, 130.0 / 96, 1.5, 2.0, 3.0 })
        {
            ScreenBounds viewport = OverlayGeometry.ToScreen(client, scale, 26, 80,
                client.Width / scale - 52, client.Height / scale - 180);
            ScreenBounds window = OverlayGeometry.Fit(viewport, 1810, 1100);
            Check(OverlayGeometry.Contains(client, viewport) && OverlayGeometry.Contains(viewport, window),
                $"Window escaped at scale {scale}.");
            Check(window.Y + window.Height <= client.Y + client.Height - 100 * scale,
                "Reserved footer area was consumed.");
        }
        return Task.CompletedTask;
    }

    private static Task FractionalNegativeOrigin()
    {
        ScreenBounds result = OverlayGeometry.ToScreen(new(-1920, -1080, 1920, 1080),
            2, 10.25, 20.25, 100.5, 80.5);
        Check(result == new ScreenBounds(-1899, -1039, 200, 160), "Incorrect screen origin or inward rounding.");
        return Task.CompletedTask;
    }

    private static Task StaleLayout()
    {
        ScreenBounds client = new(-4480, 0, 1920, 1080);
        ScreenBounds result = OverlayGeometry.ToScreen(client, 2, 0, 0, 3840, 2160);
        Check(result == client, "Stale layout escaped the actual client rectangle.");
        return Task.CompletedTask;
    }

    private static Task PreserveNativeSize()
    {
        ScreenBounds result = OverlayGeometry.Fit(new(100, 200, 1800, 1200), 1000, 600);
        Check(result == new ScreenBounds(500, 500, 1000, 600), "Small window was stretched or not centered.");
        return Task.CompletedTask;
    }

    private static Task DpiResize()
    {
        ScreenBounds viewport = new(-4400, 100, 1760, 850);
        Check(!OverlayGeometry.Contains(viewport, new(-4400, 100, 1810, 1100)),
            "DPI-expanded window was incorrectly accepted.");
        ScreenBounds corrected = OverlayGeometry.Fit(viewport, 1810, 1100);
        Check(corrected == viewport, "Overflow was not constrained on both axes.");
        return Task.CompletedTask;
    }

    private static Task InvalidBounds()
    {
        ScreenBounds client = new(0, 0, 1920, 1080);
        Check(OverlayGeometry.ToScreen(client, double.NaN, 0, 0, 100, 100) == default, "NaN DPI was accepted.");
        Check(OverlayGeometry.ToScreen(client, 1, 0, 0, 0, 100) == default, "Empty layout was accepted.");
        Check(!OverlayGeometry.Contains(client, default), "Zero window was treated as visible.");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
