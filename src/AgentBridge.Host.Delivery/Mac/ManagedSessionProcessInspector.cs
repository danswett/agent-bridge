using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Mac;

// Existing external sessions stay visible; only a live, registered managed pane may
// acquire a new Codex thread-lock binding. Delivery still checks backend ownership.
public sealed class ManagedSessionProcessInspector : IProcessInspector
{
    readonly IProcessInspector processes;
    readonly Func<IReadOnlyList<Started>> roots;
    readonly Func<Started, int, bool?> owns;
    readonly Func<int, MacProcessIdentity?> capture;
    readonly Func<string, IReadOnlyList<int>, IReadOnlyList<int>> holders;
    readonly Func<IReadOnlyList<int>?>? nativeCandidates;

    public ManagedSessionProcessInspector(ManagedTmuxBackend backend)
        : this(new SystemProcessInspector(), () => backend.Snapshot().Select(s => s.Started).ToArray(),
            backend.Owns, MacProcessIdentityReader.Capture, MacFileHolders.Find, NativeCandidates) { }

    internal ManagedSessionProcessInspector(IProcessInspector processes, Func<IReadOnlyList<Started>> roots,
        Func<Started, int, bool?> owns, Func<int, MacProcessIdentity?> capture,
        Func<string, IReadOnlyList<int>, IReadOnlyList<int>> holders, Func<IReadOnlyList<int>?>? nativeCandidates = null)
    {
        this.processes = processes; this.roots = roots; this.owns = owns;
        this.capture = capture; this.holders = holders;
        this.nativeCandidates = nativeCandidates;
    }

    public IReadOnlyList<AgentProcess> GetAgentProcesses(AgentKind agent) => processes.GetAgentProcesses(agent);
    public AgentProcessObservation Observe(AgentKind agent) => processes.Observe(agent);

    public IReadOnlyList<int> FileHolders(string path)
    {
        var registered = roots();
        if (registered.Count == 0) { return []; }
        var observed = processes.Observe(AgentKind.Codex);
        if (!observed.Listed) { return []; }
        // .NET can silently omit a process with an unreadable name on macOS. The
        // raw scan checks every same-user identity/path before filtering candidates,
        // and independently excludes processes proven to belong to another user.
        var listed = nativeCandidates is null
            ? observed.Processes.Concat(observed.Unidentified).Select(p => p.Id).Distinct().ToArray()
            : nativeCandidates()?.ToArray();
        if (listed is null) { return []; }
        if (listed.Length is 0 or > MacFileHolders.MaxProcesses || listed.Any(pid => pid <= 0)) { return []; }
        var candidates = new Dictionary<int, (Started Root, MacProcessIdentity Identity)>();
        foreach (var process in observed.Processes)
        {
            // The classification must refer to this generation of the PID. Never turn
            // an unreadable start time or a changed executable into an ownership guess.
            if (process.StartedAt is not { } start || capture(process.Id) is not { } identity
                || identity.StartedAt != start) { continue; }
            foreach (var root in registered)
            {
                if (owns(root, process.Id) != true || capture(process.Id) != identity) { continue; }
                candidates.TryAdd(process.Id, (root, identity));
                break;
            }
        }
        if (candidates.Count == 0 || candidates.Count > MacFileHolders.MaxProcesses) { return []; }
        var found = holders(path, listed).Distinct().ToArray();
        // Do not hide a daemon or an external terminal sharing this lock: the normal
        // discoverer deliberately rejects ambiguous holders rather than picking one.
        if (found.Any(pid => !candidates.ContainsKey(pid))) { return []; }
        // A file-holder query is an observation, never authority. The pane and the exact process
        // generation must still be owned after the descriptor scan finishes.
        if (found.Any(pid => !candidates.TryGetValue(pid, out var candidate)
            || capture(pid) != candidate.Identity || owns(candidate.Root, pid) != true
            || capture(pid) != candidate.Identity)) { return []; }
        return found;
    }

    static IReadOnlyList<int>? NativeCandidates() => OperatingSystem.IsMacOS() ? NativeCandidates(new MacDaemonNative()) : null;

    internal static IReadOnlyList<int>? NativeCandidates(IMacDaemonNative native)
    {
        var scan = MacAgentHaBridgeProbe.Observe(native, name => name.Equals("codex", StringComparison.OrdinalIgnoreCase)
            || name.Equals("codex.exe", StringComparison.OrdinalIgnoreCase) || name.StartsWith("codex-", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node", StringComparison.OrdinalIgnoreCase) || name.Equals("bun", StringComparison.OrdinalIgnoreCase));
        return scan.Complete ? scan.Processes.Select(p => p.ProcessId).ToArray() : null;
    }
}
