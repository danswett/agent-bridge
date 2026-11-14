using System.Runtime.InteropServices;

namespace AgentBridge.App;

static partial class SelfTest
{
    static unsafe string ForegroundOwner()
    {
        var foreground = GetForegroundWindow();
        char* text = stackalloc char[257];
        var length = GetClassNameW(foreground, text, 257);
        var windowClass = length > 0 ? new string(text, 0, length) : $"unknown (Win32 error {Marshal.GetLastPInvokeError()})";
        length = GetWindowTextW(foreground, text, 257);
        var title = length > 0 ? new string(text, 0, length) : $"unknown (Win32 error {Marshal.GetLastPInvokeError()})";
        GetWindowThreadProcessId(foreground, out var pid);
        var process = "unknown (no owning process)";
        try { if (pid != 0) { using var owner = System.Diagnostics.Process.GetProcessById((int)pid); process = $"{owner.ProcessName} ({pid})"; } }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { process = $"unknown ({pid}: {e.Message})"; }
        return $"foreground: 0x{foreground:X}; {NativeInputSetup.Foreground(windowClass, title, process)}";
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial int GetWindowTextW(nint hwnd, char* text, int maximum);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial int GetClassNameW(nint hwnd, char* name, int maximum);
}
