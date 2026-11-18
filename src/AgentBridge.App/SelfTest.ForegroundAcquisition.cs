using System.Runtime.InteropServices;

namespace AgentBridge.App;

static partial class SelfTest
{
    static void AcquireNativeForeground(nint window)
    {
        var current = GetCurrentThreadId();
        uint other = 0;
        var keys = new byte[256];
        bool saved = false, linked = false;
        var attachError = 0;
        NativeInputSetup.AcquireForeground(() => GetForegroundWindow() == window, () =>
        {
            other = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            if (other == 0 || other == current) { return false; }
            if (!(saved = GetKeyboardState(keys))) { throw new InvalidOperationException($"setup: keyboard capture before foreground attachment failed ({ForegroundOwner()})"); }
            linked = AttachThreadInput(current, other, true);
            attachError = linked ? 0 : Marshal.GetLastPInvokeError();
            return linked;
        }, () =>
        {
            AppLog.Write($"self-test: foreground attach {current}->{other}: {linked}; Win32 error {attachError}");
            var activated = TrayIcon.SetForegroundWindow(window);
            AppLog.Write($"self-test: foreground activation requested: {activated}");
        }, attached =>
        {
            // Attachment resets key state; detach and restore before reading the app's focused child.
            var detached = !attached || AttachThreadInput(current, other, false);
            var restored = !saved || SetKeyboardState(keys);
            AppLog.Write($"self-test: foreground release: detached {detached}; keyboard restored {restored}");
            if (!detached || !restored) { throw new InvalidOperationException($"setup: foreground release failed (detached {detached}; keyboard restored {restored}; {ForegroundOwner()})"); }
        });
    }

    static unsafe nint MinimizeForegroundTerminal()
    {
        var hostedArm64 = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (!hostedArm64) { return 0; }
        var foreground = GetForegroundWindow();
        char* text = stackalloc char[257];
        var length = GetClassNameW(foreground, text, 257);
        if (length == 0) { AppLog.Write($"self-test: terminal class lookup failed (Win32 error {Marshal.GetLastPInvokeError()}); {ForegroundOwner()}"); return 0; }
        var windowClass = new string(text, 0, length);
        GetWindowThreadProcessId(foreground, out var pid);
        if (pid == 0) { AppLog.Write($"self-test: terminal has no owning process; {ForegroundOwner()}"); return 0; }
        string process;
        try { using var owner = System.Diagnostics.Process.GetProcessById((int)pid); process = owner.ProcessName; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { AppLog.Write($"self-test: terminal owner lookup failed: {e.Message}; {ForegroundOwner()}"); return 0; }
        if (!NativeInputSetup.CanMinimizeTerminal(hostedArm64, windowClass, process)) { return 0; }
        GetWindowThreadProcessId(foreground, out var currentPid);
        if (currentPid != pid || GetForegroundWindow() != foreground) { return 0; }
        var requested = ShowWindowAsync(foreground, 6);
        AppLog.Write($"self-test: terminal minimize requested: {requested}; {ForegroundWindowState(foreground)}; {ForegroundOwner()}");
        return foreground;
    }

    static string ForegroundWindowState(nint window) => window == 0 ? "not attempted" :
        $"window: 0x{window:X}; exists: {IsWindow(window)}; visible: {IsWindowVisible(window)}; minimized: {IsIconic(window)}";

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint from, uint to, [MarshalAs(UnmanagedType.Bool)] bool attach);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(nint window, int command);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);
}
