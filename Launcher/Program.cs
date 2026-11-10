using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string message, string title, uint flags);

    [STAThread]
    private static int Main(string[] args)
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string application = Path.Combine(root, "lib", "ControllerHelper.exe");
        string logPath = Path.Combine(root, "logs", "launcher.log");

        try
        {
            // Report an unwritable package explicitly instead of silently losing diagnostics.
            WriteLog(logPath, "launcher.start");
            if (!File.Exists(application))
                throw new FileNotFoundException("Не найден основной исполняемый файл приложения.", application);

            var start = new ProcessStartInfo(application);
            start.WorkingDirectory = Path.GetDirectoryName(application);
            start.UseShellExecute = false;
            start.Arguments = JoinArguments(args);
            using (Process process = Process.Start(start))
            {
                if (process == null)
                    throw new InvalidOperationException("Не удалось запустить приложение.");
                process.WaitForExit();
                WriteLog(logPath, "application.exit code=" + process.ExitCode);
                return process.ExitCode;
            }
        }
        catch (Exception error)
        {
            try { WriteLog(logPath, "launcher.failure type=" + error.GetType().FullName + " hresult=" + error.HResult); }
            catch { /* Keep the original error visible if logs are not writable. */ }
            MessageBox(IntPtr.Zero, error.Message, "ControllerHelper", 0x10);
            return 1;
        }
    }

    private static void WriteLog(string path, string message)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        // FileShare.Read allows inspection while excluding other writers.
        // The launcher log is independent from the application's JSONL journal.
        if (File.Exists(path) && new FileInfo(path).Length >= 256 * 1024)
        {
            string backup = path + ".1";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(path, backup);
        }
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream, Encoding.UTF8))
            writer.WriteLine(DateTimeOffset.Now.ToString("O") + " pid=" + Process.GetCurrentProcess().Id + " " + message);
    }

    private static string JoinArguments(string[] args)
    {
        var result = new StringBuilder();
        foreach (string arg in args)
        {
            if (result.Length != 0) result.Append(' ');
            result.Append('"');
            int backslashes = 0;
            foreach (char character in arg)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                }
                else
                {
                    result.Append('\\', backslashes);
                    result.Append(character);
                }
                backslashes = 0;
            }
            result.Append('\\', backslashes * 2);
            result.Append('"');
        }
        return result.ToString();
    }
}
