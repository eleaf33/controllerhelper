using ControllerHelper.Services;

internal static class FlydigiWindowSelectionTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("Flydigi: скрытое китайское окно из диагностики выбрано вместо большого helper", () => Run(HiddenFromDiagnostic)),
        ("Flydigi: названия поддерживаемых локализаций распознаются", () => Run(LocalizedTitles)),
        ("Flydigi: видимое окно неизвестной локализации остаётся доступным", () => Run(VisibleFallback)),
        ("Flydigi: служебные/дочерние/невосстанавливаемые окна исключаются", () => Run(RejectHelpers)),
        ("Flydigi: главное видимое окно предпочитается скрытому и размер не важнее имени", () => Run(Priority)),
        ("Flydigi: неизвестное скрытое окно не показывается наугад", () => Run(NoCandidate))
    ];

    private static FlydigiWindowCandidate MainWindow(nint hwnd = 1) => new(hwnd, 42, false, false, true,
        new(715, 509, 1810, 1287), "Chrome_WidgetWin_1", "飞智空间站", 0x04C70000, 0x100);

    private static void HiddenFromDiagnostic()
    {
        var main = MainWindow();
        var helper = main with { Window = 2, WindowClass = "Chrome_WidgetWin_0", Title = "", Bounds = new(408, 408, 2880, 1544), Style = 0x06CF0000 };
        var tray = main with { Window = 3, WindowClass = "Electron_NotifyIconHostWindow", Title = "", Bounds = new(0, 0, 0, 0) };
        var crash = main with { Window = 4, ProcessId = 43, WindowClass = "crashpad_SessionEndWatcher", Title = "", Bounds = new(0, 0, 178, 49) };
        var ime = main with { Window = 5, Owned = true, WindowClass = "IME", Title = "Default IME" };
        Check(FlydigiWindowSelection.Select([helper, crash, tray, ime, main])?.Window == main.Window,
            "Hidden main window still discarded or larger helper shown");
        Check(FlydigiWindowSelection.RejectionReason(main) is null && FlydigiWindowSelection.RejectionReason(helper) == "helper-window", "Wrong classification");
    }

    private static void LocalizedTitles()
    {
        foreach (string title in new[] { "Flydigi Space Station", "FLYDIGIゲームスペース", "飞智空间站", "飛智空間站", "스페이스 스테이션 4.0", "Station spatiale Flydigi", "Stazione spaziale Flydigi" })
            Check(FlydigiWindowSelection.Select([MainWindow() with { Title = title }]) is not null, "Valid localized main window missed");
    }

    private static void VisibleFallback()
    {
        var candidate = MainWindow() with { Visible = true, Title = "Controller configuration", WindowClass = "VendorMainWindow" };
        Check(FlydigiWindowSelection.Select([candidate]) == candidate, "Existing visible-window fallback regressed");
    }

    private static void RejectHelpers()
    {
        var main = MainWindow();
        foreach (var candidate in new[] {
            main with { Owned = true }, main with { PlacementRead = false }, main with { MetadataRead = false },
            main with { Style = 0x40000000 }, main with { ExtendedStyle = 0x80 }, main with { ExtendedStyle = 0x08000000 },
            main with { Bounds = new(0, 0, 399, 900) }, main with { Bounds = new(0, 0, 900, 299) },
            main with { WindowClass = "Chrome_WidgetWin_0" }, main with { WindowClass = "Electron_NotifyIconHostWindow" },
            main with { WindowClass = "Base_PowerMessageWindow" }, main with { WindowClass = "IME" },
            main with { Title = "DevTools — Flydigi Space Station", Visible = true },
            main with { Window = 0 }, main with { ProcessId = 0 }
        }) Check(FlydigiWindowSelection.Select([candidate]) is null, "Non-main window accepted");
    }

    private static void Priority()
    {
        var hidden = MainWindow(10);
        var visible = MainWindow(20) with { Visible = true };
        var unbranded = MainWindow(30) with { Visible = true, Title = "", Bounds = new(0, 0, 8000, 8000) };
        Check(FlydigiWindowSelection.Select([unbranded, hidden, visible]) == visible, "Visible main not preferred");
        Check(FlydigiWindowSelection.Select([unbranded, hidden]) == hidden, "Area outweighed positive product identity");
        Check(FlydigiWindowSelection.Select([hidden, MainWindow(5)])?.Window == 5, "Selection not deterministic");
    }

    private static void NoCandidate()
    {
        Check(FlydigiWindowSelection.Select([]) is null, "Empty list returned a window");
        Check(FlydigiWindowSelection.Select([MainWindow() with { Title = "Unknown background window" }]) is null,
            "Unidentified hidden window exposed");
    }

    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
