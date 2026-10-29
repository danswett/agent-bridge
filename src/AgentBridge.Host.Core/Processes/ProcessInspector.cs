using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AgentBridge.Protocol;
using Microsoft.Win32.SafeHandles;

namespace AgentBridge.Host.Core.Processes;

// A running agent CLI. StartedAt tells one generation of a pid from the next; it is null
// when the OS would not say (a protected process).
public sealed record AgentProcess(int Id, DateTimeOffset? StartedAt, string? CommandLine);

// Which agent processes are running. Injected so tests never depend on real processes.
public interface IProcessInspector
{
    IReadOnlyList<AgentProcess> GetAgentProcesses(AgentKind agent);

    // The processes that have a file open, where the OS can say (Windows' Restart Manager);
    // empty elsewhere or when it cannot be read. Only ever asked, never acted on.
    IReadOnlyList<int> FileHolders(string path) => [];

    // The same, and whether the list is known to be complete. Discovery can live with a
    // process it could not identify; deleting a worktree cannot, so the reclaimer asks this.
    AgentProcessObservation Observe(AgentKind agent) => new(GetAgentProcesses(agent), true);
}

// Known is false when a process that might be this agent could not be identified, or the
// processes could not be listed at all.
public sealed record AgentProcessObservation(IReadOnlyList<AgentProcess> Processes, bool Known);

// The real machine, on Windows, macOS and Linux. Only reads: it opens a process for
// query-limited information and nothing more.
public sealed class SystemProcessInspector : IProcessInspector
{
    // Reading a command line costs a syscall on Windows and Linux, and a `ps` on macOS, so
    // each answer is kept against pid and start time: a pid alone is reused, and a
    // recycled one would otherwise keep answering with the dead process's session.
    readonly Dictionary<(int, long), string?> _commandLines = [];
    readonly Lock _gate = new();

    public IReadOnlyList<AgentProcess> GetAgentProcesses(AgentKind agent) => Scan(agent).Processes;

    public AgentProcessObservation Observe(AgentKind agent) => Scan(agent);

    AgentProcessObservation Scan(AgentKind agent)
    {
        var name = agent switch
        {
            AgentKind.Copilot => "copilot",
            AgentKind.Claude => "claude",
            AgentKind.Codex => "codex",
            _ => null,
        };
        if (name is null) { return new([], true); }

        var names = CandidateNames(name);
        var found = new List<AgentProcess>();
        var seen = new HashSet<int>();
        var live = new HashSet<(int, long)>();
        var known = true;
        foreach (var candidate in names)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(candidate); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or PlatformNotSupportedException) { known = false; continue; }
            foreach (var process in processes)
            {
                using (process)
                {
                    var id = process.Id;
                    if (!seen.Add(id)) { continue; }
                    DateTimeOffset? started = null;
                    try { started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero); }
                    catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
                    var key = (id, started?.UtcTicks ?? 0);
                    live.Add(key);
                    var commandLine = CommandLineOf(key);
                    // agent-ha-bridge's usage snapshot (Get-BridgeWorktreeUsage) gives up on a
                    // runtime it cannot identify outside Windows. On Windows it never looks at
                    // node at all; every other user's and service's node is listed there too,
                    // and an unreadable one would keep every worktree for ever.
                    if (candidate is "node" or "bun" && string.IsNullOrWhiteSpace(commandLine) && !OperatingSystem.IsWindows()) { known = false; }
                    if (candidate is "node" or "bun" && !IsAgentScript(agent, name, commandLine)) { continue; }
                    found.Add(new AgentProcess(id, started, commandLine));
                }
            }
        }

        lock (_gate)
        {
            // Forget processes that have gone, so a host up for days does not keep one
            // entry per session it ever saw. Another agent's entries are kept while their
            // process still runs.
            foreach (var key in _commandLines.Keys.Where(k => !live.Contains(k) && !IsRunning(k.Item1)).ToList())
            {
                _commandLines.Remove(key);
            }
        }
        return new(found, known);
    }

    static bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    // The agent's own executable, and the runtimes a package install runs it under. Not
    // only elsewhere: an npm install on Windows runs as node.exe too, and asking only for
    // the bare name read every one of its sessions as dead. Claude's Bun-compiled binary
    // calls itself claude.exe even on macOS.
    internal static string[] CandidateNames(string name) =>
        OperatingSystem.IsWindows() ? [name, "node", "bun"] : [name, name + ".exe", "node", "bun"];

    internal static bool IsAgentScript(AgentKind agent, string name, string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) { return false; }
        var package = agent switch
        {
            AgentKind.Copilot => "@github/copilot",
            AgentKind.Claude => "@anthropic-ai/claude-code",
            _ => "@openai/codex",
        };
        if (commandLine.Replace('\\', '/').Contains(package, StringComparison.Ordinal)) { return true; }
        foreach (var token in CommandLine.Split(commandLine))
        {
            var file = token.Replace('\\', '/');
            if (file.EndsWith("/" + name, StringComparison.Ordinal) || file.EndsWith("/" + name + ".js", StringComparison.Ordinal)) { return true; }
        }
        return false;
    }

    string? CommandLineOf((int Pid, long Ticks) key)
    {
        lock (_gate)
        {
            if (_commandLines.TryGetValue(key, out var known)) { return known; }
        }
        var read = ReadCommandLine(key.Pid);
        // A failed read is not cached: a denied or racing read would otherwise hide a
        // live session for as long as that process ran.
        if (read is not null) { lock (_gate) { _commandLines[key] = read; } }
        return read;
    }

    internal static string? ReadCommandLine(int pid)
    {
        try
        {
            if (OperatingSystem.IsWindows()) { return WindowsCommandLine(pid); }
            if (OperatingSystem.IsLinux())
            {
                var raw = File.ReadAllBytes($"/proc/{pid}/cmdline");
                var parts = Encoding.UTF8.GetString(raw).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                return CommandLine.Join(parts);
            }
            if (OperatingSystem.IsMacOS()) { return MacCommandLine(pid); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException) { }
        return null;
    }

    static string? MacCommandLine(int pid)
    {
        var start = new ProcessStartInfo("/bin/ps", ["-ww", "-o", "command=", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var ps = Process.Start(start);
        if (ps is null) { return null; }
        var text = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit(2000);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    // NtQueryInformationProcess(ProcessCommandLineInformation) with a query-limited
    // handle: no WMI (about 77 ms a process) and no access beyond reading.
    [SupportedOSPlatform("windows")]
    static string? WindowsCommandLine(int pid)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid) { return null; }
        NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
        if (needed <= 0 || needed > 1 << 20) { return null; }
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) != 0) { return null; }
            // A UNICODE_STRING: ushort Length (bytes), ushort MaximumLength, then the
            // pointer, aligned to pointer size, into this same buffer.
            var length = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    const int ProcessQueryLimitedInformation = 0x1000;
    const int ProcessCommandLineInformation = 60;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(int access, bool inherit, int pid);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, IntPtr information, int length, out int returned);

    public IReadOnlyList<int> FileHolders(string path) => OperatingSystem.IsWindows() ? RestartManagerHolders(path) : [];

    // The Restart Manager's list of processes using a file: a session of its own, the one
    // file registered, the list read. It only reports; nothing is shut down or restarted.
    [SupportedOSPlatform("windows")]
    static IReadOnlyList<int> RestartManagerHolders(string path)
    {
        if (RmStartSession(out var session, 0, Guid.NewGuid().ToString("N")) != 0) { return []; }
        try
        {
            if (RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, null) != 0) { return []; }
            uint count = 0, reasons = 0;
            var result = RmGetList(session, out var needed, ref count, null, ref reasons);
            if (result == 0) { return []; }
            if (result != ErrorMoreData || needed == 0 || needed > 64) { return []; }
            var info = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out _, ref count, info, ref reasons) != 0) { return []; }
            return [.. info.Take((int)count).Select(i => i.Process.ProcessId)];
        }
        finally { RmEndSession(session); }
    }

    const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint session, int flags, string key);

    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint session, uint files, string[] names, uint applications, IntPtr processes, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? info, ref uint reasons);
}

// Splitting and joining command lines the way the agents' own option parsers see them.
public static class CommandLine
{
    // Whitespace separates arguments; double quotes group, and a backslash escapes a
    // quote. Close enough to CommandLineToArgvW for reading options, which is all this
    // is for.
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(commandLine)) { return result; }
        var current = new StringBuilder();
        bool quoted = false, any = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"') { current.Append('"'); i++; any = true; continue; }
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (any) { result.Add(current.ToString()); current.Clear(); any = false; }
                continue;
            }
            current.Append(c);
            any = true;
        }
        if (any) { result.Add(current.ToString()); }
        return result;
    }

    public static string Join(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));

    // The session a Copilot CLI was started in, from `--session-id <id>` or
    // `--session-id=<id>`. Read from the options rather than searched for: a regex over
    // the whole line found one inside a prompt too and credited the process with a
    // session it only mentioned. Two different ids name neither.
    public static string? CopilotSessionId(string? commandLine)
    {
        var options = Split(commandLine);
        string? found = null;
        for (var i = 0; i < options.Count; i++)
        {
            string? value = null;
            if (options[i] == "--session-id" && i + 1 < options.Count) { value = options[++i]; }
            else if (options[i].StartsWith("--session-id=", StringComparison.Ordinal)) { value = options[i]["--session-id=".Length..]; }
            if (value is null || !Guid.TryParseExact(value, "D", out _)) { continue; }
            if (found is not null && !string.Equals(found, value, StringComparison.OrdinalIgnoreCase)) { return null; }
            found = value;
        }
        return found?.ToLowerInvariant();
    }
}
