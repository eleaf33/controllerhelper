using System.IO.Pipes;
using ControllerHelper.Models;
using ControllerHelper.Native;
using ControllerHelper.Services;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace ControllerHelper;

public partial class App : Application
{
    private const string MutexName = @"Local\ControllerHelper.Overlay.Instance";
    private static readonly string PipeName = CommandProtocol.PipeName(
        System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value,
        System.Diagnostics.Process.GetCurrentProcess().SessionId);
    private Mutex? _mutex;
    private CancellationTokenSource? _pipeCancellation;
    private SettingsWindow? _settingsWindow;
    private OverlayWindow? _overlay;
    private TrayIcon? _tray;
    private ControllerMonitor? _controller;
    private bool _quitting;
    private bool _exitRequested;

    public App()
    {
        ControllerHelper.Localization.Text.SetLanguage(ControllerHelper.Localization.Text.ReadLanguagePreference(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ControllerHelper", "settings.json")));
        ErrorLog.Event("application.start", new {
            os = Environment.OSVersion.VersionString,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            runtime = RuntimeInformation.FrameworkDescription,
            session = System.Diagnostics.Process.GetCurrentProcess().SessionId });
        UnhandledException += (_, eventArgs) => ErrorLog.Write("application.unhandled", eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception error) ErrorLog.Write("application.fatal", error);
        };
        StartupTrace.Write("App constructor");
        // UnhandledException += (_, eventArgs) => StartupTrace.Write(eventArgs.Exception.ToString());
        InitializeComponent();
        StartupTrace.Write("App XAML initialized");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupTrace.Write("OnLaunched");
        _mutex = new Mutex(true, MutexName, out bool firstInstance);
        string command = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() ?? "show";
        if (!CommandProtocol.IsSupported(command)) command = "show";
        if (!firstInstance)
        {
            try { await Task.Run(() => SendCommand(command)); }
            catch (Exception error)
            {
                ErrorLog.Write("ipc.send", error);
                MessageBox(0, ControllerHelper.Localization.Text.Format("App_xaml_001", error.Message),
                    "ControllerHelper", 0x10);
            }
            finally { _mutex.Dispose(); Exit(); }
            return;
        }

        AppSettings settings = SettingsStore.Load();
        ControllerHelper.Localization.Text.SetLanguage(settings.Language);
        ErrorLog.Event("settings.loaded", new { warning = SettingsStore.LoadWarning is not null });
        StartupTrace.Write("Settings loaded");
        _settingsWindow = new SettingsWindow(settings, new DisplayService(), new AudioService());
        _settingsWindow.ProfileSwitchFinished += () => _overlay?.Close();
        StartupTrace.Write("Settings window constructed");
        _settingsWindow.Activate();
        StartupTrace.Write("Settings window activated");
        _settingsWindow.AppWindow.Closing += (_, eventArgs) =>
        {
            if (_quitting) return;
            eventArgs.Cancel = true;
            _settingsWindow.AppWindow.Hide();
        };
        _controller = new ControllerMonitor(_settingsWindow.DispatcherQueue);
        StartupTrace.Write("Controller monitor started");
        _settingsWindow.AttachController(_controller);
        _controller.HotkeyPressed += ShowOverlay;
        _controller.EmergencyReturnPressed += RecoverToPc;
        /* Optional live button diagnostics (see ControllerMonitor.StateChanged).
        _controller.StateChanged += _settingsWindow.ReportController;
        bool controllerStatusLogged = false;
        _controller.StateChanged += message =>
        {
            if (controllerStatusLogged) return;
            StartupTrace.Write(message);
            controllerStatusLogged = true;
        };
        */
        _tray = new TrayIcon(_settingsWindow, ShowSettings,
            () => _ = RunCommandAsync("--to-tv"),
            () => _ = RunCommandAsync("--to-pc"), Quit);
        StartupTrace.Write("Tray icon created");
        _pipeCancellation = new CancellationTokenSource();
        _ = ListenAsync(_pipeCancellation.Token);
        ErrorLog.Event("application.ready");

        if (command is "--to-pc" or "--to-tv" or "--overlay") await RunCommandAsync(command);
    }

    private async Task ListenAsync(CancellationToken cancellation)
    {
        string? lastError = null;
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using NamedPipeServerStream pipe = new(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation);
                lastError = null;
                string? command = await CommandProtocol.ReadAsync(pipe, cancellation);
                if (command is not null)
                    _settingsWindow?.DispatcherQueue.TryEnqueue(() => _ = RunCommandAsync(command));
            }
            catch (OperationCanceledException) { break; }
            catch (IOException)
            {
                try { await Task.Delay(200, cancellation); }
                catch (OperationCanceledException) { break; }
            }
            catch (Exception error)
            {
                if (lastError != error.Message)
                {
                    lastError = error.Message;
                    ErrorLog.Write("ipc.listen", error);
                    StartupTrace.Write($"Command pipe failed: {error}");
                    _settingsWindow?.DispatcherQueue.TryEnqueue(() =>
                        _settingsWindow?.ReportError(ControllerHelper.Localization.Text.Format("App_xaml_002", error.Message)));
                }
                try { await Task.Delay(1000, cancellation); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static void SendCommand(string command)
    {
        if (!CommandProtocol.IsSupported(command)) throw new ArgumentException(ControllerHelper.Localization.Text.Get("App_xaml_003"), nameof(command));
        using NamedPipeClientStream pipe = new(".", PipeName, PipeDirection.Out);
        pipe.Connect(3000);
        using StreamWriter writer = new(pipe) { AutoFlush = true };
        writer.WriteLine(command);
    }

    private async Task RunCommandAsync(string command)
    {
        try
        {
            if (_settingsWindow is null || _exitRequested) return;
            switch (command)
            {
                case "--to-tv":
                    await _settingsWindow.SwitchToTvAsync();
                    _overlay?.Close();
                    break;
                case "--overlay": ShowOverlay(); break;
                case "--to-pc":
                    await _settingsWindow.SwitchToPcAsync();
                    _overlay?.Close();
                    break;
                default: ShowSettings(); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (_exitRequested || _settingsWindow?.IsRecovering == true) return;
            ErrorLog.Write("command.run", error);
            ShowSettings();
            _settingsWindow?.ReportError(error.Message);
        }
    }

    private void ShowSettings()
    {
        if (_exitRequested) return;
        _settingsWindow?.RefreshDevices();
        _settingsWindow?.Activate();
    }

    private void ShowOverlay()
    {
        if (_exitRequested || _settingsWindow?.IsSwitching == true) return;
        StartupTrace.Write($"ShowOverlay called: controller={_controller is not null}, open={_overlay is not null}");
        if (_overlay is not null)
        {
            _overlay.ExitFlydigiMode();
            _overlay.ActivateForInput();
            return;
        }
        if (_controller is null) return;
        try
        {
            Windows.Graphics.RectInt32? televisionBounds;
            try { televisionBounds = _settingsWindow!.GetOverlayTelevisionBounds(); }
            catch (Exception error)
            {
                StartupTrace.Write($"Could not locate TV display: {error}");
                televisionBounds = null;
            }
            _overlay = new OverlayWindow(_controller,
                () => RunOverlayOperationAsync(() => _settingsWindow!.SwitchToPcAsync()),
                () => RunOverlayOperationAsync(() => _settingsWindow!.SwitchToTvAsync()),
                () => RunOverlayOperationAsync(() => _settingsWindow!.SwitchToPcAndPowerOffAsync()),
                () => _settingsWindow!.GetOverlayLocalState(),
                cancellation => _settingsWindow!.GetOverlayStateAsync(cancellation),
                () => RunOverlayOperationAsync(() => _settingsWindow!.ToggleMonitorsAsync()),
                () => _settingsWindow!.GetFlydigiCursorSpeed(),
                speed => _settingsWindow!.SetFlydigiCursorSpeed(speed),
                televisionBounds);
            _overlay.Closed += (_, _) => _overlay = null;
        }
        catch (Exception error)
        {
            ErrorLog.Write("overlay.show", error);
            StartupTrace.Write($"ShowOverlay failed: {error}");
            ShowSettings();
            _settingsWindow?.ReportError(error.Message);
        }
    }

    private Task RunOverlayOperationAsync(Func<Task> operation) =>
        RunOverlayOperationAsync(async () => { await operation(); return true; });

    private Task<T> RunOverlayOperationAsync<T>(Func<Task<T>> operation) =>
        OverlayOperation.RunAsync(operation,
            () => _exitRequested || _settingsWindow?.IsRecovering == true,
            error =>
            {
                ErrorLog.Write("overlay.operation", error);
                // Avoid device refresh here: it could replace the original error.
                _settingsWindow?.ReportError(error.Message);
                _settingsWindow?.Activate();
            });

    private async void RecoverToPc()
    {
        if (_exitRequested || _settingsWindow is null) return;
        try
        {
            _overlay?.Close();
            await _settingsWindow.RecoverToPcAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ErrorLog.Write("recovery.run", error);
            if (_exitRequested) return;
            ShowSettings();
            _settingsWindow.ReportError(ControllerHelper.Localization.Text.Get("App_xaml_004") + error.Message);
        }
    }

    private async void Quit()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        try
        {
            if (_settingsWindow is not null) await _settingsWindow.StopProfileSwitchesAsync();
        }
        catch (Exception error)
        {
            _exitRequested = false;
            ErrorLog.Write("application.quit", error);
            ShowSettings();
            _settingsWindow?.ReportError(ControllerHelper.Localization.Text.Format("App_xaml_005", error.Message));
            return;
        }
        _quitting = true;
        _overlay?.Close();
        _controller?.Stop();
        _tray?.Dispose();
        _pipeCancellation?.Cancel();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _settingsWindow?.Close();
        ErrorLog.Event("application.exit");
        Exit();
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint type);
}
