using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

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

    static NativeInputTarget ReadNativeInput(MainWindow window)
    {
        var focus = GetFocus();
        var thread = GetWindowThreadProcessId(focus, out var process);
        var owner = GetWindowThreadProcessId(window.Handle, out var ownerProcess);
        return new(window.Handle, focus, GetAncestor(focus, 2), GetForegroundWindow(), thread, process, owner, ownerProcess);
    }

    static InvalidOperationException NativeInputRefused(MainWindow window, string why) =>
        new($"Native input refused: {why}; {PostedInputTarget(window).Detail}; {ForegroundOwner(GetForegroundWindow())}");

    static nint VerifiedNativeTarget(MainWindow window)
    {
        var state = ReadNativeInput(window);
        if (!state.Ready) { throw NativeInputRefused(window, "foreground or focused child ownership was not verified"); }
        return state.Focus;
    }

    static async Task PrepareNativeInput(MainWindow window, DependencyObject? intended = null)
    {
        intended ??= FocusManager.GetFocusedElement(window.RootElement.XamlRoot) as DependencyObject;
        var before = $"{PostedInputTarget(window).Detail}; {ForegroundOwner(GetForegroundWindow())}";
        if (GetForegroundWindow() != window.Handle)
        {
            window.Activate();
            TrayIcon.SetForegroundWindow(window.Handle);
            if (!await Until(() => GetForegroundWindow() == window.Handle, TimeSpan.FromMilliseconds(400)))
            {
                var other = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                var current = GetCurrentThreadId();
                var attached = other != 0 && other != current && AttachThreadInput(current, other, true);
                AppLog.Write($"self-test: foreground fallback attached {attached}, Win32 error {Marshal.GetLastPInvokeError()}, from thread {current} to {other}");
                try { TrayIcon.SetForegroundWindow(window.Handle); }
                finally
                {
                    if (attached && !AttachThreadInput(current, other, false))
                    {
                        throw NativeInputRefused(window, $"could not detach foreground thread (Win32 error {Marshal.GetLastPInvokeError()})");
                    }
                }
                await Until(() => GetForegroundWindow() == window.Handle, TimeSpan.FromMilliseconds(400));
            }
        }
        var focus = GetFocus();
        if (GetForegroundWindow() == window.Handle && intended is not null
            && (focus == 0 || focus == window.Handle))
        {
            var held = new Windows.Foundation.TypedEventHandler<UIElement, BringIntoViewRequestedEventArgs>((_, e) => e.Handled = true);
            window.RootElement.BringIntoViewRequested += held;
            try
            {
                if (!(await FocusManager.TryFocusAsync(intended, FocusState.Programmatic)).Succeeded)
                {
                    throw NativeInputRefused(window, "the intended XAML element refused focus");
                }
            }
            finally { window.RootElement.BringIntoViewRequested -= held; }
        }
        var target = PostedTarget(window.Handle).Target;
        AppLog.Write($"self-test: native input before: {before}; after: {PostedInputTarget(window).Detail}; {CaptureNativeWheel(window, target, GetFocus(), GetForegroundWindow()).Detail}");
        VerifiedNativeTarget(window);
    }

    static void NativeInputSent(MainWindow window, nint target, string method) =>
        AppLog.Write($"self-test: {method} sent; {PostedInputTarget(window).Detail}; after: {CaptureNativeWheel(window, target, GetFocus(), GetForegroundWindow()).Detail}");

    static unsafe string ForegroundOwner(nint foreground)
    {
        var title = stackalloc char[512];
        var length = GetWindowTextW(foreground, title, 512);
        GetWindowThreadProcessId(foreground, out var pid);
        string process;
        try { using var owner = System.Diagnostics.Process.GetProcessById((int)pid); process = owner.ProcessName; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { process = $"unavailable ({e.Message})"; }
        return $"foreground owner: handle {foreground:X}; class {NativeWheelClass(foreground)}; title {System.Text.Json.JsonSerializer.Serialize(new string(title, 0, length))}; process {process} ({pid})";
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint from, uint to, [MarshalAs(UnmanagedType.Bool)] bool attach);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial int GetWindowTextW(nint hwnd, char* text, int maximum);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial int GetClassNameW(nint hwnd, char* name, int maximum);
}
