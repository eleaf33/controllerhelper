using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ControllerHelper.Services;

internal sealed partial class FlydigiWindowService
{
    private static ScreenBounds Bounds(Rect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private Exception NativeFailure(string call, int code, string message)
    {
        Trace("native.failed", new { call, code });
        // Access denied is not a geometry failure. Do not infer elevation solely from
        // an unreadable token, or retry window manipulation with higher privileges.
        if (code == 5)
            message = ControllerHelper.Localization.Text.Get("FlydigiWindowService_Diagnostics_096");
        return code != 0 ? new Win32Exception(code, message) : new InvalidOperationException(message);
    }

    private static object LaunchProcessState(Process? process)
    {
        if (process is null) return new { available = false };
        try
        {
            bool exited = process.HasExited;
            return new { available = true, exited, exitCode = exited ? (int?)process.ExitCode : null };
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        { return new { available = false, exceptionType = error.GetType().Name }; }
    }

    private static object WindowState(nint window)
    {
        bool alive = window != 0 && IsWindow(window);
        if (!alive) return new { hwnd = window.ToInt64(), alive };
        GetWindowThreadProcessId(window, out uint pid);
        bool measured = GetWindowRect(window, out var rect);
        int rectCode = measured ? 0 : Marshal.GetLastPInvokeError();
        int dwmResult = DwmGetWindowAttribute(window, 14, out uint cloaked, sizeof(uint));
        return new { hwnd = window.ToInt64(), alive, processId = pid, visible = IsWindowVisible(window), minimized = IsIconic(window),
            measured, rectCode, bounds = Bounds(rect), dpi = GetDpiForWindow(window), dwmResult, cloaked = dwmResult == 0 ? (uint?)cloaked : null,
            foreground = GetForegroundWindow() == window };
    }

    private void LogProcessAccess(uint pid, string role)
    {
        bool sessionRead = ProcessIdToSessionId(pid, out uint session);
        int sessionCode = sessionRead ? 0 : Marshal.GetLastPInvokeError();
        nint process = OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION only
        int processCode = process != 0 ? 0 : Marshal.GetLastPInvokeError();
        nint token = 0;
        try
        {
            bool tokenRead = process != 0 && OpenProcessToken(process, 0x8, out token); // TOKEN_QUERY
            int? tokenCode = process == 0 ? null : tokenRead ? 0 : Marshal.GetLastPInvokeError();
            uint elevated = 0;
            bool elevationRead = tokenRead && GetTokenInformation(token, 20, out elevated, sizeof(uint), out _);
            int? elevationCode = !tokenRead ? null : elevationRead ? 0 : Marshal.GetLastPInvokeError();
            Trace("process.access", new { role, processId = pid, sessionRead, session, sessionCode, processCode, tokenRead, tokenCode,
                elevated = elevationRead ? (bool?)(elevated != 0) : null, elevationCode, restricted = tokenRead ? (bool?)IsTokenRestricted(token) : null });
        }
        finally { if (token != 0) CloseHandle(token); if (process != 0) CloseHandle(process); }
    }

    private void LogRelatedProcesses()
    {
        // Only Flydigi-named processes are described. No paths, command lines or titles.
        List<object> related = [];
        try
        {
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        string name = process.ProcessName;
                        if (name.Contains("Flydigi", StringComparison.OrdinalIgnoreCase) && related.Count < 16)
                            related.Add(new { processId = process.Id, name, exactName = name.Equals(ProcessName, StringComparison.OrdinalIgnoreCase) });
                    }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException) { }
                }
            }
            Trace("process.related", new { related });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
        { Trace("process.related-unavailable", new { exceptionType = error.GetType().Name }); }
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out uint value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int informationClass, out uint information, int size, out uint returned);
    [DllImport("advapi32.dll")] private static extern bool IsTokenRestricted(nint token);
}
