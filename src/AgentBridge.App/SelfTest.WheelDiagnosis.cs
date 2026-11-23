using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AgentBridge.App;

// #3982 (Part of #2558): the self-test's native wheel. On the hosted ARM64 installer the posted F24 key reaches the app but a WM_MOUSEWHEEL
// posted to the focus window never does; on x64 it does. Posting to the window under the pointer (the DesktopChildSiteBridge) was tried and
// the bridge takes the message without the app ever seeing a PointerWheelChanged, so the wheel is now a real OS wheel (SendInput
// MOUSEEVENTF_WHEEL) at the list's centre, the one input that goes where a person's wheel goes. Only on a CI runner (GITHUB_ACTIONS): it
// moves the cursor (restored afterwards) and needs the foreground, so on anyone's machine the check says it was skipped and why.
// Every run records the window under the point, the focus window, which window this thread took the wheel for, and whether the app heard it.
// Window handles and class names only: no titles, so no machine or session names.
static unsafe partial class SelfTest
{
    static nint wheelHook;
    static int wheelTaken;
    static nint wheelTakenBy;
    static uint wheelTakenMessage;
    static WheelPoint? wheelCursor;

    // Null on a CI runner; otherwise why the real wheel is not sent. A skipped check is said in its message, never a silent pass.
    static string? RealWheelSkip() =>
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? null
        : "a real OS wheel moves the cursor and needs the foreground, so it is sent only on CI runners (GITHUB_ACTIONS)";

    // Before the wheel is sent, on the UI thread: WH_GETMESSAGE sees each message this thread removes from its queue.
    static void WatchWheel()
    {
        StopWatchingWheel();
        wheelTaken = 0;
        wheelTakenBy = 0;
        wheelTakenMessage = 0;
        wheelHook = SetWindowsHookExW(3, (nint)(delegate* unmanaged[Stdcall]<int, nint, nint, nint>)&WheelHookProc, 0, GetCurrentThreadId());
    }

    // The hook goes and the cursor comes back to where the person (or the runner) had it.
    static void StopWatchingWheel()
    {
        if (wheelHook != 0) { UnhookWindowsHookEx(wheelHook); wheelHook = 0; }
        if (wheelCursor is { } was) { wheelCursor = null; SetCursorPosition(was.X, was.Y); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint WheelHookProc(int code, nint wParam, nint lParam)
    {
        // wParam 1 is PM_REMOVE: counted once, when the thread takes the message off its queue. 0x020A is WM_MOUSEWHEEL, 0x024E WM_POINTERWHEEL.
        var message = code >= 0 && wParam == 1 ? ((NativeMessage*)lParam)->Message : 0;
        if (message is 0x020A or 0x024E)
        {
            wheelTaken++;
            wheelTakenBy = ((NativeMessage*)lParam)->Window;
            wheelTakenMessage = message;
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    // A real wheel notch up (120) at the screen point: the cursor moved there first (restored in StopWatchingWheel), the foreground the
    // keys need already held (PrepareNativeInput), then SendInput, as a mouse's wheel is.
    static void SendRealWheel(nint window, int x, int y)
    {
        PrepareNativeInput(window);
        if (!GetCursorPosition(out var was)) { throw new InvalidOperationException("The cursor could not be read."); }
        wheelCursor = was;
        if (!SetCursorPosition(x, y)) { throw new InvalidOperationException("The cursor could not be moved to the wheel target."); }
        var input = new NativeInput { Type = 0, Mouse = new NativeMouse { Data = 120, Flags = 0x0800 } };
        if (SendInput(1, ref input, Marshal.SizeOf<NativeInput>()) != 1) { throw new InvalidOperationException("The wheel could not be sent."); }
    }

    /// <summary>What the wheel met: where it was sent, what was there, what took it and whether the app heard it.</summary>
    sealed record WheelDiagnosis(string Mode, string Window, string Focus, string FocusClass, string AtPoint, string AtPointClass,
        bool AtPointIsFocus, bool AtPointInWindow, int PointX, int PointY, int CursorX, int CursorY, int Taken, string TakenBy, string TakenClass,
        string TakenMessage, int? Received)
    {
        public string Line => $"native wheel ({Mode}): window {Window}; focus {Focus} ({FocusClass}); at point ({PointX},{PointY}): {AtPoint} ({AtPointClass}), " +
            $"is focus: {AtPointIsFocus}, in window: {AtPointInWindow}; cursor ({CursorX},{CursorY}); taken: {Taken}x ({TakenMessage}) by {TakenBy} ({TakenClass}); " +
            $"app heard it: {(Received is { } count ? $"yes (history {count})" : "no")}";
    }

    static WheelDiagnosis? lastWheel;

    // Right after the wheel is sent: the cursor is at the point, the focus is the focus the wheel found.
    static void DiagnoseWheel(nint window, int x, int y)
    {
        var focus = GetFocus();
        var at = WindowFromPoint(new WheelPoint { X = x, Y = y });
        GetCursorPosition(out var cursor);
        lastWheel = new("SendInput", Hex(window), Hex(focus), ClassOf(focus), Hex(at), ClassOf(at), at == focus,
            at != 0 && GetAncestor(at, 2) == window, x, y, cursor.X, cursor.Y, 0, "", "", "", null);
    }

    // After the wheel check's waits: what the thread took and whether the app heard it, added to what was recorded at the send.
    static WheelDiagnosis? WheelDiagnosisNow(int? received)
    {
        var taken = lastWheel is null ? null : lastWheel with
        {
            Taken = wheelTaken, TakenBy = Hex(wheelTakenBy), TakenClass = ClassOf(wheelTakenBy), TakenMessage = wheelTakenMessage == 0 ? "none" : $"0x{wheelTakenMessage:X}", Received = received,
        };
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

    // INPUT with a mouse event: the union is as wide as its largest member, so its alignment (8 on 64-bit) puts it at offset 8.
    [StructLayout(LayoutKind.Sequential)]
    struct NativeMouse { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeInput { public uint Type; public NativeMouse Mouse; }

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

    [LibraryImport("user32.dll", EntryPoint = "SetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPosition(int x, int y);

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, ref NativeInput input, int size);
}
