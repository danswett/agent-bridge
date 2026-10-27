// Spike S1: can a console-less .NET process (WinExe) deliver keystrokes into another
// process's console the way the PowerShell daemon does today?
//
// The daemon's sequence (agent-ha-bridge hooks/decision-inject.ps1, ConsoleInjector.Send):
//   FreeConsole -> AttachConsole(pid) -> CreateFile("CONIN$") -> WriteConsoleInputW(text)
//   -> pause -> WriteConsoleInputW(Enter) -> CloseHandle -> FreeConsole
// is reproduced here byte for byte, and then pushed on what a long-lived host would face.
//
// Every mode appends one JSON object per line to --out, because a WinExe has no stdout.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

static class Program
{
    static string outPath = Path.Combine(Path.GetTempPath(), "s1-injector.jsonl");

    static int Main(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts.TryGetValue("out", out var o)) { outPath = o; }
        var mode = args.Length > 0 ? args[0] : "";
        try
        {
            switch (mode)
            {
                case "send":
                    Emit(new { mode, result = Native.Send(uint.Parse(opts["pid"]), opts["text"], !opts.ContainsKey("no-submit"), int.Parse(opts.GetValueOrDefault("delay", "300"))) });
                    break;
                case "stress":
                    Stress(opts);
                    break;
                case "ctrl":
                    CtrlEvent(opts);
                    break;
                case "close":
                    CloseWhileAttached(opts);
                    break;
                case "children":
                    Children(opts);
                    break;
                default:
                    Emit(new { mode, error = "unknown mode" });
                    return 2;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Emit(new { mode, exception = ex.GetType().Name, message = ex.Message });
            return 1;
        }
    }

    // Alternates deliveries between several consoles: the host will serve many sessions
    // from one process, and a process can only be attached to one console at a time.
    static void Stress(Dictionary<string, string> opts)
    {
        var pids = opts["pids"].Split(',').Select(uint.Parse).ToArray();
        int count = int.Parse(opts.GetValueOrDefault("count", "50"));
        int delay = int.Parse(opts.GetValueOrDefault("delay", "60"));
        var failures = new List<string>();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            var pid = pids[i % pids.Length];
            var r = Native.Send(pid, $"stress-{i}-to-{pid}", true, delay);
            if (!r.StartsWith("ok:")) { failures.Add($"{i}:{pid}:{r}"); }
        }
        Emit(new { mode = "stress", count, elapsedMs = sw.ElapsedMilliseconds, failures, handles = Process.GetCurrentProcess().HandleCount });
    }

    // While attached, this process is part of the target console's process group, so a
    // Ctrl+C typed in that window (or any console control event) is delivered here too.
    // The default handler for a process without its own console is ExitProcess.
    static void CtrlEvent(Dictionary<string, string> opts)
    {
        uint pid = uint.Parse(opts["pid"]);
        var handler = opts.GetValueOrDefault("handler", "before");
        if (handler == "before") { Native.IgnoreControlEvents(); }
        if (handler == "null") { Native.IgnoreCtrlCAttribute(); }
        Emit(new { mode = "ctrl", phase = "before", handler });
        var r = Native.AttachAndRun(pid, () =>
        {
            if (handler == "after") { Native.IgnoreControlEvents(); }
            if (handler == "null-after") { Native.IgnoreCtrlCAttribute(); }
            Native.GenerateConsoleCtrlEvent(0 /* CTRL_C_EVENT */, 0);
            Thread.Sleep(1000);
        });
        Emit(new { mode = "ctrl", phase = "survived", handler, attach = r });
    }

    // The target's window is closed while this process is attached: Windows sends
    // CTRL_CLOSE_EVENT to every attached process and terminates each after its handler
    // returns. That is the case a person closing a session window at the moment a
    // phone reply is being typed into it would hit.
    static void CloseWhileAttached(Dictionary<string, string> opts)
    {
        uint pid = uint.Parse(opts["pid"]);
        var handler = opts.GetValueOrDefault("handler", "before");
        if (handler == "before") { Native.IgnoreControlEvents(); }
        Emit(new { mode = "close", phase = "before", handler });
        var r = Native.AttachAndRun(pid, () =>
        {
            if (handler == "after") { Native.IgnoreControlEvents(); }
            var hwnd = Native.GetConsoleWindow();
            Native.PostMessageW(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(8000);
        });
        Emit(new { mode = "close", phase = "survived", handler, attach = r, handlerCalls = Native.HandlerCalls });
    }

    // After an attach/free cycle the host has no console. A console child started
    // without CREATE_NO_WINDOW would then get a new, visible console window, which is
    // the focus-stealing flash Start-BridgeWindowlessProcess exists to prevent.
    static void Children(Dictionary<string, string> opts)
    {
        uint pid = uint.Parse(opts["pid"]);
        Native.Send(pid, "children-probe", true, 100);
        var psi = new ProcessStartInfo("cmd.exe", "/c echo windowless")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        Emit(new { mode = "children", output, exit = p.ExitCode, ownConsole = Native.GetConsoleWindow() != IntPtr.Zero });
    }

    static void Emit(object value)
    {
        File.AppendAllText(outPath, JsonSerializer.Serialize(value) + Environment.NewLine);
    }

    static Dictionary<string, string> ParseArgs(string[] args)
    {
        var d = new Dictionary<string, string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) { continue; }
            var key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { d[key] = args[++i]; }
            else { d[key] = "true"; }
        }
        return d;
    }
}

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteConsoleInputW(IntPtr h, INPUT_RECORD[] buffer, uint length, out uint written);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GenerateConsoleCtrlEvent(uint evt, uint group);
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleCtrlHandler(HandlerRoutine? handler, bool add);
    delegate bool HandlerRoutine(uint ctrlType);

    [StructLayout(LayoutKind.Sequential)]
    struct KEY_EVENT_RECORD
    {
        public int bKeyDown; // Win32 BOOL, 4 bytes
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD Key;
    }

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, OPEN_EXISTING = 3;
    const ushort KEY_EVENT = 1, VK_RETURN = 0x0D;

    static readonly object gate = new();
    static HandlerRoutine? keepAlive;

    public static int HandlerCalls;

    public static void IgnoreControlEvents()
    {
        keepAlive = type => { Interlocked.Increment(ref HandlerCalls); return true; };
        if (!SetConsoleCtrlHandler(keepAlive, true)) { throw new InvalidOperationException("SetConsoleCtrlHandler failed: " + Marshal.GetLastWin32Error()); }
    }

    // SetConsoleCtrlHandler(NULL, TRUE): the per-process "ignore Ctrl+C" attribute.
    public static void IgnoreCtrlCAttribute()
    {
        if (!SetConsoleCtrlHandler(null, true)) { throw new InvalidOperationException("SetConsoleCtrlHandler(null) failed: " + Marshal.GetLastWin32Error()); }
    }

    public static string AttachAndRun(uint pid, Action body)
    {
        lock (gate)
        {
            FreeConsole();
            if (!AttachConsole(pid)) { return "attach-failed:" + Marshal.GetLastWin32Error(); }
            try { body(); return "ok"; }
            finally { FreeConsole(); }
        }
    }

    // Byte-for-byte the daemon's ConsoleInjector.Send, behind a lock because one
    // process holds at most one console.
    public static string Send(uint pid, string text, bool submit, int submitDelayMs)
    {
        lock (gate)
        {
            FreeConsole();
            if (!AttachConsole(pid)) { return "attach-failed:" + Marshal.GetLastWin32Error(); }
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = CreateFileW("CONIN$", GENERIC_READ | GENERIC_WRITE, 1 | 2, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (handle == new IntPtr(-1)) { return "conin-failed:" + Marshal.GetLastWin32Error(); }
                var records = new List<INPUT_RECORD>();
                foreach (var c in text) { AddChar(records, c); }
                if (records.Count > 0)
                {
                    var buffer = records.ToArray();
                    if (!WriteConsoleInputW(handle, buffer, (uint)buffer.Length, out var written)) { return "write-failed:" + Marshal.GetLastWin32Error(); }
                    if (written != buffer.Length) { return $"partial:{written}/{buffer.Length}"; }
                }
                if (submit)
                {
                    if (submitDelayMs > 0) { Thread.Sleep(submitDelayMs); }
                    var enter = new List<INPUT_RECORD>();
                    AddChar(enter, '\r');
                    if (!WriteConsoleInputW(handle, enter.ToArray(), (uint)enter.Count, out _)) { return "enter-failed:" + Marshal.GetLastWin32Error(); }
                }
                return "ok:" + records.Count;
            }
            finally
            {
                if (handle != IntPtr.Zero && handle != new IntPtr(-1)) { CloseHandle(handle); }
                FreeConsole();
            }
        }
    }

    static void AddChar(List<INPUT_RECORD> records, char c)
    {
        for (int i = 0; i < 2; i++)
        {
            records.Add(new INPUT_RECORD
            {
                EventType = KEY_EVENT,
                Key = new KEY_EVENT_RECORD
                {
                    bKeyDown = i == 0 ? 1 : 0,
                    wRepeatCount = 1,
                    wVirtualKeyCode = c == '\r' ? VK_RETURN : (ushort)0,
                    UnicodeChar = c,
                },
            });
        }
    }
}
