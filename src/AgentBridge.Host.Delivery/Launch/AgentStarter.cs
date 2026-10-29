using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AgentBridge.Host.Delivery.Windows;

namespace AgentBridge.Host.Delivery.Launch;

// What to start: the program, its arguments, and where.
public sealed record StartSpec(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory);

// A started process, by id and start time (which tells it from a later one given the same id),
// or why nothing started.
public sealed record Started(int ProcessId, DateTimeOffset StartedAt);

// Starts an agent in a console window of its own on the person's desktop. The real one is
// WindowsAgentStarter; tests use a fake, so no test ever starts an agent.
public interface IAgentStarter
{
    // Null when this host can start agents here now; otherwise why it cannot.
    string? Unavailable();

    // The command line it would run, for the length check and the log; null with a problem.
    string? CommandLine(StartSpec spec, out string? problem);

    Started Start(StartSpec spec);

    // Whether the process started is still the one running under its id.
    bool Running(Started started);

    // Whether a process is the one started or runs under it (an npm shim's cmd.exe starts node,
    // which may start the agent's own binary); null when that cannot be told, as when the
    // process started has gone.
    bool? Owns(Started started, int processId) => null;
}

// Exactly as agent-ha-bridge starts one on Windows (Start-BridgeCopilotSession: Start-Process,
// which is CreateProcess with CREATE_NEW_CONSOLE): a visible console of the agent's own, in
// the working directory, with this host's environment. Whatever the user has as the default
// terminal (Windows Terminal or the console host) shows it. The window is deliberately
// visible: a session started from a phone is still one a person can walk over to and take
// over at the keyboard.
//
// The agent inherits this process's token, desktop and environment, so this host must itself
// run as the user, unelevated, on the interactive desktop. Started from Explorer, a shortcut
// or a logon task it is; started elevated, as a service or over SSH it is not, and then it
// starts nothing (Unavailable) rather than putting an administrator's agent, or one nobody
// can see, on the machine.
[SupportedOSPlatform("windows")]
public sealed class WindowsAgentStarter(bool hidden = false) : IAgentStarter
{
    public string? Unavailable()
    {
        var level = TargetGuard.IntegrityLevel(Environment.ProcessId);
        if (level < 0) { return "the host cannot check whether it runs as administrator, so it does not start sessions"; }
        if (level >= 0x3000) { return "the host is running as administrator, and a session it started would be too; start the host unelevated (from Explorer or a shortcut) to launch sessions"; }
        if (!Environment.UserInteractive || !ProcessIdToSessionId((uint)Environment.ProcessId, out var session) || session == 0)
        {
            return "the host is not running on your desktop (a service or a remote shell), so a session it started could not be seen; start the host from your desktop to launch sessions";
        }
        if (WindowStation() is not "WinSta0") { return "the host is not running on the interactive desktop, so a session it started could not be seen; start the host from your desktop to launch sessions"; }
        return null;
    }

    public string? CommandLine(StartSpec spec, out string? problem) => WindowsCommandLine.For(spec.Executable, spec.Arguments, out problem)?.CommandLine;

    public Started Start(StartSpec spec)
    {
        var line = WindowsCommandLine.For(spec.Executable, spec.Arguments, out var problem) ?? throw new InvalidOperationException(problem);
        var info = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = StartfUseShowWindow, ShowWindow = hidden ? SwHide : SwShowNormal };
        var command = new StringBuilder(line.CommandLine, line.CommandLine.Length + 1);
        if (!CreateProcessW(line.Application, command, 0, 0, false, CreateNewConsole | CreateUnicodeEnvironment, 0, spec.WorkingDirectory, ref info, out var process))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            var startedAt = GetProcessTimes(process.Process, out var created, out _, out _, out _)
                ? DateTimeOffset.FromFileTime(created)
                : DateTimeOffset.UtcNow;
            return new Started(process.ProcessId, startedAt.ToUniversalTime());
        }
        finally
        {
            CloseHandle(process.Thread);
            CloseHandle(process.Process);
        }
    }

    public bool Running(Started started)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)started.ProcessId);
        if (process == 0) { return false; }
        try
        {
            if (!GetExitCodeProcess(process, out var code) || code != StillActive) { return false; }
            // A recycled id is another process: only the one started at that moment counts.
            return !GetProcessTimes(process, out var created, out _, out _, out _)
                || Math.Abs((DateTimeOffset.FromFileTime(created).ToUniversalTime() - started.StartedAt).TotalMilliseconds) < 1000;
        }
        finally { CloseHandle(process); }
    }

    public bool? Owns(Started started, int processId)
    {
        if (!Running(started)) { return null; }
        var parents = Parents();
        if (parents is null) { return null; }
        var current = processId;
        var created = CreatedAt(current);
        for (var depth = 0; depth < 16; depth++)
        {
            if (current == started.ProcessId) { return true; }
            if (!parents.TryGetValue(current, out var parent) || parent == 0 || parent == current) { return false; }
            // An id recycled since: a parent cannot have started after its child.
            var parentCreated = CreatedAt(parent);
            if (created is { } c && parentCreated is { } pc && pc > c) { return false; }
            (current, created) = (parent, parentCreated);
        }
        return false;
    }

    // Every process's parent, from one snapshot.
    static Dictionary<int, int>? Parents()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == -1) { return null; }
        try
        {
            var map = new Dictionary<int, int>();
            var entry = new ProcessEntry32 { Size = Marshal.SizeOf<ProcessEntry32>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                map[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
            return map;
        }
        finally { CloseHandle(snapshot); }
    }

    static DateTimeOffset? CreatedAt(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == 0) { return null; }
        try { return GetProcessTimes(process, out var created, out _, out _, out _) ? DateTimeOffset.FromFileTime(created).ToUniversalTime() : null; }
        finally { CloseHandle(process); }
    }

    static string? WindowStation()
    {
        var station = GetProcessWindowStation();
        if (station == 0) { return null; }
        var buffer = new char[256];
        return GetUserObjectInformationW(station, UoiName, buffer, buffer.Length * 2, out var needed) ? new string(buffer, 0, Math.Max(0, needed / 2 - 1)) : null;
    }

    const int StartfUseShowWindow = 0x1;
    const short SwHide = 0;
    const short SwShowNormal = 1;
    const uint CreateNewConsole = 0x10;
    const uint CreateUnicodeEnvironment = 0x400;
    const int UoiName = 2;
    const uint ProcessQueryLimitedInformation = 0x1000;
    const uint StillActive = 259;
    const uint Th32csSnapProcess = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public int Size;
        public uint Usage, ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved2Ptr, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string? application, StringBuilder commandLine, nint processAttributes, nint threadAttributes, bool inheritHandles,
        uint flags, nint environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern nint GetProcessWindowStation();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetUserObjectInformationW(nint handle, int index, [Out] char[] info, int length, out int needed);
}

// macOS and Linux, until sessions can be started there (tmux, as agent-ha-bridge does).
public sealed class UnsupportedPlatformStarter : IAgentStarter
{
    public string? Unavailable() => "launching sessions is not supported on this platform yet";

    public string? CommandLine(StartSpec spec, out string? problem)
    {
        problem = Unavailable();
        return null;
    }

    public Started Start(StartSpec spec) => throw new PlatformNotSupportedException(Unavailable());

    public bool Running(Started started) => false;
}
