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
        scan((id, parent) =>
        {
            if (allowed?.Invoke() == false) { return false; }
            map[id] = parent;
            return true;
        });
        return new(map);
    }
}

internal sealed record ChildIdentity(IHeldProcess? Process, DateTimeOffset Started, string? Problem, bool Unrelated = false);

internal sealed class ProcessTreeEvidence(
    Func<int, DateTimeOffset, HeldOpen> open, ProcessTreeScan scan, Func<int, DateTimeOffset?> created)
{
    public ProcessTreeSnapshot? Capture(Func<bool>? allowed = null) => ProcessTreeSnapshot.Read(scan, allowed);
    static ChildIdentity Unknown(string why) => new(null, default, why);

    public ChildIdentity Child(IHeldProcess parent, DateTimeOffset parentStarted, int id,
        ProcessTreeSnapshot discovery, Func<bool>? allowed = null)
    {
        if (!discovery.HasParent(id, parent.Id)) { return Unknown("the descendant's parent edge is not known"); }
        if (created(id) is not { } at) { return Unknown("the descendant's creation time is not known"); }
        if (at < parentStarted) { return new(null, at, null, Unrelated: true); }
        var opened = open(id, at);
        if (opened.Process is not { } held)
        {
            if (opened.Gone && !discovery.Children(id).Any()) { return new(null, at, null, Unrelated: true); }
            return Unknown(opened.Refusal ?? "a descendant changed while its identity was verified");
        }
        return new(held, at, null);
    }
}
