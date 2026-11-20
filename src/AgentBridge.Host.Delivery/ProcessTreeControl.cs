namespace AgentBridge.Host.Delivery;

internal sealed class ProcessTreeControl(
    Func<int, DateTimeOffset, HeldOpen> open,
    Func<IReadOnlyDictionary<int, int>?> parents,
    Func<int, DateTimeOffset?> created) : IProcessControl
{
    readonly Func<int, DateTimeOffset, HeldOpen> openProcess = open;
    readonly Func<IReadOnlyDictionary<int, int>?> snapshot = parents;
    readonly Func<int, DateTimeOffset?> startOf = created;

    public HeldOpen Open(int processId, DateTimeOffset startedAt)
    {
        var result = openProcess(processId, startedAt);
        if (result.Process is not { } held) { return result; }
        var tree = new Tree(this, held, startedAt);
        try
        {
            tree.Collect();
            return HeldOpen.Of(tree);
        }
        catch { tree.Dispose(); throw; }
    }

    sealed class Tree(ProcessTreeControl owner, IHeldProcess root, DateTimeOffset started) : IHeldProcess
    {
        readonly List<(IHeldProcess Process, DateTimeOffset Started)> held = [(root, started)];
        readonly HashSet<int> seen = [root.Id];
        string? problem, terminationError, waitProblem;
        bool terminating;
        int dispatched;

        public int Id => root.Id;
        public string? ExitProblem => problem ?? waitProblem ?? terminationError;

        public bool Collect()
        {
            if (problem is not null) { return false; }
            if (owner.snapshot() is not { } map)
            {
                problem = "the host could not read a complete process tree";
                return false;
            }
            var children = map.ToLookup(p => p.Value, p => p.Key);
            for (var i = 0; i < held.Count; i++)
            {
                var parent = held[i];
                foreach (var id in children[parent.Process.Id])
                {
                    if (seen.Contains(id)) { continue; }
                    if (owner.startOf(id) is not { } at)
                    {
                        problem = "the host could not identify a descendant's creation time";
                        return false;
                    }
                    // A reused parent PID cannot own a process older than itself.
                    if (at < parent.Started) { continue; }
                    var child = owner.openProcess(id, at);
                    if (child.Process is not { } process)
                    {
                        if (child.Gone && !children[id].Any()) { continue; }
                        problem = child.Refusal ?? "a descendant changed while its tree was being verified";
                        return false;
                    }
                    seen.Add(id);
                    held.Add((process, at));
                }
            }
            return true;
        }

        void Dispatch()
        {
            // Parents first, so they cannot keep creating children while we end the tree.
            for (; dispatched < held.Count; dispatched++)
            {
                var error = held[dispatched].Process.Terminate();
                terminationError ??= error;
            }
        }

        public string? Terminate()
        {
            if (terminating) { return terminationError; }
            if (!Collect()) { return problem; }
            terminating = true;
            Dispatch();
            return terminationError;
        }

        public async Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            waitProblem = null;
            var began = Environment.TickCount64;
            for (var pass = 0; pass < 3; pass++)
            {
                if (!Collect()) { return false; }
                if (terminating) { Dispatch(); }
                foreach (var p in held)
                {
                    var remaining = Math.Max(0, timeout.TotalMilliseconds - (Environment.TickCount64 - began));
                    if (!await p.Process.WaitForExitAsync(TimeSpan.FromMilliseconds(remaining))) { waitProblem = "a verified process is still running"; return false; }
                }
                var count = held.Count;
                if (!Collect()) { return false; }
                if (held.Count == count) { return true; }
            }
            waitProblem = "the process tree kept changing while its exit was being verified";
            return false;
        }

        public void Dispose()
        {
            foreach (var p in held) { p.Process.Dispose(); }
        }
    }
}
