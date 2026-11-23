using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AgentBridge.App;

// #3982 (Part of #2558): where a posted native wheel goes. On the hosted ARM64 installer the posted F24 key reaches
// the app, but the WM_MOUSEWHEEL posted to the same native focus window never arrives; on x64 it does. Diagnosis
// only: this records, every run, the window under the wheel's point, the cursor, the focus window and which window
// this thread actually took a 0x020A for. It decides nothing the wheel check decides. Window handles and class names
// only: no titles, so no machine or session names.
static unsafe partial class SelfTest
{
    static nint wheelHook;
    static int wheelTaken;
    static nint wheelTakenBy;

    // Before the wheel is posted, on the UI thread: WH_GETMESSAGE sees each message this thread removes from its queue.
    static void WatchWheel()
    {
        StopWatchingWheel();
        wheelTaken = 0;
        wheelTakenBy = 0;
        wheelHook = SetWindowsHookExW(3, (nint)(delegate* unmanaged[Stdcall]<int, nint, nint, nint>)&WheelHookProc, 0, GetCurrentThreadId());
    }

    static void StopWatchingWheel()
    {
        if (wheelHook != 0) { UnhookWindowsHookEx(wheelHook); wheelHook = 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint WheelHookProc(int code, nint wParam, nint lParam)
    {
        // wParam 1 is PM_REMOVE: counted once, when the thread takes the message off its queue.
        if (code >= 0 && wParam == 1 && ((NativeMessage*)lParam)->Message == 0x020A)
        {
            wheelTaken++;
            wheelTakenBy = ((NativeMessage*)lParam)->Window;
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    /// <summary>What a posted wheel met: where it was aimed, what was there, and what took it.</summary>
    sealed record WheelDiagnosis(string Window, string Focus, string FocusClass, string AtPoint, string AtPointClass,
        bool AtPointIsFocus, bool AtPointInWindow, int PointX, int PointY, int CursorX, int CursorY, int Taken, string TakenBy, string TakenClass)
    {
        public string Line => $"native wheel: window {Window}; focus {Focus} ({FocusClass}); at point ({PointX},{PointY}): {AtPoint} ({AtPointClass}), " +
            $"is focus: {AtPointIsFocus}, in window: {AtPointInWindow}; cursor ({CursorX},{CursorY}); taken: {Taken}x by {TakenBy} ({TakenClass})";
    }

    static WheelDiagnosis? lastWheel;

    // Right after the post: the focus the post went to is still the focus.
    static void DiagnoseWheel(nint window, int x, int y)
    {
        var focus = GetFocus();
        var at = WindowFromPoint(new WheelPoint { X = x, Y = y });
        GetCursorPosition(out var cursor);
        lastWheel = new(Hex(window), Hex(focus), ClassOf(focus), Hex(at), ClassOf(at), at == focus,
            at != 0 && GetAncestor(at, 2) == window, x, y, cursor.X, cursor.Y, 0, "", "");
    }

    // After the wheel check's waits: what the thread took, added to what was recorded at the post.
    static WheelDiagnosis? WheelDiagnosisNow()
    {
        var taken = lastWheel is null ? null : lastWheel with { Taken = wheelTaken, TakenBy = Hex(wheelTakenBy), TakenClass = ClassOf(wheelTakenBy) };
        StopWatchingWheel();
        if (taken is not null) { AppLog.Write($"self-test: {taken.Line}"); }
        return taken;
    }

    static string Hex(nint window) => window == 0 ? "none" : $"{window:X}";

    static string ClassOf(nint window)
    {
        if (window == 0) { return "none"; }
        var name = stackalloc char[128];
        var length = GetClassNameW(window, name, 128);
        return length > 0 ? new string(name, 0, length) : "unknown";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeMessage { public nint Window; public uint Message; public nint WParam; public nint LParam; public uint Time; public int X, Y; }

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowsHookExW(int hook, nint procedure, nint module, uint thread);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(WheelPoint point);

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPosition(out WheelPoint point);
}
