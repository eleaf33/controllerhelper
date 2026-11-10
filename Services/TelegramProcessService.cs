using System.Diagnostics;

namespace ControllerHelper.Services;

public static class TelegramProcessService
{
    public static async Task<int> CloseAsync()
    {
        using Process current = Process.GetCurrentProcess();
        int sessionId = current.SessionId;
        Process[] processes = Process.GetProcessesByName("Telegram");
        int closed = 0;
        try
        {
            foreach (Process process in processes)
            {
                if (process.SessionId != sessionId || process.HasExited) continue;
                process.CloseMainWindow();
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        using CancellationTokenSource killTimeout = new(TimeSpan.FromSeconds(5));
                        await process.WaitForExitAsync(killTimeout.Token);
                    }
                }
                closed++;
            }
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
        return closed;
    }
}
