using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics;

namespace ControllerHelper.Services;

internal sealed partial class FlydigiWindowService
{
    private const string ProcessName = "Flydigi Space Station";
    private nint _window;
    private WindowPlacement _originalPlacement;
    private readonly FlydigiDiagnostics _diagnostics = new();

    internal nint Window => _window;
    internal bool IsOpen => _window != 0 && IsWindow(_window);
    internal string? AttemptId => _diagnostics.AttemptId;
    internal void Trace(string phase, object? data = null) => _diagnostics.Event(phase, data);
    internal void TraceLayout(object data) => _diagnostics.Changed("viewport", data);

    internal async Task OpenAsync(Func<RectInt32> getBounds, CancellationToken cancellation = default)
    {
        _diagnostics.Begin();
        string stage = "existing-window";
        bool captured = false;
        try
        {
            Trace("open.state", new { alreadyTracked = _window != 0, windowAlive = IsOpen });
            LogProcessAccess((uint)Environment.ProcessId, "controllerhelper");
            if (_window != 0) throw new InvalidOperationException("Клиент Flydigi уже открыт в оверлее.");
            stage = "find-window";
            FlydigiWindowCandidate? previousCandidate = FindWindow();
            stage = "find-executable";
            string? executable = FindExecutable();
            if (executable is null)
                throw new FileNotFoundException("Flydigi Space Station не найден. Установите клиент в стандартную папку Program Files.");
            FlydigiWindowCandidate candidate;
            Process? launched = null;
            try
            {
                candidate = await FlydigiWindowActivation.OpenAsync(() =>
                {
                    stage = "launch";
                    Trace("launch.begin", new { shellExecute = true, workingDirectory = "executable-directory",
                        activation = "application", existingWindow = previousCandidate is not null,
                        existingVisible = previousCandidate?.Visible });
                    launched = Process.Start(new ProcessStartInfo(executable)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(executable)!
                    });
                    Trace("launch.requested", new { processReturned = launched is not null, processId = launched?.Id });
                    stage = "wait-application-visible";
                }, () => FindWindow(visibleOnly: true), cancellation);
                Trace("launch.result", new { windowFound = true, applicationVisible = candidate.Visible, processReturned = launched is not null,
                    processState = LaunchProcessState(launched) });
            }
            finally { launched?.Dispose(); }
            nint window = candidate.Window;
            stage = "validate-window";
            GetWindowThreadProcessId(window, out uint processId);
            if (!IsWindow(window) || !IsWindowVisible(window) || processId != candidate.ProcessId)
                throw new InvalidOperationException("Окно Flydigi изменилось во время поиска. Повторите открытие.");
            Trace("selection.result", new { candidate.Visible, titleMatches = candidate.TitleMatches, candidate.WindowClass,
                activation = "application", sameWindow = previousCandidate?.Window == window });
            Trace("window.selected", WindowState(window));
            LogProcessAccess(processId, "flydigi");
            stage = "capture-placement";
            WindowPlacement placement = new() { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
            if (!GetWindowPlacement(window, ref placement))
                throw NativeFailure("GetWindowPlacement", Marshal.GetLastPInvokeError(), "Не удалось прочитать положение окна Flydigi.");
            _window = window;
            _originalPlacement = placement;
            captured = true;
            Trace("placement.captured", new { placement.ShowCommand, normal = Bounds(placement.NormalPosition), applicationVisible = true });
            stage = "show-window";
            // The application has already shown its own content. Only restore a
            // minimized visible window; never expose an uninitialized hidden HWND.
            if (IsIconic(window)) ShowWindow(window, 9);
            Trace("window.application-visible", WindowState(window));
            stage = "initial-fit";
            FitToBounds(getBounds());
            // Moving a foreign window can trigger its own DPI resize asynchronously.
            // Re-read both the destination layout and actual window rectangle afterwards.
            await Task.Delay(100, cancellation);
            if (_window != window || !IsWindow(window))
            {
                Trace("window.lost", new { stage, trackedWindowChanged = _window != window, windowAlive = IsWindow(window) });
                throw new InvalidOperationException("Окно Flydigi исчезло во время открытия. Подробности в logs.");
            }
            if (!IsWindowVisible(window))
            {
                Trace("window.show-failed", WindowState(window));
                throw new InvalidOperationException("Windows не показала окно Flydigi. Подробности в logs.");
            }
            stage = "fit-after-dpi";
            RectInt32 bounds = getBounds();
            if (!FitToBounds(bounds))
                throw new InvalidOperationException("Минимальный размер окна Flydigi превышает доступную область. " +
                    "Уменьшите масштаб интерфейса Flydigi или масштаб Windows на этом экране.");
            stage = "focus";
            if (!TryFocus(window))
            {
                await Task.Delay(150);
                if (!TryFocus(window))
                {
                    StartupTrace.Write("Flydigi focus was denied; controller pointer activation is available.");
                    Trace("focus.denied", WindowState(window));
                    if (!GetCursorPos(out Point cursor) || cursor.X < bounds.X || cursor.X >= bounds.X + bounds.Width ||
                        cursor.Y < bounds.Y || cursor.Y >= bounds.Y + bounds.Height)
                    {
                        bool moved = SetCursorPos(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                        int code = moved ? 0 : Marshal.GetLastPInvokeError();
                        Trace("cursor.center", new { moved, code });
                    }
                }
            }
            Trace("window.ready", new { state = WindowState(window), hasFocus = HasFocus() });
        }
        catch (Exception error)
        {
            Trace("open.failed", new { stage, exceptionType = error.GetType().Name,
                nativeCode = error is Win32Exception win32 ? (int?)win32.NativeErrorCode : null });
            if (captured) Restore();
            throw;
        }
    }

    internal bool FitToBounds(RectInt32 bounds)
    {
        if (!IsOpen) { Trace("fit.window-missing"); return false; }
        if (!IsWindowVisible(_window))
            throw new InvalidOperationException("Flydigi скрыл своё окно. Повторите открытие из оверлея.");
        if (bounds.Width < 400 || bounds.Height < 300)
        {
            Trace("fit.viewport-too-small", new { bounds.X, bounds.Y, bounds.Width, bounds.Height });
            throw new InvalidOperationException("Область Flydigi слишком мала.");
        }
        if (!GetWindowRect(_window, out Rect actual))
            throw NativeFailure("GetWindowRect", Marshal.GetLastPInvokeError(), "Не удалось прочитать размер окна Flydigi.");
        ScreenBounds viewport = new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        ScreenBounds target = OverlayGeometry.Fit(viewport, actual.Right - actual.Left, actual.Bottom - actual.Top);
        var before = Bounds(actual);
        if (actual.Left != target.X || actual.Top != target.Y ||
            actual.Right - actual.Left != target.Width || actual.Bottom - actual.Top != target.Height ||
            (GetWindowLongPtr(_window, -20).ToInt64() & 0x8) == 0)
        {
            // Keep focus where it is; only positioning and size are adjusted.
            if (!SetWindowPos(_window, (nint)(-1), target.X, target.Y, target.Width, target.Height, 0x10))
                throw NativeFailure("SetWindowPos", Marshal.GetLastPInvokeError(), "Не удалось поместить окно Flydigi в оверлей.");
        }
        bool measured = GetWindowRect(_window, out actual);
        int code = measured ? 0 : Marshal.GetLastPInvokeError();
        bool fits = measured && OverlayGeometry.Contains(viewport,
            new(actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top));
        _diagnostics.Changed("fit.result", new { viewport, before, target, after = Bounds(actual), measured, code, fits, dpi = GetDpiForWindow(_window) });
        return fits;
    }

    internal bool HasFocus()
    {
        if (!IsOpen) return false;
        nint foreground = GetForegroundWindow();
        if (foreground == _window) return true;
        GetWindowThreadProcessId(foreground, out uint foregroundProcess);
        if (foregroundProcess == 0) return false;
        try
        {
            using Process process = Process.GetProcessById((int)foregroundProcess);
            return process.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    internal bool PointerOverClient()
    {
        if (!IsOpen || !GetCursorPos(out Point point)) return false;
        nint underPointer = WindowFromPoint(point);
        return underPointer != 0 && GetAncestor(underPointer, 2) == _window;
    }

    internal void KeepOnTop()
    {
        if (IsOpen)
        {
            bool ok = SetWindowPos(_window, (nint)(-1), 0, 0, 0, 0, 0x13);
            int code = ok ? 0 : Marshal.GetLastPInvokeError();
            _diagnostics.Changed("topmost", new { ok, code });
        }
    }

    private bool TryFocus(nint window)
    {
        bool foregroundSet = SetForegroundWindow(window);
        bool hasFocus = HasFocus();
        Trace("focus.direct", new { foregroundSet, hasFocus });
        if (foregroundSet || hasFocus) return true;
        nint foreground = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint currentThread = GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == currentThread)
        { Trace("focus.attach-skipped", new { foregroundThread, currentThread }); return false; }
        bool attached = AttachThreadInput(currentThread, foregroundThread, true);
        int attachCode = attached ? 0 : Marshal.GetLastPInvokeError();
        Trace("focus.attach", new { attached, code = attachCode, foregroundThread, currentThread });
        if (!attached) return false;
        try
        {
            foregroundSet = SetForegroundWindow(window);
            hasFocus = HasFocus();
            Trace("focus.attached-result", new { foregroundSet, hasFocus });
            return foregroundSet || hasFocus;
        }
        finally
        {
            bool detached = AttachThreadInput(currentThread, foregroundThread, false);
            int code = detached ? 0 : Marshal.GetLastPInvokeError();
            Trace("focus.detach", new { detached, code });
        }
    }

    internal void Restore()
    {
        nint window = _window;
        _window = 0;
        if (window == 0 || !IsWindow(window))
        { if (window != 0) Trace("restore.skipped", new { reason = "window-destroyed" }); return; }
        bool unpinned = SetWindowPos(window, (nint)(-2), 0, 0, 0, 0, 0x13);
        int unpinCode = unpinned ? 0 : Marshal.GetLastPInvokeError();
        if (!IsWindowVisible(window))
        {
            Trace("restore.skipped", new { reason = "application-hidden", unpinned, unpinCode });
            return;
        }
        WindowPlacement placement = _originalPlacement;
        bool restored = SetWindowPlacement(window, ref placement);
        int placementCode = restored ? 0 : Marshal.GetLastPInvokeError();
        Trace("restore.result", new { unpinned, unpinCode, restored, placementCode, state = WindowState(window) });
    }

    internal void CloseClient()
    {
        nint window = _window;
        Restore();
        if (window != 0 && IsWindow(window))
        {
            bool posted = PostMessage(window, 0x0010, 0, 0);
            int code = posted ? 0 : Marshal.GetLastPInvokeError();
            Trace("close.requested", new { posted, code });
        }
    }

    private FlydigiWindowCandidate? FindWindow(bool visibleOnly = false)
    {
        HashSet<uint> processes = [];
        foreach (Process process in Process.GetProcessesByName(ProcessName))
        {
            using (process) processes.Add((uint)process.Id);
        }
        List<FlydigiWindowCandidate> windows = [];
        List<object> candidates = [];
        int matchingWindows = 0;
        bool enumerated = EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint processId);
            if (!processes.Contains(processId)) return true;
            matchingWindows++;
            bool visible = IsWindowVisible(window), owned = GetWindow(window, 4) != 0;
            WindowPlacement placement = new() { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
            bool placementRead = GetWindowPlacement(window, ref placement);
            int placementCode = placementRead ? 0 : Marshal.GetLastPInvokeError();
            Rect rect = placement.NormalPosition;
            StringBuilder title = new(256);
            GetWindowText(window, title, title.Capacity);
            StringBuilder windowClass = new(256);
            bool classRead = GetClassName(window, windowClass, windowClass.Capacity) != 0;
            long style = GetWindowLongPtr(window, -16).ToInt64();
            int styleCode = style != 0 ? 0 : Marshal.GetLastPInvokeError();
            long extendedStyle = GetWindowLongPtr(window, -20).ToInt64();
            int extendedStyleCode = extendedStyle != 0 ? 0 : Marshal.GetLastPInvokeError();
            FlydigiWindowCandidate candidate = new(window, processId, visible, owned, placementRead, Bounds(rect),
                windowClass.ToString(), title.ToString(), style, extendedStyle, classRead && styleCode == 0 && extendedStyleCode == 0);
            windows.Add(candidate);
            string? rejected = FlydigiWindowSelection.RejectionReason(candidate);
            if (candidates.Count < 32) candidates.Add(new { hwnd = window.ToInt64(), processId, visible, owned, placementRead, placementCode,
                placement.ShowCommand, normal = Bounds(rect), titleMatches = candidate.TitleMatches, windowClass = candidate.WindowClass,
                style = style.ToString("X"), extendedStyle = extendedStyle.ToString("X"), styleCode, extendedStyleCode, classRead, rejected });
            return true;
        }, 0);
        int enumCode = enumerated ? 0 : Marshal.GetLastPInvokeError();
        FlydigiWindowCandidate? found = FlydigiWindowSelection.Select(windows.Where(window => !visibleOnly || window.Visible));
        _diagnostics.Changed("search.snapshot", new { processIds = processes.Order().ToArray(), matchingWindows,
            candidates, visibleOnly, truncated = matchingWindows > candidates.Count, selected = found?.Window.ToInt64() ?? 0, enumerated, enumCode }, repeatAfterMs: 2000);
        return found;
    }

    private string? FindExecutable()
    {
        foreach (var (rootName, root) in new[]
        {
            ("ProgramFiles", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            ("ProgramFilesX86", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            ("LocalApplicationData", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
        })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string path = Path.Combine(root, ProcessName, $"{ProcessName}.exe");
            bool exists = File.Exists(path);
            Trace("executable.probe", new { root = rootName, exists });
            if (exists) return path;
        }
        return null;
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        internal uint Length, Flags, ShowCommand;
        internal Point MinimumPosition, MaximumPosition;
        internal Rect NormalPosition;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint window, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(nint window, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AttachThreadInput(uint thread, uint attachTo, bool attach);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int horizontal, int vertical);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint window, int index);
}
