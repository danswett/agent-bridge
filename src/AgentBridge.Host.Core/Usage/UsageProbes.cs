using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Usage;

// What the host needs to have an agent read its own allowance (#200): Claude Code's /usage in
// print mode, and Codex's app-server. The agent uses its own credentials, refresh and locking;
// the host only starts it, never holds or refreshes a token, and never ends one of these processes
// while it might be refreshing (spike #197, E4 and E13).
//
// Only `serve` has these: a one-shot command (`usage`, `ha shadow`) reads what the agents last
// saved and starts nothing it would then leave behind.
public sealed class UsageProbes
{
    public required IProbeLauncher Launcher { get; init; }

    // The machine's agent processes: a Claude probe already running (another host's, or one this
    // host left after a restart) is never joined by a second, and Codex's route depends on what
    // Codex runs (spike X-3).
    public required IProcessInspector Processes { get; init; }

    // The agent's executable as a launch finds it (AgentLocator), or null when it is not installed.
    public required Func<AgentKind, string?> Find { get; init; }

    // A folder the host owns and keeps empty, which the probes run in, so neither agent reads a
    // project's settings or instructions.
    public required string WorkingDirectory { get; init; }

    // Codex's managed daemon, over its control socket.
    public ICodexDaemon Daemon { get; init; } = new CodexDaemonSocket();

    // A process's parent, so the Codex probe's own process tree (codex.cmd, node, codex.exe) is not
    // taken for another Codex process.
    public Func<int, int?> ParentOf { get; init; } = AgentBridge.Hooks.Ancestry.ParentOf;

    // What the Codex probe tells the app-server it is.
    public string ClientVersion { get; init; } = "0";

    // A machine-wide lease on asking one agent profile, by key, or null while another host holds it.
    // Two hosts serving different bridge profiles do not share HostLock, so both could see no probe
    // running and both start one: two readers and writers of the same sign-in. A probe's lease is
    // kept until its process ends, not only until the host stops waiting for it.
    public Func<string, IDisposable?> Lease { get; init; } = _ => new NoLease();

    internal IDisposable? TryLease(AgentKind agent, string home)
    {
        try { return Lease(LeaseKey(agent, home)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // One per agent home, as the agent names its own credentials by it: the folder itself, so a
    // home reached through a link and one through its target, which share one auth.json, share
    // one lease. Only the key: the agent is still given the home as configured.
    internal static string LeaseKey(AgentKind agent, string home)
    {
        var full = Physical(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) { full = full.ToLowerInvariant(); }
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full)))[..16];
        return $"{agent.ToString().ToLowerInvariant()}-{hash}";
    }

    // The path with every link along it followed (symbolic links, and junctions on Windows), as
    // the system resolves it: on Windows `..` is taken from the text before anything is looked at,
    // elsewhere from the folder a link led to (`/r/link/../home`, with link at /r/real/nested, is
    // /r/real/home). A part that cannot be looked at is taken as it is.
    internal static string Physical(string path)
    {
        var start = Absolute(path);
        var root = Path.GetPathRoot(start) ?? "";
        var pending = new Stack<string>(start[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Reverse());
        var current = root;
        var links = 0;
        while (pending.TryPop(out var part))
        {
            if (part == ".") { continue; }
            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }
            var next = Path.Combine(current, part);
            string? target = null;
            if (links < 32)
            {
                try
                {
                    var info = new DirectoryInfo(next);
                    if (info.LinkTarget is { } link) { target = Path.IsPathRooted(link) ? link : Path.Combine(current, link); }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
            if (target is null)
            {
                current = next;
                continue;
            }
            // The link's target, itself walked part by part: it may pass through links, or `..`.
            links++;
            var targetRoot = Path.GetPathRoot(target) ?? "";
            foreach (var p in target[targetRoot.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Reverse()) { pending.Push(p); }
            current = targetRoot;
        }
        return current;
    }

    // Rooted at the current folder, as the system would take it: on Windows `..` and `.` are
    // resolved from the text (GetFullPath), elsewhere left for the walk past any link before them.
    internal static string Absolute(string path) => OperatingSystem.IsWindows() ? Path.GetFullPath(path)
        : Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), path);

    sealed class NoLease : IDisposable
    {
        public void Dispose() { }
    }
}

// How to start a probe. Environment: a null value removes the variable from what the child
// inherits. Stdio keeps standard input and output open for the caller (Codex's JSON lines);
// otherwise standard input is closed at once and output is collected, at most a few KiB.
public sealed record ProbeCommand(string Program, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment, bool Stdio = false);

// A started probe. There is deliberately no way to kill one here: Claude Code ends a refresh with
// a compare-and-swap write, and a probe ended between the token response and that write loses the
// rotated token, which signs the machine out at the next refresh (spike E4).
public interface IProbeProcess : IDisposable
{
    int Id { get; }

    // Completes with the exit code once the process has ended.
    Task<int> Exited { get; }

    // Standard output as text once it has closed (not with Stdio), cut at a few KiB.
    Task<string> Output { get; }

    // With Stdio: standard input and output.
    Stream? Input { get; }
    Stream? Reader { get; }

    // End of input. Codex's app-server exits by itself once it reads it (spike X-4).
    void CloseInput();
}

public interface IProbeLauncher
{
    // Null when the program could not be started.
    IProbeProcess? Start(ProbeCommand command);
}

// When an agent may be asked to read its allowance, shared by both probes (spike #197, "a safe
// pace"):
//   * only when the newest reading is older than Stale (the idle interval, 15 minutes);
//   * at most every IdleGap (15 minutes) with no session of the agent open here, and every
//     BusyGap (5 minutes) with one; while agent-ha-bridge runs for this user the monitor says
//     idle whatever is open;
//   * one at a time;
//   * after a failure or a rate-limited answer, not again for 15 minutes, then 30, 60, and at
//     most 2 hours, until one works.
internal sealed class ProbePace(UsageOptions options)
{
    public DateTimeOffset LastStarted = DateTimeOffset.MinValue;
    public int Failures;
    public DateTimeOffset NotBefore = DateTimeOffset.MinValue;

    public TimeSpan Gap(bool busy) => busy ? options.ProbeBusyInterval : options.ProbeIdle;

    // The earliest a probe may start, given the reading it would refresh.
    public DateTimeOffset DueAt(DateTimeOffset? readingAt, bool busy)
    {
        var stale = readingAt is { } at ? at + options.ProbeIdle : DateTimeOffset.MinValue;
        var gap = LastStarted == DateTimeOffset.MinValue ? DateTimeOffset.MinValue : LastStarted + Gap(busy);
        return Max(Max(stale, gap), NotBefore);
    }

    // A clock that went backwards makes a probe due rather than holding every one off.
    public bool Due(DateTimeOffset? readingAt, bool busy, DateTimeOffset now) =>
        now < LastStarted || now >= DueAt(readingAt, busy);

    public void Started(DateTimeOffset now) => LastStarted = now;

    public void Succeeded()
    {
        Failures = 0;
        NotBefore = DateTimeOffset.MinValue;
    }

    // Returns the wait it set.
    public TimeSpan Failed(DateTimeOffset now)
    {
        Failures++;
        var wait = Backoff(Failures, options);
        NotBefore = now + wait;
        return wait;
    }

    // Held for the longest back-off: a sign-out, or an account no probe can read.
    public void Hold(DateTimeOffset now)
    {
        Failures = Math.Max(Failures, 1);
        NotBefore = now + options.ProbeBackoffMax;
    }

    public void Release() => NotBefore = DateTimeOffset.MinValue;

    // 15 minutes, doubling, at most ProbeBackoffMax.
    public static TimeSpan Backoff(int failures, UsageOptions options)
    {
        var wait = options.ProbeBackoff;
        for (var i = 1; i < failures && wait < options.ProbeBackoffMax; i++) { wait += wait; }
        return wait < options.ProbeBackoffMax ? wait : options.ProbeBackoffMax;
    }

    static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}

internal static class ProbeEnvironment
{
    // The agent's own home, as a session of that profile would have it: inherited when the host
    // already runs with it, left out for the default home (Claude Code names its Windows credential
    // with a hash of CLAUDE_CONFIG_DIR when that is set, so setting it to the default would read
    // another credential), and set otherwise.
    // A relative home would be resolved by the probe from its own working directory (the host's
    // empty folder), naming another profile: it is made absolute here, or, where the agent names
    // its credential by the variable's very text (namespaced: Claude), null, and no probe runs.
    public static Dictionary<string, string?>? Home(string variable, string home, string folder, bool namespaced = false)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var inherited = Environment.GetEnvironmentVariable(variable);
        if (inherited is { Length: > 0 } && Same(inherited, home))
        {
            if (Path.IsPathFullyQualified(inherited)) { return environment; }
            if (namespaced) { return null; }
            environment[variable] = UsageProbes.Absolute(inherited);
            return environment;
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Same(home, Path.Combine(profile, folder)))
        {
            environment[variable] = null;
            return environment;
        }
        if (!Path.IsPathFullyQualified(home))
        {
            if (namespaced) { return null; }
            home = UsageProbes.Absolute(home);
        }
        environment[variable] = home;
        return environment;
    }

    public static bool Same(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(UsageProbes.Physical(a)), Path.TrimEndingDirectorySeparator(UsageProbes.Physical(b)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
