using System.Runtime.InteropServices;

namespace AgentBridge.App;

static partial class SelfTest
{
    sealed record NativeWheelWindow(string Hwnd, string Class, string Parent, string Root, uint Thread, uint Process);
    sealed record NativeWheelChain(List<NativeWheelWindow> Windows, string End)
    {
        public string Detail => $"{string.Join(" -> ", Windows.Select(w => $"{w.Hwnd} [{w.Class}] (root: {w.Root}, thread: {w.Thread}, process: {w.Process})"))}; {End}";
    }

    sealed record NativeWheelSnapshot(string Window, NativeWheelChain Target, NativeWheelChain Focus,
        NativeWheelChain Foreground, bool TargetInWindow, bool ForegroundMatches, string XamlFocus)
    {
        public string Detail => $"window: {Window}; target chain: {Target.Detail}; native focus chain: {Focus.Detail}; foreground chain: {Foreground.Detail}; target in window: {TargetInWindow}; foreground matches: {ForegroundMatches}; XAML focus: {XamlFocus}";
    }

    static NativeWheelSnapshot CaptureNativeWheel(MainWindow window, nint target, nint focus, nint foreground) =>
        new($"{window.Handle:X}", NativeWheelAncestry(target), NativeWheelAncestry(focus), NativeWheelAncestry(foreground),
            GetAncestor(target, 2) == window.Handle, foreground == window.Handle, FocusedName(window));

    static NativeWheelChain NativeWheelAncestry(nint hwnd)
    {
        var windows = new List<NativeWheelWindow>();
        var seen = new HashSet<nint>();
        // A bad or changing native tree must not hang the self-test's failure reporting.
        while (hwnd != 0 && windows.Count < 16)
        {
            if (!seen.Add(hwnd)) { return new(windows, $"repeated HWND {hwnd:X}"); }
            var parent = GetAncestor(hwnd, 1);
            var thread = GetWindowThreadProcessId(hwnd, out var process);
            windows.Add(new($"{hwnd:X}", NativeWheelClass(hwnd), $"{parent:X}", $"{GetAncestor(hwnd, 2):X}", thread, process));
            hwnd = parent;
        }
        return new(windows, hwnd == 0 ? "end" : $"ancestry truncated at {hwnd:X}");
    }

    static unsafe string NativeWheelClass(nint hwnd)
    {
        var name = stackalloc char[256];
        var length = GetClassNameW(hwnd, name, 256);
        return length == 0 ? $"class unavailable (Win32 error {Marshal.GetLastPInvokeError()})" : new string(name, 0, length);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial int GetClassNameW(nint hwnd, char* name, int maximum);
}
