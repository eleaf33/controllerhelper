using System.Runtime.InteropServices;
using ControllerHelper.Services;
using WinRT.Interop;
using Microsoft.UI.Xaml;

namespace ControllerHelper.Native;

internal sealed class TrayIcon : IDisposable
{
    private const uint TrayMessage = 0x8000 + 42;
    private const uint IconId = 1;
    private const uint IconMessage = 1;
    private const uint NotifyMessage = 2;
    private const uint TipMessage = 4;
    private readonly nint _handle;
    private readonly SubclassProcedure _procedure;
    private readonly uint _taskbarCreated;
    private readonly Action _show;
    private readonly Action _tv;
    private readonly Action _pc;
    private readonly Action _quit;
    private readonly nint _ownedIcon;
    private NotifyIconData _data;

    internal TrayIcon(Window window, Action show, Action tv, Action pc, Action quit)
    {
        _handle = WindowNative.GetWindowHandle(window);
        _show = show;
        _tv = tv;
        _pc = pc;
        _quit = quit;
        _procedure = HandleMessage;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        string iconPath = Path.Combine(AppContext.BaseDirectory, "controller.ico");
        _ownedIcon = File.Exists(iconPath) ? LoadImage(0, iconPath, 1, 0, 0, 0x10 | 0x40) : 0;
        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = _handle,
            Id = IconId,
            Flags = IconMessage | NotifyMessage | TipMessage,
            CallbackMessage = TrayMessage,
            Icon = _ownedIcon != 0 ? _ownedIcon : LoadIcon(0, (nint)32512),
            Tip = "ControllerHelper",
            Info = string.Empty,
            InfoTitle = string.Empty
        };
        if (!SetWindowSubclass(_handle, _procedure, (nuint)1, 0))
        {
            if (_ownedIcon != 0) DestroyIcon(_ownedIcon);
            throw new InvalidOperationException("Не удалось подключить значок к окну.");
        }
        if (!ShellNotifyIcon(0, ref _data))
        {
            StartupTrace.Write($"Shell_NotifyIcon failed: {Marshal.GetLastWin32Error()}, icon={_data.Icon}, custom={_ownedIcon != 0}");
            RemoveWindowSubclass(_handle, _procedure, (nuint)1);
            if (_ownedIcon != 0) DestroyIcon(_ownedIcon);
            throw new InvalidOperationException("Не удалось добавить значок в трей.");
        }
    }

    private nint HandleMessage(nint window, uint message, nuint wParam, nint lParam, nuint subclassId, nuint data)
    {
        if (message == _taskbarCreated) ShellNotifyIcon(0, ref _data);
        if (message == TrayMessage)
        {
            uint action = unchecked((uint)lParam.ToInt64()) & 0xffff;
            if (action is 0x203 or 0x202) _show();
            if (action is 0x205 or 0x7b) ShowMenu();
            return 0;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenu(menu, 0, (nuint)1, "Настройки");
            AppendMenu(menu, 0, (nuint)2, "Играть на ТВ");
            AppendMenu(menu, 0, (nuint)3, "Вернуться к ПК");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, (nuint)4, "Выход");
            GetCursorPos(out CursorPoint point);
            SetForegroundWindow(_handle);
            uint choice = TrackPopupMenu(menu, 0x102, point.X, point.Y, 0, _handle, 0);
            switch (choice)
            {
                case 1: _show(); break;
                case 2: _tv(); break;
                case 3: _pc(); break;
                case 4: _quit(); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        ShellNotifyIcon(2, ref _data);
        RemoveWindowSubclass(_handle, _procedure, (nuint)1);
        if (_ownedIcon != 0) DestroyIcon(_ownedIcon);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProcedure callback, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint action, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern nint LoadIcon(nint instance, nint resource);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint owner, nint rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
