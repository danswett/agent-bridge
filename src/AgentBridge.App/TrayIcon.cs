using System.Runtime.InteropServices;

namespace AgentBridge.App;

// The app's icon in the notification area: it keeps running there with the window closed, so
// toasts keep coming. Click opens the window; the menu opens it or quits. Win32 directly
// (Shell_NotifyIcon on the window's own HWND): WinUI 3 has no tray icon of its own.
sealed unsafe partial class TrayIcon : IDisposable
{
    const uint CallbackMessage = 0x8000 + 0x41;
    const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    const uint NifMessage = 1, NifIcon = 2, NifTip = 4;
    const uint WmLButtonUp = 0x202, WmRButtonUp = 0x205, WmContextMenu = 0x7B;
    const uint MfString = 0, MfSeparator = 0x800;
    const uint TpmReturnCmd = 0x100, TpmRightButton = 2;
    const int CmdOpen = 1, CmdQuit = 2;

    static TrayIcon? current;
    readonly nint hwnd;
    readonly nint icon;
    readonly uint taskbarCreated;
    readonly Action open;
    readonly Action quit;
    string tip = "Agent Bridge";
    bool added;

    public TrayIcon(nint hwnd, string iconPath, Action open, Action quit)
    {
        this.hwnd = hwnd;
        this.open = open;
        this.quit = quit;
        icon = LoadImageW(0, iconPath, 1, GetSystemMetrics(49), GetSystemMetrics(50), 0x10);
        taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        current = this;
        SetWindowSubclass(hwnd, &Proc, 1, 0);
        Add();
    }

    public void SetTip(string text)
    {
        if (text == tip) { return; }
        tip = text;
        var data = Data(NifTip);
        Shell_NotifyIconW(NimModify, ref data);
    }

    void Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip);
        added = Shell_NotifyIconW(NimAdd, ref data);
    }

    NotifyIconData Data(uint flags)
    {
        var d = new NotifyIconData { Size = (uint)sizeof(NotifyIconData), Window = hwnd, Id = 1, Flags = flags, CallbackMessage = CallbackMessage, Icon = icon };
        var text = tip.Length > 127 ? tip[..127] : tip;
        for (var i = 0; i < text.Length; i++) { d.Tip[i] = text[i]; }
        return d;
    }

    [UnmanagedCallersOnly]
    static nint Proc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        var self = current;
        if (self is not null)
        {
            if (msg == CallbackMessage)
            {
                var mouse = (uint)(lParam & 0xFFFF);
                if (mouse == WmLButtonUp) { self.open(); return 0; }
                if (mouse is WmRButtonUp or WmContextMenu) { self.Menu(); return 0; }
            }
            else if (msg == self.taskbarCreated)
            {
                // Explorer restarted: the icon has to be added again.
                self.Add();
            }
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    void Menu()
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, MfString, CmdOpen, "Open Agent Bridge");
        AppendMenuW(menu, MfSeparator, 0, null);
        AppendMenuW(menu, MfString, CmdQuit, "Quit (the host keeps running)");
        GetCursorPos(out var at);
        // Without this the menu does not close when the person clicks elsewhere.
        SetForegroundWindow(hwnd);
        var cmd = TrackPopupMenu(menu, TpmReturnCmd | TpmRightButton, at.X, at.Y, 0, hwnd, 0);
        DestroyMenu(menu);
        if (cmd == CmdOpen) { open(); }
        else if (cmd == CmdQuit) { quit(); }
    }

    public void Dispose()
    {
        if (added)
        {
            var data = Data(0);
            Shell_NotifyIconW(NimDelete, ref data);
            added = false;
        }
        RemoveWindowSubclass(hwnd, &Proc, 1);
        if (icon != 0) { DestroyIcon(icon); }
        if (current == this) { current = null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadImageW(nint instance, string name, uint type, int cx, int cy, uint load);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
}
