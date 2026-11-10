using System.Diagnostics;
using Microsoft.Win32;

namespace ControllerHelper.Services;

public static class SteamBigPictureService
{
    public static void EnsureAvailable()
    {
        using RegistryKey? registration = Registry.ClassesRoot.OpenSubKey(@"steam\shell\open\command");
        if (registration?.GetValue(null) is not string command || string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("Steam не зарегистрирован в Windows. Установите или запустите клиент Steam.");
    }

    public static void Open() => Send("steam://open/bigpicture");

    public static void Close()
    {
        Process[] clients = Process.GetProcessesByName("steam");
        try
        {
            if (clients.Length > 0) Send("steam://close/bigpicture");
        }
        finally
        {
            foreach (Process client in clients) client.Dispose();
        }
    }

    private static void Send(string uri)
    {
        EnsureAvailable();
        using Process? process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }
}
