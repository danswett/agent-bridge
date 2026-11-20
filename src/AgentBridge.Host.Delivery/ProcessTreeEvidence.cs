namespace AgentBridge.Host.Delivery;

internal delegate int ProcessTreeScan(Func<int, int, bool> add);

internal sealed class ProcessTreeSnapshot
{
    readonly Dictionary<int, int> parents;
    readonly ILookup<int, int> children;
    ProcessTreeSnapshot(Dictionary<int, int> map) { parents = map; children = map.ToLookup(p => p.Value, p => p.Key); }
    public IEnumerable<int> Children(int parent) => children[parent];
    public bool HasParent(int child, int parent) => parents.TryGetValue(child, out var actual) && actual == parent;

    public static ProcessTreeSnapshot? Read(ProcessTreeScan scan, Func<bool>? allowed = null)
    {
        var map = new Dictionary<int, int>();
        var valid = true;
        var error = scan((id, parent) =>
        {
            valid &= allowed?.Invoke() != false && id >= 0 && parent >= 0 && map.TryAdd(id, parent);
            return valid;
        });
        // Only ERROR_NO_MORE_FILES proves that enumeration ended rather than failed.
        if (error != 18 || !valid || allowed?.Invoke() == false) { return null; }
        var result = new ProcessTreeSnapshot(map);
        return allowed?.Invoke() == false ? null : result;
    }
}

internal sealed record ChildIdentity(IHeldProcess? Process, DateTimeOffset Started, string? Problem, bool Unrelated = false);

internal sealed class ProcessTreeEvidence(
    Func<int, DateTimeOffset, HeldOpen> open, ProcessTreeScan scan, Func<int, DateTimeOffset?> created)
{
    public ProcessTreeSnapshot? Capture(Func<bool>? allowed = null) => ProcessTreeSnapshot.Read(scan, allowed);
    static ChildIdentity Unknown(string why) => new(null, default, why);

    public ChildIdentity Child(IHeldProcess parent, int id,
        ProcessTreeSnapshot discovery, Func<bool>? allowed = null)
    {
        if (parent.StartedAt is not { } parentStarted) { return Unknown("the held parent's creation time is not known"); }
        if (allowed?.Invoke() == false) { return Unknown("the process-tree evidence budget was exhausted"); }
        if (!discovery.HasParent(id, parent.Id)) { return Unknown("the descendant's parent edge is not known"); }
        if (created(id) is not { } at) { return Unknown("the descendant's creation time is not known"); }
        if (at < parentStarted) { return new(null, at, null, Unrelated: true); }
        var opened = open(id, at);
        if (opened.Process is not { } held)
        {
            return Unknown(opened.Refusal ?? "a descendant changed while its identity was verified");
        }
        var keep = false;
        try
        {
            if (held.Id != id || held.StartedAt != at) { return Unknown("the held descendant's creation time changed"); }
            // The handle pins the child identity before fresh ancestry is accepted.
            if (Capture(allowed) is not { } current || !current.HasParent(id, parent.Id))
            { return Unknown("the held descendant's parent edge could not be verified"); }
            keep = true;
            return new(held, at, null);
        }
        finally { if (!keep) { held.Dispose(); } }
    }
}
