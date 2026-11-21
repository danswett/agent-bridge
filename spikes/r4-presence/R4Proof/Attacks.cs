using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace R4Proof;

// Each attack acts only on the prompt this run asked for: a window that appeared after the call
// began, in CredentialUIBroker or titled Windows Security. Keys go only while that prompt has the
// foreground and clicks only where it is under the cursor; otherwise nothing is sent and the step is
// inconclusive. Names and titles stay out of the results (they may show the person's account).
public static unsafe class Attacks
{
    public static Func<Prompt?> Arm()
    {
        var before = Windows().ToHashSet();
        return () =>
        {
            var seen = "";
            for (var i = 0; i < 50; i++, Thread.Sleep(200))
            {
                var fresh = Windows().Where(w => !before.Contains(w) && Pid(w) != Environment.ProcessId).ToList();
                seen = string.Join(", ", fresh.Select(Describe));
                var hit = fresh.Where(w => Process(w) == "CredentialUIBroker" || Title(w).Contains("Windows Security")).ToList();
                if (hit.Count == 1) { return new(hit[0], Pid(hit[0]), Describe(hit[0])); }
            }
            return new(0, 0, seen.Length == 0 ? "no new window" : "no single match among: " + seen);
        };
    }

    public static nint ForegroundWindow() => GetForegroundWindow();

    public static IReadOnlyList<Attack> All(string injector) =>
    [
        new("silent-uv-required", "WebAuthNAuthenticatorGetAssertion, UV required, right after the person's assertion; nothing touched", false, false, true, _ => (true, "nothing sent")),
        new("silent-uv-discouraged", "the same with UV discouraged", false, false, false, _ => (true, "nothing sent")),
        new("sendinput-keys", "SendInput Esc, then Alt+F4, with the prompt in the foreground", true, true, true, p => Keys(p!)),
        new("sendinput-mouse", "SendInput left click on the prompt's Cancel button (or its centre)", true, true, true, p => Click(p!)),
        new("uia", "UI Automation: list the prompt's controls, Invoke its Cancel button", true, true, true, p => Uia(p!)),
        new("injector-console", "agent-bridge-injector types Esc and Enter into this harness's console", true, true, true, _ => Inject(injector, Environment.ProcessId)),
        new("injector-prompt", "agent-bridge-injector aimed at the prompt's process", true, true, true, p => Inject(injector, p!.Pid)),
        new("minimise", "ShowWindow(SW_MINIMIZE) on the prompt, then wait for a signature", true, false, true, p => (true, $"ShowWindow={ShowWindow(p!.Hwnd, 6)}")),
        new("close", "PostMessage(WM_CLOSE) to the prompt, then wait for a signature", true, false, true, p => (true, $"PostMessage={PostMessageW(p!.Hwnd, 0x10, 0, 0)} err={Marshal.GetLastPInvokeError()}")),
    ];

    static (bool, string) Keys(Prompt p)
    {
        if (!Foreground(p)) { return (false, "prompt not in the foreground; no keys sent"); }
        var esc = Send([Key(0x1B, 0), Key(0x1B, 2)]);
        Thread.Sleep(1500);
        if (!Foreground(p)) { return (true, $"Esc sent={esc}; prompt no longer foreground, Alt+F4 not sent"); }
        return (true, $"Esc sent={esc}, Alt+F4 sent={Send([Key(0x12, 0), Key(0x73, 0), Key(0x73, 2), Key(0x12, 2)])}");
    }

    static (bool, string) Click(Prompt p)
    {
        GetWindowRect(p.Hwnd, out var r);
        var at = CancelButton(p) is { } b ? ((int)(b.X + b.Width / 2), (int)(b.Y + b.Height / 2), "Cancel") : ((r.L + r.R) / 2, (r.T + r.B) / 2, "centre");
        GetCursorPos(out var old);
        SetCursorPos(at.Item1, at.Item2);
        if (Pid(WindowFromPoint(new() { X = at.Item1, Y = at.Item2 })) != p.Pid) { SetCursorPos(old.X, old.Y); return (false, "prompt not under the cursor; no click sent"); }
        Input down = new() { Type = 0, MouseFlags = 2 }, up = new() { Type = 0, MouseFlags = 4 };
        var sent = Send([down, up]);
        SetCursorPos(old.X, old.Y);
        return (true, $"click on {at.Item3} sent={sent}");
    }

    static (bool, string) Uia(Prompt p)
    {
        var all = AutomationElement.FromHandle(p.Hwnd).FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToList();
        var kinds = string.Join(" ", all.GroupBy(e => e.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")).Select(g => $"{g.Key}x{g.Count()}"));
        var buttons = string.Join("|", all.Where(e => e.Current.ControlType == ControlType.Button).Select(e => e.Current.Name));
        var cancel = all.FirstOrDefault(IsCancel);
        if (cancel?.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke) != true) { return (true, $"UIA saw {all.Count} elements ({kinds}); buttons [{buttons}]; no invokable Cancel"); }
        ((InvokePattern)invoke).Invoke();
        return (true, $"UIA saw {all.Count} elements ({kinds}); buttons [{buttons}]; Invoke on Cancel returned");
    }

    static (bool, string) Inject(string injector, int pid)
    {
        var start = new ProcessStartInfo(injector, pid.ToString()) { RedirectStandardInput = true, RedirectStandardOutput = true };
        using var run = System.Diagnostics.Process.Start(start)!;
        run.StandardInput.Write("agent-bridge-keys 1\nsend " + Convert.ToBase64String("\u001b"u8) + " 1 50 0\n");
        run.StandardInput.Close();
        var said = run.StandardOutput.ReadToEnd().Trim().Replace('\n', ';');
        run.WaitForExit(10_000);
        while (pid == Environment.ProcessId && Console.KeyAvailable) { Console.ReadKey(true); }
        return (true, $"{System.IO.Path.GetFileName(injector)} {(pid == Environment.ProcessId ? "own console" : "prompt pid")}: exit {run.ExitCode}, said '{said}'");
    }

    static bool IsCancel(AutomationElement e) => e.Current.ControlType == ControlType.Button && e.Current.Name == "Cancel";
    static System.Windows.Rect? CancelButton(Prompt p) =>
        AutomationElement.FromHandle(p.Hwnd).FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().FirstOrDefault(IsCancel)?.Current.BoundingRectangle;

    static bool Foreground(Prompt p) => Pid(GetForegroundWindow()) == p.Pid || (SetForegroundWindow(p.Hwnd) && Pid(GetForegroundWindow()) == p.Pid);
    static Input Key(ushort vk, uint flags) => new() { Type = 1, Vk = vk, KeyFlags = flags };
    static uint Send(Input[] inputs) { fixed (Input* i = inputs) { return SendInput((uint)inputs.Length, i, sizeof(Input)); } }

    static List<nint> Windows()
    {
        var list = new List<nint>();
        var h = GCHandle.Alloc(list);
        EnumWindows(&Collect, GCHandle.ToIntPtr(h));
        h.Free();
        return list;
    }

    [UnmanagedCallersOnly]
    static int Collect(nint w, nint state) { if (IsWindowVisible(w)) { ((List<nint>)GCHandle.FromIntPtr(state).Target!).Add(w); } return 1; }

    static int Pid(nint w) { GetWindowThreadProcessId(w, out var pid); return (int)pid; }
    static string Process(nint w) { try { return System.Diagnostics.Process.GetProcessById(Pid(w)).ProcessName; } catch (ArgumentException) { return "?"; } }
    static string Title(nint w) { var b = new StringBuilder(256); GetWindowTextW(w, b, 256); return b.ToString(); }
    static string Describe(nint w) { var b = new StringBuilder(256); GetClassNameW(w, b, 256); return $"{Process(w)}/{b}"; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Vk;
        [FieldOffset(12)] public uint KeyFlags;
        [FieldOffset(20)] public uint MouseFlags;
    }
    struct Rect { public int L, T, R, B; }
    struct Point { public int X, Y; }

    [DllImport("user32.dll")] static extern int EnumWindows(delegate* unmanaged<nint, nint, int> f, nint state);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint w);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint w, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(nint w, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(nint w, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint w);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, Input* inputs, int size);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint w, out Rect r);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(Point p);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint w, int cmd);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessageW(nint w, uint msg, nint wp, nint lp);
}
