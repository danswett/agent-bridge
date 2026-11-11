using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

static partial class SelfTest
{
    static void LatestFailure(MainWindow window, RichTextBlock? text = null)
    {
        var foreground = GetForegroundWindow();
        var focus = FocusManager.GetFocusedElement(window.RootElement.XamlRoot);
        AppLog.Write($"latest failure: target [{LatestWindowInfo(window.Handle)}]; foreground [{LatestWindowInfo(foreground)}]; XAML focus={focus?.GetType().Name}, textId={text?.GetHashCode()}, textFocused={text is not null && ReferenceEquals(focus, text)}, textChildren={(text is null ? "n/a" : VisualTreeHelper.GetChildrenCount(text).ToString())}, xamlCaretOwner=not-public");
    }

    static string LatestWindowInfo(nint hwnd)
    {
        var thread = GetWindowThreadProcessId(hwnd, out var pid);
        if (thread == 0) { return $"hwnd=0x{hwnd:X}, GetWindowThreadProcessId returned zero"; }
        var gui = new LatestGui { Size = (uint)Marshal.SizeOf<LatestGui>() };
        // WinUI's composition caret owner is private; hwndCaret describes only the Win32 caret.
        var state = GetGUIThreadInfo(thread, ref gui) ? $"focus=0x{gui.Focus:X}, win32CaretOwner=0x{gui.Caret:X}, flags={gui.Flags}" : $"GetGUIThreadInfo error={Marshal.GetLastWin32Error()}";
        string process;
        try { using var owner = Process.GetProcessById((int)pid); process = owner.ProcessName; }
        catch (ArgumentException) { process = "exited before lookup"; }
        catch (InvalidOperationException) { process = "exited during lookup"; }
        catch (System.ComponentModel.Win32Exception e) { process = $"unreadable error={e.NativeErrorCode}"; }
        return $"hwnd=0x{hwnd:X}, pid={pid}, process={process}, thread={thread}, desktop={LatestDesktop(GetThreadDesktop(thread))}, {state}";
    }

    static string LatestDesktop(nint desktop)
    {
        if (desktop == 0) { return $"GetThreadDesktop error={Marshal.GetLastWin32Error()}"; }
        var name = new StringBuilder(256);
        return GetUserObjectInformationW(desktop, 2, name, name.Capacity * sizeof(char), out var needed)
            ? name.ToString() : $"GetUserObjectInformation error={Marshal.GetLastWin32Error()}, bytesNeeded={needed}";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LatestGui
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, Menu, Move, Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool GetGUIThreadInfo(uint thread, ref LatestGui info);
    [DllImport("user32.dll", SetLastError = true)]
    static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder value, int bytes, out int needed);
}
