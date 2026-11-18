using AgentBridge.Host.Delivery;

internal sealed class TreeNode(int parent, DateTimeOffset started)
{
    public int Parent = parent;
    public DateTimeOffset Started = started;
    public bool Running = true, UnknownStart = false, Survive = false;
    public string? Refusal;
    public Action? OnTerminate = null;
}

internal sealed class TreeRig
{
    public static readonly DateTimeOffset T0 = Fixture.T0;
    public Dictionary<int, TreeNode> Nodes { get; } = new()
    {
        [101] = new(0, T0),
        [102] = new(101, T0.AddSeconds(1)),
        [103] = new(102, T0.AddSeconds(2)),
        [104] = new(900, T0),
    };
    public List<int> Terminated { get; } = [];
    public int Handles, MaxHandles, SnapshotError = 18, SnapshotFailures = 0;
    public Action<int>? BeforeOpen, AfterOpen, BeforeCreated, AfterCreated;
    public Action? AfterScan = null;
    public ProcessTreeEvidence Evidence() => new(Open, Scan, Created);
    public IHeldProcess OpenRoot() => Open(101, T0).Process!;

    public int Scan(Func<int, int, bool> add)
    {
        var error = SnapshotError;
        if (SnapshotFailures > 0) { SnapshotFailures--; error = 5; }
        var rows = Nodes.Where(p => p.Value.Running).Select(p => (p.Key, p.Value.Parent)).ToArray();
        foreach (var (id, parent) in rows) { if (!add(id, parent)) { return 0; } }
        AfterScan?.Invoke();
        return error;
    }

    DateTimeOffset? Created(int id)
    {
        BeforeCreated?.Invoke(id);
        var at = Nodes.TryGetValue(id, out var n) && n.Running && !n.UnknownStart ? n.Started : (DateTimeOffset?)null;
        AfterCreated?.Invoke(id);
        return at;
    }

    public HeldOpen Open(int id, DateTimeOffset started)
    {
        BeforeOpen?.Invoke(id);
        if (!Nodes.TryGetValue(id, out var node) || !node.Running
            || Math.Abs((node.Started - started).Ticks) > TimeSpan.TicksPerMillisecond) { return HeldOpen.Exited(); }
        if (node.Refusal is { } why) { return HeldOpen.Refused(why); }
        Handles++;
        MaxHandles = Math.Max(MaxHandles, Handles);
        var held = new Held(this, id, node, node.Started);
        AfterOpen?.Invoke(id);
        return HeldOpen.Of(held);
    }

    sealed class Held(TreeRig rig, int id, TreeNode node, DateTimeOffset started) : IHeldProcess
    {
        public int Id => id;
        public DateTimeOffset? StartedAt => started;
        public Task<bool> WaitForExitAsync(TimeSpan timeout) => Task.FromResult(!node.Running);
        public string? Terminate()
        {
            rig.Terminated.Add(id);
            node.Running = node.Survive;
            node.OnTerminate?.Invoke();
            return null;
        }
        public void Dispose() => rig.Handles--;
    }
}
