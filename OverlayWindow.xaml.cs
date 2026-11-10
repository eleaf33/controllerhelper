using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;
using ControllerHelper.Services;
using WinRT.Interop;

namespace ControllerHelper;

public sealed partial class OverlayWindow : Window
{
    private readonly ControllerMonitor _controller;
    private readonly ControllerInputScope _inputScope;
    private readonly Func<Task> _returnToPc;
    private readonly Func<Task> _playOnTv;
    private readonly Func<Task> _returnAndPowerOff;
    private readonly OverlayStateRefresh _stateRefresh;
    private readonly Func<Task<bool>> _toggleMonitors;
    private readonly OverlayNavigation _navigation = new();
    private readonly FlydigiWindowService _flydigiWindow = new();
    private readonly FlydigiGamepadInput _flydigiInput = new();
    private readonly DispatcherQueueTimer _flydigiWatch;
    private readonly DispatcherQueueTimer _speedSaveTimer;
    private readonly Action<double> _saveCursorSpeed;
    private bool _speedReady;
    private bool _speedDirty;
    private bool _busy;
    private bool _closed;
    private bool _flydigiLayoutQueued;
    private bool _flydigiErrorShown;
    private readonly CancellationTokenSource _flydigiOpening = new();
    private XamlRoot? _observedXamlRoot;
    private OverlayPrimaryAction _primaryAction;

    public OverlayWindow(ControllerMonitor controller, Func<Task> returnToPc, Func<Task> playOnTv,
        Func<Task> returnAndPowerOff, Func<TelevisionOverlayState> getLocalState,
        Func<CancellationToken, Task<TelevisionOverlayState>> getState,
        Func<Task<bool>> toggleMonitors, Func<double> getCursorSpeed, Action<double> saveCursorSpeed,
        Windows.Graphics.RectInt32? televisionBounds)
    {
        InitializeComponent();
        ((FrameworkElement)Content).Language = ControllerHelper.Localization.Text.LanguageTag;
        OverlayInputGuard.Attach(OverlayRoot);
        Title = "ControllerHelper";
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        _controller = controller;
        _returnToPc = returnToPc;
        _playOnTv = playOnTv;
        _returnAndPowerOff = returnAndPowerOff;
        _stateRefresh = new OverlayStateRefresh(getLocalState, getState, ApplyState,
            error => ErrorLog.Write("overlay.network", error));
        _toggleMonitors = toggleMonitors;
        _saveCursorSpeed = saveCursorSpeed;
        _speedSaveTimer = DispatcherQueue.CreateTimer();
        _speedSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _speedSaveTimer.Tick += (_, _) => PersistCursorSpeed();
        FlydigiSpeedSlider.Value = getCursorSpeed();
        FlydigiSpeedValue.Text = $"{Math.Round(FlydigiSpeedSlider.Value * 100):0}%";
        _flydigiInput.SetCursorSpeed(FlydigiSpeedSlider.Value);
        _speedReady = true;
        _flydigiWatch = DispatcherQueue.CreateTimer();
        _flydigiWatch.Interval = TimeSpan.FromMilliseconds(500);
        _flydigiWatch.Tick += (_, _) =>
        {
            if (!_flydigiWindow.IsOpen)
            {
                _flydigiWindow.Trace("watch.window-lost");
                ExitFlydigiMode();
            }
            else UpdateFlydigiLayout();
        };
        OverlayRoot.Loaded += (_, _) =>
        {
            _observedXamlRoot = OverlayRoot.XamlRoot;
            _observedXamlRoot.Changed += OnXamlRootChanged;
            QueueFlydigiLayout();
        };
        OverlayRoot.SizeChanged += (_, _) => QueueFlydigiLayout();
        FlydigiViewport.SizeChanged += (_, _) => QueueFlydigiLayout();
        OverlayWindowStyle.Configure(AppWindow);
        PositionOverlay(televisionBounds);
        Activate();
        StartupTrace.Write($"Overlay activated: bounds={AppWindow.Position.X},{AppWindow.Position.Y} " +
            $"{AppWindow.Size.Width}x{AppWindow.Size.Height}");
        BackButton.GotFocus += (_, _) => SelectCard(0);
        MonitorButton.GotFocus += (_, _) => SelectCard(1);
        CloseButton.GotFocus += (_, _) => SelectCard(2);
        FlydigiButton.GotFocus += (_, _) => SelectCard(3);
        TelegramButton.GotFocus += (_, _) => SelectCard(4);
        BackButton.PointerEntered += (_, _) => SelectCard(0);
        MonitorButton.PointerEntered += (_, _) => SelectCard(1);
        CloseButton.PointerEntered += (_, _) => SelectCard(2);
        FlydigiButton.PointerEntered += (_, _) => SelectCard(3);
        TelegramButton.PointerEntered += (_, _) => SelectCard(4);
        BackButton.IsEnabled = MonitorButton.IsEnabled = false;
        SelectCard(2);
        CloseButton.Focus(FocusState.Programmatic);
        _inputScope = _controller.AcquireInput(ControllerInputMode.Overlay,
            Confirm, Dismiss, Move, MoveVertical, SendFlydigiInput,
            () => OverlayInputGuard.HasFocus(WindowNative.GetWindowHandle(this)) ||
                (_controller.FlydigiControlActive && _flydigiWindow.HasFocus()), _flydigiInput.Reset);
        Closed += (_, _) =>
        {
            _closed = true;
            _flydigiOpening.Cancel();
            if (_observedXamlRoot is not null) _observedXamlRoot.Changed -= OnXamlRootChanged;
            _stateRefresh.Dispose();
            try
            {
                PersistCursorSpeed();
                ExitFlydigiMode();
            }
            finally { _inputScope.Dispose(); _flydigiOpening.Dispose(); }
        };
        RefreshState();
        ActivateForInput();
    }

    internal void ActivateForInput()
    {
        if (_closed) return;
        Activate();
        if (!OverlayInputGuard.TryActivate(WindowNative.GetWindowHandle(this)))
            StartupTrace.Write("Overlay foreground activation was denied; hotkey can retry.");
    }

    private void PositionOverlay(Windows.Graphics.RectInt32? televisionBounds)
    {
        if (televisionBounds is Windows.Graphics.RectInt32 bounds)
        {
            try
            {
                Windows.Graphics.PointInt32 position = new() { X = bounds.X, Y = bounds.Y };
                DisplayArea? area = DisplayArea.GetFromPoint(position, DisplayAreaFallback.None);
                if (area is not null && area.OuterBounds.X == bounds.X && area.OuterBounds.Y == bounds.Y)
                {
                    AppWindow.MoveAndResize(area.OuterBounds);
                    return;
                }
            }
            catch (Exception error) { StartupTrace.Write($"Could not position overlay on TV: {error}"); }
        }

        try
        {
            Windows.Graphics.PointInt32 primaryOrigin = new() { X = 0, Y = 0 };
            DisplayArea area = DisplayArea.GetFromPoint(primaryOrigin, DisplayAreaFallback.Primary);
            AppWindow.MoveAndResize(area.OuterBounds);
        }
        catch (Exception error) { StartupTrace.Write($"Could not position overlay on primary display: {error}"); }
    }

    private void Move(int direction)
    {
        if (_busy) return;
        int selection = _navigation.MoveHorizontal(direction, BackButton.IsEnabled, MonitorButton.IsEnabled);
        SelectCard(selection);
        (selection == 0 ? BackButton : selection == 1 ? MonitorButton :
            selection == 2 ? CloseButton : FlydigiButton).Focus(FocusState.Programmatic);
    }

    private void MoveVertical(int direction)
    {
        if (_busy) return;
        int selection = _navigation.MoveVertical(direction, BackButton.IsEnabled, MonitorButton.IsEnabled);
        if (selection == _navigation.Selection) return;
        SelectCard(selection);
        (selection == 0 ? BackButton : selection == 1 ? MonitorButton :
            selection == 2 ? CloseButton : selection == 3 ? FlydigiButton : TelegramButton).Focus(FocusState.Programmatic);
    }

    private void SelectCard(int selection)
    {
        _navigation.Select(selection);
        BackGlow.Opacity = BackRing.Opacity = selection == 0 ? 1 : 0;
        MonitorGlow.Opacity = MonitorRing.Opacity = selection == 1 ? 1 : 0;
        CloseGlow.Opacity = CloseRing.Opacity = selection == 2 ? 1 : 0;
        BackIconGlow.Opacity = selection == 0 ? 1 : 0;
        MonitorIconGlow.Opacity = selection == 1 ? 1 : 0;
        CloseIconGlow.Opacity = selection == 2 ? 1 : 0;
        FlydigiGlow.Opacity = FlydigiRing.Opacity = FlydigiIconGlow.Opacity = selection == 3 ? 1 : 0;
        TelegramGlow.Opacity = TelegramRing.Opacity = selection == 4 ? 1 : 0;
    }

    private void Confirm()
    {
        if (_busy)
        {
            if (_navigation.Selection == 3) ErrorLog.Event("flydigi.open.skipped", new { source = "controller", reason = "busy" });
            return;
        }
        if (_navigation.Selection == 0)
        {
            if (BackButton.IsEnabled) _ = RunPrimaryAsync();
        }
        else if (_navigation.Selection == 1) _ = ToggleMonitorsAsync();
        else if (_navigation.Selection == 3) _ = OpenFlydigiAsync("controller");
        else if (_navigation.Selection == 4) _ = CloseTelegramAsync();
        else Dismiss();
    }

    private async void Back_Click(object sender, RoutedEventArgs args) => await RunPrimaryAsync();
    private async void Monitor_Click(object sender, RoutedEventArgs args) => await ToggleMonitorsAsync();
    private void Close_Click(object sender, RoutedEventArgs args) => Dismiss();
    private async void Flydigi_Click(object sender, RoutedEventArgs args) => await OpenFlydigiAsync("button");
    private void FlydigiClose_Click(object sender, RoutedEventArgs args) => CloseFlydigi();

    private void CloseFlydigi()
    {
        if (!_flydigiWindow.IsOpen) return;
        _flydigiWindow.CloseClient();
        ExitFlydigiMode();
    }
    private async void Telegram_Click(object sender, RoutedEventArgs args) => await CloseTelegramAsync();

    public void ExitFlydigiMode()
    {
        if (_inputScope.Mode != ControllerInputMode.Flydigi && !_flydigiWindow.IsOpen) return;
        _flydigiWindow.Trace("mode.exit", new { overlayClosed = _closed, windowAlive = _flydigiWindow.IsOpen, mode = _inputScope.Mode.ToString() });
        PersistCursorSpeed();
        _flydigiWatch.Stop();
        _flydigiInput.StopPointer();
        _inputScope.SetMode(ControllerInputMode.Overlay);
        _flydigiWindow.Restore();
        if (_closed) return;
        FlydigiPanel.Visibility = Visibility.Collapsed;
        MenuPanel.Visibility = Visibility.Visible;
        ActivateForInput();
        SelectCard(3);
        FlydigiButton.Focus(FocusState.Programmatic);
    }

    private async Task OpenFlydigiAsync(string source)
    {
        using IDisposable operation = ErrorLog.BeginOperation("flydigi.open");
        ErrorLog.Event("flydigi.open.request", new { source, busy = _busy, closed = _closed, mode = _inputScope.Mode.ToString() });
        if (_busy || _closed)
        {
            ErrorLog.Event("flydigi.open.skipped", new { reason = _closed ? "overlay-closed" : "busy" });
            return;
        }
        _stateRefresh.Cancel();
        _busy = true;
        FlydigiButton.IsEnabled = false;
        MonitorErrorText.Visibility = Visibility.Collapsed;
        _flydigiErrorShown = false;
        try
        {
            MenuPanel.Visibility = Visibility.Collapsed;
            FlydigiPanel.Visibility = Visibility.Visible;
            await _flydigiWindow.OpenAsync(GetFlydigiBounds, _flydigiOpening.Token);
            if (_closed)
            {
                _flydigiWindow.Trace("open.abandoned", new { reason = "overlay-closed" });
                _flydigiWindow.Restore();
                return;
            }
            if (!_flydigiWindow.IsOpen)
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("OverlayWindow_xaml_025"));
            _flydigiInput.StartPointer();
            _inputScope.SetMode(ControllerInputMode.Flydigi);
            _flydigiWatch.Start();
            _flydigiWindow.Trace("mode.entered", new { mode = _inputScope.Mode.ToString(), hasFocus = _flydigiWindow.HasFocus() });
        }
        catch (Exception error)
        {
            StartupTrace.Write($"Flydigi overlay failed: {error}");
            ErrorLog.Write("flydigi.open", error);
            _flydigiInput.StopPointer();
            _inputScope.SetMode(ControllerInputMode.Overlay);
            _flydigiWindow.Restore();
            if (_closed)
            {
                _flydigiWindow.Trace("ui.error-not-shown", new { reason = "overlay-closed" });
                return;
            }
            FlydigiPanel.Visibility = Visibility.Collapsed;
            MenuPanel.Visibility = Visibility.Visible;
            MonitorErrorText.Text = error.Message;
            MonitorErrorText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 155, 171));
            MonitorErrorText.Visibility = Visibility.Visible;
            _flydigiErrorShown = true;
            _flydigiWindow.Trace("ui.error-shown", new { exceptionType = error.GetType().Name, menuVisible = MenuPanel.Visibility == Visibility.Visible });
            FlydigiGlow.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 180, 48, 68));
            FlydigiRing.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 111, 135));
        }
        finally
        {
            _busy = false;
            if (!_closed) FlydigiButton.IsEnabled = true;
            if (!_closed && !_controller.FlydigiControlActive)
            {
                SelectCard(3);
                FlydigiButton.Focus(FocusState.Programmatic);
            }
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => QueueFlydigiLayout();

    private void QueueFlydigiLayout()
    {
        if (_closed || _flydigiLayoutQueued || FlydigiPanel.Visibility != Visibility.Visible) return;
        _flydigiLayoutQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _flydigiLayoutQueued = false;
            if (!_closed && !_busy) UpdateFlydigiLayout();
        })) _flydigiLayoutQueued = false;
    }

    private void UpdateFlydigiLayout()
    {
        if (_closed || _busy || FlydigiPanel.Visibility != Visibility.Visible || !_flydigiWindow.IsOpen) return;
        try
        {
            if (!_flydigiWindow.FitToBounds(GetFlydigiBounds()))
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("OverlayWindow_xaml_026"));
        }
        catch (Exception error)
        {
            ErrorLog.Write("flydigi.layout", error);
            _flydigiWindow.Trace("layout.failed", new { exceptionType = error.GetType().Name });
            ExitFlydigiMode();
            MonitorErrorText.Text = error.Message;
            MonitorErrorText.Visibility = Visibility.Visible;
            _flydigiErrorShown = true;
            _flydigiWindow.Trace("ui.error-shown", new { phase = "layout" });
        }
    }

    private ScreenBounds GetClientScreenBounds()
    {
        nint window = WindowNative.GetWindowHandle(this);
        CursorPoint origin = default;
        if (!GetClientRect(window, out ClientRect client) || !ClientToScreen(window, ref origin))
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("OverlayWindow_xaml_027"));
        return new(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
    }

    private ScreenBounds GetElementScreenBounds(FrameworkElement element, double inset = 0)
    {
        Windows.Foundation.Point origin = element.TransformToVisual(OverlayRoot)
            .TransformPoint(new Windows.Foundation.Point(inset, inset));
        return OverlayGeometry.ToScreen(GetClientScreenBounds(), OverlayRoot.XamlRoot.RasterizationScale,
            origin.X, origin.Y, element.ActualWidth - 2 * inset, element.ActualHeight - 2 * inset);
    }

    private Windows.Graphics.RectInt32 GetFlydigiBounds()
    {
        if (_closed) throw new OperationCanceledException(ControllerHelper.Localization.Text.Get("OverlayWindow_xaml_028"));
        ScreenBounds client = GetClientScreenBounds();
        double scale = OverlayRoot.XamlRoot.RasterizationScale;
        // Constrain the star row to the real client area, even during a DPI/display transition.
        FlydigiPanel.Width = Math.Max(0, client.Width / scale - FlydigiPanel.Margin.Left - FlydigiPanel.Margin.Right);
        FlydigiPanel.Height = Math.Max(0, client.Height / scale - FlydigiPanel.Margin.Top - FlydigiPanel.Margin.Bottom);
        FlydigiPanel.UpdateLayout();
        ScreenBounds bounds = GetElementScreenBounds(FlydigiViewport, 2);
        _flydigiWindow.TraceLayout(new { client, scale, viewport = bounds, panelWidth = FlydigiPanel.ActualWidth,
            panelHeight = FlydigiPanel.ActualHeight, viewportWidth = FlydigiViewport.ActualWidth, viewportHeight = FlydigiViewport.ActualHeight });
        return new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private void SendFlydigiInput(ControllerDevice current, ControllerDevice? previous)
    {
        bool speedSelected = false;
        if (_controller.FlydigiControlActive && (current.Buttons & 0x4) != 0 &&
            (previous is null || (previous.Buttons & 0x4) == 0))
            speedSelected = SetSpeedFromPointer();
        if (_controller.FlydigiControlActive &&
            _flydigiInput.Send(current, previous, _flydigiWindow.HasFocus(),
                _flydigiWindow.PointerOverClient(), speedSelected))
            CloseFlydigi();
    }

    private void FlydigiSpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_speedReady) return;
        FlydigiSpeedValue.Text = $"{Math.Round(args.NewValue * 100):0}%";
        _flydigiInput.SetCursorSpeed(args.NewValue);
        _flydigiWindow.KeepOnTop();
        _speedDirty = true;
        _speedSaveTimer.Stop();
        _speedSaveTimer.Start();
    }

    private bool SetSpeedFromPointer()
    {
        if (!GetCursorPos(out CursorPoint pointer)) return false;
        ScreenBounds slider = GetElementScreenBounds(FlydigiSpeedSlider);
        double left = slider.X, top = slider.Y, width = slider.Width, height = slider.Height;
        if (width <= 0 || pointer.X < left || pointer.X > left + width ||
            pointer.Y < top || pointer.Y > top + height) return false;
        double fraction = Math.Clamp((pointer.X - left) / width, 0, 1);
        double value = FlydigiSpeedSlider.Minimum + fraction *
            (FlydigiSpeedSlider.Maximum - FlydigiSpeedSlider.Minimum);
        FlydigiSpeedSlider.Value = Math.Round(value / 0.05) * 0.05;
        return true;
    }

    private void PersistCursorSpeed()
    {
        _speedSaveTimer.Stop();
        if (!_speedDirty) return;
        try
        {
            _saveCursorSpeed(FlydigiSpeedSlider.Value);
            _speedDirty = false;
        }
        catch (Exception error) { ErrorLog.Write("settings.cursor-speed", error); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { internal int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRect { internal int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out ClientRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint window, ref CursorPoint point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    private async Task CloseTelegramAsync()
    {
        if (_busy) return;
        _stateRefresh.Cancel();
        _busy = true;
        TelegramButton.IsEnabled = false;
        try
        {
            int closed = await TelegramProcessService.CloseAsync();
            MonitorErrorText.Text = closed == 0 ? ControllerHelper.Localization.Text.Get("OverlayWindow_xaml_029") : ControllerHelper.Localization.Text.Get("OverlayWindow_xaml_030");
            MonitorErrorText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 169, 221, 255));
            MonitorErrorText.Visibility = Visibility.Visible;
            TelegramGlow.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(81, 67, 118, 156));
            TelegramRing.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 142, 215, 255));
        }
        catch (Exception error)
        {
            StartupTrace.Write($"Could not close Telegram: {error}");
            ErrorLog.Write("telegram.close", error);
            MonitorErrorText.Text = ControllerHelper.Localization.Text.Format("OverlayWindow_xaml_031", error.Message);
            MonitorErrorText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 155, 171));
            MonitorErrorText.Visibility = Visibility.Visible;
            TelegramGlow.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 180, 48, 68));
            TelegramRing.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 111, 135));
        }
        finally
        {
            TelegramButton.IsEnabled = true;
            _busy = false;
            SelectCard(4);
            TelegramButton.Focus(FocusState.Programmatic);
        }
    }

    private void ApplyState(TelevisionOverlayState state, bool selectPrimary)
    {
        if (_closed) return;
        if (_flydigiErrorShown)
        {
            _flydigiWindow.Trace("ui.error-cleared", new { reason = "overlay-state-refresh" });
            _flydigiErrorShown = false;
        }
        BackGlow.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(87, 75, 58, 165));
        BackRing.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 184, 166, 255));
        ToolTipService.SetToolTip(BackButton, null);
        MonitorErrorText.Visibility = Visibility.Collapsed;
        OverlayPresentation presentation = OverlayPresentation.FromState(state);
        ErrorLog.Event("overlay.state", new
        {
            state.WakeEnabled, state.PowerControlSupported, state.PowerOffOnReturn, state.TelevisionActive, state.MonitorsActive,
            powerState = state.PowerState.ToString(), state.PowerCheckPending,
            action = presentation.PrimaryAction.ToString()
        });
        MonitorButtonText.Text = presentation.MonitorText;
        AutomationProperties.SetName(MonitorButton, presentation.MonitorLabel);
        MonitorButton.IsEnabled = presentation.MonitorEnabled;
        _primaryAction = presentation.PrimaryAction;
        BackButtonText.Text = presentation.PrimaryText;
        AutomationProperties.SetName(BackButton, presentation.PrimaryLabel);
        BackButton.IsEnabled = _primaryAction != OverlayPrimaryAction.None;
        TvIcon.Visibility = _primaryAction == OverlayPrimaryAction.PlayOnTv ? Visibility.Visible : Visibility.Collapsed;
        BackIcon.Visibility = _primaryAction == OverlayPrimaryAction.PlayOnTv ? Visibility.Collapsed : Visibility.Visible;
        CloseButtonText.Text = presentation.CloseText;
        AutomationProperties.SetName(CloseButton, presentation.CloseLabel);
        if (presentation.Warning is string warning)
        {
            MonitorErrorText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 155, 171));
            MonitorErrorText.Text = warning;
            MonitorErrorText.Visibility = Visibility.Visible;
        }
        if (selectPrimary && BackButton.IsEnabled)
        {
            SelectCard(0);
            BackButton.Focus(FocusState.Programmatic);
        }
    }

    private bool RefreshState()
    {
        try
        {
            if (_closed) return false;
            _stateRefresh.Refresh();
            return true;
        }
        catch (Exception error)
        {
            StartupTrace.Write($"Overlay state failed: {error}");
            ErrorLog.Write("overlay.local-state", error);
            if (_closed) return false;
            _primaryAction = OverlayPrimaryAction.None;
            BackButton.IsEnabled = MonitorButton.IsEnabled = false;
            MonitorErrorText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 155, 171));
            MonitorErrorText.Text = error.Message;
            MonitorErrorText.Visibility = Visibility.Visible;
            SelectCard(2);
            CloseButton.Focus(FocusState.Programmatic);
            return false;
        }
    }

    private async Task ToggleMonitorsAsync()
    {
        if (_busy || !MonitorButton.IsEnabled) return;
        _stateRefresh.Cancel();
        _busy = true;
        MonitorErrorText.Visibility = Visibility.Collapsed;
        try
        {
            await _toggleMonitors();
            if (_closed) return;
            if (!RefreshState()) return;
            MonitorGlow.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(81, 67, 118, 156));
            MonitorRing.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 142, 215, 255));
            MonitorButton.Focus(FocusState.Programmatic);
        }
        catch (Exception error)
        {
            StartupTrace.Write($"Monitor toggle failed: {error}");
            if (_closed) return;
            ToolTipService.SetToolTip(MonitorButton, error.Message);
            MonitorGlow.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 180, 48, 68));
            MonitorRing.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 111, 135));
            MonitorErrorText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 155, 171));
            MonitorErrorText.Text = error.Message;
            MonitorErrorText.Visibility = Visibility.Visible;
            MonitorButton.Focus(FocusState.Programmatic);
        }
        finally { _busy = false; }
    }

    private async Task RunPrimaryAsync()
    {
        if (_busy || !BackButton.IsEnabled) return;
        _stateRefresh.Cancel();
        _busy = true;
        BackButton.IsEnabled = false;
        OverlayPrimaryAction attempted = _primaryAction;
        ErrorLog.Event("overlay.primary", new { action = attempted.ToString() });
        try
        {
            switch (_primaryAction)
            {
                case OverlayPrimaryAction.PlayOnTv: await _playOnTv(); break;
                case OverlayPrimaryAction.ReturnAndPowerOff: await _returnAndPowerOff(); break;
                case OverlayPrimaryAction.ReturnToPc: await _returnToPc(); break;
                default: return;
            }
            if (!_closed) Close();
        }
        catch (Exception error)
        {
            StartupTrace.Write($"Return to PC failed: {error}");
            RefreshState();
            if (_closed) return;
            ToolTipService.SetToolTip(BackButton, error.Message);
            if (BackButton.IsEnabled && _primaryAction == attempted)
            {
                BackGlow.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 180, 48, 68));
                BackRing.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 111, 135));
                SelectCard(0);
                BackButton.Focus(FocusState.Programmatic);
            }
            else
            {
                SelectCard(2);
                CloseButton.Focus(FocusState.Programmatic);
            }
            MonitorErrorText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 155, 171));
            MonitorErrorText.Text = error.Message;
            MonitorErrorText.Visibility = Visibility.Visible;
        }
        finally { _busy = false; }
    }

    private void Dismiss()
    {
        if (!_busy) Close();
    }

}
