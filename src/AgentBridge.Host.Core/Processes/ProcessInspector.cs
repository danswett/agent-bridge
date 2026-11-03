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
// processes could not be listed at all: the reclaimer's test, which keeps every worktree on any
// doubt. Discovery asks two narrower things. Listed is false only when the processes could not
// be listed at all. Unidentified are the runtimes (node, bun) whose command line could not be
// read this time, so may or may not be this agent: discovery holds for one only when it was a
// session's own process, since every other user's node is unreadable too, and holding for
// those would hold every agent for as long as any of them ran.
public sealed record AgentProcessObservation(IReadOnlyList<AgentProcess> Processes, bool Known)
{
    public bool Listed { get; init; } = true;
    public IReadOnlyList<AgentProcess> Unidentified { get; init; } = [];
    // Of Codex's Processes, those that started (through any processes between) an interactive
    // Codex executable that is listed too: a launcher (npx, bunx, pnpm dlx, a version manager's
    // shim), whose session, if any, is that executable. Empty where ancestry cannot be read.
    public IReadOnlySet<int> Launchers { get; init; } = new HashSet<int>();
}

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

        var listed = true;
        var listing = new List<ListedProcess>();
        var seen = new HashSet<int>();
        foreach (var candidate in CandidateNames(name))
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(candidate); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or PlatformNotSupportedException) { listed = false; continue; }
            foreach (var process in processes)
            {
                using (process)
                {
                    var id = process.Id;
                    if (!seen.Add(id)) { continue; }
                    DateTimeOffset? started = null;
                    try { started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero); }
                    catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
                    // Only the agent's own executable is asked for its parent: that is the
                    // loader or launcher to recognise in Classify. Codex's is asked for those above it too,
                    // for the launchers its pid-less registrations are told apart from (#437).
                    var parent = candidate is "node" or "bun" ? null : AgentBridge.Hooks.Ancestry.ParentOf(id);
                    var ancestors = agent == AgentKind.Codex && parent is { } p
                        ? Ancestors(p, started, AgentBridge.Hooks.Ancestry.ParentOf, StartOf)
                        : null;
                    listing.Add(new(candidate, id, started, CommandLineOf((id, started?.UtcTicks ?? 0)), parent, ancestors));
                }
            }
        }

        var (observation, kept) = Classify(agent, name, listing, listed, OperatingSystem.IsWindows(),
            id => OperatingSystem.IsWindows() && ProcessOwnership.AnotherUsers(id));
        var live = kept.Select(p => (p.Id, p.StartedAt?.UtcTicks ?? 0)).ToHashSet();
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
        return observation;
    }

    // How far up an executable's ancestry a launcher is looked for: npx, a shell and the loader
    // between it and the executable are three; the hooks read as far.
    const int MaxAncestors = 12;

    // An executable's ancestry from its parent up, nearest first, as far as each process started
    // no later than the one below it. A parent's pid is only a number: on Windows it outlives the
    // parent, and the process another takes it for, started later, is no ancestor, nor is what is
    // above it. Followed past one, a waiting Codex above another's reused parent pid was taken for
    // the launcher of an executable it never started, and its hold dropped (#437). A start that
    // cannot be read ends the walk there too: not known to be an ancestor, it launched nothing.
    internal static int[] Ancestors(int parent, DateTimeOffset? started, Func<int, int?> parentOf, Func<int, DateTimeOffset?> startOf)
    {
        var chain = new List<int>();
        var below = started;
        int? current = parent;
        while (chain.Count < MaxAncestors && current is { } pid && pid > 0 && !chain.Contains(pid))
        {
            if (below is not { } b || startOf(pid) is not { } at || at > b) { break; }
            chain.Add(pid);
            below = at;
            current = parentOf(pid);
        }
        return [.. chain];
    }

    static DateTimeOffset? StartOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
    }

    // Ancestors: the executable's parent and those above it, nearest first, as Ancestors reads them.
    internal sealed record ListedProcess(string Candidate, int Id, DateTimeOffset? StartedAt, string? CommandLine, int? Parent = null,
        IReadOnlyList<int>? Ancestors = null);

    // What a listing of candidate processes says about this agent, apart from reading the
    // machine, so the filtering is testable. Kept are the processes whose cached command
    // lines stay remembered.
    internal static (AgentProcessObservation Observation, List<ListedProcess> Kept) Classify(
        AgentKind agent, string name, IEnumerable<ListedProcess> listing, bool listed, bool windows, Func<int, bool> anotherUsers)
    {
        var found = new List<AgentProcess>();
        var unidentified = new List<AgentProcess>();
        var kept = new List<ListedProcess>();
        var known = listed;
        var all = listing.ToList();
        // An npm install runs the agent as a node loader (npm-loader.js, codex.js) that starts
        // the agent's own executable and waits on it. The executable is the session's process:
        // it holds Copilot's lock and is what the hooks record. Counted as well, the loader was a
        // process with no session: discovery held for two minutes after every launch and every
        // restart, and worktree reclaim never ran while a session started by hand was open (#153).
        var loaders = all.Where(p => p.Candidate is not ("node" or "bun") && p.Parent is > 0)
            .Select(p => (Id: p.Parent!.Value, Child: p.StartedAt)).ToList();
        // VS Code's copilot shim (bin\copilot-shim\copilot.exe), which Agency runs Copilot
        // through, is a copilot.exe of its own that starts the real CLI with the same arguments
        // and waits on it. The real CLI holds the session's lock; counted as well, the shim was a
        // process no session accounted for, and held discovery for two minutes after every
        // launch and every restart (#615). Only the shim itself, by the folder its program is in,
        // and only a child given the very same arguments: a real CLI that starts another, even
        // with the same arguments, may hold a session of its own, and hiding a live session is
        // what deletes its worktree.
        var launched = agent == AgentKind.Copilot
            ? all.Where(p => p.Candidate is not ("node" or "bun") && p.Parent is > 0 && !string.IsNullOrWhiteSpace(p.CommandLine))
                .Select(p => (Id: p.Parent!.Value, Child: p.StartedAt, Arguments: Arguments(p.CommandLine)))
                .ToList()
            : [];
        foreach (var process in all)
        {
            var (candidate, id, started, commandLine, _, _) = process;
            // Another user's agent is none of this user's sessions, and nothing of it can
            // be read: taken for one, it held discovery for as long as it ran (#143).
            if (commandLine is null && windows && anotherUsers(id)) { continue; }
            kept.Add(process);
            // agent-ha-bridge's usage snapshot (Get-BridgeWorktreeUsage) gives up on a
            // runtime it cannot identify outside Windows. On Windows it never looks at
            // node at all; every other user's and service's node is listed there too,
            // and an unreadable one would keep every worktree for ever.
            if (candidate is "node" or "bun" && string.IsNullOrWhiteSpace(commandLine))
            {
                if (!windows) { known = false; }
                unidentified.Add(new AgentProcess(id, started, null));
                continue;
            }
            if (candidate is "node" or "bun" && !IsAgentScript(agent, name, commandLine)) { continue; }
            // A loader whose own agent executable is listed: that executable stands for it. Both
            // start times must be known, and the loader's no later than its child's, with no
            // slack: a pid the loader left behind and another agent's runtime took even a moment
            // after the child started is that agent's session, and must stay counted.
            if (candidate is "node" or "bun" && started is { } loaderStarted
                && loaders.Any(l => l.Id == id && l.Child is { } child && loaderStarted <= child)
                && IsLoaderScript(agent, commandLine))
            {
                continue;
            }
            // A launcher whose real CLI is listed: the CLI stands for it, under the same rule
            // as a loader, both start times known and the launcher's no later than its child's.
            if (candidate is not ("node" or "bun") && started is { } launcherStarted && IsCopilotShim(commandLine)
                && launched.Any(l => l.Id == id && l.Child is { } child && launcherStarted <= child && l.Arguments.SequenceEqual(Arguments(commandLine), StringComparer.Ordinal)))
            {
                continue;
            }
            found.Add(new AgentProcess(id, started, commandLine));
        }
        // A listed wrapper above an interactive Codex executable, and started no later, launched it
    // (npx, bunx, pnpm dlx under node or bun, or a version manager's shim): its session, if any,
    // is that executable. Only a wrapper: an interactive Codex can start another through its
    // shell tool, and the first is still a session, perhaps one whose registration names no
    // process. A pid above it that started later is another process the pid went to.
        var foundIds = found.Select(p => p.Id).ToHashSet();
        var launchers = new HashSet<int>();
        foreach (var child in kept.Where(p => agent == AgentKind.Codex && p.Candidate is not ("node" or "bun") && foundIds.Contains(p.Id)
            && p.Ancestors is { Count: > 0 } && (string.IsNullOrEmpty(p.CommandLine) || Discovery.CodexLockDiscovery.IsInteractive(p.CommandLine))))
        {
            foreach (var above in child.Ancestors!)
            {
                if (above == child.Id || !foundIds.Contains(above)) { continue; }
                var launcher = kept.First(p => p.Id == above);
                if (!IsWrapper(launcher) || launcher.StartedAt is not { } a || child.StartedAt is not { } b || a > b) { continue; }
                launchers.Add(above);
            }
        }
        return (new(found, known) { Listed = listed, Unidentified = unidentified, Launchers = launchers }, kept);
    }

    // A command line's arguments without its program, which differs between a launcher and
    // the CLI it starts.
    static string[] Arguments(string? commandLine) => [.. CommandLine.Split(commandLine).Skip(1)];

    // VS Code's shim, by where its program is installed: bin\copilot-shim\copilot.exe, in each
    // VS Code build. A shim started by bare name cannot be told from a real CLI, so it stays
    // counted: a hold that clears after its grace, rather than a session hidden.
    internal static bool IsCopilotShim(string? commandLine)
    {
        var arguments = CommandLine.Split(commandLine);
        if (arguments.Count == 0) { return false; }
        var parts = arguments[0].Replace('\\', '/').Split('/');
        return parts.Length >= 2
            && (parts[^1].Equals("copilot.exe", StringComparison.OrdinalIgnoreCase) || parts[^1].Equals("copilot", StringComparison.OrdinalIgnoreCase))
            && parts[^2].Equals("copilot-shim", StringComparison.OrdinalIgnoreCase);
    }

    // A runtime (what runs npx, bunx or pnpm dlx; npm's own loader is left out above), or a
    // version manager's shim named codex (Volta's bin, Scoop's, asdf's or mise's shims). Not
    // one whose command line cannot be read: not known to be a wrapper, it may be the session.
    static bool IsWrapper(ListedProcess p)
    {
        if (p.Candidate is "node" or "bun") { return true; }
        if (string.IsNullOrWhiteSpace(p.CommandLine) || CommandLine.Split(p.CommandLine) is not [var program, ..]) { return false; }
        var path = program.Replace('\\', '/').ToLowerInvariant();
        return path.Contains("/shims/", StringComparison.Ordinal) || path.Contains("/volta/bin/", StringComparison.Ordinal)
            || path.Contains("/.volta/bin/", StringComparison.Ordinal);
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

    // The scripts npm installs that only start the agent's own executable and wait on it.
    // Anything else run in node may be a session of its own, even when it starts one more.
    // Outside Windows npm runs them through a link in its bin folder (`node /usr/local/bin/codex`),
    // so a script given by path is followed to what it links to; one that cannot be followed is
    // not taken for a loader, and stays counted.
    internal static bool IsLoaderScript(AgentKind agent, string? commandLine, Func<string, string?>? resolveLink = null)
    {
        var loader = agent switch
        {
            AgentKind.Copilot => "/@github/copilot/npm-loader.js",
            AgentKind.Codex => "/@openai/codex/bin/codex.js",
            _ => null,
        };
        if (loader is null || string.IsNullOrEmpty(commandLine)) { return false; }
        resolveLink ??= LinkTarget;
        // Only the script node runs: the first argument after the runtime that is not one of its
        // options. A loader's path anywhere later (in a prompt, say) is not what the process runs.
        // An option that takes a separate value makes that value look like the script, which then
        // matches nothing, so the process stays counted.
        var arguments = CommandLine.Split(commandLine);
        if (arguments.Skip(1).FirstOrDefault(a => !a.StartsWith('-')) is not { } script) { return false; }
        return IsLoader(script, loader) || (Path.IsPathRooted(script) && resolveLink(script) is { } target && IsLoader(target, loader));

        static bool IsLoader(string path, string loader)
        {
            var file = path.Replace('\\', '/');
            while (file.Contains("//", StringComparison.Ordinal)) { file = file.Replace("//", "/", StringComparison.Ordinal); }
            return file.EndsWith(loader, StringComparison.OrdinalIgnoreCase);
        }
    }

    static string? LinkTarget(string path)
    {
        try { return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

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
        using var ps = ProcessLaunch.Start(start);
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
public static partial class CommandLine
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

    // An agent CLI embedded in another program and driven over stdio rather than a session:
    // Microsoft Scout runs `copilot.exe --headless ... --stdio`, which never takes a session id
    // and never writes a lock, so no session can account for it. agent-ha-bridge's
    // Test-BridgeAgentEmbeddedProcess, by argument rather than by text: `--headless` counts only
    // as an argument of its own before any prompt (-p, -i, or after `--`), so a prompt that
    // mentions it is not taken for the option. That holds when the quotes are escaped inside the
    // prompt, and on macOS, whose ps does not quote, so a prompt arrives as separate words.
    // --session-id anywhere settles it as a session: wrongly so only costs a hold.
    public static bool IsEmbedded(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) { return false; }
        var arguments = Split(commandLine);
        if (arguments.Any(a => a == "--session-id" || a.StartsWith("--session-id=", StringComparison.Ordinal))) { return false; }
        foreach (var a in arguments.Skip(1))
        {
            if (a == "--" || CopilotPromptOptions.Any(o => a == o || a.StartsWith(o + "=", StringComparison.Ordinal))) { return false; }
            if (a == "--headless" || a.StartsWith("--headless=", StringComparison.Ordinal)) { return true; }
        }
        return false;
    }

    // Copilot's options whose value is a prompt, which may say anything.
    static readonly string[] CopilotPromptOptions = ["-p", "--prompt", "-i", "--interactive"];

    // Codex's shared app-server daemon, which every Codex client talks to and which is nobody's
    // session (agent-ha-bridge's Test-BridgeCodexSharedProcess). It is the subcommand, the first
    // argument after the codex program that is neither an option nor an option's value: matching
    // the word anywhere took `codex "Explain the app-server protocol"` for the daemon, so a
    // session starting up was accounted for before it registered. macOS's ps does not quote,
    // so a prompt's words can arrive as separate arguments; only the first can be the subcommand.
    // After `--` everything is the prompt, which is how Launchers.Arguments passes it. The
    // app-server-daemon install folder counts only as the program's own path, not a prompt's.
    public static bool IsCodexAppServer(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) { return false; }
        var arguments = Split(commandLine);
        if (arguments.Count == 0) { return false; }
        var program = CodexProgram(arguments);
        if (InDaemonFolder(arguments[0]) || InDaemonFolder(arguments[program])) { return true; }
        for (var i = program + 1; i < arguments.Count; i++)
        {
            var a = arguments[i];
            if (a == "--") { return false; }
            if (!a.StartsWith('-')) { return a == "app-server"; }
            if (!a.Contains('=') && CodexValueOptions.Contains(a)) { i++; }
        }
        return false;
    }

    static bool InDaemonFolder(string path) => path.Replace('\\', '/').Contains("/app-server-daemon/", StringComparison.OrdinalIgnoreCase);

    // Where Codex's own command line starts. The program itself when it is Codex's executable
    // (named codex, or a vendored codex-<target> binary). Under node or bun, the script the
    // runtime runs, past its own options and their values (`--require x`, `--require=x`), when
    // that is npm's loader (.../@openai/codex/bin/codex.js) or named codex: a preload named codex
    // (`node --require /work/codex.cjs .../codex.js exec`) is not Codex, and nor is a loader's
    // path in a prompt after it. Otherwise the first later argument named codex (npx's
    // @openai/codex), else the first whose name starts with codex, else the first; never an
    // option, whose value may name anything.
    internal static int CodexProgram(IReadOnlyList<string> arguments)
    {
        static string File(string path)
        {
            var name = path.Replace('\\', '/');
            return name[(name.LastIndexOf('/') + 1)..];
        }
        if (arguments.Count == 0 || File(arguments[0]).StartsWith("codex", StringComparison.OrdinalIgnoreCase)) { return 0; }
        var from = 1;
        if (Path.GetFileNameWithoutExtension(File(arguments[0])).ToLowerInvariant() is "node" or "bun")
        {
            while (from < arguments.Count && arguments[from].StartsWith('-'))
            {
                from += RuntimeValueOptions.Contains(arguments[from]) ? 2 : 1;
            }
            if (from >= arguments.Count) { return 0; }
            var script = arguments[from];
            if (script.Replace('\\', '/').EndsWith("/@openai/codex/bin/codex.js", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(File(script)), "codex", StringComparison.OrdinalIgnoreCase))
            {
                return from;
            }
        }
        for (var i = from; i < arguments.Count; i++)
        {
            if (!arguments[i].StartsWith('-') && string.Equals(Path.GetFileNameWithoutExtension(File(arguments[i])), "codex", StringComparison.OrdinalIgnoreCase)) { return i; }
        }
        for (var i = from; i < arguments.Count; i++)
        {
            if (!arguments[i].StartsWith('-') && File(arguments[i]).StartsWith("codex", StringComparison.OrdinalIgnoreCase)) { return i; }
        }
        return 0;
    }

    // node's and bun's options that take the next argument as their value: a module to load
    // before the script, whose name may be anything.
    static readonly HashSet<string> RuntimeValueOptions = new(StringComparer.Ordinal)
    {
        "-r", "--require", "--import", "--loader", "--experimental-loader", "--preload",
    };

    // Codex's global options that take the next argument as their value, before any subcommand
    // (codex-rs cli/src/main.rs and tui/src/cli.rs). One list for every reader: one missing from
    // a reader took the value for the subcommand there.
    internal static readonly HashSet<string> CodexValueOptions = new(StringComparer.Ordinal)
    {
        "-c", "--config", "-m", "--model", "-p", "--profile", "-s", "--sandbox", "-a", "--ask-for-approval",
        "-C", "--cd", "-i", "--image", "--add-dir", "--enable", "--disable", "--local-provider", "--remote", "--remote-auth-token-env",
    };

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
