using Microsoft.UI.Windowing;

namespace ControllerHelper.Services;

internal static class OverlayWindowStyle
{
    internal static void Configure(AppWindow window)
    {
        // Keep a regular desktop window when taking focus from Steam. The caller
        // sizes it to the display bounds; no fullscreen presenter is needed.
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        window.SetPresenter(presenter);
    }
}
